/// `schemacheck --source sql:<server>;<database>`: which tables' sources tess can describe on a SQL
/// Server (or Azure SQL, or a Fabric SQL endpoint), and how. Only two kinds of partition are read,
/// and only when they match exactly:
///   - a query partition on a SQL Server provider data source: its query, as it is;
///   - an M partition that is just `Sql.Database(server, database)` and one navigation step,
///     `Source{[Schema = "s", Item = "t"]}[Data]` (described as `SELECT * FROM [s].[t]`), or
///     `Sql.Database(server, database, [Query = "..."])` (its query). Server and database may be
///     text literals or parameters (named expressions that are a text literal with `meta [...]`).
/// Any other M (more steps, other connectors, other options) is SCHEMA_TABLE_SKIPPED, never a guess.
/// The connection itself is behind ISqlDescriber (implemented in Tessera.Xmla), so nothing here
/// opens one.
module Tessera.SchemaCheckSql

open System
open System.Text
open Microsoft.AnalysisServices.Tabular
open Tessera.SchemaCheck

/// `sql:<server>;<database>`.
type SqlAddress = { Server: string; Database: string }

module SqlAddress =
    let prefix = "sql:"

    let isSql (s: string) =
        s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)

    let parse (s: string) : Result<SqlAddress, TessError> =
        let fail reason = Error(InvalidSchemaSource(s, reason))
        let rest = s.Substring(prefix.Length)

        match rest.IndexOf ';' with
        | -1 -> fail "expected sql:<server>;<database>"
        | i ->
            let server = rest.Substring(0, i).Trim()
            let database = rest.Substring(i + 1).Trim()

            if server = "" then
                fail "the server is empty (expected sql:<server>;<database>)"
            elif database = "" then
                fail "the database is empty (expected sql:<server>;<database>)"
            elif server.Contains '=' || database.Contains '=' then
                // A connection string isn't accepted: credentials never come from the command line.
                fail "give a server and a database, not a connection string (credentials come from the environment)"
            else
                Ok { Server = server; Database = database }

    let format (a: SqlAddress) = $"sql:{a.Server};{a.Database}"

/// One column of `sp_describe_first_result_set`'s answer.
type DescribedColumn = {
    /// None for a column without a name.
    Name: string option
    /// `system_type_name`, e.g. "nvarchar(50)".
    SystemTypeName: string
}

/// Describes queries on one database. The connection is opened on the first call.
type ISqlDescriber =
    inherit IDisposable
    /// The columns of the query's first result set. Error: the server rejected the query (its
    /// message). A connection or sign-in failure raises SchemaSourceFailed instead.
    abstract Describe: query: string -> Result<DescribedColumn list, string>

// ---- M ------------------------------------------------------------------------------------------

type private Token =
    | Ident of string
    | Text of string
    | Punct of char
    | Other of string

/// Tokens of an M expression; None if it has something this reader doesn't know (it then isn't
/// recognised). Comments are skipped.
let private tokenize (m: string) : Token list option =
    let tokens = ResizeArray<Token>()
    let mutable i = 0
    let mutable ok = true

    let readText (start: int) =
        // From the opening quote; "" is a quote. A #( escape inside isn't read: not recognised.
        let sb = StringBuilder()
        let mutable j = start + 1
        let mutable closed = false

        while ok && not closed && j < m.Length do
            match m.[j] with
            | '"' when j + 1 < m.Length && m.[j + 1] = '"' ->
                sb.Append '"' |> ignore
                j <- j + 2
            | '"' ->
                closed <- true
                j <- j + 1
            | '#' when j + 1 < m.Length && m.[j + 1] = '(' -> ok <- false
            | c ->
                sb.Append c |> ignore
                j <- j + 1

        if not closed then
            ok <- false

        sb.ToString(), j

    while ok && i < m.Length do
        let c = m.[i]

        if Char.IsWhiteSpace c then
            i <- i + 1
        elif c = '/' && i + 1 < m.Length && m.[i + 1] = '/' then
            while i < m.Length && m.[i] <> '\n' do
                i <- i + 1
        elif c = '/' && i + 1 < m.Length && m.[i + 1] = '*' then
            match m.IndexOf("*/", i + 2) with
            | -1 -> ok <- false
            | e -> i <- e + 2
        elif c = '"' then
            let s, j = readText i
            tokens.Add(Text s)
            i <- j
        elif c = '#' && i + 1 < m.Length && m.[i + 1] = '"' then
            let s, j = readText (i + 1)
            tokens.Add(Ident s)
            i <- j
        elif Char.IsLetter c || c = '_' then
            let start = i

            while i < m.Length && (Char.IsLetterOrDigit m.[i] || m.[i] = '_' || m.[i] = '.') do
                i <- i + 1

            tokens.Add(Ident(m.Substring(start, i - start).TrimEnd '.'))
        elif "(){}[],=".IndexOf c >= 0 then
            tokens.Add(Punct c)
            i <- i + 1
        elif Char.IsDigit c then
            let start = i

            while i < m.Length && (Char.IsLetterOrDigit m.[i] || m.[i] = '.') do
                i <- i + 1

            tokens.Add(Other(m.Substring(start, i - start)))
        else
            ok <- false

    if ok then Some(List.ofSeq tokens) else None

