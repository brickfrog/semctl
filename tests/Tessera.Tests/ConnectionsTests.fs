/// Profiles (`tess profile`), the active connection (`tess connect`, `tess session`) and the
/// `active:` / `profile:<name>` model arguments. Every test has its own config file and state
/// folder (TESS_CONFIG, TESS_STATE_DIR), and most a named session (TESS_SESSION).
module Tessera.Tests.ConnectionsTests

open System.IO
open System.Text.Json
open Expecto
open Microsoft.AnalysisServices.Tabular
open Tessera
open Tessera.Tests.Fixtures
open Tessera.Tests.XmlaFakes
open Tessera.Tests.CliHarness
open Tessera.Cli.Common

type private Home() =
    let dir = new TempDir()
    member _.Dir = dir
    member _.Config = dir.Combine("config", "config.json")
    member _.State = dir.Combine "state"

    /// The environment of a terminal (a named session, so tests don't share the real terminal's).
    member this.Env(session: string) = [
        "TESS_CONFIG", this.Config
        "TESS_STATE_DIR", this.State
        "TESS_SESSION", session
    ]

    interface System.IDisposable with
        member _.Dispose() = (dir :> System.IDisposable).Dispose()

let private code (r: Run) = r.Code

let private errorCode (r: Run) =
    use doc = JsonDocument.Parse r.Out
    doc.RootElement.GetProperty("error").GetProperty("code").GetString()

[<Tests>]
let profileTests =
    testList "tess profile" [
        testCase "set, list, show, update, remove; the config file keeps what it doesn't know"
        <| fun () ->
            use home = new Home()
            let env = home.Env "a"
            write home.Config """{ "other": { "kept": true } }"""
            let model = home.Dir.Combine "model.bim"

            let r =
                tessWithEnv env [
                    "profile"
                    "set"
                    "dev"
                    "--model"
                    model
                    "--description"
                    "Local"
                    "--setting"
                    "bpaRules=r.json"
                ]

            Expect.equal r.Code 0 r.Err

            Expect.equal
                (tessWithEnv env [
                    "profile"
                    "set"
                    "prod"
                    "--server"
                    "powerbi://api.powerbi.com/v1.0/myorg/Finance"
                    "--database"
                    "Revenue"
                ])
                    .Code
                0
                ""

            let list = tessWithEnv env [ "profile"; "list" ]
            Expect.stringContains list.Out $"dev   {model}  Local" ""
            Expect.stringContains list.Out "prod  xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Revenue" ""

            let shown = tessWithEnv env [ "profile"; "show"; "dev"; "--json" ]
            use doc = JsonDocument.Parse shown.Out
            Expect.equal (doc.RootElement.GetProperty("modelArgument").GetString()) model ""

            Expect.equal
                (doc.RootElement.GetProperty("environment").GetProperty("TESS_BPA_RULES").GetString())
                "r.json"
                "a setting is a default for its variable"

            // An update keeps what it doesn't change.
            Expect.equal (tessWithEnv env [ "profile"; "set"; "dev"; "--unset-setting"; "bpaRules" ]).Code 0 ""
            let shown = tessWithEnv env [ "profile"; "show"; "dev" ]
            Expect.stringContains shown.Out "Local" ""
            Expect.isFalse (shown.Out.Contains "bpaRules") ""

            Expect.stringContains (File.ReadAllText home.Config) "\"kept\": true" ""
            Expect.equal (tessWithEnv env [ "profile"; "remove"; "dev" ]).Code 0 ""
            let gone = tessWithEnv env [ "profile"; "show"; "dev"; "--json" ]
            Expect.equal gone.Code ExitCode.input ""
            Expect.equal (errorCode gone) "profile-not-found" ""

        testCase "never a secret: credentials in a server, unknown settings, a password setting"
        <| fun () ->
            use home = new Home()
            let env = home.Env "a"

            let refused = [
                [
                    "profile"
                    "set"
                    "x"
                    "--server"
                    "Data Source=asazure://x;Password=hunter2"
                ]
                [ "profile"; "set"; "x"; "--model"; "m"; "--setting"; "password=hunter2" ]
                [ "profile"; "set"; "x"; "--model"; "m"; "--setting"; "clientSecret=hunter2" ]
                [
                    "profile"
                    "set"
                    "x"
                    "--model"
                    "m"
                    "--setting"
                    "user=me;Password=hunter2"
                ]
                [ "profile"; "set"; "bad name"; "--model"; "m" ]
            ]

            // A path with such a word in it is no secret.
            let fine =
                tessWithEnv env [
                    "profile"
                    "set"
                    "y"
                    "--model"
                    "~/secret-santa/Sales.pbip"
                    "--setting"
                    "tokenCache=~/.secrets/cache"
                ]

            Expect.equal fine.Code 0 fine.Err

            for args in refused do
                let r = tessWithEnv env args
                Expect.notEqual r.Code 0 (String.concat " " args)

            Expect.isFalse (File.ReadAllText(home.Config).Contains "hunter2") "nothing kept"

            let both =
                tessWithEnv env [ "profile"; "set"; "x"; "--model"; "m"; "--server"; "localhost" ]

            Expect.equal both.Code ExitCode.usage both.Err
    ]

