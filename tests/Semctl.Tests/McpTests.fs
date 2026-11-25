/// `sm mcp`: the MCP server driven over in-memory streams through the real CLI entry point, and
/// once as a real process (stdout carries JSON-RPC frames only).
///
/// Expectations follow the MCP specification 2025-06-18: Lifecycle (initialize, version negotiation,
/// notifications/initialized), Transports/stdio (one message per line, nothing else on stdout),
/// Tools (tools/list, tools/call, isError results, -32602 for an unknown tool) and JSON-RPC 2.0
/// (-32700 parse error with a null id, -32600 invalid request, -32601 method not found; no batches).
module Semctl.Tests.McpTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open Semctl
open Semctl.Tests.Fixtures
open Semctl.Tests.CliHarness
open Semctl.Cli

type private Served = {
    Code: int
    /// Every frame, parsed.
    Frames: JsonElement list
    Out: string
    Err: string
}

/// Run `sm mcp <args>` with these lines as its input, to the end of input.
let private serveWith
    (connector: Semctl.Xmla.IXmlaConnector)
    (env: (string * string) list)
    (args: string list)
    (lines: string list)
    =
    use input = new StringReader(String.concat "\n" lines + "\n")
    use out = new StringWriter()
    use err = new StringWriter()

    let code =
        App.runWithInput
            (Some input)
            connector
            (fun name -> List.tryPick (fun (k, v) -> if k = name then Some v else None) env)
            out
            err
            (Array.ofList ("mcp" :: args))

    let text = out.ToString()

    let frames = [
        for line in text.Split('\n') do
            if line <> "" then
                use doc = JsonDocument.Parse line
                yield doc.RootElement.Clone()
    ]

    {
        Code = code
        Frames = frames
        Out = text
        Err = err.ToString()
    }

let private serve args lines =
    serveWith (XmlaFakes.NoConnections()) [] args lines

let private compact (o: obj) = JsonSerializer.Serialize o

let private request (id: int) (method: string) (parameters: obj) =
    compact (
        dict [
            "jsonrpc", box "2.0"
            "id", box id
            "method", box method
            "params", parameters
        ]
    )

let private initialize =
    request
        0
        "initialize"
        (dict [
            "protocolVersion", box "2025-06-18"
            "capabilities", box (dict [])
            "clientInfo", box (dict [ "name", "test"; "version", "1" ])
        ])

let private initialized =
    """{"jsonrpc":"2.0","method":"notifications/initialized"}"""

let private callTool (id: int) (name: string) (arguments: obj) =
    request id "tools/call" (dict [ "name", box name; "arguments", arguments ])

let private noArgs = box (dict<string, obj> [])

/// The frame answering request `id`.
let private answer (s: Served) (id: int) =
    match
        s.Frames
        |> List.tryFind (fun f ->
            match f.TryGetProperty "id" with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32() = id
            | _ -> false)
    with
    | Some f -> f
    | None -> failtestf "no answer to request %d:\n%s\nstderr:\n%s" id s.Out s.Err

/// A tools/call result's structured content, and whether it is an error.
let private toolResult (s: Served) (id: int) =
    let r = (answer s id).GetProperty "result"
    // The text content is the same JSON as the structured content.
    let text = r.GetProperty("content").[0].GetProperty("text").GetString()
    let structured = r.GetProperty "structuredContent"

    Expect.equal
        (JsonNode.Parse(text).ToJsonString())
        (JsonNode.Parse(structured.GetRawText()).ToJsonString())
        "text = structured"

    structured, r.GetProperty("isError").GetBoolean()

let private ok (s: Served) id =
    let content, isError = toolResult s id
    Expect.isFalse isError (content.GetRawText())
    content

let private failed (s: Served) id =
    let content, isError = toolResult s id
    Expect.isTrue isError (content.GetRawText())
    content.GetProperty "error"

let private toolNames (s: Served) id = [
    for t in (answer s id).GetProperty("result").GetProperty("tools").EnumerateArray() ->
        t.GetProperty("name").GetString()
]

let private readTools = [
    "model_overview"
    "list_objects"
    "search_model"
    "get_objects"
    "list_properties"
    "get_dependencies"
    "validate_model"
    "lint_dax"
    "format_dax"
    "get_full_model"
]

/// A project with a fixture: Sales has 600 rows, each with a sentinel string in one column.
let private withData () =
    let t = project ()
    let data = Path.Combine(t.Path, "data")
    Directory.CreateDirectory data |> ignore

    write
        (Path.Combine(data, "Sales.csv"))
        ("Amount,DateKey,Path/With\\Slashes\n"
         + String.concat "" [ for i in 1..600 -> $"{i},1,SENTINEL-ROW-{i}\n" ])

    write (Path.Combine(data, "Date.csv")) "DateKey,Year,Month\n1,2024,Jan\n"
    t, data

