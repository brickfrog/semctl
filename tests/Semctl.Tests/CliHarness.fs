/// Drive the real CLI entry point with captured stdout/stderr. Shared by the CLI test files.
module Semctl.Tests.CliHarness

open System.IO
open System.Text.Json
open Expecto
open Semctl.Cli
open Semctl.Tests.Fixtures

type Run = { Code: int; Out: string; Err: string }

/// Environment variables sm reads for configuration. Tests never see the real ones, so a
/// developer's SM_BPA_RULES or ~/.config/semctl/bpa.json can't change results.
let private configVariables =
    set [ "SM_BPA_RULES"; "XDG_CONFIG_HOME"; "HOME"; "APPDATA" ]
    // Profiles and sessions (sm connect): never the developer's own.
    + set [ "SM_CONFIG"; "SM_STATE_DIR"; "SM_SESSION"; "XDG_STATE_HOME"; "LOCALAPPDATA" ]
    // A developer's SM_PKG_SOURCE (which may name daxlib.org) never reaches a test.
    + set [ "SM_PKG_SOURCE" ]
    // A developer's real server credentials never reach a test either.
    + set Semctl.Xmla.Credentials.Var.all
    + set Semctl.Xmla.SqlCredentials.Var.all

let private runCli
    (input: TextReader option)
    (connector: Semctl.Xmla.IXmlaConnector)
    (env: (string * string) list)
    (args: string list)
    =
    let env = Map.ofList env

    let getEnv (name: string) =
        match Map.tryFind name env with
        | Some v -> Some v
        | None when configVariables.Contains name -> None
        | None -> Common.processEnv name

    use out = new StringWriter()
    use err = new StringWriter()
    let code = App.runWithInput input connector getEnv out err (Array.ofList args)

    // WriteLine uses the platform newline (CRLF on Windows); tests compare against LF.
    let lf (s: string) = s.Replace("\r\n", "\n")

    {
        Code = code
        Out = lf (out.ToString())
        Err = lf (err.ToString())
    }

/// Run `sm <args>` in-process, with server connections going to `connector`, with these
/// environment variables (and none of the real configuration variables).
let semctlOn (connector: Semctl.Xmla.IXmlaConnector) (env: (string * string) list) (args: string list) =
    runCli None connector env args

/// Run `sm <args>` in-process with these environment variables (and none of the real
/// configuration variables). The command must not open a server connection: the test fails if it tries.
let semctlWithEnv (env: (string * string) list) (args: string list) =
    let offline = XmlaFakes.NoConnections()
    let run = semctlOn offline env args

    if offline.Attempts > 0 then
        failtestf "sm %s tried to open a server connection" (String.concat " " args)

    run

/// Run `sm <args>` in-process (without the real configuration variables).
let semctl (args: string list) = semctlWithEnv [] args

/// `semctlWithStdin`, with server connections going to `connector`.
let semctlWithStdinOn
    (connector: Semctl.Xmla.IXmlaConnector)
    (env: (string * string) list)
    (input: string)
    (args: string list)
    =
    use reader = new StringReader(input)
    runCli (Some(reader :> TextReader)) connector env args

/// Run `sm <args>` with `input` passed as stdin.
let semctlWithStdin (input: string) (args: string list) =
    let offline = XmlaFakes.NoConnections()
    let run = semctlWithStdinOn offline [] input args

    if offline.Attempts > 0 then
        failtestf "sm %s tried to open a server connection" (String.concat " " args)

    run

/// Parse a run's stdout as JSON (fails the test if it isn't).
let json (r: Run) : JsonElement =
    try
        use doc = JsonDocument.Parse(r.Out)
        doc.RootElement.Clone()
    with e ->
        failtestf "stdout is not JSON (%s):\n%s" e.Message r.Out

/// Read the `error.code` field from a JSON error response.
let errorCode (r: Run) : string =
    (json r).GetProperty("error").GetProperty("code").GetString()

/// A temp folder holding a PBIP project (Sales.pbip, Sales.Report, Sales.SemanticModel) for the
/// sample model. Dispose to delete.
let project () =
    let t = new TempDir()
    writePbipProject t.Path "Sales" |> ignore
    t

module ExitCode = Semctl.Cli.Common.ExitCode
