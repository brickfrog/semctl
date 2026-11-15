/// Schema check: compare each table's data columns with the columns its source returns, the way
/// Tabular Editor 2's `-SC` (and the Tabular Editor CLI's `schemacheck`) does:
///   - a source column no data column maps to: SCHEMA_SOURCE_COLUMN_ADDED (warning);
///   - a data column whose source column isn't there: SCHEMA_MISSING_SOURCE_COLUMN (error);
///   - a data column whose type isn't what the source type maps to: SCHEMA_DATA_TYPE_CHANGED (warning);
///   - a source that couldn't be described: SCHEMA_SOURCE_QUERY_ERROR (error);
///   - a source column named twice: SCHEMA_DUPLICATE_SOURCE_COLUMN (info);
///   - a table not checked, and why: SCHEMA_TABLE_SKIPPED (info).
/// TE2's annotations are honoured (see `Annotations`). The schema itself comes from an
/// `ISchemaSource`: a schema file, a fixture directory (Tessera.SchemaCheckSources) or a SQL endpoint
/// (Tessera.SchemaCheckSql, with the connection in Tessera.Xmla). Nothing here opens a connection.
module Tessera.SchemaCheck

open System
open Microsoft.AnalysisServices.Tabular

/// One column of a source's result.
type SourceColumn = {
    Name: string
    /// The type as the source names it ("nvarchar(50)", "int64"), when it names one.
    SourceType: string option
    /// The model data type that source type maps to; None when it is unknown or not mapped exactly
    /// (then the column's type isn't compared).
    DataType: DataType option
}

/// What a source knows about one table.
type Lookup =
    /// The source's columns, in order. `TypesKnown` is false for a source that has no types at all
    /// (a CSV fixture), so no type is compared.
    | Described of columns: SourceColumn list * typesKnown: bool * origin: string
    /// Not checked, and why (an M expression tess can't read exactly, no entry in a schema file...).
    | NotChecked of reason: string
    /// The source was asked and failed (an invalid query, a missing table).
    | QueryError of message: string * query: string option

/// Raised by a source that can't go on at all (the server can't be reached, sign-in failed): the
/// whole check fails with this error, rather than one finding per table.
exception SchemaSourceFailed of TessError

/// Where table schemas come from.
type ISchemaSource =
    /// A short description for output ("schema file x.json", "sql:server;db").
    abstract Name: string
    /// Columns are matched by their model names (a fixture's files name them as the model does)
    /// rather than by their SourceColumn.
    abstract MatchesByName: bool
    abstract Describe: Table -> Lookup

/// The annotation names Tabular Editor 2 uses (TableMetadata.cs). Any value but `0`, `false` or
/// `no` (any case) turns one on, as in TE2's CheckFlag.
module Annotations =
    let noWarnings = "TabularEditor_SchemaCheckNoWarnings"
    let skip = "TabularEditor_SkipSchemaCheck"
    /// TE2 keeps reading its old misspelling.
    let skipTypo = "TabularEditor_SkipSkemaCheck"
    let ignoreAdded = "TabularEditor_IgnoreSourceColumnAdded"
    let ignoreTypes = "TabularEditor_IgnoreDataTypeChange"
    let ignoreMissing = "TabularEditor_IgnoreMissingSourceColumn"

    /// Whether an annotation (null: absent) is on.
    let isSet (annotation: Annotation) =
        match annotation with
        | null -> false
        | a ->
            let v = (if isNull a.Value then "" else a.Value).Trim()

            not (
                v = "0"
                || String.Equals(v, "false", StringComparison.OrdinalIgnoreCase)
                || String.Equals(v, "no", StringComparison.OrdinalIgnoreCase)
            )

module Codes =
    let added = "SCHEMA_SOURCE_COLUMN_ADDED"
    let typeChanged = "SCHEMA_DATA_TYPE_CHANGED"
    let missing = "SCHEMA_MISSING_SOURCE_COLUMN"
    let queryError = "SCHEMA_SOURCE_QUERY_ERROR"
    let duplicate = "SCHEMA_DUPLICATE_SOURCE_COLUMN"
    let skipped = "SCHEMA_TABLE_SKIPPED"

    /// Every code, with its default severity and a one-line description.
    let all = [
        added, "warning", "a source column that no data column of the table maps to"
        typeChanged, "warning", "a data column whose data type isn't what its source column's type maps to"
        missing, "error", "a data column whose source column the source doesn't return"
        queryError, "error", "the table's source couldn't be described (an invalid query, a missing table)"
        duplicate, "info", "a source column name that the source returns more than once (the first is used)"
        skipped, "info", "a table that wasn't checked, and why"
    ]

