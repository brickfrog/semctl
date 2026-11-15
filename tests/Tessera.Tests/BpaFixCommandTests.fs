module Tessera.Tests.BpaFixCommandTests

open System.Text.Json
open Expecto
open Microsoft.AnalysisServices.Tabular
open Tessera
open Tessera.Tests.Fixtures
open Tessera.Tests.CliHarness
open Tessera.Cli.Common

/// The sample model as model.bim in `t`, with a rules file of these (id, scope, expression, fix).
let private setup (t: TempDir) (rules: (string * string * string * string) list) =
    let path = t.Combine "model.bim"

    match ModelIO.save ModelIO.BimFile ModelIO.SaveOptions.defaults false (sampleDatabase ()) path with
    | Ok _ -> ()
    | Error e -> failtest (TessError.message e)

    let json =
        BpaTests.ruleJson [
            for id, scope, expression, fix in rules ->
                [
                    "ID", box id
                    "Name", box $"Rule {id}"
                    "Severity", box 2
                    "Category", box "Test"
                    "Scope", box scope
                    "Expression", box expression
                    if fix <> "" then
                        "FixExpression", box fix
                ]
        ]

    let rulesPath = t.Combine "rules.json"
    write rulesPath json
    path, rulesPath

let private load (path: string) =
    match ModelIO.loadPath path with
    | Ok(_, db) -> db
    | Error e -> failtest (TessError.message e)

let private lines (s: string) =
    s.Replace("\r\n", "\n").Split('\n', System.StringSplitOptions.RemoveEmptyEntries)

let private standard = [
    // LAYOUT_HIDE_FK_COLUMNS's fix (BPARules-standard.json), on a narrower expression.
    "HIDE", "DataColumn", "Name = \"DateKey\" and Table.Name = \"Date\" and not IsHidden", "IsHidden = true"
    // PERCENTAGE_FORMATTING's (microsoft/Analysis-Services BPARules.json).
    "PCT", "Measure", "Name = \"Total\"", "FormatString = \"#,0.0%\\u003B-#,0.0%\\u003B#,0.0%\""
    // PERF_UNUSED_MEASURES's.
    "UNUSED", "Measure", "Name = \"Total (hidden)\"", "Delete()"
    // AVOID_INVALID_NAME_CHARACTERS's: refused.
    "CHARS",
    "Measure",
    "Name = \"Total\"",
    "Name = string.Concat( it.Name.ToCharArray().Select( c => (char.IsControl(c) && !char.IsWhiteSpace(c)) ? ' ': c ))"
    "NOFIX", "Table", "Name = \"Sales\"", ""
]

