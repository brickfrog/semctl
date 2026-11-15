/// `tess profile`: named connection profiles in the user config (Connections): a model or a server
/// and database, a description, and behaviour overrides. Local files only; never secrets.
module Tessera.Cli.ProfileCommand

open System
open Argu
open Tessera
open Tessera.Cli.Output
open Tessera.Cli.Common
open Tessera.Cli.Connections

/// One-line description, shown in `tess --help`.
let summary =
    "named connection profiles (a model or server, behaviour overrides): set, list, show, remove."

type SetArgs =
    | [<MainCommand; ExactlyOnce>] Name of name: string
    | Model of path: string
    | Server of server: string
    | Database of name: string
    | Description of text: string
    | Setting of assignment: string
    | Unset_Setting of name: string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Name _ -> "the profile's name (letters, digits, '.', '_', '-'); an existing profile is updated."
            | Model _ -> "a local model (any path tess accepts); replaces --server/--database."
            | Server _ ->
                "a server, as `tess connect` takes it (powerbi://..., asazure://..., SSAS); replaces --model. "
                + "Never with credentials in it."
            | Database _ -> "the database on --server."
            | Description _ -> "what the profile is for."
            | Setting _ ->
                let names =
                    settings
                    |> Map.toList
                    |> List.map (fun (k, v) -> $"{k} ({v})")
                    |> String.concat ", "

                $"<name>=<value> (repeatable): a default for a TESS_* variable while the profile is used: {names}. "
                + "Secrets are never stored: credentials stay in the environment."
            | Unset_Setting _ -> "remove a setting (repeatable)."

type NameArgs =
    | [<MainCommand; ExactlyOnce>] Name of name: string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Name _ -> "the profile."

type ListArgs =
    | [<Hidden>] Placeholder

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Placeholder -> ""

type Args =
    | [<CliPrefix(CliPrefix.None)>] Set of ParseResults<SetArgs>
    | [<CliPrefix(CliPrefix.None)>] List of ParseResults<ListArgs>
    | [<CliPrefix(CliPrefix.None)>] Show of ParseResults<NameArgs>
    | [<CliPrefix(CliPrefix.None)>] Remove of ParseResults<NameArgs>

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Set _ -> "create or update a profile. Use it with `tess connect --profile <name>` or profile:<name>."
            | List _ -> "list the profiles."
            | Show _ -> "show a profile, and the variables its settings set."
            | Remove _ -> "remove a profile."

let profileJson (p: Profile) =
    obj [
        "name", str p.Name
        "model", optStr p.Model
        "server", optStr p.Server
        "database", optStr p.Database
        "description", optStr p.Description
        "modelArgument",
        (match Profile.modelArgument p with
         | Ok m -> str m
         | Error _ -> null)
        "settings", obj [ for KeyValue(k, v) in p.Settings -> k, str v ]
        "environment", obj [ for KeyValue(k, v) in Profile.environment p -> k, str v ]
    ]

