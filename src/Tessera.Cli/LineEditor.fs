/// A small single-line editor for `tess interactive` at a terminal: no dependencies, nothing
/// leaves the process. Left/Right, Home/End (Ctrl+A/Ctrl+E), Backspace/Delete, Ctrl+U (clear the
/// line), Ctrl+K (clear to the end), Up/Down through the history, Ctrl+C abandons the line (it is
/// not run and not kept in the history), Ctrl+D on an empty line ends the input (Ctrl+Z then Enter
/// on Windows).
module Tessera.Cli.LineEditor

open System
open System.Text

/// What reading a line gave.
[<RequireQualifiedAccess>]
type Read =
    | Line of string
    /// Ctrl+C: the line was abandoned.
    | Cancelled
    /// Ctrl+D on an empty line, or the input ended.
    | EndOfInput

/// Read a line at the console after writing `prompt`, offering `history` (oldest first) with Up/Down.
let readLine (prompt: string) (history: string list) : Read =
    let buffer = StringBuilder()
    let mutable cursor = 0
    let past = Array.ofList history
    // Where Up/Down is in the history: past.Length is the line being typed.
    let mutable at = past.Length
    let mutable typed = ""
    let mutable shown = 0

    let redraw () =
        // Back to the start of the line, rewrite it, blank what's left of a longer previous line.
        let text = buffer.ToString()
        Console.Write("\r" + prompt + text)
        let extra = shown - text.Length

        if extra > 0 then
            Console.Write(String(' ', extra) + String('\b', extra))

        shown <- text.Length
        Console.Write(String('\b', text.Length - cursor))

    let replace (text: string) =
        buffer.Clear().Append(text) |> ignore
        cursor <- text.Length
        redraw ()

    let previous = Console.TreatControlCAsInput
    Console.TreatControlCAsInput <- true
    Console.Write prompt

    try
        let rec loop () =
            let key = Console.ReadKey(true)
            let ctrl = key.Modifiers.HasFlag ConsoleModifiers.Control

            match key.Key with
            | ConsoleKey.Enter ->
                Console.WriteLine()

                match buffer.ToString() with
                | "\u001a" -> Read.EndOfInput
                | line -> Read.Line line
            | ConsoleKey.C when ctrl ->
                Console.WriteLine("^C")
                Read.Cancelled
            | ConsoleKey.D when ctrl && buffer.Length = 0 ->
                Console.WriteLine()
                Read.EndOfInput
            | ConsoleKey.D when ctrl -> loop ()
            | ConsoleKey.LeftArrow ->
                if cursor > 0 then
                    cursor <- cursor - 1
                    Console.Write '\b'

                loop ()
            | ConsoleKey.RightArrow ->
                if cursor < buffer.Length then
                    Console.Write(buffer.[cursor])
                    cursor <- cursor + 1

                loop ()
            | ConsoleKey.Home
            | ConsoleKey.A when ctrl || key.Key = ConsoleKey.Home ->
                cursor <- 0
                redraw ()
                loop ()
            | ConsoleKey.End
            | ConsoleKey.E when ctrl || key.Key = ConsoleKey.End ->
                cursor <- buffer.Length
                redraw ()
                loop ()
            | ConsoleKey.U when ctrl ->
                replace ""
                loop ()
            | ConsoleKey.K when ctrl ->
                buffer.Remove(cursor, buffer.Length - cursor) |> ignore
                redraw ()
                loop ()
            | ConsoleKey.Backspace ->
                if cursor > 0 then
                    buffer.Remove(cursor - 1, 1) |> ignore
                    cursor <- cursor - 1
                    redraw ()

                loop ()
            | ConsoleKey.Delete ->
                if cursor < buffer.Length then
                    buffer.Remove(cursor, 1) |> ignore
                    redraw ()

                loop ()
            | ConsoleKey.UpArrow ->
                if at > 0 then
                    if at = past.Length then
                        typed <- buffer.ToString()

                    at <- at - 1
                    replace past.[at]

                loop ()
            | ConsoleKey.DownArrow ->
                if at < past.Length then
                    at <- at + 1
                    replace (if at = past.Length then typed else past.[at])

                loop ()
            | _ when key.KeyChar = '\u001a' ->
                // Ctrl+Z (Windows' end of input), taken when Enter follows on an empty line.
                if buffer.Length = 0 then
                    buffer.Append('\u001a') |> ignore
                    cursor <- 1
                    Console.Write "^Z"

                loop ()
            | _ when not (Char.IsControl key.KeyChar) && not ctrl ->
                buffer.Insert(cursor, key.KeyChar) |> ignore
                cursor <- cursor + 1

                if cursor = buffer.Length then
                    Console.Write key.KeyChar
                    shown <- buffer.Length
                else
                    redraw ()

                loop ()
            | _ -> loop ()

        loop ()
    finally
        Console.TreatControlCAsInput <- previous

/// Ask a yes/no question at the console; "no" unless the answer starts with y.
let confirm (question: string) =
    match readLine $"{question} [y/N] " [] with
    | Read.Line answer -> answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase)
    | Read.Cancelled
    | Read.EndOfInput -> false