let private isKeyword (word: string) (t: Token) =
    match t with
    | Ident w -> w = word
    | _ -> false

/// What an M partition reads, when it is one of the recognised shapes.
type MSource = {
    /// The server and database arguments (resolved from parameters), when they could be resolved.
    Server: string option
    Database: string option
    /// SELECT * FROM [s].[t], or the Query option.
    Query: string
}

/// A text value: a literal, or a parameter (a named expression that is a text literal, optionally
/// with `meta [...]`).
let private textValue (parameters: string -> string option) (t: Token) =
    match t with
    | Text s -> Some s
    | Ident name -> parameters name
    | _ -> None

/// The value of a parameter's M: `"text"` or `"text" meta [ ... ]`.
let parameterValue (m: string) : string option =
    match tokenize m with
    | Some [ Text s ] -> Some s
    | Some(Text s :: Ident "meta" :: Punct '[' :: rest) when List.tryLast rest = Some(Punct ']') -> Some s
    | _ -> None

/// `[s].[t]` with `]` doubled.
let quoteName (s: string) = "[" + s.Replace("]", "]]") + "]"

/// Read an M partition's expression, if it is one of the recognised shapes. Error: why not.
let recogniseM (parameters: string -> string option) (m: string) : Result<MSource, string> =
    let notExact =
        "its M isn't just Sql.Database and one navigation step, so its columns can't be known without running it"

    match tokenize m with
    | None -> Error notExact
    | Some tokens ->
        // let <a> = Sql.Database(<s>, <d> [, [Query = "..."]]) [, <b> = <a>{[Schema = "s", Item = "t"]}[Data]] in <last>
        let databaseCall (tokens: Token list) =
            match tokens with
            | Ident "Sql.Database" :: Punct '(' :: s :: Punct ',' :: d :: Punct ')' :: rest -> Some((s, d, None), rest)
            | Ident "Sql.Database" :: Punct '(' :: s :: Punct ',' :: d :: Punct ',' :: Punct '[' :: Ident "Query" :: Punct '=' :: Text q :: Punct ']' :: Punct ')' :: rest ->
                Some((s, d, Some q), rest)
            | _ -> None

        let navigation (source: string) (tokens: Token list) =
            match tokens with
            | Ident a :: Punct '{' :: Punct '[' :: Ident k1 :: Punct '=' :: Text v1 :: Punct ',' :: Ident k2 :: Punct '=' :: Text v2 :: Punct ']' :: Punct '}' :: Punct '[' :: Ident "Data" :: Punct ']' :: rest when
                a = source
                ->
                match k1, k2 with
                | "Schema", "Item" -> Some((v1, v2), rest)
                | "Item", "Schema" -> Some((v2, v1), rest)
                | _ -> None
            | _ -> None

        let resolved (s: Token, d: Token) query = {
            Server = textValue parameters s
            Database = textValue parameters d
            Query = query
        }

        let viaParameter (t: Token) =
            match t with
            | Ident _ -> (textValue parameters t).IsSome
            | Text _ -> true
            | _ -> false

        let result =
            match tokens with
            | Ident "let" :: Ident a :: Punct '=' :: rest ->
                match databaseCall rest with
                | Some((s, d, Some q), [ Ident "in"; Ident last ]) when last = a -> Ok(resolved (s, d) q, s, d)
                | Some((s, d, None), Punct ',' :: Ident b :: Punct '=' :: rest) ->
                    match navigation a rest with
                    | Some((schema, item), [ Ident "in"; Ident last ]) when last = b ->
                        Ok(resolved (s, d) $"SELECT * FROM {quoteName schema}.{quoteName item}", s, d)
                    | _ -> Error notExact
                | _ -> Error notExact
            | _ -> Error notExact

        match result with
        | Error e -> Error e
        | Ok(source, s, d) ->
            if not (viaParameter s) || not (viaParameter d) then
                Error "its Sql.Database server or database is neither text nor a text parameter"
            else
                Ok source