[<Tests>]
let tests =
    testList "CLI bpa --fix" [
        testCase "a dry run by default: the diff, what was fixed, what wasn't"
        <| fun () ->
            use t = new TempDir()
            let path, rules = setup t standard
            let r = tess [ "bpa"; path; "--no-builtin"; "--rules"; rules; "--fix" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Out "~ column:Date/DateKey" ""
            Expect.stringContains r.Out "isHidden: (none) -> \"true\"" ""
            Expect.stringContains r.Out "formatString: \"#,0\" -> \"#,0.0%;-#,0.0%;#,0.0%\"" ""
            Expect.stringContains r.Out "- measure:Sales/Total (hidden)" ""
            Expect.stringContains r.Err "Fixed 3 findings (HIDE: 1, PCT: 1, UNUSED: 1)." ""
            Expect.stringContains r.Err "tess: warning: CHARS: not fixed on measure:Sales/Total: only literal values" ""
            Expect.stringContains r.Err "1 finding of rules without a fix expression" ""
            Expect.stringContains r.Err "Dry run - nothing saved." ""
            Expect.isFalse (load path).Model.Tables.["Date"].Columns.["DateKey"].IsHidden "nothing written"

        testCase "--save writes; a second run has nothing left to fix"
        <| fun () ->
            use t = new TempDir()
            let path, rules = setup t standard
            let r = tess [ "bpa"; path; "--no-builtin"; "--rules"; rules; "--fix"; "--save" ]
            Expect.equal r.Code ExitCode.ok r.Err
            let db = load path
            Expect.isTrue db.Model.Tables.["Date"].Columns.["DateKey"].IsHidden ""
            Expect.equal db.Model.Tables.["Sales"].Measures.["Total"].FormatString "#,0.0%;-#,0.0%;#,0.0%" ""
            Expect.isFalse (db.Model.Tables.["Sales"].Measures.ContainsName "Total (hidden)") ""

            let again =
                tess [ "bpa"; path; "--no-builtin"; "--rules"; rules; "--fix"; "--save" ]

            Expect.equal (lines again.Out) [| "No changes." |] again.Err
            Expect.stringContains again.Err "CHARS: not fixed" "the refused one is still there"

        testCase "--stat, --name-only, --json"
        <| fun () ->
            use t = new TempDir()
            let path, rules = setup t standard

            let names =
                tess [ "bpa"; path; "--no-builtin"; "--rules"; rules; "--fix"; "--name-only" ]

            Expect.equal
                (lines names.Out |> Set.ofArray)
                (set [ "column:Date/DateKey"; "measure:Sales/Total"; "measure:Sales/Total (hidden)" ])
                ""

            let stat = tess [ "bpa"; path; "--no-builtin"; "--rules"; rules; "--fix"; "--stat" ]
            Expect.stringContains stat.Out "~ column:Date/DateKey" ""

            let r = tess [ "bpa"; path; "--no-builtin"; "--rules"; rules; "--fix"; "--json" ]
            Expect.equal r.Code ExitCode.ok r.Err
            use doc = json r
            let root = doc.RootElement
            Expect.equal (root.GetProperty("command").GetString()) "bpa" ""
            Expect.isTrue (root.GetProperty("dryRun").GetBoolean()) ""
            Expect.equal (root.GetProperty("summary").GetProperty("modified").GetInt32()) 2 ""
            Expect.equal (root.GetProperty("summary").GetProperty("deleted").GetInt32()) 1 ""

            let warnings = [ for w in root.GetProperty("warnings").EnumerateArray() -> w.GetString() ]
            Expect.equal warnings.Length 2 (sprintf "%A" warnings)

        testCase "--path, --category and --min-severity choose what is fixed"
        <| fun () ->
            use t = new TempDir()
            let path, rules = setup t standard

            let r =
                tess [
                    "bpa"
                    path
                    "--no-builtin"
                    "--rules"
                    rules
                    "--fix"
                    "--path"
                    "measure:"
                    "--name-only"
                ]

            Expect.equal (lines r.Out |> Set.ofArray) (set [ "measure:Sales/Total"; "measure:Sales/Total (hidden)" ]) ""

            let none =
                tess [
                    "bpa"
                    path
                    "--no-builtin"
                    "--rules"
                    rules
                    "--fix"
                    "--min-severity"
                    "3"
                ]

            Expect.equal (lines none.Out) [| "No changes." |] ""
            Expect.stringContains none.Err "Nothing to fix." ""

            let other =
                tess [
                    "bpa"
                    path
                    "--no-builtin"
                    "--rules"
                    rules
                    "--fix"
                    "--category"
                    "Other"
                ]

            Expect.equal (lines other.Out) [| "No changes." |] ""

        testCase "findings ignored by annotation aren't fixed"
        <| fun () ->
            use t = new TempDir()
            let path, rules = setup t standard

            let ignore =
                tess [ "bpa"; "rules"; "ignore"; path; "PCT"; "measure:Sales/Total"; "--save" ]

            Expect.equal ignore.Code ExitCode.ok ignore.Err

            let r =
                tess [ "bpa"; path; "--no-builtin"; "--rules"; rules; "--fix"; "--name-only" ]

            Expect.isFalse (lines r.Out |> Array.contains "measure:Sales/Total") r.Out

        testCase "report switches don't combine with --fix; edit switches need it"
        <| fun () ->
            use t = new TempDir()
            let path, rules = setup t standard

            for args in
                [
                    [ "--fix"; "--fail-on"; "warning" ]
                    [ "--fix"; "--ci"; "github" ]
                    [ "--fix"; "--trx"; t.Combine "x.trx" ]
                    [ "--fix"; "--sarif"; t.Combine "x.sarif" ]
                    [ "--fix"; "--list-rules" ]
                    [ "--save" ]
                    [ "--stat" ]
                    [ "--force" ]
                    [ "--save-to"; t.Combine "out.bim" ]
                    [ "--fix"; "--save"; "--save-to"; t.Combine "out.bim" ]
                    [ "--fix"; "--save"; "--watch" ]
                ] do
                let r = tess ([ "bpa"; path; "--rules"; rules ] @ args)
                Expect.equal r.Code ExitCode.usage (String.concat " " args + ": " + r.Err)

        testCase "--save-to writes a fixed copy and leaves the model alone"
        <| fun () ->
            use t = new TempDir()
            let path, rules = setup t standard
            let out = t.Combine "fixed.bim"

            let r =
                tess [ "bpa"; path; "--no-builtin"; "--rules"; rules; "--fix"; "--save-to"; out ]

            Expect.equal r.Code ExitCode.ok r.Err
            Expect.isTrue (load out).Model.Tables.["Date"].Columns.["DateKey"].IsHidden ""
            Expect.isFalse (load path).Model.Tables.["Date"].Columns.["DateKey"].IsHidden ""
    ]
