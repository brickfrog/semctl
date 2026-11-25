/// Aggregating by group ("Aggregating by group" in the author's guide, Engine/Types.fs): in a row
/// context of a model table T, `CALCULATE ( SUM ( F[x] ) )` (and the other aggregations, and a
/// measure that is one) looks its value up in buckets of F's rows. Each test runs a query in
/// check mode (every value and row set compared with the row-by-row evaluation), against an
/// answer worked out by hand, says whether the shortcut was taken, and runs the query again with
/// it switched off.
///
/// The model is `star` (EngineHelpers.fs): Qty per Sales row 2, 1, 3, 1, 4, 5, 1 (17); Ann has
/// rows 1, 3, 7 (6), Bob 2, 5 (5), Cy 4, 6 (6); Bike is rows 1, 2, 7 (4), Helmet 3, 6 (8), Lock
/// 4 (1), Pump none; row 5's product doesn't exist (the blank row of Product). Price per row
/// 100, 100, 20, 15, 10, 20, 110.
module Semctl.Tests.EngineGroupAggTests

open Expecto
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.Tests.EngineHelpers

/// Values by group, and rows of an iteration evaluated from their buckets.
let private fired (p: Engine.Prepared) : int64 =
    let s = Engine.incrementalStats p
    s.GroupApplied + s.GroupRows

/// The query returns `expected` in check mode with no difference from the row-by-row evaluation,
/// and the same with aggregating by group off. Returns how often the shortcut was taken.
let private run (p: Engine.Prepared) (dax: string) (expected: obj list list) : int64 =
    let s = Engine.incrementalStats p
    s.Check <- true
    let before = fired p
    let mismatched = s.GroupMismatched

    try
        returnsInOrder p dax expected
        Expect.equal s.GroupMismatched mismatched "no value by group differs from the row-by-row evaluation"
    finally
        s.Check <- false

    let n = fired p - before
    s.GroupEnabled <- false

    try
        returnsInOrder p dax expected
    finally
        s.GroupEnabled <- true

    n

let private fires (p: Engine.Prepared) (dax: string) (expected: obj list list) =
    Expect.isGreaterThan (run p dax expected) 0L $"aggregated by group: {dax}"

let private fallsBack (p: Engine.Prepared) (dax: string) (expected: obj list list) =
    Expect.equal (run p dax expected) 0L $"not aggregated by group: {dax}"

/// The outcome (rows, or the error) is the same with aggregating by group on, in check mode, and
/// off.
let private sameOutcome (p: Engine.Prepared) (dax: string) =
    let s = Engine.incrementalStats p

    let outcome () =
        Engine.evaluateQuery p dax |> Result.map (List.map _.Rows)

    s.Check <- true

    let on =
        try
            outcome ()
        finally
            s.Check <- false

    Expect.equal s.GroupMismatched 0L "no value by group differs from the row-by-row evaluation"
    s.GroupEnabled <- false

    let off =
        try
            outcome ()
        finally
            s.GroupEnabled <- true

    Expect.equal on off dax
    on

let private row (expr: string) = $"EVALUATE ROW ( \"v\", {expr} )"

/// Currencies with a rate, a BLANK rate and a rate of 0, and a label that reads as a number in
/// some rows only; facts in A and D, and one in a currency that doesn't exist.
let private rates: TestModel = {
    Tables = [
        table "Cur" [ "Key", DataType.String; "Rate", DataType.Double; "Label", DataType.String ] [
            [ "A"; 2.0; "2" ]
            [ "B"; 0.0; "x" ]
            [ "C"; null; "y" ]
            [ "D"; 4.0; "4" ]
        ]
        table "Fact" [ "Id", DataType.Int64; "Cur", DataType.String; "Amount", DataType.Int64 ] [
            [ 1; "A"; 10 ]
            [ 2; "A"; 20 ]
            [ 3; "D"; 8 ]
            [ 4; "Z"; 5 ]
        ]
    ]
    Rels = [ rel ("Fact", "Cur") ("Cur", "Key") ]
    Measures = []
    Functions = []
}