let private set (ctx: Ctx) (args: ParseResults<SetArgs>) =
    let name = args.GetResult SetArgs.Name

    let assignments =
        args.GetResults SetArgs.Setting
        |> List.map (fun a ->
            match a.IndexOf '=' with
            | i when i > 0 -> Ok(a.Substring(0, i).Trim(), a.Substring(i + 1))
            | _ -> Error $"--setting {a}: expected <name>=<value>")

    match assignments |> List.tryPick (fun r -> if r.IsError then Some r else None) with
    | Some(Error message) -> UsageError message
    | _ when
        args.Contains SetArgs.Model
        && (args.Contains SetArgs.Server || args.Contains SetArgs.Database)
        ->
        UsageError "--model and --server/--database can't be combined: a profile names one model"
    | _ ->
        let existing =
            match tryProfile ctx.GetEnv name with
            | Ok p -> Ok p
            | Error(ProfileNotFound _) -> Ok(Profile.empty name)
            | Error e -> Error e

        match existing with
        | Error e -> Failed e
        | Ok p ->
            let p =
                match args.TryGetResult SetArgs.Model, args.TryGetResult SetArgs.Server with
                | Some m, _ -> {
                    p with
                        Model = Some m
                        Server = None
                        Database = None
                  }
                | None, Some s -> {
                    p with
                        Server = Some s
                        Model = None
                        Database = args.TryGetResult SetArgs.Database |> Option.orElse p.Database
                  }
                | None, None -> {
                    p with
                        Database = args.TryGetResult SetArgs.Database |> Option.orElse p.Database
                  }

            let p = {
                p with
                    Description = args.TryGetResult SetArgs.Description |> Option.orElse p.Description
                    Settings =
                        let added =
                            assignments
                            |> List.choose Result.toOption
                            |> List.fold (fun (m: Map<string, string>) (k, v) -> m.Add(k, v)) p.Settings

                        args.GetResults SetArgs.Unset_Setting
                        |> List.fold (fun m k -> Map.remove k m) added
            }

            match saveProfile ctx.GetEnv p with
            | Error e -> Failed e
            | Ok file ->
                if ctx.Json then
                    writeJson ctx.Out (obj [ "saved", str file; "profile", profileJson p ])
                else
                    ctx.Err.WriteLine($"Saved profile '{p.Name}' in {file}")

                Done

let private list (ctx: Ctx) =
    match profiles ctx.GetEnv with
    | Error e -> Failed e
    | Ok ps ->
        if ctx.Json then
            writeJson ctx.Out (obj [ "profiles", arr (ps |> List.map profileJson) ])
        elif ps.IsEmpty then
            ctx.Err.WriteLine("No profiles. `tess profile set <name> --model <path>` (or --server ...) makes one.")
        else
            let width = ps |> List.map (fun p -> p.Name.Length) |> List.fold max 0

            for p in ps do
                let model =
                    match Profile.modelArgument p with
                    | Ok m -> m
                    | Error _ -> "(no model)"

                let description =
                    p.Description |> Option.map (fun d -> "  " + d) |> Option.defaultValue ""

                ctx.Out.WriteLine($"{p.Name.PadRight width}  {model}{description}")

        Done

let private show (ctx: Ctx) (name: string) =
    match tryProfile ctx.GetEnv name with
    | Error e -> Failed e
    | Ok p ->
        if ctx.Json then
            writeJson ctx.Out (profileJson p)
        else
            let row (label: string) (value: string) =
                ctx.Out.WriteLine($"{label, -12} {value}")

            row "Profile" p.Name
            p.Description |> Option.iter (row "Description")
            p.Model |> Option.iter (row "Model")
            p.Server |> Option.iter (row "Server")
            p.Database |> Option.iter (row "Database")

            for KeyValue(k, v) in p.Settings do
                let var = Map.tryFind k settings |> Option.defaultValue "?"
                row "Setting" $"{k} = {v}  ({var})"

        Done

let run (ctx: Ctx) (args: ParseResults<Args>) =
    match args.TryGetSubCommand() with
    | Some(Set a) -> set ctx a
    | Some(List _) -> list ctx
    | Some(Show a) -> show ctx (a.GetResult NameArgs.Name)
    | Some(Remove a) ->
        let name = a.GetResult NameArgs.Name

        match removeProfile ctx.GetEnv name with
        | Error e -> Failed e
        | Ok false ->
            match profiles ctx.GetEnv with
            | Ok ps -> Failed(ProfileNotFound(name, ps |> List.map _.Name))
            | Error e -> Failed e
        | Ok true ->
            if ctx.Json then
                writeJson ctx.Out (obj [ "removed", str name ])
            else
                ctx.Err.WriteLine($"Removed profile '{name}'")

            Done
    | None -> UsageError(args.Parser.PrintUsage())
