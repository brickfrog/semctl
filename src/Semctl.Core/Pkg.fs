/// DAX packages (daxlib.org): libraries of DAX user-defined functions, installed into a model the
/// way Tabular Editor 3's DAX Package Manager (3.24+) installs them, so TE3 sees the same packages.
///
/// A package is a manifest (`manifest.daxlib`, JSON) and its functions (`lib/functions.tmdl`, a
/// TMDL document of `function` objects), as a folder or a `.daxpkg` zip.
///
/// What TE3 records, read from a model it wrote (spaceparts-analytics) and TE3's documentation:
///   - on the model, extended property `TabularEditor_ModelDaxPkgTable` (JSON): one entry per
///     installed package, keyed by a sequential integer: {"1": {"Provider": "https://api.daxlib.org",
///     "PackageId": "DaxLib.FormatString", "Version": "0.1.2"}, ...}, indented by two spaces;
///   - on the model, `TabularEditor_ModelDaxPkgSeq` (string): incremented on every install; the
///     source of new keys (spaceparts has keys 1 and 2 and Seq 3);
///   - on each function, `TabularEditor_ObjDaxPkgHandle` (string): its package's key, and
///     `TabularEditor_ObjDaxPkgContentHash` (string): the upper-case hex SHA-256 of the UTF-8 of the
///     function's expression as installed, which tells whether it was changed since. Checked: for
///     every unchanged package function in spaceparts, this is the hash of the expression TOM reads
///     from the package's functions.tmdl.
/// Line endings don't count as a change (TE3 3.24.1: Power BI Desktop rewrites them).
module Semctl.Pkg

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.AnalysisServices.Tabular
open Microsoft.AnalysisServices.Tabular.Serialization

/// The provider TE3 records for daxlib.org packages. sm records it for packages from a local
/// source too (a daxlib checkout or downloaded .daxpkg files are daxlib's packages), so TE3 offers
/// their updates.
let daxlibProvider = "https://api.daxlib.org"

let tableProperty = "TabularEditor_ModelDaxPkgTable"
let seqProperty = "TabularEditor_ModelDaxPkgSeq"
let handleProperty = "TabularEditor_ObjDaxPkgHandle"
let hashProperty = "TabularEditor_ObjDaxPkgContentHash"

/// Functions (UDFs) need this compatibility level.
let functionLevel = 1702

// ---- versions ----------------------------------------------------------------------------------

/// A package version: major.minor.patch with an optional pre-release suffix (daxlib's manifest
/// schema: `^[0-9]+\.[0-9]+\.[0-9]+(-[a-zA-Z0-9]+)?$`; dotted pre-release parts are accepted too).
/// Compared by SemVer 2.0 precedence.
module Version =
    type T = {
        Major: int64
        Minor: int64
        Patch: int64
        Pre: string list
    }

    let tryParse (s: string) : T option =
        let s = s.Trim()

        let core, pre =
            match s.IndexOf '-' with
            | -1 -> s, []
            | i -> s.Substring(0, i), s.Substring(i + 1).Split('.') |> List.ofArray

        let nums = core.Split '.'

        let num (x: string) =
            match Int64.TryParse(x, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
            | true, n -> Some n
            | _ -> None

        if
            nums.Length <> 3
            || pre
               |> List.exists (fun p -> p = "" || not (p |> Seq.forall Char.IsAsciiLetterOrDigit))
        then
            None
        else
            match num nums.[0], num nums.[1], num nums.[2] with
            | Some a, Some b, Some c ->
                Some {
                    Major = a
                    Minor = b
                    Patch = c
                    Pre = pre
                }
            | _ -> None

    let isPrerelease (s: string) =
        tryParse s |> Option.exists (fun v -> not v.Pre.IsEmpty)

    let private comparePre (a: string list) (b: string list) =
        let ident (x: string) =
            if x |> Seq.forall Char.IsAsciiDigit then
                Choice1Of2(Decimal.Parse(x, Globalization.CultureInfo.InvariantCulture))
            else
                Choice2Of2 x

        let rec go a b =
            match a, b with
            | [], [] -> 0
            | [], _ -> -1
            | _, [] -> 1
            | x :: xs, y :: ys ->
                let c =
                    match ident x, ident y with
                    | Choice1Of2 m, Choice1Of2 n -> compare m n
                    | Choice1Of2 _, Choice2Of2 _ -> -1
                    | Choice2Of2 _, Choice1Of2 _ -> 1
                    | Choice2Of2 m, Choice2Of2 n -> String.CompareOrdinal(m, n) |> sign

                if c <> 0 then c else go xs ys

        match a, b with
        | [], [] -> 0
        | [], _ -> 1
        | _, [] -> -1
        | _ -> go a b

    /// Compare two version strings; one that doesn't parse sorts before any that does (then ordinal).
    let compareStrings (a: string) (b: string) =
        match tryParse a, tryParse b with
        | Some x, Some y ->
            let c = compare (x.Major, x.Minor, x.Patch) (y.Major, y.Minor, y.Patch)
            if c <> 0 then c else comparePre x.Pre y.Pre
        | Some _, None -> 1
        | None, Some _ -> -1
        | None, None -> String.CompareOrdinal(a, b) |> sign

    /// Two version strings that name the same version ("1.0.0" and "1.0.0"; case-insensitive suffix).
    let same (a: string) (b: string) =
        String.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)

    /// The latest of these versions: the highest stable one, or with `prerelease` the highest of all
    /// (a package with only pre-release versions has no latest stable version).
    let latest (prerelease: bool) (versions: string list) =
        versions
        |> List.filter (fun v -> prerelease || not (isPrerelease v))
        |> List.sortWith compareStrings
        |> List.tryLast