[<RequireQualifiedAccess>]
type Severity =
    | Error
    | Warning
    | Info

type Issue = {
    Code: string
    Severity: Severity
    Ref: ObjectRef
    Message: string
    /// The source column it is about, if any.
    SourceColumn: string option
}

[<RequireQualifiedAccess>]
type TableStatus =
    | Checked
    | Skipped
    | Failed

type TableResult = {
    Table: string
    Status: TableStatus
    /// Why it was skipped, or the error.
    Reason: string option
    /// Where the schema came from (a file, a query), for a checked table.
    Origin: string option
    TypesChecked: bool
    /// The source's columns, for a checked table.
    Columns: SourceColumn list
    Issues: Issue list
}

module TableResult =
    /// Any added, missing or retyped column.
    let hasDrift (r: TableResult) =
        r.Issues
        |> List.exists (fun i -> i.Code = Codes.added || i.Code = Codes.missing || i.Code = Codes.typeChanged)

type Report = {
    Source: string
    Tables: TableResult list
} with

    member r.Issues = r.Tables |> List.collect (fun t -> t.Issues)

// ---- Data types -----------------------------------------------------------------------------------

/// The token `get` and `set` use for a data type: TOM's name in camelCase (int64, dateTime, ...).
let dataTypeToken (t: DataType) =
    let s = string t
    string (Char.ToLowerInvariant s.[0]) + s.Substring 1

/// A data type token, any case. Only the types a data column can hold.
let tryParseDataType (s: string) =
    match s.Trim().ToLowerInvariant() with
    | "string" -> Some DataType.String
    | "int64" -> Some DataType.Int64
    | "double" -> Some DataType.Double
    | "decimal" -> Some DataType.Decimal
    | "datetime" -> Some DataType.DateTime
    | "boolean" -> Some DataType.Boolean
    | "binary" -> Some DataType.Binary
    | _ -> None

/// A SQL Server type name without its length, precision or scale: "nvarchar(50)" -> "nvarchar".
let sqlBaseType (systemTypeName: string) =
    let s = systemTypeName.Trim()

    match s.IndexOf '(' with
    | -1 -> s.ToLowerInvariant()
    | i -> s.Substring(0, i).Trim().ToLowerInvariant()

/// SQL Server types for a query partition (a legacy provider data source), as Tabular Editor 2
/// maps them (TableMetadata.DataTypeMap), without its guesses: TE2 maps any other name containing
/// "int" or "date" by that, and everything else to string; here an unknown type is not compared.
let legacySqlType (systemTypeName: string) : DataType option =
    match sqlBaseType systemTypeName with
    | "binary"
    | "varbinary" -> Some DataType.Binary
    | "char"
    | "nchar"
    | "varchar"
    | "nvarchar"
    | "text" -> Some DataType.String
    | "real"
    | "float" -> Some DataType.Double
    | "decimal"
    | "numeric"
    | "money"
    | "smallmoney" -> Some DataType.Decimal
    | "int"
    | "bigint"
    | "smallint"
    | "tinyint" -> Some DataType.Int64
    | "datetime"
    | "date"
    | "time"
    // TE2's "contains date" rule, which is exact for these.
    | "datetime2"
    | "smalldatetime"
    | "datetimeoffset" -> Some DataType.DateTime
    | "bit" -> Some DataType.Boolean
    | _ -> None

/// SQL Server types for an M partition (Sql.Database). Power Query's own mapping isn't documented
/// by Microsoft, so only the types whose model type isn't in doubt are compared. Numeric types other
/// than int/bigint/float/real (decimal, numeric, money, smallmoney, smallint, tinyint), time,
/// datetimeoffset, uniqueidentifier and binary types are not compared.
let mSqlType (systemTypeName: string) : DataType option =
    match sqlBaseType systemTypeName with
    | "char"
    | "nchar"
    | "varchar"
    | "nvarchar"
    | "text"
    | "ntext" -> Some DataType.String
    | "int"
    | "bigint" -> Some DataType.Int64
    | "float"
    | "real" -> Some DataType.Double
    | "bit" -> Some DataType.Boolean
    | "date"
    | "datetime"
    | "datetime2"
    | "smalldatetime" -> Some DataType.DateTime
    | _ -> None

