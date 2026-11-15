module Tessera.Cli.SetCommand

open System
open Argu
open Tessera
open Tessera.Inventory
open Tessera.Cli.Common

/// One-line description, shown in `tess --help`.
let summary = "set properties on an object (dry run unless --save)."

type Args =
    | [<MainCommand; ExactlyOnce>] Target of model: string * object: string
    | [<AltCommandLine("-p")>] Property of assignment: string
    | Unset of path: string
    | Save
    | Save_To of path: string
    | Force
    | Stat
    | Name_Only
    | Update_Schema
    | Source of source: string
    | Drop_Removed_Columns

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Target _ -> "the model, and the object (any form `get` accepts)."
            | Property _ ->
                "<path>=<value> (repeatable), with the paths `get -p` prints, e.g. formatString=0.00, "
                + "isHidden=true, kpi.targetExpression=..., annotations.X=...; `null` clears, `-` reads stdin."
            | Unset _ -> "clear or reset a property (repeatable); the same as -p <path>=null."
            | Save -> Mutation.Usage.save
            | Save_To _ -> Mutation.Usage.saveTo
            | Force -> Mutation.Usage.force
            | Stat -> Mutation.Usage.stat
            | Name_Only -> Mutation.Usage.nameOnly
            | Update_Schema ->
                "instead of -p: bring the table's columns (or every table's, for `model`) in line with --source, "
                + "as `schemacheck --update` does."
            | Source _ -> "with --update-schema: a schema file, a fixture directory, or sql:<server>;<database>."
            | Drop_Removed_Columns -> "with --update-schema: also remove columns whose source column is gone."

/// The assignments, in command-line order. At most one value can come from stdin.
let private assignments (args: ParseResults<Args>) =
    let stdinUses =
        args.GetResults Args.Property
        |> List.filter (fun p -> p.EndsWith "=-")
        |> List.length

    if stdinUses > 1 then
        Error "only one -p value can be read from stdin (-)"
    else
        let stdin () = Console.In.ReadToEnd()

        args.GetAllResults()
        |> List.choose (function
            | Property text -> Some(SetProperty.Assignment.parse stdin text)
            | Unset path -> Some(Ok(SetProperty.Assignment.unset path))
            | _ -> None)
        |> List.fold
            (fun acc a ->
                match acc, a with
                | Ok xs, Ok x -> Ok(x :: xs)
                | Error e, _
                | _, Error e -> Error e)
            (Ok [])
        |> Result.bind (function
            | [] -> Error "nothing to set: pass -p <path>=<value> or --unset <path>"
            | xs -> Ok(List.rev xs))

/// `set <model> <table|model> --update-schema --source <src>`: schemacheck's update, for one table.
let private updateSchema (ctx: Ctx) (args: ParseResults<Args>) model selector opts =
    match opts, args.TryGetResult Args.Source with
    | Error message, _ -> UsageError message
    | _ when args.Contains Args.Property || args.Contains Args.Unset ->
        UsageError "--update-schema sets the columns from the source; it doesn't combine with -p or --unset"
    | _, None ->
        UsageError "--update-schema needs --source: a schema file, a fixture directory, or sql:<server>;<database>"
    | Ok opts, Some source ->
        let drop = args.Contains Args.Drop_Removed_Columns

        Mutation.run ctx "set" opts model (fun db ->
            Selector.resolve (objects db) selector
            |> Result.bind (fun target ->
                let update tables =
                    SchemaCheckSource.update ctx (SchemaCheckSource.live ctx) source tables drop db

                match target.Ref.Kind, target.Ref.Path with
                | ObjectKind.Table, [ table ] -> update [ table ]
                | ObjectKind.Model, _ -> update []
                | _ ->
                    Error(
                        ReadOnlyProperty(
                            ObjectRef.format target.Ref,
                            "schema",
                            "--update-schema takes a table, or `model` for every table"
                        )
                    )))

let run (ctx: Ctx) (args: ParseResults<Args>) =
    let model, selector = args.GetResult Args.Target

    let opts =
        Mutation.options
            (args.Contains Args.Save)
            (args.TryGetResult Args.Save_To)
            (args.Contains Args.Force)
            (args.Contains Args.Stat)
            (args.Contains Args.Name_Only)

    let schemaSwitches =
        [
            args.Contains Args.Source, "--source"
            args.Contains Args.Drop_Removed_Columns, "--drop-removed-columns"
        ]
        |> List.filter fst
        |> List.map snd

    if args.Contains Args.Update_Schema then
        updateSchema ctx args model selector opts
    elif not schemaSwitches.IsEmpty then
        let names = String.concat ", " schemaSwitches
        UsageError $"{names}: only with --update-schema"
    else
        match opts, assignments args with
        | Error message, _
        | _, Error message -> UsageError message
        | Ok opts, Ok assignments ->
            Mutation.run ctx "set" opts model (fun db ->
                Selector.resolve (objects db) selector
                |> Result.bind (fun target -> SetProperty.set db target assignments))
