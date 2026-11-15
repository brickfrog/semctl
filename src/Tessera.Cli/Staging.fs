/// A model held in memory with staged, unsaved edits: what `tess mcp` serves and `tess interactive`
/// edits. One shape for both, so they agree on what "unsaved" means, on the save gate and on
/// report files:
///   - every edit is staged whole (a batch, a command): the model is replaced by the edited copy,
///     and the model as it was goes on an undo stack;
///   - report files that a staged rename or move rewrites are staged too (an overlay of path ->
///     text), so later edits plan against the staged text and several renames compose; nothing is
///     written until `save`, which writes the model first, then the report files;
///   - the save gate: a save is refused while the model has gated errors (Mutation.gatedErrors) it
///     didn't have when it was loaded or last saved, unless forced. Errors of renamed objects are
///     matched under the names they had then.
///
/// Reads never get the staged model itself: `tess interactive` hands each command a copy
/// (`ModelIO.Override`), and an edit replaces the staged model rather than changing it, so a
/// snapshot on the undo stack stays as it was.
module Tessera.Cli.Staging

open System
open System.IO
open System.Text
open Microsoft.AnalysisServices.Tabular
open Tessera

/// A report file with staged edits.
type StagedFile = {
    Path: string
    Display: string
    /// The file's text on disk when it was first staged (the save refuses if the disk changed since).
    Original: string
    Current: string
    Bom: bool
    References: int
}

/// A staged model as it was before an edit (for undo).
type Snapshot = {
    Db: Database
    Origins: Map<ObjectRef, ObjectRef>
    Unsaved: int
    Files: Map<string, StagedFile>
}

type StagedModel = {
    Source: ModelSource
    /// The model as staged. Replaced (never changed in place) by each staged edit.
    mutable Db: Database
    /// The model as it was loaded or last saved.
    mutable Saved: Database
    /// For each object renamed since the load or save: its current ref -> its ref then.
    mutable Origins: Map<ObjectRef, ObjectRef>
    /// The gated errors the model had when it was loaded (or last saved), keyed by their refs then.
    mutable Baseline: Set<string * ObjectRef * string>
    /// Objects changed by staged edits since the load or save.
    mutable Unsaved: int
    /// Staged report files, by full path.
    mutable Files: Map<string, StagedFile>
    /// The model before each staged edit, latest first.
    mutable History: Snapshot list
}

let private keyOf (origins: Map<ObjectRef, ObjectRef>) (i: Validate.Issue) =
    i.Rule, Map.tryFind i.Ref origins |> Option.defaultValue i.Ref, i.Message

let private baselineOf (db: Database) =
    Mutation.gatedErrors db |> List.map (keyOf Map.empty) |> Set.ofList

let private fullPath (p: string) = Path.GetFullPath p

let private bomBytes = [| 0xEFuy; 0xBBuy; 0xBFuy |]

let private bytesOf (f: StagedFile) =
    let body = Encoding.UTF8.GetBytes f.Current
    if f.Bom then Array.append bomBytes body else body

/// The report folders next to a model (PBIR reports whose definition.pbir points at it).
let reportsOf (source: ModelSource) =
    source.SemanticModelFolder
    |> Option.map Report.reportsOf
    |> Option.defaultValue []

/// A check for RemoveObject.removeWith that reads report files from disk.
let diskReportCheck (source: ModelSource) =
    ReportUsage.checkWith File.ReadAllBytes (reportsOf source)

