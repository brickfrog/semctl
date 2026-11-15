/// `tess interactive <model>` (alias `tess shell`): a REPL over one model held in memory.
///
/// Every tess command runs inside it without the model argument (`get [Total]`, `set ...`,
/// `query --data ./data "EVALUATE ..."`): the session puts the model back in. Loads of the model
/// get a copy of the model as staged (ModelIO.Override), and edits of it (everything that goes
/// through Mutation.runWith) are staged rather than written: `save` writes them, `revert` discards
/// them, `undo` takes back the last one, `status` and `diff` show them. A command's `--dry-run`
/// shows its edit without staging it; its `--save` stages it and saves the session (every staged
/// edit) at once. The in-memory model is Staging.StagedModel, the same as `tess mcp`'s.
///
/// Input from a terminal is read with LineEditor (history in memory; a history file only with
/// --history). Piped input runs line by line in batch mode: `#` comments, --echo, and the first
/// failing command ends the run with its exit code (--no-batch runs on). Staged edits are never
/// dropped silently: at the end of piped input they make the run fail (exit 1); at a terminal
/// `exit` asks first. `exit --force` discards them on purpose.
module Tessera.Cli.InteractiveCommand

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open Argu
open Tessera
open Tessera.Cli.Output
open Tessera.Cli.Common
open Tessera.Cli.Staging

/// One-line description, shown in `tess --help`.
let summary =
    "a shell over one model: run any command without the model; edits stage in memory until `save`."

type Args =
    | [<MainCommand>] Model of path: string
    | Echo
    | Batch
    | No_Batch
    | No_Banner
    | History of path: string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Model _ ->
                "the model (any path tess accepts, or xmla:<server>;<database>); default the active connection "
                + "(`tess connect`)."
            | Echo -> "write each command to stdout before its output (for transcripts of piped input)."
            | Batch ->
                "read commands from stdin line by line and stop at the first that fails (the default when stdin "
                + "is piped)."
            | No_Batch -> "with piped input, run every line even after one fails."
            | No_Banner -> "don't print the welcome banner."
            | History _ ->
                "keep the command history in this file (read at the start, appended to). Without it the history "
                + "lives in memory only."

// ---------------------------------------------------------------------------------------------
// Splitting a line into arguments

/// Split a command line into arguments, as `te interactive` does: whitespace separates them;
/// "..." and '...' group (the quotes are dropped, a doubled quote is a literal one); [...] groups
/// and keeps its brackets (a doubled ]] inside is kept as it is), so 'Sales'[Total] and [Total]
/// reach the command whole. A '...' directly followed by [ keeps its quotes too: it is a DAX table
/// name ('Internet Sales'[Amount]). An unterminated group is an error.
let split (line: string) : Result<string list, string> =
    let args = ResizeArray<string>()
    let current = StringBuilder()
    let mutable inArg = false
    let mutable error = None
    let mutable i = 0
    let n = line.Length

    /// Read a quoted group starting at i (the opening quote); the text inside and the index after it.
    let quoted (q: char) (start: int) =
        let sb = StringBuilder()
        let mutable k = start + 1
        let mutable closed = false

        while not closed && k < n do
            if line.[k] = q then
                if k + 1 < n && line.[k + 1] = q then
                    sb.Append q |> ignore
                    k <- k + 2
                else
                    closed <- true
                    k <- k + 1
            else
                sb.Append line.[k] |> ignore
                k <- k + 1

        if closed then Ok(sb.ToString(), k) else Error $"unterminated {q} at column {start + 1}"

    while error.IsNone && i < n do
        let c = line.[i]

        if Char.IsWhiteSpace c then
            if inArg then
                args.Add(current.ToString())
                current.Clear() |> ignore
                inArg <- false

            i <- i + 1
        else
            inArg <- true

            match c with
            | '"' ->
                match quoted '"' i with
                | Ok(text, next) ->
                    current.Append text |> ignore
                    i <- next
                | Error e -> error <- Some e
            | '\'' ->
                match quoted '\'' i with
                | Ok(text, next) ->
                    // 'Table'[...]: a DAX table name keeps its quotes (doubled again).
                    if next < n && line.[next] = '[' then
                        current.Append('\'').Append(text.Replace("'", "''")).Append('\'') |> ignore
                    else
                        current.Append text |> ignore

                    i <- next
                | Error e -> error <- Some e
            | '[' ->
                let mutable k = i + 1
                let mutable closed = false

                while not closed && k < n do
                    if line.[k] = ']' then
                        if k + 1 < n && line.[k + 1] = ']' then
                            k <- k + 2
                        else
                            closed <- true
                            k <- k + 1
                    else
                        k <- k + 1

                if closed then
                    current.Append(line, i, k - i) |> ignore
                    i <- k
                else
                    error <- Some $"unterminated [ at column {i + 1}"
            | c ->
                current.Append c |> ignore
                i <- i + 1

    match error with
    | Some e -> Error e
    | None ->
        if inArg then
            args.Add(current.ToString())

        Ok(List.ofSeq args)

