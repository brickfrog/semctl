/// The offline schema sources for `schemacheck --source`: a schema file (JSON, see `SchemaFile`)
/// and a fixture directory in the `sm data` layout. Neither opens anything but local files.
module Semctl.SchemaCheckSources

open System
open System.IO
open System.Text.Json
open Microsoft.AnalysisServices.Tabular
open Semctl.Prelude
open Semctl.SchemaCheck

// ---- Schema file ----------------------------------------------------------------------------------

/// A schema file: what each table's source returns, by model table name.
///
///   { "tables": {
///       "Sales": [ { "name": "OrderDate", "type": "dateTime" }, { "name": "Amount", "type": "decimal" } ],
///       "Product": [ "ProductKey", { "name": "Name", "sourceType": "nvarchar(50)" } ] } }
///
/// A column is a name, or an object with `name` and optionally `type` (a model data type: string,
/// int64, double, decimal, dateTime, boolean, binary) and `sourceType` (the source's own name for
/// it, shown in messages). A column without `type` isn't type-checked. Names are source column
/// names (what the model's sourceColumn refers to). Table names match ignoring case.
module SchemaFile =

    type Parsed = (string * SourceColumn list) list

    let parse (path: string) (text: string) : Result<Parsed, SemctlError> =
        let fail reason =
            Error(InvalidSchemaSource(path, reason))

        try
            use doc = JsonDocument.Parse(text, documentOptions)
            let root = doc.RootElement

            match root.ValueKind with
            | JsonValueKind.Object ->
                match root.TryGetProperty "tables" with
                | true, tables when tables.ValueKind = JsonValueKind.Object ->
                    let parseColumn (table: string) (i: int) (c: JsonElement) : Result<SourceColumn, string> =
                        match c.ValueKind with
                        | JsonValueKind.String when not (String.IsNullOrWhiteSpace(c.GetString())) ->
                            Ok {
                                Name = c.GetString()
                                SourceType = None
                                DataType = None
                            }
                        | JsonValueKind.Object ->
                            let text (name: string) =
                                match c.TryGetProperty name with
                                | true, v when v.ValueKind = JsonValueKind.String -> Ok(Some(v.GetString()))
                                | true, v when v.ValueKind = JsonValueKind.Null -> Ok None
                                | true, _ -> Error $"table '{table}', column {i + 1}: \"{name}\" must be a string"
                                | false, _ -> Ok None

                            match text "name", text "type", text "sourceType" with
                            | Error e, _, _
                            | _, Error e, _
                            | _, _, Error e -> Error e
                            | Ok None, _, _ -> Error $"table '{table}', column {i + 1}: \"name\" is missing"
                            | Ok(Some n), _, _ when String.IsNullOrWhiteSpace n ->
                                Error $"table '{table}', column {i + 1}: \"name\" is empty"
                            | Ok(Some n), Ok t, Ok st ->
                                match t with
                                | None ->
                                    Ok {
                                        Name = n
                                        SourceType = st
                                        DataType = None
                                    }
                                | Some t ->
                                    match tryParseDataType t with
                                    | Some dt ->
                                        Ok {
                                            Name = n
                                            SourceType = st
                                            DataType = Some dt
                                        }
                                    | None ->
                                        Error
                                            $"table '{table}', column '{n}': unknown type '{t}' (known: string, int64, double, decimal, dateTime, boolean, binary)"
                        | _ -> Error $"table '{table}', column {i + 1}: expected a column name or an object"

                    let parsed = [
                        for p in tables.EnumerateObject() ->
                            if p.Value.ValueKind <> JsonValueKind.Array then
                                Error $"table '{p.Name}': expected an array of columns"
                            else
                                let columns =
                                    p.Value.EnumerateArray() |> Seq.mapi (parseColumn p.Name) |> List.ofSeq

                                match
                                    columns
                                    |> List.tryPick (function
                                        | Error e -> Some e
                                        | Ok _ -> None)
                                with
                                | Some e -> Error e
                                | None ->
                                    Ok(
                                        p.Name,
                                        columns
                                        |> List.choose (function
                                            | Ok c -> Some c
                                            | Error _ -> None)
                                    )
                    ]

                    match
                        parsed
                        |> List.tryPick (function
                            | Error e -> Some e
                            | Ok _ -> None)
                    with
                    | Some e -> fail e
                    | None ->
                        let tables =
                            parsed
                            |> List.choose (function
                                | Ok t -> Some t
                                | Error _ -> None)

                        let repeated =
                            tables
                            |> List.countBy (fun (n, _) -> n.ToUpperInvariant())
                            |> List.tryFind (fun (_, n) -> n > 1)

                        match repeated with
                        | Some _ ->
                            let name =
                                tables
                                |> List.map fst
                                |> List.find (fun n -> n.ToUpperInvariant() = fst repeated.Value)

                            fail $"table '{name}' is listed more than once (names match ignoring case)"
                        | None -> Ok tables
                | _ -> fail "expected an object with a \"tables\" object"
            | _ -> fail "expected an object with a \"tables\" object"
        with :? JsonException as e ->
            fail $"not valid JSON: {e.Message}"

    let load (path: string) : Result<Parsed, SemctlError> =
        try
            parse path (File.ReadAllText path)
        with
        | :? IOException as e -> Error(InvalidSchemaSource(path, e.Message))
        | :? UnauthorizedAccessException as e -> Error(InvalidSchemaSource(path, e.Message))

    let source (path: string) (parsed: Parsed) = {
        new ISchemaSource with
            member _.Name = $"schema file {path}"
            member _.MatchesByName = false

            member _.Describe(t: Table) =
                match parsed |> List.tryFind (fun (n, _) -> eqCi n t.Name) with
                | Some(_, columns) ->
                    // A file that types no column at all is a name-only schema.
                    let typed = columns |> List.exists (fun c -> c.DataType.IsSome)
                    Described(columns, typed, path)
                | None -> NotChecked "the schema file has no entry for it"
    }

