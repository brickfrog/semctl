/// The CLI entry point, kept separate from Program.main so tests can drive it with captured writers.
module Tessera.Cli.App

open System.IO
open Argu
open Tessera
open Tessera.Cli.Args
open Tessera.Cli.Common
open Tessera.Cli.Output

let private parser = lazy (ArgumentParser.Create<CliArgs>(programName = "tess"))

/// For Argu's "unrecognized argument: 'x'": the subcommands (or, for `-x`, the options) of the
/// command it was given to that are close to x. The command is found by following the
/// subcommands named before x on the command line.
let private argumentSuggestions (argv: string array) (message: string) =
    let m =
        System.Text.RegularExpressions.Regex.Match(message, "unrecognized argument: '(.*?)'")

    if not m.Success then
        []
    else
        let bad = m.Groups.[1].Value

        let subcommands (p: ArgumentParser) = [
            for sub in p.GetSubCommandParsers() do
                match sub.ParentInfo with
                | Some info -> info.CommandLineNames.Value, sub
                | None -> ()
        ]

        let rec walk (p: ArgumentParser) (tokens: string list) =
            match tokens with
            | [] -> p
            | t :: _ when t = bad -> p
            | t :: rest ->
                match subcommands p |> List.tryFind (fun (names, _) -> List.contains t names) with
                | Some(_, sub) -> walk sub rest
                | None -> walk p rest

        let p = walk parser.Value (List.ofArray argv)

        let candidates =
            if bad.StartsWith "-" then
                [
                    for c in p.GetArgumentCases() do
                        if not c.IsMainCommand && not c.IsHidden.Value then
                            yield! c.CommandLineNames.Value |> List.filter (fun n -> n.StartsWith "-")
                    "--help"
                    "--json"
                ]
            else
                subcommands p |> List.collect fst

        // Options are compared without their dashes: "-x" is one letter, not two.
        let bare (s: string) = s.TrimStart '-'

        Suggest.closest (candidates |> List.map bare) (bare bad)
        |> List.map (fun b -> candidates |> List.find (fun c -> bare c = b))

/// Argu's message with the hint added to its first line ("ERROR: unrecognized argument: 'x'.").
let private withHint (message: string) (suggestions: string list) =
    match suggestions with
    | [] -> message
    | _ ->
        // Argu ends lines with Environment.NewLine: "\r\n" on Windows.
        let newline =
            match message.IndexOf '\n' with
            | n when n > 0 && message.[n - 1] = '\r' -> n - 1
            | n -> n

        let first, rest =
            if newline < 0 then
                message, ""
            else
                message.Substring(0, newline), message.Substring newline

        first.TrimEnd('.') + Suggest.hint suggestions + "." + rest

/// `tess test run ... --watch`.
let private (|WatchedTestRun|_|) (args: ParseResults<TestCommand.Args>) =
    match args.TryGetSubCommand() with
    | Some(TestCommand.Run run) when run.Contains TestCommand.RunArgs.Watch -> Some run
    | _ -> None

/// Commands whose arguments name no model of a session (`tess interactive` passes them as typed).
let private modelessCommands =
    set [
        "init"
        "completion"
        "connect"
        "profile"
        "session"
        "interactive"
        "shell"
        "mcp"
    ]

/// For `tess interactive`: put the session's model into a command line where its command takes it,
/// after the command and subcommand words (`bpa rules list` -> `bpa rules list <model>`). The line
/// is left as it is for a command without a model, or when it names the model (or, for `format`, a
/// .dax file) itself.
let private insertModel (words: string list) (model: string) : string list =
    let subcommands (p: ArgumentParser) = [
        for sub in p.GetSubCommandParsers() do
            match sub.ParentInfo with
            | Some info -> info.CommandLineNames.Value, sub
            | None -> ()
    ]

    let rec walk (p: ArgumentParser) (taken: string list) (rest: string list) =
        match rest with
        | w :: more ->
            match subcommands p |> List.tryFind (fun (names, _) -> List.contains w names) with
            | Some(_, sub) -> walk sub (taken @ [ w ]) more
            | None -> p, taken, rest
        | [] -> p, taken, rest

    let p, taken, rest = walk parser.Value [] words
    let takesModel = p.GetArgumentCases() |> List.exists (fun c -> c.IsMainCommand)

    let full (p: string) =
        try
            System.IO.Path.GetFullPath(p).TrimEnd('/', '\\')
        with _ ->
            p

    let namesModel =
        rest
        |> List.exists (fun a -> a = model || (not (a.StartsWith "-") && full a = full model))

    // `format` of loose DAX (a .dax file, --expr) takes no model.
    let daxFile =
        List.tryHead taken = Some "format"
        && rest
           |> List.exists (fun a ->
               a.EndsWith(".dax", System.StringComparison.OrdinalIgnoreCase)
               || a = "--expr"
               || a = "-e"
               || a.StartsWith "--expr=")

    match taken with
    | [] -> words
    | command :: _ when modelessCommands.Contains command -> words
    | _ when not takesModel || namesModel || daxFile -> words
    | _ -> taken @ [ model ] @ rest

