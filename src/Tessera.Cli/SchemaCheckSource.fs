/// What `schemacheck --source` and `set --update-schema --source` name: a schema file, a fixture
/// directory, or `sql:<server>;<database>` (the only one that connects anywhere).
module Tessera.Cli.SchemaCheckSource

open System
open System.IO
open Microsoft.AnalysisServices.Tabular
open Tessera
open Tessera.Cli.Common

/// Opens a describer for a SQL endpoint (the live one is in Tessera.Xmla; tests pass their own).
type SqlConnect = SchemaCheckSql.SqlAddress -> SchemaCheckSql.ISqlDescriber

/// Real connections, signing in from the command's environment. Nothing is contacted until a
/// table needs describing.
let live (ctx: Ctx) : SqlConnect =
    Tessera.Xmla.LiveSql.connect (Source.access ctx).Environment

/// The schema source a --source value names. A SQL source's describer is opened (lazily, on the
/// first table that needs it) only for a `sql:` value.
let private openSource (connect: SqlConnect) (db: Database) (source: string) =
    if SchemaCheckSql.SqlAddress.isSql source then
        SchemaCheckSql.SqlAddress.parse source
        |> Result.map (fun address ->
            let describer = connect address
            SchemaCheckSql.source address db describer, Some(describer :> IDisposable))
    elif Directory.Exists source then
        SchemaCheckSources.fixtureSource source |> Result.map (fun s -> s, None)
    elif File.Exists source then
        SchemaCheckSources.SchemaFile.load source
        |> Result.map (fun parsed -> SchemaCheckSources.SchemaFile.source source parsed, None)
    else
        Error(InvalidSchemaSource(source, "no such file or directory (or sql:<server>;<database>)"))

/// Check `db` against `source`; the describer (if any) is closed afterwards.
let check (connect: SqlConnect) (db: Database) (source: string) (tables: string list) =
    openSource connect db source
    |> Result.bind (fun (s, disposable) ->
        try
            SchemaCheck.check db s tables
        finally
            disposable |> Option.iter (fun d -> d.Dispose()))

/// The --update edit: check, write what was found to stderr (stdout is the change set), update.
let update (ctx: Ctx) (connect: SqlConnect) (source: string) (tables: string list) (dropRemoved: bool) (db: Database) =
    check connect db source tables
    |> Result.bind (fun report ->
        for i in report.Issues do
            ctx.Err.WriteLine $"tess: schemacheck: {ObjectRef.format i.Ref}: {i.Message} [{i.Code}]"

        SchemaCheck.update db report dropRemoved)
