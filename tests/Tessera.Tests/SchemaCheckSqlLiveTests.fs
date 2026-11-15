/// schemacheck against a real SQL Server: only when TESSERA_SQL_TEST_SERVER names one (a local
/// container, e.g. `docker run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=... -p 127.0.0.1:14333:1433
/// mcr.microsoft.com/mssql/server:2022-latest`), with TESS_SQL_USER / TESS_SQL_PASSWORD and
/// TESS_SQL_TRUST_SERVER_CERTIFICATE=1 for its self-signed certificate. The test creates a
/// database `tess_schemacheck` and tables in it. Skipped otherwise.
module Tessera.Tests.SchemaCheckSqlLiveTests

open System
open Expecto
open Microsoft.AnalysisServices.Tabular
open Tessera
open Tessera.SchemaCheck
open Tessera.SchemaCheckSql

let private server () =
    match Environment.GetEnvironmentVariable "TESSERA_SQL_TEST_SERVER" with
    | null
    | "" -> None
    | s -> Some s

let private env: Tessera.Xmla.Credentials.Environment = {
    GetEnv = Tessera.Cli.Common.processEnv
    ReadSecret = Tessera.Xmla.Credentials.readSecret Console.In
}

let private database = "tess_schemacheck"

/// Set-up SQL (tess itself only describes queries, never runs them), straight through SqlClient.
let private execute (server: string) (db: string) (sql: string) =
    let user = env.GetEnv "TESS_SQL_USER" |> Option.defaultValue ""
    let password = env.GetEnv "TESS_SQL_PASSWORD" |> Option.defaultValue ""

    let b =
        Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
            DataSource = server,
            InitialCatalog = db,
            UserID = user,
            Password = password,
            TrustServerCertificate = true,
            Encrypt = Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory
        )

    use c = new Microsoft.Data.SqlClient.SqlConnection(b.ConnectionString)
    c.Open()
    use cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, c)
    cmd.ExecuteNonQuery() |> ignore

/// SchemaCheckTests' model, its Sales table reading dbo.Sales in the test database.
let private liveModel () =
    let db = SchemaCheckTests.model ()

    (db.Model.Tables.["Sales"].Partitions.[0].Source :?> MPartitionSource).Expression <-
        $"let Source = Sql.Database(\"x\", \"{database}\"), T = Source{{[Schema=\"dbo\",Item=\"Sales\"]}}[Data] in T"

    db

[<Tests>]
let tests =
    testList "schemacheck live SQL" [
        testCase "a real SQL Server: describe, compare, report query errors"
        <| fun () ->
            match server () with
            | None -> skiptest "TESSERA_SQL_TEST_SERVER isn't set"
            | Some server ->
                execute server "master" $"IF DB_ID('{database}') IS NULL CREATE DATABASE {database}"

                execute
                    server
                    database
                    "DROP TABLE IF EXISTS dbo.Sales; DROP TABLE IF EXISTS [we]]ird].[T]]1];
                     IF SCHEMA_ID('we]ird') IS NULL EXEC('CREATE SCHEMA [we]]ird]');
                     CREATE TABLE dbo.Sales (OrderId int NOT NULL, Amount decimal(18,2), OrderDate datetime2, Region nvarchar(20), Qty bigint);
                     CREATE TABLE [we]]ird].[T]]1] (A int, B uniqueidentifier);"

                let db = liveModel ()
                let weird = new Table(Name = "Weird")
                weird.Columns.Add(new DataColumn(Name = "A", SourceColumn = "A", DataType = DataType.Int64))

                weird.Partitions.Add(
                    new Partition(
                        Name = "Weird",
                        Source =
                            new MPartitionSource(
                                Expression =
                                    $"let S = Sql.Database(\"x\", \"{database}\"), T = S{{[Schema=\"we]ird\",Item=\"T]1\"]}}[Data] in T"
                            )
                    )
                )

                db.Model.Tables.Add weird
                let address = { Server = server; Database = database }
                use describer = Tessera.Xmla.LiveSql.connect env address

                match check db (source address db describer) [ "Sales"; "Weird"; "Product" ] with
                | Error e -> failtest (TessError.message e)
                | Ok r ->
                    let found =
                        r.Issues |> List.map (fun i -> i.Code, ObjectRef.format i.Ref) |> List.sort

                    Expect.equal
                        found
                        (List.sort [
                            // Sales: Region and Qty are new; Old is gone; decimal isn't compared on M.
                            Codes.added, "table:Sales"
                            Codes.added, "table:Sales"
                            Codes.missing, "column:Sales/Old"
                            // Weird: B (uniqueidentifier) is new.
                            Codes.added, "table:Weird"
                            // Product's legacy data source names database Shop, not the test one.
                            Codes.skipped, "table:Product"
                        ])
                        (sprintf "%A" r.Issues)

                    Expect.exists r.Issues (fun i -> i.Message.Contains "'Qty' (bigint)") ""

                // A query the server rejects is a finding, not a failure.
                let broken = SchemaCheckTests.model ()

                (broken.Model.Tables.["Sales"].Partitions.[0].Source :?> MPartitionSource).Expression <-
                    $"let Source = Sql.Database(\"x\", \"{database}\"), T = Source{{[Schema=\"dbo\",Item=\"Nope\"]}}[Data] in T"

                match check broken (source address broken describer) [ "Sales" ] with
                | Ok r ->
                    Expect.equal (r.Issues |> List.map (fun i -> i.Code)) [ Codes.queryError ] ""
                    Expect.stringContains r.Issues.[0].Message "Nope" ""
                | Error e -> failtest (TessError.message e)

        testCase "a wrong password fails the whole check, with the password scrubbed"
        <| fun () ->
            match server () with
            | None -> skiptest "TESSERA_SQL_TEST_SERVER isn't set"
            | Some server ->
                let wrong = "Wrong-Pa55word!"

                let env = {
                    env with
                        GetEnv =
                            fun n ->
                                if n = "TESS_SQL_PASSWORD" then
                                    Some wrong
                                else
                                    Tessera.Cli.Common.processEnv n
                }

                let address = { Server = server; Database = database }
                use describer = Tessera.Xmla.LiveSql.connect env address
                let db = liveModel ()

                match check db (source address db describer) [ "Sales" ] with
                | Error(AuthenticationFailed(_, reason, _)) -> Expect.isFalse (reason.Contains wrong) reason
                | other -> failtestf "%A" other

        testCase "an untrusted certificate is refused unless TESS_SQL_TRUST_SERVER_CERTIFICATE is set"
        <| fun () ->
            match server () with
            | None -> skiptest "TESSERA_SQL_TEST_SERVER isn't set"
            | Some server ->
                let env = {
                    env with
                        GetEnv =
                            fun n ->
                                if n = "TESS_SQL_TRUST_SERVER_CERTIFICATE" then
                                    None
                                else
                                    Tessera.Cli.Common.processEnv n
                }

                let address = { Server = server; Database = database }
                use describer = Tessera.Xmla.LiveSql.connect env address
                let db = liveModel ()

                match check db (source address db describer) [ "Sales" ] with
                | Error(ConnectionFailed _) -> ()
                | other -> failtestf "%A" other
    ]
