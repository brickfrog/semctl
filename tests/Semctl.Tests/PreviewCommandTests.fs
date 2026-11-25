/// `sm preview`: the first rows of a table, offline (--data) and from a (fake) server.
module Semctl.Tests.PreviewCommandTests

open System.IO
open System.Text.Json
open Expecto
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.Tests.Fixtures
open Semctl.Tests.CliHarness
open Semctl.Tests.EngineHelpers
open Semctl.Tests.XmlaFakes
open Semctl.Cli.Common

/// The star test model as a .bim file, with its data as a fixture directory next to it.
let private setup () =
    let t = new TempDir()
    let db, store = build star
    let path = t.Combine "model.bim"
    File.WriteAllText(path, JsonSerializer.SerializeDatabase db)
    let dir = t.Combine "data"
    Directory.CreateDirectory dir |> ignore

    for table in store.Tables do
        use writer =
            new StreamWriter(Path.Combine(dir, FixtureName.encode table.Name + ".csv"))

        Fixture.writeCsv writer table

    t, path, dir

let private lines (s: string) =
    s.Split('\n') |> Array.filter ((<>) "") |> List.ofArray


[<Tests>]
let localTests =
    testList "CLI preview on model files" [
        testCase "the first rows in fixture order, all columns; --rows; --columns picks and orders"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t

            let r = semctl [ "preview"; model; "Product"; "--data"; dir; "--rows"; "2" ]
            Expect.equal r.Code ExitCode.ok r.Err

            Expect.equal
                (lines r.Out)
                [
                    "Product[ProductKey]  Product[Name]  Product[Color]  Product[Category]"
                    "-------------------  -------------  --------------  -----------------"
                    "                  1  Bike           Red             Cycling"
                    "                  2  Helmet         Red             Safety"
                ]
                r.Out

            let r =
                semctl [
                    "preview"
                    model
                    "product"
                    "--data"
                    dir
                    "-c"
                    "category,name"
                    "-n"
                    "3"
                    "--csv"
                ]

            Expect.equal r.Code ExitCode.ok r.Err

            Expect.equal
                (lines (r.Out.Replace("\r", "")))
                [
                    "Product[Category],Product[Name]"
                    "Cycling,Bike"
                    "Safety,Helmet"
                    "Safety,Lock"
                ]
                ""

        testCase "default 10 rows; a table with fewer rows isn't truncated"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t
            let r = semctl [ "preview"; model; "Customer"; "--data"; dir; "--json" ]
            Expect.equal r.Code ExitCode.ok r.Err
            use doc = JsonDocument.Parse r.Out
            let root = doc.RootElement
            Expect.equal (root.GetProperty("command").GetString()) "preview" ""
            Expect.equal (root.GetProperty("table").GetString()) "Customer" ""
            let result = root.GetProperty("results").[0]
            Expect.equal (result.GetProperty("rows").GetArrayLength()) 3 ""
            Expect.isFalse (result.GetProperty("truncated").GetBoolean()) ""

            let r = semctl [ "preview"; model; "Sales"; "--data"; dir; "--rows"; "1"; "--json" ]
            use doc = JsonDocument.Parse r.Out
            let result = doc.RootElement.GetProperty("results").[0]
            Expect.equal (result.GetProperty("rows").GetArrayLength()) 1 ""
            Expect.isTrue (result.GetProperty("truncated").GetBoolean()) "more rows than shown"

        testCase "an unknown table or column: exit 3, with close names"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t
            let r = semctl [ "preview"; model; "Prodct"; "--data"; dir; "--json" ]
            Expect.equal r.Code ExitCode.input r.Out
            Expect.equal (errorCode r) "object-not-found" ""
            Expect.stringContains r.Out "table:Product" "suggested"

            let r = semctl [ "preview"; model; "Product"; "--data"; dir; "-c"; "Colour" ]
            Expect.equal r.Code ExitCode.input r.Out
            Expect.stringContains r.Err "column:Product/Color" r.Err

        testCase "usage errors"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t

            for args in
                [
                    [ "preview"; model; "Product" ]
                    [ "preview"; model ]
                    [ "preview" ]
                    [ "preview"; model; "Product"; "--data"; dir; "--rows"; "0" ]
                    [ "preview"; model; "Product"; "--data"; dir; "--json"; "--csv" ]
                    [ "preview"; $"xmla:{workspace};Sales"; "Product"; "--data"; dir ]
                ] do
                Expect.equal (semctl args).Code ExitCode.usage (String.concat " " args)
    ]

[<Tests>]
let serverTests =
    testSequenced
    <| testList "CLI preview on a server" [
        testCase "TOPN on the server, capped, columns projected"
        <| fun () ->
            let server = FakeServer()
            let sales = sampleDatabase ()
            sales.Name <- "Sales"
            server.Add sales

            server.OnQuery <-
                fun _ -> [
                    {
                        Columns = [
                            {
                                Name = "Sales[Amount]"
                                Type = QueryType.Decimal
                            }
                            {
                                Name = "Sales[Note]"
                                Type = QueryType.String
                            }
                        ]
                        Rows = [
                            [| QueryValue.Decimal 1M; QueryValue.String "a" |]
                            [| QueryValue.Decimal 2M; QueryValue.String "b" |]
                            [| QueryValue.Decimal 3M; QueryValue.String "c" |]
                        ]
                        Truncated = false
                    }
                ]

            let r =
                semctlOn server tokenEnv [ "preview"; $"xmla:{workspace};Sales"; "It's [odd]"; "-n"; "2"; "-c"; "note" ]

            Expect.equal r.Code ExitCode.ok r.Err
            Expect.equal (lines r.Out) [ "Sales[Note]"; "-----------"; "a"; "b" ] r.Out
            let _, dax, limit = server.Queries |> List.exactlyOne
            Expect.equal dax "EVALUATE TOPN ( 3, 'It''s [odd]' )" "the name is quoted; one row more than shown"
            Expect.equal limit (Some 3) "rows capped on the way back too"
            Expect.stringContains r.Err "Showing the first 2 rows" "the table has more"

            let j =
                semctlOn server tokenEnv [ "preview"; $"xmla:{workspace};Sales"; "T"; "-n"; "2"; "--json" ]

            use doc = JsonDocument.Parse j.Out
            let result = doc.RootElement.GetProperty("results").[0]
            Expect.equal (result.GetProperty("rows").GetArrayLength()) 2 ""
            Expect.isTrue (result.GetProperty("truncated").GetBoolean()) "3 rows came back for 2"

            let all =
                semctlOn server tokenEnv [ "preview"; $"xmla:{workspace};Sales"; "T"; "-n"; "3"; "--json" ]

            use doc = JsonDocument.Parse all.Out
            let result = doc.RootElement.GetProperty("results").[0]
            Expect.equal (result.GetProperty("rows").GetArrayLength()) 3 ""
            Expect.isFalse (result.GetProperty("truncated").GetBoolean()) "3 rows for 4 asked: that's all of them"
    ]
