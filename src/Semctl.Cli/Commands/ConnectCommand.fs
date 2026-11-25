/// `sm connect`: make a model or server this terminal's active connection, after checking that it
/// can be used (a server: reached with the configured credentials, its databases listed; a local
/// model: loaded), so later commands can name it `active:`. Also: `--profile <name>` (no check, no
/// connection), no argument (show it), `--clear`. The state is local (Connections) and never holds
/// a secret.
module Semctl.Cli.ConnectCommand

open System
open System.IO
open Argu
open Semctl
open Semctl.Prelude
open Semctl.Xmla
open Semctl.Cli.Output
open Semctl.Cli.Common

/// One-line description, shown in `sm --help`.
let summary =
    "make a model or server (powerbi://, asazure://, SSAS) this terminal's active connection (`active:`); "
    + "check it, list its databases."

type Args =
    | [<MainCommand>] Server of server: string
    | Profile of name: string
    | Clear
    | No_Remember

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Server _ ->
                "a local model (any path sm accepts), or a server: powerbi://api.powerbi.com/v1.0/myorg/<workspace>, "
                + "asazure://<region>.asazure.windows.net/<server>, "
                + "https://<host>/olap/msmdpump.dll, or (Windows only) an SSAS host, host:port or host\\instance; "
                + "optionally followed by ;<database>. The `xmla:` prefix is optional here. Credentials come from the "
                + "environment, never the command line: SM_XMLA_TOKEN (or SM_XMLA_TOKEN_FILE) for an access token; "
                + "SM_XMLA_CLIENT_ID, SM_XMLA_TENANT_ID and SM_XMLA_CLIENT_SECRET (or ..._SECRET_FILE) for a "
                + "service principal; SM_XMLA_AUTH=device-code to sign in as a user; SM_XMLA_USER and "
                + "SM_XMLA_PASSWORD (or ..._PASSWORD_FILE) for a user name and password. Without it: show the "
                + "active connection."
            | Profile _ -> "make this profile's model the active connection (`sm profile`); nothing is contacted."
            | Clear -> "forget this terminal's active connection."
            | No_Remember -> "only check the server; leave the active connection as it is."

type private Report = {
    Address: XmlaAddress
    Credential: string
    Server: ServerProperties
    Databases: DatabaseSummary list
}

let private reportJson (r: Report) : System.Text.Json.Nodes.JsonObject =
    jsonObj [
        "server", str r.Address.DataSource
        "database", optStr r.Address.Database
        "authentication", str r.Credential
        "product", str r.Server.ProductName
        "version", str r.Server.Version
        "compatibilityMode",
        (match r.Server.CompatibilityMode with
         | Some m -> str (string m)
         | None -> null)
        "databases",
        arr [
            for d in r.Databases ->
                obj [
                    "name", str d.Name
                    "id", str d.Id
                    "compatibilityLevel", num d.CompatibilityLevel
                    "source", str (XmlaAddress.display (XmlaAddress.withDatabase d.Name r.Address))
                ]
        ]
    ]

let private reportText (out: TextWriter) (r: Report) =
    let row (label: string) (value: string) = out.WriteLine($"{label, -15} {value}")
    row "Server" r.Address.DataSource
    row "Signed in with" r.Credential

    row
        "Product"
        (match r.Server.ProductName, r.Server.Version with
         | "", v -> v
         | p, v -> $"{p} {v}")

    r.Server.CompatibilityMode |> Option.iter (fun m -> row "Mode" (string m))
    out.WriteLine()

    match r.Databases with
    | [] -> out.WriteLine("No databases (that these credentials can see).")
    | databases ->
        let width = databases |> List.map (fun d -> d.Name.Length) |> List.fold max 0

        for d in databases do
            out.WriteLine($"{d.Name.PadRight width}  {d.CompatibilityLevel}")

let private now () =
    DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Globalization.CultureInfo.InvariantCulture)

let private activeJson (a: Connections.ActiveConnection) =
    obj [ "model", str a.Model; "profile", optStr a.Profile; "since", str a.Since ]

/// Make it the active connection: the state, or a warning when it can't be kept (the check stands).
let private remember (ctx: Ctx) (active: Connections.ActiveConnection) =
    match Connections.activate ctx.GetEnv active with
    | Ok state ->
        if not ctx.Json then
            ctx.Err.WriteLine(
                $"Active connection for {Connections.SessionKey.describe state.Key}: {active.Model} (use it as `active:`)"
            )

        Some state
    | Error e ->
        ctx.Err.WriteLine($"sm: warning: not kept as the active connection: {SemctlError.message e}")
        None