/// A table with two equal rows and no key.
let private duplicates: TestModel = {
    Tables = [
        table "Dup" [ "K", DataType.String; "V", DataType.Int64 ] [ [ "a"; 1 ]; [ "a"; 1 ]; [ "b"; 2 ] ]
    ]
    Rels = []
    Measures = []
    Functions = []
}

/// `star` with Customer -> Country: Sales reaches Country through Customer. France has no
/// customers.
let private withCountry: TestModel = {
    star with
        Tables =
            star.Tables
            @ [
                table "Country" [ "Country", DataType.String; "Region", DataType.String ] [
                    [ "US"; "Americas" ]
                    [ "UK"; "Europe" ]
                    [ "FR"; "Europe" ]
                ]
            ]
        Rels = star.Rels @ [ rel ("Customer", "Country") ("Country", "Country") ]
}

let private bothWaysStar = {
    star with
        Rels =
            star.Rels
            |> List.map (fun r -> if r.From = ("Sales", "CustomerKey") then bothWays r else r)
}

let private qty = "CALCULATE ( SUM ( Sales[Qty] ) )"

[<Tests>]
let tests =
    testList "Engine aggregation by group" [

        testList "taken" [
            testCase "SUMX over a dimension" (fun () ->
                let p = prepare star
                fires p (row $"SUMX ( Customer, {qty} * 10 )") [ [ 170 ] ]
                // 6 / 1 + 5 / 2 + 6 / 3.
                fires p (row $"SUMX ( Customer, {qty} / Customer[CustomerKey] )") [ [ 10.5 ] ])

            testCase "fact rows of the blank row are in no bucket" (fun () ->
                // Row 5's product doesn't exist: Bike 4, Helmet 8, Lock 1, Pump nothing.
                let p = prepare star
                fires p (row $"SUMX ( Product, {qty} )") [ [ 13 ] ]
                fires p (row $"SUMX ( Product, {qty} / Product[ProductKey] )") [ [ 4.0 + 4.0 + 1.0 / 3.0 ] ])

            testCase "rows with nothing in their buckets" (fun () ->
                // Cy's rows: Helmet 5, Lock 1; Bike and Pump have nothing.
                let p = prepare star

                let cy (e: string) =
                    row $"CALCULATE ( {e}, Customer[Name] = \"Cy\" )"
                // SUMX leaves out the BLANK rows: BLANK / any key is BLANK.
                fires p (cy $"SUMX ( Product, {qty} / Product[ProductKey] )") [ [ 5.0 / 2.0 + 1.0 / 3.0 ] ]
                // BLANK + 1 is 1: those rows count. Bike 1, Helmet 6, Lock 2, Pump 1.
                fires p (cy $"SUMX ( Product, {qty} + 1 )") [ [ 10 ] ]
                fires p (cy $"AVERAGEX ( Product, {qty} + 1 )") [ [ 2.5 ] ]
                fires p (cy $"MINX ( Product, {qty} + 1 )") [ [ 1 ] ]
                // AVERAGEX of the BLANKs and the two values: the values' average.
                fires p (cy $"AVERAGEX ( Product, {qty} / Product[ProductKey] )") [ [ (5.0 / 2.0 + 1.0 / 3.0) / 2.0 ] ]
                fires p (cy $"COUNTX ( Product, {qty} / Product[ProductKey] )") [ [ 2 ] ])

            testCase "other aggregations" (fun () ->
                let p = prepare star
                // Highest price: Ann 110, Bob 100, Cy 20; lowest: Ann 20, Bob 10, Cy 15.
                fires p (row "SUMX ( Customer, CALCULATE ( MAX ( Sales[Price] ) ) )") [ [ 230 ] ]
                fires p (row "SUMX ( Customer, CALCULATE ( MIN ( Sales[Price] ) ) )") [ [ 45 ] ]
                fires p (row "SUMX ( Customer, CALCULATE ( COUNTROWS ( Sales ) ) )") [ [ 7 ] ]
                fires p (row "SUMX ( Customer, CALCULATE ( COUNT ( Sales[Qty] ) ) )") [ [ 7 ] ]
                // Ann 6 / 3, Bob 5 / 2, Cy 6 / 2.
                fires p (row "SUMX ( Customer, CALCULATE ( AVERAGE ( Sales[Qty] ) ) )") [ [ 2.0 + 2.5 + 3.0 ] ])

            testCase "a measure that is one aggregation" (fun () ->
                let p = prepare star
                fires p (row "SUMX ( Customer, [Units] )") [ [ 17 ] ]
                fires p (row "SUMX ( Customer, [Orders] )") [ [ 7 ] ]
                // A measure inside CALCULATE is not looked into.
                fallsBack p (row "SUMX ( Customer, CALCULATE ( [Units] ) )") [ [ 17 ] ]
                // [Amount] is an iteration, not one aggregation.
                fallsBack p (row "SUMX ( Customer, [Amount] )") [ [ 625 ] ])

            testCase "a column of the table iterated" (fun () ->
                let p = prepare star
                fires p (row "SUMX ( Customer, CALCULATE ( SUM ( Customer[CustomerKey] ) ) )") [ [ 6 ] ])

            testCase "rows that repeat, in a table with no key" (fun () ->
                // Each row's transition filters both equal rows: 2 + 2 + 1, and 2 + 2 + 2.
                let p = prepare duplicates
                fires p (row "SUMX ( Dup, CALCULATE ( COUNTROWS ( Dup ) ) )") [ [ 5 ] ]
                fires p (row "SUMX ( Dup, CALCULATE ( SUM ( Dup[V] ) ) )") [ [ 6 ] ])

            testCase "a fact table reached through another table" (fun () ->
                // US: Ann and Cy, 12; UK: Bob, 5; FR: nobody.
                let p = prepare withCountry
                fires p (row $"SUMX ( Country, {qty} )") [ [ 17 ] ]

                fires p (row $"CONCATENATEX ( Country, Country[Country] & \"=\" & {qty}, \",\", Country[Country] )") [
                    [ "FR=,UK=5,US=12" ]
                ])

            testCase "a table that is the rows of a model table" (fun () ->
                // FILTER keeps rows of Customer: each row's transition is the same as iterating
                // Customer's.
                let p = prepare star
                fires p (row $"SUMX ( FILTER ( Customer, Customer[Country] = \"US\" ), {qty} )") [ [ 12 ] ])

            testCase "rates of 0, BLANK and text" (fun () ->
                // A 30 / 2, D 8 / 4; B and C have nothing: BLANK / 0 and BLANK / BLANK are BLANK.
                // Fact 4's currency doesn't exist.
                let p = prepare rates
                fires p (row "SUMX ( Cur, CALCULATE ( SUM ( Fact[Amount] ) ) / Cur[Rate] )") [ [ 17.0 ] ]
                // Text: the same outcome, row by row or by group, whether a row raises or not.
                sameOutcome p (row "IFERROR ( SUMX ( Cur, CALCULATE ( SUM ( Fact[Amount] ) ) / Cur[Label] ), -1 )")
                |> ignore

                sameOutcome p (row "SUMX ( Cur, IFERROR ( CALCULATE ( SUM ( Fact[Amount] ) ) / Cur[Label], 100 ) )")
                |> ignore

                sameOutcome p (row "SUMX ( Cur, CALCULATE ( SUM ( Fact[Amount] ) ) / Cur[Label] )")
                |> ignore

                Expect.isGreaterThan (fired p) 0L "aggregated by group")
        ]

        testList "not taken" [
            testCase "a date table" (fun () ->
                // A transition on a date table has a rule of its own.
                fallsBack (prepare star) (row $"SUMX ( 'Date', {qty} )") [ [ 17 ] ])

            testCase "a table with relationships of its own" (fun () ->
                fallsBack (prepare star) (row $"SUMX ( Sales, {qty} )") [ [ 17 ] ])

            testCase "a table that is not the rows of a model table" (fun () ->
                fallsBack (prepare star) (row $"SUMX ( VALUES ( Customer[Name] ), {qty} )") [ [ 17 ] ])

            testCase "two row contexts" (fun () ->
                fallsBack (prepare star) (row $"SUMX ( Product, SUMX ( Customer, {qty} ) )") [ [ 13 ] ])

            testCase "a filter argument" (fun () ->
                // Rows with more than one: 2 + 3 + 4 + 5.
                fallsBack (prepare star) (row "SUMX ( Customer, CALCULATE ( SUM ( Sales[Qty] ), Sales[Qty] > 1 ) )") [
                    [ 14 ]
                ])

            testCase "an aggregation that reads the blank row" (fun () ->
                fallsBack (prepare star) (row "SUMX ( Customer, CALCULATE ( DISTINCTCOUNT ( Sales[ProductKey] ) ) )") [
                    // Ann: 1, 2; Bob: 1, 9; Cy: 3, 2.
                    [ 6 ]
                ])

            testCase "a relationship that filters both ways" (fun () ->
                fallsBack (prepare bothWaysStar) (row $"SUMX ( Customer, {qty} )") [ [ 17 ] ])

            testCase "calculation groups" (fun () ->
                let db, store = build star

                let t =
                    new Table(Name = "Shown", CalculationGroup = new CalculationGroup(Precedence = 1))

                t.Columns.Add(new DataColumn(Name = "Item", DataType = DataType.String, SourceColumn = "Name"))
                t.Columns.Add(new DataColumn(Name = "Ordinal", DataType = DataType.Int64, SourceColumn = "Ordinal"))

                t.CalculationGroup.CalculationItems.Add(
                    new CalculationItem(Name = "As is", Expression = "SELECTEDMEASURE ()", Ordinal = 0)
                )

                t.Partitions.Add(new Partition(Name = "Shown", Source = new CalculationGroupSource()))
                db.Model.Tables.Add t
                db.Model.DiscourageImplicitMeasures <- true

                let p =
                    match Engine.prepare db store with
                    | Ok p -> p
                    | Error e -> failtestf "prepare failed: %s" (SemctlError.message e)

                fallsBack p (row $"SUMX ( Customer, {qty} )") [ [ 17 ] ]
                fallsBack p (row "SUMX ( Customer, [Units] )") [ [ 17 ] ])

            testCase "a function called from a row context" (fun () ->
                let p = prepare star

                let dax =
                    $"DEFINE FUNCTION f = ( ) => SUMX ( Customer, {qty} ) EVALUATE ROW ( \"v\", SUMX ( Product, f ( ) ) )"

                let before = fired p

                match sameOutcome p dax with
                | Error(DaxNotSupported(what, _)) -> Expect.stringContains what "called from a row context" dax
                | other -> failtestf "expected not-supported, got %A" other

                Expect.equal (fired p) before "not aggregated by group")

            testCase "a calculated table being computed" (fun () ->
                let m = {
                    star with
                        Tables =
                            star.Tables
                            @ [
                                calculatedTable
                                    "Totals"
                                    [ "v", DataType.Int64 ]
                                    $"ROW ( \"v\", SUMX ( Customer, {qty} ) )"
                            ]
                }

                let p = prepare m
                Expect.equal (fired p) 0L "not aggregated by group while the table is computed"
                fallsBack p "EVALUATE Totals" [ [ 17 ] ])

            testCase "switched off" (fun () ->
                let p = prepare star
                let s = Engine.incrementalStats p
                s.GroupEnabled <- false

                try
                    returnsInOrder p (row $"SUMX ( Customer, {qty} )") [ [ 17 ] ]
                    Expect.equal (fired p) 0L "nothing by group while switched off"
                finally
                    s.GroupEnabled <- true)
        ]
    ]
