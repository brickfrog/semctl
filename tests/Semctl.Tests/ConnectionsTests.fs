/// Profiles (`sm profile`), the active connection (`sm connect`, `sm session`) and the
/// `active:` / `profile:<name>` model arguments. Every test has its own config file and state
/// folder (SM_CONFIG, SM_STATE_DIR), and most a named session (SM_SESSION).
module Semctl.Tests.ConnectionsTests

open System.IO
open System.Text.Json
open Expecto
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.Tests.Fixtures
open Semctl.Tests.XmlaFakes
open Semctl.Tests.CliHarness
open Semctl.Cli.Common

type private Home() =
    let dir = new TempDir()
    member _.Dir = dir
    member _.Config = dir.Combine("config", "config.json")
    member _.State = dir.Combine "state"

    /// The environment of a terminal (a named session, so tests don't share the real terminal's).
    member this.Env(session: string) = [ "SM_CONFIG", this.Config; "SM_STATE_DIR", this.State; "SM_SESSION", session ]

    interface System.IDisposable with
        member _.Dispose() = (dir :> System.IDisposable).Dispose()

let private code (r: Run) = r.Code

let private errorCode (r: Run) =
    use doc = JsonDocument.Parse r.Out
    doc.RootElement.GetProperty("error").GetProperty("code").GetString()

