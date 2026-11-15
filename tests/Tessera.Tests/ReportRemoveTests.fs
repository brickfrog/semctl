/// Report-aware removal: `remove`, `bpa --fix` Delete() and `mcp apply_changes` remove refuse to
/// delete what a PBIR report next to the model uses (unless forced), and `mcp apply_changes` rename
/// rewrites the report as `tess rename` does.
module Tessera.Tests.ReportRemoveTests

open System.IO
open System.Text.Json
open Expecto
open Tessera
open Tessera.Tests.Fixtures
open Tessera.Tests.CliHarness
open Tessera.Cli.Common

/// The sample PBIP project with one visual (ReportTests.visual) on a page called "Overview".
let private withReport () =
    let t = project ()

    let file =
        t.Combine("Sales.Report", "definition", "pages", "p1", "visuals", "v1", "visual.json")

    write file ReportTests.visual
    write (t.Combine("Sales.Report", "definition", "pages", "p1", "page.json")) """{ "displayName": "Overview" }"""
    write (t.Combine("Sales.Report", "definition", "version.json")) """{ "version": "2.0.0" }"""
    t, file

let private load (path: string) =
    match ModelIO.loadPath path with
    | Ok(_, db) -> db
    | Error e -> failtestf "%s" (TessError.message e)

let private errorOf (r: Run) =
    use doc = JsonDocument.Parse r.Out
    doc.RootElement.GetProperty("error").Clone()