// ---- Partitions -----------------------------------------------------------------------------------

/// Providers whose query is T-SQL.
let private sqlProviders = [
    "SQLNCLI"
    "MSOLEDBSQL"
    "SQLOLEDB"
    "System.Data.SqlClient"
    "Microsoft.Data.SqlClient"
]

let private connectionValue (connectionString: string) (keys: string list) =
    try
        let b = Data.Common.DbConnectionStringBuilder(ConnectionString = connectionString)

        keys
        |> List.tryPick (fun k ->
            match b.TryGetValue k with
            | true, v when not (isNull v) && string v <> "" -> Some(string v)
            | _ -> None)
    with _ ->
        None

/// How a table's source is described, and with which type mapping.
type Plan = {
    Query: string
    /// The database the partition itself names, when it names one.
    Database: string option
    /// The partition's M (true) or a legacy query (false): the type mappings differ.
    IsM: bool
}

/// The query for a table's first partition (as Tabular Editor 2 checks the first). Error: why the
/// table can't be checked on a SQL endpoint.
let plan (db: Database) (t: Table) : Result<Plan, string> =
    if t.Partitions.Count = 0 then
        Error "it has no partitions"
    else
        let p = t.Partitions.[0]

        match p.Source with
        | :? QueryPartitionSource as q ->
            match q.DataSource with
            | :? ProviderDataSource as ds ->
                let provider =
                    if not (String.IsNullOrEmpty ds.Provider) then
                        Some ds.Provider
                    else
                        connectionValue ds.ConnectionString [ "Provider" ]

                let isSql =
                    match provider with
                    | Some pr ->
                        sqlProviders
                        |> List.exists (fun s -> pr.StartsWith(s, StringComparison.OrdinalIgnoreCase))
                    | None -> false

                if not isSql then
                    let name = defaultArg provider "none"
                    Error $"its data source '{ds.Name}' isn't a SQL Server provider (provider: {name})"
                elif String.IsNullOrWhiteSpace q.Query then
                    Error "its partition has no query"
                else
                    Ok {
                        Query = q.Query
                        Database = connectionValue ds.ConnectionString [ "Initial Catalog"; "Database" ]
                        IsM = false
                    }
            | null -> Error "its query partition has no data source"
            | ds -> Error $"its data source '{ds.Name}' is a structured (Power Query) data source"
        | :? MPartitionSource as ms ->
            let parameters (name: string) =
                match db.Model.Expressions.Find name with
                | null -> None
                | e when e.Kind = ExpressionKind.M -> parameterValue e.Expression
                | _ -> None

            recogniseM parameters ms.Expression
            |> Result.map (fun s -> {
                Query = s.Query
                Database = s.Database
                IsM = true
            })
        | :? EntityPartitionSource -> Error "it is a Direct Lake / entity partition"
        | _ -> Error $"its partition is of kind {p.SourceType}, which tess can't describe on a SQL endpoint"

/// The SQL endpoint as a schema source. Each recognised table's query is described with
/// `describer`; a table whose partition names a different database than the address is skipped.
let source (address: SqlAddress) (db: Database) (describer: ISqlDescriber) : ISchemaSource = {
    new ISchemaSource with
        member _.Name = SqlAddress.format address
        member _.MatchesByName = false

        member _.Describe(t: Table) =
            match plan db t with
            | Error reason -> NotChecked reason
            | Ok plan ->
                match plan.Database with
                | Some d when not (String.Equals(d, address.Database, StringComparison.OrdinalIgnoreCase)) ->
                    NotChecked $"its partition reads database '{d}', not '{address.Database}'"
                | _ ->
                    match describer.Describe plan.Query with
                    | Error message -> QueryError(message, Some plan.Query)
                    | Ok columns ->
                        let map = if plan.IsM then mSqlType else legacySqlType

                        let named = [
                            for c in columns do
                                match c.Name with
                                | Some n when n <> "" -> {
                                    Name = n
                                    SourceType = Some c.SystemTypeName
                                    DataType = map c.SystemTypeName
                                  }
                                | _ -> ()
                        ]

                        if named.Length < columns.Length then
                            QueryError("the query returns a column without a name", Some plan.Query)
                        else
                            Described(named, true, plan.Query)
}
