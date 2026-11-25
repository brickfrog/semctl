/// Where DAX packages come from: a local folder or file (offline; the default and the only kind
/// the tests use), or daxlib.org's API, reached only through the `get` function the caller passes
/// (the CLI passes PkgFetch.get; nothing here touches the network).
module Semctl.PkgSource

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Semctl.Prelude
open Semctl.Pkg

/// A package as a source lists it: its latest version (or the version found) and what it says
/// about itself.
type Summary = {
    Id: string
    Version: string
    Description: string
    Authors: string list
    Tags: string list
    /// Online only.
    Published: string option
    /// Online only.
    Downloads: int64 option
}

type Source = {
    /// For messages: the folder or file, or "daxlib.org (https://api.daxlib.org)".
    Name: string
    IsOnline: bool
    /// Packages whose ID, description or tags contain the text (case-insensitive; "" lists all),
    /// with their latest version (a pre-release one only with `prerelease`).
    Search: string -> bool -> Result<Summary list, SemctlError>
    /// The versions of a package, newest first (PackageNotFound when the source has none).
    Versions: string -> Result<string list, SemctlError>
    /// One version of a package, read.
    Get: string -> string -> Result<Package, SemctlError>
    /// Package folders or files the source skipped because they can't be read (path, reason).
    Skipped: (string * string) list
}

let private contains (text: string) (s: string) =
    text = "" || s.Contains(text, StringComparison.OrdinalIgnoreCase)

let private matches (text: string) (id: string) (description: string) (tags: string list) =
    let text = text.Trim()

    contains text id
    || contains text description
    || tags |> List.exists (contains text)

let private summaryOf (m: Manifest) = {
    Id = m.Id
    Version = m.Version
    Description = m.Description
    Authors = m.Authors
    Tags = m.Tags
    Published = None
    Downloads = None
}

// ---- local -------------------------------------------------------------------------------------

/// How deep a folder is searched for packages: enough for a daxlib checkout
/// (<checkout>/packages/<letter>/<id>/<version>/manifest.daxlib).
let private maxDepth = 6

type private LocalEntry = {
    Manifest: Manifest
    Path: string
    /// For a package inside a zip: its folder in the archive ("" for a .daxpkg).
    ZipPrefix: string option
}

let private isZip (path: string) =
    let ext = Path.GetExtension path

    ext.Equals(".daxpkg", StringComparison.OrdinalIgnoreCase)
    || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)

/// How an entry is named in messages: the path, and the folder in the archive.
let private display (e: LocalEntry) =
    match e.ZipPrefix with
    | Some p when p <> "" -> $"{e.Path}!/{p.TrimEnd('/')}"
    | _ -> e.Path

/// The packages in a .daxpkg or .zip file (one at its root, or one per folder holding a
/// manifest.daxlib: a zip of a daxlib checkout), and the ones that can't be read.
let private zipEntries (path: string) : LocalEntry list * (string * string) list =
    let bytes =
        try
            if FileInfo(path).Length > maxArchiveBytes then
                Error(InvalidPackage(path, "the file is too large"))
            else
                Ok(File.ReadAllBytes path)
        with e ->
            Error(InvalidPackage(path, e.Message))

    match
        bytes
        |> Result.bind (fun b -> zipPackages path b |> Result.map (fun ps -> b, ps))
    with
    | Error e -> [], [ path, SemctlError.message e ]
    | Ok(_, []) -> [], [ path, "no manifest.daxlib in it" ]
    | Ok(b, prefixes) ->
        let results = [
            for prefix in prefixes ->
                let label = if prefix = "" then path else $"{path}!{prefix}"

                match readZipManifestAt label b prefix with
                | Ok m ->
                    Ok {
                        Manifest = m
                        Path = path
                        ZipPrefix = Some prefix
                    }
                | Error e -> Error(label, SemctlError.message e)
        ]

        (results
         |> List.choose (function
             | Ok e -> Some e
             | Error _ -> None)),
        (results
         |> List.choose (function
             | Error x -> Some x
             | Ok _ -> None))

