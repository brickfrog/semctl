/// VertiPaq statistics (`sm vertipaq`): how big each table, column, relationship, user hierarchy
/// and partition of a model is in VertiPaq's storage, and how many rows and distinct values it has.
///
/// One shape for every source: a model's fixture data (VertipaqFixture.fs: counts exact, sizes
/// estimated), a server's DMVs (Semctl.Xmla/VertipaqServer.fs) and a VertiPaq Analyzer file
/// (VertipaqVpax.fs). A statistic that wasn't read is None, never 0: a size is reported, summed,
/// annotated or exported only when it is known.
module Semctl.Vertipaq

open System
open System.Globalization
open Microsoft.AnalysisServices.Tabular

[<RequireQualifiedAccess>]
type Origin =
    /// Model files with fixture data: rows, cardinality and RI are exact, sizes estimated.
    | Fixture of directory: string
    /// A model on a server, read through its DMVs and DAX queries.
    | Server of address: string
    /// A VertiPaq Analyzer file (from DAX Studio, Tabular Editor 3, Bravo or sm).
    | Vpax of path: string

type TableStats = {
    Table: string
    /// "data", "calculated" (a calculated table) or "calculationGroup".
    Kind: string
    IsHidden: bool
    Rows: int64 option
}

/// A column's data in one partition (summed over its segments).
type ColumnPartition = {
    Partition: string
    Rows: int64
    Segments: int64
    DataSize: int64
}

type ColumnStats = {
    Table: string
    Column: string
    /// "data", "calculated", "calculatedTableColumn" or "rowNumber".
    Kind: string
    /// TOM's data type name (Int64, String, ...).
    DataType: string
    IsHidden: bool
    /// Distinct values, BLANK counting as one.
    Cardinality: int64 option
    DictionarySize: int64 option
    DataSize: int64 option
    /// The attribute hierarchy.
    HierarchySize: int64 option
    /// What VertiPaq chose, HASH or VALUE: only a server (or a file from one) knows.
    Encoding: string option
    /// The model's EncodingHint (Default, Hash or Value).
    EncodingHint: string option
    /// Where the data is, by partition; empty when that isn't known.
    Partitions: ColumnPartition list
}

type RelationshipStats = {
    /// The relationship's name in the model (a GUID in most models).
    Name: string
    FromTable: string
    FromColumn: string
    ToTable: string
    ToColumn: string
    IsActive: bool
    /// Many or One.
    FromCardinalityType: string
    ToCardinalityType: string
    /// Distinct keys of the From column with no match in the To column.
    MissingKeys: int64 option
    /// Rows of the From table with no match in the To column (referential-integrity violations).
    InvalidRows: int64 option
    /// The relationship's structure, charged to the From table ...
    SizeFrom: int64 option
    /// ... and to the To table.
    SizeTo: int64 option
}

type HierarchyStats = {
    Table: string
    Hierarchy: string
    Size: int64 option
}

type PartitionStats = {
    Table: string
    Partition: string
    /// Import, DirectQuery, Dual, DirectLake, Default, Push.
    Mode: string option
    Rows: int64 option
    Segments: int64 option
    /// The data of the partition's segments (not dictionaries or hierarchies, which are per column).
    DataSize: int64 option
}

type Stats = {
    ModelName: string
    Origin: Origin
    /// Sizes are estimates (fixture data), not what a server measured.
    Estimated: bool
    Tables: TableStats list
    Columns: ColumnStats list
    Relationships: RelationshipStats list
    Hierarchies: HierarchyStats list
    Partitions: PartitionStats list
    /// What couldn't be read, for the user.
    Warnings: string list
}

