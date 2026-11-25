/// `sm preview`: the first rows of a table (Tabular Editor 3's table preview), from model files
/// with fixture data (the local engine) or from a model on a server.
module Semctl.Cli.PreviewCommand

open System
open System.Diagnostics
open Argu
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.Xmla
open Semctl.Cli.Output
open Semctl.Cli.Common

/// One-line description, shown in `sm --help`.
let summary =
    "show the first rows of a table: model files with --data <fixture> (offline), or a server model (xmla:<server>;<database>)."

/// Rows shown unless --rows says otherwise.
let defaultRows = 10

type Args =
    | [<MainCommand>] Target of model_and_table: string list
    | [<AltCommandLine("-n")>] Rows of rows: int
    | [<AltCommandLine("-c")>] Columns of columns: string
    | Data of directory: string
    | Csv

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Target _ ->
                "the model (model files with --data, or xmla:<server>[;<database>]; quote it), then the table's name."
            | Rows _ -> $"how many rows (default {defaultRows})."
            | Columns _ -> "only these columns, in this order (comma-separated or repeated; default: all)."
            | Data _ ->
                "model files: evaluate with the local DAX engine over this fixture directory (see `sm data`). "
                + "Nothing leaves the machine."
            | Csv -> "write CSV."

/// The query: on a server, TOPN bounds the work (asked for one row more than shown, so a longer
/// table is marked truncated) ("TOPN does not guarantee any sort order", dax.guide,
/// so a server's rows are whichever it returns first). The local engine evaluates the table itself
/// and its first rows, in fixture order, are kept.
let query (table: string) (topn: int option) =
    match topn with
    | Some n -> $"EVALUATE TOPN ( {n}, {Dax.quoteTable table} )"
    | None -> $"EVALUATE {Dax.quoteTable table}"

/// A result column's column name: `T[c]` (or `'T'[c]`) is `c`.
let private columnName (resultName: string) =
    let i = resultName.IndexOf '['

    if i >= 0 && resultName.EndsWith "]" then
        resultName.Substring(i + 1, resultName.Length - i - 2).Replace("]]", "]")
    else
        resultName

/// The first `rows` rows, with only the columns asked for, in that order.
let project (rows: int) (columns: string list) (r: QueryResult) : Result<QueryResult, string * string list> =
    let indexed = r.Columns |> List.mapi (fun i c -> i, c)

    let picked =
        columns
        |> List.fold
            (fun acc name ->
                acc
                |> Result.bind (fun xs ->
                    match
                        indexed
                        |> List.tryFind (fun (_, c) ->
                            String.Equals(columnName c.Name, name, StringComparison.OrdinalIgnoreCase))
                    with
                    | Some x -> Ok(xs @ [ x ])
                    | None -> Error(name, Suggest.closest (r.Columns |> List.map (fun c -> columnName c.Name)) name)))
            (Ok [])

    picked
    |> Result.map (fun picked ->
        let picked = if columns.IsEmpty then indexed else picked
        let kept = List.truncate rows r.Rows

        {
            Columns = picked |> List.map snd
            Rows =
                kept
                |> List.map (fun row -> picked |> List.map (fun (i, _) -> row.[i]) |> Array.ofList)
            Truncated = r.Truncated || r.Rows.Length > rows
        })

let private render (ctx: Ctx) (csv: bool) (source: string) (table: string) (r: QueryResult) (elapsed: TimeSpan) =
    if ctx.Json then
        writeJson
            ctx.Out
            (obj [
                "command", str "preview"
                "source", str source
                "table", str table
                "results", QueryResult.listToJson [ r ]
                "durationMs", num (int elapsed.TotalMilliseconds)
            ])
    elif csv then
        ctx.Out.Write(QueryResult.toCsv ctx.Out.NewLine r)
    else
        for line in QueryResult.toTable r do
            ctx.Out.WriteLine line

    if not ctx.Json && r.Truncated then
        ctx.Err.WriteLine($"Showing the first {r.Rows.Length} rows (--rows for more).")

    Done