// ---- Which tables ---------------------------------------------------------------------------------

/// The columns a schema check compares: data columns (not calculated columns, not the row number).
let dataColumns (t: Table) : DataColumn list = [
    for c in t.Columns do
        match c with
        | :? DataColumn as d when c.Type = ColumnType.Data -> d
        | _ -> ()
]

/// A table the engine computes: calculated tables, calculation groups.
let private isComputed (t: Table) =
    not (isNull t.CalculationGroup)
    || t.Partitions
       |> Seq.exists (fun p ->
           p.SourceType = PartitionSourceType.Calculated
           || p.SourceType = PartitionSourceType.CalculationGroup)

/// Why a table isn't a source table, or None if it is one.
let notASourceTable (t: Table) =
    if not (isNull t.CalculationGroup) then
        Some "a calculation group"
    elif isComputed t then
        Some "a calculated table"
    elif (dataColumns t).IsEmpty then
        Some "no data columns"
    else
        None

let private eqCi (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

// ---- Comparing --------------------------------------------------------------------------------------

/// Compare one table with what its source returns. `byName`: match a column by its model name (a
/// fixture's files name columns as the model does) rather than by its SourceColumn.
let compareTable
    (noWarnings: bool)
    (t: Table)
    (columns: SourceColumn list)
    (typesKnown: bool)
    (byName: bool)
    : Issue list =
    let tableRef = ObjectRef.table t.Name

    let columnRef (c: Column) =
        ObjectRef.inTable ObjectKind.Column t.Name c.Name

    let warning = if noWarnings then Severity.Error else Severity.Warning

    let flag name =
        Annotations.isSet (t.Annotations.Find name)

    let colFlag (c: Column) name =
        Annotations.isSet (c.Annotations.Find name)

    let data = dataColumns t

    // A repeated source name: TE2 keeps the first (its schema is a case-insensitive dictionary).
    let seen = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

    let unique, repeated = columns |> List.partition (fun c -> seen.Add c.Name)

    let duplicates =
        repeated
        |> List.map (fun c -> c.Name)
        |> List.distinctBy (fun n -> n.ToUpperInvariant())
        |> List.map (fun n -> {
            Code = Codes.duplicate
            Severity = Severity.Info
            Ref = tableRef
            Message = $"The source of table '{t.Name}' returns more than one column named '{n}'; the first is used."
            SourceColumn = Some n
        })

    let mapsTo (src: SourceColumn) (c: DataColumn) =
        if byName then
            eqCi c.Name src.Name
        else
            let sc = if isNull c.SourceColumn then "" else c.SourceColumn
            eqCi sc src.Name || eqCi sc ("[" + src.Name + "]")

    let matched = Collections.Generic.HashSet<DataColumn>(HashIdentity.Reference)

    let compared = [
        for src in unique do
            let targets = data |> List.filter (mapsTo src)

            for c in targets do
                matched.Add c |> ignore

            if targets.IsEmpty then
                if not (flag Annotations.ignoreAdded) then
                    let typeText =
                        match src.SourceType with
                        | Some s -> $" ({s})"
                        | None -> ""

                    {
                        Code = Codes.added
                        Severity = warning
                        Ref = tableRef
                        Message =
                            $"Column '{src.Name}'{typeText} is in the source of table '{t.Name}' but no column of the table maps to it."
                        SourceColumn = Some src.Name
                    }
            elif typesKnown && not (flag Annotations.ignoreTypes) then
                match src.DataType with
                | Some expected ->
                    for c in targets do
                        let concrete =
                            c.DataType <> DataType.Automatic
                            && c.DataType <> DataType.Unknown
                            && c.DataType <> DataType.Variant

                        if concrete && c.DataType <> expected && not (colFlag c Annotations.ignoreTypes) then
                            let typeText =
                                match src.SourceType with
                                | Some s -> $"{s}, "
                                | None -> ""

                            {
                                Code = Codes.typeChanged
                                Severity = warning
                                Ref = columnRef c
                                Message =
                                    $"Column '{t.Name}'[{c.Name}] is {dataTypeToken c.DataType}, but its source column '{src.Name}' ({typeText}which maps to {dataTypeToken expected}) is not."
                                SourceColumn = Some src.Name
                            }
                | None -> ()
    ]

    // Every data column is matched or not whatever the type settings (TE2 only counts a column as
    // matched while it compares types, so with IgnoreDataTypeChange it reports every column missing).
    let missing = [
        if not (flag Annotations.ignoreMissing) then
            for c in data do
                if not (matched.Contains c) && not (colFlag c Annotations.ignoreMissing) then
                    let what =
                        if byName then
                            $"the source has no column '{c.Name}'"
                        else
                            let sc =
                                if String.IsNullOrEmpty c.SourceColumn then
                                    "(none)"
                                else
                                    c.SourceColumn

                            $"its source column '{sc}' isn't in the source"

                    {
                        Code = Codes.missing
                        Severity = Severity.Error
                        Ref = columnRef c
                        Message = $"Column '{t.Name}'[{c.Name}]: {what}."
                        SourceColumn = if byName then Some c.Name else Option.ofObj c.SourceColumn
                    }
    ]

    duplicates @ compared @ missing

/// Check `tables` (all source tables when empty: names are matched ignoring case) against `source`.
/// Unknown table names are an error.
let check (db: Database) (source: ISchemaSource) (tables: string list) : Result<Report, TessError> =
    let model = db.Model
    let all = [ for t in model.Tables -> t ]

    let selected =
        tables
        |> List.fold
            (fun acc name ->
                acc
                |> Result.bind (fun found ->
                    match all |> List.tryFind (fun t -> eqCi t.Name name) with
                    | Some t -> Ok(found @ [ t, true ])
                    | None ->
                        let names = all |> List.map (fun t -> t.Name)
                        let candidates = Suggest.closest names name

                        Error(ObjectNotFoundSuggest(ObjectRef.format (ObjectRef.table name), candidates))))
            (Ok [])
        |> Result.map (fun named ->
            if tables.IsEmpty then
                all |> List.map (fun t -> t, false)
            else
                named |> List.distinctBy (fun (t, _) -> t.Name))

    selected
    |> Result.bind (fun selected ->
        try
            let noWarnings = Annotations.isSet (model.Annotations.Find Annotations.noWarnings)
            let byName = source.MatchesByName

            let skippedResult (t: Table) (reason: string) = {
                Table = t.Name
                Status = TableStatus.Skipped
                Reason = Some reason
                Origin = None
                TypesChecked = false
                Columns = []
                Issues = [
                    {
                        Code = Codes.skipped
                        Severity = Severity.Info
                        Ref = ObjectRef.table t.Name
                        Message = $"Table '{t.Name}' was not checked: {reason}."
                        SourceColumn = None
                    }
                ]
            }

            let results = [
                for t, named in selected do
                    match notASourceTable t with
                    | Some why ->
                        // A table that has no source isn't reported unless it was asked for.
                        if named then
                            skippedResult t why
                    | None when
                        Annotations.isSet (t.Annotations.Find Annotations.skip)
                        || Annotations.isSet (t.Annotations.Find Annotations.skipTypo)
                        ->
                        skippedResult t "its TabularEditor_SkipSchemaCheck annotation is set"
                    | None ->
                        match source.Describe t with
                        | NotChecked reason -> skippedResult t reason
                        | QueryError(message, query) ->
                            let detail =
                                match query with
                                | Some q -> $" Query: {q}"
                                | None -> ""

                            {
                                Table = t.Name
                                Status = TableStatus.Failed
                                Reason = Some message
                                Origin = query
                                TypesChecked = false
                                Columns = []
                                Issues = [
                                    {
                                        Code = Codes.queryError
                                        Severity = Severity.Error
                                        Ref = ObjectRef.table t.Name
                                        Message =
                                            $"The source of table '{t.Name}' couldn't be described: {message}.{detail}"
                                        SourceColumn = None
                                    }
                                ]
                            }
                        | Described(columns, typesKnown, origin) -> {
                            Table = t.Name
                            Status = TableStatus.Checked
                            Reason = None
                            Origin = Some origin
                            TypesChecked = typesKnown
                            Columns = columns
                            Issues = compareTable noWarnings t columns typesKnown byName
                          }
            ]

            Ok {
                Source = source.Name
                Tables = results
            }
        with SchemaSourceFailed e ->
            Error e)

// ---- Updating the model ---------------------------------------------------------------------------

/// Bring the model's data columns in line with the source: add a data column for each source column
/// that was reported added (named and sourced as the source names it, with its mapped type), change
/// the data type of each column reported retyped (nothing else about it changes), and, only with
/// `dropRemoved`, remove the columns whose source column is gone (through `remove`, so DAX that
/// still uses one blocks it). Whatever the annotations made the check ignore is left alone.
let update (db: Database) (report: Report) (dropRemoved: bool) : Result<Mutation.Effect, TessError> =
    let model = db.Model
    let warnings = ResizeArray<string>()
    let notes = ResizeArray<string>()
    let toRemove = ResizeArray<string>()

    let tableOf (name: string) = model.Tables.Find name

    for r in report.Tables do
        let t = tableOf r.Table

        match r.Status with
        | TableStatus.Skipped -> ()
        | TableStatus.Failed -> warnings.Add $"Table '{r.Table}' was not updated: its source couldn't be described."
        | TableStatus.Checked ->
            let columns = r.Columns
            let hasLineage = not (String.IsNullOrEmpty t.LineageTag)

            for i in r.Issues do
                match i.SourceColumn with
                | Some srcName when i.Code = Codes.added ->
                    let src = columns |> List.tryFind (fun c -> eqCi c.Name srcName)

                    match src |> Option.bind (fun c -> c.DataType) with
                    | None ->
                        warnings.Add
                            $"Column '{srcName}' was not added to table '{t.Name}': the source gives no type tess can map exactly."
                    | Some dt ->
                        let taken =
                            t.Columns |> Seq.exists (fun c -> eqCi c.Name srcName)
                            || t.Measures |> Seq.exists (fun m -> eqCi m.Name srcName)

                        if taken then
                            warnings.Add
                                $"Column '{srcName}' was not added to table '{t.Name}': the table already has a column or measure of that name."
                        else
                            let c = new DataColumn(Name = srcName, SourceColumn = srcName, DataType = dt)

                            if hasLineage then
                                c.LineageTag <- Guid.NewGuid().ToString()

                            c.SummarizeBy <-
                                (if dt = DataType.Int64 || dt = DataType.Double || dt = DataType.Decimal then
                                     AggregateFunction.Sum
                                 else
                                     AggregateFunction.None)

                            t.Columns.Add c
                            notes.Add $"Added column '{t.Name}'[{srcName}] ({dataTypeToken dt})"
                | Some srcName when i.Code = Codes.typeChanged ->
                    let src = columns |> List.tryFind (fun c -> eqCi c.Name srcName)

                    match i.Ref.Path, src |> Option.bind (fun c -> c.DataType) with
                    | [ _; columnName ], Some dt ->
                        let c = t.Columns.Find columnName
                        let before = c.DataType
                        c.DataType <- dt
                        notes.Add $"Changed '{t.Name}'[{columnName}] from {dataTypeToken before} to {dataTypeToken dt}"
                    | _ -> ()
                | _ when i.Code = Codes.missing ->
                    match i.Ref.Path with
                    | [ _; columnName ] ->
                        if dropRemoved then
                            toRemove.Add(ObjectRef.format i.Ref)
                        else
                            warnings.Add
                                $"Column '{t.Name}'[{columnName}] has no source column any more; kept (--drop-removed-columns removes it)."
                    | _ -> ()
                | _ -> ()

    // One column at a time, so a column other DAX still uses is kept (with a warning) and the rest go.
    let removed =
        toRemove
        |> Seq.fold
            (fun acc selector ->
                acc
                |> Result.bind (fun (effect: Mutation.Effect) ->
                    match RemoveObject.remove db (RemoveObject.RemoveSpec.ofSelectors [ selector ]) with
                    | Ok e ->
                        Ok {
                            effect with
                                Warnings = effect.Warnings @ e.Warnings
                                Notes = effect.Notes @ [ $"Removed {selector}: its source column is gone" ] @ e.Notes
                        }
                    | Error(RemoveBlocked(_, dependents)) ->
                        let users = String.concat ", " dependents

                        Ok {
                            effect with
                                Warnings =
                                    effect.Warnings
                                    @ [
                                        $"{selector} has no source column any more but was kept: {users} still use it."
                                    ]
                        }
                    | Error e -> Error e))
            (Ok Mutation.Effect.none)

    removed
    |> Result.map (fun effect -> {
        effect with
            Warnings = List.ofSeq warnings @ effect.Warnings
            Notes = List.ofSeq notes @ effect.Notes
    })
