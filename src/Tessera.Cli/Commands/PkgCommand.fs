/// `tess pkg`: DAX user-defined function packages from daxlib.org, installed the way Tabular
/// Editor 3's DAX Package Manager installs them (see Tessera.Pkg).
///
/// Offline first: packages come from a local folder or file (`--from <path>`, or TESS_PKG_SOURCE).
/// daxlib.org is contacted only when the source named is `daxlib`, never with --offline, and
/// never by `list` or `remove`, which read only the model.
module Tessera.Cli.PkgCommand

open System
open Argu
open Tessera
open Tessera.Cli.Common
open Tessera.Cli.Output

/// One-line description, shown in `tess --help`.
let summary =
    "DAX function packages (daxlib.org): list, search, show, install, update, remove (dry run unless --save)."

/// The environment variable naming the default package source (a path, or `daxlib`).
let sourceVariable = "TESS_PKG_SOURCE"

module private Usage =
    let from =
        "where packages come from: a daxlib checkout, a folder of packages or .daxpkg files, one package "
        + "(folder or .daxpkg), or `daxlib` for daxlib.org over HTTPS (default: TESS_PKG_SOURCE; none: an error)."

    let offline =
        "never go online: refuse `daxlib` as the source, even from TESS_PKG_SOURCE."

    let prerelease =
        "consider pre-release versions (1.0.0-beta) for the latest version."

    let overwrite =
        "replace model functions that have the same name as a package function."

    let skipExisting =
        "keep model functions that have the same name as a package function (the package's are skipped)."

    let discard =
        "overwrite (update) or remove (remove) package functions changed since they were installed."

    let keep =
        "leave package functions changed since they were installed as they are (remove: unlinked from the package)."

let private forceRemoving =
    "remove package functions that other DAX still calls, leaving those calls broken; save even if the change "
    + "introduces new errors; with --save-to, overwrite a non-empty destination."

type ListArgs =
    | [<MainCommand; ExactlyOnce>] Model of model: string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Model _ -> "the model."

type SearchArgs =
    | [<MainCommand>] Text of text: string
    | From of source: string
    | Offline
    | Prerelease

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Text _ -> "text to look for in package IDs, descriptions and tags (none: list every package)."
            | From _ -> Usage.from
            | Offline -> Usage.offline
            | Prerelease -> Usage.prerelease

type ShowArgs =
    | [<MainCommand; ExactlyOnce>] Package of package: string
    | From of source: string
    | Offline
    | Prerelease

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Package _ -> "the package ID, optionally with a version: DaxLib.SVG or DaxLib.SVG@1.0.0."
            | From _ -> Usage.from
            | Offline -> Usage.offline
            | Prerelease -> Usage.prerelease

type InstallArgs =
    | [<MainCommand; ExactlyOnce>] Target of model_and_packages: string list
    | From of source: string
    | Offline
    | Prerelease
    | Overwrite
    | Skip_Existing
    | Ignore_Dependencies
    | Save
    | Save_To of path: string
    | Force
    | Stat
    | Name_Only

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Target _ ->
                "the model, then one or more package IDs, each optionally with a version (DaxLib.SVG@1.0.0; "
                + "default: the latest)."
            | From _ -> Usage.from
            | Offline -> Usage.offline
            | Prerelease -> Usage.prerelease
            | Overwrite -> Usage.overwrite
            | Skip_Existing -> Usage.skipExisting
            | Ignore_Dependencies -> "install even if packages it depends on aren't installed."
            | Save -> Mutation.Usage.save
            | Save_To _ -> Mutation.Usage.saveTo
            | Force -> Mutation.Usage.force
            | Stat -> Mutation.Usage.stat
            | Name_Only -> Mutation.Usage.nameOnly