/// Every package under a folder: folders holding a manifest.daxlib, and .daxpkg and .zip files.
/// Hidden folders (.git) are not searched.
let private scan (root: string) =
    let found = ResizeArray<LocalEntry>()
    let skipped = ResizeArray<string * string>()

    let rec walk (dir: string) (depth: int) =
        if File.Exists(Path.Combine(dir, "manifest.daxlib")) then
            match readFolderManifest dir with
            | Ok m ->
                found.Add {
                    Manifest = m
                    Path = dir
                    ZipPrefix = None
                }
            | Error e -> skipped.Add(dir, SemctlError.message e)
        else
            let files, dirs =
                try
                    Directory.GetFiles dir |> Array.sort, Directory.GetDirectories dir |> Array.sort
                with
                | :? IOException
                | :? UnauthorizedAccessException -> [||], [||]

            for f in files do
                if isZip f then
                    let es, sk = zipEntries f
                    found.AddRange es
                    skipped.AddRange sk

            if depth < maxDepth then
                for d in dirs do
                    if not ((Path.GetFileName d).StartsWith '.') then
                        walk d (depth + 1)

    walk root 0
    List.ofSeq found, List.ofSeq skipped

/// A local source: a package folder, a .daxpkg file, a zip of packages, or a folder of any of
/// them (a daxlib checkout, a folder of downloads).
let local (path: string) : Result<Source, SemctlError> =
    let full = Path.GetFullPath path

    let entries, skipped =
        if File.Exists full then
            if isZip full then
                zipEntries full
            else
                [], [ full, "not a .daxpkg or .zip file" ]
        elif Directory.Exists full then
            scan full
        else
            [], []

    if not (File.Exists full || Directory.Exists full) then
        Error(PathNotFound path)
    elif entries.IsEmpty then
        let reason =
            match skipped with
            | [] -> "no packages found (a package is a folder with a manifest.daxlib, or a .daxpkg file)"
            | (p, r) :: _ -> $"no readable packages ({p}: {r})"

        Error(PackageSourceUnreadable(path, reason))
    else
        let byId =
            entries
            |> List.groupBy (fun e -> e.Manifest.Id.ToLowerInvariant())
            |> Map.ofList

        let versionsOf (id: string) =
            match Map.tryFind (id.ToLowerInvariant()) byId with
            | None -> []
            | Some es ->
                es
                |> List.distinctBy (fun e -> e.Manifest.Version.ToLowerInvariant())
                |> List.sortWith (fun a b -> Version.compareStrings b.Manifest.Version a.Manifest.Version)

        let ids = entries |> List.map _.Manifest.Id |> List.distinct

        let notFound (id: string) =
            PackageNotFound(id, path, Suggest.closest ids id)

        Ok {
            Name = path
            IsOnline = false
            Search =
                fun text prerelease ->
                    Ok [
                        for id in ids |> List.sortBy _.ToLowerInvariant() do
                            let vs = versionsOf id
                            let versions = vs |> List.map _.Manifest.Version

                            match Version.latest prerelease versions with
                            | Some latest ->
                                let e = vs |> List.find (fun e -> e.Manifest.Version = latest)

                                if matches text e.Manifest.Id e.Manifest.Description e.Manifest.Tags then
                                    yield summaryOf e.Manifest
                            | None -> ()
                    ]
            Versions =
                fun id ->
                    match versionsOf id with
                    | [] -> Error(notFound id)
                    | vs -> Ok(vs |> List.map _.Manifest.Version)
            Get =
                fun id version ->
                    match versionsOf id with
                    | [] -> Error(notFound id)
                    | vs ->
                        match vs |> List.tryFind (fun e -> Version.same e.Manifest.Version version) with
                        | None -> Error(PackageVersionNotFound(id, version, vs |> List.map _.Manifest.Version))
                        | Some e ->
                            match e.ZipPrefix with
                            | Some prefix ->
                                try
                                    readZipAt (display e) (File.ReadAllBytes e.Path) prefix
                                with ex ->
                                    Error(InvalidPackage(display e, ex.Message))
                            | None -> readFolder e.Path
            Skipped = skipped
        }

