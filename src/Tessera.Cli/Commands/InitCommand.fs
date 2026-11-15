module Tessera.Cli.InitCommand

open System.IO
open System.Text.Json.Nodes
open Argu
open Tessera
open Tessera.Cli.Output
open Tessera.Cli.Common

/// One-line description, shown in `tess --help`.
let summary = "create a new, empty semantic model."

type Args =
    | [<MainCommand; ExactlyOnce>] Destination of path: string
    | [<AltCommandLine("-n")>] Name of name: string
    | Compatibility_Level of level: int
    | [<AltCommandLine("-f")>] Format of format: string
    | Culture of culture: string
    | Force

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Destination _ -> "where to create the model (X.SemanticModel folder, .bim file or TMDL folder)."
            | Name _ -> "model name. Default: the folder or file name, without .SemanticModel or .bim."
            | Compatibility_Level _ ->
                $"compatibility level (at least {Scaffold.minCompatibilityLevel}). "
                + $"Default: {Scaffold.defaultCompatibilityLevel}."
            | Format _ ->
                "output format: bim | tmdl | pbip | database.json. Default: inferred from the path "
                + "(*.bim -> bim, *.SemanticModel -> pbip, otherwise a tmdl folder)."
            | Culture _ -> $"model culture, also used as the source query culture. Default: {Scaffold.defaultCulture}."
            | Force -> "overwrite a non-empty destination."

let initJson (opts: Scaffold.Options) (saved: ModelIO.SavedModel) =
    obj [
        "model",
        obj [
            "name", str opts.Name
            "compatibilityLevel", num opts.CompatibilityLevel
            "culture", str opts.Culture
        ]
        "output",
        obj [
            "format", str (ModelIO.OutputFormat.token saved.Format)
            "path", str saved.Path
            "eol", str (ModelIO.Eol.token saved.Eol)
            "files", arr [ for f in saved.Files -> str f ]
        ]
    ]

let initText (out: TextWriter) (opts: Scaffold.Options) (saved: ModelIO.SavedModel) =
    let n = saved.Files.Length
    let files = if n = 1 then "1 file" else $"{n} files"
    out.WriteLine($"Created {ModelIO.OutputFormat.token saved.Format}  {saved.Path}  ({files})")
    out.WriteLine($"  model {opts.Name}, compatibility level {opts.CompatibilityLevel}, culture {opts.Culture}")

/// A database as TOM JSON with its name and ID left out, and the annotation the database.json
/// layout adds to say how it splits the model: what makes two models the same model.
let private canonical (db: Microsoft.AnalysisServices.Tabular.Database) =
    let copy = ModelIO.clone db
    copy.Name <- "Model"
    copy.ID <- "Model"

    match copy.Model.Annotations.Find FolderFormat.FolderOptions.annotationName with
    | null -> ()
    | a -> copy.Model.Annotations.Remove a |> ignore

    ModelIO.toBimText ModelIO.SaveOptions.defaults copy

/// The destination already holds the model `init` would create, in the format it would write:
/// re-running `init` has nothing to do. Anything else there is left to the usual refusal.
let private alreadyThere (format: ModelIO.OutputFormat) (opts: Scaffold.Options) (path: string) =
    match ModelIO.loadPath path, Scaffold.create opts with
    | Ok(src, existing), Ok fresh ->
        let sameFormat =
            match format, src.Format with
            | ModelIO.BimFile, Bim -> src.SemanticModelFolder.IsNone
            | ModelIO.TmdlFolder, Tmdl -> src.SemanticModelFolder.IsNone
            | ModelIO.PbipFolder, Tmdl -> src.SemanticModelFolder.IsSome
            | ModelIO.DatabaseJsonFolder, DatabaseJson -> true
            | _ -> false

        if sameFormat && canonical existing = canonical fresh then
            Some src
        else
            None
    | _ -> None

let private unchangedJson (opts: Scaffold.Options) (format: ModelIO.OutputFormat) (src: ModelSource) =
    obj [
        "created", bool false
        "reason", str "the destination already holds this model, as init would write it; nothing was written"
        "model",
        obj [
            "name", str opts.Name
            "compatibilityLevel", num opts.CompatibilityLevel
            "culture", str opts.Culture
        ]
        "output", obj [ "format", str (ModelIO.OutputFormat.token format); "path", str src.Path ]
    ]

let run (ctx: Ctx) (args: ParseResults<Args>) =
    let path = args.GetResult Args.Destination
    let defaults = Scaffold.defaults path

    let format =
        match args.TryGetResult Args.Format with
        | None -> Ok(ModelIO.OutputFormat.infer path)
        | Some f ->
            ModelIO.OutputFormat.tryParse f
            |> Option.map Ok
            |> Option.defaultValue (
                Error
                    $"""unknown format '{f}'{Suggest.hint (Suggest.closest ModelIO.OutputFormat.tokens f)} (expected {System.String.Join(", ", ModelIO.OutputFormat.tokens)})"""
            )

    let name =
        match args.TryGetResult Args.Name with
        | None -> Ok defaults.Name
        | Some n when System.String.IsNullOrWhiteSpace n -> Error "--name must not be empty"
        | Some n -> Ok(n.Trim())

    let level =
        match args.TryGetResult Args.Compatibility_Level with
        | None -> Ok defaults.CompatibilityLevel
        | Some l when l < Scaffold.minCompatibilityLevel ->
            Error $"--compatibility-level must be at least {Scaffold.minCompatibilityLevel}"
        | Some l -> Ok l

    let culture =
        match args.TryGetResult Args.Culture with
        | None -> Ok defaults.Culture
        | Some c ->
            Scaffold.tryCulture c
            |> Option.map Ok
            |> Option.defaultValue (Error $"unknown or non-specific culture '{c}' (expected e.g. en-US)")

    match format, name, level, culture with
    | Error e, _, _, _
    | _, Error e, _, _
    | _, _, Error e, _
    | _, _, _, Error e -> UsageError e
    | Ok format, Ok name, Ok level, Ok culture ->
        let opts: Scaffold.Options = {
            Name = name
            CompatibilityLevel = level
            Culture = culture
        }

        match Scaffold.init format opts (args.Contains Args.Force) path with
        | Error(DestinationNotEmpty _ as e) ->
            match alreadyThere format opts path with
            | Some src ->
                if ctx.Json then
                    writeJson ctx.Out (unchangedJson opts format src)
                else
                    ctx.Out.WriteLine($"Nothing to do: {src.Path} already holds this model.")

                Done
            | None -> Failed e
        | Error e -> Failed e
        | Ok saved ->
            if ctx.Json then
                writeJson ctx.Out (initJson opts saved)
            else
                initText ctx.Out opts saved

            Done