[<Tests>]
let profileTests =
    testList "sm profile" [
        testCase "set, list, show, update, remove; the config file keeps what it doesn't know"
        <| fun () ->
            use home = new Home()
            let env = home.Env "a"
            write home.Config """{ "other": { "kept": true } }"""
            let model = home.Dir.Combine "model.bim"

            let r =
                semctlWithEnv env [
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
                (semctlWithEnv env [
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

            let list = semctlWithEnv env [ "profile"; "list" ]
            Expect.stringContains list.Out $"dev   {model}  Local" ""
            Expect.stringContains list.Out "prod  xmla:powerbi://api.powerbi.com/v1.0/myorg/Finance;Revenue" ""

            let shown = semctlWithEnv env [ "profile"; "show"; "dev"; "--json" ]
            use doc = JsonDocument.Parse shown.Out
            Expect.equal (doc.RootElement.GetProperty("modelArgument").GetString()) model ""

            Expect.equal
                (doc.RootElement.GetProperty("environment").GetProperty("SM_BPA_RULES").GetString())
                "r.json"
                "a setting is a default for its variable"

            // An update keeps what it doesn't change.
            Expect.equal (semctlWithEnv env [ "profile"; "set"; "dev"; "--unset-setting"; "bpaRules" ]).Code 0 ""
            let shown = semctlWithEnv env [ "profile"; "show"; "dev" ]
            Expect.stringContains shown.Out "Local" ""
            Expect.isFalse (shown.Out.Contains "bpaRules") ""

            Expect.stringContains (File.ReadAllText home.Config) "\"kept\": true" ""
            Expect.equal (semctlWithEnv env [ "profile"; "remove"; "dev" ]).Code 0 ""
            let gone = semctlWithEnv env [ "profile"; "show"; "dev"; "--json" ]
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
                semctlWithEnv env [
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
                let r = semctlWithEnv env args
                Expect.notEqual r.Code 0 (String.concat " " args)

            Expect.isFalse (File.ReadAllText(home.Config).Contains "hunter2") "nothing kept"

            let both =
                semctlWithEnv env [ "profile"; "set"; "x"; "--model"; "m"; "--server"; "localhost" ]

            Expect.equal both.Code ExitCode.usage both.Err
    ]

[<Tests>]
let connectTests =
    testList "sm connect, sessions and active:" [
        testCase "a local model becomes the active connection; active: names it; --clear forgets it"
        <| fun () ->
            use home = new Home()
            use t = project ()
            let env = home.Env "a"
            let r = semctlWithEnv env [ "connect"; t.Path ]
            Expect.equal r.Code 0 r.Err
            Expect.stringContains r.Err "Active connection for SM_SESSION=a" ""
            Expect.stringContains (semctlWithEnv env [ "connect" ]).Out t.Path "shown"
            let listed = semctlWithEnv env [ "list"; "active:"; "--kind"; "measure" ]
            Expect.equal listed.Code 0 listed.Err
            Expect.stringContains listed.Out "measure:Sales/Total" ""

            // Another session doesn't see it.
            let other = semctlWithEnv (home.Env "b") [ "list"; "active:"; "--json" ]
            Expect.equal other.Code ExitCode.input other.Out
            Expect.equal (errorCode other) "no-active-connection" ""

            Expect.equal (semctlWithEnv env [ "connect"; "--clear" ]).Code 0 ""
            Expect.equal (semctlWithEnv env [ "list"; "active:" ]).Code ExitCode.input ""

        testCase "a terminal's own session (no SM_SESSION) keeps it too"
        <| fun () ->
            use home = new Home()
            use t = project ()
            let env = [ "SM_CONFIG", home.Config; "SM_STATE_DIR", home.State ]
            Expect.equal (semctlWithEnv env [ "connect"; t.Path ]).Code 0 ""
            Expect.equal (semctlWithEnv env [ "info"; "active:" ]).Code 0 ""
            let shown = semctlWithEnv env [ "session" ]
            Expect.stringContains shown.Out "this terminal (process " ""
            Expect.stringContains (semctlWithEnv env [ "session"; "list" ]).Out t.Path ""

        testCase "connect --profile: nothing contacted; profile:<name> one-shot; the profile's settings apply"
        <| fun () ->
            use home = new Home()
            use t = project ()
            let env = home.Env "a"
            let rules = home.Dir.Combine "missing-rules.json"

            Expect.equal
                (semctlWithEnv env [ "profile"; "set"; "dev"; "--model"; t.Path; "--setting"; $"bpaRules={rules}" ])
                    .Code
                0
                ""

            Expect.equal
                (semctlWithEnv env [ "get"; "profile:dev"; "[Total]"; "-p"; "expression" ]).Out
                "SUM(Sales[Amount])\n"
                ""

            // The profile's bpaRules is SM_BPA_RULES for commands that use the profile...
            let bpa = semctlWithEnv env [ "bpa"; "profile:dev" ]
            Expect.notEqual bpa.Code 0 bpa.Out
            Expect.stringContains (bpa.Out + bpa.Err) "missing-rules.json" ""
            // ... unless the environment sets it.
            let ownRules = home.Dir.Combine "own.json"
            write ownRules "[]"

            let mine =
                semctlWithEnv (("SM_BPA_RULES", ownRules) :: env) [ "bpa"; "profile:dev" ]

            Expect.isFalse ((mine.Out + mine.Err).Contains "missing-rules.json") (mine.Out + mine.Err)

            let connected = semctlWithEnv env [ "connect"; "--profile"; "dev"; "--json" ]
            Expect.equal connected.Code 0 connected.Err
            use doc = JsonDocument.Parse connected.Out
            Expect.equal (doc.RootElement.GetProperty("active").GetProperty("profile").GetString()) "dev" ""
            let viaActive = semctlWithEnv env [ "bpa"; "active:" ]

            Expect.stringContains
                (viaActive.Out + viaActive.Err)
                "missing-rules.json"
                "active: brings its profile's settings"

            let nope = semctlWithEnv env [ "info"; "profile:nope"; "--json" ]
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

            let r =
                semctlOn server (tokenEnv @ env) [ "connect"; $"{workspace};Sales"; "--json" ]

            Expect.equal r.Code 0 r.Err
            use doc = JsonDocument.Parse r.Out
            let expected = $"xmla:{workspace};Sales"
            Expect.equal (doc.RootElement.GetProperty("active").GetProperty("model").GetString()) expected ""
            Expect.equal (semctlOn server (tokenEnv @ env) [ "connect" ]).Out (expected + "\n") ""

            // Only `active:` itself contacts it; bare `sm interactive` refuses to.
            let before = server.Requests.Length
            let i = semctlWithEnv env [ "interactive" ]
            Expect.equal i.Code ExitCode.usage i.Err
            Expect.stringContains i.Err "sm interactive active:" ""
            Expect.equal server.Requests.Length before "nothing contacted"

            let unchanged =
                semctlOn server (tokenEnv @ env) [ "connect"; workspace; "--no-remember" ]

            Expect.equal unchanged.Code 0 unchanged.Err
            Expect.equal (semctlOn server (tokenEnv @ env) [ "connect" ]).Out (expected + "\n") "--no-remember"

        testCase "no place for state: connect still checks, and warns that it can't keep it"
        <| fun () ->
            use t = project ()
            let r = semctl [ "connect"; t.Path ]
            Expect.equal r.Code 0 r.Err
            Expect.stringContains r.Err "warning: not kept as the active connection" ""
            Expect.equal (semctl [ "info"; "active:" ]).Code ExitCode.input ""

        testCase "sm interactive without a model starts on the (local) active connection"
        <| fun () ->
            use home = new Home()
            use t = project ()
            let env = home.Env "a"
            Expect.equal (semctlWithEnv env [ "connect"; t.Path ]).Code 0 ""
            use input = new StringReader("info\n")
            use out = new StringWriter()
            use err = new StringWriter()

            let getEnv name =
                env |> List.tryPick (fun (k, v) -> if k = name then Some v else None)

            let code =
                Semctl.Cli.App.runWithInput (Some input) (NoConnections()) getEnv out err [| "interactive" |]

            Expect.equal code 0 (err.ToString())
            Expect.stringContains (out.ToString()) "Sales.SemanticModel" ""

        testCase "session clear and prune"
        <| fun () ->
            use home = new Home()
            use t = project ()
            let env = home.Env "a"
            Expect.equal (semctlWithEnv env [ "connect"; t.Path ]).Code 0 ""
            // A terminal that is gone: a process id nothing runs as.
            write (Path.Combine(home.State, "pid-999999999.json")) """{ "model": "x", "terminal": 999999999 }"""
            let listed = semctlWithEnv env [ "session"; "list"; "--json" ]
            use doc = JsonDocument.Parse listed.Out
            let sessions = doc.RootElement.GetProperty "sessions"
            Expect.equal (sessions.GetArrayLength()) 2 ""
            Expect.equal (semctlWithEnv env [ "session"; "prune" ]).Code 0 ""
            Expect.isFalse (File.Exists(Path.Combine(home.State, "pid-999999999.json"))) "pruned"
            Expect.isTrue (File.Exists(Path.Combine(home.State, "name-a.json"))) "a named session is kept"
            Expect.equal (semctlWithEnv env [ "session"; "clear" ]).Code 0 ""
            Expect.isFalse (File.Exists(Path.Combine(home.State, "name-a.json"))) "cleared"
    ]
