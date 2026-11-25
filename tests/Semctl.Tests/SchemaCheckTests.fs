module Semctl.Tests.SchemaCheckTests

open System
open System.IO
open System.Text.Json
open Expecto
open Argu
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.SchemaCheck
open Semctl.SchemaCheckSql
open Semctl.Tests.Fixtures
open Semctl.Tests.CliHarness
open Semctl.Cli.Common

// ---- A model with every kind of source -----------------------------------------------------------

let navigationM =
    "let\n    Source = Sql.Database(#\"Server\", \"Shop\"),\n    dbo_Sales = Source{[Schema=\"dbo\",Item=\"Sales\"]}[Data]\nin\n    dbo_Sales"

let private dataColumn (name: string) (source: string) (t: DataType) =
    new DataColumn(Name = name, SourceColumn = source, DataType = t)

/// Sales: an M partition over Sql.Database(parameter, "Shop") + one navigation step.
/// Product: a query partition on a SQL Server provider data source.
/// Shaped: M with an extra step (not exact). Calc: a calculated table.
let model () =
    let db = new Database(Name = "Shop", ID = "Shop", CompatibilityLevel = 1601)
    let m = new Model(Name = "Model", Culture = "en-US")
    db.Model <- m

    m.Expressions.Add(
        new NamedExpression(
            Name = "Server",
            Kind = ExpressionKind.M,
            Expression = "\"localhost\" meta [IsParameterQuery=true, Type=\"Text\", IsParameterQueryRequired=true]"
        )
    )

    let ds =
        new ProviderDataSource(
            Name = "Legacy",
            ConnectionString = "Provider=MSOLEDBSQL;Data Source=localhost;Initial Catalog=Shop;Integrated Security=SSPI"
        )

    m.DataSources.Add ds

    let sales = new Table(Name = "Sales", LineageTag = "t-sales")
    sales.Columns.Add(dataColumn "Order Id" "OrderId" DataType.Int64)
    sales.Columns.Add(dataColumn "Amount" "Amount" DataType.Double)
    sales.Columns.Add(dataColumn "Order Date" "[OrderDate]" DataType.DateTime)

    let old = dataColumn "Old" "Old" DataType.String

    old.Description <- "kept"
    sales.Columns.Add old

    sales.Columns.Add(
        new CalculatedColumn(Name = "Double", Expression = "'Sales'[Amount] * 2", DataType = DataType.Double)
    )

    sales.Partitions.Add(new Partition(Name = "Sales", Source = new MPartitionSource(Expression = navigationM)))

    m.Tables.Add sales

    let product = new Table(Name = "Product")
    let key = dataColumn "Key" "Key" DataType.Int64
    key.FormatString <- "0"
    key.Description <- "the key"
    product.Columns.Add key
    product.Columns.Add(dataColumn "Name" "Name" DataType.String)

    product.Partitions.Add(
        new Partition(
            Name = "Product",
            Source = new QueryPartitionSource(DataSource = ds, Query = "SELECT [Key], Name, Price FROM dbo.Product")
        )
    )

    m.Tables.Add product

    let shaped = new Table(Name = "Shaped")
    shaped.Columns.Add(dataColumn "A" "A" DataType.String)

    shaped.Partitions.Add(
        new Partition(
            Name = "Shaped",
            Source =
                new MPartitionSource(
                    Expression =
                        "let Source = Sql.Database(\"s\", \"Shop\"), T = Source{[Schema=\"dbo\",Item=\"T\"]}[Data], S = Table.SelectColumns(T, {\"A\"}) in S"
                )
        )
    )

    m.Tables.Add shaped

    let calc = new Table(Name = "Calc")
    calc.Partitions.Add(new Partition(Name = "Calc", Source = new CalculatedPartitionSource(Expression = "{ 1 }")))
    m.Tables.Add calc

    m.Tables.["Sales"].Measures.Add(new Measure(Name = "Olds", Expression = "COUNTROWS ( VALUES ( 'Sales'[Old] ) )"))
    db

let private col name (t: DataType option) = {
    Name = name
    SourceType = None
    DataType = t
}

/// A fixed schema per table name.
let private fixedSource (tables: (string * SourceColumn list) list) = {
    new ISchemaSource with
        member _.Name = "test"
        member _.MatchesByName = false

        member _.Describe(t: Table) =
            match tables |> List.tryFind (fun (n, _) -> n = t.Name) with
            | Some(_, columns) -> Described(columns, true, "test")
            | None -> NotChecked "not in the test source"
}