// ---- daxlib.org --------------------------------------------------------------------------------

/// The one online source: daxlib.org's API. Nothing else is ever contacted.
let daxlibHost = "api.daxlib.org"

let daxlibBase = Uri("https://api.daxlib.org/")

let daxlibName = "daxlib.org (https://api.daxlib.org)"

/// An HTTPS GET of a URL on the daxlib host: the body, None for 404, or the reason it failed.
type Get = Uri -> Result<byte array option, string>

/// The URLs sm fetches (documented in the README; the same ones the daxlib SDK uses).
module Urls =
    let private segment (s: string) =
        Uri.EscapeDataString(s.ToLowerInvariant())

    let search (text: string) (skip: int) (take: int) (prerelease: bool) =
        let pre = if prerelease then "true" else "false"
        Uri(daxlibBase, $"v1/query-packages?q={Uri.EscapeDataString text}&skip={skip}&take={take}&prerelease={pre}")

    let versions (id: string) =
        Uri(daxlibBase, $"v1/package/{segment id}/versions.json")

    let package (id: string) (version: string) =
        Uri(daxlibBase, $"v1/package/{segment id}/{segment version}/{segment id}.{segment version}.daxpkg")

let private parseJson (url: Uri) (bytes: byte array) : Result<JsonNode, SemctlError> =
    try
        Ok(JsonNode.Parse(ReadOnlySpan bytes))
    with :? JsonException as e ->
        Error(PackageFetchFailed(string url, $"the answer isn't JSON: {e.Message}"))

let private textOf (node: JsonNode) =
    match node with
    | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
    | _ -> None

let private textsOf (node: JsonNode) =
    match node with
    | :? JsonArray as a -> [ for x in a -> textOf x ] |> List.choose (fun x -> x)
    | other ->
        textOf other
        |> Option.map (fun s ->
            s.Split(',')
            |> Array.map _.Trim()
            |> Array.filter (fun x -> x <> "")
            |> List.ofArray)
        |> Option.defaultValue []

/// The search results the API returns: {"data": [{"id", "version", "authors": [...], ...}]}.
let parseSearch (url: Uri) (bytes: byte array) : Result<Summary list, SemctlError> =
    parseJson url bytes
    |> Result.bind (fun node ->
        match node with
        | :? JsonObject as o ->
            match o.["data"] with
            | :? JsonArray as data ->
                Ok [
                    for item in data do
                        match item with
                        | :? JsonObject as p ->
                            match textOf p.["id"], textOf p.["version"] with
                            | Some id, Some version ->
                                yield {
                                    Id = id
                                    Version = version
                                    Description = textOf p.["description"] |> Option.defaultValue ""
                                    Authors = textsOf p.["authors"]
                                    Tags = textsOf p.["tags"]
                                    Published = textOf p.["published"]
                                    Downloads =
                                        match p.["downloads"] with
                                        | :? JsonValue as v when v.GetValueKind() = JsonValueKind.Number ->
                                            Some(v.GetValue<int64>())
                                        | _ -> None
                                }
                            | _ -> ()
                        | _ -> ()
                ]
            | _ -> Error(PackageFetchFailed(string url, "the answer has no data array"))
        | _ -> Error(PackageFetchFailed(string url, "the answer isn't a JSON object")))

/// The search API's page size, and how many pages a search reads at most.
let private pageSize = 100
let private maxPages = 20