// ---- packages ----------------------------------------------------------------------------------

type Dependency = { Id: string; Version: string }

/// A package's manifest (`manifest.daxlib`).
type Manifest = {
    Id: string
    Version: string
    Authors: string list
    Description: string
    Tags: string list
    ReleaseNotes: string option
    ProjectUrl: string option
    RepositoryUrl: string option
    Dependencies: Dependency list
}

/// A package version, read: its manifest and its functions (TOM objects of a scratch database,
/// copied into a model on install).
type Package = {
    Manifest: Manifest
    Functions: Function list
    /// Where it was read from (a path, or a URL).
    Origin: string
}

/// The package ID pattern of daxlib's manifest schema.
let private idPattern =
    RegularExpressions.Regex(@"^\w+([.-]\w+)*$", RegularExpressions.RegexOptions.CultureInvariant)

let isValidId (id: string) =
    id.Length <= 128 && idPattern.IsMatch id

/// Read a manifest. `authors` and `tags` are comma-separated strings in daxlib's schema (the API
/// returns arrays; both are accepted).
let parseManifest (json: string) : Result<Manifest, string> =
    try
        match
            JsonNode.Parse(json, documentOptions = JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip))
        with
        | :? JsonObject as o ->
            let text (name: string) =
                match o.[name] with
                | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
                | _ -> None

            let list (name: string) =
                match o.[name] with
                | :? JsonArray as a ->
                    [
                        for x in a do
                            match x with
                            | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String ->
                                yield v.GetValue<string>().Trim()
                            | _ -> ()
                    ]
                    |> List.filter (fun s -> s <> "")
                | _ ->
                    match text name with
                    | Some s ->
                        s.Split(',')
                        |> Array.map _.Trim()
                        |> Array.filter (fun s -> s <> "")
                        |> List.ofArray
                    | None -> []

            let dependencies =
                match o.["dependencies"] with
                | :? JsonArray as a -> [
                    for x in a do
                        match x with
                        | :? JsonObject as d ->
                            let field (n: string) =
                                match d.[n] with
                                | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String ->
                                    Some(v.GetValue<string>())
                                | _ -> None

                            match field "id", field "version" with
                            | Some id, Some v -> yield Ok { Id = id; Version = v }
                            | _ -> yield Error "a dependency without an id and a version"
                        | _ -> yield Error "a dependency that isn't an object"
                  ]
                | null -> []
                | _ -> [ Error "dependencies isn't an array" ]

            match text "id", text "version" with
            | None, _ -> Error "the manifest has no id"
            | _, None -> Error "the manifest has no version"
            | Some id, _ when not (isValidId id) -> Error $"'{id}' isn't a valid package id"
            | _, Some v when (Version.tryParse v).IsNone -> Error $"'{v}' isn't a valid version"
            | Some id, Some v ->
                match
                    dependencies
                    |> List.tryPick (function
                        | Error e -> Some e
                        | Ok _ -> None)
                with
                | Some e -> Error e
                | None ->
                    Ok {
                        Id = id
                        Version = v.Trim()
                        Authors = list "authors"
                        Description = text "description" |> Option.defaultValue ""
                        Tags = list "tags"
                        ReleaseNotes = text "releaseNotes" |> Option.filter (fun s -> s <> "")
                        ProjectUrl = text "projectUrl" |> Option.filter (fun s -> s <> "")
                        RepositoryUrl = text "repositoryUrl" |> Option.filter (fun s -> s <> "")
                        Dependencies =
                            dependencies
                            |> List.choose (function
                                | Ok d -> Some d
                                | Error _ -> None)
                    }
        | _ -> Error "the manifest isn't a JSON object"
    with :? JsonException as e ->
        Error $"the manifest isn't valid JSON: {e.Message}"