let private salesSchema = [
    col "OrderId" (Some DataType.Int64)
    col "Amount" (Some DataType.Decimal)
    col "orderdate" (Some DataType.DateTime)
    col "Region" (Some DataType.String)
]

let private run (db: Database) tables source =
    match check db source tables with
    | Ok r -> r
    | Error e -> failtestf "check failed: %s" (SemctlError.message e)

let private codes (r: Report) =
    r.Issues |> List.map (fun i -> i.Code, ObjectRef.format i.Ref) |> List.sort

/// A describer that answers from a map of query -> columns and records what it was asked.
type private FakeDescriber(answers: Map<string, Result<DescribedColumn list, string>>) =
    let asked = Collections.Generic.List<string>()
    member _.Asked = List.ofSeq asked
    member val Disposed = false with get, set

    interface ISqlDescriber with
        member _.Describe(q: string) =
            asked.Add q

            match Map.tryFind q answers with
            | Some a -> a
            | None -> Error $"no answer for {q}"

    interface IDisposable with
        member this.Dispose() = this.Disposed <- true

let private dc name typeName = {
    Name = Some name
    SystemTypeName = typeName
}

let private salesQuery = "SELECT * FROM [dbo].[Sales]"
let private productQuery = "SELECT [Key], Name, Price FROM dbo.Product"

let private sqlAnswers =
    Map.ofList [
        salesQuery,
        Ok [
            dc "OrderId" "int"
            dc "Amount" "decimal(18,2)"
            dc "OrderDate" "datetime2(7)"
            dc "Old" "nvarchar(20)"
        ]
        productQuery, Ok [ dc "Key" "int"; dc "Name" "nvarchar(50)"; dc "Price" "money" ]
    ]

let private address = {
    Server = "localhost,14333"
    Database = "Shop"
}

// ---- CLI helpers -----------------------------------------------------------------------------------

let private setup () =
    let t = new TempDir()
    let path = t.Combine "model.bim"
    File.WriteAllText(path, JsonSerializer.SerializeDatabase(model ()))
    t, path

let private writeSchema (t: TempDir) (text: string) =
    let path = t.Combine "schema.json"
    File.WriteAllText(path, text)
    path

let private schemaText =
    """{ "tables": {
  // comments are fine
  "Sales": [ { "name": "OrderId", "type": "int64" }, { "name": "Amount", "type": "decimal", "sourceType": "decimal(18,2)" },
             { "name": "OrderDate", "type": "dateTime" },
             { "name": "Region", "type": "string", "sourceType": "nvarchar(20)" } ],
  "product": [ "Key", "Name" ],
} }"""

/// schemaText, with Sales's Old column still at the source.
let private fullSchemaText =
    schemaText.Replace(
        """{ "name": "OrderDate", "type": "dateTime" },""",
        """{ "name": "OrderDate", "type": "dateTime" }, "Old","""
    )

let private parser =
    lazy (ArgumentParser.Create<Semctl.Cli.SchemaCheckCommand.Args>())

/// `sm schemacheck <args>` with SQL connections going to `connect`.
let private runSql (connect: SqlAddress -> ISqlDescriber) (args: string list) =
    use out = new StringWriter()
    use err = new StringWriter()

    let ctx: Ctx = {
        Out = out
        Err = err
        Json = false
        GetEnv = fun _ -> None
        Connector = XmlaFakes.NoConnections()
    }

    let parsed = parser.Value.ParseCommandLine(Array.ofList args)

    let code =
        match Semctl.Cli.SchemaCheckCommand.runWith connect ctx parsed with
        | Done -> 0
        | DoneWith c -> c
        | Failed e -> ExitCode.ofError e
        | UsageError _
        | UsageErrorSuggest _ -> ExitCode.usage

    code, out.ToString().Replace("\r\n", "\n"), err.ToString()

let private lines (s: string) =
    s.Split('\n')
    |> Array.map (fun l -> l.TrimEnd '\r')
    |> Array.filter ((<>) "")
    |> List.ofArray

