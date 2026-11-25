/// `sm interactive`: the line splitter, and sessions driven through the real CLI entry point
/// with their input from a reader (piped input), or from a scripted terminal.
module Semctl.Tests.InteractiveTests

open System
open System.IO
open System.Text.Json
open Expecto
open Semctl
open Semctl.Tests.Fixtures
open Semctl.Tests.CliHarness
open Semctl.Cli
open Semctl.Cli.Common

/// Run `sm interactive <args>` with these lines as its input.
let private session (args: string list) (lines: string list) =
    use input = new StringReader(String.concat "\n" lines + "\n")
    use out = new StringWriter()
    use err = new StringWriter()

    let code =
        App.runWithInput
            (Some input)
            (XmlaFakes.NoConnections())
            (fun _ -> None)
            out
            err
            (Array.ofList ("interactive" :: args))

    let lf (s: string) = s.Replace("\r\n", "\n")

    {
        Code = code
        Out = lf (out.ToString())
        Err = lf (err.ToString())
    }


let private measures (path: string) = [ for m in (loadModel path).Model.Tables.["Sales"].Measures -> m.Name ]

/// The sample project with one visual (ReportTests.visual).
let private withReport () =
    let t = project ()

    let file =
        t.Combine("Sales.Report", "definition", "pages", "p1", "visuals", "v1", "visual.json")

    write file ReportTests.visual
    write (t.Combine("Sales.Report", "definition", "version.json")) """{ "version": "2.0.0" }"""
    t, file

let private ok (r: Run) =
    Expect.equal r.Code ExitCode.ok (r.Out + r.Err)

/// The JSON documents in a session's output (each indented over several lines, ending with a
/// "}" line of its own).
let private jsonDocs (out: string) = [
    let chunk = Text.StringBuilder()

    for line in out.Split('\n') do
        if chunk.Length > 0 || line.StartsWith "{" then
            chunk.AppendLine line |> ignore

            if line = "}" then
                use doc = JsonDocument.Parse(chunk.ToString())
                yield doc.RootElement.Clone()
                chunk.Clear() |> ignore
]

[<Tests>]
let splitTests =
    let split line =
        match InteractiveCommand.split line with
        | Ok args -> args
        | Error e -> failtestf "split %s: %s" line e

    testList "sm interactive: splitting a line" [
        testCase "words, double and single quotes (doubled to escape)"
        <| fun () ->
            Expect.equal (split "get  [Total]   -p expression") [ "get"; "[Total]"; "-p"; "expression" ] ""

            Expect.equal
                (split "query \"EVALUATE ROW(\"\"x\"\", 1)\"")
                [ "query"; "EVALUATE ROW(\"x\", 1)" ]
                "a doubled double quote is a literal one"

            Expect.equal (split "set x 'it''s'") [ "set"; "x"; "it's" ] ""
            Expect.equal (split "a\"b c\"d") [ "ab cd" ] "groups join the word around them"

        testCase "brackets stay; a quoted table name before [ keeps its quotes"
        <| fun () ->
            Expect.equal (split "get [Total Sales]") [ "get"; "[Total Sales]" ] ""
            Expect.equal (split "get 'Internet Sales'[Sales Amount]") [ "get"; "'Internet Sales'[Sales Amount]" ] ""
            Expect.equal (split "get 'It''s'[A]") [ "get"; "'It''s'[A]" ] "doubled again inside the kept quotes"
            Expect.equal (split "get [a]]b]") [ "get"; "[a]]b]" ] "]] stays as it is"
            Expect.equal (split "ls 'Net Sales'/'Sales Amount'") [ "ls"; "Net Sales/Sales Amount" ] ""

        testCase "an unterminated group is an error, not a silent split"
        <| fun () ->
            for line in [ "get \"abc"; "get 'abc"; "get [abc" ] do
                Expect.isError (InteractiveCommand.split line) line

        testCase "empty and blank lines are no arguments"
        <| fun () ->
            Expect.equal (split "") [] ""
            Expect.equal (split "   \t ") [] ""
    ]