/// daxlib.org's API, through `get`.
let daxlib (get: Get) : Source =
    let fetch (url: Uri) =
        get url
        |> Result.mapError (fun reason -> PackageFetchFailed(string url, reason))

    let versions (id: string) =
        if not (isValidId id) then
            Error(PackageNotFound(id, daxlibName, []))
        else
            let url = Urls.versions id

            result {
                let! raw = fetch url

                match raw with
                | None -> return! Error(PackageNotFound(id, daxlibName, []))
                | Some bytes ->
                    let! node = parseJson url bytes

                    match node with
                    | :? JsonObject as o ->
                        match o.["versions"] with
                        | :? JsonArray as a ->
                            match [ for x in a -> textOf x ] |> List.choose (fun x -> x) with
                            | [] -> return! Error(PackageNotFound(id, daxlibName, []))
                            | vs -> return vs |> List.sortWith (fun a b -> Version.compareStrings b a)
                        | _ -> return! Error(PackageFetchFailed(string url, "the answer has no versions array"))
                    | _ -> return! Error(PackageFetchFailed(string url, "the answer isn't a JSON object"))
            }

    {
        Name = daxlibName
        IsOnline = true
        Search =
            fun text prerelease ->
                let rec page (n: int) (accRev: Summary list) =
                    let url = Urls.search (text.Trim()) (n * pageSize) pageSize prerelease

                    result {
                        let! raw = fetch url

                        let! items =
                            match raw with
                            | None -> Error(PackageFetchFailed(string url, "HTTP 404"))
                            | Some bytes -> parseSearch url bytes

                        let accRev = List.rev items @ accRev

                        if items.Length < pageSize || n + 1 >= maxPages then
                            return List.rev accRev
                        else
                            return! page (n + 1) accRev
                    }

                page 0 []
        Versions = versions
        Get =
            fun id version ->
                result {
                    let! vs = versions id

                    match vs |> List.tryFind (Version.same version) with
                    | None -> return! Error(PackageVersionNotFound(id, version, vs))
                    | Some v ->
                        let url = Urls.package id v
                        let! raw = fetch url

                        match raw with
                        | None -> return! Error(PackageVersionNotFound(id, version, vs))
                        | Some bytes -> return! readZip (string url) bytes
                }
        Skipped = []
    }

// ---- choosing ----------------------------------------------------------------------------------

/// The value of --from (or SM_PKG_SOURCE) that names daxlib.org...
let daxlibToken = "daxlib"

/// ... and its unambiguous spelling, which is never a path.
let daxlibUrl = "https://api.daxlib.org"

/// The source to use: `--from` first, then SM_PKG_SOURCE. The online source only when one of
/// them says `daxlib` (or https://api.daxlib.org), and never with `offline`. `daxlib` while a file
/// or folder of that name is in the current directory is refused as ambiguous: a clone of the
/// daxlib repository is called that, and reading it must never turn into a network call. No source
/// named is an error: sm never goes online on its own.
let chooseWith
    (exists: string -> bool)
    (from: string option)
    (env: string option)
    (offline: bool)
    (get: Get)
    : Result<Source, SemctlError> =
    let named, where =
        match from, env with
        | Some f, _ -> Some f, "--from"
        | None, Some e -> Some e, "SM_PKG_SOURCE"
        | None, None -> None, ""

    let online () =
        if offline then
            Error(
                NoPackageSource
                    $"{where} names daxlib.org, and --offline forbids going online; pass --from <folder|file>"
            )
        else
            Ok(daxlib get)

    match named with
    | None ->
        Error(
            NoPackageSource
                "No package source: pass --from <folder|file> (a daxlib checkout, a package folder, a .daxpkg), or --from daxlib to use daxlib.org; or set SM_PKG_SOURCE"
        )
    | Some s when s.Trim().TrimEnd('/').Equals(daxlibUrl, StringComparison.OrdinalIgnoreCase) -> online ()
    | Some s when s.Trim().Equals(daxlibToken, StringComparison.OrdinalIgnoreCase) ->
        if exists s then
            Error(
                NoPackageSource
                    $"{where} '{s}' could be daxlib.org or the '{s}' here: pass ./{s} for the folder, or {daxlibUrl} for daxlib.org"
            )
        else
            online ()
    | Some path -> local path