/// Copy a directory tree (a model to edit without touching the original).
let rec private copyTree (from: string) (``to``: string) =
    Directory.CreateDirectory ``to`` |> ignore

    for f in Directory.GetFiles from do
        File.Copy(f, Path.Combine(``to``, Path.GetFileName f))

    for d in Directory.GetDirectories from do
        if Path.GetFileName d <> ".git" then
            copyTree d (Path.Combine(``to``, Path.GetFileName d))

/// Every file under a folder, relative path -> bytes. A .platform file written for the first time
/// gets a new random logicalId on every save, so that one value is blanked.
let private filesOf (root: string) =
    let bytes (f: string) =
        if Path.GetFileName f = ".platform" then
            Text.RegularExpressions.Regex.Replace(
                File.ReadAllText f,
                "\"logicalId\": \"[^\"]*\"",
                "\"logicalId\": \"\""
            )
            |> Text.Encoding.UTF8.GetBytes
        else
            File.ReadAllBytes f

    Directory.GetFiles(root, "*", SearchOption.AllDirectories)
    |> Array.map (fun f -> Path.GetRelativePath(root, f).Replace('\\', '/'), bytes f)
    |> Array.sortBy fst
    |> List.ofArray

/// The same edit, saved by `sm set --save` in one copy and by save_model in another: the files
/// must come out byte for byte the same (the session's model is a clone of the loaded one).
let private sameAsSet (model: string) (selector: string) (path: string) (value: string) =
    use t = new TempDir()
    let a = t.Combine "a"
    let b = t.Combine "b"

    let copyModel destination =
        Directory.CreateDirectory destination |> ignore

        let copy =
            Path.Combine(destination, Path.GetFileName(Path.TrimEndingDirectorySeparator model))

        if File.Exists model then
            File.Copy(model, copy)
        else
            copyTree model copy

        copy

    let cliModel = copyModel a
    let sessionModel = copyModel b
    let viaSet = semctl [ "set"; cliModel; selector; "-p"; $"{path}={value}"; "--save" ]
    Expect.equal viaSet.Code 0 viaSet.Err

    let s =
        serve [ sessionModel; "--allow"; "metadata-write"; "--save" ] [
            callTool
                1
                "apply_changes"
                (dict [
                    "operations",
                    box [|
                        box (
                            dict [
                                "op", box "set"
                                "object", box selector
                                "properties", box (dict [ path, value ])
                            ]
                        )
                    |]
                ])
            callTool 2 "save_model" noArgs
        ]

    ok s 1 |> ignore
    ok s 2 |> ignore
    let expected = filesOf a
    let actual = filesOf b
    Expect.equal (actual |> List.map fst) (expected |> List.map fst) "the same files"

    for (name, x), (_, y) in List.zip expected actual do
        Expect.isTrue (x = y) $"{name}: byte-identical"

    actual

