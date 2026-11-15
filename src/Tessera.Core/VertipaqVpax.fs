/// VertiPaq Analyzer files (.vpax), through SQLBI's own libraries (Dax.Vpax, MIT): reading one made
/// by DAX Studio, Tabular Editor 3, Bravo or tess, writing one they can open, and obfuscating it
/// (Dax.Vpax.Obfuscator, MIT) as Bravo does.
///
/// A .vpax is a zip (an OPC package) of DaxModel.json (Dax.Metadata's model: the statistics and
/// the model's metadata), DaxVpaView.json (the same as flat tables, for older readers), Model.bim
/// (the model as TOM JSON) and CompatabilityMode.txt (sic, part of the format).
module Tessera.VertipaqVpax

open System
open System.IO
open System.IO.Compression
open System.Text.Json
open Microsoft.AnalysisServices.Tabular
open Tessera.Vertipaq

type DaxModel = Dax.Metadata.Model

let private eq (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

/// `ExtractorLib` of a file tess writes: tells estimates from measurements when it is read back.
let estimatedLib = "Tessera.Vertipaq.Fixture"

let measuredLib = "Tessera.Vertipaq"

let private name (n: Dax.Metadata.DaxName) = if isNull n then "" else n.Name

// ---- Reading -----------------------------------------------------------------------------------

/// Whether DaxModel.json says the statistics were not read from data (DAX Studio's "read
/// statistics from data" off). Files from before ExtractorProperties existed don't say.
let private statisticsSkipped (path: string) =
    try
        use zip = ZipFile.OpenRead path

        match zip.GetEntry "DaxModel.json" with
        | null -> false
        | entry ->
            use stream = entry.Open()
            use doc = JsonDocument.Parse(stream)

            match doc.RootElement.TryGetProperty "ExtractorProperties" with
            | true, props when props.ValueKind = JsonValueKind.Object ->
                match props.TryGetProperty "StatisticsEnabled" with
                | true, v when v.ValueKind = JsonValueKind.False -> true
                | _ -> false
            | _ -> false
    with _ ->
        false

/// The statistics in a Dax.Metadata model.
let toStats (origin: Origin) (estimated: bool) (riRead: bool) (m: DaxModel) : Stats =
    let tableKind (t: Dax.Metadata.Table) =
        match t.TableType with
        | "CalculatedTable" -> "calculated"
        | "CalculationGroup" -> "calculationGroup"
        | _ -> "data"

    let columnKind (c: Dax.Metadata.Column) =
        if c.IsRowNumber then
            "rowNumber"
        else
            match c.ColumnType with
            | "Calculated" -> "calculated"
            | "CalculatedTableColumn" -> "calculatedTableColumn"
            | "RowNumber" -> "rowNumber"
            | _ -> "data"

    let partsOf (c: Dax.Metadata.Column) =
        c.ColumnSegments
        |> Seq.filter (fun s -> not (isNull s.Partition))
        |> Seq.groupBy (fun s -> name s.Partition.PartitionName)
        |> Seq.map (fun (p, segs) -> {
            Partition = p
            Rows = segs |> Seq.sumBy _.SegmentRows
            Segments = int64 (Seq.length segs)
            DataSize = segs |> Seq.sumBy _.UsedSize
        })
        |> List.ofSeq

    let tables = List.ofSeq m.Tables

    {
        ModelName = name m.ModelName
        Origin = origin
        Estimated = estimated
        Tables = [
            for t in tables ->
                {
                    Table = name t.TableName
                    Kind = tableKind t
                    IsHidden = t.IsHidden
                    Rows = Some t.RowsCount
                }
        ]
        Columns = [
            for t in tables do
                for c in t.Columns ->
                    let unread = c.ColumnCardinality = 0L && t.RowsCount > 0L
                    // tess writes 0 for what it couldn't estimate: in its own files, a column
                    // without a cardinality has no sizes either.
                    let size (n: int64) = if unread && estimated then None else Some n

                    {
                        Table = name t.TableName
                        Column = name c.ColumnName
                        Kind = columnKind c
                        DataType = c.DataType
                        IsHidden = c.IsHidden
                        // A cardinality of 0 means "not read" (the RowNumber columns): a column with
                        // rows has at least one value.
                        Cardinality = if unread then None else Some c.ColumnCardinality
                        DictionarySize = size c.DictionarySize
                        DataSize = size c.DataSize
                        HierarchySize = size c.HierarchiesSize
                        Encoding = Option.ofObj c.Encoding |> Option.filter (fun e -> e <> "" && e <> "UNKNOWN")
                        EncodingHint = Option.ofObj c.EncodingHint
                        Partitions = partsOf c
                    }
        ]
        Relationships = [
            for r in m.Relationships do
                if not (isNull r.FromColumn) && not (isNull r.ToColumn) then
                    {
                        Name = r.Name
                        FromTable = name r.FromColumn.Table.TableName
                        FromColumn = name r.FromColumn.ColumnName
                        ToTable = name r.ToColumn.Table.TableName
                        ToColumn = name r.ToColumn.ColumnName
                        IsActive = r.IsActive
                        FromCardinalityType = r.FromCardinalityType
                        ToCardinalityType = r.ToCardinalityType
                        MissingKeys = if riRead then Some r.MissingKeys else None
                        InvalidRows = if riRead then Some r.InvalidRows else None
                        SizeFrom = Some r.UsedSizeFrom
                        SizeTo = Some r.UsedSizeTo
                    }
        ]
        Hierarchies = [
            for t in tables do
                for h in t.UserHierarchies ->
                    {
                        Table = name t.TableName
                        Hierarchy = name h.HierarchyName
                        Size = Some h.UsedSize
                    }
        ]
        Partitions = [
            for t in tables do
                for p in t.Partitions ->
                    let pname = name p.PartitionName

                    let segs =
                        t.Columns
                        |> Seq.map (fun c ->
                            c.ColumnSegments
                            |> Seq.filter (fun s -> not (isNull s.Partition) && name s.Partition.PartitionName = pname)
                            |> List.ofSeq)
                        |> List.ofSeq

                    // As VertiPaq Analyzer's partition view: rows and segments the most of any
                    // column, data the sum.
                    {
                        Table = name t.TableName
                        Partition = pname
                        Mode = p.Mode |> Option.ofNullable |> Option.map string
                        Rows = Some(segs |> List.map (List.sumBy _.SegmentRows) |> List.fold max 0L)
                        Segments = Some(segs |> List.map (fun s -> int64 s.Length) |> List.fold max 0L)
                        DataSize = Some(segs |> List.sumBy (List.sumBy _.UsedSize))
                    }
        ]
        Warnings = []
    }

type Imported = {
    Stats: Stats
    /// The file's model, to write again (`--import a.vpax --export b.vpax`).
    DaxModel: DaxModel
    /// The model as TOM, if the file has it (an obfuscated file doesn't).
    Database: Database option
}

/// Read a .vpax file.
let read (path: string) : Result<Imported, TessError> =
    let full = Path.GetFullPath path

    if not (File.Exists full) then
        Error(PathNotFound full)
    else
        try
            let content = Dax.Vpax.Tools.VpaxTools.ImportVpax(full, importDatabase = true)

            match content.DaxModel with
            | null -> Error(LoadFailed(full, "not a VertiPaq Analyzer file: it has no DaxModel.json"))
            | m ->
                let estimated = m.ExtractorLib = estimatedLib
                let skipped = statisticsSkipped full

                let stats = toStats (Origin.Vpax full) estimated (not skipped) m

                let warnings = [
                    if skipped then
                        "the file was made without reading statistics from data: missing keys and invalid rows are unknown"
                    if not (String.IsNullOrEmpty m.ObfuscatorDictionaryId) then
                        "the file is obfuscated: names are not the model's"
                ]

                Ok {
                    Stats = { stats with Warnings = warnings }
                    DaxModel = m
                    Database = Option.ofObj content.TomDatabase
                }
        with
        | :? InvalidDataException as e -> Error(LoadFailed(full, $"not a VertiPaq Analyzer file: {e.Message}"))
        | :? IOException as e -> Error(LoadFailed(full, e.Message))
        | e -> Error(LoadFailed(full, $"not a VertiPaq Analyzer file: {e.Message}"))

// ---- Writing -----------------------------------------------------------------------------------

/// The model's metadata (following Dax.Model.Extractor's TomExtractor) with the statistics in it.
/// Unknown statistics are written as 0, which is how the format says "not read"; the second
/// result names them. A column whose partitions aren't known has its data in one segment of the
/// table's first partition.
let toDaxModel (version: string) (s: Stats) (db: Database) : DaxModel * string list =
    let lib = if s.Estimated then estimatedLib else measuredLib
    let m = DaxModel(lib, version, "tess", version)
    let unknown = Collections.Generic.List<string>()
    let tom = db.Model
    m.ModelName <- Dax.Metadata.DaxName(db.Name)
    m.CompatibilityLevel <- db.CompatibilityLevel
    m.CompatibilityMode <- db.CompatibilityMode.ToString()
    m.ExtractionDate <- DateTime.UtcNow
    m.LastUpdate <- db.LastUpdate
    m.LastProcessed <- db.LastProcessed
    m.Version <- db.Version
    m.Culture <- tom.Culture
    m.DefaultMode <- enum<Dax.Metadata.Partition.PartitionMode>(int tom.DefaultMode)
    m.ExtractorProperties.StatisticsEnabled <- true

    match s.Origin with
    | Origin.Server address -> m.ServerName <- Dax.Metadata.DaxName(address)
    | _ -> ()

    let known (what: string) (v: int64 option) =
        match v with
        | Some n -> n
        | None ->
            unknown.Add what
            0L

    let note (text: string) =
        if String.IsNullOrEmpty text then
            null
        else
            Dax.Metadata.DaxNote(text)

    let expr (text: string) =
        Dax.Metadata.DaxExpression.GetExpression text

    for t in tom.Tables do
        let st = s.Tables |> List.tryFind (fun x -> eq x.Table t.Name)
        let kind = VertipaqFixture.tableKind t
        let dt = Dax.Metadata.Table(m)
        dt.TableName <- Dax.Metadata.DaxName(t.Name)
        dt.IsHidden <- t.IsHidden
        dt.IsPrivate <- t.IsPrivate
        dt.Description <- note t.Description

        dt.TableType <-
            match kind with
            | "calculated" -> "CalculatedTable"
            | "calculationGroup" -> "CalculationGroup"
            | _ -> null

        dt.TableExpression <-
            t.Partitions
            |> Seq.tryPick (fun p ->
                match p.Source with
                | :? CalculatedPartitionSource as c -> Some(expr c.Expression)
                | _ -> None)
            |> Option.toObj

        dt.IsDateTable <- Nullable(t.DataCategory = "Time")
        dt.RowsCount <- known $"rows of '{t.Name}'" (st |> Option.bind _.Rows)

        let partitions = [
            for i, p in Seq.indexed t.Partitions ->
                let dp = Dax.Metadata.Partition(dt)
                dp.PartitionName <- Dax.Metadata.DaxName(p.Name)
                dp.PartitionNumber <- int64 i
                dp.Description <- note p.Description
                dp.Mode <- Nullable(enum<Dax.Metadata.Partition.PartitionMode>(int p.Mode))
                dp.Type <- Nullable(enum<Dax.Metadata.Partition.PartitionType>(int p.SourceType))
                dt.Partitions.Add dp
                dp
        ]

        for c in t.Columns do
            let sc =
                s.Columns |> List.tryFind (fun x -> eq x.Table t.Name && eq x.Column c.Name)

            let dc = Dax.Metadata.Column(dt)
            dc.ColumnName <- Dax.Metadata.DaxName(c.Name)
            dc.DataType <- c.DataType.ToString()
            dc.ColumnType <- c.Type.ToString()
            dc.IsRowNumber <- (c.Type = ColumnType.RowNumber)
            dc.IsHidden <- c.IsHidden
            dc.EncodingHint <- c.EncodingHint.ToString()
            dc.IsAvailableInMDX <- c.IsAvailableInMDX
            dc.IsKey <- c.IsKey
            dc.IsNullable <- c.IsNullable
            dc.IsUnique <- c.IsUnique
            dc.KeepUniqueRows <- c.KeepUniqueRows
            dc.State <- c.State.ToString()
            dc.FormatString <- c.FormatString
            dc.DisplayFolder <- note c.DisplayFolder
            dc.Description <- note c.Description

            if not (isNull c.SortByColumn) then
                dc.SortByColumnName <- Dax.Metadata.DaxName(c.SortByColumn.Name)

            match c with
            | :? CalculatedColumn as cc -> dc.ColumnExpression <- expr cc.Expression
            | _ -> ()

            match sc with
            | Some sc ->
                let what = $"{Dax.quoteTable t.Name}[{c.Name}]"
                dc.ColumnCardinality <- known $"cardinality of {what}" sc.Cardinality
                dc.DictionarySize <- known $"dictionary of {what}" sc.DictionarySize
                dc.Encoding <- Option.toObj sc.Encoding

                let hierarchy = Dax.Metadata.ColumnHierarchy(dc)
                hierarchy.StructureName <- Dax.Metadata.DaxName($"H${t.Name}-{c.Name}")
                hierarchy.UsedSize <- known $"hierarchy of {what}" sc.HierarchySize
                dc.ColumnHierarchies.Add hierarchy

                let parts =
                    match sc.Partitions, sc.DataSize, partitions with
                    | [], Some size, first :: _ -> [
                        first, (st |> Option.bind _.Rows |> Option.defaultValue 0L), 1L, size
                      ]
                    | [], None, _ ->
                        unknown.Add $"data of {what}"
                        []
                    | parts, _, _ ->
                        parts
                        |> List.choose (fun part ->
                            partitions
                            |> List.tryFind (fun dp -> eq dp.PartitionName.Name part.Partition)
                            |> Option.map (fun dp -> dp, part.Rows, part.Segments, part.DataSize))

                for dp, rows, segments, size in parts do
                    // One segment per partition, holding the partition's whole share of the column.
                    let seg = Dax.Metadata.ColumnSegment(dc, dp)
                    seg.SegmentNumber <- 0L
                    seg.SegmentRows <- rows
                    seg.UsedSize <- size
                    seg.CompressionType <- null
                    seg.VertipaqState <- if segments > 0L then "COMPLETED" else null
                    dc.ColumnSegments.Add seg
            | None -> ()

            dt.Columns.Add dc

        for ms in t.Measures do
            let dm = Dax.Metadata.Measure()
            dm.Table <- dt
            dm.MeasureName <- Dax.Metadata.DaxName(ms.Name)
            dm.MeasureExpression <- expr ms.Expression
            dm.DisplayFolder <- note ms.DisplayFolder
            dm.Description <- note ms.Description
            dm.IsHidden <- ms.IsHidden
            dm.DataType <- ms.DataType.ToString()
            dm.FormatString <- ms.FormatString

            if not (isNull ms.FormatStringDefinition) then
                dm.FormatStringExpression <- expr ms.FormatStringDefinition.Expression

            dt.Measures.Add dm

        for h in t.Hierarchies do
            let dh = Dax.Metadata.UserHierarchy(dt)
            dh.HierarchyName <- Dax.Metadata.DaxName(h.Name)
            dh.IsHidden <- h.IsHidden

            dh.UsedSize <-
                known
                    $"hierarchy '{t.Name}'[{h.Name}]"
                    (s.Hierarchies
                     |> List.tryFind (fun x -> eq x.Table t.Name && eq x.Hierarchy h.Name)
                     |> Option.bind _.Size)

            for l in h.Levels |> Seq.sortBy _.Ordinal do
                if not (isNull l.Column) then
                    match dt.Columns |> Seq.tryFind (fun c -> c.ColumnName.Name = l.Column.Name) with
                    | Some c -> dh.Levels.Add c
                    | None -> ()

            dt.UserHierarchies.Add dh

        if not (isNull t.CalculationGroup) then
            let cg = Dax.Metadata.CalculationGroup(dt)
            cg.Precedence <- t.CalculationGroup.Precedence
            cg.Description <- note t.CalculationGroup.Description

            for item in t.CalculationGroup.CalculationItems do
                let ci = Dax.Metadata.CalculationItem(cg)
                ci.ItemName <- Dax.Metadata.DaxName(item.Name)
                ci.ItemExpression <- expr item.Expression
                ci.Description <- note item.Description

                if not (isNull item.FormatStringDefinition) then
                    ci.FormatStringDefinition <- expr item.FormatStringDefinition.Expression

                cg.CalculationItems.Add ci

            dt.CalculationGroup <- cg

            match dt.Columns |> Seq.tryFind (fun c -> not c.IsRowNumber) with
            | Some c -> c.IsCalculationGroupAttribute <- true
            | None -> ()

        m.Tables.Add dt

    let findColumn (table: string) (column: string) =
        m.Tables
        |> Seq.tryFind (fun t -> eq t.TableName.Name table)
        |> Option.bind (fun t -> t.Columns |> Seq.tryFind (fun c -> eq c.ColumnName.Name column))

    for r in tom.Relationships do
        match r with
        | :? SingleColumnRelationship as rel when not (isNull rel.FromColumn) && not (isNull rel.ToColumn) ->
            match findColumn rel.FromTable.Name rel.FromColumn.Name, findColumn rel.ToTable.Name rel.ToColumn.Name with
            | Some f, Some t ->
                let dr = Dax.Metadata.Relationship(f, t)
                dr.Name <- rel.Name
                dr.FromCardinalityType <- rel.FromCardinality.ToString()
                dr.ToCardinalityType <- rel.ToCardinality.ToString()
                dr.RelyOnReferentialIntegrity <- rel.RelyOnReferentialIntegrity
                dr.JoinOnDateBehavior <- rel.JoinOnDateBehavior.ToString()
                dr.CrossFilteringBehavior <- rel.CrossFilteringBehavior.ToString()
                dr.Type <- rel.Type.ToString()
                dr.IsActive <- rel.IsActive
                dr.SecurityFilteringBehavior <- rel.SecurityFilteringBehavior.ToString()

                let sr =
                    s.Relationships
                    |> List.tryFind (fun x ->
                        eq x.FromTable rel.FromTable.Name
                        && eq x.FromColumn rel.FromColumn.Name
                        && eq x.ToTable rel.ToTable.Name
                        && eq x.ToColumn rel.ToColumn.Name)

                let what =
                    $"relationship {Dax.quoteTable rel.FromTable.Name}[{rel.FromColumn.Name}] -> {Dax.quoteTable rel.ToTable.Name}[{rel.ToColumn.Name}]"

                dr.UsedSizeFrom <- known $"size of {what}" (sr |> Option.bind _.SizeFrom)
                dr.UsedSizeTo <- sr |> Option.bind _.SizeTo |> Option.defaultValue 0L
                dr.MissingKeys <- known $"missing keys of {what}" (sr |> Option.bind _.MissingKeys)
                dr.InvalidRows <- known $"invalid rows of {what}" (sr |> Option.bind _.InvalidRows)
                m.Relationships.Add dr
            | _ -> ()
        | _ -> ()

    for role in tom.Roles do
        let dr = Dax.Metadata.Role(m)
        dr.RoleName <- Dax.Metadata.DaxName(role.Name)

        for tp in role.TablePermissions do
            match m.Tables |> Seq.tryFind (fun t -> eq t.TableName.Name tp.Table.Name) with
            | Some t ->
                let p = Dax.Metadata.TablePermission(dr)
                p.Table <- t
                p.FilterExpression <- expr tp.FilterExpression
                dr.TablePermissions.Add p
            | None -> ()

        m.Roles.Add dr

    m, List.ofSeq unknown

/// Obfuscate a model in place (names, expressions, descriptions), extending `dictionary` if one
/// is given (incremental obfuscation, so names stay the same across files). Returns the dictionary
/// that maps the obfuscated names back.
let obfuscate
    (dictionary: string option)
    (m: DaxModel)
    : Result<Dax.Vpax.Obfuscator.Common.ObfuscationDictionary, TessError> =
    let previous =
        match dictionary with
        | None -> Ok None
        | Some path ->
            let full = Path.GetFullPath path

            if not (File.Exists full) then
                Error(PathNotFound full)
            else
                try
                    Ok(Some(Dax.Vpax.Obfuscator.Common.ObfuscationDictionary.ReadFrom full))
                with e ->
                    Error(LoadFailed(full, $"not an obfuscation dictionary: {e.Message}"))

    previous
    |> Result.bind (fun previous ->
        try
            let obfuscator = Dax.Vpax.Obfuscator.VpaxObfuscator()

            match previous with
            | Some d -> Ok(obfuscator.Obfuscate(m, d))
            | None -> Ok(obfuscator.Obfuscate(m))
        with e ->
            Error(SaveFailed("(obfuscation)", e.Message)))

/// Write a .vpax file (replacing one there). With `database`, the file also carries the model as
/// TOM (Model.bim) and the flat view (DaxVpaView.json); an obfuscated file must carry neither.
let write (path: string) (m: DaxModel) (database: Database option) : Result<string, TessError> =
    let full = Path.GetFullPath path
    let temp = full + ".tmp-" + Guid.NewGuid().ToString("N")

    try
        try
            match database with
            | Some db -> Dax.Vpax.Tools.VpaxTools.ExportVpax(temp, m, Dax.ViewVpaExport.Model(m), db)
            | None -> Dax.Vpax.Tools.VpaxTools.ExportVpax(temp, m, null, null)

            File.Move(temp, full, true)
            Ok full
        with e ->
            Error(SaveFailed(full, e.Message))
    finally
        if File.Exists temp then
            File.Delete temp
