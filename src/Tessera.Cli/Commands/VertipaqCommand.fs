/// `tess vertipaq`: VertiPaq storage statistics (Tabular Editor 3's `te vertipaq`, VertiPaq
/// Analyzer's views): from model files with fixture data (offline, sizes estimated), from a model
/// on a server, or from a .vpax file; written to a .vpax, or into the model as annotations.
module Tessera.Cli.VertipaqCommand

open System
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open Argu
open Microsoft.AnalysisServices.Tabular
open Tessera
open Tessera.Vertipaq
open Tessera.Xmla
open Tessera.Cli.Output
open Tessera.Cli.Common

/// One-line description, shown in `tess --help`.
let summary =
    "VertiPaq statistics (rows, cardinality, sizes, RI violations) from fixture data (--data), a server model or a .vpax; --export, --annotate."

type Args =
    | [<MainCommand>] Target of model_and_tables: string list
    | Data of directory: string
    | Import of vpax: string
    | Tables
    | Columns
    | Relationships
    | Partitions
    | Hierarchies
    | All
    | Top of n: int
    | Export of vpax: string
    | Obfuscate
    | Dictionary of path: string
    | Annotate
    | Save
    | Save_To of path: string
    | Force
    | Stat
    | Name_Only

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Target _ ->
                "the model (model files with --data, or xmla:<server>[;<database>]; quote it), then table names to "
                + "show only those (default: all). With --import the model is optional."
            | Data _ ->
                "model files: count rows and distinct values in this fixture directory (see `tess data`) and estimate "
                + "sizes. Nothing leaves the machine."
            | Import _ ->
                "read the statistics from a VertiPaq Analyzer file (DAX Studio, Tabular Editor 3, Bravo, tess)."
            | Tables -> "show the tables (the default view)."
            | Columns -> "show the columns."
            | Relationships -> "show the relationships, with referential-integrity violations."
            | Partitions -> "show the partitions."
            | Hierarchies -> "show the user hierarchies."
            | All -> "show every view."
            | Top _ -> "show only the N largest objects of each view."
            | Export _ -> "write the statistics to this .vpax file (DAX Studio, Bravo and Tabular Editor 3 open it)."
            | Obfuscate ->
                "with --export: obfuscate names and expressions (as Bravo does); the dictionary that reverses it is "
                + "written next to the file as <name>.dict."
            | Dictionary _ ->
                "with --obfuscate: extend this existing dictionary, so names obfuscate the same way as before."
            | Annotate ->
                "write the statistics into the model as Vertipaq_* annotations (what Best Practice rules read). "
                + "A dry run unless --save or --save-to."
            | Save -> Mutation.Usage.save
            | Save_To _ -> Mutation.Usage.saveTo
            | Force -> Mutation.Usage.force + " With --export, replace the file (and its .dict)."
            | Stat -> Mutation.Usage.stat
            | Name_Only -> Mutation.Usage.nameOnly

// ---- Formatting --------------------------------------------------------------------------------

let private inv = CultureInfo.InvariantCulture

let private num64 (n: int64) : JsonNode = JsonValue.Create n

let private fraction (f: float) : JsonNode = JsonValue.Create(Math.Round(f, 4))

/// Known values only: an unknown statistic is left out of JSON (absent, never null or 0).
let private opt (name: string) (v: int64 option) =
    v |> Option.map (fun n -> name, num64 n) |> Option.toList

let private optFraction (name: string) (v: float option) =
    v |> Option.map (fun f -> name, fraction f) |> Option.toList

let private bytes (v: int64 option) =
    match v with
    | Some n -> n.ToString("N0", inv)
    | None -> "?"

let private count (v: int64 option) =
    match v with
    | Some n -> n.ToString("N0", inv)
    | None -> "?"

let private percent (v: float option) =
    match v with
    | Some f -> (f * 100.0).ToString("0.0", inv) + "%"
    | None -> "?"

let private columnRef (t: string) (c: string) =
    ObjectRef.format (ObjectRef.inTable ObjectKind.Column t c)

let private daxColumn (t: string) (c: string) =
    Dax.quoteTable t + "[" + c.Replace("]", "]]") + "]"