/// Read a package's functions.tmdl: only its `function` objects are taken (anything else in the
/// document is an error, so a package can't slip tables or roles into a model).
let parseFunctions (tmdl: string) : Result<Function list, string> =
    try
        let context = MetadataSerializationContext.Create(MetadataSerializationStyle.Tmdl)
        use reader = new StringReader(tmdl)
        context.ReadFromDocument("functions.tmdl", reader, Text.Encoding.UTF8) |> ignore
        let db = context.ToDatabase()
        let m = db.Model

        let others = [
            if m.Tables.Count > 0 then
                "tables"
            if m.Relationships.Count > 0 then
                "relationships"
            if m.Roles.Count > 0 then
                "roles"
            if m.Expressions.Count > 0 then
                "expressions"
            if m.DataSources.Count > 0 then
                "data sources"
            if m.Perspectives.Count > 0 then
                "perspectives"
            if m.Cultures.Count > 0 then
                "cultures"
        ]

        match others with
        | [] ->
            // Expressions with "\n" line ends on every OS, whatever the file has: the content hash
            // is taken over them, and TE3's recorded hashes are of the "\n" form.
            for f in m.Functions do
                if not (isNull f.Expression) && f.Expression.Contains '\r' then
                    f.Expression <- f.Expression.Replace("\r\n", "\n")

            Ok [ for f in m.Functions -> f ]
        | o -> Error("functions.tmdl holds more than functions: " + String.concat ", " o)
    with e ->
        Error $"lib/functions.tmdl can't be read: {e.Message}"

/// The largest package read (daxlib's biggest are a few hundred KB) and the largest file in one.
let maxPackageBytes = 16L * 1024L * 1024L

/// Text of a package file: UTF-8 (a BOM is dropped).
let private decode (bytes: byte array) =
    let s = Text.Encoding.UTF8.GetString bytes
    if s.StartsWith '﻿' then s.Substring 1 else s

let private build (origin: string) (manifest: string) (functions: string) =
    match parseManifest manifest with
    | Error reason -> Error(InvalidPackage(origin, reason))
    | Ok m ->
        match parseFunctions functions with
        | Error reason -> Error(InvalidPackage(origin, reason))
        | Ok fs ->
            Ok {
                Manifest = m
                Functions = fs
                Origin = origin
            }

/// A package folder: `manifest.daxlib` and `lib/functions.tmdl`.
let readFolder (dir: string) : Result<Package, SemctlError> =
    let manifest = Path.Combine(dir, "manifest.daxlib")
    let functions = Path.Combine(dir, "lib", "functions.tmdl")

    if not (File.Exists manifest) then
        Error(InvalidPackage(dir, "no manifest.daxlib"))
    elif not (File.Exists functions) then
        Error(InvalidPackage(dir, "no lib/functions.tmdl"))
    elif FileInfo(functions).Length > maxPackageBytes then
        Error(InvalidPackage(dir, "lib/functions.tmdl is too large"))
    else
        try
            build dir (decode (File.ReadAllBytes manifest)) (decode (File.ReadAllBytes functions))
        with e ->
            Error(InvalidPackage(dir, e.Message))

/// The largest zip read: a `.daxpkg`, or an archive of many packages (a zip of a daxlib checkout
/// is about 5 MB).
let maxArchiveBytes = 256L * 1024L * 1024L

let private entryName (e: ZipArchiveEntry) =
    e.FullName.Replace('\\', '/').TrimStart('/')

/// Text files of a zip, read in memory, no more than `maxPackageBytes` of any.
let private zipTexts
    (origin: string)
    (bytes: byte array)
    (names: string list)
    : Result<string option list, SemctlError> =
    if int64 bytes.Length > maxArchiveBytes then
        Error(InvalidPackage(origin, "the file is too large"))
    else
        try
            use zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read)

            let entry (name: string) =
                zip.Entries |> Seq.tryFind (fun e -> entryName e = name)

            let read (e: ZipArchiveEntry) =
                use s = e.Open()
                use ms = new MemoryStream()
                // Read no more than the cap, whatever the entry's header says.
                let buffer = Array.zeroCreate<byte> 81920
                let mutable n = s.Read(buffer, 0, buffer.Length)

                while n > 0 do
                    if ms.Length + int64 n > maxPackageBytes then
                        failwith $"{e.FullName} is too large"

                    ms.Write(buffer, 0, n)
                    n <- s.Read(buffer, 0, buffer.Length)

                decode (ms.ToArray())

            Ok(names |> List.map (fun n -> entry n |> Option.map read))
        with
        | :? InvalidDataException as e -> Error(InvalidPackage(origin, $"not a zip file: {e.Message}"))
        | e -> Error(InvalidPackage(origin, e.Message))

