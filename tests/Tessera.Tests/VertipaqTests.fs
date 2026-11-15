/// `tess vertipaq`: statistics from fixture data, a (fake) server and .vpax files; the views,
/// --export (and --obfuscate), --annotate, and `bpa --vpax`.
module Tessera.Tests.VertipaqTests

open System
open System.IO
open System.IO.Compression
open System.Text.Json
open Expecto
open Microsoft.AnalysisServices.Tabular
open Tessera
open Tessera.Vertipaq
open Tessera.Xmla
open Tessera.Tests.Fixtures
open Tessera.Tests.CliHarness
open Tessera.Tests.EngineHelpers
open Tessera.Tests.XmlaFakes
open Tessera.Cli.Common

let private contoso = Path.Combine(__SOURCE_DIRECTORY__, "vertipaq", "Contoso.vpax")

/// The star test model as a .bim file, with its data as a fixture directory next to it. In the
/// data, one sale (Id 5) has a ProductKey (9) that no product has.
let private setupWith (m: TestModel) =
    let t = new TempDir()
    let db, store = build m
    let path = t.Combine "model.bim"
    File.WriteAllText(path, JsonSerializer.SerializeDatabase db)
    let dir = t.Combine "data"
    Directory.CreateDirectory dir |> ignore

    for table in store.Tables do
        use writer =
            new StreamWriter(Path.Combine(dir, FixtureName.encode table.Name + ".csv"))

        Fixture.writeCsv writer table

    t, path, dir

let private setup () = setupWith star

let private statsOf (model: string) (dir: string) =
    match ModelIO.loadPath model with
    | Error e -> failtestf "%s" (TessError.message e)
    | Ok(_, db) ->
        match VertipaqFixture.fromDirectory db dir with
        | Ok s -> db, s
        | Error e -> failtestf "%s" (TessError.message e)

let private col (s: Stats) (t: string) (c: string) =
    s.Columns |> List.find (fun x -> x.Table = t && x.Column = c)

let private relFrom (s: Stats) (t: string) (c: string) =
    s.Relationships |> List.find (fun r -> r.FromTable = t && r.FromColumn = c)

let private json (r: Run) =
    use doc = JsonDocument.Parse r.Out
    doc.RootElement.Clone()

let private errorCode (r: Run) =
    (json r).GetProperty("error").GetProperty("code").GetString()

