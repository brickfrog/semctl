/// `sm mcp`: a Model Context Protocol server (spec 2025-06-18) over stdio, for one model.
///
/// Permission tiers decide which tools exist: a tool outside the grants is never registered, so it
/// is neither listed nor callable (calling it is an unknown tool). Reads come first and narrow down
/// (overview -> list/search -> detail); writes are one atomic batch per call, applied in memory
/// through the same pipeline as the editing commands, and saved only by `save_model`, which exists
/// only with `--save`. There is no raw TMSL tool.
module Semctl.Cli.Mcp.Server

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.Prelude
open Semctl.Inventory
open Semctl.Cli
open Semctl.Cli.Output
open Semctl.Cli.Common

/// The protocol version sm speaks, and the older ones it also accepts from a client.
let protocolVersion = "2025-06-18"
let private acceptedVersions = [ "2025-06-18"; "2025-03-26"; "2024-11-05" ]

// ---------------------------------------------------------------------------------------------
// Grants and options

[<RequireQualifiedAccess>]
type Grant =
    /// Metadata read: the overview, listing, search, detail, dependencies, validate, lint, format.
    | Read
    /// Metadata write: apply_changes (and save_model with --save). Implies Read.
    | MetadataWrite
    /// DAX query results: model rows reach the client.
    | DataRead
    /// The Best Practice Analyzer: its rules and their findings.
    | Bpa

module Grant =
    let all = [ Grant.Read; Grant.MetadataWrite; Grant.DataRead; Grant.Bpa ]

    let token =
        function
        | Grant.Read -> "read"
        | Grant.MetadataWrite -> "metadata-write"
        | Grant.DataRead -> "data-read"
        | Grant.Bpa -> "bpa"

    let tryParse (s: string) =
        all
        |> List.tryFind (fun g -> String.Equals(token g, s, StringComparison.OrdinalIgnoreCase))

/// Where `run_query` gets rows from.
[<RequireQualifiedAccess>]
type DataSource =
    | NoData
    /// A fixture directory, evaluated by the local engine.
    | Fixture of directory: string
    /// A server model the user named (`--server xmla:...`).
    | Server of address: XmlaAddress

type Options = {
    Grants: Set<Grant>
    /// `save_model` exists (it writes the model back where it was loaded from).
    Save: bool
    Data: DataSource
    /// JSONL, one line per tool call: no values, no DAX, no query results.
    Audit: TextWriter option
}

// ---------------------------------------------------------------------------------------------
// The session

/// The served model (staged edits, the save gate and staged report files: Staging.StagedModel,
/// shared with `sm interactive`) and the server's own state.
type Session = {
    Ctx: Ctx
    Options: Options
    Model: Staging.StagedModel
    /// The local engine over the fixture, prepared for the current model (dropped on each write).
    mutable Prepared: Result<Semctl.Engine.Prepared, SemctlError> option
} with

    member s.Db = s.Model.Db
    member s.Source = s.Model.Source
    /// Objects changed by batches since the model was loaded or saved.
    member s.Unsaved = s.Model.Unsaved

module Session =
    let create (ctx: Ctx) (source: ModelSource) (db: Database) (options: Options) = {
        Ctx = ctx
        Options = options
        Model = Staging.StagedModel.create source db
        Prepared = None
    }

    /// The gated errors present now that weren't when the model was loaded (or last saved).
    let introducedSinceLoad (s: Session) =
        Staging.StagedModel.introducedSinceLoad s.Model

// ---------------------------------------------------------------------------------------------
// Tools

/// Why a tool call failed: a sm error, or arguments that don't fit the tool.
type Failure =
    | Semctl of SemctlError
    | BadArguments of string
    /// Operation `index` of an apply_changes batch failed (so the batch changed nothing).
    | AtOperation of index: int * Failure
    /// A bug in sm (an exception), reported to the client as `internal`.
    | Internal of message: string

type Tool = {
    Name: string
    Title: string
    Description: string
    Grant: Grant
    /// JSON Schema for the arguments.
    Schema: JsonObject
    ReadOnly: bool
    Destructive: bool
    Run: Session -> JsonObject -> Result<JsonNode, Failure>
}

module private Args =
    let node (args: JsonObject) (name: string) : JsonNode =
        let mutable v: JsonNode = null
        if args.TryGetPropertyValue(name, &v) then v else null

    let kind (n: JsonNode) =
        match n with
        | null -> JsonValueKind.Null
        | n -> n.GetValueKind()

    let optString (args: JsonObject) name : Result<string option, Failure> =
        match node args name with
        | null -> Ok None
        | v when kind v = JsonValueKind.String -> Ok(Some(v.GetValue<string>()))
        | _ -> Error(BadArguments $"'{name}' must be a string")

    let string (args: JsonObject) name : Result<string, Failure> =
        optString args name
        |> Result.bind (function
            | Some s -> Ok s
            | None -> Error(BadArguments $"missing '{name}'"))

    let optBool (args: JsonObject) name : Result<bool option, Failure> =
        match node args name with
        | null -> Ok None
        | v when kind v = JsonValueKind.True -> Ok(Some true)
        | v when kind v = JsonValueKind.False -> Ok(Some false)
        | _ -> Error(BadArguments $"'{name}' must be true or false")

    let bool args name dflt =
        optBool args name |> Result.map (Option.defaultValue dflt)

    let optInt (args: JsonObject) name : Result<int option, Failure> =
        match node args name with
        | null -> Ok None
        | v when kind v = JsonValueKind.Number ->
            match v.AsValue().TryGetValue<int>() with
            | true, n -> Ok(Some n)
            | _ -> Error(BadArguments $"'{name}' must be a whole number")
        | _ -> Error(BadArguments $"'{name}' must be a whole number")

    let optStrings (args: JsonObject) name : Result<string list, Failure> =
        match node args name with
        | null -> Ok []
        | :? JsonArray as a when a |> Seq.forall (fun x -> kind x = JsonValueKind.String) ->
            Ok [ for x in a -> x.GetValue<string>() ]
        | v when kind v = JsonValueKind.String -> Ok [ v.GetValue<string>() ]
        | _ -> Error(BadArguments $"'{name}' must be an array of strings")