type UpdateArgs =
    | [<MainCommand; ExactlyOnce>] Target of model_and_packages: string list
    | From of source: string
    | Offline
    | Prerelease
    | Discard_Changes
    | Keep_Changes
    | Overwrite
    | Skip_Existing
    | Save
    | Save_To of path: string
    | Force
    | Stat
    | Name_Only

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Target _ ->
                "the model, then the packages to update, each optionally with a version (DaxLib.SVG@1.0.0, "
                + "which may be older; default: the latest); none: every installed package."
            | From _ -> Usage.from
            | Offline -> Usage.offline
            | Prerelease -> Usage.prerelease
            | Discard_Changes -> Usage.discard
            | Keep_Changes -> Usage.keep
            | Overwrite -> Usage.overwrite
            | Skip_Existing -> Usage.skipExisting
            | Save -> Mutation.Usage.save
            | Save_To _ -> Mutation.Usage.saveTo
            | Force -> forceRemoving
            | Stat -> Mutation.Usage.stat
            | Name_Only -> Mutation.Usage.nameOnly

type RemoveArgs =
    | [<MainCommand; ExactlyOnce>] Target of model_and_packages: string list
    | Discard_Changes
    | Keep_Changes
    | Save
    | Save_To of path: string
    | Force
    | Stat
    | Name_Only

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Target _ -> "the model, then one or more installed package IDs."
            | Discard_Changes -> Usage.discard
            | Keep_Changes -> Usage.keep
            | Save -> Mutation.Usage.save
            | Save_To _ -> Mutation.Usage.saveTo
            | Force -> forceRemoving
            | Stat -> Mutation.Usage.stat
            | Name_Only -> Mutation.Usage.nameOnly

type Args =
    | [<CliPrefix(CliPrefix.None)>] List of ParseResults<ListArgs>
    | [<CliPrefix(CliPrefix.None)>] Search of ParseResults<SearchArgs>
    | [<CliPrefix(CliPrefix.None)>] Show of ParseResults<ShowArgs>
    | [<CliPrefix(CliPrefix.None)>] Install of ParseResults<InstallArgs>
    | [<CliPrefix(CliPrefix.None)>] Update of ParseResults<UpdateArgs>
    | [<CliPrefix(CliPrefix.None)>] Remove of ParseResults<RemoveArgs>

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | List _ -> "the packages installed in a model (reads only the model)."
            | Search _ -> "find packages in the source."
            | Show _ -> "a package's details, versions and functions."
            | Install _ -> "install packages into a model as user-defined functions."
            | Update _ -> "move installed packages to another (default: the latest) version."
            | Remove _ -> "remove installed packages and their functions (reads only the model)."

// ---- helpers -----------------------------------------------------------------------------------

/// `Id` or `Id@version`.
let private parseSpec (spec: string) =
    match spec.LastIndexOf '@' with
    | i when i > 0 && i < spec.Length - 1 -> spec.Substring(0, i), Some(spec.Substring(i + 1))
    | _ -> spec, None

let private source (ctx: Ctx) (from: string option) (offline: bool) =
    PkgSource.choose from (ctx.GetEnv sourceVariable) offline PkgFetch.get
    |> Result.map (fun s ->
        for path, reason in s.Skipped do
            ctx.Err.WriteLine($"tess: skipped {path}: {reason}")

        s)

/// The version to use: the one asked for, else the latest (stable unless `prerelease`).
let private resolve (src: PkgSource.Source) (id: string) (version: string option) (prerelease: bool) =
    match version with
    | Some v -> src.Get id v
    | None ->
        src.Versions id
        |> Result.bind (fun versions ->
            match Pkg.Version.latest prerelease versions with
            | Some v -> src.Get id v
            | None -> Error(PackageVersionNotFound(id, "(latest stable)", versions)))

let private onConflict (overwrite: bool) (skip: bool) =
    match overwrite, skip with
    | true, true -> Error "--overwrite and --skip-existing can't be combined"
    | true, false -> Ok Pkg.OverwriteConflicts
    | false, true -> Ok Pkg.SkipConflicts
    | false, false -> Ok Pkg.RefuseConflicts