[<Tests>]
let tests =
    testList "sm mcp" [
        testList "protocol" [
            testCase "initialize: the version, the tools capability, server info; stdout is frames only"
            <| fun () ->
                use t = project ()
                let s = serve [ t.Path ] [ initialize; initialized; request 1 "ping" null ]
                Expect.equal s.Code 0 s.Err
                // Two requests, two frames: the notification is not answered.
                Expect.equal s.Frames.Length 2 s.Out
                Expect.isTrue (s.Out.EndsWith "\n") "each frame ends with a newline"
                Expect.isFalse (s.Out.Contains "\r") "\\n, never the platform newline"

                for f in s.Frames do
                    Expect.equal (f.GetProperty("jsonrpc").GetString()) "2.0" ""

                let r = (answer s 0).GetProperty "result"
                Expect.equal (r.GetProperty("protocolVersion").GetString()) "2025-06-18" ""
                Expect.equal (r.GetProperty("serverInfo").GetProperty("name").GetString()) "sm" ""
                Expect.isTrue (r.GetProperty("capabilities").TryGetProperty("tools") |> fst) ""
                Expect.equal ((answer s 1).GetProperty("result").GetRawText()) "{}" "ping"
                Expect.stringContains s.Err "sm mcp: serving" "the banner goes to stderr"

            testCase "version negotiation: an older version it accepts is echoed; an unknown one gets 2025-06-18"
            <| fun () ->
                use t = project ()

                let init id (v: string) =
                    request id "initialize" (dict [ "protocolVersion", box v; "capabilities", box (dict []) ])

                let s = serve [ t.Path ] [ init 1 "2025-03-26"; init 2 "1999-01-01" ]

                let version id =
                    (answer s id).GetProperty("result").GetProperty("protocolVersion").GetString()

                Expect.equal (version 1) "2025-03-26" ""
                Expect.equal (version 2) "2025-06-18" ""

            testCase "JSON-RPC errors: parse error, batch, no jsonrpc, unknown method; notifications unanswered"
            <| fun () ->
                use t = project ()

                let s =
                    serve [ t.Path ] [
                        "{not json"
                        """[{"jsonrpc":"2.0","id":5,"method":"ping"}]"""
                        """{"id":6,"method":"ping"}"""
                        request 7 "resources/list" null
                        """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}}"""
                        ""
                        request 8 "ping" null + "\r"
                    ]

                let errorOf (f: JsonElement) =
                    f.GetProperty("error").GetProperty("code").GetInt32()

                Expect.equal s.Frames.Length 5 s.Out
                Expect.equal (errorOf s.Frames.[0]) -32700 "parse error"
                Expect.equal (s.Frames.[0].GetProperty("id").ValueKind) JsonValueKind.Null "with a null id"
                Expect.equal (errorOf s.Frames.[1]) -32600 "no batches in 2025-06-18"
                Expect.equal (errorOf s.Frames.[2]) -32600 "jsonrpc 2.0 is required"
                Expect.equal (errorOf (answer s 7)) -32601 "method not found"
                Expect.equal ((answer s 8).GetProperty("result").GetRawText()) "{}" "CRLF input is fine"

            testCase "tools/call with malformed params is -32602; a string id is echoed"
            <| fun () ->
                use t = project ()

                let s =
                    serve [ t.Path ] [
                        """{"jsonrpc":"2.0","id":"a","method":"tools/call","params":{"arguments":{}}}"""
                        """{"jsonrpc":"2.0","id":"b","method":"tools/call","params":{"name":"model_overview","arguments":[1]}}"""
                    ]

                for f in s.Frames do
                    Expect.equal (f.GetProperty("error").GetProperty("code").GetInt32()) -32602 ""

                Expect.equal [ for f in s.Frames -> f.GetProperty("id").GetString() ] [ "a"; "b" ] ""
        ]

        testList "tiers" [
            testCase "tools/list per grant: read by default; each grant adds its tools"
            <| fun () ->
                let t, data = withData ()
                use _ = t

                let list args =
                    toolNames (serve (t.Path :: args) [ request 1 "tools/list" null ]) 1

                Expect.equal (list []) readTools "read"
                Expect.equal (list [ "--allow"; "read" ]) readTools "read, explicitly"

                Expect.equal
                    (list [ "--allow"; "metadata-write" ])
                    (readTools @ [ "apply_changes" ])
                    "write implies read"

                Expect.equal
                    (list [ "--allow"; "metadata-write"; "--save" ])
                    (readTools @ [ "apply_changes"; "save_model" ])
                    "save_model only with --save"

                Expect.equal (list [ "--allow"; "bpa" ]) [ "list_bpa_rules"; "run_bpa" ] "bpa alone"
                Expect.equal (list [ "--allow"; "data-read"; "--data"; data ]) [ "run_query" ] "data alone"

                Expect.equal
                    (list [ "--allow"; "read,bpa"; "--allow"; "data-read"; "--data"; data ])
                    (readTools @ [ "list_bpa_rules"; "run_bpa"; "run_query" ])
                    "comma-separated and repeated"

            testCase "an ungranted tool fails exactly like a made-up one"
            <| fun () ->
                use t = project ()

                let s =
                    serve [ t.Path ] [
                        callTool 1 "apply_changes" (dict [ "operations", box [| dict [ "op", "set" ] |] ])
                        callTool 2 "run_query" (dict [ "dax", "EVALUATE Sales" ])
                        callTool 3 "save_model" noArgs
                        callTool 4 "no_such_tool" noArgs
                    ]

                let error id =
                    let e = (answer s id).GetProperty "error"
                    e.GetProperty("code").GetInt32(), e.GetProperty("message").GetString()

                Expect.equal (error 1) (-32602, "Unknown tool: apply_changes") ""
                Expect.equal (error 2) (-32602, "Unknown tool: run_query") ""
                Expect.equal (error 3) (-32602, "Unknown tool: save_model") ""
                Expect.equal (error 4) (-32602, "Unknown tool: no_such_tool") ""

            testCase "option errors go to stderr, even with --json; stdout stays empty"
            <| fun () ->
                use t = project ()

                for args, code in
                    [
                        [ t.Path; "--allow"; "everything"; "--json" ], 2
                        [ t.Path; "--allow"; "data-read" ], 2
                        [ t.Path; "--data"; t.Path ], 2
                        [ t.Path; "--save" ], 2
                        [ t.Path; "--bogus" ], 2
                        [ Path.Combine(t.Path, "missing.bim"); "--json" ], 3
                    ] do
                    let s = serve args [ initialize ]
                    Expect.equal s.Code code (String.concat " " args + "\n" + s.Err)
                    Expect.equal s.Out "" (String.concat " " args)
                    Expect.isNonEmpty s.Err ""

                let unknown = serve [ t.Path; "--allow"; "metdata-write" ] []
                Expect.stringContains unknown.Err "did you mean 'metadata-write'" ""

                let help = serve [ "--help" ] []
                Expect.equal help.Code 0 ""
                Expect.equal help.Out "" "help goes to stderr too"
                Expect.stringContains help.Err "--allow" ""
        ]

        testList "reads" [
            testCase "overview, list with conditions, detail, properties, dependencies"
            <| fun () ->
                use t = project ()

                let s =
                    serve [ t.Path ] [
                        callTool 1 "model_overview" noArgs
                        callTool
                            2
                            "list_objects"
                            (dict [ "kinds", box [| "measure" |]; "where", box [| "isHidden=false" |] ])
                        callTool
                            3
                            "get_objects"
                            (dict [ "objects", box [| "[Total]"; "Nope" |]; "properties", box [| "expression" |] ])
                        callTool 4 "list_properties" (dict [ "object", "[Total]" ])
                        callTool
                            5
                            "get_dependencies"
                            (dict [ "object", box "Sales[Amount]"; "direction", box "referencedBy" ])
                        callTool 6 "search_model" (dict [ "text", "sum(" ])
                        callTool 7 "get_objects" (dict [ "objects", box [| "Sales" |]; "format", box "tmdl" ])
                    ]

                let overview = ok s 1

                Expect.equal
                    [
                        for t in overview.GetProperty("tables").EnumerateArray() -> t.GetProperty("name").GetString()
                    ]
                    [ "Sales"; "Date"; "Time Intelligence" ]
                    ""

                Expect.equal
                    (overview.GetProperty("relationships").[0].GetProperty("endpoints").GetString())
                    "Sales[DateKey] -> Date[DateKey]"
                    ""

                let listed = ok s 2

                Expect.equal
                    [
                        for o in listed.GetProperty("objects").EnumerateArray() -> o.GetProperty("ref").GetString()
                    ]
                    [ "measure:Sales/Total" ]
                    ""

                Expect.equal (listed.GetProperty("total").GetInt32()) 1 ""

                let objects = (ok s 3).GetProperty("objects")

                Expect.equal
                    (objects.[0].GetProperty("properties").GetProperty("expression").GetString())
                    "SUM(Sales[Amount])"
                    ""

                Expect.equal
                    (objects.[1].GetProperty("error").GetProperty("code").GetString())
                    "object-not-found"
                    "per object"

                let props = (ok s 4).GetProperty("properties")

                Expect.exists
                    [ for p in props.EnumerateArray() -> p.GetProperty("property").GetString() ]
                    ((=) "formatString")
                    ""

                let deps = ok s 5

                Expect.equal
                    (deps.GetProperty("referencedBy").[0].GetProperty("ref").GetString())
                    "measure:Sales/Total"
                    ""

                Expect.equal ((ok s 6).GetProperty("hits").[0].GetProperty("ref").GetString()) "measure:Sales/Total" ""

                Expect.stringContains
                    ((ok s 7).GetProperty("objects").[0].GetProperty("tmdl").GetString())
                    "table Sales"
                    ""

            testCase "a bad argument is a tool error the agent sees, not a protocol error"
            <| fun () ->
                use t = project ()

                let s =
                    serve [ t.Path ] [
                        callTool 1 "list_properties" noArgs
                        callTool 2 "list_objects" (dict [ "kinds", box [| "mesure" |] ])
                        callTool 3 "list_properties" (dict [ "object", "[Totl]" ])
                    ]

                Expect.equal ((failed s 1).GetProperty("code").GetString()) "invalid-arguments" ""
                Expect.stringContains ((failed s 2).GetProperty("message").GetString()) "did you mean 'measure'" ""
                let notFound = failed s 3
                Expect.equal (notFound.GetProperty("code").GetString()) "object-not-found" ""

                Expect.equal
                    [
                        for x in notFound.GetProperty("suggestions").EnumerateArray() -> x.GetString()
                    ]
                    [ "measure:Sales/Total" ]
                    ""

            testCase "validate, lint and format"
            <| fun () ->
                use t = project ()

                let s =
                    serve [ t.Path ] [
                        callTool 1 "validate_model" noArgs
                        callTool 2 "lint_dax" noArgs
                        callTool 3 "format_dax" (dict [ "dax", "sum(Sales[Amount])" ])
                        callTool 4 "format_dax" (dict [ "object", "[Total]" ])
                    ]

                Expect.equal ((ok s 1).GetProperty("command").GetString()) "validate" ""
                Expect.equal ((ok s 2).GetProperty("command").GetString()) "lint" ""
                Expect.equal ((ok s 3).GetProperty("formatted").GetString()) "SUM ( Sales[Amount] )" ""
                Expect.equal ((ok s 4).GetProperty("ref").GetString()) "measure:Sales/Total" ""

            testCase "validate_model runs the syntax-tree rules too, as `sm validate` does"
            <| fun () ->
                use t = project ()
                let model = Path.Combine(t.Path, "Sales.SemanticModel")

                let add =
                    CliHarness.semctl [
                        "add"
                        model
                        "measure:Sales/Bad"
                        "--expression"
                        "COUNTROWS ( { IGNORE ( 1 ) } )"
                        "--save"
                        "--force"
                    ]

                Expect.equal add.Code 0 add.Err

                let s = serve [ t.Path ] [ callTool 1 "validate_model" noArgs ]

                let codes = [
                    for f in (ok s 1).GetProperty("findings").EnumerateArray() -> f.GetProperty("code").GetString()
                ]

                Expect.contains codes "ignore-outside-summarizecolumns" ""

            testCase "save_model counts the syntax-tree rules' errors"
            <| fun () ->
                use t = project ()

                let s =
                    serve [ t.Path; "--allow"; "metadata-write"; "--save" ] [
                        callTool
                            1
                            "apply_changes"
                            (dict [
                                "operations",
                                box [|
                                    box (
                                        dict [
                                            "op", box "set"
                                            "object", box "[Total]"
                                            "properties", box (dict [ "expression", "COUNTROWS ( { IGNORE ( 1 ) } )" ])
                                        ]
                                    )
                                |]
                            ])
                        callTool 2 "save_model" noArgs
                    ]

                let introduced = (ok s 1).GetProperty("introducedErrors")
                Expect.equal (introduced.GetArrayLength()) 1 ""
                Expect.equal (introduced.[0].GetProperty("code").GetString()) "ignore-outside-summarizecolumns" ""
                Expect.equal ((failed s 2).GetProperty("code").GetString()) "change-introduces-errors" ""

            testCase "bpa: rules and findings; never a URL"
            <| fun () ->
                use t = project ()

                let s =
                    serve [ t.Path; "--allow"; "bpa" ] [
                        callTool 1 "list_bpa_rules" noArgs
                        callTool 2 "run_bpa" noArgs
                    ]

                Expect.isGreaterThan ((ok s 1).GetProperty("rules").GetArrayLength()) 10 ""
                let bpa = ok s 2
                Expect.equal (bpa.GetProperty("command").GetString()) "bpa" ""
                Expect.isGreaterThan (bpa.GetProperty("rulesEvaluated").GetInt32()) 10 ""
        ]

        testList "writes" [
            testCase "a batch: changes, the errors it introduced and resolved; later reads see it"
            <| fun () ->
                use t = project ()

                let s =
                    serve [ t.Path; "--allow"; "metadata-write" ] [
                        callTool
                            1
                            "apply_changes"
                            (dict [
                                "operations",
                                box [|
                                    box (
                                        dict [
                                            "op", box "set"
                                            "object", box "[Total]"
                                            "properties", box (dict [ "formatString", box "0.0"; "isHidden", box true ])
                                        ]
                                    )
                                    box (
                                        dict [
                                            "op", "add"
                                            "kind", "measure"
                                            "table", "Sales"
                                            "name", "Broken"
                                            "expression", "SUM(Nope[x])"
                                        ]
                                    )
                                |]
                            ])
                        callTool
                            2
                            "apply_changes"
                            (dict [
                                "operations",
                                box [|
                                    box (
                                        dict [
                                            "op", box "set"
                                            "object", box "[Broken]"
                                            "properties", box (dict [ "expression", "1" ])
                                        ]
                                    )
                                    box (dict [ "op", "rename"; "object", "[Total]"; "newName", "Sum of Amount" ])
                                |]
                            ])
                        callTool
                            3
                            "get_objects"
                            (dict [
                                "objects", box [| "[Total (hidden)]" |]
                                "properties", box [| "expression" |]
                            ])
                        callTool 4 "model_overview" noArgs
                    ]

                let first = ok s 1
                let changes = first.GetProperty("changes")

                Expect.equal
                    [ for c in changes.EnumerateArray() -> c.GetProperty("changeKind").GetString() ]
                    [ "modified"; "created" ]
                    ""

                let introduced = first.GetProperty("introducedErrors")
                Expect.equal (introduced.GetArrayLength()) 1 ""
                Expect.equal (introduced.[0].GetProperty("objectPath").GetString()) "measure:Sales/Broken" ""
                Expect.equal (first.GetProperty("resolvedErrors").GetArrayLength()) 0 ""
                Expect.stringContains (first.GetProperty("saved").GetString()) "in memory" ""

                let second = ok s 2
                Expect.equal (second.GetProperty("introducedErrors").GetArrayLength()) 0 ""
                let resolved = second.GetProperty("resolvedErrors")
                Expect.equal (resolved.GetArrayLength()) 1 "the broken measure is fixed"
                Expect.equal (resolved.[0].GetProperty("code").GetString()) "unresolved-reference" ""
                Expect.equal (second.GetProperty("errorsIntroducedSinceLoad").GetInt32()) 0 ""

                // The rename fixed the DAX that refers to the measure.
                Expect.equal
                    ((ok s 3).GetProperty("objects").[0].GetProperty("properties").GetProperty("expression").GetString())
                    "[Sum of Amount]"
                    ""

                // [Total] and [Broken] twice, and [Total (hidden)], whose DAX the rename fixed.
                Expect.equal ((ok s 4).GetProperty("unsavedChanges").GetInt32()) 5 ""
                // Nothing written without --save.
                Expect.equal (semctl [ "get"; t.Path; "[Total]"; "-p"; "formatString" ]).Out "#,0\n" ""

            testCase "all or nothing: a failing operation leaves the model exactly as it was"
            <| fun () ->
                use t = project ()

                let s =
                    serve [ t.Path; "--allow"; "metadata-write" ] [
                        callTool 1 "get_full_model" noArgs
                        callTool
                            2
                            "apply_changes"
                            (dict [
                                "operations",
                                box [|
                                    box (
                                        dict [
                                            "op", box "set"
                                            "object", box "[Total]"
                                            "properties", box (dict [ "description", "changed" ])
                                        ]
                                    )
                                    box (dict [ "op", box "remove"; "objects", box [| "Sales[Amount]" |] ])
                                    box (
                                        dict [
                                            "op", box "set"
                                            "object", box "[Total]"
                                            "properties", box (dict [ "summarizeBy", "sum" ])
                                        ]
                                    )
                                |]
                            ])
                        callTool 3 "apply_changes" (dict [ "operations", box [| box (dict [ "op", "explode" ]) |] ])
                        callTool 4 "get_full_model" noArgs
                        callTool 5 "model_overview" noArgs
                    ]

                let error = failed s 2
                Expect.equal (error.GetProperty("operation").GetInt32()) 1 "remove is refused: [Total] refers to it"
                Expect.stringContains (error.GetProperty("message").GetString()) "nothing was changed" ""
                let bad = failed s 3
                Expect.equal (bad.GetProperty("code").GetString()) "invalid-arguments" ""
                Expect.equal (bad.GetProperty("operation").GetInt32()) 0 ""

                Expect.equal
                    ((ok s 4).GetProperty("tmdl").GetString())
                    ((ok s 1).GetProperty("tmdl").GetString())
                    "byte-identical"

                Expect.equal ((ok s 5).GetProperty("unsavedChanges").GetInt32()) 0 ""

            testCase "save_model writes what `sm set --save` writes: PBIP, and .bim with a password"
            <| fun () ->
                use p = project ()
                sameAsSet p.Path "[Total]" "description" "Ünïcode ✓" |> ignore

                use bim = new TempDir()
                let db = sampleDatabase ()

                db.Model.DataSources.Add(
                    new Microsoft.AnalysisServices.Tabular.ProviderDataSource(
                        Name = "SQL",
                        ConnectionString = "Provider=SQLOLEDB;Data Source=x;User ID=u;Password=s3cret",
                        Password = "s3cret"
                    )
                )

                let opts = {
                    ModelIO.SaveOptions.defaults with
                        IncludeSensitive = true
                }

                write (bim.Combine "Model.bim") (ModelIO.toBimText opts db)
                let savedFiles = sameAsSet (bim.Combine "Model.bim") "[Total]" "formatString" "0.00"

                let saved =
                    savedFiles
                    |> List.find (fst >> (=) "Model.bim")
                    |> snd
                    |> Text.Encoding.UTF8.GetString

                Expect.stringContains saved "s3cret" "kept, as `set --save` keeps it"

            testCase "save_model writes what `sm set --save` writes: the sample model (SEMCTL_SAMPLE_MODEL)"
            <| fun () ->
                match Environment.GetEnvironmentVariable "SEMCTL_SAMPLE_MODEL" with
                | null
                | "" -> skiptest "SEMCTL_SAMPLE_MODEL is not set"
                | sample ->
                    match ModelSource.resolve sample with
                    | Error e -> failtest (SemctlError.message e)
                    | Ok source ->
                        let model = defaultArg source.SemanticModelFolder source.Path
                        sameAsSet model "[Revenue]" "description" "changed by a test" |> ignore

            testCase "save_model writes the files; refused while the session introduced errors, unless forced"
            <| fun () ->
                use t = project ()

                let s =
                    serve [ t.Path; "--allow"; "metadata-write"; "--save" ] [
                        callTool 1 "save_model" noArgs
                        callTool
                            2
                            "apply_changes"
                            (dict [
                                "operations",
                                box [|
                                    box (
                                        dict [
                                            "op", box "set"
                                            "object", box "[Total]"
                                            "properties", box (dict [ "expression", "SUM(Nope[x])" ])
                                        ]
                                    )
                                |]
                            ])
                        callTool 3 "save_model" noArgs
                        callTool
                            4
                            "apply_changes"
                            (dict [
                                "operations",
                                box [|
                                    box (
                                        dict [
                                            "op", box "set"
                                            "object", box "[Total]"
                                            "properties", box (dict [ "expression", "SUM(Sales[Amount]) * 2" ])
                                        ]
                                    )
                                |]
                            ])
                        callTool 5 "save_model" noArgs
                    ]

                Expect.isFalse ((ok s 1).GetProperty("saved").GetBoolean()) "nothing to save"
                Expect.equal ((ok s 2).GetProperty("errorsIntroducedSinceLoad").GetInt32()) 1 ""
                Expect.equal ((failed s 3).GetProperty("code").GetString()) "change-introduces-errors" ""
                Expect.isTrue ((ok s 5).GetProperty("saved").GetBoolean()) ""

                Expect.equal
                    (semctl [ "get"; t.Path; "[Total]"; "-p"; "expression" ]).Out
                    "SUM(Sales[Amount]) * 2\n"
                    "on disk"

                let forced =
                    serve [ t.Path; "--allow"; "metadata-write"; "--save" ] [
                        callTool
                            1
                            "apply_changes"
                            (dict [
                                "operations",
                                box [|
                                    box (
                                        dict [
                                            "op", box "set"
                                            "object", box "[Total]"
                                            "properties", box (dict [ "expression", "SUM(Nope[x])" ])
                                        ]
                                    )
                                |]
                            ])
                        callTool 2 "save_model" (dict [ "force", true ])
                    ]

                Expect.isTrue ((ok forced 2).GetProperty("saved").GetBoolean()) ""
                Expect.equal (semctl [ "get"; t.Path; "[Total]"; "-p"; "expression" ]).Out "SUM(Nope[x])\n" ""
        ]

        testList "queries" [
            testCase "rows: 100 by default, at most 500, and the result says it was cut"
            <| fun () ->
                let t, data = withData ()
                use _ = t

                let s =
                    serve [ t.Path; "--allow"; "data-read"; "--data"; data ] [
                        callTool 1 "run_query" (dict [ "dax", "EVALUATE Sales" ])
                        callTool 2 "run_query" (dict [ "dax", box "EVALUATE Sales"; "maxRows", box 1000 ])
                        callTool
                            3
                            "run_query"
                            (dict [ "dax", box "EVALUATE ROW(\"n\", COUNTROWS(Sales))"; "maxRows", box 5 ])
                        callTool 4 "run_query" (dict [ "dax", box "EVALUATE Sales"; "maxRows", box 0 ])
                        callTool 5 "run_query" (dict [ "dax", "EVALUATE Nope" ])
                    ]

                let rows (r: JsonElement) =
                    r.GetProperty("results").[0].GetProperty("rows").GetArrayLength()

                let first = ok s 1
                Expect.equal (rows first) 100 ""
                Expect.isTrue (first.GetProperty("truncated").GetBoolean()) ""
                Expect.stringContains (first.GetProperty("note").GetString()) "maxRows" ""
                let capped = ok s 2
                Expect.equal (rows capped) 500 "never more than 500"
                Expect.equal (capped.GetProperty("maxRows").GetInt32()) 500 ""
                Expect.isTrue (capped.TryGetProperty("maxRowsCapped") |> fst) ""
                let small = ok s 3
                Expect.equal (rows small) 1 ""
                Expect.isFalse (small.GetProperty("truncated").GetBoolean()) ""
                Expect.equal ((failed s 4).GetProperty("code").GetString()) "invalid-arguments" "0 is not 'all'"
                Expect.isNonEmpty ((failed s 5).GetProperty("code").GetString()) ""

            testCase "on a server named with --server: capped while reading"
            <| fun () ->
                use t = project ()
                let server = XmlaFakes.FakeServer()
                let remoteDb = sampleDatabase ()
                remoteDb.Name <- "Sales"
                server.Add remoteDb

                server.OnQuery <-
                    fun _ -> [
                        {
                            Columns = [ { Name = "[n]"; Type = QueryType.Int64 } ]
                            Rows = [ for i in 1..800 -> [| QueryValue.Int64(int64 i) |] ]
                            Truncated = false
                        }
                    ]

                let s =
                    serveWith server XmlaFakes.tokenEnv [
                        t.Path
                        "--allow"
                        "data-read"
                        "--server"
                        $"xmla:{XmlaFakes.workspace};Sales"
                    ] [
                        callTool 1 "run_query" (dict [ "dax", box "EVALUATE x"; "maxRows", box 700 ])
                    ]

                Expect.equal ((ok s 1).GetProperty("results").[0].GetProperty("rows").GetArrayLength()) 500 s.Err
                Expect.equal (server.Queries |> List.map (fun (_, _, limit) -> limit)) [ Some 500 ] "asked for 500 rows"
        ]

        testList "audit log" [
            testCase "one line per call: tool, objects, outcome; never values, DAX or query results"
            <| fun () ->
                let t, data = withData ()
                use _ = t
                let log = Path.Combine(t.Path, "audit.jsonl")

                let s =
                    serve [
                        t.Path
                        "--allow"
                        "metadata-write,data-read"
                        "--data"
                        data
                        "--audit-log"
                        log
                    ] [
                        callTool 1 "get_objects" (dict [ "objects", box [| "[Total]" |] ])
                        callTool
                            2
                            "apply_changes"
                            (dict [
                                "operations",
                                box [|
                                    box (
                                        dict [
                                            "op", box "set"
                                            "object", box "[Total]"
                                            "properties", box (dict [ "description", "SENTINEL-VALUE" ])
                                        ]
                                    )
                                |]
                            ])
                        callTool
                            3
                            "run_query"
                            (dict [ "dax", "EVALUATE FILTER(Sales, Sales[Amount] = 7) // SENTINEL-DAX" ])
                        callTool 4 "list_properties" (dict [ "object", "[SENTINEL-MISSING]" ])
                        callTool 5 "no_such_tool" noArgs
                    ]

                // The query did return the sentinel row: it went to the client...
                Expect.stringContains s.Out "SENTINEL-ROW-7" ""
                let text = File.ReadAllText log
                // ... and never to the log.
                Expect.isFalse (text.Contains "SENTINEL-ROW") "no query results"
                Expect.isFalse (text.Contains "SENTINEL-VALUE") "no property values"
                Expect.isFalse (text.Contains "SENTINEL-DAX") "no query text"

                let lines = [
                    for l in text.Split('\n', StringSplitOptions.RemoveEmptyEntries) ->
                        use doc = JsonDocument.Parse l
                        doc.RootElement.Clone()
                ]

                Expect.equal lines.Length 6 "a start line, then one per call"
                Expect.equal (lines.[0].GetProperty("event").GetString()) "start" ""
                let calls = lines.Tail

                Expect.equal
                    [ for c in calls -> c.GetProperty("tool").GetString() ]
                    [
                        "get_objects"
                        "apply_changes"
                        "run_query"
                        "list_properties"
                        "no_such_tool"
                    ]
                    ""

                Expect.equal
                    [ for c in calls -> c.GetProperty("outcome").GetString() ]
                    [ "ok"; "ok"; "ok"; "error"; "unknown-tool" ]
                    ""

                Expect.equal
                    [ for o in calls.[1].GetProperty("objects").EnumerateArray() -> o.GetString() ]
                    [ "[Total]" ]
                    ""

                Expect.equal
                    (calls.[3].GetProperty("errorCode").GetString())
                    "object-not-found"
                    "the code, not the message"

                for c in calls do
                    Expect.isTrue (c.TryGetProperty "time" |> fst) ""
        ]

        testCase "as a real process: stdout holds frames only, as UTF-8"
        <| fun () ->
            use t = project ()
            let smDll = typeof<Semctl.Cli.Common.Ctx>.Assembly.Location

            let psi = ProcessStartInfo("dotnet")
            psi.ArgumentList.Add smDll
            psi.ArgumentList.Add "mcp"
            psi.ArgumentList.Add t.Path
            psi.RedirectStandardInput <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.StandardOutputEncoding <- Text.UTF8Encoding(false)
            psi.StandardInputEncoding <- Text.UTF8Encoding(false)
            psi.UseShellExecute <- false
            use p = Process.Start psi
            let err = p.StandardError.ReadToEndAsync()
            let out = p.StandardOutput.BaseStream

            let bytes =
                async {
                    use ms = new MemoryStream()
                    do! out.CopyToAsync ms |> Async.AwaitTask
                    return ms.ToArray()
                }
                |> Async.StartAsTask

            for line in
                [
                    initialize
                    initialized
                    callTool 1 "apply_changes" noArgs
                    callTool
                        2
                        "get_objects"
                        (dict [ "objects", box [| "Sales" |]; "properties", box [| "description" |] ])
                    callTool 3 "format_dax" (dict [ "dax", "\"Ünïcode ✓\"" ])
                ] do
                p.StandardInput.Write(line + "\n")

            p.StandardInput.Close()
            Expect.isTrue (p.WaitForExit 60000) "exits at end of input"
            Expect.equal p.ExitCode 0 err.Result
            let raw = bytes.Result
            Expect.isFalse (raw.Length >= 3 && raw.[0] = 0xEFuy && raw.[1] = 0xBBuy) "no BOM"
            let text = Text.Encoding.UTF8.GetString raw
            let frames = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            Expect.equal frames.Length 4 text
            // Non-ASCII both ways (Windows consoles default to another code page).
            Expect.stringContains text "Ünïcode ✓" ""

            for f in frames do
                use doc = JsonDocument.Parse f
                Expect.equal (doc.RootElement.GetProperty("jsonrpc").GetString()) "2.0" f

            Expect.stringContains err.Result "sm mcp: serving" ""
    ]