/// `chooseWith`, looking in the current directory.
let choose = chooseWith (fun s -> File.Exists s || Directory.Exists s)

// ---- updating ----------------------------------------------------------------------------------

/// `pkg update`: move the named packages (ID, and a version or the latest) or, with none named,
/// every installed package to its latest version, from `src`. With every package, one the source
/// doesn't have is a warning; online, packages recorded under another provider are skipped with a
/// warning, so their IDs are never sent to daxlib.org.
let update
    (src: Source)
    (db: Microsoft.AnalysisServices.Tabular.Database)
    (specs: (string * string option) list)
    (prerelease: bool)
    (onModified: OnModified)
    (onConflict: OnConflict)
    (force: bool)
    : Result<Mutation.Effect, SemctlError> =
    state db
    |> Result.bind (fun st ->
        let isDaxlib (i: Installed) =
            String.Equals(i.Provider.TrimEnd('/'), daxlibProvider, StringComparison.OrdinalIgnoreCase)

        let targets, skipped =
            match specs with
            | [] ->
                let foreign =
                    st.Installed |> List.filter (fun i -> src.IsOnline && not (isDaxlib i))

                Ok(
                    st.Installed
                    |> List.filter (fun i -> not (List.contains i foreign))
                    |> List.map (fun i -> i, None)
                ),
                foreign
                |> List.map (fun i -> $"{i.PackageId} was installed from {i.Provider}, not daxlib.org; not updated")
            | _ ->
                let picked =
                    specs
                    |> List.map (fun (id, version) ->
                        match
                            st.Installed
                            |> List.tryFind (fun i ->
                                String.Equals(i.PackageId, id, StringComparison.OrdinalIgnoreCase))
                        with
                        | Some i -> Ok(i, version)
                        | None -> Error(PackageNotInstalled(id, st.Installed |> List.map _.PackageId)))

                match
                    picked
                    |> List.tryPick (function
                        | Error e -> Some e
                        | Ok _ -> None)
                with
                | Some e -> Error e, []
                | None ->
                    Ok(
                        picked
                        |> List.choose (function
                            | Ok x -> Some x
                            | Error _ -> None)
                    ),
                    []

        let rec go (acc: Mutation.Effect) targets =
            match targets with
            | [] -> Ok acc
            | ((i: Installed), version) :: rest ->
                let chosen =
                    match version with
                    | Some v -> src.Get i.PackageId v |> Result.map Some
                    | None ->
                        src.Versions i.PackageId
                        |> Result.bind (fun vs ->
                            match Version.latest prerelease vs with
                            | Some latest when Version.compareStrings latest i.Version > 0 ->
                                src.Get i.PackageId latest |> Result.map Some
                            | _ -> Ok None)

                let step =
                    match chosen with
                    | Error(PackageNotFound _) when specs.IsEmpty ->
                        Ok {
                            Mutation.Effect.none with
                                Warnings = [ $"{i.PackageId} isn't in {src.Name}; not updated" ]
                        }
                    | Error e -> Error e
                    | Ok None ->
                        Ok {
                            Mutation.Effect.none with
                                Notes = [ $"{i.PackageId} {i.Version} is the latest version" ]
                        }
                    | Ok(Some pkg) -> Pkg.update db pkg onModified onConflict force

                match step with
                | Error e -> Error e
                | Ok e ->
                    go
                        {
                            acc with
                                Warnings = acc.Warnings @ e.Warnings
                                Notes = acc.Notes @ e.Notes
                        }
                        rest

        targets
        |> Result.bind (fun targets ->
            let start = {
                Mutation.Effect.none with
                    Warnings = skipped
            }

            match targets, skipped with
            | [], [] ->
                Ok {
                    start with
                        Notes = [ "No packages installed" ]
                }
            | _ -> go start targets))