let private onModified (discard: bool) (keep: bool) =
    match discard, keep with
    | true, true -> Error "--discard-changes and --keep-changes can't be combined"
    | true, false -> Ok Pkg.DiscardModified
    | false, true -> Ok Pkg.KeepModified
    | false, false -> Ok Pkg.RefuseModified

/// Run several package edits as one change: they apply in order, and the first error stops all.
let private sequence
    (edits: (Microsoft.AnalysisServices.Tabular.Database -> Result<Tessera.Mutation.Effect, TessError>) list)
    db
    =
    let rec go (acc: Tessera.Mutation.Effect) edits =
        match edits with
        | [] -> Ok acc
        | e :: rest ->
            match e db with
            | Error err -> Error err
            | Ok(effect: Tessera.Mutation.Effect) ->
                go
                    {
                        acc with
                            Warnings = acc.Warnings @ effect.Warnings
                            Notes = acc.Notes @ effect.Notes
                    }
                    rest

    go Tessera.Mutation.Effect.none edits

let private joined (parts: string list) = String.concat ", " parts

let private stateText (s: Pkg.FunctionState) =
    match s with
    | Pkg.Unchanged -> "unchanged"
    | Pkg.Modified -> "changed"
    | Pkg.Unknown -> "unknown"

let private functionRef (name: string) =
    ObjectRef.format (ObjectRef.modelLevel ObjectKind.Function name)

// ---- list --------------------------------------------------------------------------------------

let private list (ctx: Ctx) (args: ParseResults<ListArgs>) =
    match
        ModelIO.loadPath (args.GetResult ListArgs.Model)
        |> Result.bind (fun (_, db) -> Pkg.state db)
    with
    | Error e -> Failed e
    | Ok st ->
        if ctx.Json then
            writeJson
                ctx.Out
                (obj [
                    "packages",
                    arr [
                        for i in st.Installed ->
                            obj [
                                "id", str i.PackageId
                                "version", str i.Version
                                "provider", str i.Provider
                                "key", num i.Key
                                "functions",
                                arr [
                                    for f in i.Functions ->
                                        obj [
                                            "name", str f.Name
                                            "ref", str (functionRef f.Name)
                                            "state", str (stateText f.State)
                                        ]
                                ]
                            ]
                    ]
                    "orphans",
                    arr [
                        for name, key in st.Orphans ->
                            obj [ "name", str name; "ref", str (functionRef name); "key", str key ]
                    ]
                ])
        else
            if st.Installed.IsEmpty then
                ctx.Out.WriteLine("No packages installed.")

            for i in st.Installed do
                let n = i.Functions.Length

                let changed =
                    i.Functions |> List.filter (fun f -> f.State = Pkg.Modified) |> List.map _.Name

                let functions = if n = 1 then "1 function" else $"{n} functions"

                let changedText =
                    match changed with
                    | [] -> ""
                    | names -> $"; changed since installed: {joined names}"

                ctx.Out.WriteLine($"{i.PackageId} {i.Version}  ({functions}{changedText})  {i.Provider}")

            for name, key in st.Orphans do
                ctx.Err.WriteLine(
                    $"tess: {functionRef name} names package key {key}, which the model's package table doesn't have"
                )

        Done

// ---- search and show ---------------------------------------------------------------------------

let private summaryJson (s: PkgSource.Summary) =
    obj [
        "id", str s.Id
        "version", str s.Version
        "description", str s.Description
        "authors", arr (s.Authors |> List.map str)
        "tags", arr (s.Tags |> List.map str)
        "published", optStr s.Published
        "downloads",
        (match s.Downloads with
         | Some d -> Text.Json.Nodes.JsonValue.Create d :> Text.Json.Nodes.JsonNode
         | None -> null)
    ]