// ---------------------------------------------------------------------------------------------
// Input

/// Where the session's lines come from.
type LineSource =
    /// Read the next line (None: the input ended), showing `prompt` at a terminal.
    abstract Read: prompt: string -> history: string list -> LineEditor.Read
    /// Someone can answer questions.
    abstract IsTerminal: bool
    /// Ask a yes/no question ("no" unless answered yes).
    abstract Confirm: question: string -> bool

/// Lines from a reader (piped input, a script, a test): never a terminal.
let readerSource (reader: TextReader) =
    { new LineSource with
        member _.Read _ _ =
            match reader.ReadLine() with
            | null -> LineEditor.Read.EndOfInput
            | line -> LineEditor.Read.Line line

        member _.IsTerminal = false
        member _.Confirm _ = false
    }

/// The console, with the line editor.
let consoleSource =
    { new LineSource with
        member _.Read prompt history = LineEditor.readLine prompt history
        member _.IsTerminal = true
        member _.Confirm question = LineEditor.confirm question
    }

// ---------------------------------------------------------------------------------------------
// The session

/// What the session needs from the CLI around it.
type Host = {
    /// Run a tess command line (with the context the session was started in). Returns its exit code.
    Dispatch: string array -> int
    /// Put the model argument into a command line where its command takes it (after the command and
    /// subcommand words); the line as it is for commands without a model.
    InsertModel: string list -> string -> string list
}

type Options = {
    Echo: bool
    /// Stop at the first failing command.
    Batch: bool
    Banner: bool
    HistoryFile: string option
}

let private plural n (word: string) =
    if n = 1 then $"1 {word}" else $"{n} {word}s"

/// Commands the session doesn't run: they'd take over its input or never return.
let private refusedCommands =
    Map.ofList [
        "interactive", "you are in a session already"
        "shell", "you are in a session already"
        "mcp", "`tess mcp` owns stdin and stdout; start it from the outer shell"
        "__complete", "completion candidates come from the outer shell"
    ]

let private builtinHelp =
    """Session commands:
  status (pwd)          the model, and what is staged (--json)
  diff [--stat|--name-only|--json]
                        the staged changes against the model as saved (diff <other> compares with another model)
  save [--force]        write every staged edit (and staged report files); --force past new errors
  revert                discard every staged edit
  undo                  take back the last staged edit
  history               the commands run so far
  clear (cls)           clear the screen
  help (?)              this list; <command> --help for a command's own
  exit (quit, q) [--force]
                        leave; staged edits are kept only by `save` (--force discards them without asking)

Every tess command works without the model argument: list, get, find, set, rename, add, remove, move,
script, validate, bpa, lint, format, deps, query, preview, test, convert, deploy, ... Edits stage in memory:
a command's --dry-run shows its edit without staging it, its --save stages it and saves the session.
Arguments: "..." and '...' group words; [Total] and 'Sales'[Total] stay whole. `-` (stdin) is refused."""

type private State = {
    Ctx: Ctx
    Host: Host
    Options: Options
    Input: string
    Model: StagedModel
    Source: LineSource
    mutable History: string list
}

let private name (s: State) =
    let p = s.Input.TrimEnd('/', '\\')

    match Path.GetFileName p with
    | "" -> p
    | n -> n

let private prompt (s: State) =
    let mark = if StagedModel.isDirty s.Model then "*" else ""
    $"tess {name s}{mark}> "

/// The staged changes against the model as loaded or saved.
let private stagedDiff (m: StagedModel) =
    let renames = m.Origins |> Map.toList |> List.map (fun (current, original) -> original, current)

    Diff.compare
        {
            Diff.Options.none with
                KnownRenames = renames
        }
        m.Saved
        m.Db

let private filesJson (files: StagedFile list) =
    arr [
        for f in files ->
            obj [ "path", str f.Path; "display", str f.Display; "references", num f.References ]
    ]