/// Run `tess mcp <args>` over these JSON-RPC lines; the tools/call results by id.
let private mcp (args: string list) (calls: (int * string * obj) list) =
    let request (id: int) (name: string) (arguments: obj) =
        JsonSerializer.Serialize(
            dict [
                "jsonrpc", box "2.0"
                "id", box id
                "method", box "tools/call"
                "params", box (dict [ "name", box name; "arguments", arguments ])
            ]
        )

    let lines =
        [
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}"""
            """{"jsonrpc":"2.0","method":"notifications/initialized"}"""
        ]
        @ [ for id, name, arguments in calls -> request id name arguments ]

    use input = new StringReader(String.concat "\n" lines + "\n")
    use out = new StringWriter()
    use err = new StringWriter()

    let code =
        Tessera.Cli.App.runWithInput
            (Some input)
            (XmlaFakes.NoConnections())
            (fun _ -> None)
            out
            err
            (Array.ofList ("mcp" :: args))

    Expect.equal code 0 (err.ToString())

    let results =
        out.ToString().Split('\n')
        |> Array.filter (fun l -> l <> "")
        |> Array.map (fun l -> JsonDocument.Parse(l).RootElement.Clone())

    fun (id: int) ->
        let frame =
            results
            |> Array.find (fun f ->
                match f.TryGetProperty "id" with
                | true, v -> v.GetInt32() = id
                | _ -> false)

        let result = frame.GetProperty "result"
        result.GetProperty "structuredContent", result.GetProperty("isError").GetBoolean()

let private ops (operations: obj list) =
    box (dict [ "operations", box operations ])

[<Tests>]
let tests =
    testList "report-aware removal" [
        testCase "remove refuses a column a visual uses (through its hierarchy level), naming the visual"
        <| fun () ->
            let t, _ = withReport ()
            use _ = t
            let r = tess [ "remove"; t.Path; "Date[Year]"; "--save" ]
            Expect.equal r.Code ExitCode.input r.Out
            Expect.stringContains r.Err "Can't remove column:Date/Year: a report uses it:" ""

            Expect.stringContains
                r.Err
                "level:Date/Calendar/Year <- Sales.Report: page 'Overview', visual v1 (tableEx)  (1 reference)"
                ""

            Expect.stringContains r.Err "--force" ""
            Expect.isTrue ((load t.Path).Model.Tables.["Date"].Columns.ContainsName "Year") "nothing removed"

        testCase "schemacheck --drop-removed-columns keeps a column a visual uses, with a warning"
        <| fun () ->
            let t, _ = withReport ()
            use _ = t
            // Date's source returns every data column but Year, which the visual uses.
            let date = (load t.Path).Model.Tables.["Date"]

            let columns = [
                for c in date.Columns do
                    match c with
                    | :? Microsoft.AnalysisServices.Tabular.DataColumn as d when d.SourceColumn <> "Year" ->
                        $"\"{d.SourceColumn}\""
                    | _ -> ()
            ]

            let schema = t.Combine "schema.json"
            write schema ("""{ "tables": { "Date": [ """ + String.concat ", " columns + " ] } }")

            let r =
                tess [
                    "schemacheck"
                    t.Path
                    "Date"
                    "--source"
                    schema
                    "--update"
                    "--drop-removed-columns"
                    "--save"
                ]

            Expect.equal r.Code ExitCode.ok (r.Out + r.Err)
            Expect.stringContains r.Err "was kept: a report uses it" ""
            Expect.stringContains r.Err "visual v1" ""
            Expect.isTrue ((load t.Path).Model.Tables.["Date"].Columns.ContainsName "Year") "kept"

        testCase "--json lists the usages; --force removes it anyway"
        <| fun () ->
            let t, _ = withReport ()
            use _ = t
            let r = tess [ "remove"; t.Path; "Date[Year]"; "--json" ]
            Expect.equal r.Code ExitCode.input r.Out
            let e = errorOf r
            Expect.equal (e.GetProperty("code").GetString()) "used-by-report" ""
            Expect.equal (e.GetProperty("usages").GetArrayLength()) 1 ""

            let forced = tess [ "remove"; t.Path; "Date[Year]"; "--force"; "--save" ]
            Expect.equal forced.Code ExitCode.ok forced.Err
            Expect.isFalse ((load t.Path).Model.Tables.["Date"].Columns.ContainsName "Year") "removed"

        testCase "what no report uses is removed as before; --save-to isn't checked"
        <| fun () ->
            let t, _ = withReport ()
            use _ = t
            let r = tess [ "remove"; t.Path; "Date[Month]"; "--save" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.isFalse ((load t.Path).Model.Tables.["Date"].Columns.ContainsName "Month") ""

            let copy = t.Combine "copy.bim"
            let r = tess [ "remove"; t.Path; "Date[Year]"; "--save-to"; copy ]
            Expect.equal r.Code ExitCode.ok r.Err

        testCase "a measure with --cascade: the report's use of it is found after the cascade"
        <| fun () ->
            let t, _ = withReport ()
            use _ = t
            let r = tess [ "remove"; t.Path; "[Total]"; "--cascade" ]
            Expect.equal r.Code ExitCode.input r.Out
            Expect.stringContains r.Err "measure:Sales/Total <- Sales.Report: page 'Overview', visual v1" ""

        testCase "a whole table: one usage line per file, for the table"
        <| fun () ->
            let t, _ = withReport ()
            use _ = t
            let r = tess [ "remove"; t.Path; "Sales"; "--cascade"; "--json" ]
            Expect.equal r.Code ExitCode.input r.Out
            let usages = (errorOf r).GetProperty "usages"
            Expect.equal (usages.GetArrayLength()) 1 (usages.GetRawText())
            Expect.stringStarts (usages.[0].GetString()) "table:Sales <- Sales.Report" ""

        testCase "report-level measures and PBIR-Legacy files that mention it block too"
        <| fun () ->
            let t = project ()
            use _ = t
            write (t.Combine("Sales.Report", "report.json")) """{ "sections": [ "Year" ] }"""
            let r = tess [ "remove"; t.Path; "Date[Year]" ]
            Expect.equal r.Code ExitCode.input r.Out
            Expect.stringContains r.Err "Sales.Report: Sales.Report/report.json  (mentions it by name)" ""

        testCase "bpa --fix refuses a Delete() of what the report uses, unless --force"
        <| fun () ->
            let t, _ = withReport ()
            use _ = t

            let rules = t.Combine "rules.json"

            write
                rules
                (BpaTests.ruleJson [
                    [
                        "ID", box "DEL"
                        "Name", box "Rule DEL"
                        "Severity", box 2
                        "Category", box "Test"
                        "Scope", box "DataColumn"
                        "Expression", box "Name = \"Year\" and Table.Name = \"Date\""
                        "FixExpression", box "Delete()"
                    ]
                ])

            let r = tess [ "bpa"; t.Path; "--no-builtin"; "--rules"; rules; "--fix"; "--save" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Err "DEL: not fixed on column:Date/Year: a report uses it (1 report file;" ""
            Expect.isTrue ((load t.Path).Model.Tables.["Date"].Columns.ContainsName "Year") "kept"

            let forced =
                tess [
                    "bpa"
                    t.Path
                    "--no-builtin"
                    "--rules"
                    rules
                    "--fix"
                    "--save"
                    "--force"
                ]

            Expect.equal forced.Code ExitCode.ok forced.Err
            Expect.isFalse ((load t.Path).Model.Tables.["Date"].Columns.ContainsName "Year") "removed"

        testCase "mcp remove: refused with the usages, unless force"
        <| fun () ->
            let t, _ = withReport ()
            use _ = t

            let remove (force: bool) =
                box (dict [ "op", box "remove"; "objects", box [ "Date[Year]" ]; "force", box force ])

            let result =
                mcp [ t.Path; "--allow"; "metadata-write" ] [
                    1, "apply_changes", ops [ remove false ]
                    2, "apply_changes", ops [ remove true ]
                ]

            let refused, isError = result 1
            Expect.isTrue isError (refused.GetRawText())
            Expect.equal (refused.GetProperty("error").GetProperty("code").GetString()) "used-by-report" ""
            let forced, isError = result 2
            Expect.isFalse isError (forced.GetRawText())

        testCase "mcp rename rewrites the report: staged with the batch, written by save_model"
        <| fun () ->
            let t, file = withReport ()
            use _ = t

            let rename (o: string) (n: string) =
                box (dict [ "op", box "rename"; "object", box o; "newName", box n ])

            let before = File.ReadAllText file
            let mutable afterBatch = ""

            let result =
                mcp [ t.Path; "--allow"; "metadata-write"; "--save" ] [
                    // Two renames of the same field in one batch compose.
                    1, "apply_changes", ops [ rename "Sales[Amount]" "Net"; rename "Sales[Net]" "Net Amount" ]
                    2, "save_model", box (dict<string, obj> [])
                ]

            let applied, isError = result 1
            Expect.isFalse isError (applied.GetRawText())
            let files = applied.GetProperty "reportFiles"
            Expect.equal (files.GetArrayLength()) 1 ""
            Expect.equal (files.[0].GetProperty("references").GetInt32()) 4 "2 + 2"
            Expect.isFalse (applied.GetRawText().Contains "not updated by tess mcp") ""
            let saved, isError = result 2
            Expect.isFalse isError (saved.GetRawText())
            Expect.equal (saved.GetProperty("reportFiles").GetArrayLength()) 1 ""
            afterBatch <- File.ReadAllText file
            Expect.notEqual afterBatch before ""
            Expect.stringContains afterBatch "\"Property\": \"Net Amount\"" ""
            Expect.isFalse (afterBatch.Contains "\"Property\": \"Net\"") ""
            Expect.isTrue ((load t.Path).Model.Tables.["Sales"].Columns.ContainsName "Net Amount") ""

        testCase "mcp: a failed batch drops its report edits; without save_model nothing is written"
        <| fun () ->
            let t, file = withReport ()
            use _ = t
            let before = File.ReadAllText file

            let result =
                mcp [ t.Path; "--allow"; "metadata-write" ] [
                    1,
                    "apply_changes",
                    ops [
                        box (dict [ "op", box "rename"; "object", box "Sales[Amount]"; "newName", box "Net" ])
                        box (dict [ "op", box "rename"; "object", box "Nope"; "newName", box "X" ])
                    ]
                    2, "model_overview", box (dict<string, obj> [])
                ]

            let _, isError = result 1
            Expect.isTrue isError ""
            let overview, _ = result 2
            Expect.equal (overview.GetProperty("unsavedChanges").GetInt32()) 0 ""
            Expect.equal (File.ReadAllText file) before "nothing written"
    ]
