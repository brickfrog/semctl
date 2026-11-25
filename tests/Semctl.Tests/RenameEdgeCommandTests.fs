module Semctl.Tests.RenameEdgeCommandTests

open Expecto
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.Tests.Fixtures
open Semctl.Tests.CliHarness
open Semctl.Cli.Common


/// RenameEdgeTests' model, as a TMDL folder.
let private folder () =
    let t = new TempDir()
    let dir = t.Combine "tmdl"

    ModelIO.save ModelIO.TmdlFolder ModelIO.SaveOptions.defaults false (RenameEdgeTests.model ()) dir
    |> ignore

    t, dir

[<Tests>]
let tests =
    testList "CLI rename edge cases" [
        testCase "a calculated-table sourceColumn shows in the change, and is saved"
        <| fun () ->
            let t, dir = folder ()
            use _ = t
            let r = semctl [ "rename"; dir; "Sales[Amount]"; "Net" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Out "~ column:Big Sales/Amount" ""
            Expect.stringContains r.Out "sourceColumn: \"Sales[Amount]\" -> \"Sales[Net]\"" ""
            Expect.stringContains r.Err "and 1 calculated-table sourceColumn" ""

            let saved = semctl [ "rename"; dir; "Sales[Amount]"; "Net"; "--save"; "--json" ]
            Expect.equal saved.Code ExitCode.ok saved.Err
            let doc = json saved

            let change =
                doc.GetProperty("changes").EnumerateArray()
                |> Seq.find (fun c -> c.GetProperty("objectPath").GetString() = "column:Big Sales/Amount")

            Expect.equal (change.GetProperty("properties").[0].GetProperty("property").GetString()) "sourceColumn" ""

            let c =
                (loadModel dir).Model.Tables.["Big Sales"].Columns.["Amount"] :?> CalculatedTableColumn

            Expect.equal c.SourceColumn "Sales[Net]" "reloads"

        testCase "warnings: a stale field-parameter label, a measure qualified with another table"
        <| fun () ->
            let t, dir = folder ()
            use _ = t
            let r = semctl [ "rename"; dir; "[Total]"; "Revenue"; "--json" ]
            Expect.equal r.Code ExitCode.ok r.Err
            let doc = json r

            let warnings =
                doc.GetProperty("warnings").EnumerateArray()
                |> Seq.map (fun w -> w.GetString())
                |> List.ofSeq

            Expect.exists warnings (fun w -> w.Contains "field parameter 'Metric'") (string warnings)
            Expect.exists warnings (fun w -> w.StartsWith "'Date'[Total] in measure:Date/Odd") (string warnings)

            Expect.isEmpty
                (doc.GetProperty("introducedErrors").EnumerateArray() |> List.ofSeq)
                "the existing unresolved reference isn't new"
    ]