/// The packages in a zip: the folders in it (as entry-name prefixes, "" for the root, as in a
/// `.daxpkg`) that hold a manifest.daxlib. Hidden folders are skipped.
let zipPackages (origin: string) (bytes: byte array) : Result<string list, SemctlError> =
    if int64 bytes.Length > maxArchiveBytes then
        Error(InvalidPackage(origin, "the file is too large"))
    else
        try
            use zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read)

            Ok [
                for e in zip.Entries do
                    let name = entryName e

                    if name = "manifest.daxlib" || name.EndsWith "/manifest.daxlib" then
                        let prefix = name.Substring(0, name.Length - "manifest.daxlib".Length)

                        if not (prefix.Split('/') |> Array.exists (fun p -> p.StartsWith '.')) then
                            yield prefix
            ]
            |> Result.map List.sort
        with
        | :? InvalidDataException as e -> Error(InvalidPackage(origin, $"not a zip file: {e.Message}"))
        | e -> Error(InvalidPackage(origin, e.Message))

/// The package in a zip's folder `prefix` ("" for a `.daxpkg`), read in memory.
let readZipAt (origin: string) (bytes: byte array) (prefix: string) : Result<Package, SemctlError> =
    zipTexts origin bytes [ prefix + "manifest.daxlib"; prefix + "lib/functions.tmdl" ]
    |> Result.bind (function
        | [ None; _ ] -> Error(InvalidPackage(origin, "no manifest.daxlib"))
        | [ _; None ] -> Error(InvalidPackage(origin, "no lib/functions.tmdl"))
        | [ Some m; Some f ] -> build origin m f
        | _ -> Error(InvalidPackage(origin, "unreadable")))

/// Just the manifest of the package in a zip's folder `prefix`.
let readZipManifestAt (origin: string) (bytes: byte array) (prefix: string) : Result<Manifest, SemctlError> =
    zipTexts origin bytes [ prefix + "manifest.daxlib" ]
    |> Result.bind (function
        | [ Some m ] -> parseManifest m |> Result.mapError (fun r -> InvalidPackage(origin, r))
        | _ -> Error(InvalidPackage(origin, "no manifest.daxlib")))

/// A `.daxpkg`, read in memory.
let readZip (origin: string) (bytes: byte array) : Result<Package, SemctlError> =
    if int64 bytes.Length > maxPackageBytes then
        Error(InvalidPackage(origin, "the package is too large"))
    else
        readZipAt origin bytes ""

/// Just the manifest of a `.daxpkg`.
let readZipManifest (origin: string) (bytes: byte array) : Result<Manifest, SemctlError> =
    readZipManifestAt origin bytes ""

/// Just the manifest of a package folder.
let readFolderManifest (dir: string) : Result<Manifest, SemctlError> =
    let manifest = Path.Combine(dir, "manifest.daxlib")

    try
        parseManifest (decode (File.ReadAllBytes manifest))
        |> Result.mapError (fun r -> InvalidPackage(dir, r))
    with e ->
        Error(InvalidPackage(dir, e.Message))

// ---- what a model has installed ----------------------------------------------------------------

/// The upper-case hex SHA-256 of an expression's UTF-8, as TE3 records it.
let contentHash (expression: string) =
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expression: string)))

let private lf (s: string) = s.Replace("\r\n", "\n")

/// Whether a function's expression is still what was installed (its hash), line endings aside.
let unchanged (expression: string) (hash: string) =
    let expression = if isNull expression then "" else expression
    let n = lf expression

    [ expression; n; n.Replace("\n", "\r\n") ]
    |> List.exists (fun e -> String.Equals(contentHash e, hash, StringComparison.OrdinalIgnoreCase))

/// A model's or a function's extended properties (TOM gives each owner its own collection type).
type private Props = {
    Find: string -> ExtendedProperty
    Add: ExtendedProperty -> unit
    Remove: ExtendedProperty -> unit
}

let private ofModel (m: Model) = {
    Find = fun n -> m.ExtendedProperties.Find n
    Add = fun p -> m.ExtendedProperties.Add p
    Remove = fun p -> m.ExtendedProperties.Remove p |> ignore
}

let private ofFunction (f: Function) = {
    Find = fun n -> f.ExtendedProperties.Find n
    Add = fun p -> f.ExtendedProperties.Add p
    Remove = fun p -> f.ExtendedProperties.Remove p |> ignore
}

let private extended (props: Props) (name: string) =
    match props.Find name with
    | :? StringExtendedProperty as s -> Some s.Value
    | :? JsonExtendedProperty as j -> Some j.Value
    | _ -> None

type FunctionState =
    /// The expression is as installed.
    | Unchanged
    /// The expression was changed since it was installed.
    | Modified
    /// No content hash: can't tell.
    | Unknown

type InstalledFunction = { Name: string; State: FunctionState }