let private show (ctx: Ctx) =
    match Connections.session ctx.GetEnv with
    | Some { Active = Some a; Key = key } ->
        if ctx.Json then
            writeJson ctx.Out (obj [ "session", str (Connections.SessionKey.describe key); "active", activeJson a ])
        else
            let from =
                a.Profile |> Option.map (fun p -> $"  (profile {p})") |> Option.defaultValue ""

            ctx.Out.WriteLine($"{a.Model}{from}")

        Done
    | _ ->
        match Connections.activeModel ctx.GetEnv with
        | Error e -> Failed e
        | Ok _ -> Done

let private local (ctx: Ctx) (model: string) (remembering: bool) =
    match ModelIO.loadPath model with
    | Error e -> Failed e
    | Ok(source, db) ->
        let active: Connections.ActiveConnection = {
            Model = Path.GetFullPath model
            Profile = None
            Since = now ()
        }

        let kept = if remembering then remember ctx active else None

        if ctx.Json then
            writeJson
                ctx.Out
                (obj [
                    "model", str source.Path
                    "format", str (ModelFormat.token source.Format)
                    "name", str (if isNull db.Model then "" else db.Model.Name)
                    "active", (if kept.IsSome then activeJson active else null)
                ])
        else
            ctx.Out.WriteLine($"Model  {source.Path}  ({ModelFormat.token source.Format})")

        Done

let run (ctx: Ctx) (args: ParseResults<Args>) =
    let remembering = not (args.Contains Args.No_Remember)

    match args.TryGetResult Args.Server, args.TryGetResult Args.Profile, args.Contains Args.Clear with
    | _, Some _, true
    | Some _, _, true -> UsageError "--clear takes no model, server or profile"
    | Some _, Some _, _ -> UsageError "give a model or server, or --profile, not both"
    | None, None, true ->
        match Connections.clear ctx.GetEnv with
        | Error e -> Failed e
        | Ok cleared ->
            if ctx.Json then
                writeJson ctx.Out (obj [ "cleared", bool cleared ])
            else
                ctx.Err.WriteLine(
                    if cleared then
                        "Cleared the active connection."
                    else
                        "No active connection."
                )

            Done
    | None, Some name, false ->
        match
            Connections.tryProfile ctx.GetEnv name
            |> Result.bind Connections.Profile.modelArgument
        with
        | Error e -> Failed e
        | Ok model ->
            let active: Connections.ActiveConnection = {
                Model = model
                Profile = Some name
                Since = now ()
            }

            match Connections.activate ctx.GetEnv active with
            | Error e -> Failed e
            | Ok state ->
                if ctx.Json then
                    writeJson
                        ctx.Out
                        (obj [
                            "session", str (Connections.SessionKey.describe state.Key)
                            "active", activeJson active
                        ])
                else
                    ctx.Err.WriteLine(
                        $"Active connection for {Connections.SessionKey.describe state.Key}: profile {name}, {model} (use it as `active:`)"
                    )

                Done
    | None, None, false -> show ctx
    | Some model, None, false when
        not (XmlaAddress.isRemote model)
        && (File.Exists model || Directory.Exists model)
        ->
        local ctx model remembering
    | Some server, None, false ->
        let access = Source.access ctx

        let result =
            result {
                let! address = XmlaAddress.parseServer server

                return!
                    Remote.withConnection access address (fun c ->
                        result {
                            let! server = Remote.serverProperties c
                            let! databases = Remote.databases c

                            // A database in the address must exist.
                            do!
                                match address.Database with
                                | Some _ -> Remote.resolveDatabase c |> Result.map ignore
                                | None -> Ok()

                            let! credential = Credentials.resolve access.Environment address

                            return {
                                Address = address
                                Credential = XmlaCredential.describe credential
                                Server = server
                                Databases = databases
                            }
                        })
            }

        match result with
        | Error e -> Failed e
        | Ok report ->
            let active: Connections.ActiveConnection = {
                Model = XmlaAddress.display report.Address
                Profile = None
                Since = now ()
            }

            let kept = if remembering then remember ctx active else None

            if ctx.Json then
                let json = reportJson report

                if kept.IsSome then
                    json.["active"] <- activeJson active

                writeJson ctx.Out json
            else
                reportText ctx.Out report

            Done
