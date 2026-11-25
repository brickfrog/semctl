module Semctl.Tests.DepsTests

open Expecto
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.Deps
open Semctl.Tests.Fixtures

/// The sample model plus expressions exercising each resolution rule.
let private model () =
    let db = sampleDatabase ()
    let sales = db.Model.Tables.["Sales"]
    let date = db.Model.Tables.["Date"]

    sales.Measures.Add(new Measure(Name = "Avg", Expression = "DIVIDE([Total], COUNTROWS(Sales))"))

    sales.Measures.Add(
        new Measure(
            Name = "With Vars",
            Expression =
                "VAR Sales = 1 // shadows the table\nVAR t = ADDCOLUMNS('Date', \"Local\", 1)\n"
                + "RETURN SUMX(t, [Local] + t[Year]) + Sales + 'Date'[Year] + Double(2)"
        )
    )

    sales.Measures.Add(new Measure(Name = "Broken", Expression = "'Nope'[X] + [Missing] + Sales[Gone]"))
    sales.Measures.Add(new Measure(Name = "Commented", Expression = "1 // [Total]\n/* 'Date' */"))
    sales.Columns.Add(new CalculatedColumn(Name = "Doubled", Expression = "[Amount] * 2"))

    sales.Measures.["Total"].FormatStringDefinition <-
        new FormatStringDefinition(Expression = "IF([Avg] > 1, \"0\", \"0.0\")")

    date.Columns.["Month"].SortByColumn <- date.Columns.["DateKey"]

    let readers = db.Model.Roles.["Readers"]
    readers.TablePermissions.Add(new TablePermission(Table = sales, FilterExpression = "[Amount] > 0"))

    db.Model.Functions.Add(new Function(Name = "AddOne", Expression = "(Sales: INT64) => Sales + 1 + Double(Sales)"))
    db

let private targets (refs: Reference list) =
    refs
    |> List.map (fun r ->
        match r.Target with
        | Resolved t -> ObjectRef.format t, r.Via
        | Unresolved text -> "? " + text, r.Via)

let private refOf s =
    match ObjectRef.parse s with
    | Ok r -> r
    | Error e -> failtestf "%A" e