/// An installed package: its entry in the model's package table and the functions that carry its key.
type Installed = {
    Key: int
    Provider: string
    PackageId: string
    Version: string
    Functions: InstalledFunction list
}

/// What a model records about packages.
type State = {
    Installed: Installed list
    /// Functions with a package key the table doesn't have (name, key).
    Orphans: (string * string) list
    /// The next key's sequence number (TabularEditor_ModelDaxPkgSeq), if set.
    Seq: int option
}

let private parseInt (s: string) =
    match Int32.TryParse(s.Trim(), Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture) with
    | true, n -> Some n
    | _ -> None

/// The package table's JSON, as an object (absent: empty).
let private readTable (db: Database) : Result<JsonObject, SemctlError> =
    match extended (ofModel db.Model) tableProperty with
    | None -> Ok(JsonObject())
    | Some json when String.IsNullOrWhiteSpace json -> Ok(JsonObject())
    | Some json ->
        try
            match JsonNode.Parse json with
            | :? JsonObject as o -> Ok o
            | _ -> Error(InvalidPackageMetadata $"{tableProperty} isn't a JSON object")
        with :? JsonException as e ->
            Error(InvalidPackageMetadata $"{tableProperty} isn't valid JSON: {e.Message}")

let private entryText (entry: JsonNode) (name: string) =
    match entry with
    | :? JsonObject as o ->
        match o.[name] with
        | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> v.GetValue<string>()
        | _ -> ""
    | _ -> ""

let private functionHandle (f: Function) =
    extended (ofFunction f) handleProperty |> Option.map _.Trim()

