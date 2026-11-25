/// `rename` and `move` keep the PBIR report of a PBIP project in step.
module Semctl.Tests.ReportCommandTests

open System.IO
open System.Text.Json
open Expecto
open Semctl
open Semctl.Tests.Fixtures
open Semctl.Tests.CliHarness
open Semctl.Cli.Common


/// The sample PBIP project with a PBIR report holding one visual (ReportTests.visual).
let private withReport () =
    let t = project ()

    let file =
        t.Combine("Sales.Report", "definition", "pages", "p1", "visuals", "v1", "visual.json")

    write file ReportTests.visual
    write (t.Combine("Sales.Report", "definition", "version.json")) """{ "version": "2.0.0" }"""
    t, file

let private visualPath = "Sales.Report/definition/pages/p1/visuals/v1/visual.json"

[<Tests>]
let tests =
    testList "CLI report references" [
        testCase "a dry run shows the report edits and writes nothing"
        <| fun () ->
            let t, file = withReport ()
            use _ = t
            let r = semctl [ "rename"; t.Path; "[Total]"; "Total Sales" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Out $"~ report {visualPath}  (3 references)" ""
            let lines = r.Out.Split '\n'

            let has (prefix: string) (text: string) =
                lines |> Array.exists (fun l -> l.StartsWith prefix && l.Contains text)

            Expect.isTrue (has "      - " "\"Property\": \"Total\" }") "the old line"
            Expect.isTrue (has "      + " "\"Property\": \"Total Sales\" }") "the new line"
            Expect.equal (File.ReadAllText file) ReportTests.visual "nothing written"

        testCase "--save writes the model, then the report"
        <| fun () ->
            let t, file = withReport ()
            use _ = t
            let r = semctl [ "rename"; t.Path; "Sales[Amount]"; "Net Amount"; "--save" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Err "Updated 2 references in 1 file of the report" ""
            let text = File.ReadAllText file
            Expect.stringContains text "\"Property\": \"Net Amount\"" ""
            Expect.stringContains text "\"queryRef\": \"Sales.Amount\"" "queryRef kept"
            JsonDocument.Parse(text).Dispose()
            Expect.isTrue ((loadModel t.Path).Model.Tables.["Sales"].Columns.Contains "Net Amount") ""

        testCase "--json lists the files and their changed lines"
        <| fun () ->
            let t, file = withReport ()
            use _ = t
            let r = semctl [ "rename"; t.Path; "Date"; "Calendar Date"; "--json" ]
            Expect.equal r.Code ExitCode.ok r.Err
            let doc = json r
            let files = doc.GetProperty "files"
            Expect.equal (files.GetArrayLength()) 1 ""
            let f = files.[0]
            Expect.equal (f.GetProperty("path").GetString()) file ""
            Expect.equal (f.GetProperty("display").GetString()) visualPath ""
            Expect.equal (f.GetProperty("references").GetInt32()) 2 "Date[Amount] and the hierarchy"
            Expect.isFalse (f.GetProperty("written").GetBoolean()) "a dry run"
            let line = f.GetProperty("lines").[0]
            Expect.stringContains (line.GetProperty("before").GetString()) "\"Entity\": \"Date\"" ""
            Expect.stringContains (line.GetProperty("after").GetString()) "\"Entity\": \"Calendar Date\"" ""

            let saved = semctl [ "rename"; t.Path; "Date"; "Calendar Date"; "--save"; "--json" ]
            let doc = json saved
            Expect.isTrue (doc.GetProperty("files").[0].GetProperty("written").GetBoolean()) ""
            Expect.stringContains (File.ReadAllText file) "\"Entity\": \"Calendar Date\"" ""

        testCase "--stat and --name-only list the files"
        <| fun () ->
            let t, _ = withReport ()
            use _ = t
            let stat = semctl [ "rename"; t.Path; "hierarchy:Date/Calendar"; "Cal"; "--stat" ]
            Expect.stringContains stat.Out $"~ report {visualPath}  1 reference" ""

            let names =
                semctl [ "rename"; t.Path; "level:Date/Calendar/Year"; "Y"; "--name-only" ]

            Expect.stringContains names.Out visualPath ""

        testCase "--save-to leaves the report alone, with a warning"
        <| fun () ->
            let t, file = withReport ()
            use _ = t
            use o = new TempDir()
            let r = semctl [ "rename"; t.Path; "[Total]"; "T"; "--save-to"; o.Combine "x.bim" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Err "report files are not updated: --save-to" ""
            Expect.equal (File.ReadAllText file) ReportTests.visual ""

        testCase "a report that changed since it was read is not overwritten"
        <| fun () ->
            // Simulated by planning against one text and writing over another.
            let t, file = withReport ()
            use _ = t

            let edits, _ =
                Report.plan [ t.Combine "Sales.Report" ] (Report.RenameMeasure("Sales", "Total", "T"))

            File.WriteAllText(file, ReportTests.visual + " ")
            Expect.isError (Report.write edits) ""
            Expect.equal (File.ReadAllText file) (ReportTests.visual + " ") ""

        testCase "a conflicting report prevents the model save and a late failure restores it"
        <| fun () ->
            let t, file = withReport ()
            use _ = t

            let source, db =
                ModelIO.loadPath t.Path
                |> function
                    | Ok pair -> pair
                    | Error e -> failtest (SemctlError.message e)

            db.Model.Tables.["Sales"].Measures.["Total"].Name <- "Changed"

            let edits, _ =
                Report.plan [ t.Combine "Sales.Report" ] (Report.RenameMeasure("Sales", "Total", "Changed"))

            let checkOriginalModel () =
                Expect.isTrue ((loadModel t.Path).Model.Tables.["Sales"].Measures.Contains "Total") ""

            File.AppendAllText(file, " ")
            Expect.isError (Report.saveBack source db edits) "report conflict"
            checkOriginalModel ()
            File.WriteAllText(file, ReportTests.visual)
            // The second check now encounters the first write to the same destination.
            Expect.isError (Report.saveBack source db (edits @ edits)) "failure after model and report writes"
            checkOriginalModel ()
            Expect.equal (File.ReadAllText file) ReportTests.visual "restored report"
            Expect.isOk (Report.saveBack source db edits) "retry"

        testCase "saveBack succeeds while another reader holds model and report files open with FileShare.ReadWrite"
        <| fun () ->
            let t, file = withReport ()
            use _ = t

            let source, db =
                ModelIO.loadPath t.Path
                |> function
                    | Ok pair -> pair
                    | Error e -> failtest (SemctlError.message e)

            let measureFile = Path.Combine(source.Path, "tables", "Sales.tmdl")

            use _modelReader =
                new FileStream(measureFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)

            use _reportReader =
                new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)

            db.Model.Tables.["Sales"].Measures.["Total"].Name <- "Changed"

            let edits, _ =
                Report.plan [ t.Combine "Sales.Report" ] (Report.RenameMeasure("Sales", "Total", "Changed"))

            Expect.isOk (Report.saveBack source db edits) "cooperative readers must not block saving"
            Expect.isTrue ((loadModel t.Path).Model.Tables.["Sales"].Measures.Contains "Changed") ""
            Expect.stringContains (File.ReadAllText file) "\"Property\": \"Changed\"" ""

        testCase "rolling back a failed save restores deleted model files and removes newly created ones"
        <| fun () ->
            let t, file = withReport ()
            use _ = t

            let source, db =
                ModelIO.loadPath t.Path
                |> function
                    | Ok pair -> pair
                    | Error e -> failtest (SemctlError.message e)

            let oldTable = Path.Combine(source.Path, "tables", "Sales.tmdl")
            let newTable = Path.Combine(source.Path, "tables", "Orders.tmdl")
            let originalSalesBytes = File.ReadAllBytes oldTable

            db.Model.Tables.["Sales"].Name <- "Orders"

            let edits, _ =
                Report.plan [ t.Combine "Sales.Report" ] (Report.RenameTable("Sales", "Orders"))

            Expect.isError (Report.saveBack source db (edits @ edits)) "failure after model and report writes"
            Expect.isTrue (File.Exists oldTable) "deleted table file restored"
            Expect.equal (File.ReadAllBytes oldTable) originalSalesBytes "restored table file bytes"
            Expect.isFalse (File.Exists newTable) "created table file removed"
            Expect.equal (File.ReadAllText file) ReportTests.visual "restored report"

            Expect.isOk (Report.saveBack source db edits) "retry after rollback"
            Expect.isFalse (File.Exists oldTable) "old table file deleted on retry"
            Expect.isTrue (File.Exists newTable) "new table file written on retry"
            Expect.stringContains (File.ReadAllText file) "\"Entity\": \"Orders\"" ""

        testCase "move rewrites the measure's own entity and warns about shared aliases"
        <| fun () ->
            let t, file = withReport ()
            use _ = t
            let r = semctl [ "move"; t.Path; "[Total]"; "Date"; "--save" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Err "through a shared source alias are not updated" ""
            Expect.stringContains r.Err visualPath ""
            let text = File.ReadAllText file
            Expect.stringContains text "\"SourceRef\": { \"Entity\": \"Date\" } }, \"Property\": \"Total\"" ""

        testCase "kinds reports don't name: no report edits"
        <| fun () ->
            let t, _ = withReport ()
            use _ = t
            let r = semctl [ "rename"; t.Path; "role:Readers"; "Viewers"; "--json" ]
            Expect.equal r.Code ExitCode.ok r.Err
            let doc = json r
            Expect.equal (doc.GetProperty("files").GetArrayLength()) 0 ""

        testCase "a report pointing at another model is not touched"
        <| fun () ->
            let t, file = withReport ()
            use _ = t
            write (t.Combine("Sales.Report", "definition.pbir")) (pbirByPath "../Other.SemanticModel")

            let r =
                semctl [ "rename"; t.Combine "Sales.SemanticModel"; "[Total]"; "T"; "--json" ]

            Expect.equal r.Code ExitCode.ok r.Err
            let doc = json r
            Expect.equal (doc.GetProperty("files").GetArrayLength()) 0 ""
            Expect.equal (File.ReadAllText file) ReportTests.visual ""

        testCase "PBIR-Legacy report.json and report-level measures are reported, not edited"
        <| fun () ->
            use t = project ()
            let legacy = t.Combine("Sales.Report", "report.json")
            write legacy """{ "config": "{\"Property\":\"Total\"}" }"""
            let r = semctl [ "rename"; t.Path; "[Total]"; "T"; "--save" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Err "Sales.Report/report.json (PBIR-Legacy format" ""
            Expect.stringContains (File.ReadAllText legacy) "Total" "untouched"

            let t2, _ = withReport ()
            use _ = t2
            let ext = t2.Combine("Sales.Report", "definition", "reportExtensions.json")

            write
                ext
                """{ "entities": [ { "name": "Sales", "measures": [ { "name": "R", "expression": "[Total] * 2" } ] } ] }"""

            let r = semctl [ "rename"; t2.Path; "[Total]"; "T" ]

            Expect.stringContains
                r.Err
                "report-level measures in Sales.Report/definition/reportExtensions.json mention 'Total'"
                ""
    ]