let private status (s: State) (json: bool) =
    let m = s.Model
    let changes = ChangeSet.ofDiff (stagedDiff m)
    let files = m.Files |> Map.toList |> List.map snd

    if json then
        let o = Changes.changesJson "status" changes :?> JsonObject
        o.["model"] <- str m.Source.Path
        o.["format"] <- str (ModelFormat.token m.Source.Format)
        o.["unsavedChanges"] <- num m.Unsaved
        o.["undo"] <- num (StagedModel.undoDepth m)
        o.["reportFiles"] <- filesJson files
        o.["errorsIntroducedSinceLoad"] <- num (StagedModel.introducedSinceLoad m).Length
        writeJson s.Ctx.Out o
    else
        let out = s.Ctx.Out
        out.WriteLine($"Model   {m.Source.Path}  ({ModelFormat.token m.Source.Format})")

        if not (StagedModel.isDirty m) then
            out.WriteLine("Nothing staged.")
        else
            out.WriteLine(
                $"""Staged  {plural m.Unsaved "change"} in {plural (StagedModel.undoDepth m) "edit"} (not saved)"""
            )

            if not changes.IsEmpty then
                out.WriteLine()
                Changes.statText out changes

            for f in files do
                out.WriteLine($"""~ report {f.Display}  ({plural f.References "reference"})""")

            match (StagedModel.introducedSinceLoad m).Length with
            | 0 -> ()
            | n ->
                out.WriteLine()
                out.WriteLine($"""{plural n "new error"}: `save` refuses until they are fixed (or `save --force`).""")

    0

let private diff (s: State) (flags: string list) =
    let changes = stagedDiff s.Model
    let json = s.Ctx.Json || List.contains "--json" flags || List.contains "-j" flags

    match flags |> List.filter (fun f -> not (List.contains f [ "--json"; "-j"; "--stat"; "--name-only" ])) with
    | bad :: _ ->
        s.Ctx.Err.WriteLine($"tess: diff: unexpected '{bad}' (diff takes --stat, --name-only or --json)")
        ExitCode.usage
    | [] ->
        if json then
            writeJson s.Ctx.Out (Changes.changesJson "diff" (ChangeSet.ofDiff changes))
        elif List.contains "--stat" flags then
            Changes.statText s.Ctx.Out (ChangeSet.ofDiff changes)
        elif List.contains "--name-only" flags then
            Changes.namesText s.Ctx.Out (ChangeSet.ofDiff changes)
        elif changes.IsEmpty then
            s.Ctx.Out.WriteLine("No changes.")
        else
            Changes.diffText s.Ctx.Out changes

        // As `tess diff`: 1 when there are differences.
        if changes.IsEmpty then ExitCode.ok else ExitCode.findings

let private fail (s: State) (json: bool) (e: TessError) =
    if json then
        writeJson s.Ctx.Out (tessErrorJson e)
    else
        s.Ctx.Err.WriteLine($"tess: {TessError.message e}")

    ExitCode.ofError e

let private save (s: State) (json: bool) (force: bool) =
    let m = s.Model

    if not (StagedModel.isDirty m) then
        if json then
            writeJson s.Ctx.Out (obj [ "saved", bool false; "note", str "nothing staged" ])
        else
            s.Ctx.Err.WriteLine("Nothing staged; nothing to save.")

        ExitCode.ok
    else
        let unsaved = m.Unsaved

        match StagedModel.save m force with
        | Error e -> fail s json e
        | Ok saved ->
            let refs = saved.Files |> List.sumBy (fun f -> f.References)

            if json then
                writeJson
                    s.Ctx.Out
                    (obj [
                        "saved", bool true
                        "changes", num unsaved
                        "format", str (ModelIO.OutputFormat.token saved.Model.Format)
                        "path", str saved.Model.Path
                        "eol", str (ModelIO.Eol.token saved.Model.Eol)
                        "reportFiles",
                        arr [
                            for f in saved.Files ->
                                obj [ "path", str f.Path; "display", str f.Display; "references", num f.References ]
                        ]
                    ])
            else
                let format = ModelIO.OutputFormat.token saved.Model.Format

                s.Ctx.Err.WriteLine(
                    $"""Saved {format}  {saved.Model.Path}  ({ModelIO.Eol.token saved.Model.Eol}): {plural unsaved "change"}"""
                )

                if not saved.Files.IsEmpty then
                    s.Ctx.Err.WriteLine(
                        $"""Updated {plural refs "reference"} in {plural saved.Files.Length "file"} of the report"""
                    )

            ExitCode.ok