/// What the model has installed.
let state (db: Database) : Result<State, SemctlError> =
    readTable db
    |> Result.bind (fun table ->
        let entries = [
            for kv in table do
                match parseInt kv.Key with
                | Some key -> yield Ok(key, kv.Value)
                | None ->
                    yield Error(InvalidPackageMetadata $"{tableProperty} has a key that isn't a number: '{kv.Key}'")
        ]

        match
            entries
            |> List.tryPick (function
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some e -> Error e
        | None ->
            let entries =
                entries
                |> List.choose (function
                    | Ok x -> Some x
                    | Error _ -> None)

            let keys = entries |> List.map fst |> Set.ofList

            let functionsOf (key: int) = [
                for f in db.Model.Functions do
                    match functionHandle f with
                    | Some h when parseInt h = Some key ->
                        yield {
                            Name = f.Name
                            State =
                                match extended (ofFunction f) hashProperty with
                                | Some hash when hash.Trim() <> "" ->
                                    if unchanged f.Expression (hash.Trim()) then
                                        Unchanged
                                    else
                                        Modified
                                | _ -> Unknown
                        }
                    | _ -> ()
            ]

            Ok {
                Installed = [
                    for key, entry in entries ->
                        {
                            Key = key
                            Provider = entryText entry "Provider"
                            PackageId = entryText entry "PackageId"
                            Version = entryText entry "Version"
                            Functions = functionsOf key
                        }
                ]
                Orphans = [
                    for f in db.Model.Functions do
                        match functionHandle f with
                        | Some h when not (parseInt h |> Option.exists keys.Contains) -> yield f.Name, h
                        | _ -> ()
                ]
                Seq = extended (ofModel db.Model) seqProperty |> Option.bind parseInt
            })

/// The table JSON as TE3 writes it: indented by two spaces, "\n" line ends, nothing escaped that
/// needn't be. (Byte for byte what spaceparts holds, checked by the tests.)
let tableJson (table: JsonObject) =
    table.ToJsonString(
        JsonSerializerOptions(
            WriteIndented = true,
            IndentSize = 2,
            NewLine = "\n",
            Encoder = Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        )
    )

let private setString (props: Props) (name: string) (value: string) =
    match props.Find name with
    | :? StringExtendedProperty as s ->
        if s.Value <> value then
            s.Value <- value
    | null -> props.Add(StringExtendedProperty(Name = name, Value = value))
    | other ->
        props.Remove other |> ignore
        props.Add(StringExtendedProperty(Name = name, Value = value))

let private writeTable (db: Database) (table: JsonObject) =
    let value = tableJson table
    let props = ofModel db.Model

    match props.Find tableProperty with
    | :? JsonExtendedProperty as j ->
        if j.Value <> value then
            j.Value <- value
    | null -> props.Add(JsonExtendedProperty(Name = tableProperty, Value = value))
    | other ->
        props.Remove other |> ignore
        props.Add(JsonExtendedProperty(Name = tableProperty, Value = value))

let private unlink (f: Function) =
    for name in [ handleProperty; hashProperty ] do
        match f.ExtendedProperties.Find name with
        | null -> ()
        | p -> f.ExtendedProperties.Remove p |> ignore

let private link (f: Function) (key: int) =
    setString (ofFunction f) hashProperty (contentHash f.Expression)
    setString (ofFunction f) handleProperty (string key)

/// A package's function, ready for the model: a copy without any package-tracking properties of
/// its own (only sm sets those).
let private copyOf (source: Function) =
    let f = source.Clone()
    unlink f
    f

/// Copy what a package defines onto an existing function, keeping the model's own extras
/// (lineage tag, annotations the package doesn't set).
let private overwrite (target: Function) (source: Function) =
    target.Expression <- source.Expression
    target.Description <- source.Description
    target.IsHidden <- source.IsHidden

    for a in source.Annotations do
        match target.Annotations.Find a.Name with
        | null -> target.Annotations.Add(Annotation(Name = a.Name, Value = a.Value))
        | t ->
            if t.Value <> a.Value then
                t.Value <- a.Value

    for p in source.ExtendedProperties do
        if not (p.Name = handleProperty || p.Name = hashProperty) then
            match target.ExtendedProperties.Find p.Name, p with
            | null, (:? StringExtendedProperty as s) ->
                target.ExtendedProperties.Add(StringExtendedProperty(Name = s.Name, Value = s.Value))
            | null, (:? JsonExtendedProperty as j) ->
                target.ExtendedProperties.Add(JsonExtendedProperty(Name = j.Name, Value = j.Value))
            | (:? StringExtendedProperty as t), (:? StringExtendedProperty as s) ->
                if t.Value <> s.Value then
                    t.Value <- s.Value
            | (:? JsonExtendedProperty as t), (:? JsonExtendedProperty as s) ->
                if t.Value <> s.Value then
                    t.Value <- s.Value
            | _ -> ()

// ---- install, update, remove -------------------------------------------------------------------

/// What to do with a package function whose name a model function already has (TE3 asks: Yes =
/// overwrite, No = skip, Cancel).
type OnConflict =
    | RefuseConflicts
    | OverwriteConflicts
    | SkipConflicts

/// What to do with installed functions changed since they were installed, on update or remove
/// (TE3 asks: Yes = overwrite/remove them, No = leave them, Cancel).
type OnModified =
    | RefuseModified
    | DiscardModified
    | KeepModified

type InstallOptions = {
    OnConflict: OnConflict
    /// Install even if the package's dependencies aren't installed.
    IgnoreDependencies: bool
    /// Recorded as the package's provider.
    Provider: string
}

let private requireLevel (db: Database) =
    if db.CompatibilityLevel >= functionLevel then
        Ok()
    else
        Error(
            CannotAdd(
                "model",
                $"DAX packages hold user-defined functions, which need compatibility level {functionLevel} or higher (the model is at {db.CompatibilityLevel})"
            )
        )

let private findInstalled (st: State) (id: string) =
    st.Installed
    |> List.tryFind (fun i -> String.Equals(i.PackageId, id, StringComparison.OrdinalIgnoreCase))

let private notInstalled (st: State) (id: string) =
    PackageNotInstalled(id, st.Installed |> List.map _.PackageId)

let private functionsWithKey (db: Database) (key: int) = [
    for f in db.Model.Functions do
        match functionHandle f with
        | Some h when parseInt h = Some key -> yield f
        | _ -> ()
]

let private isModified (f: Function) =
    match extended (ofFunction f) hashProperty with
    | Some hash when hash.Trim() <> "" -> not (unchanged f.Expression (hash.Trim()))
    | _ -> false

let private plural (n: int) (one: string) (many: string) =
    if n = 1 then $"1 {one}" else $"{n} {many}"

let private joined (parts: string list) = String.concat ", " parts

let private functionCount (n: int) = plural n "function" "functions"

/// The key for a new install: past the sequence number, every key in the table and every key a
/// function carries, so it never collides whichever way Seq was kept.
let private nextKey (db: Database) (table: JsonObject) =
    let seqValue =
        extended (ofModel db.Model) seqProperty
        |> Option.bind parseInt
        |> Option.defaultValue 1

    let tableMax = table |> Seq.choose (fun kv -> parseInt kv.Key) |> Seq.fold max 0

    let functionMax =
        db.Model.Functions
        |> Seq.choose (fun f -> functionHandle f |> Option.bind parseInt)
        |> Seq.fold max 0

    max seqValue (max tableMax functionMax + 1)

/// Install a package into the model. Refused if it is installed already (use update), when a
/// package function's name is taken (unless OnConflict says otherwise), or when a dependency isn't
/// installed (unless IgnoreDependencies).
let install (db: Database) (pkg: Package) (opts: InstallOptions) : Result<Mutation.Effect, SemctlError> =
    requireLevel db
    |> Result.bind (fun () -> state db)
    |> Result.bind (fun st ->
        let m = pkg.Manifest

        match findInstalled st m.Id with
        | Some i ->
            Error(
                PackageConflict(
                    m.Id,
                    $"version {i.Version} is installed already; `sm pkg update` changes the version",
                    []
                )
            )
        | None ->
            let missing =
                m.Dependencies |> List.filter (fun d -> (findInstalled st d.Id).IsNone)

            if not missing.IsEmpty && not opts.IgnoreDependencies then
                Error(
                    PackageConflict(
                        m.Id,
                        "it depends on packages the model doesn't have; install them first, or pass --ignore-dependencies",
                        missing |> List.map (fun d -> $"{d.Id} {d.Version}")
                    )
                )
            else
                let taken =
                    pkg.Functions
                    |> List.filter (fun f -> not (isNull (db.Model.Functions.Find f.Name)))
                    |> List.map _.Name

                match opts.OnConflict, taken with
                | RefuseConflicts, _ :: _ ->
                    Error(
                        PackageConflict(
                            m.Id,
                            "the model already has functions of these names; pass --overwrite to replace them or --skip-existing to keep them",
                            taken
                        )
                    )
                | _ ->
                    readTable db
                    |> Result.map (fun table ->
                        let key = nextKey db table
                        let mutable added = 0
                        let mutable replaced = 0
                        let mutable skipped = []

                        for source in pkg.Functions do
                            match db.Model.Functions.Find source.Name with
                            | null ->
                                let f = copyOf source
                                db.Model.Functions.Add f
                                link f key
                                added <- added + 1
                            | existing when opts.OnConflict = OverwriteConflicts ->
                                overwrite existing source
                                link existing key
                                replaced <- replaced + 1
                            | existing -> skipped <- existing.Name :: skipped

                        table.[string key] <-
                            JsonObject(
                                [
                                    Collections.Generic.KeyValuePair<string, JsonNode>(
                                        "Provider",
                                        JsonValue.Create opts.Provider
                                    )
                                    Collections.Generic.KeyValuePair<string, JsonNode>(
                                        "PackageId",
                                        JsonValue.Create m.Id
                                    )
                                    Collections.Generic.KeyValuePair<string, JsonNode>(
                                        "Version",
                                        JsonValue.Create m.Version
                                    )
                                ]
                            )

                        writeTable db table
                        setString (ofModel db.Model) seqProperty (string (key + 1))

                        let details = [
                            plural added "function added" "functions added"
                            if replaced > 0 then
                                plural replaced "existing function replaced" "existing functions replaced"
                            if not skipped.IsEmpty then
                                plural skipped.Length "existing function kept" "existing functions kept"
                        ]

                        {
                            Mutation.Effect.none with
                                Notes = [ $"Installed {m.Id} {m.Version} ({joined details})" ]
                                Warnings = [
                                    if not skipped.IsEmpty then
                                        "kept the model's own "
                                        + (List.rev skipped |> String.concat ", ")
                                        + $": {m.Id} {m.Version} may expect its own version of them"
                                    if not missing.IsEmpty then
                                        $"{m.Id} depends on "
                                        + (missing |> List.map (fun d -> $"{d.Id} {d.Version}") |> String.concat ", ")
                                        + ", which the model doesn't have"
                                ]
                        }))

/// Functions of an installed package changed since they were installed (names).
let modifiedFunctions (db: Database) (key: int) =
    functionsWithKey db key |> List.filter isModified |> List.map _.Name

/// Remove these functions as `sm remove` does: refused while other DAX calls them, unless `force`
/// (then those references are left broken). Returns the removal's warnings.
let private dropFunctions (db: Database) (fs: Function list) (force: bool) : Result<string list, SemctlError> =
    match fs with
    | [] -> Ok []
    | _ ->
        let selectors =
            fs
            |> List.map (fun f -> ObjectRef.format (ObjectRef.modelLevel ObjectKind.Function f.Name))

        RemoveObject.remove db {
            RemoveObject.RemoveSpec.ofSelectors selectors with
                Force = force
        }
        |> Result.map _.Warnings

/// Replace an installed package with another version of it (`pkg`). Functions the new version
/// drops are removed, new ones added; functions changed since they were installed are refused
/// unless OnModified says otherwise.
let update
    (db: Database)
    (pkg: Package)
    (onModified: OnModified)
    (onConflict: OnConflict)
    (force: bool)
    : Result<Mutation.Effect, SemctlError> =
    requireLevel db
    |> Result.bind (fun () -> state db)
    |> Result.bind (fun st ->
        let m = pkg.Manifest

        match findInstalled st m.Id with
        | None -> Error(notInstalled st m.Id)
        | Some installed when Version.same installed.Version m.Version ->
            Ok {
                Mutation.Effect.none with
                    Notes = [ $"{m.Id} {m.Version} is installed already" ]
            }
        | Some installed ->
            let key = installed.Key
            let old = functionsWithKey db key
            let modified = old |> List.filter isModified |> List.map _.Name

            let incoming = pkg.Functions |> List.map _.Name |> Set.ofList

            let taken =
                pkg.Functions
                |> List.filter (fun f ->
                    match db.Model.Functions.Find f.Name with
                    | null -> false
                    | existing -> functionHandle existing |> Option.bind parseInt <> Some key)
                |> List.map _.Name

            match onModified, modified, onConflict, taken with
            | RefuseModified, _ :: _, _, _ ->
                Error(
                    PackageConflict(
                        m.Id,
                        "these functions were changed since they were installed; pass --discard-changes to overwrite them or --keep-changes to leave them as they are",
                        modified
                    )
                )
            | _, _, RefuseConflicts, _ :: _ ->
                Error(
                    PackageConflict(
                        m.Id,
                        $"version {m.Version} adds functions the model already has; pass --overwrite to replace them or --skip-existing to keep them",
                        taken
                    )
                )
            | _ ->
                readTable db
                |> Result.bind (fun table ->
                    let keep (f: Function) =
                        onModified = KeepModified && List.contains f.Name modified

                    let mutable updated = 0
                    let mutable added = 0
                    let kept = ResizeArray<string>()

                    for source in pkg.Functions do
                        match db.Model.Functions.Find source.Name with
                        | null ->
                            let f = copyOf source
                            db.Model.Functions.Add f
                            link f key
                            added <- added + 1
                        | existing when functionHandle existing |> Option.bind parseInt = Some key ->
                            if keep existing then
                                kept.Add existing.Name
                            else
                                overwrite existing source
                                link existing key
                                updated <- updated + 1
                        | existing when onConflict = OverwriteConflicts ->
                            overwrite existing source
                            link existing key
                            added <- added + 1
                        | _ -> ()

                    // Dropped by the new version: removed after the rest is updated, so only DAX
                    // outside the package (or your changes) can still call them.
                    let dropped = old |> List.filter (fun f -> not (incoming.Contains f.Name))

                    for f in dropped do
                        if keep f then
                            unlink f
                            kept.Add f.Name

                    let removing = dropped |> List.filter (fun f -> not (keep f))

                    dropFunctions db removing force
                    |> Result.map (fun removalWarnings ->
                        match table.[string key] with
                        | :? JsonObject as entry -> entry.["Version"] <- JsonValue.Create m.Version
                        | _ -> ()

                        writeTable db table

                        let details = [
                            plural updated "function updated" "functions updated"
                            if added > 0 then
                                plural added "added" "added"
                            if not removing.IsEmpty then
                                plural removing.Length "removed" "removed"
                        ]

                        {
                            Mutation.Effect.none with
                                Notes = [ $"Updated {m.Id} {installed.Version} -> {m.Version} ({joined details})" ]
                                Warnings = [
                                    if kept.Count > 0 then
                                        "kept your changes to "
                                        + String.concat ", " kept
                                        + $": they may not work with {m.Id} {m.Version}"
                                    yield! removalWarnings
                                ]
                        })))

/// Remove an installed package and its functions. Functions changed since they were installed are
/// refused unless OnModified says otherwise (KeepModified leaves them in the model, no longer
/// linked to the package).
let remove (db: Database) (id: string) (onModified: OnModified) (force: bool) : Result<Mutation.Effect, SemctlError> =
    state db
    |> Result.bind (fun st ->
        match findInstalled st id with
        | None -> Error(notInstalled st id)
        | Some installed ->
            let fs = functionsWithKey db installed.Key
            let modified = fs |> List.filter isModified |> List.map _.Name

            match onModified, modified with
            | RefuseModified, _ :: _ ->
                Error(
                    PackageConflict(
                        installed.PackageId,
                        "these functions were changed since they were installed; pass --discard-changes to remove them anyway or --keep-changes to keep them (unlinked from the package)",
                        modified
                    )
                )
            | _ ->
                readTable db
                |> Result.bind (fun table ->
                    let keep (f: Function) =
                        onModified = KeepModified && List.contains f.Name modified

                    let kept = fs |> List.filter keep
                    let removing = fs |> List.filter (fun f -> not (keep f))

                    dropFunctions db removing force
                    |> Result.map (fun removalWarnings ->
                        for f in kept do
                            unlink f

                        table.Remove(string installed.Key) |> ignore
                        writeTable db table

                        {
                            Mutation.Effect.none with
                                Notes = [
                                    $"Removed {installed.PackageId} {installed.Version} ({functionCount removing.Length})"
                                ]
                                Warnings = [
                                    if not kept.IsEmpty then
                                        "kept "
                                        + (kept |> List.map _.Name |> String.concat ", ")
                                        + " (changed since installed), no longer part of a package"
                                    yield! removalWarnings
                                ]
                        })))