[<Tests>]
let connectTests =
    testList "tess connect, sessions and active:" [
        testCase "a local model becomes the active connection; active: names it; --clear forgets it"
        <| fun () ->
            use home = new Home()
            use t = project ()
            let env = home.Env "a"
            let r = tessWithEnv env [ "connect"; t.Path ]
            Expect.equal r.Code 0 r.Err
            Expect.stringContains r.Err "Active connection for TESS_SESSION=a" ""
            Expect.stringContains (tessWithEnv env [ "connect" ]).Out t.Path "shown"
            let listed = tessWithEnv env [ "list"; "active:"; "--kind"; "measure" ]
            Expect.equal listed.Code 0 listed.Err
            Expect.stringContains listed.Out "measure:Sales/Total" ""

            // Another session doesn't see it.
            let other = tessWithEnv (home.Env "b") [ "list"; "active:"; "--json" ]
            Expect.equal other.Code ExitCode.input other.Out
            Expect.equal (errorCode other) "no-active-connection" ""

            Expect.equal (tessWithEnv env [ "connect"; "--clear" ]).Code 0 ""
            Expect.equal (tessWithEnv env [ "list"; "active:" ]).Code ExitCode.input ""

        testCase "a terminal's own session (no TESS_SESSION) keeps it too"
        <| fun () ->
            use home = new Home()
            use t = project ()
            let env = [ "TESS_CONFIG", home.Config; "TESS_STATE_DIR", home.State ]
            Expect.equal (tessWithEnv env [ "connect"; t.Path ]).Code 0 ""
            Expect.equal (tessWithEnv env [ "info"; "active:" ]).Code 0 ""
            let shown = tessWithEnv env [ "session" ]
            Expect.stringContains shown.Out "this terminal (process " ""
            Expect.stringContains (tessWithEnv env [ "session"; "list" ]).Out t.Path ""

        testCase "connect --profile: nothing contacted; profile:<name> one-shot; the profile's settings apply"
        <| fun () ->
            use home = new Home()
            use t = project ()
            let env = home.Env "a"
            let rules = home.Dir.Combine "missing-rules.json"

            Expect.equal
                (tessWithEnv env [ "profile"; "set"; "dev"; "--model"; t.Path; "--setting"; $"bpaRules={rules}" ]).Code
                0
                ""

            Expect.equal
                (tessWithEnv env [ "get"; "profile:dev"; "[Total]"; "-p"; "expression" ]).Out
                "SUM(Sales[Amount])\n"
                ""

            // The profile's bpaRules is TESS_BPA_RULES for commands that use the profile...
            let bpa = tessWithEnv env [ "bpa"; "profile:dev" ]
            Expect.notEqual bpa.Code 0 bpa.Out
            Expect.stringContains (bpa.Out + bpa.Err) "missing-rules.json" ""
            // ... unless the environment sets it.
            let ownRules = home.Dir.Combine "own.json"
            write ownRules "[]"

            let mine =
                tessWithEnv (("TESS_BPA_RULES", ownRules) :: env) [ "bpa"; "profile:dev" ]

            Expect.isFalse ((mine.Out + mine.Err).Contains "missing-rules.json") (mine.Out + mine.Err)

            let connected = tessWithEnv env [ "connect"; "--profile"; "dev"; "--json" ]
            Expect.equal connected.Code 0 connected.Err
            use doc = JsonDocument.Parse connected.Out
            Expect.equal (doc.RootElement.GetProperty("active").GetProperty("profile").GetString()) "dev" ""
            let viaActive = tessWithEnv env [ "bpa"; "active:" ]

            Expect.stringContains
                (viaActive.Out + viaActive.Err)
                "missing-rules.json"
                "active: brings its profile's settings"

            let nope = tessWithEnv env [ "info"; "profile:nope"; "--json" ]
            Expect.equal (errorCode nope) "profile-not-found" ""

        testCase "a server: checked, then kept as xmla:<server>;<database>"
        <| fun () ->
            use home = new Home()
            let env = home.Env "a"
            let server = FakeServer()
            let sales = sampleDatabase ()
            sales.Name <- "Sales"
            sales.ID <- "sales-id"
            server.Add sales
            let r = tessOn server (tokenEnv @ env) [ "connect"; $"{workspace};Sales"; "--json" ]
            Expect.equal r.Code 0 r.Err
            use doc = JsonDocument.Parse r.Out
            let expected = $"xmla:{workspace};Sales"
            Expect.equal (doc.RootElement.GetProperty("active").GetProperty("model").GetString()) expected ""
            Expect.equal (tessOn server (tokenEnv @ env) [ "connect" ]).Out (expected + "\n") ""

            // Only `active:` itself contacts it; bare `tess interactive` refuses to.
            let before = server.Requests.Length
            let i = tessWithEnv env [ "interactive" ]
            Expect.equal i.Code ExitCode.usage i.Err
            Expect.stringContains i.Err "tess interactive active:" ""
            Expect.equal server.Requests.Length before "nothing contacted"

            let unchanged =
                tessOn server (tokenEnv @ env) [ "connect"; workspace; "--no-remember" ]

            Expect.equal unchanged.Code 0 unchanged.Err
            Expect.equal (tessOn server (tokenEnv @ env) [ "connect" ]).Out (expected + "\n") "--no-remember"

        testCase "no place for state: connect still checks, and warns that it can't keep it"
        <| fun () ->
            use t = project ()
            let r = tess [ "connect"; t.Path ]
            Expect.equal r.Code 0 r.Err
            Expect.stringContains r.Err "warning: not kept as the active connection" ""
            Expect.equal (tess [ "info"; "active:" ]).Code ExitCode.input ""

        testCase "tess interactive without a model starts on the (local) active connection"
        <| fun () ->
            use home = new Home()
            use t = project ()
            let env = home.Env "a"
            Expect.equal (tessWithEnv env [ "connect"; t.Path ]).Code 0 ""
            use input = new StringReader("info\n")
            use out = new StringWriter()
            use err = new StringWriter()

            let getEnv name =
                env |> List.tryPick (fun (k, v) -> if k = name then Some v else None)

            let code =
                Tessera.Cli.App.runWithInput (Some input) (NoConnections()) getEnv out err [| "interactive" |]

            Expect.equal code 0 (err.ToString())
            Expect.stringContains (out.ToString()) "Sales.SemanticModel" ""

        testCase "session clear and prune"
        <| fun () ->
            use home = new Home()
            use t = project ()
            let env = home.Env "a"
            Expect.equal (tessWithEnv env [ "connect"; t.Path ]).Code 0 ""
            // A terminal that is gone: a process id nothing runs as.
            write (Path.Combine(home.State, "pid-999999999.json")) """{ "model": "x", "terminal": 999999999 }"""
            let listed = tessWithEnv env [ "session"; "list"; "--json" ]
            use doc = JsonDocument.Parse listed.Out
            let sessions = doc.RootElement.GetProperty "sessions"
            Expect.equal (sessions.GetArrayLength()) 2 ""
            Expect.equal (tessWithEnv env [ "session"; "prune" ]).Code 0 ""
            Expect.isFalse (File.Exists(Path.Combine(home.State, "pid-999999999.json"))) "pruned"
            Expect.isTrue (File.Exists(Path.Combine(home.State, "name-a.json"))) "a named session is kept"
            Expect.equal (tessWithEnv env [ "session"; "clear" ]).Code 0 ""
            Expect.isFalse (File.Exists(Path.Combine(home.State, "name-a.json"))) "cleared"
    ]
