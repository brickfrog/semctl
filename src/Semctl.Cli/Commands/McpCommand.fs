/// `sm mcp <model>`: serve the model to an MCP client (an AI agent) over stdio. See Mcp/Server.fs.
module Semctl.Cli.McpCommand

open System
open System.IO
open System.Text
open Argu
open Semctl
open Semctl.Cli.Common
open Semctl.Cli.Mcp

/// One-line description, shown in `sm --help`.
let summary =
    "serve a model to an AI agent: an MCP server over stdio (tools by --allow; changes in memory unless --save)."

type Args =
    | [<MainCommand; ExactlyOnce>] Model of path: string
    | Allow of grants: string
    | Data of directory: string
    | Server of address: string
    | Save
    | Audit_Log of path: string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Model _ -> "the model to serve (any model path sm accepts, or xmla:<server>;<database>)."
            | Allow _ ->
                "what the agent may do (repeatable or comma-separated): read (default: metadata, search, "
                + "validate, lint, format), metadata-write (change the model in memory; implies read), "
                + "data-read (DAX query rows, with --data or --server), bpa (Best Practice Analyzer). "
                + "Tools outside the grants are not offered."
            | Data _ -> "with --allow data-read: answer queries with the local engine over this fixture directory."
            | Server _ ->
                "with --allow data-read: answer queries on this server model instead (xmla:<server>;<database>)."
            | Save -> "offer a save_model tool, which writes the changes back to the model's files."
            | Audit_Log _ ->
                "append one JSON line per tool call to this file: the time, the tool, the objects named and the "
                + "outcome. Never values, DAX or query results."

let private grantsOf (values: string list) : Result<Set<Server.Grant>, Outcome> =
    let tokens = parseList values

    match tokens |> List.tryFind (fun t -> (Server.Grant.tryParse t).IsNone) with
    | Some bad -> Error(usage (unknownValue "--allow" "grant" bad (Server.Grant.all |> List.map Server.Grant.token)))
    | None ->
        let grants =
            match tokens |> List.choose Server.Grant.tryParse with
            | [] -> [ Server.Grant.Read ]
            | gs -> gs

        let grants =
            if List.contains Server.Grant.MetadataWrite grants then
                Server.Grant.Read :: grants
            else
                grants

        Ok(Set.ofList grants)

let private dataSourceOf (grants: Set<Server.Grant>) (data: string option) (server: string option) =
    match grants.Contains Server.Grant.DataRead, data, server with
    | _, Some _, Some _ -> Error(UsageError "give --data or --server, not both")
    | false, Some _, _
    | false, _, Some _ -> Error(UsageError "--data and --server answer DAX queries: they need --allow data-read")
    | true, None, None ->
        Error(
            UsageError
                "--allow data-read needs where the rows come from: --data <fixture directory> (the local engine) or --server xmla:<server>;<database>"
        )
    | true, Some dir, None ->
        if Directory.Exists dir then
            Ok(Server.DataSource.Fixture dir)
        else
            Error(Failed(PathNotFound(Path.GetFullPath dir)))
    | true, None, Some address ->
        match XmlaAddress.parse address with
        | Ok a -> Ok(Server.DataSource.Server a)
        | Error e -> Error(Failed e)
    | false, None, None -> Ok Server.DataSource.NoData

let private openAudit (path: string) : Result<TextWriter, SemctlError> =
    try
        let stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)

        Ok(new StreamWriter(stream, UTF8Encoding(false)) :> TextWriter)
    with e ->
        Error(SaveFailed(path, e.Message))

/// Run the server. `input` is where frames come from: None for the process's stdin, in which case
/// frames go to the process's stdout as UTF-8 and everything else written to Console.Out is sent to
/// stderr while the server runs. Given an input (tests), frames go to ctx.Out.
let run (ctx: Ctx) (args: ParseResults<Args>) (input: TextReader option) : Outcome =
    let model = args.GetResult Args.Model

    let checkedOptions =
        grantsOf (args.GetResults Args.Allow)
        |> Result.bind (fun grants ->
            dataSourceOf grants (args.TryGetResult Args.Data) (args.TryGetResult Args.Server)
            |> Result.map (fun data -> grants, data))
        |> Result.bind (fun (grants, data) ->
            let save = args.Contains Args.Save

            if save && not (grants.Contains Server.Grant.MetadataWrite) then
                Error(UsageError "--save offers a save_model tool for changes: it needs --allow metadata-write")
            elif save && XmlaAddress.isRemote model then
                Error(
                    UsageError
                        "--save writes the model's files: a server model's changes stay in memory (`sm deploy` deploys files)"
                )
            else
                Ok(grants, data, save))

    match checkedOptions with
    | Error outcome -> outcome
    | Ok(grants, data, save) ->
        match Source.load ctx model with
        | Error e -> Failed e
        | Ok(source, db) ->
            let audit =
                match args.TryGetResult Args.Audit_Log with
                | None -> Ok None
                | Some path -> openAudit path |> Result.map Some

            match audit with
            | Error e -> Failed e
            | Ok audit ->
                use _ = audit |> Option.defaultValue TextWriter.Null

                let options: Server.Options = {
                    Grants = grants
                    Save = save
                    Data = data
                    Audit = audit
                }

                let session = Server.Session.create ctx source db options
                let tools = Server.registered options |> List.map _.Name |> String.concat ", "

                ctx.Err.WriteLine(
                    $"sm mcp: serving {source.Path} over stdio (MCP {Server.protocolVersion}); tools: {tools}"
                )

                match input with
                | Some reader -> Server.serve session reader ctx.Out
                | None ->
                    let utf8 = UTF8Encoding(false)
                    use stdin = new StreamReader(Console.OpenStandardInput(), utf8)
                    use stdout = new StreamWriter(Console.OpenStandardOutput(), utf8)
                    // Nothing else may reach stdout while frames do: send stray writes to stderr.
                    let original = Console.Out
                    Console.SetOut Console.Error

                    try
                        Server.serve session stdin stdout
                    finally
                        Console.SetOut original

                Done