[<Tests>]
let sessionTests =
    testList "sm interactive" [
        testCase "commands run without the model; edits stage until save"
        <| fun () ->
            use t = project ()

            let r =
                session [ t.Path ] [
                    "add measure:Sales/Margin --expression \"[Total] * 0.1\""
                    "get [Margin] -p expression"
                    "status"
                    "save"
                ]

            ok r
            Expect.stringContains r.Out "[Total] * 0.1" "get sees the staged measure"
            Expect.stringContains r.Out "Staged  1 change in 1 edit (not saved)" ""
            Expect.stringContains r.Err "Staged (1 unsaved change in this session)" ""
            Expect.stringContains r.Err "Saved pbip" ""
            Expect.contains (measures t.Path) "Margin" "saved"

        testCase "nothing is written before save; the end of piped input with staged edits fails"
        <| fun () ->
            use t = project ()

            let r =
                session [ t.Path ] [ "add measure:Sales/Margin -e 1"; "rename [Total] Revenue" ]

            Expect.equal r.Code 1 r.Err
            Expect.stringContains r.Err "The session ended with 3 unsaved changes that were never saved" ""
            Expect.stringContains r.Err "  measure:Sales/Margin" "the changes are listed"
            Expect.equal (measures t.Path) [ "Total"; "Total (hidden)" ] "nothing written"

        testCase "exit --force discards on purpose; exit without it fails unattended"
        <| fun () ->
            use t = project ()
            ok (session [ t.Path ] [ "add measure:Sales/Margin -e 1"; "exit --force"; "this never runs" ])
            let r = session [ t.Path ] [ "add measure:Sales/Margin -e 1"; "exit" ]
            Expect.equal r.Code 1 r.Err
            Expect.equal (measures t.Path) [ "Total"; "Total (hidden)" ] ""

        testCase "undo takes back the last edit, revert all of them"
        <| fun () ->
            use t = project ()

            let r =
                session [ t.Path ] [
                    "add measure:Sales/A -e 1"
                    "add measure:Sales/B -e 2"
                    "undo"
                    "status --json"
                    "revert"
                    "undo"
                    "add measure:Sales/C -e 3"
                    "save"
                ]

            ok r
            let changes = (List.exactlyOne (jsonDocs r.Out)).GetProperty "changes"
            Expect.equal (changes.GetArrayLength()) 1 "only A is left"
            Expect.equal (changes.[0].GetProperty("objectPath").GetString()) "measure:Sales/A" ""
            Expect.stringContains r.Err "Took back the last edit (1 unsaved change left)." ""
            Expect.stringContains r.Err "Discarded 1 unsaved change." ""
            Expect.stringContains r.Err "Nothing to undo." "revert clears the undo stack"
            Expect.equal (measures t.Path) [ "Total"; "Total (hidden)"; "C" ] ""

        testCase "diff shows the staged changes against the model as saved (exit 0 in batch mode)"
        <| fun () ->
            use t = project ()

            let r =
                session [ t.Path ] [ "diff"; "set [Total] -p formatString=0.0"; "diff --stat"; "exit --force" ]

            ok r
            Expect.stringContains r.Out "No changes." "before the edit"
            Expect.stringContains r.Out "~ measure:Sales/Total  1 property\n1 modified" "the --stat form"

        testCase "renames stage as moves; diff against another model inserts the session's"
        <| fun () ->
            use t = project ()
            // Outside the project folder: a .bim next to the PBIP would make the folder ambiguous.
            use elsewhere = new TempDir()
            let other = elsewhere.Combine "other.bim"
            ok (semctl [ "convert"; t.Path; other ])

            let r =
                session [ t.Path ] [
                    "rename [Total] Revenue"
                    "status --json"
                    "diff " + other + " --stat"
                    "revert"
                ]

            Expect.stringContains r.Out "measure:Sales/Total" ""
            Expect.stringContains r.Out "\"changeKind\": \"moved\"" "a rename is a move, not delete + add"
            Expect.stringContains r.Out "measure:Sales/Revenue" ""

        testCase "--dry-run shows an edit without staging it; --save stages it and saves the session"
        <| fun () ->
            use t = project ()

            let r =
                session [ t.Path ] [
                    "add measure:Sales/A -e 1"
                    "add measure:Sales/B -e 2 --dry-run"
                    "status --json"
                    "add measure:Sales/C -e 3 --save"
                    "status"
                ]

            ok r
            Expect.stringContains r.Err "Dry run - nothing saved." ""
            Expect.stringContains r.Out "Nothing staged." "after --save"
            Expect.equal (measures t.Path) [ "Total"; "Total (hidden)"; "A"; "C" ] "--save saves every staged edit"

        testCase "batch mode stops at the first failure with its exit code; --no-batch runs on"
        <| fun () ->
            use t = project ()
            let r = session [ t.Path ] [ "get [Nope]"; "list" ]
            Expect.equal r.Code ExitCode.input r.Err
            Expect.stringContains r.Err "stopped at the failing command (exit 3): get [Nope]" ""
            Expect.isFalse (r.Out.Contains "measure:Sales/Total") "list never ran"

            let r = session [ t.Path; "--no-batch" ] [ "get [Nope]"; "list" ]
            ok r
            Expect.stringContains r.Out "measure:Sales/Total" ""

        testCase "# comments are skipped; --echo writes each command, not the comments"
        <| fun () ->
            use t = project ()
            let r = session [ t.Path; "--echo" ] [ "# a comment"; "  "; "info" ]
            ok r
            Expect.isFalse (r.Out.Contains "a comment") ""
            Expect.stringContains r.Out "> info\n" ""
            Expect.equal (r.Out.Split('\n') |> Array.filter (fun l -> l.Contains "> ")).Length 1 "one line echoed"

        testCase "commands that would take over the input are refused"
        <| fun () ->
            use t = project ()

            for line, says in
                [
                    "mcp", "`sm mcp` owns stdin"
                    "interactive x", "you are in a session already"
                    "shell", "you are in a session already"
                    "set [Total] -p expression=-", "`-` (read from stdin)"
                    "validate --watch", "--watch can't be used"
                    "add measure:Sales/A -e 1 --dry-run --save", "can't be combined"
                ] do
                let r = session [ t.Path ] [ line ]
                Expect.equal r.Code ExitCode.usage (line + r.Err)
                Expect.stringContains r.Err says line

        testCase "subcommands get the model after their own words"
        <| fun () ->
            use t = project ()

            let r =
                session [ t.Path ] [ "bpa rules list"; "translations cultures"; "deps [Total] --referenced-by" ]

            ok r
            Expect.stringContains r.Out "measure:Sales/Total (hidden)" "deps ran on the model"

        testCase "query --data evaluates the staged model"
        <| fun () ->
            use t = project ()
            let data = t.Combine "data"
            write (Path.Combine(data, "Sales.csv")) "Amount,DateKey,Path/With\\Slashes\n2,1,a\n3,1,b\n"
            write (Path.Combine(data, "Date.csv")) "DateKey,Year,Month\n1,2024,Jan\n"
            let q = $"query --data {data} \"EVALUATE ROW(\"\"t\"\", [Total])\" --json"

            let r =
                session [ t.Path ] [ q; "set [Total] -p \"expression=SUM(Sales[Amount]) * 10\""; q; "revert"; q ]

            ok r

            let values = [
                for doc in jsonDocs r.Out -> doc.GetProperty("results").[0].GetProperty("rows").[0].[0].GetDecimal()
            ]

            Expect.equal values [ 5m; 50m; 5m ] "the staged expression, then the saved one again"

        testCase "--json: every command and built-in answers in JSON"
        <| fun () ->
            use t = project ()

            let r =
                session [ t.Path; "--json" ] [ "get [Total]"; "add measure:Sales/A -e 1"; "save" ]

            ok r
            let docs = jsonDocs r.Out
            Expect.equal docs.Length 3 r.Out
            Expect.isTrue (docs.[2].GetProperty("saved").GetBoolean()) ""
            Expect.isTrue (docs.[1].GetProperty("staged").GetBoolean()) ""

        testCase "the save gate: refused while staged edits introduced errors, unless --force"
        <| fun () ->
            use t = project ()

            let r =
                session [ t.Path; "--no-batch" ] [
                    "add measure:Sales/Bad -e \"SUM(Sales[Nope])\""
                    "save"
                    "save --force"
                ]

            ok r
            Expect.stringContains r.Err "introduces 1 error" ""
            Expect.stringContains r.Err "sm: Not saved: the staged edits introduce 1 error:\n  measure:Sales/Bad" ""
            Expect.contains (measures t.Path) "Bad" "saved with --force"

        testCase "the save gate counts the syntax-tree rules' errors (IGNORE outside SUMMARIZECOLUMNS)"
        <| fun () ->
            use t = project ()

            let r =
                session [ t.Path; "--no-batch" ] [
                    "add measure:Sales/Bad -e \"COUNTROWS ( { IGNORE ( 1 ) } )\""
                    "save"
                ]

            Expect.stringContains r.Err "sm: Not saved: the staged edits introduce 1 error:" r.Err
            Expect.stringContains r.Err "ignore-outside-summarizecolumns" r.Err
            Expect.isFalse (measures t.Path |> List.contains "Bad") "not saved"

        testCase "a staged rename stages the report edit; save writes the model, then the report"
        <| fun () ->
            let t, file = withReport ()
            use _ = t
            let before = File.ReadAllText file

            let r =
                session [ t.Path; "--no-batch" ] [
                    "rename Sales[Amount] Net"
                    "rename Sales[Net] \"Net Amount\""
                    "status"
                    "remove \"Sales[Net Amount]\" --force --dry-run"
                    "remove Date[Year]"
                ]

            Expect.equal r.Code 1 "ended with staged edits"
            Expect.equal (File.ReadAllText file) before "nothing written before save"

            Expect.stringContains
                r.Out
                "~ report Sales.Report/definition/pages/p1/visuals/v1/visual.json  (4 references)"
                ""

            Expect.stringContains r.Err "a report uses it" "the check reads the staged report"

            let r = session [ t.Path ] [ "rename Sales[Amount] Net"; "save" ]
            ok r
            Expect.stringContains r.Err "Updated 2 references in 1 file of the report" ""
            Expect.stringContains (File.ReadAllText file) "\"Property\": \"Net\"" ""

        testCase "the removal check sees a staged rename's report edit"
        <| fun () ->
            let t, _ = withReport ()
            use _ = t
            // The visual names Date[Year]'s level; after a staged rename of the level it names the new one.
            let r =
                session [ t.Path; "--no-batch" ] [
                    "rename level:Date/Calendar/Year Yr"
                    "remove Date[Year]"
                    "exit --force"
                ]

            ok r
            Expect.stringContains r.Err "level:Date/Calendar/Yr <- Sales.Report" ""

        testCase "the rest of the commands, inside a session"
        <| fun () ->
            use t = project ()
            let script = t.Combine "add.dax"
            write script "DEFINE MEASURE Sales[Scripted] = 42"

            let r =
                session [ t.Path; "--no-batch" ] [
                    "move \"[Total (hidden)]\" Date"
                    $"script apply {script}"
                    "script export [Scripted]"
                    "validate"
                    "bpa --no-builtin"
                    "lint"
                    "format --expr \"sum(Sales[Amount])\""
                    "format"
                    "status --json"
                    "exit --force"
                ]

            ok r
            Expect.stringContains r.Out "MEASURE 'Sales'[Scripted] = 42" "script export sees the staged script"
            Expect.stringContains r.Out "SUM ( Sales[Amount] )" "format --expr formats the DAX it's given"
            Expect.isFalse (r.Err.Contains "doesn't take a file or model") "no model is put before --expr"

            let changes = (List.last (jsonDocs r.Out)).GetProperty "changes"
            let refs = [ for c in changes.EnumerateArray() -> c.GetProperty("objectPath").GetString() ]
            Expect.contains refs "measure:Date/Total (hidden)" "move staged"
            Expect.contains refs "measure:Sales/Scripted" "script apply staged"
            Expect.equal (measures t.Path) [ "Total"; "Total (hidden)" ] "nothing written"

        testCase "--json: the session's own errors are JSON too"
        <| fun () ->
            use t = project ()

            let r =
                session [ t.Path; "--json"; "--no-batch" ] [
                    "add measure:Sales/A -e 1"
                    "mcp"
                    "get \"oops"
                    "diff --nope"
                ]

            Expect.equal r.Code 1 r.Out
            let docs = jsonDocs r.Out
            let codes = [ for d in docs.Tail -> d.GetProperty("error").GetProperty("code").GetString() ]
            Expect.equal codes [ "usage"; "usage"; "usage"; "unsaved-changes" ] r.Out
            let last = List.last docs
            Expect.equal (last.GetProperty("error").GetProperty("changes").[0].GetString()) "measure:Sales/A" ""

        testCase "a model that doesn't loadModel fails before the session starts"
        <| fun () ->
            let r = session [ "/nonexistent/model" ] [ "list" ]
            Expect.equal r.Code ExitCode.input r.Err

        testCase "--batch and --no-batch can't be combined"
        <| fun () ->
            use t = project ()
            Expect.equal (session [ t.Path; "--batch"; "--no-batch" ] []).Code ExitCode.usage ""
    ]