let private eq (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

/// The sum of sizes, if every one is known.
let private sumKnown (sizes: int64 option seq) : int64 option =
    sizes
    |> Seq.fold
        (fun acc s ->
            match acc, s with
            | Some a, Some b -> Some(a + b)
            | _ -> None)
        (Some 0L)

module Column =
    /// Dictionary + data + hierarchy.
    let totalSize (c: ColumnStats) =
        sumKnown [ c.DictionarySize; c.DataSize; c.HierarchySize ]

module Relationship =
    let size (r: RelationshipStats) = sumKnown [ r.SizeFrom; r.SizeTo ]

module Stats =
    let columnsOf (s: Stats) (table: string) =
        s.Columns |> List.filter (fun c -> eq c.Table table)

    let hierarchiesOf (s: Stats) (table: string) =
        s.Hierarchies |> List.filter (fun h -> eq h.Table table)

    let partitionsOf (s: Stats) (table: string) =
        s.Partitions |> List.filter (fun p -> eq p.Table table)

    /// The relationships' sizes charged to a table: SizeFrom where it is the From table, SizeTo
    /// where it is the To table (as VertiPaq Analyzer does, so the tables add up to the model).
    let relationshipsSize (s: Stats) (table: string) =
        sumKnown [
            for r in s.Relationships do
                if eq r.FromTable table then
                    r.SizeFrom

                if eq r.ToTable table then
                    r.SizeTo
        ]

    let columnsSize (s: Stats) (table: string) =
        columnsOf s table |> List.map Column.totalSize |> sumKnown

    let hierarchiesSize (s: Stats) (table: string) =
        hierarchiesOf s table |> List.map _.Size |> sumKnown

    /// Columns + user hierarchies + relationships (VertiPaq Analyzer's "Table Size").
    let tableSize (s: Stats) (table: string) =
        sumKnown [ columnsSize s table; hierarchiesSize s table; relationshipsSize s table ]

    let modelSize (s: Stats) =
        s.Tables |> List.map (fun t -> tableSize s t.Table) |> sumKnown

    /// part / whole, when both are known and the whole isn't 0.
    let fraction (part: int64 option) (whole: int64 option) : float option =
        match part, whole with
        | Some p, Some w when w > 0L -> Some(float p / float w)
        | _ -> None

    /// Only the objects of these tables (all, for an empty list); a relationship stays if either
    /// end is one of them.
    let restrict (tables: string list) (s: Stats) : Stats =
        if tables.IsEmpty then
            s
        else
            let keep (t: string) = tables |> List.exists (eq t)

            {
                s with
                    Tables = s.Tables |> List.filter (fun t -> keep t.Table)
                    Columns = s.Columns |> List.filter (fun c -> keep c.Table)
                    Relationships = s.Relationships |> List.filter (fun r -> keep r.FromTable || keep r.ToTable)
                    Hierarchies = s.Hierarchies |> List.filter (fun h -> keep h.Table)
                    Partitions = s.Partitions |> List.filter (fun p -> keep p.Table)
            }

// ---- Estimates ---------------------------------------------------------------------------------

/// How sizes are estimated from data, without a server. VertiPaq's real sizes depend on how it
/// sorts and run-length encodes each segment, which only a refresh decides, so these are a
/// model of the uncompressed layout, assuming hash encoding everywhere:
///   - data: every row holds a value id of ceil(log2(cardinality)) bits (at least 1), so
///     ceil(rows x bits / 8) bytes;
///   - dictionary: 16 bytes per distinct value, plus 2 bytes per character of a text value;
///   - attribute hierarchy: 8 bytes per distinct value (when the column is available in MDX);
///   - user hierarchy: 8 bytes per distinct combination of each level with the levels above it;
///   - relationship: ceil(From cardinality x ceil(log2(To cardinality)) / 8) bytes, charged to the
///     From table.
module Estimate =
    /// Bits for a value id among `cardinality` values: at least 1.
    let bits (cardinality: int64) =
        if cardinality <= 2L then
            1
        else
            int (Math.Ceiling(Math.Log2(float cardinality)))

    let dataSize (rows: int64) (cardinality: int64) =
        if rows = 0L then
            0L
        else
            (rows * int64 (bits cardinality) + 7L) / 8L

    /// `textLength`: the total characters of the distinct values, for a text column.
    let dictionarySize (cardinality: int64) (textLength: int64) = 16L * cardinality + 2L * textLength

    let hierarchySize (availableInMdx: bool) (cardinality: int64) =
        if availableInMdx then 8L * cardinality else 0L

    /// `levelCombinations`: for each level, the distinct combinations of it and the levels above.
    let userHierarchySize (levelCombinations: int64 list) =
        levelCombinations |> List.sumBy (fun n -> 8L * n)

    let relationshipSize (fromCardinality: int64) (toCardinality: int64) =
        (fromCardinality * int64 (bits toCardinality) + 7L) / 8L

// ---- Annotations -------------------------------------------------------------------------------

/// The annotations `--annotate` writes: the names of Michael Kovalsky's VertiPaq Analyzer scripts
/// for Tabular Editor (VertipaqAnnotations.cs, VpaxToTabularEditor.cs), which the Best Practice
/// rules published in microsoft/Analysis-Services read (`GetAnnotation("Vertipaq_Cardinality")`).
module Annotations =
    let modelNames = [ "Vertipaq_ModelSize" ]

    let tableNames = [ "Vertipaq_RowCount"; "Vertipaq_TableSize"; "Vertipaq_TableSizePctOfModel" ]

    let columnNames = [
        "Vertipaq_Cardinality"
        "Vertipaq_ColumnHierarchySize"
        "Vertipaq_ColumnSize"
        "Vertipaq_DataSize"
        "Vertipaq_DictionarySize"
        "Vertipaq_ColumnSizePctOfTable"
        "Vertipaq_ColumnSizePctOfModel"
    ]

    let relationshipNames = [
        "Vertipaq_MaxFromCardinality"
        "Vertipaq_MaxToCardinality"
        "Vertipaq_RelationshipSize"
        "Vertipaq_RIViolationInvalidRows"
    ]

    let hierarchyNames = [ "Vertipaq_UserHierarchySize" ]

    let partitionNames = [
        "Vertipaq_RecordCount"
        "Vertipaq_SegmentCount"
        "Vertipaq_RecordsPerSegment"
    ]

    /// sm's own, on the model: "true" when the sizes are estimates from fixture data.
    let estimated = "Semctl_Vertipaq_Estimated"

    let private integer (n: int64) =
        n.ToString(CultureInfo.InvariantCulture)

    /// A fraction rounded to 3 decimals, as the scripts write it ("0.125"), in the invariant culture.
    let private ratio (f: float) =
        Math.Round(f, 3).ToString("0.###", CultureInfo.InvariantCulture)

    let private values (pairs: (string * string option) list) =
        pairs |> List.choose (fun (n, v) -> v |> Option.map (fun v -> n, v))

    /// What each object should carry, by its object reference: name and value, known values only.
    let wanted (s: Stats) : (ObjectRef * (string * string) list) list =
        let model = Stats.modelSize s

        [
            ObjectRef.model,
            values [
                "Vertipaq_ModelSize", model |> Option.map integer
                estimated, (if s.Estimated then Some "true" else None)
            ]

            for t in s.Tables do
                let size = Stats.tableSize s t.Table

                ObjectRef.table t.Table,
                values [
                    "Vertipaq_RowCount", t.Rows |> Option.map integer
                    "Vertipaq_TableSize", size |> Option.map integer
                    "Vertipaq_TableSizePctOfModel", Stats.fraction size model |> Option.map ratio
                ]

            for c in s.Columns do
                if c.Kind <> "rowNumber" then
                    let size = Column.totalSize c
                    let table = Stats.tableSize s c.Table

                    ObjectRef.inTable ObjectKind.Column c.Table c.Column,
                    values [
                        "Vertipaq_Cardinality", c.Cardinality |> Option.map integer
                        "Vertipaq_ColumnHierarchySize", c.HierarchySize |> Option.map integer
                        "Vertipaq_ColumnSize", size |> Option.map integer
                        "Vertipaq_DataSize", c.DataSize |> Option.map integer
                        "Vertipaq_DictionarySize", c.DictionarySize |> Option.map integer
                        "Vertipaq_ColumnSizePctOfTable", Stats.fraction size table |> Option.map ratio
                        "Vertipaq_ColumnSizePctOfModel", Stats.fraction size model |> Option.map ratio
                    ]

            for h in s.Hierarchies do
                ObjectRef.inTable ObjectKind.Hierarchy h.Table h.Hierarchy,
                values [ "Vertipaq_UserHierarchySize", h.Size |> Option.map integer ]

            for p in s.Partitions do
                ObjectRef.inTable ObjectKind.Partition p.Table p.Partition,
                values [
                    "Vertipaq_RecordCount", p.Rows |> Option.map integer
                    "Vertipaq_SegmentCount", p.Segments |> Option.map integer
                    // As the script computes it: rows / segments, for one segment.
                    "Vertipaq_RecordsPerSegment",
                    (match p.Rows, p.Segments with
                     | Some r, Some 1L -> Some(integer r)
                     | Some _, Some 0L -> Some "0"
                     | _ -> None)
                ]
        ]

    /// The model's relationship with these endpoints (names differ between a model's files and
    /// its server copy, so relationships are matched by their columns).
    let findRelationship (db: Database) (r: RelationshipStats) : SingleColumnRelationship option =
        db.Model.Relationships
        |> Seq.tryPick (fun rel ->
            match rel with
            | :? SingleColumnRelationship as s when
                not (isNull s.FromColumn)
                && not (isNull s.ToColumn)
                && eq s.FromTable.Name r.FromTable
                && eq s.FromColumn.Name r.FromColumn
                && eq s.ToTable.Name r.ToTable
                && eq s.ToColumn.Name r.ToColumn
                ->
                Some s
            | _ -> None)

    let private relationshipValues (s: Stats) (r: RelationshipStats) =
        let card (table: string) (column: string) =
            s.Columns
            |> List.tryFind (fun c -> eq c.Table table && eq c.Column column)
            |> Option.bind _.Cardinality

        values [
            "Vertipaq_MaxFromCardinality", card r.FromTable r.FromColumn |> Option.map integer
            "Vertipaq_MaxToCardinality", card r.ToTable r.ToColumn |> Option.map integer
            "Vertipaq_RelationshipSize", Relationship.size r |> Option.map integer
            "Vertipaq_RIViolationInvalidRows", r.InvalidRows |> Option.map integer
        ]

    /// Set one object's family of annotations to exactly `wanted`: add or update those, remove the
    /// family's others. Returns whether anything changed.
    let private setFamily (o: MetadataObject) (family: string list) (wanted: (string * string) list) =
        match BpaRuleEdit.annotationsOf o with
        | None -> false
        | Some annotations ->
            let mutable changed = false

            for name in family do
                match wanted |> List.tryFind (fun (n, _) -> n = name), annotations.Find name with
                | Some(_, v), None ->
                    annotations.Add(Annotation(Name = name, Value = v))
                    changed <- true
                | Some(_, v), Some a when a.Value <> v ->
                    a.Value <- v
                    changed <- true
                | Some _, Some _ -> ()
                | None, None -> ()
                | None, Some _ ->
                    annotations.Remove name
                    changed <- true

            changed

    type Applied = {
        /// Objects whose annotations changed.
        Changed: int
        /// Statistics of objects the model doesn't have (by reference).
        Unmatched: string list
    }

    /// Write the statistics into the model as annotations. `tables` limits it to those tables'
    /// objects (and leaves the model's own annotations alone); empty means the whole model.
    let apply (tables: string list) (s: Stats) (db: Database) : Applied =
        // Values come from the whole model's statistics (a table's share of the model, the other
        // end of a relationship); the table filter only chooses which objects are written.
        let wanted = wanted s

        let get (r: ObjectRef) =
            wanted |> List.tryFind (fun (o, _) -> o = r) |> Option.map snd

        let mutable changed = 0
        let unmatched = ResizeArray<string>()

        let keep (t: string) =
            tables.IsEmpty || tables |> List.exists (eq t)

        let set (o: MetadataObject) family (r: ObjectRef) =
            if setFamily o family (defaultArg (get r) []) then
                changed <- changed + 1

        let m = db.Model

        if tables.IsEmpty then
            set m (modelNames @ [ estimated ]) ObjectRef.model

        let findTable (name: string) =
            m.Tables |> Seq.tryFind (fun t -> eq t.Name name)

        for t in m.Tables do
            if keep t.Name then
                let statsTable = s.Tables |> List.tryFind (fun x -> eq x.Table t.Name)

                let tableRef =
                    ObjectRef.table (defaultArg (statsTable |> Option.map _.Table) t.Name)

                set t tableNames tableRef

                for c in t.Columns do
                    if c.Type <> ColumnType.RowNumber then
                        let name =
                            s.Columns
                            |> List.tryFind (fun x -> eq x.Table t.Name && eq x.Column c.Name)
                            |> Option.map (fun x -> x.Table, x.Column)
                            |> Option.defaultValue (t.Name, c.Name)

                        set c columnNames (ObjectRef.inTable ObjectKind.Column (fst name) (snd name))

                for h in t.Hierarchies do
                    let name =
                        s.Hierarchies
                        |> List.tryFind (fun x -> eq x.Table t.Name && eq x.Hierarchy h.Name)
                        |> Option.map (fun x -> x.Table, x.Hierarchy)
                        |> Option.defaultValue (t.Name, h.Name)

                    set h hierarchyNames (ObjectRef.inTable ObjectKind.Hierarchy (fst name) (snd name))

                for p in t.Partitions do
                    let name =
                        s.Partitions
                        |> List.tryFind (fun x -> eq x.Table t.Name && eq x.Partition p.Name)
                        |> Option.map (fun x -> x.Table, x.Partition)
                        |> Option.defaultValue (t.Name, p.Name)

                    set p partitionNames (ObjectRef.inTable ObjectKind.Partition (fst name) (snd name))

        // Relationships: matched by their columns.
        let matchedRels = Collections.Generic.HashSet<Relationship>(HashIdentity.Reference)

        for r in s.Relationships |> List.filter (fun r -> keep r.FromTable || keep r.ToTable) do
            match findRelationship db r with
            | Some rel ->
                matchedRels.Add rel |> ignore

                if setFamily rel relationshipNames (relationshipValues s r) then
                    changed <- changed + 1
            | None ->
                unmatched.Add(
                    $"relationship {Dax.quoteTable r.FromTable}[{r.FromColumn}] -> {Dax.quoteTable r.ToTable}[{r.ToColumn}]"
                )

        for rel in m.Relationships do
            let touches =
                match rel with
                | :? SingleColumnRelationship as sc when not (isNull sc.FromTable) && not (isNull sc.ToTable) ->
                    keep sc.FromTable.Name || keep sc.ToTable.Name
                | _ -> tables.IsEmpty

            if touches && not (matchedRels.Contains rel) then
                if setFamily rel relationshipNames [] then
                    changed <- changed + 1

        // Statistics of objects the model doesn't have.
        for t in s.Tables do
            if keep t.Table && (findTable t.Table).IsNone then
                unmatched.Add(ObjectRef.format (ObjectRef.table t.Table))

        for c in s.Columns do
            if c.Kind <> "rowNumber" && keep c.Table then
                match findTable c.Table with
                | Some t when t.Columns |> Seq.exists (fun x -> eq x.Name c.Column) -> ()
                | Some _ -> unmatched.Add(ObjectRef.format (ObjectRef.inTable ObjectKind.Column c.Table c.Column))
                | None -> ()

        {
            Changed = changed
            Unmatched = List.ofSeq unmatched
        }
