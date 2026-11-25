/// JSON-RPC 2.0 over MCP's stdio transport (spec 2025-06-18, "Transports"): one JSON-RPC message per
/// line, UTF-8, no embedded newlines. Nothing but these frames may be written to the output: every
/// diagnostic goes to stderr.
module Semctl.Cli.Mcp.JsonRpc

open System
open System.IO
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

/// Compact (one line: JSON escapes every newline inside strings) and readable non-ASCII.
let private frameOptions =
    JsonSerializerOptions(WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

/// A node as one compact line of JSON.
let compact (node: JsonNode) =
    match node with
    | null -> "null"
    | n -> n.ToJsonString frameOptions

/// Standard JSON-RPC error codes.
module Code =
    let parseError = -32700
    let invalidRequest = -32600
    let methodNotFound = -32601
    let invalidParams = -32602
    let internalError = -32603

/// A message read from the client.
type Incoming =
    /// A request: it gets exactly one response with the same id.
    | Request of id: JsonNode * method: string * parameters: JsonNode
    /// A notification (no id): it gets no response.
    | Notification of method: string * parameters: JsonNode
    /// A response to a request the server sent. sm sends none, so these are ignored.
    | Response
    /// Not a JSON-RPC message: answered with this error (and a null id, or the id if there was one).
    | Invalid of id: JsonNode * code: int * message: string

let private copy (node: JsonNode) =
    match node with
    | null -> null
    | n -> n.DeepClone()

/// Read one line as a message.
let parse (line: string) : Incoming =
    let parsed =
        try
            Ok(JsonNode.Parse line)
        with e ->
            Error e.Message

    match parsed with
    | Error message -> Invalid(null, Code.parseError, $"parse error: {message}")
    | Ok(:? JsonArray) ->
        // MCP 2025-06-18 removed JSON-RPC batching.
        Invalid(null, Code.invalidRequest, "batches are not supported (MCP 2025-06-18): send one message per line")
    | Ok(:? JsonObject as o) ->
        let version =
            match o.["jsonrpc"] with
            | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> v.GetValue<string>()
            | _ -> null

        let mutable id: JsonNode = null
        let hasId = o.TryGetPropertyValue("id", &id)

        let idOk =
            match (id: JsonNode) with
            | :? JsonValue as v ->
                match v.GetValueKind() with
                | JsonValueKind.String
                | JsonValueKind.Number -> true
                | _ -> false
            | _ -> false

        let method =
            match o.["method"] with
            | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
            | _ -> None

        let parameters = copy o.["params"]

        if version <> "2.0" then
            Invalid((if idOk then copy id else null), Code.invalidRequest, "expected \"jsonrpc\": \"2.0\"")
        else
            match method with
            | Some m when not hasId -> Notification(m, parameters)
            | Some m when idOk -> Request(copy id, m, parameters)
            | Some _ -> Invalid(null, Code.invalidRequest, "a request's id must be a string or a number")
            | None when hasId && (o.ContainsKey "result" || o.ContainsKey "error") -> Response
            | None -> Invalid((if idOk then copy id else null), Code.invalidRequest, "missing \"method\"")
    | Ok _ -> Invalid(null, Code.invalidRequest, "expected a JSON object")

let result (id: JsonNode) (result: JsonNode) : JsonNode =
    let o = JsonObject()
    o.["jsonrpc"] <- JsonValue.Create "2.0"
    o.["id"] <- copy id
    o.["result"] <- result
    o

let error (id: JsonNode) (code: int) (message: string) : JsonNode =
    let e = JsonObject()
    e.["code"] <- JsonValue.Create code
    e.["message"] <- JsonValue.Create message
    let o = JsonObject()
    o.["jsonrpc"] <- JsonValue.Create "2.0"
    o.["id"] <- copy id
    o.["error"] <- e
    o

/// Write one frame: the message on one line, ended by "\n" (never the platform newline), flushed.
let write (out: TextWriter) (message: JsonNode) =
    out.Write(compact message)
    out.Write('\n')
    out.Flush()

/// The next non-blank line, without its line break ("\r\n" tolerated), or None at end of input.
let rec readLine (input: TextReader) : string option =
    match input.ReadLine() with
    | null -> None
    | line when String.IsNullOrWhiteSpace line -> readLine input
    | line -> Some(line.TrimEnd '\r')