/// Rows as aligned columns: numbers (every column but the first) right-aligned.
let private table (out: TextWriter) (header: string list) (rows: string list list) =
    let all = header :: rows

    let widths =
        header
        |> List.mapi (fun i _ -> all |> List.map (fun r -> r.[i].Length) |> List.max)

    for r in all do
        let cells =
            r
            |> List.mapi (fun i cell ->
                if i = 0 then
                    cell.PadRight widths.[i]
                else
                    cell.PadLeft widths.[i])

        out.WriteLine((String.concat "  " cells).TrimEnd())

/// Largest first, unknown sizes last, then by name; `--top` keeps the first N.
let private bySize (top: int option) (size: 'a -> int64 option) (name: 'a -> string) (xs: 'a list) =
    let sorted =
        xs
        |> List.sortBy (fun x ->
            (match size x with
             | Some n -> -n
             | None -> Int64.MaxValue),
            (name x).ToUpperInvariant())

    match top with
    | Some n -> List.truncate n sorted
    | None -> sorted

type private Views = {
    Tables: bool
    Columns: bool
    Relationships: bool
    Partitions: bool
    Hierarchies: bool
}

let private views (args: ParseResults<Args>) =
    if args.Contains All then
        {
            Tables = true
            Columns = true
            Relationships = true
            Partitions = true
            Hierarchies = true
        }
    else
        let v = {
            Tables = args.Contains Tables
            Columns = args.Contains Columns
            Relationships = args.Contains Relationships
            Partitions = args.Contains Partitions
            Hierarchies = args.Contains Hierarchies
        }

        if v.Columns || v.Relationships || v.Partitions || v.Hierarchies then
            v
        else
            { v with Tables = true }

let private originJson (o: Origin) =
    match o with
    | Origin.Fixture d -> obj [ "kind", str "fixture"; "directory", str d ]
    | Origin.Server a -> obj [ "kind", str "server"; "address", str a ]
    | Origin.Vpax p -> obj [ "kind", str "vpax"; "path", str p ]

let private originText (o: Origin) =
    match o with
    | Origin.Fixture d -> $"fixture data {d}"
    | Origin.Server a -> a
    | Origin.Vpax p -> p

let private cardinalityOf (s: Stats) (t: string) (c: string) =
    s.Columns
    |> List.tryFind (fun x ->
        String.Equals(x.Table, t, StringComparison.OrdinalIgnoreCase)
        && String.Equals(x.Column, c, StringComparison.OrdinalIgnoreCase))
    |> Option.bind _.Cardinality

let private render (ctx: Ctx) (v: Views) (top: int option) (exported: JsonNode option) (s: Stats) =
    let model = Stats.modelSize s
    let tableSize t = Stats.tableSize s t

    let visibleColumns =
        s.Columns
        |> List.filter (fun c -> c.Kind <> "rowNumber" || (Column.totalSize c) <> Some 0L)

    let tables = s.Tables |> bySize top (fun t -> tableSize t.Table) _.Table

    let columns =
        visibleColumns
        |> bySize top Column.totalSize (fun c -> c.Table + "/" + c.Column)

    let relationships =
        s.Relationships
        |> bySize top Relationship.size (fun r -> r.FromTable + "/" + r.FromColumn)

    let partitions =
        s.Partitions |> bySize top _.DataSize (fun p -> p.Table + "/" + p.Partition)

    let hierarchies =
        s.Hierarchies |> bySize top _.Size (fun h -> h.Table + "/" + h.Hierarchy)

    if ctx.Json then
        writeJson
            ctx.Out
            (obj [
                "command", str "vertipaq"
                "model", str s.ModelName
                "origin", originJson s.Origin
                "estimated", bool s.Estimated
                yield! opt "size" model
                "tableCount", num s.Tables.Length
                if v.Tables then
                    "tables",
                    arr [
                        for t in tables ->
                            let size = tableSize t.Table

                            obj [
                                "table", str t.Table
                                "ref", str (ObjectRef.format (ObjectRef.table t.Table))
                                "kind", str t.Kind
                                yield! opt "rows" t.Rows
                                yield! opt "size" size
                                yield! opt "columnsSize" (Stats.columnsSize s t.Table)
                                yield! opt "userHierarchiesSize" (Stats.hierarchiesSize s t.Table)
                                yield! opt "relationshipsSize" (Stats.relationshipsSize s t.Table)
                                yield! optFraction "fractionOfModel" (Stats.fraction size model)
                            ]
                    ]
                if v.Columns then
                    "columns",
                    arr [
                        for c in columns ->
                            let size = Column.totalSize c

                            obj [
                                "table", str c.Table
                                "column", str c.Column
                                "ref", str (columnRef c.Table c.Column)
                                "kind", str c.Kind
                                "dataType", str c.DataType
                                yield! opt "cardinality" c.Cardinality
                                yield! opt "size" size
                                yield! opt "dataSize" c.DataSize
                                yield! opt "dictionarySize" c.DictionarySize
                                yield! opt "hierarchySize" c.HierarchySize
                                match c.Encoding with
                                | Some e -> "encoding", str e
                                | None -> ()
                                match c.EncodingHint with
                                | Some e -> "encodingHint", str e
                                | None -> ()
                                yield! optFraction "fractionOfTable" (Stats.fraction size (tableSize c.Table))
                                yield! optFraction "fractionOfModel" (Stats.fraction size model)
                            ]
                    ]
                if v.Relationships then
                    "relationships",
                    arr [
                        for r in relationships ->
                            obj [
                                "name", str r.Name
                                "ref", str (ObjectRef.format (ObjectRef.modelLevel ObjectKind.Relationship r.Name))
                                "from", str (columnRef r.FromTable r.FromColumn)
                                "to", str (columnRef r.ToTable r.ToColumn)
                                "active", bool r.IsActive
                                "fromCardinalityType", str r.FromCardinalityType
                                "toCardinalityType", str r.ToCardinalityType
                                yield! opt "fromCardinality" (cardinalityOf s r.FromTable r.FromColumn)
                                yield! opt "toCardinality" (cardinalityOf s r.ToTable r.ToColumn)
                                yield! opt "missingKeys" r.MissingKeys
                                yield! opt "invalidRows" r.InvalidRows
                                yield! opt "size" (Relationship.size r)
                            ]
                    ]
                if v.Partitions then
                    "partitions",
                    arr [
                        for p in partitions ->
                            obj [
                                "table", str p.Table
                                "partition", str p.Partition
                                "ref",
                                str (ObjectRef.format (ObjectRef.inTable ObjectKind.Partition p.Table p.Partition))
                                match p.Mode with
                                | Some m -> "mode", str m
                                | None -> ()
                                yield! opt "rows" p.Rows
                                yield! opt "segments" p.Segments
                                yield! opt "dataSize" p.DataSize
                            ]
                    ]
                if v.Hierarchies then
                    "hierarchies",
                    arr [
                        for h in hierarchies ->
                            obj [
                                "table", str h.Table
                                "hierarchy", str h.Hierarchy
                                "ref",
                                str (ObjectRef.format (ObjectRef.inTable ObjectKind.Hierarchy h.Table h.Hierarchy))
                                yield! opt "size" h.Size
                            ]
                    ]
                match exported with
                | Some e -> "exported", e
                | None -> ()
                "warnings", arr (s.Warnings |> List.map str)
            ])
    else
        let out = ctx.Out
        let estimated = if s.Estimated then " (sizes estimated)" else ""
        out.WriteLine($"{s.ModelName}  {originText s.Origin}  size {bytes model}{estimated}")

        let section (title: string) =
            out.WriteLine()
            out.WriteLine(title)

        if v.Tables then
            section "Tables"

            table out [ "Table"; "Rows"; "Size"; "%Model"; "Columns"; "Hierarchies"; "Relationships" ] [
                for t in tables ->
                    let size = tableSize t.Table

                    [
                        t.Table
                        count t.Rows
                        bytes size
                        percent (Stats.fraction size model)
                        bytes (Stats.columnsSize s t.Table)
                        bytes (Stats.hierarchiesSize s t.Table)
                        bytes (Stats.relationshipsSize s t.Table)
                    ]
            ]

        if v.Columns then
            section "Columns"

            table out [
                "Column"
                "Cardinality"
                "Size"
                "%Table"
                "%Model"
                "Data"
                "Dictionary"
                "Hierarchy"
                "Encoding"
            ] [
                for c in columns ->
                    let size = Column.totalSize c

                    [
                        daxColumn c.Table c.Column
                        count c.Cardinality
                        bytes size
                        percent (Stats.fraction size (tableSize c.Table))
                        percent (Stats.fraction size model)
                        bytes c.DataSize
                        bytes c.DictionarySize
                        bytes c.HierarchySize
                        (match c.Encoding, c.EncodingHint with
                         | Some e, _ -> e
                         | None, Some h when h <> "Default" -> $"hint {h}"
                         | _ -> "")
                    ]
            ]

        if v.Relationships then
            section "Relationships"

            table out [
                "Relationship"
                "Cardinality"
                "From card."
                "To card."
                "Missing keys"
                "Invalid rows"
                "Size"
            ] [
                for r in relationships ->
                    let active = if r.IsActive then "" else " (inactive)"

                    [
                        $"{daxColumn r.FromTable r.FromColumn} -> {daxColumn r.ToTable r.ToColumn}{active}"
                        $"{r.FromCardinalityType}:{r.ToCardinalityType}"
                        count (cardinalityOf s r.FromTable r.FromColumn)
                        count (cardinalityOf s r.ToTable r.ToColumn)
                        count r.MissingKeys
                        count r.InvalidRows
                        bytes (Relationship.size r)
                    ]
            ]

        if v.Partitions then
            section "Partitions"

            table out [ "Partition"; "Mode"; "Rows"; "Segments"; "Data" ] [
                for p in partitions ->
                    [
                        $"{Dax.quoteTable p.Table} {p.Partition}"
                        defaultArg p.Mode ""
                        count p.Rows
                        count p.Segments
                        bytes p.DataSize
                    ]
            ]

        if v.Hierarchies then
            section "Hierarchies"

            table out [ "Hierarchy"; "Size" ] [
                for h in hierarchies -> [ $"{Dax.quoteTable h.Table}[{h.Hierarchy}]"; bytes h.Size ]
            ]

        if s.Estimated then
            ctx.Err.WriteLine(
                "Rows, cardinality and RI violations are counted in the fixture data; sizes are estimates, not what VertiPaq would store (see README)."
            )

        for w in s.Warnings do
            ctx.Err.WriteLine($"tess: warning: {w}")

    Done

// ---- Sources -----------------------------------------------------------------------------------

/// Where the statistics come from.
type private StatsSource =
    | FromFixture of model: string * directory: string
    | FromServer of model: string
    | FromVpax of vpax: string * model: string option

/// The table names asked for, checked against the statistics.
let private checkTables (s: Stats) (names: string list) : Result<string list, TessError> =
    let known = s.Tables |> List.map _.Table

    names
    |> List.fold
        (fun acc name ->
            acc
            |> Result.bind (fun found ->
                match
                    known
                    |> List.tryFind (fun t -> String.Equals(t, name, StringComparison.OrdinalIgnoreCase))
                with
                | Some t -> Ok(found @ [ t ])
                | None ->
                    match Suggest.closest known name with
                    | [] -> Error(ObjectNotFound $"table:{name}")
                    | close ->
                        Error(ObjectNotFoundSuggest($"table:{name}", close |> List.map (fun n -> $"table:{n}")))))
        (Ok [])

let private isModelPath (p: string) =
    XmlaAddress.isRemote p || File.Exists p || Directory.Exists p

/// Read the statistics, and the model they are about (when there is one).
let private load (ctx: Ctx) (source: StatsSource) : Result<Stats * Database option, TessError> =
    match source with
    | FromFixture(model, directory) ->
        ModelIO.loadPath model
        |> Result.bind (fun (_, db) -> VertipaqFixture.fromDirectory db directory |> Result.map (fun s -> s, Some db))
    | FromServer model ->
        XmlaAddress.parse model
        |> Result.bind (VertipaqServer.load (Source.access ctx))
        |> Result.map (fun (_, db, s) -> s, Some db)
    | FromVpax(vpax, model) ->
        VertipaqVpax.read vpax
        |> Result.bind (fun imported ->
            match model with
            | None -> Ok(imported.Stats, imported.Database)
            | Some m -> Source.load ctx m |> Result.map (fun (_, db) -> imported.Stats, Some db))

// ---- --export ----------------------------------------------------------------------------------

let private export
    (args: ParseResults<Args>)
    (path: string)
    (s: Stats)
    (db: Database option)
    : Result<JsonNode * string list, TessError> =
    let full = Path.GetFullPath path
    let force = args.Contains Force
    let obfuscate = args.Contains Obfuscate
    let dict = Path.ChangeExtension(full, ".dict")

    if File.Exists full && not force then
        Error(DestinationNotEmpty full)
    elif obfuscate && File.Exists dict && not force then
        Error(DestinationNotEmpty dict)
    else
        // The model for the file: the imported file's own (lossless), or one built from the model.
        let built =
            match args.TryGetResult Import, db with
            | Some vpax, _ -> VertipaqVpax.read vpax |> Result.map (fun i -> i.DaxModel, i.Database, [])
            | None, Some db ->
                let m, unknown = VertipaqVpax.toDaxModel (version ()) s db
                Ok(m, Some db, unknown)
            | None, None -> Error(RemoteSourceUnsupported(path, "there is no model to export"))

        built
        |> Result.bind (fun (m, database, unknown) ->
            let warnings = [
                if not unknown.IsEmpty then
                    let shown = unknown |> List.truncate 5 |> String.concat ", "

                    let more =
                        if unknown.Length > 5 then
                            $" and {unknown.Length - 5} more"
                        else
                            ""

                    $"the file holds 0 (the format's \"not read\") for statistics that are unknown: {shown}{more}"
            ]

            if obfuscate then
                VertipaqVpax.obfuscate (args.TryGetResult Dictionary) m
                |> Result.bind (fun dictionary ->
                    // Only DaxModel.json: Model.bim and the flat view would carry the real names.
                    VertipaqVpax.write full m None
                    |> Result.bind (fun written ->
                        try
                            dictionary.WriteTo(dict, overwrite = true, indented = true)

                            Ok(
                                obj [ "path", str written; "obfuscated", bool true; "dictionary", str dict ],
                                warnings
                            )
                        with e ->
                            Error(SaveFailed(dict, e.Message))))
            else
                VertipaqVpax.write full m database
                |> Result.map (fun written -> obj [ "path", str written; "obfuscated", bool false ], warnings))

// ---- --annotate --------------------------------------------------------------------------------

let private annotate (ctx: Ctx) (args: ParseResults<Args>) (source: StatsSource) (tables: string list) =
    match
        Mutation.options
            (args.Contains Save)
            (args.TryGetResult Save_To)
            (args.Contains Force)
            (args.Contains Stat)
            (args.Contains Name_Only)
    with
    | Error message -> UsageError message
    | Ok opts ->
        let edit (statsOf: Database -> Result<Stats, TessError>) (db: Database) =
            statsOf db
            |> Result.bind (fun s ->
                checkTables s tables
                |> Result.map (fun tables ->
                    let applied = Annotations.apply tables s db

                    let unmatched =
                        match applied.Unmatched with
                        | [] -> []
                        | xs ->
                            let shown = xs |> List.truncate 5 |> String.concat ", "
                            let more = if xs.Length > 5 then $" and {xs.Length - 5} more" else ""

                            [
                                $"statistics for objects the model doesn't have were not written: {shown}{more}"
                            ]

                    {
                        Mutation.Effect.none with
                            Warnings = s.Warnings @ unmatched
                            Notes = [
                                if s.Estimated then
                                    "Sizes are estimates from fixture data (Tessera_Vertipaq_Estimated is set on the model)."
                            ]
                    }))

        match source with
        | FromFixture(model, directory) ->
            Mutation.run ctx "vertipaq" opts model (edit (fun db -> VertipaqFixture.fromDirectory db directory))
        | FromServer model ->
            Mutation.run
                ctx
                "vertipaq"
                opts
                model
                (edit (fun _ ->
                    XmlaAddress.parse model
                    |> Result.bind (VertipaqServer.load (Source.access ctx))
                    |> Result.map (fun (_, _, s) -> s)))
        | FromVpax(_, None) ->
            UsageError "--annotate needs the model to write to: tess vertipaq <model> --import <file.vpax> --annotate"
        | FromVpax(vpax, Some model) ->
            match VertipaqVpax.read vpax with
            | Error e -> Failed e
            | Ok imported -> Mutation.run ctx "vertipaq" opts model (edit (fun _ -> Ok imported.Stats))

// ---- run ---------------------------------------------------------------------------------------

let run (ctx: Ctx) (args: ParseResults<Args>) =
    let positional = args.GetResults Target |> List.concat
    let data = args.TryGetResult Data
    let import = args.TryGetResult Import
    let top = args.TryGetResult Top
    let annotating = args.Contains Annotate
    let exporting = args.TryGetResult Export

    let mutationFlags =
        [
            args.Contains Save, "--save"
            (args.TryGetResult Save_To).IsSome, "--save-to"
            args.Contains Stat, "--stat"
            args.Contains Name_Only, "--name-only"
        ]
        |> List.filter fst
        |> List.map snd

    let source =
        match import, data, positional with
        | Some _, Some _, _ -> Error "--import and --data can't be combined: the statistics come from one place"
        | Some vpax, None, first :: rest when isModelPath first -> Ok(FromVpax(vpax, Some first), rest)
        | Some vpax, None, tables -> Ok(FromVpax(vpax, None), tables)
        | None, _, [] ->
            Error
                "expected a model: tess vertipaq <model> --data <fixture>, tess vertipaq xmla:<server>;<database>, or --import <file.vpax>"
        | None, Some _, model :: _ when XmlaAddress.isRemote model ->
            Error "--data is for model files: a server model's statistics are read on the server"
        | None, Some directory, model :: tables -> Ok(FromFixture(model, directory), tables)
        | None, None, model :: tables when XmlaAddress.isRemote model -> Ok(FromServer model, tables)
        | None, None, model :: _ ->
            Error
                $"'{model}' is model files: give its fixture data with --data <directory> (see `tess data init`), read a server model (xmla:<server>;<database>), or --import a .vpax"

    match source with
    | _ when top |> Option.exists (fun n -> n < 1) -> UsageError "--top must be at least 1"
    | _ when args.Contains Obfuscate && exporting.IsNone -> UsageError "--obfuscate is for --export"
    | _ when (args.TryGetResult Dictionary).IsSome && not (args.Contains Obfuscate) ->
        UsageError "--dictionary is for --export --obfuscate"
    | _ when annotating && exporting.IsSome ->
        UsageError "--annotate and --export can't be combined: run them one after the other"
    | _ when not annotating && not mutationFlags.IsEmpty ->
        UsageError $"""{String.concat ", " mutationFlags} only apply with --annotate"""
    | Error message -> UsageError message
    | Ok(source, tables) when annotating -> annotate ctx args source tables
    | Ok(source, tables) ->
        match load ctx source with
        | Error e -> Failed e
        | Ok(stats, db) ->
            match checkTables stats tables with
            | Error e -> Failed e
            | Ok tables ->
                let exported =
                    match exporting with
                    | None -> Ok None
                    | Some path ->
                        // The file has everything; the filter is only for what is shown.
                        export args path stats db |> Result.map Some

                match exported with
                | Error e -> Failed e
                | Ok exported ->
                    let stats = Stats.restrict tables stats

                    let stats =
                        match exported with
                        | Some(_, warnings) -> {
                            stats with
                                Warnings = stats.Warnings @ warnings
                          }
                        | None -> stats

                    let result = render ctx (views args) top (exported |> Option.map fst) stats

                    match exported with
                    | Some(node, _) when not ctx.Json ->
                        let path = (node :?> JsonObject).["path"].GetValue<string>()
                        ctx.Err.WriteLine($"Wrote {path}")

                        if args.Contains Obfuscate then
                            let dict = Path.ChangeExtension(path, ".dict")

                            ctx.Err.WriteLine(
                                $"Wrote {dict}: it maps the obfuscated names back to the real ones, so keep it private."
                            )
                    | _ -> ()

                    result