let private columnsArg (args: ParseResults<Args>) =
    parseList (args.GetResults Args.Columns)

/// The table in the model, by name, ignoring case.
let private findTable (db: Database) (name: string) : Result<Table, SemctlError> =
    match
        db.Model.Tables
        |> Seq.tryFind (fun t -> String.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
    with
    | Some t -> Ok t
    | None ->
        let names = db.Model.Tables |> Seq.map _.Name

        match Suggest.closest names name with
        | [] -> Error(ObjectNotFound $"table:{name}")
        | close -> Error(ObjectNotFoundSuggest($"table:{name}", close |> List.map (fun n -> $"table:{n}")))

let private finish ctx (args: ParseResults<Args>) source table rows columns (elapsed: TimeSpan) (r: QueryResult) =
    let csv = args.Contains Args.Csv

    match project rows columns r with
    | Error(name, close) ->
        Failed(
            match close with
            | [] -> ObjectNotFound $"column:{table}/{name}"
            | close ->
                ObjectNotFoundSuggest($"column:{table}/{name}", close |> List.map (fun c -> $"column:{table}/{c}"))
        )
    | Ok r -> render ctx csv source table r elapsed

let private local (ctx: Ctx) (args: ParseResults<Args>) source directory table rows columns =
    let clock = Stopwatch.StartNew()

    match ModelIO.loadPath source with
    | Error e -> Failed e
    | Ok(src, db) ->
        match findTable db table with
        | Error e -> Failed e
        | Ok t ->
            match Fixture.loadAndCheck db directory with
            | Error e -> Failed e
            | Ok(loaded, issues) ->
                let errors =
                    issues
                    |> List.filter (fun i -> i.Severity = Validate.Severity.Error)
                    |> List.length

                if errors > 0 then
                    Failed(FixtureHasErrors(loaded.Directory, errors))
                else
                    match
                        Engine.prepare db loaded.Store
                        |> Result.bind (fun p -> Engine.evaluateQuery p (query t.Name None))
                    with
                    | Error e -> Failed e
                    | Ok(r :: _) -> finish ctx args src.Path t.Name rows columns clock.Elapsed r
                    | Ok [] -> Failed(QueryFailed "the query returned no result")

let private remote (ctx: Ctx) (args: ParseResults<Args>) source table rows columns =
    let clock = Stopwatch.StartNew()

    match XmlaAddress.parse source with
    | Error e -> Failed e
    | Ok address ->
        // One row more than shown, to tell whether the table has more.
        match Remote.query (Source.access ctx) address (query table (Some(rows + 1))) (Some(rows + 1)) with
        | Error e -> Failed e
        | Ok(address, r :: _) -> finish ctx args (XmlaAddress.display address) table rows columns clock.Elapsed r
        | Ok(_, []) -> Failed(QueryFailed "the server returned no result")

let run (ctx: Ctx) (args: ParseResults<Args>) =
    let rows = args.GetResult(Args.Rows, defaultValue = defaultRows)
    let data = args.TryGetResult Args.Data
    let columns = columnsArg args

    match args.GetResults Args.Target |> List.concat with
    | _ when rows < 1 -> UsageError "--rows must be at least 1"
    | _ when ctx.Json && args.Contains Args.Csv -> UsageError "--json and --csv can't be combined"
    | [ source; table ] when XmlaAddress.isRemote source ->
        if data.IsSome then
            UsageError "--data is for model files: a server model is read on the server"
        else
            remote ctx args source table rows columns
    | [ source; table ] ->
        match data with
        | None ->
            UsageError
                $"'{source}' is model files: give the fixture data to read with --data <directory> (see `sm data init`), or preview a server model (xmla:<server>;<database>)"
        | Some dataDir -> local ctx args source dataDir table rows columns
    | [ _ ] -> UsageError "expected a table after the model"
    | [] -> UsageError "expected a model and a table: sm preview <model> <table> [--rows N]"
    | _ -> UsageError "expected a model and one table (quote a name with spaces)"
