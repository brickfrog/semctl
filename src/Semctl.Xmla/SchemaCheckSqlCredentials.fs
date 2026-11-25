/// How `schemacheck --source sql:<server>;<database>` signs in: from environment variables (and
/// files they point at), never from the command line. Nothing here contacts anything.
module Semctl.Xmla.SqlCredentials

open System
open Semctl
open Semctl.SchemaCheckSql

/// Values of this type hold secrets: never print one (use `describe`).
[<RequireQualifiedAccess>]
type SqlCredential =
    /// A Microsoft Entra access token for https://database.windows.net/.
    | AccessToken of token: string
    /// A service principal; sm asks Microsoft's sign-in service for a SQL token with it.
    | ServicePrincipal of clientId: string * tenantId: string * secret: string
    /// A SQL Server login.
    | SqlLogin of user: string * password: string
    /// The current Windows (or Kerberos) identity.
    | Integrated

module Var =
    /// auto (default), token, spn, sql, integrated.
    let auth = "SM_SQL_AUTH"
    let token = "SM_SQL_TOKEN"
    let tokenFile = "SM_SQL_TOKEN_FILE"
    let user = "SM_SQL_USER"
    let password = "SM_SQL_PASSWORD"
    let passwordFile = "SM_SQL_PASSWORD_FILE"
    /// `1`/`true`: accept a server certificate that isn't trusted (a local test container's
    /// self-signed one). The connection is still encrypted. Off unless set.
    let trustServerCertificate = "SM_SQL_TRUST_SERVER_CERTIFICATE"

    let all = [ auth; token; tokenFile; user; password; passwordFile; trustServerCertificate ]

let describe =
    function
    | SqlCredential.AccessToken _ -> "access token"
    | SqlCredential.ServicePrincipal(clientId, tenantId, _) -> $"service principal {clientId} in tenant {tenantId}"
    | SqlCredential.SqlLogin(user, _) -> $"SQL login {user}"
    | SqlCredential.Integrated -> "integrated security"

let secrets =
    function
    | SqlCredential.AccessToken token -> [ token ]
    | SqlCredential.ServicePrincipal(_, _, secret) -> [ secret ]
    | SqlCredential.SqlLogin(_, password) -> [ password ]
    | SqlCredential.Integrated -> []

/// The host part of a server name: no `tcp:` prefix, port or instance.
let host (server: string) =
    let s = server.Trim()

    let s =
        if s.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) then
            s.Substring 4
        else
            s

    s.Split([| ','; '\\' |]).[0].Trim().ToLowerInvariant()

/// Microsoft's cloud SQL hosts, which take Entra tokens: Azure SQL (all clouds), Synapse, Fabric.
let private cloudSuffixes = [
    ".database.windows.net"
    ".database.chinacloudapi.cn"
    ".database.usgovcloudapi.net"
    ".sql.azuresynapse.net"
    ".datawarehouse.fabric.microsoft.com"
    ".database.fabric.microsoft.com"
]

let isCloud (server: string) =
    let h = host server
    cloudSuffixes |> List.exists (fun s -> h.EndsWith s)

/// Whether SM_SQL_TRUST_SERVER_CERTIFICATE is on.
let trustServerCertificate (env: Credentials.Environment) =
    match env.GetEnv Var.trustServerCertificate with
    | Some v ->
        let v = v.Trim().ToLowerInvariant()
        v = "1" || v = "true" || v = "yes"
    | None -> false

let private token (env: Credentials.Environment) =
    Credentials.secret env Var.token Var.tokenFile
    |> Result.map (Option.map (fun t -> SqlCredential.AccessToken(t.Trim())))

/// The service principal connected mode uses: SM_XMLA_CLIENT_ID / _TENANT_ID / _CLIENT_SECRET(_FILE),
/// else AZURE_CLIENT_ID / AZURE_TENANT_ID / AZURE_CLIENT_SECRET.
let private servicePrincipal (env: Credentials.Environment) =
    let v = Credentials.Var.clientId, Credentials.Var.tenantId

    match env.GetEnv(fst v), env.GetEnv(snd v) with
    | Some clientId, Some tenantId ->
        Credentials.secret env Credentials.Var.clientSecret Credentials.Var.clientSecretFile
        |> Result.bind (function
            | Some s -> Ok(Some(SqlCredential.ServicePrincipal(clientId, tenantId, s)))
            | None -> Ok None)
    | _ ->
        match
            env.GetEnv Credentials.Var.azureClientId,
            env.GetEnv Credentials.Var.azureTenantId,
            env.GetEnv Credentials.Var.azureClientSecret
        with
        | Some c, Some t, Some s -> Ok(Some(SqlCredential.ServicePrincipal(c, t, s)))
        | _ -> Ok None

let private login (env: Credentials.Environment) =
    match env.GetEnv Var.user with
    | None -> Ok None
    | Some user ->
        Credentials.secret env Var.password Var.passwordFile
        |> Result.bind (function
            | Some p -> Ok(Some(SqlCredential.SqlLogin(user, p)))
            | None -> Error(InvalidCredentials $"{Var.user} is set but {Var.password} (or {Var.passwordFile}) isn't"))

let private hint =
    $"set {Var.token} (or {Var.tokenFile}) to an Entra access token for https://database.windows.net/; or "
    + $"{Credentials.Var.clientId}, {Credentials.Var.tenantId} and {Credentials.Var.clientSecret} for a service principal; "
    + $"or {Var.user} and {Var.password} for a SQL login"

/// The credential for a server, from the environment. A token or service principal is only ever
/// used for a Microsoft cloud host; nothing interactive is ever started.
let resolve (env: Credentials.Environment) (address: SqlAddress) : Result<SqlCredential, SemctlError> =
    let cloud = isCloud address.Server
    let server = address.Server

    let required (what: string) (found: Result<SqlCredential option, SemctlError>) =
        found
        |> Result.bind (function
            | Some c -> Ok c
            | None -> Error(AuthenticationRequired(server, $"{Var.auth}={what}, but {hint}")))

    let cloudOnly (mode: string) next =
        Credentials.cloudOnly cloud Var.auth server mode "Azure SQL and Fabric hosts" (fun () ->
            next ()
            |> Result.mapError (function
                | InvalidCredentials msg -> InvalidCredentials(msg + " (an Entra credential is never sent elsewhere)")
                | e -> e))

    match (env.GetEnv Var.auth |> Option.defaultValue "auto").Trim().ToLowerInvariant() with
    | "token" -> cloudOnly "token" (fun () -> required "token" (token env))
    | "spn" -> cloudOnly "spn" (fun () -> required "spn" (servicePrincipal env))
    | "sql" -> required "sql" (login env)
    | "integrated" -> Ok SqlCredential.Integrated
    | "auto" ->
        if cloud then
            Credentials.firstOf server hint [
                (fun () -> token env)
                (fun () -> servicePrincipal env)
                (fun () -> login env)
            ]
        else
            login env |> Result.map (Option.defaultValue SqlCredential.Integrated)
    | other -> Error(InvalidCredentials $"{Var.auth}={other}: expected auto, token, spn, sql or integrated")