// ---- Fixture directory ----------------------------------------------------------------------------

/// The column names a fixture file has: a CSV's header, or the properties of a JSON file's rows
/// (in the order they first appear). None, with the reason, when they can't be read.
let fixtureColumns (path: string) : Result<string list, string> =
    try
        match Path.GetExtension(path).ToLowerInvariant() with
        | ".csv" ->
            use reader = new StreamReader(path, Text.UTF8Encoding(false, true), true)
            let csv = CsvReader(reader)

            match csv.Read() with
            | Error(line, message) -> Error $"line {line}: {message}"
            | Ok false -> Error "the file is empty (no header)"
            | Ok true -> Ok [ for i in 0 .. csv.FieldCount - 1 -> csv.FieldString i ]
        | _ ->
            use doc = JsonDocument.Parse(File.ReadAllText path, documentOptions)

            match doc.RootElement.ValueKind with
            | JsonValueKind.Array ->
                let names = ResizeArray<string>()
                let seen = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                let mutable rows = 0

                for row in doc.RootElement.EnumerateArray() do
                    rows <- rows + 1

                    if row.ValueKind = JsonValueKind.Object then
                        for p in row.EnumerateObject() do
                            if seen.Add p.Name then
                                names.Add p.Name

                if rows = 0 then
                    Error "the file has no rows, so it names no columns"
                else
                    Ok(List.ofSeq names)
            | _ -> Error "expected an array of row objects"
    with
    | :? IOException as e -> Error e.Message
    | :? UnauthorizedAccessException as e -> Error e.Message
    | :? JsonException as e -> Error $"not valid JSON: {e.Message}"
    | :? Text.DecoderFallbackException -> Error "the file isn't UTF-8"

/// A fixture directory as a schema source: each table's file names its columns, by model column name.
/// Fixture files have no types, so only names are compared. Calculated columns in a file are
/// ignored, as `sm data` ignores them.
let fixtureSource (dir: string) : Result<ISchemaSource, SemctlError> =
    if not (Directory.Exists dir) then
        Error(InvalidSchemaSource(dir, "no such directory"))
    else
        let files = Directory.GetFiles dir |> Array.map Path.GetFileName |> List.ofArray

        Ok
            {
                new ISchemaSource with
                    member _.Name = $"fixture {dir}"
                    member _.MatchesByName = true

                    member _.Describe(t: Table) =
                        let assignment = Fixture.assign [ t.Name ] files

                        match assignment.Tables with
                        | [ _, [] ] -> NotChecked "the fixture has no file for it"
                        | [ _, [ file ] ] ->
                            let path = Path.Combine(dir, file)

                            match fixtureColumns path with
                            | Error reason -> QueryError($"{file}: {reason}", None)
                            | Ok names ->
                                let computed =
                                    t.Columns
                                    |> Seq.filter (fun c -> c.Type <> ColumnType.Data)
                                    |> Seq.map (fun c -> c.Name)
                                    |> Set.ofSeq

                                let isComputed (n: string) =
                                    computed |> Set.exists (fun c -> eqCi c n)

                                let columns = [
                                    for n in names do
                                        if not (isComputed n) then
                                            {
                                                Name = n
                                                SourceType = None
                                                DataType = None
                                            }
                                ]

                                Described(columns, false, path)
                        | [ _, many ] ->
                            let list = String.concat ", " many
                            NotChecked $"the fixture has more than one file for it ({list})"
                        | _ -> NotChecked "the fixture has no file for it"
            }