let private has (e: JsonElement) (name: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    e.TryGetProperty(name, &v)

let private readImported (path: string) =
    match VertipaqVpax.read path with
    | Ok i -> i
    | Error e -> failtestf "%s" (TessError.message e)

/// Every text in every part of a .vpax (a zip).
let private partsText (path: string) =
    use zip = ZipFile.OpenRead path

    [
        for e in zip.Entries ->
            use r = new StreamReader(e.Open())
            e.FullName, r.ReadToEnd()
    ]

[<Tests>]
let estimateTests =
    testList "vertipaq estimates" [
        testCase "the formulas the README documents"
        <| fun () ->
            Expect.equal (Estimate.bits 1L) 1 "a single value still takes a bit"
            Expect.equal (Estimate.bits 2L) 1 "two values"
            Expect.equal (Estimate.bits 3L) 2 "three values"
            Expect.equal (Estimate.bits 256L) 8 "256 values"
            Expect.equal (Estimate.bits 257L) 9 "257 values"
            Expect.equal (Estimate.dataSize 7L 5L) 3L "7 rows x 3 bits = 21 bits = 3 bytes"
            Expect.equal (Estimate.dataSize 0L 0L) 0L "no rows"
            Expect.equal (Estimate.dictionarySize 4L 18L) 100L "16 x 4 + 2 x 18"
            Expect.equal (Estimate.hierarchySize true 4L) 32L "8 bytes a value"
            Expect.equal (Estimate.hierarchySize false 4L) 0L "not available in MDX: none"
            Expect.equal (Estimate.userHierarchySize [ 2L; 5L ]) 56L "8 x (2 + 5)"
            Expect.equal (Estimate.relationshipSize 4L 4L) 1L "4 keys x 2 bits = 1 byte"
    ]

[<Tests>]
let fixtureTests =
    testList "vertipaq from fixture data" [
        testCase "rows, cardinality and RI violations are exact; sizes follow the formulas"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t
            let _, s = statsOf model dir
            Expect.isTrue s.Estimated "fixture sizes are estimates"

            Expect.equal
                (s.Tables |> List.map (fun x -> x.Table, x.Rows))
                [ "Product", Some 4L; "Customer", Some 3L; "Date", Some 4L; "Sales", Some 7L ]
                "rows per table"

            Expect.equal (col s "Product" "Color").Cardinality (Some 3L) "Red, Black, Blue"
            Expect.equal (col s "Sales" "ProductKey").Cardinality (Some 4L) "1, 2, 3 and the orphan 9"
            Expect.equal (col s "Sales" "Qty").Cardinality (Some 5L) "1..5"
            Expect.equal (col s "Sales" "Qty").DataSize (Some 3L) "7 rows x 3 bits"
            // Bike, Helmet, Lock, Pump: 18 characters.
            Expect.equal (col s "Product" "Name").DictionarySize (Some 100L) "16 x 4 + 2 x 18"
            Expect.equal (col s "Product" "Name").HierarchySize (Some 32L) "8 x 4"
            Expect.equal (col s "Product" "Name").Encoding None "only a server knows the encoding"
            Expect.equal (col s "Product" "Name").EncodingHint (Some "Default") "the model's hint"

            let orphan = relFrom s "Sales" "ProductKey"
            Expect.equal orphan.MissingKeys (Some 1L) "one key (9) has no product"
            Expect.equal orphan.InvalidRows (Some 1L) "one sale has it"
            Expect.equal orphan.SizeFrom (Some 1L) "4 keys x ceil(log2 4) = 2 bits: 1 byte"

            let ship = relFrom s "Sales" "ShipDate"
            Expect.isFalse ship.IsActive "the inactive relationship"
            Expect.equal ship.InvalidRows (Some 0L) "inactive relationships are checked too"

            let p = s.Partitions |> List.find (fun p -> p.Table = "Sales")
            Expect.equal p.Rows (Some 7L) "one partition holds the table"
            Expect.equal p.Segments None "segments depend on the server"

            Expect.equal
                p.DataSize
                (Stats.columnsOf s "Sales"
                 |> List.map _.DataSize
                 |> List.reduce (Option.map2 (+)))
                "the partition's data is its columns' data"

            Expect.equal
                (Stats.tableSize s "Sales")
                (Option.map2
                    (+)
                    (Stats.columnsSize s "Sales")
                    (Some(s.Relationships |> List.sumBy (fun r -> defaultArg r.SizeFrom 0L))))
                "a table's size includes the relationships charged to it"

            Expect.isEmpty s.Warnings "nothing unknown"

        testCase "a BLANK key with no BLANK on the one side is an RI violation"
        <| fun () ->
            let m = {
                star with
                    Tables =
                        star.Tables
                        |> List.map (fun t ->
                            if t.Name = "Sales" then
                                {
                                    t with
                                        Rows = t.Rows @ [ [ 8; null; 1; null; null; 1; 1 ] ]
                                }
                            else
                                t)
            }

            let t, model, dir = setupWith m
            use _ = t
            let _, s = statsOf model dir
            let r = relFrom s "Sales" "ProductKey"
            Expect.equal r.InvalidRows (Some 2L) "the orphan 9 and the BLANK"
            Expect.equal r.MissingKeys (Some 2L) "9 and BLANK, as DISTINCTCOUNT counts them"

        testCase "calculated columns and tables come from the engine; what it refuses is unknown, with a warning"
        <| fun () ->
            let m = {
                star with
                    Tables =
                        (star.Tables
                         |> List.map (fun t ->
                             if t.Name = "Sales" then
                                 withCalculated
                                     [
                                         "Big", DataType.Boolean, "Sales[Qty] > 2"
                                         "Broken", DataType.Int64, "NOSUCHFUNCTION ( 1 )"
                                     ]
                                     t
                             else
                                 t))
                        @ [
                            calculatedTable "Colors" [ "Color", DataType.String ] "DISTINCT ( Product[Color] )"
                        ]
            }

            let t, model, dir = setupWith m
            use _ = t
            let _, s = statsOf model dir
            Expect.equal (col s "Sales" "Big").Cardinality (Some 2L) "TRUE and FALSE"
            Expect.equal (col s "Sales" "Big").Kind "calculated" "kind"
            Expect.equal (s.Tables |> List.find (fun x -> x.Table = "Colors")).Rows (Some 3L) "three colours"
            Expect.equal (col s "Colors" "Color").Cardinality (Some 3L) "distinct colours"
            // Red, Black, Blue: 12 characters.
            Expect.equal
                (col s "Colors" "Color").DictionarySize
                (Some(16L * 3L + 2L * 12L))
                "text lengths from the engine"

            let broken = col s "Sales" "Broken"
            Expect.equal broken.Cardinality None "unknown"
            Expect.equal (Column.totalSize broken) None "no size"
            Expect.equal (Stats.tableSize s "Sales") None "an unknown part makes the table's size unknown"
            Expect.exists s.Warnings (fun w -> w.Contains "'Sales'[Broken]") "the warning names the column"

        testCase "a fixture with errors is refused"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t
            File.AppendAllText(Path.Combine(dir, "Sales.csv"), "8,x,1,2024-01-01,2024-01-01,1,1\n")

            match ModelIO.loadPath model with
            | Ok(_, db) ->
                match VertipaqFixture.fromDirectory db dir with
                | Error(FixtureHasErrors _) -> ()
                | other -> failtestf "expected FixtureHasErrors, got %A" other
            | Error e -> failtestf "%s" (TessError.message e)

        testCase "a table with several partitions has no per-partition statistics"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t

            match ModelIO.loadPath model with
            | Ok(_, db) ->
                let sales = db.Model.Tables.["Sales"]
                sales.Partitions.Add(new Partition(Name = "Sales 2", Source = new MPartitionSource(Expression = "1")))

                match VertipaqFixture.fromDirectory db dir with
                | Ok s ->
                    let parts = s.Partitions |> List.filter (fun p -> p.Table = "Sales")
                    Expect.equal parts.Length 2 "both partitions are listed"
                    Expect.all parts (fun p -> p.Rows.IsNone && p.DataSize.IsNone) "but not measured"
                    Expect.exists s.Warnings (fun w -> w.Contains "several partitions") "and that is said"
                    Expect.isSome (col s "Sales" "Qty").DataSize "the columns are still measured"
                | Error e -> failtestf "%s" (TessError.message e)
            | Error e -> failtestf "%s" (TessError.message e)
    ]

[<Tests>]
let annotationTests =
    testList "vertipaq annotations" [
        testCase "Kovalsky's names and values, invariant; a second run changes nothing; stale ones go"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t
            let db, s = statsOf model dir
            let applied = Annotations.apply [] s db
            Expect.isGreaterThan applied.Changed 0 "annotations were written"
            Expect.isEmpty applied.Unmatched "every object is in the model"
            let sales = db.Model.Tables.["Sales"]
            Expect.equal (sales.Annotations.Find "Vertipaq_RowCount").Value "7" "row count"
            Expect.equal (sales.Columns.["Qty"].Annotations.Find "Vertipaq_Cardinality").Value "5" "cardinality"
            Expect.equal (sales.Columns.["Qty"].Annotations.Find "Vertipaq_DataSize").Value "3" "data size"

            let pct =
                (sales.Columns.["Qty"].Annotations.Find "Vertipaq_ColumnSizePctOfTable").Value

            Expect.isTrue (pct.StartsWith "0." && not (pct.Contains ",")) $"a fraction, in the invariant culture: {pct}"

            let rel =
                db.Model.Relationships
                |> Seq.cast<SingleColumnRelationship>
                |> Seq.find (fun r -> r.FromColumn.Name = "ProductKey")

            Expect.equal (rel.Annotations.Find "Vertipaq_RIViolationInvalidRows").Value "1" "RI violations"
            Expect.equal (rel.Annotations.Find "Vertipaq_MaxFromCardinality").Value "4" "from cardinality"
            Expect.equal (db.Model.Annotations.Find Annotations.estimated).Value "true" "marked as estimated"
            Expect.equal (Annotations.apply [] s db).Changed 0 "idempotent"

            // Statistics that are now unknown remove what an earlier run wrote.
            let unknown = {
                s with
                    Columns =
                        s.Columns
                        |> List.map (fun c ->
                            if c.Column = "Qty" then
                                {
                                    c with
                                        Cardinality = None
                                        DataSize = None
                                }
                            else
                                c)
                    Estimated = false
            }

            Annotations.apply [] unknown db |> ignore
            Expect.isNull (sales.Columns.["Qty"].Annotations.Find "Vertipaq_Cardinality") "stale cardinality removed"
            Expect.isNull (sales.Columns.["Qty"].Annotations.Find "Vertipaq_ColumnSize") "unknown size removed"
            Expect.isNotNull (sales.Columns.["Qty"].Annotations.Find "Vertipaq_DictionarySize") "known ones stay"
            Expect.isNull (db.Model.Annotations.Find Annotations.estimated) "not estimated any more"

        testCase "relationships are matched by their columns, not their names; a table filter stays in it"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t
            let db, s = statsOf model dir

            let renamed = {
                s with
                    Relationships = s.Relationships |> List.map (fun r -> { r with Name = "server-" + r.Name })
            }

            Annotations.apply [ "Product" ] renamed db |> ignore

            let annotated (r: SingleColumnRelationship) =
                not (isNull (r.Annotations.Find "Vertipaq_RelationshipSize"))

            let rels =
                db.Model.Relationships |> Seq.cast<SingleColumnRelationship> |> List.ofSeq

            Expect.isTrue
                (rels |> List.find (fun r -> r.ToTable.Name = "Product") |> annotated)
                "the relationship to Product, despite its other name"

            Expect.isFalse
                (rels |> List.find (fun r -> r.ToTable.Name = "Customer") |> annotated)
                "relationships of other tables are left alone"

            Expect.isNull (db.Model.Tables.["Sales"].Annotations.Find "Vertipaq_RowCount") "other tables too"
            Expect.isNull (db.Model.Annotations.Find "Vertipaq_ModelSize") "and the model"
            Expect.isNotNull (db.Model.Tables.["Product"].Annotations.Find "Vertipaq_RowCount") "Product is annotated"
    ]

[<Tests>]
let vpaxTests =
    testList "vertipaq .vpax files" [
        testCase "a file from Bravo: sizes and counts as VertiPaq Analyzer's own view of it"
        <| fun () ->
            let i = readImported contoso
            let s = i.Stats
            Expect.isFalse s.Estimated "measured by a server"
            Expect.equal s.Tables.Length 10 "tables"
            Expect.isSome i.Database "the file carries the model"

            let view =
                partsText contoso |> List.find (fun (n, _) -> n = "DaxVpaView.json") |> snd

            use doc = JsonDocument.Parse(view.TrimStart '﻿')

            for t in doc.RootElement.GetProperty("Tables").EnumerateArray() do
                let name = t.GetProperty("TableName").GetString()
                Expect.equal (Stats.tableSize s name) (Some(t.GetProperty("TableSize").GetInt64())) $"size of {name}"

                Expect.equal
                    (s.Tables |> List.find (fun x -> x.Table = name)).Rows
                    (Some(t.GetProperty("RowsCount").GetInt64()))
                    $"rows of {name}"

            let broken =
                s.Relationships
                |> List.filter (fun r -> r.InvalidRows |> Option.exists (fun n -> n > 0L))

            Expect.equal (broken |> List.map (fun r -> r.MissingKeys, r.InvalidRows)) [ Some 3L, Some 12535L ] "RI"

        testCase "an exported file opens in VertiPaq Analyzer's view model and reads back the same, still estimated"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t
            let db, s = statsOf model dir
            let m, unknown = VertipaqVpax.toDaxModel "0.0.0-test" s db
            Expect.isEmpty unknown "everything was known"
            let path = t.Combine "out.vpax"
            Expect.isOk (VertipaqVpax.write path m (Some db)) "written"

            let parts = partsText path |> List.map fst |> Set.ofList

            Expect.isTrue
                (Set.isSubset (set [ "DaxModel.json"; "DaxVpaView.json"; "Model.bim"; "CompatabilityMode.txt" ]) parts)
                $"the four parts: {parts}"

            // What DAX Studio builds its VertiPaq Analyzer grids from.
            let content = Dax.Vpax.Tools.VpaxTools.ImportVpax(path)
            let view = Dax.ViewModel.VpaModel(content.DaxModel)

            for vt in view.Tables do
                Expect.equal (Some vt.TableSize) (Stats.tableSize s vt.TableName) $"size of {vt.TableName}"
                Expect.equal (Some vt.RowsCount) (s.Tables |> List.find (fun x -> x.Table = vt.TableName)).Rows "rows"

            let back = (readImported path).Stats
            Expect.isTrue back.Estimated "an estimate stays an estimate"
            Expect.equal (col back "Sales" "Qty").Cardinality (col s "Sales" "Qty").Cardinality "cardinality"
            Expect.equal (Column.totalSize (col back "Sales" "Qty")) (Column.totalSize (col s "Sales" "Qty")) "size"
            Expect.equal (relFrom back "Sales" "ProductKey").InvalidRows (Some 1L) "RI"
            Expect.equal (Stats.modelSize back) (Stats.modelSize s) "model size"

        testCase
            "obfuscated: only DaxModel.json, no real name in any part; the dictionary maps back and can be extended"
        <| fun () ->
            use t = new TempDir()
            let i = readImported contoso
            let real = i.Stats.Tables |> List.map _.Table

            let dict =
                VertipaqVpax.obfuscate None i.DaxModel
                |> Result.defaultWith (fun e -> failtestf "%A" e)

            let path = t.Combine "obf.vpax"
            Expect.isOk (VertipaqVpax.write path i.DaxModel None) "written"
            let parts = partsText path

            Expect.equal
                (parts
                 |> List.map fst
                 |> List.filter (fun n -> n.EndsWith ".json" || n.EndsWith ".bim"))
                [ "DaxModel.json" ]
                "parts"

            // DAX's reserved names (Contoso has tables called Currency and Date) stay as they are; the
            // dictionary lists them.
            let kept = set dict.UnobfuscatedValues
            Expect.equal kept (set [ "Currency"; "Date" ]) "the reserved names"

            for name in real |> List.filter (fun n -> not (kept.Contains n)) do
                for part, text in parts do
                    Expect.isFalse (text.Contains $"\"{name}\"") $"'{name}' is not in {part}"

            let back = (readImported path).Stats

            Expect.equal
                (back.Tables
                 |> List.map (fun x ->
                     if kept.Contains x.Table then
                         x.Table
                     else
                         dict.GetValue x.Table)
                 |> List.sort)
                (List.sort real)
                "names map back"

            Expect.exists (readImported path).Stats.Warnings (fun w -> w.Contains "obfuscated") "said when read"

            // Incremental: the same dictionary gives the same names again.
            let dictPath = t.Combine "obf.dict"
            dict.WriteTo(dictPath, overwrite = true)
            let again = readImported contoso

            VertipaqVpax.obfuscate (Some dictPath) again.DaxModel
            |> Result.defaultWith (fun e -> failtestf "%A" e)
            |> ignore

            Expect.equal
                (again.DaxModel.Tables |> Seq.map _.TableName.Name |> List.ofSeq)
                (back.Tables |> List.map _.Table)
                "same obfuscated names"

        testCase "not a .vpax, a missing file"
        <| fun () ->
            use t = new TempDir()
            let path = t.Combine "x.vpax"
            File.WriteAllText(path, "hello")

            match VertipaqVpax.read path with
            | Error(LoadFailed _) -> ()
            | other -> failtestf "expected LoadFailed, got %A" other

            match VertipaqVpax.read (t.Combine "none.vpax") with
            | Error(PathNotFound _) -> ()
            | other -> failtestf "expected PathNotFound, got %A" other
    ]

// ---- The server ---------------------------------------------------------------------------------

let private result (rows: QueryValue list list) : QueryResult list = [
    {
        Columns =
            match rows with
            | first :: _ ->
                first
                |> List.mapi (fun i _ -> {
                    Name = $"c{i}"
                    Type = QueryType.Variant
                })
            | [] -> []
        Rows = rows |> List.map Array.ofList
        Truncated = false
    }
]

let private s (text: string) = QueryValue.String text
let private i (n: int64) = QueryValue.Int64 n
let private d (n: decimal) = QueryValue.Decimal n

/// A fake server holding the star model, answering exactly VertiPaq Analyzer's queries. Numbers
/// come back as decimals, integers and strings, as DMVs and ADOMD send them.
let private starServer () =
    let server = FakeServer()
    let db, _ = build star
    db.Name <- "Shop"
    server.Add db
    let detached = ResultReader.detach db

    let daxTables = [ "Product"; "Customer"; "Date"; "Sales" ]

    let daxColumns = [
        for t in detached.Model.Tables do
            for c in t.Columns do
                if c.Type <> ColumnType.RowNumber then
                    t.Name, c.Name, false
    ]

    let indexed = [
        for r in detached.Model.Relationships do
            let r = r :?> SingleColumnRelationship
            r.FromTable.Name, r.FromColumn.Name, r.ToTable.Name, r.ToColumn.Name, r.IsActive
    ]

    let relStats =
        indexed
        |> List.mapi (fun n (ft, fc, tt, tc, active) ->
            n + 1,
            {
                Name = ""
                FromTable = ft
                FromColumn = fc
                ToTable = tt
                ToColumn = tc
                IsActive = active
                FromCardinalityType = "Many"
                ToCardinalityType = "One"
                MissingKeys = None
                InvalidRows = None
                SizeFrom = None
                SizeTo = None
            })

    let answers =
        dict [
            VertipaqServer.tablesQuery,
            result [
                [ s "Product"; s "Product (1)"; d 4M ]
                [ s "Sales"; s "Sales (4)"; i 6L ]
                [ s "NotInModel"; s "NotInModel (9)"; i 1L ]
            ]
            VertipaqServer.columnsQuery,
            result [
                [ s "Sales"; s "ProductKey (40)"; s "ProductKey"; d 120M; i 2L ]
                [ s "Product"; s "ProductKey (10)"; s "ProductKey"; s "88"; i 1L ]
                [ s "Sales"; s "Gone (41)"; s "Gone"; d 1M; i 1L ]
            ]
            VertipaqServer.cardinalityQuery,
            result [
                [ s "Sales"; s "H$Sales (4)$ProductKey (40)"; i 3L ]
                [ s "Product"; s "H$Product (1)$ProductKey (10)"; d 4M ]
            ]
            VertipaqServer.segmentsQuery,
            result [
                [ s "Sales"; s "Sales"; s "ProductKey (40)"; i 0L; i 5L; d 40M ]
                [ s "Sales"; s "Sales"; s "ProductKey (40)"; i 1L; i 2L; d 24M ]
                [ s "Product"; s "Product"; s "ProductKey (10)"; i 0L; i 4L; d 16M ]
            ]
            VertipaqServer.hierarchiesQuery,
            result [
                [ s "Sales"; d 48M; s "H$Sales (4)$ProductKey (40)" ]
                [ s "Product"; i 32L; s "H$Product (1)$ProductKey (10)" ]
            ]
            VertipaqServer.userHierarchyNamesQuery, result []
            VertipaqServer.userHierarchiesQuery, result []
            VertipaqServer.relationshipSizesQuery, result [ [ s "Sales"; s "R$Sales (4)$x (100)"; d 32M ] ]
            VertipaqServer.relationshipsQuery, result [ [ d 100M; d 4M; d 40M; d 1M; d 10M ] ]
            yield!
                VertipaqServer.tableRowsQueries daxTables
                |> List.map (fun q -> q, result [ [ s "0000Product"; i 4L ]; [ s "0003Sales"; s "7" ] ])
            yield!
                VertipaqServer.columnCardinalityQueries daxColumns
                |> List.map (fun q -> q, result [ [ s "0004Sales"; s "0004ProductKey"; i 4L ] ])
            yield!
                VertipaqServer.relationshipQueries relStats
                |> List.map (fun q -> q, result [ [ i 1L; i 1L; i 1L ] ])
        ]

    let unexpected = ResizeArray<string>()

    server.OnQuery <-
        fun q ->
            match answers.TryGetValue q with
            | true, r -> r
            | _ ->
                unexpected.Add q
                []

    server, answers, unexpected

[<Tests>]
let serverTests =
    testSequenced
    <| testList "vertipaq on a server" [
        testCase "exactly VertiPaq Analyzer's queries; numbers of any shape; DAX counts win over the DMVs"
        <| fun () ->
            let server, answers, unexpected = starServer ()

            let r =
                tessOn server tokenEnv [ "vertipaq"; $"xmla:{workspace};Shop"; "--all"; "--json" ]

            Expect.equal r.Code ExitCode.ok r.Err
            Expect.isEmpty unexpected "no other query is sent"

            Expect.equal
                (server.Queries |> List.map (fun (_, q, _) -> q) |> Set.ofList)
                (answers.Keys |> Set.ofSeq)
                "every query is sent"

            Expect.all server.Queries (fun (db, _, limit) -> db = "Shop" && limit.IsNone) "on the database, unlimited"
            Expect.equal server.Closed 1 "one connection, closed"
            let j = json r
            Expect.isFalse (j.GetProperty("estimated").GetBoolean()) "measured"

            let table name =
                j.GetProperty("tables").EnumerateArray()
                |> Seq.find (fun t -> t.GetProperty("table").GetString() = name)

            Expect.equal ((table "Sales").GetProperty("rows").GetInt64()) 7L "COUNTROWS, not the DMV's 6"
            Expect.equal ((table "Product").GetProperty("rows").GetInt64()) 4L "rows"

            let column t c =
                j.GetProperty("columns").EnumerateArray()
                |> Seq.find (fun x ->
                    x.GetProperty("table").GetString() = t
                    && x.GetProperty("column").GetString() = c)

            let pk = column "Sales" "ProductKey"
            Expect.equal (pk.GetProperty("cardinality").GetInt64()) 4L "DISTINCTCOUNT, not the hierarchy's 3"
            Expect.equal (pk.GetProperty("dictionarySize").GetInt64()) 120L "dictionary"
            Expect.equal (pk.GetProperty("dataSize").GetInt64()) 64L "two segments"
            Expect.equal (pk.GetProperty("hierarchySize").GetInt64()) 48L "hierarchy"
            Expect.equal (pk.GetProperty("encoding").GetString()) "VALUE" "encoding"
            let ppk = column "Product" "ProductKey"
            Expect.equal (ppk.GetProperty("cardinality").GetInt64()) 4L "the hierarchy's count where DAX said nothing"
            Expect.equal (ppk.GetProperty("dictionarySize").GetInt64()) 88L "a number sent as text"
            Expect.equal (ppk.GetProperty("encoding").GetString()) "HASH" "encoding"
            let name = column "Product" "Name"
            Expect.isFalse (has name "cardinality") "not counted: absent, not 0"
            Expect.isFalse (has name "dictionarySize") "no row in the DMV: not in storage, so unknown"

            let rel =
                j.GetProperty("relationships").EnumerateArray()
                |> Seq.find (fun x -> x.GetProperty("from").GetString() = "column:Sales/ProductKey")

            Expect.equal (rel.GetProperty("size").GetInt64()) 32L "R$ size, by the DMV ids"
            Expect.equal (rel.GetProperty("invalidRows").GetInt64()) 1L "RI from DAX"
            Expect.equal (rel.GetProperty("missingKeys").GetInt64()) 1L "missing keys"

            let other =
                j.GetProperty("relationships").EnumerateArray()
                |> Seq.find (fun x -> x.GetProperty("from").GetString() = "column:Sales/CustomerKey")

            Expect.isFalse (has other "invalidRows") "no answer for it: unknown"

            let part =
                j.GetProperty("partitions").EnumerateArray()
                |> Seq.find (fun x -> x.GetProperty("table").GetString() = "Sales")

            Expect.equal (part.GetProperty("segments").GetInt64()) 2L "segments"
            Expect.equal (part.GetProperty("rows").GetInt64()) 7L "rows in segments"

        testCase "a DMV that fails leaves its statistics unknown, with a warning; the rest is read"
        <| fun () ->
            let server, _, _ = starServer ()
            let answer = server.OnQuery

            server.OnQuery <-
                fun q ->
                    if q = VertipaqServer.columnsQuery then
                        raise (XmlaQueryError "Either the user does not have permission or the DMV doesn't exist.")
                    else
                        answer q

            let r =
                tessOn server tokenEnv [ "vertipaq"; $"xmla:{workspace};Shop"; "--columns"; "--json" ]

            Expect.equal r.Code ExitCode.ok r.Err
            let j = json r

            Expect.exists
                (j.GetProperty("warnings").EnumerateArray() |> Seq.map _.GetString())
                (fun w -> w.StartsWith "dictionary sizes are unknown")
                "warned"

            Expect.all
                (j.GetProperty("columns").EnumerateArray() |> List.ofSeq)
                (fun c -> not (has c "dictionarySize") && not (has c "size"))
                "no dictionary size, so no total"

            let pk =
                j.GetProperty("columns").EnumerateArray()
                |> Seq.find (fun x ->
                    x.GetProperty("column").GetString() = "ProductKey"
                    && x.GetProperty("table").GetString() = "Sales")

            Expect.equal (pk.GetProperty("cardinality").GetInt64()) 4L "DAX still counted"

        testCase "--data with a server model is refused before connecting"
        <| fun () ->
            let server, _, _ = starServer ()

            let r =
                tessOn server tokenEnv [ "vertipaq"; $"xmla:{workspace};Shop"; "--data"; "x" ]

            Expect.equal r.Code ExitCode.usage r.Err
            Expect.isEmpty server.Requests "nothing contacted"
    ]

// ---- The command --------------------------------------------------------------------------------

[<Tests>]
let commandTests =
    testList "CLI vertipaq" [
        testCase "the table view by default, largest first; estimated, said on stderr; no connection"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t
            let r = tess [ "vertipaq"; model; "--data"; dir ]
            Expect.equal r.Code ExitCode.ok r.Err
            let lines = r.Out.Split('\n') |> Array.filter ((<>) "")
            Expect.stringContains lines.[0] "(sizes estimated)" "the header says so"
            Expect.equal lines.[1] "Tables" "one view"
            Expect.stringStarts lines.[3] "Sales " "the largest table first"
            Expect.stringContains r.Err "sizes are estimates" "stderr explains"

        testCase "--json: the views asked for; unknown statistics are absent; --top"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t

            let r =
                tess [
                    "vertipaq"
                    model
                    "--data"
                    dir
                    "--columns"
                    "--relationships"
                    "--top"
                    "2"
                    "--json"
                ]

            Expect.equal r.Code ExitCode.ok r.Err
            let j = json r
            Expect.equal (j.GetProperty("command").GetString()) "vertipaq" "command"
            Expect.isTrue (j.GetProperty("estimated").GetBoolean()) "estimated"
            Expect.equal (j.GetProperty("origin").GetProperty("kind").GetString()) "fixture" "origin"
            Expect.isFalse (has j "tables") "only the views asked for"
            Expect.equal (j.GetProperty("columns").GetArrayLength()) 2 "--top 2"
            Expect.equal (j.GetProperty("relationships").GetArrayLength()) 2 "--top 2"
            let first = j.GetProperty("columns").[0]
            Expect.isFalse (has first "encoding") "unknown: absent"
            Expect.isTrue (has first "encodingHint") "known: present"

            Expect.isGreaterThanOrEqual
                (first.GetProperty("size").GetInt64())
                (j.GetProperty("columns").[1].GetProperty("size").GetInt64())
                "by size"

        testCase "tables named after the model filter every view; an unknown one gets suggestions"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t
            let r = tess [ "vertipaq"; model; "product"; "--data"; dir; "--all"; "--json" ]
            Expect.equal r.Code ExitCode.ok r.Err
            let j = json r
            Expect.equal (j.GetProperty("tables").GetArrayLength()) 1 "one table"

            Expect.all
                (j.GetProperty("columns").EnumerateArray() |> List.ofSeq)
                (fun c -> c.GetProperty("table").GetString() = "Product")
                "its columns"

            Expect.equal (j.GetProperty("relationships").GetArrayLength()) 1 "the relationship that ends at it"

            let r = tess [ "vertipaq"; model; "Prodcut"; "--data"; dir; "--json" ]
            Expect.equal r.Code ExitCode.input r.Err
            Expect.stringContains r.Out "table:Product" "did you mean"

        testCase "usage errors"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t

            for args, says in
                [
                    [ "vertipaq"; model ], "--data"
                    [ "vertipaq" ], "expected a model"
                    [ "vertipaq"; model; "--data"; dir; "--obfuscate" ], "--obfuscate is for --export"
                    [ "vertipaq"; model; "--data"; dir; "--save" ], "only apply with --annotate"
                    [ "vertipaq"; model; "--data"; dir; "--top"; "0" ], "--top"
                    [ "vertipaq"; model; "--data"; dir; "--import"; contoso ], "can't be combined"
                    [ "vertipaq"; "--import"; contoso; "--annotate" ], "needs the model"
                    [ "vertipaq"; model; "--data"; dir; "--annotate"; "--export"; "x.vpax" ], "can't be combined"
                ] do
                let r = tess args
                Expect.equal r.Code ExitCode.usage (String.concat " " args)
                Expect.stringContains r.Err says (String.concat " " args)

        testCase "--export writes a file, refuses to replace one without --force; --import reads it"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t
            let out = t.Combine "stats.vpax"
            let r = tess [ "vertipaq"; model; "--data"; dir; "--export"; out; "--json" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.equal ((json r).GetProperty("exported").GetProperty("path").GetString()) out "reported"
            Expect.isTrue (File.Exists out) "written"
            let before = File.ReadAllBytes out

            let r = tess [ "vertipaq"; model; "--data"; dir; "--export"; out ]
            Expect.equal r.Code ExitCode.output r.Err
            Expect.equal (File.ReadAllBytes out) before "not replaced"

            let r = tess [ "vertipaq"; model; "--data"; dir; "--export"; out; "--force" ]
            Expect.equal r.Code ExitCode.ok r.Err

            let r = tess [ "vertipaq"; "--import"; out; "--json" ]
            Expect.equal r.Code ExitCode.ok r.Err
            let j = json r
            Expect.isTrue (j.GetProperty("estimated").GetBoolean()) "still an estimate"
            Expect.equal (j.GetProperty("origin").GetProperty("kind").GetString()) "vpax" "origin"

        testCase "--obfuscate writes the .dict next to the file and won't replace it"
        <| fun () ->
            use t = new TempDir()
            let out = t.Combine "obf.vpax"
            let r = tess [ "vertipaq"; "--import"; contoso; "--export"; out; "--obfuscate" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.isTrue (File.Exists(t.Combine "obf.dict")) "the dictionary"
            Expect.stringContains r.Err "keep it private" "said"

            Expect.stringContains
                r.Err
                "reserved in DAX are not obfuscated: 'Currency', 'Date'"
                "what stays readable is said"

            File.Delete out
            let r = tess [ "vertipaq"; "--import"; contoso; "--export"; out; "--obfuscate" ]
            Expect.equal r.Code ExitCode.output "the old dictionary is in the way"
            Expect.isFalse (File.Exists out) "nothing written"

            let r =
                tess [
                    "vertipaq"
                    "--import"
                    contoso
                    "--export"
                    out
                    "--obfuscate"
                    "--dictionary"
                    t.Combine "obf.dict"
                    "--force"
                ]

            Expect.equal r.Code ExitCode.ok r.Err

        testCase "--annotate: a dry run, --save writes, a second run has nothing to do"
        <| fun () ->
            let t, model, dir = setup ()
            use _ = t
            let before = File.ReadAllText model
            let r = tess [ "vertipaq"; model; "--data"; dir; "--annotate"; "--stat" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Out "column:Sales/Qty" "the change"
            Expect.stringContains r.Err "Dry run" "a dry run"
            Expect.equal (File.ReadAllText model) before "nothing written"

            let r = tess [ "vertipaq"; model; "--data"; dir; "--annotate"; "--save" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains (File.ReadAllText model) "Vertipaq_Cardinality" "saved"

            let r = tess [ "vertipaq"; model; "--data"; dir; "--annotate"; "--json" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.equal ((json r).GetProperty("changes").GetArrayLength()) 0 "no changes"

        testCase
            "bpa --vpax: Microsoft's RI rule finds Contoso's violation; the model file is untouched; not with --fix"
        <| fun () ->
            use t = new TempDir()
            let model = t.Combine "Contoso.bim"
            let db = (readImported contoso).Database.Value
            File.WriteAllText(model, JsonSerializer.SerializeDatabase db)
            let before = File.ReadAllText model
            let rules = t.Combine "rules.json"

            // Three rules from microsoft/Analysis-Services BestPracticeRules/BPARules.json, as published.
            File.WriteAllText(
                rules,
                """[
  { "ID": "AVOID_BI-DIRECTIONAL_RELATIONSHIPS_AGAINST_HIGH-CARDINALITY_COLUMNS", "Name": "Avoid bi-directional relationships against high-cardinality columns", "Category": "Performance", "Severity": 2,
    "Scope": "DataColumn, CalculatedColumn, CalculatedTableColumn",
    "Expression": "UsedInRelationships.Any(CrossFilteringBehavior == CrossFilteringBehavior.BothDirections)\n\nand\n\nConvert.ToInt64(GetAnnotation(\"Vertipaq_Cardinality\")) > 100000", "CompatibilityLevel": 1200 },
  { "ID": "LARGE_TABLES_SHOULD_BE_PARTITIONED", "Name": "Large tables should be partitioned", "Category": "Performance", "Severity": 2, "Scope": "Table",
    "Expression": "Convert.ToInt64(GetAnnotation(\"Vertipaq_RowCount\")) > 25000000\nand\nPartitions.Count = 1", "CompatibilityLevel": 1200 },
  { "ID": "FIX_REFERENTIAL_INTEGRITY_VIOLATIONS", "Name": "Fix referential integrity violations", "Category": "Maintenance", "Severity": 2, "Scope": "Relationship",
    "Expression": "Convert.ToInt64(GetAnnotation(\"Vertipaq_RIViolationInvalidRows\")) > 0", "CompatibilityLevel": 1200 }
]"""
            )

            let run extra =
                tess ([ "bpa"; model; "-r"; rules; "--no-builtin"; "--no-config"; "--json" ] @ extra)

            let ids (r: Run) =
                (json r).GetProperty("findings").EnumerateArray()
                |> Seq.map (fun f -> f.GetProperty("code").GetString())
                |> List.ofSeq

            let without = run []
            Expect.equal without.Code ExitCode.ok without.Err
            Expect.isEmpty (ids without) "no statistics, no findings"

            let r = run [ "--vpax"; contoso ]
            Expect.equal (ids r) [ "FIX_REFERENTIAL_INTEGRITY_VIOLATIONS" ] r.Out
            Expect.isFalse (r.Err.Contains "not supported") "all three rules run"
            Expect.equal (File.ReadAllText model) before "the model file is untouched"

            let r = tess [ "bpa"; model; "--fix"; "--vpax"; contoso ]
            Expect.equal r.Code ExitCode.usage r.Err
    ]