let private semctl (r: Result<'a, SemctlError>) : Result<'a, Failure> = r |> Result.mapError Semctl

let private resolve (s: Session) (selector: string) =
    Selector.resolve (objects s.Db) selector |> semctl

let private ofKinds (tokens: string list) : Result<ObjectKind list, Failure> =
    match parseKindsWith tokens with
    | Ok kinds -> Ok kinds
    | Error(message, _) -> Error(BadArguments message)

let private enumName (v: obj) =
    let s = string v
    string (Char.ToLowerInvariant s.[0]) + s.Substring 1

/// Results past this many items are cut, and the result says so.
let private listLimit = 500

let private capped (name: string) (items: 'a list) (toJson: 'a list -> JsonNode) =
    let shown = List.truncate listLimit items

    [
        name, toJson shown
        "total", num items.Length
        if items.Length > listLimit then
            "truncated", bool true

            "note",
            str
                $"only the first {listLimit}: narrow the request (a container, kinds, a name pattern, --where conditions)"
    ]

// ---- read --------------------------------------------------------------------------------------

let private overview (s: Session) (_: JsonObject) =
    let db = s.Db
    let m = db.Model
    let info = Inventory.info s.Source db

    let tables = [
        for t in m.Tables ->
            obj [
                "name", str t.Name
                "hidden", (if t.IsHidden then bool true else null)
                "calculationGroup", (if isNull t.CalculationGroup then null else bool true)
                "columns", num t.Columns.Count
                "measures", num t.Measures.Count
                "hierarchies", num t.Hierarchies.Count
                "partitions", num t.Partitions.Count
                "description",
                (if String.IsNullOrWhiteSpace t.Description then
                     null
                 else
                     str t.Description)
            ]
    ]

    let relationships = [
        for r in m.Relationships ->
            obj [
                "name", str r.Name
                "endpoints", str (Inventory.relationshipEndpoints r)
                "active", bool r.IsActive
                "crossFilteringBehavior", str (enumName r.CrossFilteringBehavior)
                match r with
                | :? SingleColumnRelationship as sr ->
                    "fromCardinality", str (enumName sr.FromCardinality)
                    "toCardinality", str (enumName sr.ToCardinality)
                | _ -> ()
            ]
    ]

    Ok(
        obj [
            "model", InfoCommand.infoJson info
            "tables", arr tables
            "relationships", arr relationships
            "unsavedChanges", num s.Unsaved
            "grants", arr (s.Options.Grants |> Set.toList |> List.map (Grant.token >> str))
        ]
    )

let private listObjects (s: Session) (args: JsonObject) =
    result {
        let glob (pattern: string option) =
            pattern |> Option.map Where.glob |> Option.defaultValue (fun _ -> true)

        let! container = Args.optString args "container"
        let! kindTokens = Args.optStrings args "kinds"
        let! kinds = ofKinds kindTokens
        let! name = Args.optString args "name"
        let! rawConditions = Args.optStrings args "where"

        let! conditions =
            rawConditions
            |> resultTraverse (Where.Condition.parse >> Result.mapError BadArguments)

        let! c = resolve s (defaultArg container "model")

        let! found =
            (if conditions.IsEmpty then
                 Ok(Where.within (objects s.Db) c)
             else
                 Where.filter s.Db c conditions)
            |> semctl

        let byName = glob name

        let found =
            found
            |> List.filter (fun e -> (kinds.IsEmpty || List.contains e.Ref.Kind kinds) && byName (ObjectEntry.name e))

        return obj (capped "objects" found ListCommand.listJson)
    }

let private search (s: Session) (args: JsonObject) =
    result {
        let! text = Args.string args "text"
        let! regex = Args.bool args "regex" false
        let! caseSensitive = Args.bool args "caseSensitive" false
        let! properties = Args.optStrings args "in"
        let! kindTokens = Args.optStrings args "kinds"
        let! kinds = ofKinds kindTokens

        let opts: Find.Options = {
            Find.Options.literal text with
                Regex = regex
                CaseSensitive = caseSensitive
                Properties = properties
                Objects = { Filter.none with Kinds = kinds }
        }

        let! hits = Find.search s.Db opts |> semctl
        return obj (capped "hits" hits FindCommand.findJson)
    }

let private getObjects (s: Session) (args: JsonObject) =
    result {
        let! objects' = Args.optStrings args "objects"
        let! paths = Args.optStrings args "properties"
        let! format = Args.optString args "format"
        let! children = Args.bool args "children" false

        do!
            match objects', format with
            | [], _ -> Error(BadArguments "missing 'objects': the objects to show, in any form `sm get` takes")
            | _, Some f when f <> "json" && f <> "tmdl" -> Error(BadArguments "'format' is json or tmdl")
            | _ -> Ok()

        let one (selector: string) =
            match resolve s selector with
            | Error(Semctl e) -> obj [ "object", str selector; "error", (semctlErrorJson e).["error"].DeepClone() ]
            | Error f -> obj [ "object", str selector; "error", str (string f) ]
            | Ok e ->
                match paths, format with
                | _ :: _, _ ->
                    let props = Serialize.properties s.Db e
                    GetCommand.propertiesJson e (paths |> List.collect (fun p -> Serialize.select p props))
                | [], Some "tmdl" -> obj (refFields e @ [ "tmdl", str (Serialize.objectTmdl children e.Object) ])
                | [], _ -> GetCommand.getJson e (Serialize.objectJson s.Db children e.Object)

        return obj [ "objects", arr (objects' |> List.map one) ]
    }

let private listProperties (s: Session) (args: JsonObject) =
    result {
        let! selector = Args.string args "object"
        let! all = Args.bool args "all" false
        let! e = resolve s selector

        let descriptors =
            SetProperty.catalog s.Db e |> List.filter (fun d -> all || d.Writable)

        return obj (refFields e @ [ "properties", GetCommand.catalogJson descriptors ])
    }

let private dependencies (s: Session) (args: JsonObject) =
    result {
        let! selector = Args.string args "object"
        let! direction = Args.optString args "direction"
        let! depth = Args.optInt args "depth"

        let! directions =
            match defaultArg direction "both" with
            | "dependsOn" -> Ok [ Deps.DependsOn ]
            | "referencedBy" -> Ok [ Deps.ReferencedBy ]
            | "both" -> Ok [ Deps.DependsOn; Deps.ReferencedBy ]
            | other -> Error(BadArguments $"'direction' is dependsOn, referencedBy or both, not '{other}'")

        do!
            match depth with
            | Some d when d < 0 -> Error(BadArguments "'depth' is 0 (unlimited) or more")
            | _ -> Ok()

        let! e = resolve s selector
        let g = Deps.build s.Db
        let depth = defaultArg depth 1
        return DepsCommand.depsJson e.Ref [ for d in directions -> d, Deps.tree d depth g e.Ref ]
    }

let private validate (s: Session) (_: JsonObject) =
    let clock = Stopwatch.StartNew()
    let g = Deps.build s.Db
    let issues = ValidateSemantic.validateGraph g s.Db
    Ok(ValidateCommand.validateJsonGrouped clock.Elapsed (Validate.group g issues))

let private lint (s: Session) (args: JsonObject) =
    result {
        let! selectors = Args.optStrings args "objects"
        let clock = Stopwatch.StartNew()
        let! entries = resultTraverse (resolve s) selectors
        let only = entries |> List.map _.Ref |> Set.ofList

        let findings =
            Lint.lint s.Db |> List.filter (fun f -> only.IsEmpty || only.Contains f.Ref)

        return LintCommand.lintJson clock.Elapsed findings
    }

let private formatDax (s: Session) (args: JsonObject) =
    result {
        let! dax = Args.optString args "dax"
        let! selector = Args.optString args "object"
        let! long = Args.bool args "long" false

        let opts = {
            Format.FormatOptions.defaults with
                Long = long
        }

        let format (text: string) =
            Format.format opts text
            |> Result.mapError (fun e -> BadArguments(Format.FormatError.message e))

        match dax, selector with
        | Some text, None ->
            let! f = format text
            return obj [ "formatted", str f; "changed", bool (f <> text) ]
        | None, Some selector ->
            let! e = resolve s selector

            match Serialize.properties s.Db e |> List.tryFind (fun (p, _) -> p = "expression") with
            | None -> return! Error(BadArguments $"{ObjectRef.format e.Ref} has no DAX expression")
            | Some(_, text) ->
                let! f = format text

                return
                    obj (
                        refFields e
                        @ [
                            "property", str "expression"
                            "formatted", str f
                            "changed", bool (f <> text)
                        ]
                    )
        | _ -> return! Error(BadArguments "give either 'dax' (text to format) or 'object' (whose expression to format)")
    }

let private fullModel (s: Session) (_: JsonObject) =
    Ok(obj [ "tmdl", str (Serialize.objectTmdl true s.Db.Model) ])

// ---- bpa ---------------------------------------------------------------------------------------

/// Built-in rules, the model's own, and the user's config files; never anything from a URL.
let private collectRules (s: Session) =
    let opts: Bpa.RuleOptions = {
        Bpa.RuleOptions.defaults with
            AllowExternal = false
            Fetch = fun _ -> Error "sm mcp never fetches rules"
            Environment = {
                GetEnv = s.Ctx.GetEnv
                IsWindows = OperatingSystem.IsWindows()
            }
    }

    Bpa.collectRules s.Source s.Db opts |> semctl

let private listRules (s: Session) (_: JsonObject) =
    collectRules s
    |> Result.map (fun collected ->
        obj [
            "rules",
            arr [
                for r in collected.Effective ->
                    obj [
                        "id", str r.Id
                        "name", str r.Name
                        "category",
                        (if String.IsNullOrWhiteSpace r.Category then
                             null
                         else
                             str r.Category)
                        "severity", num r.Severity
                        "description", optStr r.Description
                        "scopes", arr (r.Scopes |> List.map (fun sc -> str (string sc)))
                        "expression", str r.Expression
                    ]
            ]
            "warnings", arr (collected.Warnings |> List.map str)
            "notes", arr (collected.Notes |> List.map str)
        ])

let private runBpa (s: Session) (args: JsonObject) =
    result {
        let! ids = Args.optStrings args "rules"
        let clock = Stopwatch.StartNew()
        let! collected = collectRules s

        let selected (r: BpaRules.Rule) =
            ids.IsEmpty
            || ids
               |> List.exists (fun id -> String.Equals(id, r.Id, StringComparison.OrdinalIgnoreCase))

        let results = Bpa.analyze s.Db (collected.Effective |> List.filter selected)
        let findings = results |> List.collect (fun r -> r.Findings)

        let problems = [
            for r in results do
                match r.Status with
                | Bpa.Unsupported reason -> r.Rule, "unsupported", reason
                | Bpa.Failed reason -> r.Rule, "failed", reason
                | _ -> ()
        ]

        return
            Findings.envelope "bpa" clock.Elapsed (findings |> List.map BpaCommand.toFinding) [
                "rulesEvaluated", num (results |> List.filter (fun r -> r.Status = Bpa.Evaluated) |> List.length)
                "ruleErrors",
                arr [
                    for rule, stage, reason in problems ->
                        obj [ "ruleId", str rule.Id; "stage", str stage; "message", str reason ]
                ]
                "warnings", arr (collected.Warnings |> List.map str)
            ]
    }

// ---- data --------------------------------------------------------------------------------------

/// Rows per result: 100 unless asked, never more than 500.
let defaultRows = 100
let maxRows = 500

let private prepared (s: Session) (directory: string) =
    match s.Prepared with
    | Some p -> p
    | None ->
        let p =
            Fixture.loadAndCheck s.Db directory
            |> Result.bind (fun (loaded, issues) ->
                let errors =
                    issues
                    |> List.filter (fun i -> i.Severity = Validate.Severity.Error)
                    |> List.length

                if errors > 0 then
                    Error(FixtureHasErrors(loaded.Directory, errors))
                else
                    Engine.prepare s.Db loaded.Store)

        s.Prepared <- Some p
        p

let private runQuery (s: Session) (args: JsonObject) =
    result {
        let! dax = Args.string args "dax"
        let! requested = Args.optInt args "maxRows"

        match requested with
        | Some n when n < 1 -> return! Error(BadArguments $"'maxRows' is 1 to {maxRows}")
        | _ when String.IsNullOrWhiteSpace dax -> return! Error(BadArguments "the DAX query is empty")
        | _ ->
            let limit = min maxRows (defaultArg requested defaultRows)

            let cap (r: QueryResult) =
                if r.Rows.Length > limit then
                    {
                        r with
                            Rows = List.truncate limit r.Rows
                            Truncated = true
                    }
                else
                    r

            let source, rawResults =
                match s.Options.Data with
                | DataSource.Fixture directory ->
                    "local engine", prepared s directory |> Result.bind (fun p -> Engine.evaluateQuery p dax)
                | DataSource.Server address ->
                    // Capped while reading: no more rows than asked come over the wire.
                    XmlaAddress.display address,
                    Semctl.Xmla.Remote.query (Semctl.Cli.Source.access s.Ctx) address dax (Some limit)
                    |> Result.map snd
                | DataSource.NoData ->
                    "none", Error(LoadFailed("run_query", "no data source: start sm mcp with --data or --server"))

            let! results = rawResults |> semctl
            let results = results |> List.map cap
            let truncated = results |> List.exists _.Truncated

            return
                obj [
                    "source", str source
                    "maxRows", num limit
                    "results", QueryResult.listToJson results
                    "truncated", bool truncated
                    if truncated then
                        "note",
                        str (
                            if limit < maxRows then
                                $"only the first {limit} rows of each result: pass maxRows (at most {maxRows}) for more, or aggregate in the query"
                            else
                                $"only the first {maxRows} rows of each result (the most this server returns): aggregate or filter in the query"
                        )
                    match requested with
                    | Some n when n > maxRows -> "maxRowsCapped", str $"maxRows is at most {maxRows}"
                    | _ -> ()
                ]
    }

// ---- write -------------------------------------------------------------------------------------

/// One operation of a batch, as `apply_changes` takes it.
/// Report files a batch rewrites: planned against the session's staged files and the batch's own
/// earlier edits, so renames in one batch compose; staged with the batch, or dropped with it.
type private BatchFiles(model: Staging.StagedModel) =
    let mutable files: Map<string, Staging.StagedFile> = Map.empty

    member _.Read(path: string) : byte array =
        match Map.tryFind (System.IO.Path.GetFullPath path) files with
        | Some f ->
            let body = Text.Encoding.UTF8.GetBytes f.Current

            if f.Bom then
                Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] body
            else
                body
        | None -> Staging.StagedModel.read model path

    member _.Add(edits: Report.FileEdit list) =
        files <- Staging.StagedModel.mergeFiles files edits

    /// The batch's edits, from the text as staged before the batch to the text after it.
    member _.Edits: Report.FileEdit list = [
        for KeyValue(_, f) in files ->
            {
                Path = f.Path
                Display = f.Display
                Before = f.Original
                After = f.Current
                Bom = f.Bom
                References = f.References
            }
    ]

let private operation
    (s: Session)
    (batch: BatchFiles)
    (db: Database)
    (op: JsonObject)
    : Result<Mutation.Effect, Failure> =
    result {
        let entry selector =
            Selector.resolve (objects db) selector |> semctl

        let metadata () =
            result {
                let! description = Args.optString op "description"
                let! displayFolder = Args.optString op "displayFolder"
                let! formatString = Args.optString op "formatString"
                let! hidden = Args.bool op "hidden" false

                return {
                    AddObject.Metadata.none with
                        Description = description
                        DisplayFolder = displayFolder
                        FormatString = formatString
                        Hidden = hidden
                }
            }

        let add (r: ObjectRef) (details: AddObject.Details) =
            result {
                let! m = metadata ()

                return!
                    AddObject.add db {
                        AddObject.AddSpec.create r details with
                            Metadata = m
                    }
                    |> semctl
            }

        match! Args.string op "op" with
        | "set" ->
            let! selector = Args.string op "object"

            let! assigned =
                match Args.node op "properties" with
                | null -> Ok []
                | :? JsonObject as props ->
                    Ok [
                        for KeyValue(path, v) in props ->
                            let value =
                                match v with
                                | null -> SetProperty.Value.Null
                                | v when Args.kind v = JsonValueKind.String ->
                                    SetProperty.Value.Text(v.GetValue<string>())
                                | v -> SetProperty.Value.Text(v.ToJsonString())

                            ({ Path = path; Value = value }: SetProperty.Assignment)
                    ]
                | _ -> Error(BadArguments "'properties' must be an object: {\"<path>\": <value or null>}")

            let! unset = Args.optStrings op "unset"

            match assigned @ (unset |> List.map SetProperty.Assignment.unset) with
            | [] -> return! Error(BadArguments "set: give 'properties' (and/or 'unset')")
            | assignments ->
                let! e = entry selector
                return! SetProperty.set db e assignments |> semctl
        | "rename" ->
            let! selector = Args.string op "object"
            let! newName = Args.string op "newName"
            let! target = entry selector
            let! p = Rename.plan db target newName |> semctl

            if Rename.RenamePlan.isNoOp p then
                return {
                    Mutation.Effect.none with
                        Notes = [ $"{ObjectRef.format p.Target.Ref} is already named '{p.NewName}'." ]
                }
            else
                Rename.apply p

                // Report files that name the field are rewritten as `sm rename` does,
                // staged with the batch and written by save_model.
                let reports = Staging.reportsOf s.Source

                let files, warnings =
                    match reports, Report.Change.ofMove p.Target.Ref p.NewRef with
                    | [], _
                    | _, None -> [], []
                    | _, Some change -> Report.planWith batch.Read reports change

                batch.Add files

                let updated =
                    match files with
                    | [] -> []
                    | _ ->
                        let refs = plural (files |> List.sumBy _.References) "reference"
                        let fileCount = plural files.Length "file"

                        [
                            $"Updated {refs} in {fileCount} of the report (staged; written by save_model)"
                        ]

                return {
                    Warnings = p.Warnings @ warnings
                    Renamed = [ p.Target.Ref, p.NewRef ]
                    Notes =
                        $"Renamed {ObjectRef.format p.Target.Ref} -> {ObjectRef.format p.NewRef}"
                        :: updated
                }
        | "add" ->
            let! kind = Args.string op "kind"
            let! table = Args.optString op "table"
            let! name = Args.optString op "name"
            let! expression = Args.optString op "expression"

            let need what (v: string option) =
                match v with
                | Some x -> Ok x
                | None -> Error(BadArguments $"add {kind}: missing '{what}'")

            match kind with
            | "measure" ->
                let! t = need "table" table
                let! n = need "name" name
                let! x = need "expression" expression
                return! add (ObjectRef.inTable ObjectKind.Measure t n) (AddObject.Details.Measure x)
            | "calculatedColumn" ->
                let! t = need "table" table
                let! n = need "name" name
                let! x = need "expression" expression

                return!
                    add
                        (ObjectRef.inTable ObjectKind.Column t n)
                        (AddObject.Details.Column(AddObject.ColumnSource.Calculated x))
            | "dataColumn" ->
                let! t = need "table" table
                let! n = need "name" name
                let! dt = Args.string op "dataType"

                match Enum.TryParse<DataType>(dt, true) with
                | true, dataType when dataType <> DataType.Automatic ->
                    let! sourceColumn = Args.optString op "sourceColumn"

                    return!
                        add
                            (ObjectRef.inTable ObjectKind.Column t n)
                            (AddObject.Details.Column(AddObject.ColumnSource.Data(dataType, sourceColumn)))
                | _ ->
                    return!
                        Error(
                            BadArguments
                                $"add dataColumn: unknown dataType '{dt}' (string, int64, double, decimal, dateTime, boolean, binary)"
                        )
            | "calculatedTable" ->
                let! n = need "name" name
                let! x = need "expression" expression
                return! add (ObjectRef.table n) (AddObject.Details.Table(AddObject.TableSource.Calculated x))
            | "relationship" ->
                let! from = Args.string op "from"
                let! ``to`` = Args.string op "to"
                let! active = Args.bool op "active" true

                return!
                    add
                        {
                            Kind = ObjectKind.Relationship
                            Path = Option.toList name
                        }
                        (AddObject.Details.Relationship {
                            AddObject.RelationshipSpec.between from ``to`` with
                                Inactive = not active
                        })
            | other ->
                return!
                    Error(
                        BadArguments
                            $"add: unknown kind '{other}' (measure, calculatedColumn, dataColumn, calculatedTable, relationship; for anything else use a 'script' operation)"
                    )
        | "remove" ->
            let! selectors = Args.optStrings op "objects"
            let! cascade = Args.bool op "cascade" false
            let! force = Args.bool op "force" false

            match selectors with
            | [] -> return! Error(BadArguments "remove: missing 'objects'")
            | selectors ->
                // Refused while a report next to the model uses what would go, unless forced.
                let check = ReportUsage.checkWith batch.Read (Staging.reportsOf s.Source)

                return!
                    RemoveObject.removeWith check db {
                        RemoveObject.RemoveSpec.ofSelectors selectors with
                            Cascade = cascade
                            Force = force
                    }
                    |> semctl
        | "move" ->
            let! measure = Args.string op "measure"
            let! table = Args.string op "table"
            let! folder = Args.optString op "displayFolder"

            return!
                MoveObject.move db {
                    MoveObject.MoveSpec.Measure = measure
                    Table = table
                    DisplayFolder = folder
                }
                |> semctl
        | "script" ->
            let! text = Args.string op "dax"
            return! ApplyScript.applyText "script" db text |> semctl
        | other -> return! Error(BadArguments $"unknown op '{other}' (set, rename, add, remove, move, script)")
    }

let private issuesJson (issues: Validate.Issue list) =
    arr (issues |> List.map (ValidateCommand.toFinding >> Findings.findingJson))

let private applyChanges (s: Session) (args: JsonObject) =
    result {
        let! ops =
            match Args.node args "operations" with
            | :? JsonArray as ops when ops.Count > 0 ->
                ops
                |> Seq.indexed
                |> Seq.toList
                |> resultTraverse (fun (i, node) ->
                    match node with
                    | :? JsonObject as o -> Ok(i, o)
                    | _ -> Error(BadArguments $"operations[{i}] is not an object"))
            | _ -> Error(BadArguments "missing 'operations': a non-empty array of operations")

        // One batch, all or nothing: applied to a copy, which replaces the model only if every
        // operation succeeds.
        let work = ModelIO.clone s.Db
        let batch = BatchFiles(s.Model)

        let edit (db: Database) : Result<Mutation.Effect, Failure> =
            ops
            |> List.fold
                (fun acc (i, op) ->
                    acc
                    |> Result.bind (fun (soFar: Mutation.Effect) ->
                        match operation s batch db op with
                        | Ok e ->
                            Ok {
                                Warnings = soFar.Warnings @ e.Warnings
                                Notes = soFar.Notes @ e.Notes
                                Renamed = soFar.Renamed @ e.Renamed
                            }
                        | Error f -> Error(AtOperation(i, f))))
                (Ok Mutation.Effect.none)

        let! applied, resolved = SaveGate.applyResolving work edit
        let files = batch.Edits
        Staging.StagedModel.stage s.Model work applied.Changes files
        s.Prepared <- None
        let sinceLoad = Session.introducedSinceLoad s
        let o = Changes.changesJson "apply_changes" applied.Changes
        o.["introducedErrors"] <- issuesJson applied.IntroducedErrors
        o.["resolvedErrors"] <- issuesJson resolved
        o.["errorsIntroducedSinceLoad"] <- num sinceLoad.Length
        o.["warnings"] <- arr (applied.Effect.Warnings |> List.map str)
        o.["notes"] <- arr (applied.Effect.Notes |> List.map str)
        o.["unsavedChanges"] <- num s.Unsaved

        if not files.IsEmpty then
            o.["reportFiles"] <-
                arr [
                    for f in files ->
                        obj [ "path", str f.Path; "display", str f.Display; "references", num f.References ]
                ]

        o.["saved"] <-
            str (
                if s.Options.Save then
                    "no: the change is in memory; call save_model to write it"
                else
                    "no: this server keeps changes in memory (started without --save)"
            )

        return o :> JsonNode
    }

let private save (s: Session) (args: JsonObject) =
    result {
        let! force = Args.bool args "force" false

        if s.Source.Format = ModelFormat.Xmla then
            return!
                Error(
                    Semctl(
                        RemoteSourceUnsupported(
                            s.Source.Path,
                            "sm mcp can't save to a server; changes stay in memory (`sm deploy` deploys files)"
                        )
                    )
                )
        elif not (Staging.StagedModel.isDirty s.Model) then
            return obj [ "saved", bool false; "note", str "no unsaved changes" ]
        else
            let! saved = Staging.StagedModel.save s.Model force |> semctl

            return
                obj [
                    "saved", bool true
                    "format", str (ModelIO.OutputFormat.token saved.Model.Format)
                    "path", str saved.Model.Path
                    "eol", str (ModelIO.Eol.token saved.Model.Eol)
                    "errorsIntroducedSinceLoad", issuesJson saved.Introduced
                    if not saved.Files.IsEmpty then
                        "reportFiles",
                        arr [
                            for f in saved.Files ->
                                obj [ "path", str f.Path; "display", str f.Display; "references", num f.References ]
                        ]
                ]
    }

// ---- the tool table ----------------------------------------------------------------------------

let private schemaObj (props: (string * JsonNode) list) =
    jsonObj [
        "type", str "object"
        "properties", obj props
        "additionalProperties", bool false
    ]

let private requiredSchema (props: (string * JsonNode) list) (required: string list) =
    jsonObj [
        "type", str "object"
        "properties", obj props
        "required", arr (required |> List.map str)
        "additionalProperties", bool false
    ]

let private strProp (desc: string option) =
    obj [ "type", str "string"; "description", optStr desc ]

let private boolProp () = obj [ "type", str "boolean" ]

let private strArrayProp (desc: string option) =
    obj [
        "type", str "array"
        "items", obj [ "type", str "string" ]
        "description", optStr desc
    ]

let private objectsSchema () =
    strArrayProp (
        Some
            "objects, in any form `sm get` takes: a ref (measure:Sales/Total), DAX style ('Sales'[Total], [Total]) or a bare name"
    )

let tools: Tool list = [
    {
        Name = "model_overview"
        Title = "Model overview"
        Description =
            "Start here. The model's name, compatibility level, object counts, every table (with its column, "
            + "measure, hierarchy and partition counts) and every relationship. Cheap; then use list_objects or "
            + "search_model to narrow down, and get_objects for detail."
        Grant = Grant.Read
        Schema = schemaObj []
        ReadOnly = true
        Destructive = false
        Run = overview
    }
    {
        Name = "list_objects"
        Title = "List objects"
        Description =
            "List the objects inside a container (the model by default, a table, a hierarchy), optionally only "
            + "some kinds, a name wildcard, and property conditions ('isHidden=true', 'expression=*CALCULATE*', "
            + "'displayFolder!=KPIs*'; * and ? wildcards, case-insensitive, all must hold). Returns refs to pass "
            + "to the other tools. At most 500."
        Grant = Grant.Read
        Schema =
            schemaObj [
                "container", strProp (Some "an object; default the model")
                "kinds",
                obj [
                    "type", str "array"
                    "items",
                    obj [
                        "type", str "string"
                        "enum", arr [ for k in ObjectKind.all -> str (ObjectKind.token k) ]
                    ]
                ]
                "name", strProp (Some "wildcard on the object's name")
                "where",
                strArrayProp (
                    Some
                        "<path>=<pattern> or <path>!=<pattern>; `objectKind` is the object's kind (measure, column, ...)"
                )
            ]
        ReadOnly = true
        Destructive = false
        Run = listObjects
    }
    {
        Name = "search_model"
        Title = "Search the model"
        Description =
            "Find text in every object's properties: names, descriptions, DAX and M expressions, format "
            + "strings, annotations, translations. Literal and case-insensitive unless asked; 'in' limits it to "
            + "property paths (wildcards, e.g. 'expression', 'annotations.*'). Returns one hit per matching line."
        Grant = Grant.Read
        Schema =
            requiredSchema [
                "text", strProp None
                "regex", boolProp ()
                "caseSensitive", boolProp ()
                "in", strArrayProp None
                "kinds", strArrayProp None
            ] [ "text" ]
        ReadOnly = true
        Destructive = false
        Run = search
    }
    {
        Name = "get_objects"
        Title = "Get objects"
        Description =
            "Full detail of named objects: their TOM JSON (default) or TMDL, or just some properties by path "
            + "('expression' for an object's DAX, 'formatString', 'source.expression' for a partition's M, "
            + "'annotations.X'). 'children' includes child objects (a table's columns, measures, ...)."
        Grant = Grant.Read
        Schema =
            requiredSchema [
                "objects", objectsSchema ()
                "properties", strArrayProp None
                "format", obj [ "type", str "string"; "enum", arr [ str "json"; str "tmdl" ] ]
                "children", boolProp ()
            ] [ "objects" ]
        ReadOnly = true
        Destructive = false
        Run = getObjects
    }
    {
        Name = "list_properties"
        Title = "List settable properties"
        Description =
            "The properties a 'set' operation can write on an object: path, type, current value, allowed "
            + "values of enums, what a reference points at. 'all' adds read-only ones with the reason."
        Grant = Grant.Read
        Schema = requiredSchema [ "object", strProp None; "all", boolProp () ] [ "object" ]
        ReadOnly = true
        Destructive = false
        Run = listProperties
    }
    {
        Name = "get_dependencies"
        Title = "Get dependencies"
        Description = "What an object's DAX depends on and what refers to it, to 'depth' levels (default 1, 0 for all)."
        Grant = Grant.Read
        Schema =
            requiredSchema [
                "object", strProp None
                "direction",
                obj [
                    "type", str "string"
                    "enum", arr [ str "dependsOn"; str "referencedBy"; str "both" ]
                ]
                "depth", obj [ "type", str "integer"; "minimum", num 0 ]
            ] [ "object" ]
        ReadOnly = true
        Destructive = false
        Run = dependencies
    }
    {
        Name = "validate_model"
        Title = "Validate the model"
        Description =
            "The model's errors and warnings (unresolved references, circular references, duplicate names, "
            + "invalid DAX, TOM's own checks), with dependent errors grouped under their root cause."
        Grant = Grant.Read
        Schema = schemaObj []
        ReadOnly = true
        Destructive = false
        Run = validate
    }
    {
        Name = "lint_dax"
        Title = "Lint DAX"
        Description =
            "DAX anti-patterns in the model's expressions (or only these objects'), with the rule and whether "
            + "sm lint --fix can fix it."
        Grant = Grant.Read
        Schema = schemaObj [ "objects", objectsSchema () ]
        ReadOnly = true
        Destructive = false
        Run = lint
    }
    {
        Name = "format_dax"
        Title = "Format DAX"
        Description =
            "Format DAX text, or an object's expression, in sm's (SQLBI-style) layout. Changes nothing: "
            + "use apply_changes to store the result."
        Grant = Grant.Read
        Schema = schemaObj [ "dax", strProp None; "object", strProp None; "long", boolProp () ]
        ReadOnly = true
        Destructive = false
        Run = formatDax
    }
    {
        Name = "get_full_model"
        Title = "Get the full model"
        Description =
            "The whole model as TMDL. Large models use a great many tokens: prefer model_overview, "
            + "list_objects and get_objects."
        Grant = Grant.Read
        Schema = schemaObj []
        ReadOnly = true
        Destructive = false
        Run = fullModel
    }
    {
        Name = "list_bpa_rules"
        Title = "List Best Practice Analyzer rules"
        Description =
            "The Best Practice Analyzer rules that apply: sm's built-in rules, the model's own, and the "
            + "user's rule files (never any URL)."
        Grant = Grant.Bpa
        Schema = schemaObj []
        ReadOnly = true
        Destructive = false
        Run = listRules
    }
    {
        Name = "run_bpa"
        Title = "Run the Best Practice Analyzer"
        Description = "Run the Best Practice Analyzer (all rules, or these rule IDs) and list the violations."
        Grant = Grant.Bpa
        Schema = schemaObj [ "rules", strArrayProp None ]
        ReadOnly = true
        Destructive = false
        Run = runBpa
    }
    {
        Name = "run_query"
        Title = "Run a DAX query"
        Description =
            "Run a DAX query (EVALUATE ...) and return rows: 100 per result unless maxRows says otherwise, at "
            + "most 500, and the result says when it was cut short. Aggregate in the query rather than "
            + "reading many rows."
        Grant = Grant.DataRead
        Schema =
            requiredSchema [
                "dax", strProp None
                "maxRows", obj [ "type", str "integer"; "minimum", num 1; "maximum", num 500 ]
            ] [ "dax" ]
        ReadOnly = true
        Destructive = false
        Run = runQuery
    }
    {
        Name = "apply_changes"
        Title = "Change the model"
        Description =
            "Apply a batch of operations to the model, all or nothing: if one fails, nothing changes. Each "
            + "operation has an 'op': "
            + "set {object, properties: {path: value or null}, unset: [paths]} (paths from list_properties); "
            + "rename {object, newName} (fixes the DAX that refers to it, and the field references of PBIR "
            + "reports next to the model, written by save_model); "
            + "add {kind: measure|calculatedColumn|dataColumn|calculatedTable|relationship, table, name, expression, "
            + "dataType, sourceColumn, from, to, active, description, displayFolder, formatString, hidden}; "
            + "remove {objects, cascade, force} (refused while other DAX or a report's visuals use them, "
            + "unless force); move {measure, table, displayFolder}; "
            + "script {dax: a DAX script of DEFINE MEASURE/COLUMN/TABLE blocks, created or updated}. "
            + "Returns the changes, and the errors the batch introduced and resolved. Changes stay in memory."
        Grant = Grant.MetadataWrite
        Schema =
            requiredSchema [
                "operations",
                obj [
                    "type", str "array"
                    "minItems", num 1
                    "items",
                    requiredSchema [
                        "op",
                        obj [
                            "type", str "string"
                            "enum", arr (List.map str [ "set"; "rename"; "add"; "remove"; "move"; "script" ])
                        ]
                    ] [ "op" ]
                ]
            ] [ "operations" ]
        ReadOnly = false
        Destructive = true
        Run = applyChanges
    }
    {
        Name = "save_model"
        Title = "Save the model"
        Description =
            "Write the changes made this session back to the model's files, then the report files they "
            + "rewrite. Refused if they introduced errors, unless 'force' is true."
        Grant = Grant.MetadataWrite
        Schema = schemaObj [ "force", boolProp () ]
        ReadOnly = false
        Destructive = true
        Run = save
    }
]

/// The tools these options register: only what the grants cover, and save_model only with --save.
let registered (options: Options) =
    tools
    |> List.filter (fun t ->
        options.Grants.Contains t.Grant
        && (t.Name <> "save_model" || options.Save)
        && (t.Name <> "run_query" || options.Data <> DataSource.NoData))

let private toolJson (t: Tool) : JsonNode =
    obj [
        "name", str t.Name
        "title", str t.Title
        "description", str t.Description
        "inputSchema", t.Schema.DeepClone()
        "annotations",
        obj [
            "title", str t.Title
            "readOnlyHint", bool t.ReadOnly
            "destructiveHint", bool t.Destructive
            "idempotentHint", bool t.ReadOnly
            "openWorldHint", bool false
        ]
    ]

// ---------------------------------------------------------------------------------------------
// The audit log

/// The object names a call's arguments mention (never values, DAX or query text).
let private objectNames (args: JsonObject) =
    let keys = [
        "object"
        "objects"
        "container"
        "table"
        "measure"
        "from"
        "to"
        "name"
        "newName"
    ]

    let rec names (o: JsonObject) = [
        for key in keys do
            match Args.node o key with
            | null -> ()
            | :? JsonArray as a ->
                for x in a do
                    if Args.kind x = JsonValueKind.String then
                        yield x.GetValue<string>()
            | v when Args.kind v = JsonValueKind.String -> yield v.GetValue<string>()
            | _ -> ()

        match Args.node o "operations" with
        | :? JsonArray as ops ->
            for op in ops do
                match op with
                | :? JsonObject as op -> yield! names op
                | _ -> ()
        | _ -> ()
    ]

    names args |> List.distinct

let private audit (s: Session) (fields: (string * JsonNode) list) =
    match s.Options.Audit with
    | None -> ()
    | Some w ->
        let line =
            obj (
                ("time", str (DateTime.UtcNow.ToString("o", Globalization.CultureInfo.InvariantCulture)))
                :: fields
            )

        w.Write(JsonRpc.compact line)
        w.Write('\n')
        w.Flush()

// ---------------------------------------------------------------------------------------------
// Dispatch

let private textResult (structured: JsonNode) (isError: bool) : JsonNode =
    let content = JsonObject()
    content.["type"] <- JsonValue.Create "text"
    content.["text"] <- JsonValue.Create(JsonRpc.compact structured)
    let o = JsonObject()
    o.["content"] <- JsonArray(content :> JsonNode)
    o.["structuredContent"] <- structured
    o.["isError"] <- JsonValue.Create isError
    o

let rec private failureJson (f: Failure) =
    match f with
    | Semctl e -> semctlErrorJson e
    | BadArguments message -> errorJson "invalid-arguments" message
    | Internal message -> errorJson "internal" message
    | AtOperation(i, inner) ->
        let json = failureJson inner
        let error = json.["error"].AsObject()
        error.["operation"] <- num i
        let message = error.["message"].GetValue<string>()
        error.["message"] <- str $"operations[{i}]: {message}; nothing was changed"
        json

let rec private failureCode (f: Failure) =
    match f with
    | Semctl e -> SemctlError.code e
    | BadArguments _ -> "invalid-arguments"
    | Internal _ -> "internal"
    | AtOperation(_, inner) -> failureCode inner

/// Run a tools/call: the JSON-RPC result, or a JSON-RPC error (code, message) for an unknown tool or
/// malformed params.
let private call (s: Session) (available: Tool list) (parameters: JsonNode) : Result<JsonNode, int * string> =
    match parameters with
    | :? JsonObject as p ->
        match Args.optString p "name", Args.node p "arguments" with
        | Ok(Some name), args when isNull args || (args :? JsonObject) ->
            let args =
                match args with
                | :? JsonObject as a -> a
                | _ -> JsonObject()

            match available |> List.tryFind (fun t -> t.Name = name) with
            | None ->
                audit s [ "event", str "call"; "tool", str name; "outcome", str "unknown-tool" ]
                Error(JsonRpc.Code.invalidParams, $"Unknown tool: {name}")
            | Some tool ->
                let outcome =
                    try
                        tool.Run s args
                    with e ->
                        s.Ctx.Err.WriteLine($"sm mcp: internal error in {name}: {e}")
                        Error(Internal e.Message)

                let names = objectNames args

                match outcome with
                | Ok structured ->
                    audit s [
                        "event", str "call"
                        "tool", str name
                        "objects", arr (names |> List.map str)
                        "outcome", str "ok"
                    ]

                    Ok(textResult structured false)
                | Error f ->
                    audit s [
                        "event", str "call"
                        "tool", str name
                        "objects", arr (names |> List.map str)
                        "outcome", str "error"
                        "errorCode", str (failureCode f)
                    ]

                    Ok(textResult (failureJson f) true)
        | _ -> Error(JsonRpc.Code.invalidParams, "tools/call takes {\"name\": <tool>, \"arguments\": {...}}")
    | _ -> Error(JsonRpc.Code.invalidParams, "tools/call takes {\"name\": <tool>, \"arguments\": {...}}")

let private instructions =
    "sm serves one Power BI / Analysis Services semantic model. Start with model_overview, narrow down with "
    + "list_objects or search_model, then read detail with get_objects, list_properties and get_dependencies. "
    + "Objects are named as `sm` names them (measure:Sales/Total, 'Sales'[Total], [Total], Sales). "
    + "Writes (apply_changes) are one atomic batch and report the errors they introduce; they stay in memory "
    + "unless a save_model tool is offered."

/// Answer one request (Some response) or notification (None).
let handle (s: Session) (available: Tool list) (message: JsonRpc.Incoming) : JsonNode option =
    match message with
    | JsonRpc.Notification _
    | JsonRpc.Response -> None
    | JsonRpc.Invalid(id, code, text) -> Some(JsonRpc.error id code text)
    | JsonRpc.Request(id, method, parameters) ->
        match method with
        | "initialize" ->
            let requested =
                match parameters with
                | :? JsonObject as p ->
                    match Args.optString p "protocolVersion" with
                    | Ok v -> v
                    | Error _ -> None
                | _ -> None

            let negotiated =
                match requested with
                | Some v when List.contains v acceptedVersions -> v
                | _ -> protocolVersion

            Some(
                JsonRpc.result
                    id
                    (obj [
                        "protocolVersion", str negotiated
                        "capabilities", obj [ "tools", obj [ "listChanged", bool false ] ]
                        "serverInfo", obj [ "name", str "sm"; "title", str "semctl"; "version", str (version ()) ]
                        "instructions", str instructions
                    ])
            )
        | "ping" -> Some(JsonRpc.result id (JsonObject()))
        | "tools/list" -> Some(JsonRpc.result id (obj [ "tools", arr (available |> List.map toolJson) ]))
        | "tools/call" ->
            match call s available parameters with
            | Ok r -> Some(JsonRpc.result id r)
            | Error(code, text) -> Some(JsonRpc.error id code text)
        | other -> Some(JsonRpc.error id JsonRpc.Code.methodNotFound $"Method not found: {other}")

/// Serve until the input ends. Frames go to `output` only.
let serve (s: Session) (input: TextReader) (output: TextWriter) =
    let available = registered s.Options

    audit s [
        "event", str "start"
        "model", str s.Source.Path
        "grants", arr (s.Options.Grants |> Set.toList |> List.map (Grant.token >> str))
        "tools", arr (available |> List.map (fun t -> str t.Name))
    ]

    let rec loop () =
        match JsonRpc.readLine input with
        | None -> ()
        | Some line ->
            match handle s available (JsonRpc.parse line) with
            | Some response -> JsonRpc.write output response
            | None -> ()

            loop ()

    loop ()
