/// Which visuals of the PBIR reports next to a model use a field: what `remove` (and `bpa --fix`
/// Delete(), `mcp apply_changes` remove) checks before deleting a table, column, measure,
/// hierarchy or level a report shows.
///
/// A use is found exactly as `rename` finds what to rewrite (Report: the semantic query
/// expressions that name the field structurally), so the two always agree. Report-level measures
/// (reportExtensions.json) and PBIR-Legacy report.json files are matched by name, since tess can't
/// read their references structurally: removing what they merely mention is refused too.
module Tessera.ReportUsage

open System
open System.IO
open System.Text
open System.Text.Json

/// One report file that uses a field.
type Usage = {
    /// The report folder's name (X.Report).
    Report: string
    /// The file, relative to the folder holding the report (as `rename` shows it).
    File: string
    /// The page's display name, for a file inside definition/pages/<page>/.
    Page: string option
    /// The visual's folder name (its id), for definition/pages/<page>/visuals/<visual>/visual.json.
    Visual: string option
    /// The visual's type (barChart, card, ...), when the file says.
    VisualType: string option
    /// How many references to the field the file has (0: it only mentions the name).
    References: int
}

/// The report changes whose matches are exactly the field's uses: a rename to a name nothing has.
let private probe (r: ObjectRef) =
    let other (name: string) = name + "\u0001"

    match r.Kind, r.Path with
    | ObjectKind.Table, [ t ] -> Some(Report.RenameTable(t, other t))
    | ObjectKind.Column, [ t; c ] -> Some(Report.RenameColumn(t, c, other c))
    | ObjectKind.Measure, [ t; m ] -> Some(Report.RenameMeasure(t, m, other m))
    | ObjectKind.Hierarchy, [ t; h ] -> Some(Report.RenameHierarchy(t, h, other h))
    | ObjectKind.Level, [ t; h; l ] -> Some(Report.RenameLevel(t, h, l, other l))
    | _ -> None

let private body (bytes: byte array) =
    if Report.startsWithBom bytes then bytes.[3..] else bytes

/// A string property of a JSON file, by path (None if the file or property isn't there).
let private jsonString (read: string -> byte array) (file: string) (path: string list) =
    try
        if not (File.Exists file) then
            None
        else
            use doc = JsonDocument.Parse(ReadOnlyMemory(body (read file)))

            let rec walk (e: JsonElement) =
                function
                | [] when e.ValueKind = JsonValueKind.String -> Some(e.GetString())
                | [] -> None
                | name :: rest ->
                    match e.ValueKind with
                    | JsonValueKind.Object ->
                        match e.TryGetProperty(name: string) with
                        | true, v -> walk v rest
                        | _ -> None
                    | _ -> None

            walk doc.RootElement path
    with _ ->
        None

/// Where in the report a file is: its page and visual, from definition/pages/<p>/visuals/<v>/...
let private locate (read: string -> byte array) (reportDir: string) (file: string) =
    let definition = Path.Combine(reportDir, Pbip.definitionFolderName)
    let relative = Path.GetRelativePath(definition, file).Replace('\\', '/')

    match relative.Split '/' |> List.ofArray with
    | "pages" :: page :: rest when rest <> [] ->
        let pageFolder = Path.Combine(definition, "pages", page)

        let pageName =
            jsonString read (Path.Combine(pageFolder, "page.json")) [ "displayName" ]
            |> Option.defaultValue page

        match rest with
        | "visuals" :: visual :: _ ->
            let visualFile = Path.Combine(pageFolder, "visuals", visual, "visual.json")
            Some pageName, Some visual, jsonString read visualFile [ "visual"; "visualType" ]
        | _ -> Some pageName, None, None
    | _ -> None, None, None

let private reportName (reportDir: string) =
    Path.GetFileName(reportDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))

let private mentions (read: string -> byte array) (name: string) (file: string) =
    try
        Encoding.UTF8.GetString(body (read file)).Contains(name, StringComparison.OrdinalIgnoreCase)
    with _ ->
        false

/// The report files (of these report folders) that use the object, reading files through `read`.
let usagesWith (read: string -> byte array) (reportDirs: string list) (r: ObjectRef) : Usage list =
    match probe r with
    | None -> []
    | Some change ->
        let name = Report.Change.oldName change

        [
            for dir in reportDirs do
                let definition = Path.Combine(dir, Pbip.definitionFolderName)
                let legacy = Path.Combine(dir, "report.json")

                let usage file references =
                    let page, visual, visualType = locate read dir file

                    {
                        Report = reportName dir
                        File = Report.displayOf dir file
                        Page = page
                        Visual = visual
                        VisualType = visualType
                        References = references
                    }

                if Directory.Exists definition then
                    let files =
                        Directory.GetFiles(definition, "*.json", SearchOption.AllDirectories)
                        |> Array.sort

                    for file in files do
                        if Path.GetFileName file = "reportExtensions.json" then
                            if mentions read name file then
                                yield usage file 0
                        else
                            try
                                let _, _, n, notRewritten = Report.rewriteBytes change (read file)

                                if n + notRewritten > 0 then
                                    yield usage file (n + notRewritten)
                            with :? JsonException ->
                                // Not readable as JSON: a mention is taken as a use.
                                if mentions read name file then
                                    yield usage file 0
                elif File.Exists legacy && mentions read name legacy then
                    yield usage legacy 0
        ]

/// The usages, reading the files from disk.
let usages (reportDirs: string list) (r: ObjectRef) =
    usagesWith File.ReadAllBytes reportDirs r

/// One line for a usage: "Sales.Report: page 'Overview', visual 3f2a (barChart)  (2 references)".
let describe (u: Usage) =
    let where =
        match u.Page, u.Visual with
        | Some page, Some visual ->
            let kind = u.VisualType |> Option.map (fun t -> $" ({t})") |> Option.defaultValue ""

            $"page '{page}', visual {visual}{kind}"
        | Some page, None -> $"page '{page}' ({u.File})"
        | None, _ -> u.File

    let count =
        match u.References with
        | 0 -> "mentions it by name"
        | 1 -> "1 reference"
        | n -> $"{n} references"

    $"{u.Report}: {where}  ({count})"

/// A check for RemoveObject.removeWith: for the objects about to be removed, the report usages
/// that block it (described), each line naming the object.
let checkWith (read: string -> byte array) (reportDirs: string list) : ObjectRef list -> string list =
    fun refs ->
        match reportDirs with
        | [] -> []
        | dirs -> [
            for r in refs do
                for u in usagesWith read dirs r do
                    $"{ObjectRef.format r} <- {describe u}"
          ]
