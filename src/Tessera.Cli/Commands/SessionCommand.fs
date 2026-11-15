/// `tess session`: the per-terminal state behind `tess connect` (Connections): show it, list every
/// session's, clear this one, prune those whose terminal is gone. Local files only.
module Tessera.Cli.SessionCommand

open System.IO
open Argu
open Tessera
open Tessera.Cli.Output
open Tessera.Cli.Common
open Tessera.Cli.Connections

/// One-line description, shown in `tess --help`.
let summary =
    "the per-terminal session state behind `tess connect`: show, list, clear, prune."

type NoArgs =
    | [<Hidden>] Placeholder

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Placeholder -> ""

type Args =
    | [<CliPrefix(CliPrefix.None)>] Show of ParseResults<NoArgs>
    | [<CliPrefix(CliPrefix.None)>] List of ParseResults<NoArgs>
    | [<CliPrefix(CliPrefix.None)>] Clear of ParseResults<NoArgs>
    | [<CliPrefix(CliPrefix.None)>] Prune of ParseResults<NoArgs>

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Show _ ->
                "which session this is (the terminal, or TESS_SESSION), its state file and active connection "
                + "(the default)."
            | List _ -> "every session's state file, and whether its terminal is still there."
            | Clear _ -> "forget this session's active connection (the same as `tess connect --clear`)."
            | Prune _ -> "delete the state files of terminals that are gone."

let activeJson (a: ActiveConnection) =
    obj [ "model", str a.Model; "profile", optStr a.Profile; "since", str a.Since ]

let private show (ctx: Ctx) =
    match session ctx.GetEnv with
    | None ->
        Failed(
            NoActiveConnection
                "tess can't tell which terminal this is, or where its state goes (set TESS_STATE_DIR or HOME, or TESS_SESSION)"
        )
    | Some s ->
        if ctx.Json then
            writeJson
                ctx.Out
                (obj [
                    "session", str (SessionKey.describe s.Key)
                    "stateFile", str s.File
                    "active",
                    (match s.Active with
                     | Some a -> activeJson a
                     | None -> null)
                ])
        else
            ctx.Out.WriteLine($"Session     {SessionKey.describe s.Key}")
            ctx.Out.WriteLine($"State file  {s.File}")

            match s.Active with
            | Some a ->
                let from =
                    a.Profile |> Option.map (fun p -> $"  (profile {p})") |> Option.defaultValue ""

                ctx.Out.WriteLine($"Active      {a.Model}{from}")
            | None -> ctx.Out.WriteLine("Active      (none)")

        Done

let run (ctx: Ctx) (args: ParseResults<Args>) =
    match args.TryGetSubCommand() with
    | None
    | Some(Show _) -> show ctx
    | Some(List _) ->
        let all = allSessions ctx.GetEnv

        if ctx.Json then
            writeJson
                ctx.Out
                (obj [
                    "sessions",
                    arr [
                        for file, model, stale in all ->
                            obj [ "stateFile", str file; "model", optStr model; "stale", bool stale ]
                    ]
                ])
        elif all.IsEmpty then
            ctx.Err.WriteLine("No sessions.")
        else
            for file, model, stale in all do
                let mark = if stale then "  (terminal gone)" else ""
                let shown = defaultArg model "(no model)"
                ctx.Out.WriteLine($"{Path.GetFileName file}  {shown}{mark}")

        Done
    | Some(Clear _) ->
        match Connections.clear ctx.GetEnv with
        | Error e -> Failed e
        | Ok cleared ->
            if ctx.Json then
                writeJson ctx.Out (obj [ "cleared", bool cleared ])
            else
                ctx.Err.WriteLine(
                    if cleared then
                        "Cleared the active connection."
                    else
                        "No active connection."
                )

            Done
    | Some(Prune _) ->
        let stale = allSessions ctx.GetEnv |> List.filter (fun (_, _, s) -> s)

        let deleted = [
            for file, _, _ in stale do
                try
                    File.Delete file
                    yield file
                with _ ->
                    ()
        ]

        if ctx.Json then
            writeJson ctx.Out (obj [ "deleted", arr (deleted |> List.map str) ])
        else
            ctx.Err.WriteLine($"Deleted {deleted.Length} state file(s) of terminals that are gone.")

        Done