[<Tests>]
let tests =
    testList "Deps" [
        let g = lazy (build (model ()))
        let deps s = dependsOn g.Value (refOf s) |> targets

        let refBy s =
            referencedBy g.Value (refOf s)
            |> List.map (fun r -> ObjectRef.format r.From, r.Via)

        testCase "[Measure] and bare table names"
        <| fun () ->
            Expect.equal
                (deps "measure:Sales/Avg")
                [ "measure:Sales/Total", "expression"; "table:Sales", "expression" ]
                ""

        testCase "unqualified [Column] resolves in the object's own table"
        <| fun () -> Expect.equal (deps "column:Sales/Doubled") [ "column:Sales/Amount", "expression" ] ""

        testCase "VAR names shadow tables; locally made columns and VAR tables are skipped"
        <| fun () ->
            Expect.equal
                (deps "measure:Sales/With Vars")
                [
                    "table:Date", "expression"
                    "column:Date/Year", "expression"
                    "function:Double", "expression"
                ]
                ""

        testCase "unresolved references are kept, as written"
        <| fun () ->
            Expect.equal
                (deps "measure:Sales/Broken")
                [
                    "? 'Nope'[X]", "expression"
                    "? [Missing]", "expression"
                    "? Sales[Gone]", "expression"
                ]
                ""

        testCase "an alias shadows the measure only in its iterator row context"
        <| fun () ->
            let db = sampleDatabase ()

            let dax =
                """VAR t = ROW("Total", [Total]) RETURN RANKX(t, [Total], [Total]) + [Total]"""

            db.Model.Tables.["Sales"].Measures.Add(new Measure(Name = "Rank", Expression = dax))
            let hits = dependsOn (build db) (refOf "measure:Sales/Rank")

            Expect.equal
                (hits |> List.choose _.Start)
                [
                    dax.IndexOf("[Total]")
                    dax.LastIndexOf("[Total])")
                    dax.LastIndexOf("[Total]")
                ]
                ""

        testCase "unnamed projections retain virtual columns in both DAX separator dialects"
        <| fun () ->
            let expression =
                """SUMX(SELECTCOLUMNS(DATATABLE("X", INTEGER, {{1}}), [X]), [X]) + [Total]"""

            for dax in [ expression; expression.Replace(',', ';') ] do
                let db = sampleDatabase ()
                db.Model.Tables.["Sales"].Measures.Add(new Measure(Name = "Projected", Expression = dax))
                let hits = dependsOn (build db) (refOf "measure:Sales/Projected")
                Expect.equal (targets hits) [ "measure:Sales/Total", "expression" ] dax

        testCase "comments are ignored"
        <| fun () -> Expect.isEmpty (deps "measure:Sales/Commented") ""

        testCase "format string expressions and RLS filters are DAX too"
        <| fun () ->
            Expect.contains (deps "measure:Sales/Total") ("measure:Sales/Avg", "formatStringDefinition.expression") ""

            Expect.equal (deps "role:Readers") [ "column:Sales/Amount", "tablePermissions.Sales.filterExpression" ] ""

        testCase "UDF parameters shadow tables; UDF calls are dependencies"
        <| fun () -> Expect.equal (deps "function:AddOne") [ "function:Double", "expression" ] ""

        testCase "calculation items"
        <| fun () ->
            Expect.equal
                (deps "calcitem:Time Intelligence/YTD")
                // A qualified 'Date'[DateKey] is a column reference, not a separate table one.
                [ "column:Date/DateKey", "expression" ]
                ""

        testCase "structural links: sort-by, levels, relationships"
        <| fun () ->
            let r = refBy "column:Date/DateKey"
            Expect.contains r ("column:Date/Month", "sortByColumn") ""
            Expect.contains r ("relationship:Sales-Date", "toColumn") ""
            Expect.contains r ("calcitem:Time Intelligence/YTD", "expression") ""
            Expect.contains (refBy "column:Date/Year") ("level:Date/Calendar/Year", "column") ""

        testCase "line numbers"
        <| fun () ->
            let lines =
                dependsOn g.Value (refOf "measure:Sales/With Vars")
                |> List.map (fun r -> r.Line)
                |> List.distinct

            Expect.equal lines [ Some 2; Some 3 ] "'Date' on line 2, the rest on line 3"

        testCase "DAX references carry their source span; structural links don't"
        <| fun () ->
            let db = model ()
            let dax = db.Model.Tables.["Sales"].Measures.["With Vars"].Expression

            for r in dependsOn g.Value (refOf "measure:Sales/With Vars") do
                match r.Start, r.Length with
                | Some start, Some length -> Expect.equal (dax.Substring(start, length)) r.Text ""
                | _ -> failtest "a DAX reference without a span"

            for r in g.Value.References |> List.filter (fun r -> r.Line.IsNone) do
                Expect.equal (r.Start, r.Length) (None, None) r.Via

        testCase "tree: depth, and cycles are marked not followed"
        <| fun () ->
            let db = model ()
            let sales = db.Model.Tables.["Sales"]
            sales.Measures.Add(new Measure(Name = "A", Expression = "[B]"))
            sales.Measures.Add(new Measure(Name = "B", Expression = "[A] + [Total]"))
            let g = build db
            let one = tree DependsOn 1 g (refOf "measure:Sales/A")

            Expect.equal
                (one |> List.map (fun n -> n.Target, n.Children.IsEmpty))
                [ Resolved(refOf "measure:Sales/B"), true ]
                ""

            let all = tree DependsOn 0 g (refOf "measure:Sales/A")

            let b = all |> List.exactlyOne

            let cycle =
                b.Children |> List.find (fun n -> n.Target = Resolved(refOf "measure:Sales/A"))

            Expect.isTrue cycle.Cycle "A -> B -> A is circular"
            Expect.isEmpty cycle.Children ""

            Expect.isTrue
                (b.Children
                 |> List.exists (fun n -> n.Target = Resolved(refOf "measure:Sales/Total")))
                ""

        testCase "the same target twice is one node with both occurrences"
        <| fun () ->
            let db = model ()
            db.Model.Tables.["Sales"].Measures.Add(new Measure(Name = "Twice", Expression = "[Total]\n+ [Total]"))
            let nodes = tree DependsOn 1 (build db) (refOf "measure:Sales/Twice")

            Expect.equal (nodes |> List.map (fun n -> n.Via)) [ [ "expression", Some 1; "expression", Some 2 ] ] ""

        testCase "a calculated table's own columns are local; [X] there isn't a column of its own table"
        <| fun () ->
            let db = model ()
            let cal = new Table(Name = "Cal")

            cal.Partitions.Add(
                new Partition(
                    Name = "Cal",
                    Source =
                        new CalculatedPartitionSource(
                            Expression =
                                "ADDCOLUMNS(CALENDAR(DATE(2020,1,1), DATE(2020,12,31)), \"Year\", YEAR([Date]), \"T\", [Total])"
                        )
                )
            )

            cal.Columns.Add(
                new CalculatedTableColumn(Name = "Date", SourceColumn = "[Date]", DataType = DataType.DateTime)
            )

            cal.Columns.Add(
                new CalculatedTableColumn(Name = "Year", SourceColumn = "[Year]", DataType = DataType.Int64)
            )

            db.Model.Tables.Add cal
            let g = build db
            let deps = dependsOn g (refOf "table:Cal") |> targets

            Expect.equal
                deps
                [ "measure:Sales/Total", "expression" ]
                "only the measure; [Date] is produced, not referenced"

        testCase "window functions propagate virtual columns into ORDERBY and keep outer row context"
        <| fun () ->
            let db = sampleDatabase ()

            let dax =
                """VAR t = ADDCOLUMNS(VALUES(Product[Color]), "Total", [Total]) RETURN SUMX(OFFSET(1, t, ORDERBY([Total], ASC)), [Total]) + [Total]"""

            db.Model.Tables.["Sales"].Measures.Add(new Measure(Name = "Windowed", Expression = dax))
            let hits = dependsOn (build db) (refOf "measure:Sales/Windowed")

            Expect.equal
                (hits
                 |> List.filter (fun r -> r.Target = Resolved(refOf "measure:Sales/Total"))
                 |> List.choose _.Start)
                [ dax.IndexOf("[Total]"); dax.LastIndexOf("[Total]") ]
                "only the ADDCOLUMNS definition and trailing addition refer to the model measure"

            let db2 = sampleDatabase ()

            let rankDax =
                """VAR t = ADDCOLUMNS(VALUES(Date[Year]), "Total", [Total]) RETURN SUMX(t, RANK(DENSE, t, ORDERBY([Total], DESC)) + [Total])"""

            db2.Model.Tables.["Sales"].Measures.Add(new Measure(Name = "Ranked", Expression = rankDax))
            let rankHits = dependsOn (build db2) (refOf "measure:Sales/Ranked")

            Expect.equal
                (rankHits
                 |> List.filter (fun r -> r.Target = Resolved(refOf "measure:Sales/Total"))
                 |> List.choose _.Start)
                [ rankDax.IndexOf("[Total]") ]
                "RANK's ORDERBY and outer SUMX see the virtual column"

        testCase "IF, SWITCH, anonymous qualified projections, and table generators retain virtual columns"
        <| fun () ->
            let db = sampleDatabase ()
            let sales = db.Model.Tables.["Sales"]
            sales.Measures.Add(new Measure(Name = "Value", Expression = "1"))
            sales.Measures.Add(new Measure(Name = "Date", Expression = "1"))

            let add name expr =
                sales.Measures.Add(new Measure(Name = name, Expression = expr))

            let ifDax =
                """VAR t = IF(TRUE(), ROW("Total", [Total]), ROW("Total", 0)) RETURN SUMX(t, [Total])"""

            let switchDax =
                """VAR t = SWITCH(1, 1, ROW("Total", [Total]), ROW("Total", 0)) RETURN SUMX(t, [Total])"""

            let anonDax =
                """SUMX(SELECTCOLUMNS(ROW("Total", [Total]), ''[Total]), [Total]) + [Total]"""

            let seriesDax = """SUMX(GENERATESERIES(1, 3), [Value]) + [Value]"""
            let ctorDax = """SUMX({ 1, 2 }, [Value]) + [Value]"""

            let calDax =
                """COUNTROWS(FILTER(CALENDAR(DATE(2024,1,1), DATE(2024,1,2)), [Date] > DATE(2024,1,1))) + [Date]"""

            let sampleDax = """SUMX(SAMPLE(1, ROW("Total", [Total]), [Total], ASC), [Total])"""

            let xirrDax =
                """XIRR(ROW("Total", [Total], "When", DATE(2024,1,1)), [Total], [When])"""

            add "IfTable" ifDax
            add "SwitchTable" switchDax
            add "AnonProj" anonDax
            add "Series" seriesDax
            add "Ctor" ctorDax
            add "CalExpr" calDax
            add "SampleExpr" sampleDax
            add "XirrExpr" xirrDax

            let g = build db

            let totalStarts name =
                dependsOn g (refOf $"measure:Sales/{name}")
                |> List.filter (fun r -> r.Target = Resolved(refOf "measure:Sales/Total"))
                |> List.choose _.Start

            Expect.equal (totalStarts "IfTable") [ ifDax.IndexOf("[Total]") ] "IF preserves ROW column"
            Expect.equal (totalStarts "SwitchTable") [ switchDax.IndexOf("[Total]") ] "SWITCH preserves ROW column"

            Expect.equal
                (totalStarts "AnonProj")
                [ anonDax.IndexOf("[Total]"); anonDax.LastIndexOf("[Total]") ]
                "''[Total] preserves virtual column"

            Expect.equal (totalStarts "SampleExpr") [ sampleDax.IndexOf("[Total]") ] "SAMPLE preserves virtual column"
            Expect.equal (totalStarts "XirrExpr") [ xirrDax.IndexOf("[Total]") ] "XIRR preserves virtual column"

            let valueStarts name =
                dependsOn g (refOf $"measure:Sales/{name}")
                |> List.filter (fun r -> r.Target = Resolved(refOf "measure:Sales/Value"))
                |> List.choose _.Start

            Expect.equal (valueStarts "Series") [ seriesDax.LastIndexOf("[Value]") ] "GENERATESERIES produces [Value]"
            Expect.equal (valueStarts "Ctor") [ ctorDax.LastIndexOf("[Value]") ] "table constructor produces [Value]"

            let dateStarts =
                dependsOn g (refOf "measure:Sales/CalExpr")
                |> List.filter (fun r -> r.Target = Resolved(refOf "measure:Sales/Date"))
                |> List.choose _.Start

            Expect.equal dateStarts [ calDax.LastIndexOf("[Date]") ] "CALENDAR produces [Date]"
    ]
