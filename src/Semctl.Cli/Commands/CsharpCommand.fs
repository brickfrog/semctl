/// `sm csharp <model> <script.csx>`: run a C# script against the model's TOM objects, then show
/// what it changed like any editing command (dry run unless --save). The name is provisional
/// (`script` is DAX scripts). The script is a local file; it runs in-process with the user's rights
/// (docs/scripting-design.md). Its output goes to stderr, so --json stays clean.
module Semctl.Cli.CsharpCommand

open System
open Argu
open Semctl
open Semctl.Cli.Common

/// One-line description, shown in `sm --help`.
let summary =
    "run a C# script (.csx) against the model's TOM objects, then show its changes (dry run unless --save)."

type Args =
    | [<MainCommand; ExactlyOnce>] Target of model: string * script: string
    | Save
    | Save_To of path: string
    | Force
    | Stat
    | Name_Only

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Target _ ->
                "the model, then a local C# script file: top-level statements, with `Model` (the TOM model), "
                + "Info(), Warning(), Output() and Error() in scope. It runs with your rights; it is never fetched."
            | Save -> Mutation.Usage.save
            | Save_To _ -> Mutation.Usage.saveTo
            | Force -> Mutation.Usage.force
            | Stat -> Mutation.Usage.stat
            | Name_Only -> Mutation.Usage.nameOnly

let run (ctx: Ctx) (args: ParseResults<Args>) =
    let opts =
        Mutation.options
            (args.Contains Args.Save)
            (args.TryGetResult Args.Save_To)
            (args.Contains Args.Force)
            (args.Contains Args.Stat)
            (args.Contains Args.Name_Only)

    let model, script = args.GetResult Args.Target

    match opts with
    | Error message -> UsageError message
    | Ok _ when script.Contains "://" -> UsageError $"scripts are local files; sm never fetches one: {script}"
    | Ok opts ->
        let name = IO.Path.GetFileName script

        Mutation.run ctx "csharp" opts model (fun db ->
            Semctl.Scripting.CsScript.run db.Model script ctx.Err
            |> Result.map (fun () -> {
                Semctl.Mutation.Effect.none with
                    Notes = [ $"Ran {name}" ]
            }))