let private search (ctx: Ctx) (args: ParseResults<SearchArgs>) =
    match source ctx (args.TryGetResult SearchArgs.From) (args.Contains SearchArgs.Offline) with
    | Error e -> Failed e
    | Ok src ->
        let text = args.TryGetResult SearchArgs.Text |> Option.defaultValue ""

        match src.Search text (args.Contains SearchArgs.Prerelease) with
        | Error e -> Failed e
        | Ok found ->
            if ctx.Json then
                writeJson ctx.Out (obj [ "source", str src.Name; "packages", arr (found |> List.map summaryJson) ])
            else if found.IsEmpty then
                ctx.Out.WriteLine("No packages found.")
            else
                let width =
                    found |> List.map (fun s -> s.Id.Length + 1 + s.Version.Length) |> List.max

                for s in found do
                    let name = $"{s.Id} {s.Version}"
                    ctx.Out.WriteLine($"{name.PadRight width}  {s.Description}")

            Done

let private show (ctx: Ctx) (args: ParseResults<ShowArgs>) =
    match source ctx (args.TryGetResult ShowArgs.From) (args.Contains ShowArgs.Offline) with
    | Error e -> Failed e
    | Ok src ->
        let id, version = parseSpec (args.GetResult ShowArgs.Package)

        match src.Versions id with
        | Error e -> Failed e
        | Ok versions ->
            match resolve src id version (args.Contains ShowArgs.Prerelease) with
            | Error e -> Failed e
            | Ok pkg ->
                let m = pkg.Manifest

                if ctx.Json then
                    writeJson
                        ctx.Out
                        (obj [
                            "id", str m.Id
                            "version", str m.Version
                            "description", str m.Description
                            "authors", arr (m.Authors |> List.map str)
                            "tags", arr (m.Tags |> List.map str)
                            "releaseNotes", optStr m.ReleaseNotes
                            "projectUrl", optStr m.ProjectUrl
                            "repositoryUrl", optStr m.RepositoryUrl
                            "dependencies",
                            arr [ for d in m.Dependencies -> obj [ "id", str d.Id; "version", str d.Version ] ]
                            "versions", arr (versions |> List.map str)
                            "functions",
                            arr [
                                for f in pkg.Functions ->
                                    obj [
                                        "name", str f.Name
                                        "description",
                                        (if String.IsNullOrEmpty f.Description then
                                             null
                                         else
                                             str f.Description)
                                    ]
                            ]
                            "source", str src.Name
                            "origin", str pkg.Origin
                        ])
                else
                    let w = ctx.Out
                    w.WriteLine($"{m.Id} {m.Version}")

                    if m.Description <> "" then
                        w.WriteLine($"  {m.Description}")

                    if not m.Authors.IsEmpty then
                        w.WriteLine($"  authors: {joined m.Authors}")

                    if not m.Tags.IsEmpty then
                        w.WriteLine($"  tags: {joined m.Tags}")

                    m.ProjectUrl |> Option.iter (fun u -> w.WriteLine($"  project: {u}"))
                    m.RepositoryUrl |> Option.iter (fun u -> w.WriteLine($"  repository: {u}"))
                    m.ReleaseNotes |> Option.iter (fun n -> w.WriteLine($"  release notes: {n}"))

                    for d in m.Dependencies do
                        w.WriteLine($"  depends on: {d.Id} {d.Version}")

                    w.WriteLine($"  versions: {joined versions}")
                    w.WriteLine($"  functions ({pkg.Functions.Length}):")

                    for f in pkg.Functions do
                        w.WriteLine($"    {f.Name}")

                Done

// ---- install, update, remove -------------------------------------------------------------------

let private options save saveTo force stat nameOnly =
    Mutation.options save saveTo force stat nameOnly