module StagedModel =
    let create (source: ModelSource) (db: Database) = {
        Source = source
        Db = db
        Saved = db
        Origins = Map.empty
        Baseline = baselineOf db
        Unsaved = 0
        Files = Map.empty
        History = []
    }

    /// The gated errors present now that weren't when the model was loaded (or last saved).
    let introducedSinceLoad (m: StagedModel) =
        Mutation.gatedErrors m.Db
        |> List.filter (fun i -> not (m.Baseline.Contains(keyOf m.Origins i)))

    /// Staged edits that aren't saved.
    let isDirty (m: StagedModel) = m.Unsaved > 0 || not m.Files.IsEmpty

    /// Read a file as staged: a staged report file's text, otherwise the file on disk.
    let read (m: StagedModel) (path: string) : byte array =
        match Map.tryFind (fullPath path) m.Files with
        | Some f -> bytesOf f
        | None -> File.ReadAllBytes path

    /// A check for RemoveObject.removeWith over the report files as staged.
    let reportCheck (m: StagedModel) =
        ReportUsage.checkWith (read m) (reportsOf m.Source)

    /// Staged report files merged with newer edits (planned against the staged text).
    let mergeFiles (files: Map<string, StagedFile>) (edits: Report.FileEdit list) =
        edits
        |> List.fold
            (fun (acc: Map<string, StagedFile>) (e: Report.FileEdit) ->
                let key = fullPath e.Path

                let staged =
                    match Map.tryFind key acc with
                    | Some f -> {
                        f with
                            Current = e.After
                            References = f.References + e.References
                      }
                    | None -> {
                        Path = e.Path
                        Display = e.Display
                        Original = e.Before
                        Current = e.After
                        Bom = e.Bom
                        References = e.References
                      }

                acc.Add(key, staged))
            files

    /// Stage an edit: `db` (the edited copy) replaces the model, with the changes it made and the
    /// report file edits it planned. The model as it was can be had back with `undo`.
    let stage (m: StagedModel) (db: Database) (changes: ChangeSet.ObjectChange list) (files: Report.FileEdit list) =
        m.History <-
            {
                Db = m.Db
                Origins = m.Origins
                Unsaved = m.Unsaved
                Files = m.Files
            }
            :: m.History

        m.Db <- db

        for c in changes do
            match c.MovedFrom with
            | Some old ->
                let origin = Map.tryFind old m.Origins |> Option.defaultValue old
                m.Origins <- (Map.remove old m.Origins).Add(c.Ref, origin)
            | None -> ()

        m.Unsaved <- m.Unsaved + changes.Length
        m.Files <- mergeFiles m.Files files

    /// Take back the last staged edit. False if there is none (since the load, save or revert).
    let undo (m: StagedModel) =
        match m.History with
        | [] -> false
        | last :: rest ->
            m.History <- rest
            m.Db <- last.Db
            m.Origins <- last.Origins
            m.Unsaved <- last.Unsaved
            m.Files <- last.Files
            true

    /// How many staged edits `undo` can take back.
    let undoDepth (m: StagedModel) = m.History.Length

    let private reset (m: StagedModel) =
        m.Saved <- m.Db
        m.Origins <- Map.empty
        m.Baseline <- baselineOf m.Db
        m.Unsaved <- 0
        m.Files <- Map.empty
        m.History <- []

    /// Discard every staged edit: back to the model as it was loaded or last saved.
    let revert (m: StagedModel) =
        m.Db <- m.Saved
        reset m

    /// The staged report files as edits from the disk's text to the staged text.
    let fileEdits (m: StagedModel) : Report.FileEdit list = [
        for KeyValue(_, f) in m.Files do
            if f.Original <> f.Current then
                ({
                    Path = f.Path
                    Display = f.Display
                    Before = f.Original
                    After = f.Current
                    Bom = f.Bom
                    References = f.References
                }
                : Report.FileEdit)
    ]

    type Saved = {
        Model: ModelIO.SavedModel
        Files: Report.FileEdit list
        /// The errors the save let through (with force).
        Introduced: Validate.Issue list
    }

    /// Write the model back where it was loaded from, then the staged report files. Refused for a
    /// server model, and (unless `force`) while staged edits introduced gated errors.
    let save (m: StagedModel) (force: bool) : Result<Saved, TessError> =
        let introduced = introducedSinceLoad m

        if m.Source.Format = ModelFormat.Xmla then
            Error(
                RemoteSourceUnsupported(
                    m.Source.Path,
                    "a server model's staged edits can't be saved to the server; use --save-to <path> (or "
                    + "`convert`) to write them as files, then `tess deploy` them"
                )
            )
        elif not introduced.IsEmpty && not force then
            Error(ChangeIntroducesErrors introduced.Length)
        else
            let files = fileEdits m

            ModelIO.saveBack m.Source m.Db
            |> Result.bind (fun saved -> Report.write files |> Result.map (fun () -> saved))
            |> Result.map (fun saved ->
                reset m

                {
                    Model = saved
                    Files = files
                    Introduced = introduced
                })

// ---------------------------------------------------------------------------------------------
// The session a command runs in (tess interactive)

/// What a command inside `tess interactive` does with an edit of the session's model.
[<RequireQualifiedAccess>]
type EditMode =
    /// Stage it (the default).
    | Stage
    /// Show it only, as a dry run outside the session does (`--dry-run`).
    | DryRun

/// The session a command runs in: its model, and what to do with the command's edits.
type Active = {
    Model: StagedModel
    /// The model argument the session was started with.
    Input: string
    Mode: EditMode
}

let private current = Threading.AsyncLocal<Active option>()

/// The session the current command runs in, if any.
let active () = current.Value

/// Whether `input` (a model argument) names the session's model.
let names (a: Active) (input: string) =
    if XmlaAddress.isRemote a.Input || XmlaAddress.isRemote input then
        String.Equals(input, a.Input, StringComparison.OrdinalIgnoreCase)
        || String.Equals(input, a.Model.Source.Path, StringComparison.OrdinalIgnoreCase)
    else
        let comparison =
            if OperatingSystem.IsLinux() then
                StringComparison.Ordinal
            else
                StringComparison.OrdinalIgnoreCase

        let full (p: string) =
            try
                fullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            with _ ->
                p

        let target = full input

        [ Some a.Input; Some a.Model.Source.Path; a.Model.Source.SemanticModelFolder ]
        |> List.choose id
        |> List.exists (fun p -> String.Equals(full p, target, comparison))

/// Run `action` as a command of the session: loads of its model get a copy of the staged model,
/// and its edits are staged (by Mutation.runWith) as `a.Mode` says.
let within (a: Active) (action: unit -> 'a) : 'a =
    let previous = current.Value
    current.Value <- Some a

    try
        ModelIO.Override.using
            (fun input ->
                if names a input then
                    Some(a.Model.Source, ModelIO.clone a.Model.Db)
                else
                    None)
            action
    finally
        current.Value <- previous

/// The session whose model this is (the source a command loaded), if the command runs in one.
let stagingFor (source: ModelSource) =
    match current.Value with
    | Some a when obj.ReferenceEquals(a.Model.Source, source) || a.Model.Source = source -> Some a
    | _ -> None

/// How to read report files for planning edits of this model: through the session's staged files
/// when the model is a session's, otherwise from disk.
let readerFor (source: ModelSource) : string -> byte array =
    match stagingFor source with
    | Some a -> StagedModel.read a.Model
    | None -> File.ReadAllBytes

/// The report check for removing objects of this model (as staged, in a session).
let reportCheckFor (source: ModelSource) =
    ReportUsage.checkWith (readerFor source) (reportsOf source)