/// Run the CLI with these environment variables, making any server connections through
/// `connector`, and (for `tess mcp` and `tess interactive`) reading from `input` (None: the
/// process's stdin). Returns the process exit code.
let rec runWithInput
    (input: TextReader option)
    (connector: Tessera.Xmla.IXmlaConnector)
    (getEnv: string -> string option)
    (out: TextWriter)
    (err: TextWriter)
    (argv: string array)
    : int =
    // `tess __complete <words...>` reaches Argu as one argument, so no word is read as an option.
    let argv = CompletionCommand.packWords argv
    // `tess mcp` owns stdout for its JSON-RPC frames: every message of its own goes to stderr.
    let isMcp = argv |> Array.tryFind (fun a -> not (a.StartsWith "-")) = Some "mcp"
    // Decide JSON mode before parsing so usage errors can be reported as JSON too.
    let jsonRequested =
        not isMcp && argv |> Array.exists (fun a -> a = "--json" || a = "-j")

    let usageErrorWith (message: string) (suggestions: string list) =
        if jsonRequested then
            writeJson out (errorJsonWith "usage" message suggestions)
        else
            err.WriteLine(message)

        ExitCode.usage

    let usageError (message: string) = usageErrorWith message []

    // `active:` and `profile:<name>` model arguments (local state only), and their profiles' settings.
    match Connections.prepare getEnv argv with
    | Error e ->
        if jsonRequested then
            writeJson out (tessErrorJson e)
        else
            err.WriteLine($"tess: {TessError.message e}")

        ExitCode.ofError e
    | Ok(argv, getEnv) ->

        try
            let results = parser.Value.ParseCommandLine(argv, raiseOnUsage = true)

            let ctx = {
                Out = out
                Err = err
                Json = results.Contains Json && not isMcp
                GetEnv = getEnv
                Connector = connector
            }

            if results.Contains Version then
                if ctx.Json then
                    writeJson out (versionJson (version ()))
                else
                    out.WriteLine($"tess {version ()}")

                ExitCode.ok
            else
                let outcome =
                    match results.TryGetSubCommand() with
                    | Some(Convert args) -> ConvertCommand.run ctx args
                    | Some(Info args) -> InfoCommand.run ctx args
                    | Some(List args) -> ListCommand.run ctx args
                    | Some(Get args) -> GetCommand.run ctx args
                    | Some(Find args) -> FindCommand.run ctx args
                    | Some(Diff args) -> DiffCommand.run ctx args
                    | Some(Deps args) -> DepsCommand.run ctx args
                    | Some(Init args) -> InitCommand.run ctx args
                    | Some(Validate args) when args.Contains ValidateCommand.Args.Watch ->
                        WatchCommands.validate ctx args
                    | Some(Validate args) -> ValidateCommand.run ctx args
                    | Some(Rename args) -> RenameCommand.run ctx args
                    | Some(Remove args) -> RemoveCommand.run ctx args
                    | Some(Move args) -> MoveCommand.run ctx args
                    | Some(Bpa args) when args.Contains BpaCommand.Args.Watch -> WatchCommands.bpa ctx args
                    | Some(Bpa args) -> BpaCommand.run ctx args
                    | Some(Set args) -> SetCommand.run ctx args
                    | Some(Add args) -> AddCommand.run ctx args
                    | Some(Translations args) -> TranslationsCommand.run ctx args
                    | Some(Perspective args) -> PerspectiveCommand.run ctx args
                    | Some(Script args) -> ScriptCommand.run ctx args
                    | Some(Lint args) when args.Contains LintCommand.Args.Watch -> WatchCommands.lint ctx args
                    | Some(Lint args) -> LintCommand.run ctx args
                    | Some(Format args) -> FormatCommand.run ctx args
                    | Some(Connect args) -> ConnectCommand.run ctx args
                    | Some(Profile args) -> ProfileCommand.run ctx args
                    | Some(Session args) -> SessionCommand.run ctx args
                    | Some(Query args) -> QueryCommand.run ctx args
                    | Some(Preview args) -> PreviewCommand.run ctx args
                    | Some(Deploy args) -> DeployCommand.run ctx args
                    | Some(Data args) -> DataCommand.run ctx args
                    | Some(Refresh args) -> RefreshCommand.run ctx args
                    | Some(Test(WatchedTestRun run as args)) -> WatchCommands.testRun ctx args run
                    | Some(Test args) -> TestCommand.run ctx args
                    | Some(Completion args) -> CompletionCommand.run parser.Value ctx args
                    | Some(Complete args) -> CompletionCommand.complete parser.Value ctx args
                    | Some(Mcp args) -> McpCommand.run ctx args input
                    | Some(Vertipaq args) -> VertipaqCommand.run ctx args
                    | Some(Schemacheck args) -> SchemaCheckCommand.run ctx args
                    | Some(Interactive args) ->
                        let host: InteractiveCommand.Host = {
                            // Each line runs as a command of its own, with this run's context.
                            Dispatch = fun line -> runWithInput None connector getEnv out err line
                            InsertModel = insertModel
                        }

                        // Without a model: the active connection, if it is local (a server is contacted
                        // only when named: `tess interactive active:`).
                        let model =
                            match args.TryGetResult InteractiveCommand.Args.Model with
                            | Some m -> Ok(Some m)
                            | None ->
                                match Connections.activeModel getEnv with
                                | Ok a when XmlaAddress.isRemote a.Model ->
                                    Error(
                                        UsageError(
                                            $"the active connection is a server ({a.Model}): start the session with "
                                            + "`tess interactive active:` to connect to it"
                                        )
                                    )
                                | Ok a -> Ok(Some a.Model)
                                | Error _ -> Ok None

                        match model with
                        | Error outcome -> outcome
                        | Ok model -> InteractiveCommand.run ctx args input host model
                    | _ -> UsageError(parser.Value.PrintUsage())

                match outcome with
                | Done -> ExitCode.ok
                | DoneWith code -> code
                | UsageError message -> usageError message
                | UsageErrorSuggest(message, suggestions) -> usageErrorWith message suggestions
                | Failed e ->
                    if ctx.Json then
                        writeJson out (tessErrorJson e)
                    else
                        err.WriteLine($"tess: {TessError.message e}")

                    ExitCode.ofError e
        with
        | :? ArguParseException as e when e.ErrorCode = ErrorCode.HelpText && isMcp ->
            err.WriteLine(e.Message)
            ExitCode.ok
        | :? ArguParseException as e when e.ErrorCode = ErrorCode.HelpText ->
            out.WriteLine(e.Message)
            ExitCode.ok
        | :? ArguParseException as e ->
            let suggestions = argumentSuggestions argv e.Message
            usageErrorWith (withHint e.Message suggestions) suggestions
        | e ->
            if jsonRequested then
                writeJson out (errorJson "internal" e.Message)
            else
                err.WriteLine($"tess: internal error: {e}")

            ExitCode.internalError

/// Run the CLI with these environment variables, making any server connections through
/// `connector`. Returns the process exit code.
let runWithConnector
    (connector: Tessera.Xmla.IXmlaConnector)
    (getEnv: string -> string option)
    (out: TextWriter)
    (err: TextWriter)
    (argv: string array)
    : int =
    runWithInput None connector getEnv out err argv

/// Run the CLI with these environment variables. Returns the process exit code.
// Kept out of line: the offline tests check that the live connector is created only here.
[<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
let runWith (getEnv: string -> string option) (out: TextWriter) (err: TextWriter) (argv: string array) : int =
    runWithConnector (Tessera.Xmla.LiveConnector()) getEnv out err argv

/// Run the CLI. Returns the process exit code.
let run (out: TextWriter) (err: TextWriter) (argv: string array) : int = runWith processEnv out err argv