/// Run one command line of the session (not a built-in).
let private command (s: State) (args: string list) =
    let err = s.Ctx.Err

    let dryRun =
        args |> List.exists (fun a -> a = "--dry-run" || a = "--revert")

    let args =
        args
        |> List.filter (fun a -> not (List.contains a [ "--dry-run"; "--revert"; "--stage" ]))

    match args with
    | [] -> ExitCode.ok
    | first :: _ when refusedCommands.ContainsKey first ->
        err.WriteLine($"tess: {first} can't run inside tess interactive: {refusedCommands.[first]}")
        ExitCode.usage
    | _ when List.contains "-" args ->
        err.WriteLine(
            "tess: `-` (read from stdin) can't be used inside tess interactive, which reads its commands from "
            + "stdin; use a file, or run the command from the outer shell"
        )

        ExitCode.usage
    | _ when List.contains "--watch" args ->
        err.WriteLine("tess: --watch can't be used inside tess interactive; run it from the outer shell")
        ExitCode.usage
    | _ when dryRun && List.contains "--save" args ->
        err.WriteLine("tess: --dry-run and --save can't be combined")
        ExitCode.usage
    | _ ->
        let argv = s.Host.InsertModel args s.Input

        let argv =
            if s.Ctx.Json && not (List.contains "--json" argv || List.contains "-j" argv) then
                argv @ [ "--json" ]
            else
                argv

        let active = {
            Model = s.Model
            Input = s.Input
            Mode = if dryRun then EditMode.DryRun else EditMode.Stage
        }

        Staging.within active (fun () -> s.Host.Dispatch(Array.ofList argv))

/// What a line asks for: go on, or end the session with an exit code.
[<RequireQualifiedAccess>]
type private Next =
    | Continue of exitCode: int
    | Exit of exitCode: int

/// Leaving with staged edits: at a terminal, ask (no by default); otherwise fail.
let private leave (s: State) (force: bool) =
    let m = s.Model

    if not (StagedModel.isDirty m) || force then
        Next.Exit ExitCode.ok
    elif s.Source.IsTerminal then
        if s.Source.Confirm $"""Discard {plural m.Unsaved "unsaved change"} and exit?""" then
            Next.Exit ExitCode.ok
        else
            Next.Continue ExitCode.ok
    else
        let e = UnsavedChanges m.Unsaved
        s.Ctx.Err.WriteLine($"tess: {TessError.message e}")

        for c in ChangeSet.ofDiff (stagedDiff m) do
            s.Ctx.Err.WriteLine($"  {ObjectRef.format c.Ref}")

        Next.Exit(ExitCode.ofError e)

let private run1 (s: State) (line: string) : Next =
    match split line with
    | Error message ->
        s.Ctx.Err.WriteLine($"tess: {message}")
        Next.Continue ExitCode.usage
    | Ok [] -> Next.Continue ExitCode.ok
    | Ok(word :: rest) ->
        let json = s.Ctx.Json || List.contains "--json" rest || List.contains "-j" rest
        let force = List.contains "--force" rest

        match word.ToLowerInvariant() with
        | "help"
        | "?" ->
            s.Ctx.Out.WriteLine(builtinHelp)
            Next.Continue ExitCode.ok
        | "status"
        | "pwd" -> Next.Continue(status s json)
        | "diff" when rest |> List.forall (fun a -> a.StartsWith "-") -> Next.Continue(diff s rest)
        | "save" -> Next.Continue(save s json force)
        | "revert" ->
            let n = s.Model.Unsaved
            StagedModel.revert s.Model

            if json then
                writeJson s.Ctx.Out (obj [ "reverted", num n ])
            else
                s.Ctx.Err.WriteLine(
                    if n = 0 then
                        "Nothing staged."
                    else
                        $"""Discarded {plural n "unsaved change"}."""
                )

            Next.Continue ExitCode.ok
        | "undo" ->
            let undone = StagedModel.undo s.Model

            if json then
                writeJson s.Ctx.Out (obj [ "undone", bool undone; "unsavedChanges", num s.Model.Unsaved ])
            elif undone then
                s.Ctx.Err.WriteLine($"""Took back the last edit ({plural s.Model.Unsaved "unsaved change"} left).""")
            else
                s.Ctx.Err.WriteLine("Nothing to undo.")

            Next.Continue ExitCode.ok
        | "history" ->
            for h in s.History do
                s.Ctx.Out.WriteLine h

            Next.Continue ExitCode.ok
        | "clear"
        | "cls" ->
            if s.Source.IsTerminal then
                try
                    Console.Clear()
                with _ ->
                    ()

            Next.Continue ExitCode.ok
        | "exit"
        | "quit"
        | "q" -> leave s force
        | _ -> Next.Continue(command s (word :: rest))

