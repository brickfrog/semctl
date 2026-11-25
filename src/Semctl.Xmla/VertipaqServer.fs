/// VertiPaq statistics from a model on a server (`sm vertipaq xmla:<server>;<database>`): the
/// storage DMVs and DAX queries VertiPaq Analyzer runs (Dax.Model.Extractor's DmvExtractor and
/// StatExtractor, MIT, by SQLBI), sent through the session's query seam like any other query.
/// Every query is read-only. One that fails (a server that doesn't have that DMV, a user without
/// the permission) leaves its statistics unknown, with a warning; nothing else is affected.
module Semctl.Xmla.VertipaqServer

open System
open System.Collections.Generic
open System.Globalization
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.Vertipaq

let private eq (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

// ---- The queries (exactly what is sent; the README lists them) ---------------------------------

let tablesQuery =
    "SELECT DIMENSION_NAME AS TABLE_NAME, TABLE_ID AS TABLE_ID, ROWS_COUNT AS ROWS_IN_TABLE "
    + "FROM $SYSTEM.DISCOVER_STORAGE_TABLES WHERE RIGHT ( LEFT ( TABLE_ID, 2 ), 1 ) <> '$'"

let columnsQuery =
    "SELECT DIMENSION_NAME AS TABLE_NAME, COLUMN_ID AS COLUMN_ID, ATTRIBUTE_NAME AS COLUMN_NAME, "
    + "DICTIONARY_SIZE AS DICTIONARY_SIZE_BYTES, COLUMN_ENCODING AS COLUMN_ENCODING_INT "
    + "FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMNS WHERE COLUMN_TYPE = 'BASIC_DATA'"

let cardinalityQuery =
    "SELECT DIMENSION_NAME AS TABLE_NAME, TABLE_ID AS COLUMN_HIERARCHY_ID, ROWS_COUNT - 3 AS COLUMN_CARDINALITY "
    + "FROM $SYSTEM.DISCOVER_STORAGE_TABLES WHERE LEFT ( TABLE_ID, 2 ) = 'H$'"

let segmentsQuery =
    "SELECT DIMENSION_NAME AS TABLE_NAME, PARTITION_NAME, COLUMN_ID AS COLUMN_NAME, SEGMENT_NUMBER, "
    + "RECORDS_COUNT AS SEGMENT_ROWS, USED_SIZE "
    + "FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS WHERE RIGHT ( LEFT ( TABLE_ID, 2 ), 1 ) <> '$'"

let hierarchiesQuery =
    "SELECT DIMENSION_NAME AS TABLE_NAME, USED_SIZE, TABLE_ID AS COLUMN_HIERARCHY_ID "
    + "FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS WHERE LEFT ( TABLE_ID, 2 ) = 'H$' AND SEGMENT_NUMBER = 0"

let userHierarchyNamesQuery =
    "SELECT [ID] AS USER_HIERARCHY_ID, [Name] AS USER_HIERARCHY_NAME FROM $SYSTEM.TMSCHEMA_HIERARCHIES"

let userHierarchiesQuery =
    "SELECT DIMENSION_NAME AS TABLE_NAME, USED_SIZE AS USED_SIZE, TABLE_ID AS HIERARCHY_ID "
    + "FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS WHERE LEFT ( TABLE_ID, 2 ) = 'U$'"

let relationshipSizesQuery =
    "SELECT DIMENSION_NAME AS TABLE_NAME, TABLE_ID AS RELATIONSHIP_ID, USED_SIZE AS USED_SIZE "
    + "FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS WHERE LEFT ( TABLE_ID, 2 ) = 'R$'"

let relationshipsQuery =
    "SELECT [ID] AS [RelationshipID], [FromTableID], [FromColumnID], [ToTableID], [ToColumnID] "
    + "FROM $SYSTEM.TMSCHEMA_RELATIONSHIPS"

let private column (table: string) (name: string) =
    Dax.quoteTable table + "[" + name.Replace("]", "]]") + "]"

let private text (s: string) = s.Replace("\"", "\"\"")

let private union (rows: string list) =
    match rows with
    | [ one ] -> "EVALUATE " + one
    | _ -> "EVALUATE UNION ( " + String.Join(", ", rows) + " )"

/// Rows per table, in batches of 50 (as StatExtractor).
let tableRowsQueries (tables: string list) =
    tables
    |> List.chunkBySize 50
    |> List.map (fun chunk ->
        chunk
        |> List.mapi (fun i t ->
            $"ROW ( \"Table\", \"{i:D4}{text t}\", \"Cardinality\", COUNTROWS ( {Dax.quoteTable t} ) )")
        |> union)

/// Distinct values per column, in batches of 50. The index in front of the name keeps the row's
/// strings distinct, so UNION can't merge two rows.
/// A field parameter's column (Group By Columns) is counted on its attribute hierarchy, which
/// works without its grouping columns.
let columnCardinalityQueries (columns: (string * string * bool) list) =
    columns
    |> List.chunkBySize 50
    |> List.map (fun chunk ->
        chunk
        |> List.mapi (fun i (t, c, grouped) ->
            let count =
                if grouped then
                    $"COUNTROWS ( ALLNOBLANKROW ( {column t c} ) )"
                else
                    $"DISTINCTCOUNT ( {column t c} )"

            $"ROW ( \"Table\", \"{i:D4}{text t}\", \"Column\", \"{i:D4}{text c}\", \"Cardinality\", {count} )")
        |> union)

/// Missing keys and invalid rows per relationship, in batches of 10: the From rows on the To
/// table's blank row.
let relationshipQueries (relationships: (int * RelationshipStats) list) =
    relationships
    |> List.chunkBySize 10
    |> List.map (fun chunk ->
        chunk
        |> List.map (fun (id, r) ->
            let f, k = column r.FromTable r.FromColumn, column r.ToTable r.ToColumn

            $"CALCULATETABLE ( ROW ( \"RelationshipId\", {id}, \"MissingKeys\", DISTINCTCOUNT ( {f} ), \"InvalidRows\", COUNTROWS ( {Dax.quoteTable r.FromTable} ) ), ISBLANK ( {k} ), USERELATIONSHIP ( {f}, {k} ) )")
        |> union)

// ---- Reading the answers -----------------------------------------------------------------------

/// A number, however the server sends it (DMVs say decimal or integer, ADOMD may give a string).
let number (v: QueryValue) : int64 option =
    match v with
    | QueryValue.Int64 n -> Some n
    | QueryValue.Decimal d -> Some(int64 d)
    | QueryValue.Double d when not (Double.IsNaN d) -> Some(int64 d)
    | QueryValue.String s ->
        match Decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, d -> Some(int64 d)
        | _ -> None
    | QueryValue.Boolean b -> Some(if b then 1L else 0L)
    | _ -> None

let private textOf (v: QueryValue) =
    match v with
    | QueryValue.String s -> Some s
    | QueryValue.Blank -> None
    | QueryValue.Int64 n -> Some(n.ToString(CultureInfo.InvariantCulture))
    | other -> Some(string other)

/// The number in the last parentheses of a DMV id: `Sales (12)` is 12.
let idOf (dmvId: string) : int64 option =
    let o, c = dmvId.LastIndexOf '(', dmvId.LastIndexOf ')'

    if o >= 0 && c > o then
        match Int64.TryParse(dmvId.Substring(o + 1, c - o - 1), NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, n -> Some n
        | _ -> None
    else
        None

/// What follows the last `$` of a structure's id: in `H$Sales (12)$Amount (40)`, `Amount (40)`.
let afterLastDollar (s: string) = s.Substring(s.LastIndexOf '$' + 1)

type private Run = {
    Query: string -> Result<QueryResult list, SemctlError>
    Warn: string -> unit
}

/// The rows of a query, or None (with a warning) if it failed.
let private rows (run: Run) (what: string) (query: string) : QueryValue array list option =
    match run.Query query with
    | Ok results -> Some(results |> List.collect _.Rows)
    | Error e ->
        run.Warn $"{what} are unknown: {SemctlError.message e}"
        None

let private cell (row: QueryValue array) (i: int) =
    if i < row.Length then row.[i] else QueryValue.Blank

/// Whether a table's statistics can be read with DAX (not DirectQuery, Dual or DirectLake, which
/// StatExtractor leaves to the DMVs by default).
let private importOnly (m: Model) (t: Table) =
    let modeOf (p: Partition) =
        if p.Mode = ModeType.Default then m.DefaultMode else p.Mode

    t.Partitions
    |> Seq.forall (fun p ->
        match modeOf p with
        | ModeType.DirectQuery
        | ModeType.Dual
        | ModeType.DirectLake -> false
        | _ -> true)

/// Read the statistics of the database `name` (its model already read as `db`).
let collect (query: string -> Result<QueryResult list, SemctlError>) (address: string) (db: Database) : Stats =
    let warnings = ResizeArray<string>()
    let run = { Query = query; Warn = warnings.Add }
    let m = db.Model

    let findTable (name: string) =
        m.Tables |> Seq.tryFind (fun t -> eq t.Name name)

    // Tables: rows (DMV, then DAX for the import tables); the DMV id of each.
    let tableRows = Dictionary<string, int64>(StringComparer.OrdinalIgnoreCase)
    let tableIds = Dictionary<int64, string>()

    match rows run "row counts" tablesQuery with
    | Some rs ->
        for r in rs do
            match textOf (cell r 0), textOf (cell r 1), number (cell r 2) with
            | Some t, Some id, Some n when (findTable t).IsSome ->
                tableRows.[t] <- n
                idOf id |> Option.iter (fun i -> tableIds.[i] <- (findTable t).Value.Name)
            | _ -> ()
    | None -> ()

    let daxTables = [
        for t in m.Tables do
            if importOnly m t then
                t.Name
    ]

    let daxRows = Dictionary<string, int64>(StringComparer.OrdinalIgnoreCase)

    for q in tableRowsQueries daxTables do
        match rows run "row counts from DAX" q with
        | Some rs ->
            for r in rs do
                match textOf (cell r 0), number (cell r 1) with
                | Some t, Some n when t.Length > 4 -> daxRows.[t.Substring 4] <- n
                | Some t, None when t.Length > 4 -> daxRows.[t.Substring 4] <- 0L
                | _ -> ()
        | None -> ()

    let rowsOf (t: string) =
        match daxRows.TryGetValue t with
        | true, n -> Some n
        | _ ->
            match tableRows.TryGetValue t with
            | true, n -> Some n
            | _ -> None

    // Columns: DMV id, dictionary, encoding; cardinality; segments; hierarchies.
    let columnId = Dictionary<string * string, string>(HashIdentity.Structural)
    let columnByDmv = Dictionary<string * string, string>(HashIdentity.Structural)
    let dictionary = Dictionary<string * string, int64>(HashIdentity.Structural)
    let encoding = Dictionary<string * string, string>(HashIdentity.Structural)

    let key (t: string) (c: string) =
        t.ToUpperInvariant(), c.ToUpperInvariant()

    let columnsRead =
        match rows run "dictionary sizes" columnsQuery with
        | Some rs ->
            for r in rs do
                match textOf (cell r 0), textOf (cell r 1), textOf (cell r 2) with
                | Some t, Some id, Some c ->
                    columnId.[key t c] <- id
                    columnByDmv.[key t id] <- c

                    number (cell r 3) |> Option.iter (fun n -> dictionary.[key t c] <- n)

                    match number (cell r 4) with
                    | Some 1L -> encoding.[key t c] <- "HASH"
                    | Some 2L -> encoding.[key t c] <- "VALUE"
                    | _ -> ()
                | _ -> ()

            true
        | None -> false

    let byDmv (t: string) (dmvId: string) =
        match columnByDmv.TryGetValue(key t dmvId) with
        | true, c -> Some c
        | _ -> None

    let dmvCardinality = Dictionary<string * string, int64>(HashIdentity.Structural)

    match rows run "cardinalities" cardinalityQuery with
    | Some rs ->
        for r in rs do
            match textOf (cell r 0), textOf (cell r 1), number (cell r 2) with
            | Some t, Some id, Some n ->
                byDmv t (afterLastDollar id)
                |> Option.iter (fun c -> dmvCardinality.[key t c] <- n)
            | _ -> ()
    | None -> ()

    let daxColumns = [
        for t in m.Tables do
            if importOnly m t then
                for c in t.Columns do
                    if c.Type <> ColumnType.RowNumber then
                        t.Name,
                        c.Name,
                        not (isNull c.RelatedColumnDetails)
                        && c.RelatedColumnDetails.GroupByColumns.Count > 0
    ]

    let daxCardinality = Dictionary<string * string, int64>(HashIdentity.Structural)

    for q in columnCardinalityQueries daxColumns do
        match rows run "cardinalities from DAX" q with
        | Some rs ->
            for r in rs do
                match textOf (cell r 0), textOf (cell r 1), number (cell r 2) with
                | Some t, Some c, n when t.Length > 4 && c.Length > 4 ->
                    daxCardinality.[key (t.Substring 4) (c.Substring 4)] <- defaultArg n 0L
                | _ -> ()
        | None -> ()

    // Segments: per column and partition.
    let segments =
        Dictionary<string * string, Dictionary<string, int64 * int64 * int64>>(HashIdentity.Structural)

    let segmentsRead =
        match rows run "data sizes" segmentsQuery with
        | Some rs ->
            for r in rs do
                match textOf (cell r 0), textOf (cell r 1), textOf (cell r 2) with
                | Some t, Some p, Some id ->
                    match byDmv t id with
                    | Some c ->
                        let parts =
                            match segments.TryGetValue(key t c) with
                            | true, d -> d
                            | _ ->
                                let d = Dictionary<string, int64 * int64 * int64>(StringComparer.OrdinalIgnoreCase)
                                segments.[key t c] <- d
                                d

                        let rows0, size0, count0 =
                            match parts.TryGetValue p with
                            | true, x -> x
                            | _ -> 0L, 0L, 0L

                        parts.[p] <-
                            rows0 + defaultArg (number (cell r 4)) 0L,
                            size0 + defaultArg (number (cell r 5)) 0L,
                            count0 + 1L
                    | None -> ()
                | _ -> ()

            true
        | None -> false

    let hierarchy = Dictionary<string * string, int64>(HashIdentity.Structural)

    let hierarchiesRead =
        match rows run "hierarchy sizes" hierarchiesQuery with
        | Some rs ->
            for r in rs do
                match textOf (cell r 0), number (cell r 1), textOf (cell r 2) with
                | Some t, Some n, Some id ->
                    match byDmv t (afterLastDollar id) with
                    | Some c ->
                        let k = key t c
                        hierarchy.[k] <- (if hierarchy.ContainsKey k then hierarchy.[k] else 0L) + n
                    | None -> ()
                | _ -> ()

            true
        | None -> false

    let tables = [
        for t in m.Tables ->
            {
                Table = t.Name
                Kind = VertipaqFixture.tableKind t
                IsHidden = t.IsHidden
                Rows = rowsOf t.Name
            }
    ]

    let columnStats (table: string) (name: string) (kind: string) (dataType: string) (hidden: bool) hint =
        let k = key table name
        let isRowNumber = kind = "rowNumber"

        let get (d: Dictionary<string * string, 'v>) =
            match d.TryGetValue k with
            | true, v -> Some v
            | _ -> None

        let parts =
            match get segments with
            | Some parts -> [
                for KeyValue(p, (r, s, n)) in parts ->
                    {
                        Partition = p
                        Rows = r
                        Segments = n
                        DataSize = s
                    }
              ]
            | None -> []

        let known = columnId.ContainsKey k

        {
            Table = table
            Column = name
            Kind = kind
            DataType = dataType
            IsHidden = hidden
            Cardinality =
                if isRowNumber then
                    None
                else
                    match get daxCardinality with
                    | Some n -> Some n
                    | None -> get dmvCardinality
            DictionarySize =
                if columnsRead && known then
                    Some(defaultArg (get dictionary) 0L)
                else
                    None
            DataSize =
                if segmentsRead && known then
                    Some(parts |> List.sumBy _.DataSize)
                else
                    None
            HierarchySize =
                if hierarchiesRead && known then
                    Some(defaultArg (get hierarchy) 0L)
                else
                    None
            Encoding = get encoding
            EncodingHint = hint
            Partitions = parts
        }

    let columns = [
        for t in m.Tables do
            for c in t.Columns do
                columnStats
                    t.Name
                    c.Name
                    (VertipaqFixture.columnKind c)
                    (c.DataType.ToString())
                    c.IsHidden
                    (Some(c.EncodingHint.ToString()))

            // Every table has a RowNumber column on the server that a model read as files doesn't
            // show; VertiPaq Analyzer counts it in the table's size.
            if not (t.Columns |> Seq.exists (fun c -> c.Type = ColumnType.RowNumber)) then
                for KeyValue((table, _), id) in columnId do
                    if eq table t.Name then
                        match byDmv t.Name id with
                        | Some name when name.StartsWith("RowNumber-", StringComparison.OrdinalIgnoreCase) ->
                            columnStats t.Name name "rowNumber" "Int64" true None
                        | _ -> ()
    ]

    // User hierarchies.
    let hierarchyNames = Dictionary<int64, string>()

    match rows run "user hierarchy names" userHierarchyNamesQuery with
    | Some rs ->
        for r in rs do
            match number (cell r 0), textOf (cell r 1) with
            | Some id, Some n -> hierarchyNames.[id] <- n
            | _ -> ()
    | None -> ()

    let userSizes = Dictionary<string * string, int64>(HashIdentity.Structural)

    let userRead =
        match rows run "user hierarchy sizes" userHierarchiesQuery with
        | Some rs ->
            for r in rs do
                match textOf (cell r 0), number (cell r 1), textOf (cell r 2) with
                | Some t, Some n, Some id ->
                    let short = afterLastDollar id

                    let name =
                        match idOf short with
                        | Some i when hierarchyNames.ContainsKey i -> hierarchyNames.[i]
                        | _ -> short

                    let k = key t name
                    userSizes.[k] <- (if userSizes.ContainsKey k then userSizes.[k] else 0L) + n
                | _ -> ()

            true
        | None -> false

    let hierarchies = [
        for t in m.Tables do
            for h in t.Hierarchies ->
                {
                    Table = t.Name
                    Hierarchy = h.Name
                    Size =
                        if userRead then
                            match userSizes.TryGetValue(key t.Name h.Name) with
                            | true, n -> Some n
                            | _ -> Some 0L
                        else
                            None
                }
    ]

    // Relationships: sizes by DMV id; the DMV ids of their columns; then missing keys and rows.
    let columnIdNumber (t: string) (c: string) =
        match columnId.TryGetValue(key t c) with
        | true, id -> idOf id
        | _ -> None

    let tableIdOf (t: string) =
        tableIds
        |> Seq.tryPick (fun (KeyValue(id, n)) -> if eq n t then Some id else None)

    let relSizes = Dictionary<int64 * int64, int64>(HashIdentity.Structural)

    let sizesRead =
        match rows run "relationship sizes" relationshipSizesQuery with
        | Some rs ->
            for r in rs do
                match textOf (cell r 0), textOf (cell r 1), number (cell r 2) with
                | Some t, Some id, Some n ->
                    match tableIdOf t, idOf id with
                    | Some tid, Some rid -> relSizes.[(tid, rid)] <- n
                    | _ -> ()
                | _ -> ()

            true
        | None -> false

    let relIds =
        Dictionary<(int64 * int64) * (int64 * int64), int64>(HashIdentity.Structural)

    match rows run "relationship sizes (their ids)" relationshipsQuery with
    | Some rs ->
        for r in rs do
            match number (cell r 0), number (cell r 1), number (cell r 2), number (cell r 3), number (cell r 4) with
            | Some id, Some ft, Some fc, Some tt, Some tc -> relIds.[((ft, fc), (tt, tc))] <- id
            | _ -> ()
    | None -> ()

    let baseRelationships = [
        for r in m.Relationships do
            match r with
            | :? SingleColumnRelationship as s when not (isNull s.FromColumn) && not (isNull s.ToColumn) ->
                let fromT, fromC, toT, toC =
                    s.FromTable.Name, s.FromColumn.Name, s.ToTable.Name, s.ToColumn.Name

                let dmvId =
                    match tableIdOf fromT, columnIdNumber fromT fromC, tableIdOf toT, columnIdNumber toT toC with
                    | Some ft, Some fc, Some tt, Some tc ->
                        match relIds.TryGetValue(((ft, fc), (tt, tc))) with
                        | true, id -> Some(ft, tt, id)
                        | _ -> None
                    | _ -> None

                let sizeFrom, sizeTo =
                    match dmvId with
                    | Some(ft, tt, id) when sizesRead ->
                        let get k =
                            match relSizes.TryGetValue k with
                            | true, n -> n
                            | _ -> 0L

                        Some(get (ft, id)), Some(get (tt, id))
                    | _ -> None, None

                let rel = {
                    Name = s.Name
                    FromTable = fromT
                    FromColumn = fromC
                    ToTable = toT
                    ToColumn = toC
                    IsActive = s.IsActive
                    FromCardinalityType = s.FromCardinality.ToString()
                    ToCardinalityType = s.ToCardinality.ToString()
                    MissingKeys = None
                    InvalidRows = None
                    SizeFrom = sizeFrom
                    SizeTo = sizeTo
                }

                rel, importOnly m s.FromTable && importOnly m s.ToTable
            | _ -> ()
    ]

    let indexed = baseRelationships |> List.mapi (fun i (r, dax) -> i + 1, r, dax)
    let ri = Dictionary<int64, int64 * int64>()

    for q in
        relationshipQueries [
            for i, r, dax in indexed do
                if dax then
                    i, r
        ] do
        match rows run "referential-integrity violations" q with
        | Some rs ->
            for r in rs do
                match number (cell r 0) with
                | Some id -> ri.[id] <- (defaultArg (number (cell r 1)) 0L, defaultArg (number (cell r 2)) 0L)
                | None -> ()
        | None -> ()

    let relationships = [
        for i, r, _ in indexed ->
            match ri.TryGetValue(int64 i) with
            | true, (missing, invalid) -> {
                r with
                    MissingKeys = Some missing
                    InvalidRows = Some invalid
              }
            | _ -> r
    ]

    let partitions = [
        for t in m.Tables do
            for p in t.Partitions ->
                let parts =
                    columns
                    |> List.filter (fun c -> eq c.Table t.Name)
                    |> List.choose (fun c -> c.Partitions |> List.tryFind (fun x -> eq x.Partition p.Name))

                {
                    Table = t.Name
                    Partition = p.Name
                    Mode = Some(p.Mode.ToString())
                    Rows =
                        if segmentsRead then
                            Some(parts |> List.map _.Rows |> List.fold max 0L)
                        else
                            None
                    Segments =
                        if segmentsRead then
                            Some(parts |> List.map _.Segments |> List.fold max 0L)
                        else
                            None
                    DataSize =
                        if segmentsRead then
                            Some(parts |> List.sumBy _.DataSize)
                        else
                            None
                }
    ]

    let skipped =
        m.Tables
        |> Seq.filter (fun t -> not (importOnly m t))
        |> Seq.map _.Name
        |> List.ofSeq

    if not skipped.IsEmpty then
        let names = skipped |> List.map (fun n -> $"'{n}'") |> String.concat ", "

        warnings.Add(
            $"DirectQuery, Dual and Direct Lake tables are measured from the DMVs only (no DAX queries): {names}"
        )

    {
        ModelName = VertipaqFixture.modelName db
        Origin = Origin.Server address
        Estimated = false
        Tables = tables
        Columns = columns
        Relationships = relationships
        Hierarchies = hierarchies
        Partitions = partitions
        Warnings = List.ofSeq warnings
    }

/// Connect, find the database, read its model and statistics, and close the connection.
let load (access: Remote.Access) (address: XmlaAddress) : Result<ModelSource * Database * Stats, SemctlError> =
    Remote.withConnection access address (fun c ->
        Remote.resolveDatabase c
        |> Result.bind (fun d ->
            Remote.readDatabase c d.Name
            |> Result.map (fun db ->
                let address = XmlaAddress.withDatabase d.Name address
                let display = XmlaAddress.display address
                let stats = collect (fun q -> Remote.queryOn c d.Name q None) display db
                ModelSource.remote address, db, stats)))