let private install (ctx: Ctx) (args: ParseResults<InstallArgs>) =
    let opts =
        options
            (args.Contains InstallArgs.Save)
            (args.TryGetResult InstallArgs.Save_To)
            (args.Contains InstallArgs.Force)
            (args.Contains InstallArgs.Stat)
            (args.Contains InstallArgs.Name_Only)

    let conflict =
        onConflict (args.Contains InstallArgs.Overwrite) (args.Contains InstallArgs.Skip_Existing)

    match args.GetResult InstallArgs.Target, opts, conflict with
    | [], _, _
    | [ _ ], _, _ -> UsageError "expected the model and at least one package"
    | _, Error message, _
    | _, _, Error message -> UsageError message
    | model :: specs, Ok opts, Ok conflict ->
        match source ctx (args.TryGetResult InstallArgs.From) (args.Contains InstallArgs.Offline) with
        | Error e -> Failed e
        | Ok src ->
            let prerelease = args.Contains InstallArgs.Prerelease

            // Every package is read before the model is touched.
            let packages =
                specs
                |> List.map (fun spec ->
                    let id, version = parseSpec spec
                    resolve src id version prerelease)

            match
                packages
                |> List.tryPick (function
                    | Error e -> Some e
                    | Ok _ -> None)
            with
            | Some e -> Failed e
            | None ->
                let installOpts: Pkg.InstallOptions = {
                    OnConflict = conflict
                    IgnoreDependencies = args.Contains InstallArgs.Ignore_Dependencies
                    Provider = Pkg.daxlibProvider
                }

                let edits =
                    packages
                    |> List.choose (function
                        | Ok p -> Some p
                        | Error _ -> None)
                    |> List.map (fun p db -> Pkg.install db p installOpts)

                Mutation.run ctx "pkg install" opts model (sequence edits)

let private update (ctx: Ctx) (args: ParseResults<UpdateArgs>) =
    let opts =
        options
            (args.Contains UpdateArgs.Save)
            (args.TryGetResult UpdateArgs.Save_To)
            (args.Contains UpdateArgs.Force)
            (args.Contains UpdateArgs.Stat)
            (args.Contains UpdateArgs.Name_Only)

    let conflict =
        onConflict (args.Contains UpdateArgs.Overwrite) (args.Contains UpdateArgs.Skip_Existing)

    let modified =
        onModified (args.Contains UpdateArgs.Discard_Changes) (args.Contains UpdateArgs.Keep_Changes)

    match args.GetResult UpdateArgs.Target, opts, conflict, modified with
    | [], _, _, _ -> UsageError "expected the model, and optionally packages to update"
    | _, Error message, _, _
    | _, _, Error message, _
    | _, _, _, Error message -> UsageError message
    | model :: specs, Ok opts, Ok conflict, Ok modified ->
        match source ctx (args.TryGetResult UpdateArgs.From) (args.Contains UpdateArgs.Offline) with
        | Error e -> Failed e
        | Ok src ->
            let prerelease = args.Contains UpdateArgs.Prerelease

            let edit (db: Microsoft.AnalysisServices.Tabular.Database) =
                PkgSource.update src db (specs |> List.map parseSpec) prerelease modified conflict opts.Force

            Mutation.run ctx "pkg update" opts model edit

let private remove (ctx: Ctx) (args: ParseResults<RemoveArgs>) =
    let opts =
        options
            (args.Contains RemoveArgs.Save)
            (args.TryGetResult RemoveArgs.Save_To)
            (args.Contains RemoveArgs.Force)
            (args.Contains RemoveArgs.Stat)
            (args.Contains RemoveArgs.Name_Only)

    let modified =
        onModified (args.Contains RemoveArgs.Discard_Changes) (args.Contains RemoveArgs.Keep_Changes)

    match args.GetResult RemoveArgs.Target, opts, modified with
    | [], _, _
    | [ _ ], _, _ -> UsageError "expected the model and at least one package"
    | _, Error message, _
    | _, _, Error message -> UsageError message
    | model :: ids, Ok opts, Ok modified ->
        let edits =
            ids
            |> List.map (fun id db -> Pkg.remove db (fst (parseSpec id)) modified opts.Force)

        Mutation.run ctx "pkg remove" opts model (sequence edits)

let run (ctx: Ctx) (args: ParseResults<Args>) =
    match args.TryGetSubCommand() with
    | Some(List a) -> list ctx a
    | Some(Search a) -> search ctx a
    | Some(Show a) -> show ctx a
    | Some(Install a) -> install ctx a
    | Some(Update a) -> update ctx a
    | Some(Remove a) -> remove ctx a
    | None -> UsageError(args.Parser.PrintUsage())
