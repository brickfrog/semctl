/// VertiPaq statistics from a model's fixture data, offline (`sm vertipaq <model> --data <dir>`).
///
/// Rows, cardinality and referential-integrity violations are exact: counted in the fixture's
/// rows. Data columns are counted in the fixture store itself; calculated columns, calculated
/// tables and calculation groups, which the store doesn't hold, through the local engine's
/// public API (`Engine.evaluateScalar`), with the DAX VertiPaq Analyzer runs on a server. Where
/// the engine refuses, the statistic is unknown and a warning says why. Sizes are estimates
/// (`Vertipaq.Estimate`).
module Semctl.VertipaqFixture

open System
open System.Collections.Generic
open Microsoft.AnalysisServices.Tabular
open Semctl.Vertipaq

let private eq (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

let private column (table: string) (name: string) =
    Dax.quoteTable table + "[" + name.Replace("]", "]]") + "]"

let tableKind (t: Table) =
    if not (isNull t.CalculationGroup) then
        "calculationGroup"
    elif
        t.Partitions
        |> Seq.exists (fun p -> p.SourceType = PartitionSourceType.Calculated)
    then
        "calculated"
    else
        "data"

let columnKind (c: Column) =
    match c.Type with
    | ColumnType.Calculated -> "calculated"
    | ColumnType.CalculatedTableColumn -> "calculatedTableColumn"
    | ColumnType.RowNumber -> "rowNumber"
    | _ -> "data"

/// The database's name, or the model's (a PBIP's database has none).
let modelName (db: Database) =
    if not (String.IsNullOrEmpty db.Name) then
        db.Name
    elif not (isNull db.Model) && not (String.IsNullOrEmpty db.Model.Name) then
        db.Model.Name
    else
        "Model"

/// What a column holds, as far as the estimate needs it.
type private Counted = {
    Rows: int64
    Cardinality: int64
    /// Total characters of the distinct values (text columns; 0 otherwise).
    TextLength: int64
    /// Each row's value id, when the column is in the store (for user hierarchies).
    Ids: int array option
}

let private asInt (v: QueryValue) : int64 option =
    match v with
    | QueryValue.Blank -> Some 0L
    | QueryValue.Int64 n -> Some n
    | QueryValue.Decimal d -> Some(int64 d)
    | QueryValue.Double d when Math.Floor d = d -> Some(int64 d)
    | _ -> None

/// Statistics from fixture data that has already been loaded and checked (no errors).
let collect (db: Database) (loaded: Fixture.Loaded) : Stats =
    let store = loaded.Store
    let warnings = ResizeArray<string>()

    let needsEngine =
        db.Model.Tables
        |> Seq.exists (fun t ->
            tableKind t <> "data"
            || t.Columns |> Seq.exists (fun c -> c.Type = ColumnType.Calculated))

    let prepared =
        lazy
            (match Engine.prepare db store with
             | Ok p -> Some p
             | Error e ->
                 warnings.Add(
                     $"calculated columns and tables have no statistics: the local engine can't evaluate the model ({SemctlError.message e})"
                 )

                 None)

    /// An integer the engine computes, or None (with a warning naming `what`).
    let scalar (what: string) (dax: string) : int64 option =
        if not needsEngine then
            None
        else
            match prepared.Value with
            | None -> None
            | Some p ->
                match Engine.evaluateScalar p dax with
                | Ok v ->
                    match asInt v with
                    | Some n -> Some n
                    | None ->
                        warnings.Add($"{what}: the engine returned a value that isn't a count")
                        None
                | Error e ->
                    warnings.Add($"{what} has no statistics: {SemctlError.message e}")
                    None

    let tableRows = Dictionary<string, int64 option>(StringComparer.OrdinalIgnoreCase)
    let counted = Dictionary<string * string, Counted option>(HashIdentity.Structural)

    let key (t: string) (c: string) =
        t.ToUpperInvariant(), c.ToUpperInvariant()

    let tables = [
        for t in db.Model.Tables ->
            let kind = tableKind t

            let rows =
                match kind, store.TryTable t.Name with
                | "data", Some st -> Some(int64 st.RowCount)
                | "data", None -> Some 0L
                | _ -> scalar $"table '{t.Name}'" $"COUNTROWS ( {Dax.quoteTable t.Name} )"

            tableRows.[t.Name] <- rows

            {
                Table = t.Name
                Kind = kind
                IsHidden = t.IsHidden
                Rows = rows
            }
    ]

    let columns = [
        for t in db.Model.Tables do
            let rows = tableRows.[t.Name]
            let inStore = if tableKind t = "data" then store.TryTable t.Name else None

            for c in t.Columns do
                if c.Type <> ColumnType.RowNumber then
                    let fromStore =
                        match c.Type, inStore with
                        | ColumnType.Data, Some st ->
                            st.TryColumn c.Name
                            |> Option.map (fun sc ->
                                let text =
                                    if sc.DataType = DaxType.String then
                                        sc.Values
                                        |> Array.sumBy (fun v ->
                                            match v with
                                            | DaxValue.String s -> int64 s.Length
                                            | _ -> 0L)
                                    else
                                        0L

                                {
                                    Rows = int64 sc.RowCount
                                    Cardinality = int64 sc.DistinctCount
                                    TextLength = text
                                    Ids = Some sc.ValueIds
                                })
                        | _ -> None

                    let count =
                        match fromStore with
                        | Some x -> Some x
                        | None when c.Type = ColumnType.Data && tableKind t = "data" ->
                            // A column the fixture can't hold (a Binary or Variant column).
                            None
                        | None ->
                            let ref = column t.Name c.Name
                            let what = $"column {ref}"

                            // A field parameter's column (Group By Columns) can't be grouped by
                            // alone; VertiPaq Analyzer counts its attribute hierarchy instead.
                            let grouped =
                                not (isNull c.RelatedColumnDetails)
                                && c.RelatedColumnDetails.GroupByColumns.Count > 0

                            let values =
                                if grouped then
                                    $"ALLNOBLANKROW ( {ref} )"
                                else
                                    $"DISTINCT ( {ref} )"

                            let distinct =
                                if grouped then
                                    $"COUNTROWS ( {values} )"
                                else
                                    $"DISTINCTCOUNT ( {ref} )"

                            match rows, scalar what distinct with
                            | Some r, Some card ->
                                let text =
                                    if c.DataType = DataType.String then
                                        scalar what $"SUMX ( {values}, LEN ( {ref} ) )"
                                    else
                                        Some 0L

                                text
                                |> Option.map (fun text -> {
                                    Rows = r
                                    Cardinality = card
                                    TextLength = text
                                    Ids = None
                                })
                            | _ -> None

                    counted.[key t.Name c.Name] <- count

                    {
                        Table = t.Name
                        Column = c.Name
                        Kind = columnKind c
                        DataType = c.DataType.ToString()
                        IsHidden = c.IsHidden
                        Cardinality = count |> Option.map _.Cardinality
                        DictionarySize =
                            count
                            |> Option.map (fun x -> Estimate.dictionarySize x.Cardinality x.TextLength)
                        DataSize = count |> Option.map (fun x -> Estimate.dataSize x.Rows x.Cardinality)
                        HierarchySize =
                            count
                            |> Option.map (fun x -> Estimate.hierarchySize c.IsAvailableInMDX x.Cardinality)
                        Encoding = None
                        EncodingHint = Some(c.EncodingHint.ToString())
                        Partitions =
                            match count, List.ofSeq t.Partitions with
                            | Some x, [ p ] -> [
                                {
                                    Partition = p.Name
                                    Rows = x.Rows
                                    Segments = 1L
                                    DataSize = Estimate.dataSize x.Rows x.Cardinality
                                }
                              ]
                            | _ -> []
                    }
    ]

    let cardinalityOf (t: string) (c: string) =
        match counted.TryGetValue(key t c) with
        | true, Some x -> Some x.Cardinality
        | _ -> None

    let storeRelationships = DataStore.relationshipsOf db

    let relationships = [
        for r in db.Model.Relationships do
            match r with
            | :? SingleColumnRelationship as s when not (isNull s.FromColumn) && not (isNull s.ToColumn) ->
                let fromT, fromC, toT, toC =
                    s.FromTable.Name, s.FromColumn.Name, s.ToTable.Name, s.ToColumn.Name

                let viaStore =
                    storeRelationships
                    |> List.tryFind (fun sr -> sr.Name = s.Name)
                    |> Option.bind store.Match
                    |> Option.map (fun m ->
                        let orphans = m.OrphanFromRows
                        let ids = m.Resolved.FromColumn.ValueIds
                        let missing = HashSet<int>()

                        for row in orphans do
                            missing.Add ids.[row] |> ignore

                        int64 missing.Count, int64 orphans.Length)

                let missing, invalid =
                    match viaStore with
                    | Some(m, i) -> Some m, Some i
                    | None ->
                        // An end the store doesn't hold: VertiPaq Analyzer's own query.
                        let f, k = column fromT fromC, column toT toC
                        let filter = $"ISBLANK ( {k} ), USERELATIONSHIP ( {f}, {k} )"
                        let what = $"relationship {f} -> {k}"

                        scalar what $"CALCULATE ( DISTINCTCOUNT ( {f} ), {filter} )",
                        scalar what $"CALCULATE ( COUNTROWS ( {Dax.quoteTable fromT} ), {filter} )"

                let size =
                    match cardinalityOf fromT fromC, cardinalityOf toT toC with
                    | Some f, Some t -> Some(Estimate.relationshipSize f t)
                    | _ -> None

                {
                    Name = s.Name
                    FromTable = fromT
                    FromColumn = fromC
                    ToTable = toT
                    ToColumn = toC
                    IsActive = s.IsActive
                    FromCardinalityType = s.FromCardinality.ToString()
                    ToCardinalityType = s.ToCardinality.ToString()
                    MissingKeys = missing
                    InvalidRows = invalid
                    SizeFrom = size
                    SizeTo = size |> Option.map (fun _ -> 0L)
                }
            | _ -> ()
    ]

    let hierarchies = [
        for t in db.Model.Tables do
            for h in t.Hierarchies ->
                let levels =
                    h.Levels
                    |> Seq.sortBy _.Ordinal
                    |> Seq.map (fun l ->
                        if isNull l.Column then
                            None
                        else
                            match counted.TryGetValue(key t.Name l.Column.Name) with
                            | true, Some x -> x.Ids
                            | _ -> None)
                    |> List.ofSeq

                let levels =
                    List.foldBack
                        (fun item acc ->
                            match item, acc with
                            | Some x, Some xs -> Some(x :: xs)
                            | _ -> None)
                        levels
                        (Some [])

                let size =
                    match levels with
                    | Some(first :: _ as levels) ->
                        let rows = first.Length
                        let seen = HashSet<int64 list>(HashIdentity.Structural)

                        let combos = [
                            for depth in 1 .. levels.Length ->
                                seen.Clear()
                                let prefix = List.truncate depth levels

                                for row in 0 .. rows - 1 do
                                    seen.Add(prefix |> List.map (fun ids -> int64 ids.[row])) |> ignore

                                int64 seen.Count
                        ]

                        Some(Estimate.userHierarchySize combos)
                    | _ -> None

                {
                    Table = t.Name
                    Hierarchy = h.Name
                    Size = size
                }
    ]

    let several = ResizeArray<string>()

    let partitions = [
        for t in db.Model.Tables do
            let one = t.Partitions.Count = 1

            if not one && t.Partitions.Count > 1 then
                several.Add t.Name

            let dataSize =
                columns
                |> List.filter (fun c -> eq c.Table t.Name)
                |> List.map _.DataSize
                |> List.fold
                    (fun acc s ->
                        match acc, s with
                        | Some a, Some b -> Some(a + b)
                        | _ -> None)
                    (Some 0L)

            for p in t.Partitions ->
                {
                    Table = t.Name
                    Partition = p.Name
                    Mode = Some(p.Mode.ToString())
                    Rows = if one then tableRows.[t.Name] else None
                    Segments = None
                    DataSize = if one then dataSize else None
                }
    ]

    if several.Count > 0 then
        let names = several |> Seq.map (fun n -> $"'{n}'") |> String.concat ", "

        warnings.Add(
            $"tables with several partitions have no per-partition statistics (a fixture has one file per table): {names}"
        )

    {
        ModelName = modelName db
        Origin = Origin.Fixture loaded.Directory
        Estimated = true
        Tables = tables
        Columns = columns
        Relationships = relationships
        Hierarchies = hierarchies
        Partitions = partitions
        Warnings = List.ofSeq warnings
    }

/// Load and check a fixture, refuse it if it has errors (their values would be counted as BLANK),
/// and collect the statistics.
let fromDirectory (db: Database) (directory: string) : Result<Stats, SemctlError> =
    Fixture.loadAndCheck db directory
    |> Result.bind (fun (loaded, issues) ->
        let errors =
            issues
            |> List.filter (fun i -> i.Severity = Validate.Severity.Error)
            |> List.length

        if errors > 0 then
            Error(FixtureHasErrors(loaded.Directory, errors))
        else
            Ok(collect db loaded))