let private remember (s: State) (line: string) =
    s.History <- s.History @ [ line ]

    match s.Options.HistoryFile with
    | Some file ->
        try
            File.AppendAllText(file, line + "\n")
        with e ->
            s.Ctx.Err.WriteLine($"tess: warning: can't write the history file {file}: {e.Message}")
    | None -> ()

/// Run a session over `model` (loaded from `input`), reading lines from `source`. Returns the exit code.
let runSession
    (ctx: Ctx)
    (host: Host)
    (options: Options)
    (input: string)
    (source: ModelSource)
    (db: Microsoft.AnalysisServices.Tabular.Database)
    (lines: LineSource)
    : int =
    let s = {
        Ctx = ctx
        Host = host
        Options = options
        Input = input
        Model = StagedModel.create source db
        Source = lines
        History = []
    }

    match options.HistoryFile with
    | Some file when File.Exists file ->
        try
            s.History <- File.ReadAllLines file |> Array.filter (fun l -> l <> "") |> List.ofArray
        with e ->
            ctx.Err.WriteLine($"tess: warning: can't read the history file {file}: {e.Message}")
    | _ -> ()

    if options.Banner && lines.IsTerminal then
        ctx.Out.WriteLine($"tess {version ()} interactive: {source.Path} ({ModelFormat.token source.Format})")
        ctx.Out.WriteLine("Commands run without the model; edits stage until `save`. `help` lists the rest.")

    // A failure of piped input in batch mode ends the run with its code; otherwise the run's code is 0.
    let rec loop () =
        match lines.Read (prompt s) s.History with
        | LineEditor.Read.Cancelled -> loop ()
        | LineEditor.Read.EndOfInput ->
            match leave s false with
            | Next.Exit code -> code
            | Next.Continue _ -> loop ()
        | LineEditor.Read.Line line ->
            let trimmed = line.Trim()

            if trimmed = "" || trimmed.StartsWith "#" then
                loop ()
            else
                if options.Echo then
                    ctx.Out.WriteLine(prompt s + trimmed)

                if lines.IsTerminal then
                    remember s trimmed

                let next =
                    try
                        run1 s trimmed
                    with e ->
                        ctx.Err.WriteLine($"tess: internal error: {e}")
                        Next.Continue ExitCode.internalError

                ctx.Out.Flush()

                match next with
                | Next.Exit code -> code
                | Next.Continue code when code <> ExitCode.ok && options.Batch && not lines.IsTerminal ->
                    // `diff` with differences is not a failure, as with `tess diff` in a script.
                    if code = ExitCode.findings && trimmed.StartsWith "diff" then
                        loop ()
                    else
                        ctx.Err.WriteLine($"tess: stopped at the failing command (exit {code}): {trimmed}")
                        code
                | Next.Continue _ -> loop ()

    let cancel = ConsoleCancelEventHandler(fun _ e -> e.Cancel <- true)

    if lines.IsTerminal then
        Console.CancelKeyPress.AddHandler cancel

    try
        loop ()
    finally
        if lines.IsTerminal then
            Console.CancelKeyPress.RemoveHandler cancel

/// `tess interactive`. `input`: the lines (tests); None reads the console, or stdin when piped.
let run (ctx: Ctx) (args: ParseResults<Args>) (input: TextReader option) (host: Host) (model: string option) =
    match model with
    | _ when args.Contains Args.Batch && args.Contains Args.No_Batch ->
        UsageError "--batch and --no-batch can't be combined"
    | None ->
        UsageError
            "expected a model: tess interactive <model> (or make one the active connection with `tess connect <model>`)"
    | Some model ->
        match Source.load ctx model with
        | Error e -> Failed e
        | Ok(source, db) ->
            let piped =
                match input with
                | Some _ -> true
                | None -> Console.IsInputRedirected || args.Contains Args.Batch

            let options = {
                Echo = args.Contains Args.Echo
                Batch = not (args.Contains Args.No_Batch)
                Banner = not (args.Contains Args.No_Banner)
                HistoryFile = args.TryGetResult Args.History
            }

            let lines =
                match input with
                | Some reader -> readerSource reader
                | None when piped -> readerSource Console.In
                | None -> consoleSource

            match runSession ctx host options model source db lines with
            | 0 -> Done
            | code -> DoneWith code