/// A scripted terminal: these lines, then the end of input; answers to questions in order.
type private Terminal(lines: string list, answers: bool list) =
    let mutable lines = lines
    let mutable answers = answers
    member val Questions = ResizeArray<string>()

    interface InteractiveCommand.LineSource with
        member _.Read _ _ =
            match lines with
            | l :: rest ->
                lines <- rest
                LineEditor.Read.Line l
            | [] -> LineEditor.Read.EndOfInput

        member _.IsTerminal = true

        member this.Confirm q =
            this.Questions.Add q

            match answers with
            | a :: rest ->
                answers <- rest
                a
            | [] -> false

[<Tests>]
let terminalTests =
    let run (path: string) (terminal: Terminal) =
        use out = new StringWriter()
        use err = new StringWriter()

        let ctx: Ctx = {
            In = TextReader.Null
            Out = out
            Err = err
            Json = false
            GetEnv = fun _ -> None
            Connector = XmlaFakes.NoConnections()
        }

        let host: InteractiveCommand.Host = {
            Dispatch = fun argv -> App.runWithConnector (XmlaFakes.NoConnections()) (fun _ -> None) out err argv
            InsertModel = fun words model -> (List.head words) :: model :: List.tail words
        }

        let options: InteractiveCommand.Options = {
            Echo = false
            Batch = true
            Banner = true
            HistoryFile = None
        }

        match ModelIO.loadPath path with
        | Error e -> failtest (SemctlError.message e)
        | Ok(source, db) ->
            let code = InteractiveCommand.runSession ctx host options path source db terminal
            code, out.ToString(), err.ToString()

    testList "sm interactive at a terminal" [
        testCase "exit with staged edits asks (no by default) and keeps the session going"
        <| fun () ->
            use t = project ()

            let terminal =
                Terminal([ "add measure:Sales/A -e 1"; "exit"; "get [Nope]"; "exit" ], [ false; true ])

            let code, out, err = run t.Path terminal
            Expect.equal code 0 err

            Expect.equal
                (List.ofSeq terminal.Questions)
                [ "Discard 1 unsaved change and exit?"; "Discard 1 unsaved change and exit?" ]
                ""

            Expect.stringContains err "No object matches '[Nope]'" "a failure doesn't end a terminal session"
            Expect.stringContains out "interactive:" "the banner"
            Expect.equal (measures t.Path) [ "Total"; "Total (hidden)" ] "discarded, not saved"

        testCase "the end of input (Ctrl+D) asks too; nothing staged leaves at once"
        <| fun () ->
            use t = project ()
            let terminal = Terminal([ "add measure:Sales/A -e 1"; "save" ], [])
            let code, _, err = run t.Path terminal
            Expect.equal code 0 err
            Expect.isEmpty terminal.Questions ""
            Expect.contains (measures t.Path) "A" ""
    ]