[<Tests>]
let tests =
    testList "schemacheck" [
        testList "M recognition" [
            let parameters =
                function
                | "Server" -> Some "srv"
                | "Db" -> Some "Shop"
                | _ -> None

            testCase "Sql.Database + one navigation step, with parameters"
            <| fun () ->
                match recogniseM parameters navigationM with
                | Ok s ->
                    Expect.equal s.Query salesQuery ""
                    Expect.equal s.Server (Some "srv") ""
                    Expect.equal s.Database (Some "Shop") ""
                | Error e -> failtest e

            testCase "Item before Schema, comments, quoted step names, ] in names"
            <| fun () ->
                let m =
                    "// navigation\nlet /* x */ #\"My Source\" = Sql.Database(\"s\", #\"Db\"),\n #\"Nav\" = #\"My Source\"{[Item=\"T]1\", Schema=\"s q\"]}[Data] in #\"Nav\""

                match recogniseM parameters m with
                | Ok s -> Expect.equal s.Query "SELECT * FROM [s q].[T]]1]" ""
                | Error e -> failtest e

            testCase "the Query option"
            <| fun () ->
                let m =
                    "let Source = Sql.Database(\"s\", \"d\", [Query=\"select 1 as \"\"x\"\"\"]) in Source"

                match recogniseM parameters m with
                | Ok s ->
                    Expect.equal s.Query "select 1 as \"x\"" ""
                    Expect.equal s.Database (Some "d") ""
                | Error e -> failtest e

            testCase "anything else is not recognised"
            <| fun () ->
                for m in
                    [
                        "let Source = Sql.Database(\"s\", \"d\"), T = Source{[Schema=\"dbo\",Item=\"T\"]}[Data], R = Table.RemoveColumns(T, {\"A\"}) in R"
                        "let Source = Sql.Database(\"s\", \"d\", [CommandTimeout=#duration(0,1,0,0)]), T = Source{[Schema=\"dbo\",Item=\"T\"]}[Data] in T"
                        "let Source = Sql.Database(\"s\", \"d\"), T = Source{[Schema=\"dbo\",Item=\"T\"]}[Data] in Source"
                        "let Source = Sql.Databases(\"s\"), T = Source{[Name=\"d\"]}[Data] in T"
                        "let Source = Sql.Database(\"s\", \"d\", [Query=\"select #(lf) 1\"]) in Source"
                        "let Source = Lakehouse.Contents(null) in Source"
                        "let Source = Sql.Database(Unknown, \"d\"), T = Source{[Schema=\"dbo\",Item=\"T\"]}[Data] in T"
                        "Sql.Database(\"s\", \"d\")"
                    ] do
                    Expect.isError (recogniseM parameters m) m

            testCase "a parameter is a text literal, with or without meta"
            <| fun () ->
                Expect.equal (parameterValue "\"x\"") (Some "x") ""
                Expect.equal (parameterValue "\"x\" meta [IsParameterQuery=true, Type=\"Text\"]") (Some "x") ""
                Expect.equal (parameterValue "#date(2020, 1, 1) meta [IsParameterQuery=true]") None ""
                Expect.equal (parameterValue "let a = \"x\" in a") None ""
        ]

        testList "types and addresses" [
            testCase "SQL type names map without guessing"
            <| fun () ->
                Expect.equal (sqlBaseType " NVarChar(50) ") "nvarchar" ""
                Expect.equal (legacySqlType "decimal(18,2)") (Some DataType.Decimal) "TE2 maps decimal to decimal"
                Expect.equal (legacySqlType "datetime2(7)") (Some DataType.DateTime) ""
                Expect.equal (legacySqlType "uniqueidentifier") None "TE2 guesses string; sm doesn't compare"
                Expect.equal (legacySqlType "hierarchyid") None ""
                Expect.equal (mSqlType "decimal(18,2)") None "Power Query's decimal mapping isn't documented"
                Expect.equal (mSqlType "money") None ""
                Expect.equal (mSqlType "int") (Some DataType.Int64) ""
                Expect.equal (mSqlType "nvarchar(max)") (Some DataType.String) ""

            testCase "sql: addresses"
            <| fun () ->
                Expect.equal
                    (SqlAddress.parse "sql:tcp:x.database.windows.net,1433; My DB")
                    (Ok {
                        Server = "tcp:x.database.windows.net,1433"
                        Database = "My DB"
                    })
                    ""

                for bad in [ "sql:server"; "sql:;db"; "sql:s;"; "sql:Server=s;Database=d;Password=p" ] do
                    Expect.isError (SqlAddress.parse bad) bad

            testCase "cloud hosts take Entra credentials; others never do"
            <| fun () ->
                let env (vars: (string * string) list) : Semctl.Xmla.Credentials.Environment = {
                    GetEnv = fun n -> vars |> List.tryFind (fun (k, _) -> k = n) |> Option.map snd
                    ReadSecret = fun _ -> Error "no files"
                }

                let resolve vars server =
                    Semctl.Xmla.SqlCredentials.resolve (env vars) { Server = server; Database = "d" }
                    |> Result.map Semctl.Xmla.SqlCredentials.describe

                let token = [ "SM_SQL_TOKEN", "t" ]
                let spn = [ "AZURE_CLIENT_ID", "c"; "AZURE_TENANT_ID", "t"; "AZURE_CLIENT_SECRET", "s" ]
                let login = [ "SM_SQL_USER", "u"; "SM_SQL_PASSWORD", "p" ]

                Expect.equal (resolve token "x.database.windows.net") (Ok "access token") ""

                Expect.equal
                    (resolve spn "tcp:x.datawarehouse.fabric.microsoft.com,1433")
                    (Ok "service principal c in tenant t")
                    ""

                Expect.equal (resolve login "x.database.windows.net") (Ok "SQL login u") ""
                Expect.isError (resolve [] "x.database.windows.net") "no credential for a cloud host"

                Expect.equal
                    (resolve (token @ spn) "localhost")
                    (Ok "integrated security")
                    "a token isn't sent to localhost"

                Expect.equal (resolve login "localhost") (Ok "SQL login u") ""
                Expect.isError (resolve (("SM_SQL_AUTH", "token") :: token) "localhost") ""
                Expect.isError (resolve [ "SM_SQL_USER", "u" ] "localhost") "a user without a password"
                Expect.isError (resolve [ "SM_SQL_AUTH", "bogus" ] "localhost") ""
        ]

        testList "comparing" [
            testCase "added, missing and retyped columns; sourceColumn matched ignoring case and [brackets]"
            <| fun () ->
                let r = run (model ()) [ "Sales" ] (fixedSource [ "Sales", salesSchema ])

                Expect.equal
                    (codes r)
                    (List.sort [
                        Codes.added, "table:Sales"
                        Codes.typeChanged, "column:Sales/Amount"
                        Codes.missing, "column:Sales/Old"
                    ])
                    "the calculated column is never missing"

                let t = r.Tables |> List.exactlyOne
                Expect.equal t.Status TableStatus.Checked ""
                Expect.isTrue (TableResult.hasDrift t) ""

                let sev = r.Issues |> List.map (fun i -> i.Code, i.Severity) |> Map.ofList

                Expect.equal sev.[Codes.added] Severity.Warning ""
                Expect.equal sev.[Codes.typeChanged] Severity.Warning ""
                Expect.equal sev.[Codes.missing] Severity.Error ""

            testCase "a repeated source column is reported once, and the first is used"
            <| fun () ->
                let r =
                    run
                        (model ())
                        [ "Product" ]
                        (fixedSource [
                            "Product",
                            [
                                col "Key" (Some DataType.Int64)
                                col "KEY" (Some DataType.String)
                                col "key" None
                                col "Name" None
                            ]
                        ])

                Expect.equal (codes r) [ Codes.duplicate, "table:Product" ] "no type change from the second Key"

            testCase "TE2's annotations: on unless 0, false or no"
            <| fun () ->
                let withAnnotation (o: Choice<Table, Column>) name value =
                    let a = new Annotation(Name = name, Value = value)

                    match o with
                    | Choice1Of2 t -> t.Annotations.Add a
                    | Choice2Of2 c -> c.Annotations.Add a

                let checkWith edit =
                    let db = model ()
                    edit db.Model.Tables.["Sales"]

                    run db [ "Sales" ] (fixedSource [ "Sales", salesSchema ])
                    |> codes
                    |> List.map fst
                    |> Set.ofList

                Expect.equal
                    (checkWith (fun t -> withAnnotation (Choice1Of2 t) Annotations.ignoreAdded "1"))
                    (Set.ofList [ Codes.typeChanged; Codes.missing ])
                    ""

                Expect.equal
                    (checkWith (fun t -> withAnnotation (Choice1Of2 t) Annotations.ignoreTypes "true"))
                    (Set.ofList [ Codes.added; Codes.missing ])
                    "ignoring types doesn't make every column missing (as it does in TE2)"

                Expect.equal
                    (checkWith (fun t -> withAnnotation (Choice2Of2 t.Columns.["Amount"]) Annotations.ignoreTypes "yes"))
                    (Set.ofList [ Codes.added; Codes.missing ])
                    ""

                Expect.equal
                    (checkWith (fun t -> withAnnotation (Choice1Of2 t) Annotations.ignoreMissing "x"))
                    (Set.ofList [ Codes.added; Codes.typeChanged ])
                    ""

                Expect.equal
                    (checkWith (fun t -> withAnnotation (Choice2Of2 t.Columns.["Old"]) Annotations.ignoreMissing "1"))
                    (Set.ofList [ Codes.added; Codes.typeChanged ])
                    ""

                for off in [ "0"; "false"; "No" ] do
                    Expect.equal
                        (checkWith (fun t -> withAnnotation (Choice1Of2 t) Annotations.ignoreAdded off))
                        (Set.ofList [ Codes.added; Codes.typeChanged; Codes.missing ])
                        off

                for skip in [ Annotations.skip; Annotations.skipTypo ] do
                    let db = model ()
                    withAnnotation (Choice1Of2 db.Model.Tables.["Sales"]) skip "1"
                    let r = run db [ "Sales" ] (fixedSource [ "Sales", salesSchema ])
                    Expect.equal (codes r) [ Codes.skipped, "table:Sales" ] skip
                    Expect.equal (r.Tables.[0].Status) TableStatus.Skipped ""

            testCase "the model's SchemaCheckNoWarnings makes the warnings errors"
            <| fun () ->
                let db = model ()
                db.Model.Annotations.Add(new Annotation(Name = Annotations.noWarnings, Value = "1"))
                let r = run db [ "Sales" ] (fixedSource [ "Sales", salesSchema ])
                Expect.all r.Issues (fun i -> i.Severity = Severity.Error) ""

            testCase "tables: unnamed tables without a source are left out; a named one is reported"
            <| fun () ->
                let r = run (model ()) [] (fixedSource [ "Sales", salesSchema ])
                Expect.equal (r.Tables |> List.map (fun t -> t.Table)) [ "Sales"; "Product"; "Shaped" ] "no Calc"
                let named = run (model ()) [ "calc" ] (fixedSource [])
                Expect.equal (named.Tables.[0].Reason) (Some "a calculated table") ""

                match check (model ()) (fixedSource []) [ "Sale" ] with
                | Error(ObjectNotFoundSuggest(_, candidates)) -> Expect.contains candidates "Sales" ""
                | other -> failtestf "%A" other
        ]

        testList "SQL source (fake connection)" [
            testCase "M navigation and legacy query partitions are described; others skipped, untouched"
            <| fun () ->
                let fake = new FakeDescriber(sqlAnswers)
                let r = run (model ()) [] (source address (model ()) fake)
                Expect.equal fake.Asked [ salesQuery; productQuery ] "only the recognised tables are asked about"

                // Sales matches (decimal isn't compared on an M partition); Product's query has Price.
                Expect.equal (codes r) [ Codes.added, "table:Product"; Codes.skipped, "table:Shaped" ] ""
                Expect.equal r.Tables.[0].Origin (Some salesQuery) ""
                Expect.isTrue r.Tables.[0].TypesChecked ""
                Expect.stringContains r.Issues.[0].Message "'Price' (money)" ""

            testCase "query errors are findings; a failed connection fails the whole check"
            <| fun () ->
                let fake =
                    new FakeDescriber(Map.ofList [ salesQuery, Error "Invalid object name 'dbo.Sales'." ])

                let r = run (model ()) [ "Sales" ] (source address (model ()) fake)
                Expect.equal (codes r) [ Codes.queryError, "table:Sales" ] ""
                Expect.stringContains r.Issues.[0].Message "Invalid object name" ""
                Expect.equal r.Tables.[0].Status TableStatus.Failed ""

                let failing = {
                    new ISqlDescriber with
                        member _.Describe _ =
                            raise (SchemaSourceFailed(ConnectionFailed("localhost", "refused", None)))
                    interface IDisposable with
                        member _.Dispose() = ()
                }

                match check (model ()) (source address (model ()) failing) [] with
                | Error(ConnectionFailed _) -> ()
                | other -> failtestf "%A" other

            testCase "a partition naming another database is skipped; an unnamed column is an error"
            <| fun () ->
                let other = { address with Database = "Other" }
                let fake = new FakeDescriber(sqlAnswers)
                let r = run (model ()) [ "Sales"; "Product" ] (source other (model ()) fake)
                Expect.equal (r.Tables |> List.map (fun t -> t.Status)) [ TableStatus.Skipped; TableStatus.Skipped ] ""
                Expect.isEmpty fake.Asked ""

                let unnamed =
                    new FakeDescriber(Map.ofList [ salesQuery, Ok [ { Name = None; SystemTypeName = "int" } ] ])

                let r = run (model ()) [ "Sales" ] (source address (model ()) unnamed)
                Expect.equal (codes r) [ Codes.queryError, "table:Sales" ] ""
        ]

        testList "update" [
            testCase "adds columns, changes types in place, keeps removed columns unless asked"
            <| fun () ->
                let db = model ()

                let r =
                    run
                        db
                        []
                        (fixedSource [
                            "Sales", salesSchema
                            "Product", [ col "Key" (Some DataType.String); col "Name" None; col "Price" None ]
                        ])

                match update db r false with
                | Error e -> failtest (SemctlError.message e)
                | Ok effect ->
                    let sales = db.Model.Tables.["Sales"]
                    let region = sales.Columns.["Region"] :?> DataColumn
                    Expect.equal region.SourceColumn "Region" ""
                    Expect.equal region.DataType DataType.String ""
                    Expect.isFalse (String.IsNullOrEmpty region.LineageTag) "the table has lineage tags"

                    Expect.equal (region.Annotations.Find "SummarizationSetBy").Value "Automatic" "as `sm add` makes it"

                    let price = db.Model.Tables.["Product"]
                    Expect.isTrue (String.IsNullOrEmpty price.LineageTag) "Product has no lineage tag"
                    Expect.equal sales.Columns.["Amount"].DataType DataType.Decimal ""
                    Expect.isNotNull (sales.Columns.Find "Old") "kept"
                    let key = db.Model.Tables.["Product"].Columns.["Key"]
                    Expect.equal key.DataType DataType.String ""
                    Expect.equal key.Description "the key" "metadata kept"
                    Expect.equal key.FormatString "0" ""
                    Expect.isNull (db.Model.Tables.["Product"].Columns.Find "Price") "no type, not added"
                    Expect.exists effect.Warnings (fun w -> w.Contains "'Price' was not added") ""
                    Expect.exists effect.Warnings (fun w -> w.Contains "--drop-removed-columns") ""

            testCase "--drop-removed-columns removes what nothing uses and keeps what DAX uses"
            <| fun () ->
                let db = model ()
                let schema = salesSchema |> List.filter (fun c -> c.Name <> "Amount")
                let r = run db [ "Sales" ] (fixedSource [ "Sales", schema ])

                match update db r true with
                | Error e -> failtest (SemctlError.message e)
                | Ok effect ->
                    let sales = db.Model.Tables.["Sales"]
                    Expect.isNotNull (sales.Columns.Find "Old") "a measure uses it"
                    Expect.exists effect.Warnings (fun w -> w.Contains "column:Sales/Old" && w.Contains "kept") ""
                    // Amount is used by the calculated column, so it stays too.
                    Expect.isNotNull (sales.Columns.Find "Amount") ""
                    let db = model ()
                    db.Model.Tables.["Sales"].Measures.Clear()
                    let r = run db [ "Sales" ] (fixedSource [ "Sales", salesSchema ])

                    match update db r true with
                    | Ok effect ->
                        Expect.isNull (db.Model.Tables.["Sales"].Columns.Find "Old") ""
                        Expect.exists effect.Notes (fun n -> n.StartsWith "Removed column:Sales/Old") ""
                    | Error e -> failtest (SemctlError.message e)
        ]

        testList "CLI" [
            testCase "a schema file: findings, summary line, exit 1 on errors"
            <| fun () ->
                let t, path = setup ()
                use _ = t
                let schema = writeSchema t schemaText
                let r = semctl [ "schemacheck"; path; "--source"; schema ]
                Expect.equal r.Code ExitCode.findings r.Err
                let out = lines r.Out
                Expect.exists out (fun l -> l.Contains "table:Sales" && l.Contains "'Region' (nvarchar(20))") ""

                Expect.exists
                    out
                    (fun l -> l.Contains "column:Sales/Amount" && l.Contains "SCHEMA_DATA_TYPE_CHANGED")
                    ""

                Expect.exists out (fun l -> l.Contains "table:Shaped" && l.Contains "no entry") ""

                Expect.stringStarts
                    (List.last out)
                    "Checked 2 tables against schema file"
                    "Sales and Product; Product names only"

                Expect.stringContains (List.last out) "data types not checked for 1 table" ""

            testCase "--json: the findings envelope with schemacheck's keys"
            <| fun () ->
                let t, path = setup ()
                use _ = t
                let schema = writeSchema t schemaText

                let r =
                    semctl [ "--json"; "schemacheck"; path; "Sales"; "Product"; "--source"; schema ]

                Expect.equal r.Code ExitCode.findings r.Err
                use doc = JsonDocument.Parse r.Out
                let root = doc.RootElement
                Expect.equal (root.GetProperty("command").GetString()) "schemacheck" ""
                Expect.equal (root.GetProperty("tablesChecked").GetInt32()) 2 ""
                Expect.equal (root.GetProperty("tablesSkipped").GetInt32()) 0 ""
                Expect.equal (root.GetProperty("tablesWithDrift").GetInt32()) 1 ""

                let f =
                    root.GetProperty("findings").EnumerateArray()
                    |> Seq.find (fun f -> f.GetProperty("code").GetString() = Codes.added)

                Expect.equal (f.GetProperty("source").GetString()) "schemaCheck" ""
                Expect.equal (f.GetProperty("objectType").GetString()) "Table" ""
                Expect.equal (f.GetProperty("severity").GetString()) "warning" ""
                Expect.isFalse (f.GetProperty("fixable").GetBoolean()) ""
                Expect.isFalse (r.Out.Contains "null") "no null keys"
                let tables = root.GetProperty("tables")
                Expect.isFalse (tables.[1].GetProperty("typesChecked").GetBoolean()) "Product's entry has no types"

            testCase "--fail-on warning, --ci github, --trx and --sarif"
            <| fun () ->
                let t, path = setup ()
                use _ = t

                let schema =
                    writeSchema
                        t
                        """{ "tables": { "Product": [ {"name":"Key","type":"int64"}, {"name":"Name","type":"string"}, "Price" ] } }"""

                let trx = t.Combine "out.trx"
                let sarif = t.Combine "out.sarif"
                let clean = semctl [ "schemacheck"; path; "Product"; "--source"; schema ]
                Expect.equal clean.Code ExitCode.ok "a warning only"

                let r =
                    semctl [
                        "schemacheck"
                        path
                        "Product"
                        "--source"
                        schema
                        "--fail-on"
                        "warning"
                        "--ci"
                        "github"
                        "--trx"
                        trx
                        "--sarif"
                        sarif
                    ]

                Expect.equal r.Code ExitCode.findings r.Err

                Expect.stringContains
                    r.Err
                    "::warning title=SCHEMA_SOURCE_COLUMN_ADDED::table:Product: Column 'Price'"
                    ""

                let trxText = File.ReadAllText trx
                Expect.stringContains trxText "outcome=\"Warning\"" ""
                Expect.stringContains trxText "executor://semctl/schemacheck" ""
                Expect.stringContains (File.ReadAllText sarif) "SCHEMA_SOURCE_COLUMN_ADDED" ""

            testCase "a fixture directory compares names only, by model column name"
            <| fun () ->
                let t, path = setup ()
                use _ = t
                let dir = t.Combine "data"
                write (Path.Combine(dir, "Sales.csv")) "Order Id,Amount,Order Date,Double,Extra\n1,2,2024-01-01,4,x\n"
                write (Path.Combine(dir, "Product.json")) """[ { "Key": 1 }, { "Name": "a" } ]"""
                let r = semctl [ "--json"; "schemacheck"; path; "--source"; dir ]
                Expect.equal r.Code ExitCode.findings r.Err
                use doc = JsonDocument.Parse r.Out

                let found = [
                    for f in doc.RootElement.GetProperty("findings").EnumerateArray() ->
                        f.GetProperty("code").GetString(), f.GetProperty("objectPath").GetString()
                ]

                Expect.equal
                    (found |> List.sort)
                    (List.sort [
                        Codes.added, "table:Sales"
                        Codes.missing, "column:Sales/Old"
                        Codes.skipped, "table:Shaped"
                    ])
                    "Double is a calculated column: neither added nor missing"

            testCase "--update: a dry run by default, --save writes, and the model then checks clean"
            <| fun () ->
                let t, path = setup ()
                use _ = t
                let schema = writeSchema t fullSchemaText
                let before = File.ReadAllText path
                let dry = semctl [ "schemacheck"; path; "Sales"; "--source"; schema; "--update" ]
                Expect.equal dry.Code ExitCode.ok dry.Err
                Expect.stringContains dry.Out "Region" "the diff shows the new column"
                Expect.stringContains dry.Err "SCHEMA_SOURCE_COLUMN_ADDED" "the findings go to stderr"
                Expect.equal (File.ReadAllText path) before "nothing written"

                let saved =
                    semctl [ "schemacheck"; path; "Sales"; "--source"; schema; "--update"; "--save" ]

                Expect.equal saved.Code ExitCode.ok saved.Err
                let again = semctl [ "--json"; "schemacheck"; path; "Sales"; "--source"; schema ]
                Expect.equal again.Code ExitCode.ok again.Out
                use doc = JsonDocument.Parse again.Out
                Expect.equal (doc.RootElement.GetProperty("tablesWithDrift").GetInt32()) 0 again.Out

            testCase "usage and input errors"
            <| fun () ->
                let t, path = setup ()
                use _ = t
                let schema = writeSchema t schemaText

                let code args =
                    (semctl ([ "schemacheck"; path ] @ args)).Code

                Expect.equal (code []) ExitCode.usage "--source is required"
                Expect.equal (code [ "--source"; schema; "--save" ]) ExitCode.usage "--save needs --update"
                Expect.equal (code [ "--source"; schema; "--drop-removed-columns" ]) ExitCode.usage ""
                Expect.equal (code [ "--source"; schema; "--update"; "--trx"; "x.trx" ]) ExitCode.usage ""
                Expect.equal (code [ "--source"; schema; "--fail-on"; "bad" ]) ExitCode.usage ""
                Expect.equal (code [ "--source"; t.Combine "nope.json" ]) ExitCode.input ""
                Expect.equal (code [ "--source"; "sql:only-a-server" ]) ExitCode.input "refused before connecting"
                Expect.equal (code [ "Nope"; "--source"; schema ]) ExitCode.input ""

                let bad =
                    writeSchema t """{ "tables": { "Sales": [ { "name": "A", "type": "number" } ] } }"""

                let r = semctl [ "--json"; "schemacheck"; path; "--source"; bad ]
                Expect.equal r.Code ExitCode.input ""
                Expect.stringContains r.Out "invalid-schema-source" ""
                Expect.stringContains r.Out "unknown type 'number'" ""

            testCase "set --update-schema: the same update for one table"
            <| fun () ->
                let t, path = setup ()
                use _ = t
                let schema = writeSchema t fullSchemaText

                let saved =
                    semctl [ "set"; path; "Sales"; "--update-schema"; "--source"; schema; "--save" ]

                Expect.equal saved.Code ExitCode.ok saved.Err
                let db = Semctl.ModelIO.loadPath path |> Result.map snd

                match db with
                | Ok db ->
                    Expect.isNotNull (db.Model.Tables.["Sales"].Columns.Find "Region") ""
                    Expect.equal db.Model.Tables.["Sales"].Columns.["Amount"].DataType DataType.Decimal ""
                | Error e -> failtest (SemctlError.message e)

                Expect.equal (semctl [ "set"; path; "Sales"; "--update-schema" ]).Code ExitCode.usage "needs --source"

                Expect.equal
                    (semctl [
                        "set"
                        path
                        "Sales"
                        "--update-schema"
                        "--source"
                        schema
                        "-p"
                        "isHidden=true"
                    ])
                        .Code
                    ExitCode.usage
                    ""

                Expect.equal (semctl [ "set"; path; "Sales"; "--source"; schema ]).Code ExitCode.usage "--source alone"

                Expect.equal
                    (semctl [ "set"; path; "[Olds]"; "--update-schema"; "--source"; schema ]).Code
                    ExitCode.usage
                    "a measure has no schema"

            testCase "the sample model (SEMCTL_SAMPLE_MODEL): every table has M steps beyond navigation, so all skip"
            <| fun () ->
                match Environment.GetEnvironmentVariable "SEMCTL_SAMPLE_MODEL" with
                | null
                | "" -> skiptest "SEMCTL_SAMPLE_MODEL is not set"
                | sample ->
                    // A SQL connection is opened only for a table sm can describe; here there is none,
                    // so this runs (and passes) with no server at all.
                    let r =
                        semctl [ "--json"; "schemacheck"; sample; "--source"; "sql:localhost;Database" ]

                    Expect.equal r.Code ExitCode.ok r.Err
                    use doc = JsonDocument.Parse r.Out
                    Expect.equal (doc.RootElement.GetProperty("tablesChecked").GetInt32()) 0 ""
                    Expect.isGreaterThan (doc.RootElement.GetProperty("tablesSkipped").GetInt32()) 0 ""

                    for f in doc.RootElement.GetProperty("findings").EnumerateArray() do
                        Expect.equal (f.GetProperty("code").GetString()) Codes.skipped ""

            testCase "a sql: source with no SQL tables never connects"
            <| fun () ->
                let t, path = setup ()
                use _ = t
                // Through the real entry point: Shaped isn't SQL-readable, so nothing is asked.
                let r = semctl [ "schemacheck"; path; "Shaped"; "--source"; "sql:localhost;Shop" ]
                Expect.equal r.Code ExitCode.ok r.Err
                Expect.stringContains r.Out "SCHEMA_TABLE_SKIPPED" ""

            testCase "a sql: source through a fake connection, closed afterwards"
            <| fun () ->
                let t, path = setup ()
                use _ = t
                let fake = new FakeDescriber(sqlAnswers)

                let code, out, _ =
                    runSql (fun _ -> fake) [ path; "--source"; "sql:localhost,14333;Shop" ]

                Expect.equal code ExitCode.ok out
                Expect.stringContains out "table:Product" ""
                Expect.stringContains out "Checked 2 tables against sql:localhost,14333;Shop" ""
                Expect.isTrue fake.Disposed ""
        ]
    ]
