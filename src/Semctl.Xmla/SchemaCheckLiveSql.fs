/// The real SQL connection for `schemacheck --source sql:<server>;<database>`: Microsoft.Data.SqlClient
/// to the server the user named, encrypted (Encrypt=Mandatory), describing each query with
/// `sp_describe_first_result_set` (nothing is executed and no rows are read). With a service
/// principal, MSAL also asks Microsoft's sign-in service for a SQL token. Nothing else is contacted.
/// The connection is opened on the first query, and only then.
module Semctl.Xmla.LiveSql

open System
open System.Data
open Microsoft.Data.SqlClient
open Microsoft.Identity.Client
open Semctl
open Semctl.SchemaCheck
open Semctl.SchemaCheckSql
open Semctl.Xmla.SqlCredentials

/// Where an Entra token for a host comes from, and for which resource.
let private cloudOf (server: string) =
    let h = host server

    if h.EndsWith ".chinacloudapi.cn" then
        "https://login.chinacloudapi.cn", "https://database.chinacloudapi.cn/.default"
    elif h.EndsWith ".usgovcloudapi.net" then
        "https://login.microsoftonline.us", "https://database.usgovcloudapi.net/.default"
    else
        "https://login.microsoftonline.com", "https://database.windows.net/.default"

let private servicePrincipalToken (server: string) (clientId: string) (tenantId: string) (secret: string) =
    let authority, scope = cloudOf server

    let app =
        ConfidentialClientApplicationBuilder
            .Create(clientId)
            .WithClientSecret(secret)
            .WithAuthority($"{authority}/{tenantId}")
            .Build()

    app.AcquireTokenForClient([| scope |]).ExecuteAsync().GetAwaiter().GetResult().AccessToken

/// The connection string (without secrets for token sign-in).
let private connectionString (address: SqlAddress) (credential: SqlCredential) (trust: bool) =
    let b =
        SqlConnectionStringBuilder(
            DataSource = address.Server,
            InitialCatalog = address.Database,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = trust,
            ApplicationName = "semctl schemacheck",
            ConnectTimeout = 30,
            Pooling = false
        )

    match credential with
    | SqlCredential.SqlLogin(user, password) ->
        b.UserID <- user
        b.Password <- password
    | SqlCredential.Integrated -> b.IntegratedSecurity <- true
    | SqlCredential.AccessToken _
    | SqlCredential.ServicePrincipal _ -> ()

    b.ConnectionString

type private Describer(address: SqlAddress, env: Credentials.Environment) =
    let mutable connection: SqlConnection option = None
    let mutable known: string list = []

    let scrub (text: string) = Secrets.scrub known text

    let fail (e: SemctlError) = raise (SchemaSourceFailed e)

    let openConnection () =
        // The managed network layer on every OS: the native one for Windows (SNI.dll) isn't shipped.
        AppContext.SetSwitch("Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows", true)

        let credential =
            match SqlCredentials.resolve env address with
            | Ok c -> c
            | Error e -> fail e

        known <- secrets credential

        try
            let c =
                new SqlConnection(connectionString address credential (trustServerCertificate env))

            try
                match credential with
                | SqlCredential.AccessToken token -> c.AccessToken <- token
                | SqlCredential.ServicePrincipal(clientId, tenantId, secret) ->
                    let token = servicePrincipalToken address.Server clientId tenantId secret
                    known <- token :: known
                    c.AccessToken <- token
                | _ -> ()

                c.Open()
                c
            with _ ->
                c.Dispose()
                reraise ()
        with
        | :? MsalException as e ->
            fail (
                AuthenticationFailed(
                    address.Server,
                    scrub e.Message,
                    Some $"(signed in with {describe credential}; {Var.auth} picks how)"
                )
            )
        | :? SqlException as e when e.Number = 18456 ->
            fail (
                AuthenticationFailed(
                    address.Server,
                    scrub e.Message,
                    Some $"(signed in with {describe credential}; {Var.auth} picks how)"
                )
            )
        | :? SqlException as e when e.Number = 4060 -> fail (DatabaseNotFound(address.Server, address.Database, []))
        | e ->
            let hint =
                if trustServerCertificate env then
                    None
                else
                    Some
                        $"If the server's certificate isn't trusted (a test server's self-signed one), {Var.trustServerCertificate}=1 accepts it."

            fail (ConnectionFailed(address.Server, scrub e.Message, hint))

    interface ISqlDescriber with
        member _.Describe(query: string) =
            let c =
                match connection with
                | Some c -> c
                | None ->
                    let c = openConnection ()
                    connection <- Some c
                    c

            try
                use cmd = new SqlCommand("sys.sp_describe_first_result_set", c)
                cmd.CommandType <- CommandType.StoredProcedure
                cmd.CommandTimeout <- 60

                cmd.Parameters.Add(SqlParameter("@tsql", SqlDbType.NVarChar, -1, Value = query))
                |> ignore

                use reader = cmd.ExecuteReader()
                let name = reader.GetOrdinal "name"
                let typeName = reader.GetOrdinal "system_type_name"
                let hidden = reader.GetOrdinal "is_hidden"

                let columns = [
                    while reader.Read() do
                        let isHidden = not (reader.IsDBNull hidden) && reader.GetBoolean hidden

                        if not isHidden then
                            {
                                Name =
                                    if reader.IsDBNull name then
                                        None
                                    else
                                        Some(reader.GetString name)
                                SystemTypeName =
                                    if reader.IsDBNull typeName then
                                        ""
                                    else
                                        reader.GetString typeName
                            }
                ]

                Ok columns
            with
            | :? SqlException as e when c.State = ConnectionState.Open -> Error(scrub e.Message)
            | e -> fail (ConnectionFailed(address.Server, scrub e.Message, None))

    interface IDisposable with
        member _.Dispose() =
            connection |> Option.iter (fun c -> c.Dispose())
            connection <- None

/// A describer for the address, signing in from the environment. Nothing is contacted until the
/// first query.
let connect (env: Credentials.Environment) (address: SqlAddress) : ISqlDescriber = new Describer(address, env)
