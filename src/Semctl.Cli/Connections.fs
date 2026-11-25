/// Connection profiles and the active connection of a terminal: local state only, never secrets.
///
/// Profiles live in the user config file: $SM_CONFIG, or %APPDATA%\semctl\config.json on
/// Windows, else $XDG_CONFIG_HOME/semctl/config.json (~/.config/semctl/config.json):
///   { "formatVersion": 1,
///     "profiles": { "prod": { "server": "powerbi://.../Finance", "database": "Revenue",
///                             "auth": "device-code", "description": "...",
///                             "settings": { "bpaRules": "./rules.json" } } } }
/// A profile names a model (a local path, or a server and database) and behaviour overrides
/// ("settings"): defaults for the non-secret SM_* variables, which a variable set in the
/// environment still overrides. Secrets are never stored: credentials come from the environment
/// (and the opt-in token cache) as without a profile, and a server string with credentials in it
/// is refused.
///
/// The active connection belongs to one terminal: a state file in $SM_STATE_DIR, or
/// %LOCALAPPDATA%\semctl\sessions on Windows, else $XDG_STATE_HOME/semctl/sessions
/// (~/.local/state/semctl/sessions), named after the terminal's process (on Linux and macOS the
/// session leader, getsid: the terminal's shell; on Windows the parent process), with its start
/// time so a later process with the same id doesn't inherit it. SM_SESSION=<name> names a
/// session to share between terminals instead.
///
/// Commands use them through two model arguments, rewritten before the command line is parsed:
/// `active:` (the active connection) and `profile:<name>`.
module Semctl.Cli.Connections

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.Json.Nodes
open Semctl

// ---------------------------------------------------------------------------------------------
// Profiles

/// Behaviour overrides a profile may carry: setting name -> the SM_* variable it defaults.
/// Only variables that hold no secret.
let settings =
    [
        "auth", "SM_XMLA_AUTH"
        "clientId", "SM_XMLA_CLIENT_ID"
        "tenantId", "SM_XMLA_TENANT_ID"
        "user", "SM_XMLA_USER"
        "tokenCache", "SM_XMLA_TOKEN_CACHE"
        "bpaRules", "SM_BPA_RULES"
    ]
    |> Map.ofList

type Profile = {
    Name: string
    /// A local model (any path sm accepts).
    Model: string option
    /// A server (as `sm connect` takes it), and optionally a database on it.
    Server: string option
    Database: string option
    Description: string option
    /// Behaviour overrides: names from `settings`.
    Settings: Map<string, string>
}

module Profile =
    let empty (name: string) = {
        Name = name
        Model = None
        Server = None
        Database = None
        Description = None
        Settings = Map.empty
    }

    /// The model argument a profile stands for: its path, or xmla:<server>[;<database>].
    let modelArgument (p: Profile) =
        match p.Model, p.Server with
        | Some m, _ -> Ok m
        | None, Some server ->
            XmlaAddress.parseServer server
            |> Result.map (fun a ->
                let a =
                    match p.Database with
                    | Some d -> XmlaAddress.withDatabase d a
                    | None -> a

                XmlaAddress.display a)
        | None, None -> Error(InvalidConfig(p.Name, "the profile names no model (--model, or --server)"))

    /// The environment variable defaults it sets.
    let environment (p: Profile) =
        p.Settings
        |> Map.toList
        |> List.choose (fun (k, v) -> Map.tryFind k settings |> Option.map (fun var -> var, v))
        |> Map.ofList

let private rooted (v: string option) =
    v |> Option.filter (fun d -> Path.IsPathRooted d)

/// The user config file (holding the profiles), if a location can be found.
let configFile (getEnv: string -> string option) =
    match getEnv "SM_CONFIG" with
    | Some file -> Some file
    | None when OperatingSystem.IsWindows() ->
        getEnv "APPDATA"
        |> rooted
        |> Option.map (fun d -> Path.Combine(d, "semctl", "config.json"))
    | None ->
        match getEnv "XDG_CONFIG_HOME" |> rooted with
        | Some d -> Some(Path.Combine(d, "semctl", "config.json"))
        | None ->
            getEnv "HOME"
            |> rooted
            |> Option.map (fun h -> Path.Combine(h, ".config", "semctl", "config.json"))

let private readJson (file: string) : Result<JsonObject, SemctlError> =
    try
        if not (File.Exists file) then
            Ok(JsonObject())
        else
            match JsonNode.Parse(File.ReadAllText file) with
            | :? JsonObject as o -> Ok o
            | _ -> Error(InvalidConfig(file, "it is not a JSON object"))
    with e ->
        Error(InvalidConfig(file, e.Message))

let private writeJson (file: string) (o: JsonObject) : Result<unit, SemctlError> =
    try
        match Path.GetDirectoryName(Path.GetFullPath file) with
        | null -> ()
        | dir -> Directory.CreateDirectory dir |> ignore

        let tmp = $"{file}.{Guid.NewGuid():N}.tmp"

        try
            File.WriteAllText(tmp, o.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n")
            File.Move(tmp, file, true)
            Ok()
        with _ ->
            try
                File.Delete tmp
            with _ ->
                ()

            reraise ()
    with e ->
        Error(InvalidConfig(file, e.Message))

let private text (o: JsonObject) (name: string) =
    match o.[name] with
    | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
    | _ -> None

let private profileOf (name: string) (o: JsonObject) = {
    Name = name
    Model = text o "model"
    Server = text o "server"
    Database = text o "database"
    Description = text o "description"
    Settings =
        match o.["settings"] with
        | :? JsonObject as s ->
            Map.ofList [
                for KeyValue(k, v) in s do
                    match v with
                    | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> k, v.GetValue<string>()
                    | _ -> ()
            ]
        | _ -> Map.empty
}

let private profileJson (p: Profile) : JsonNode =
    let o = JsonObject()

    let put (k: string) (v: string option) =
        v |> Option.iter (fun v -> o.[k] <- JsonValue.Create v)

    put "model" p.Model
    put "server" p.Server
    put "database" p.Database
    put "description" p.Description

    if not p.Settings.IsEmpty then
        let s = JsonObject()

        for KeyValue(k, v) in p.Settings do
            s.[k] <- JsonValue.Create v

        o.["settings"] <- s

    o

let private needConfig (getEnv: string -> string option) =
    match configFile getEnv with
    | Some f -> Ok f
    | None -> Error(InvalidConfig("the user config", "no location for it: set SM_CONFIG, or HOME (APPDATA on Windows)"))

/// Every profile in the user config (none without a config file).
let profiles (getEnv: string -> string option) : Result<Profile list, SemctlError> =
    match configFile getEnv with
    | None -> Ok []
    | Some file ->
        readJson file
        |> Result.map (fun root ->
            match root.["profiles"] with
            | :? JsonObject as ps -> [
                for KeyValue(name, v) in ps do
                    match v with
                    | :? JsonObject as o -> profileOf name o
                    | _ -> ()
              ]
            | _ -> [])

let tryProfile getEnv (name: string) =
    profiles getEnv
    |> Result.bind (fun ps ->
        match ps |> List.tryFind (fun p -> p.Name = name) with
        | Some p -> Ok p
        | None -> Error(ProfileNotFound(name, ps |> List.map _.Name)))

/// A profile name: letters, digits, '.', '_' and '-'.
let validName (name: string) =
    name <> ""
    && name
       |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '.' || c = '_' || c = '-')

/// Check a profile holds no secret before it is written.
let check (p: Profile) : Result<Profile, SemctlError> =
    // Credentials as a connection string writes them (`Password=...`): a path that merely has such a
    // word in it is fine.
    let secretKeys = [
        "password"
        "pwd"
        "secret"
        "clientsecret"
        "client_secret"
        "token"
        "accesstoken"
    ]

    let hasSecret (v: string) =
        Text.RegularExpressions.Regex.Matches(v, @"([A-Za-z_ ]+)\s*=")
        |> Seq.exists (fun m ->
            let key = m.Groups.[1].Value.Trim().Replace(" ", "").ToLowerInvariant()
            secretKeys |> List.exists (fun k -> key.EndsWith k))

    if not (validName p.Name) then
        Error(InvalidName(p.Name, "a profile name has only letters, digits, '.', '_' and '-'"))
    else
        match
            p.Settings
            |> Map.toList
            |> List.tryFind (fun (k, _) -> not (settings.ContainsKey k))
        with
        | Some(k, _) ->
            let known = settings |> Map.keys |> String.concat ", "
            Error(InvalidConfig(p.Name, $"unknown setting '{k}' (known: {known}); secrets are never stored"))
        | None ->
            match p.Server |> Option.map XmlaAddress.parseServer with
            | Some(Error e) -> Error e
            | _ ->
                match
                    [ p.Model; p.Server; p.Database ]
                    @ (p.Settings |> Map.toList |> List.map (snd >> Some))
                    |> List.choose id
                    |> List.tryFind hasSecret
                with
                | Some _ -> Error(SecretOnCommandLine "a profile value")
                | None ->
                    match p.Model, p.Server with
                    | Some _, Some _ -> Error(InvalidConfig(p.Name, "a profile names a model or a server, not both"))
                    | _ -> Ok p

/// Write (create or replace) a profile, keeping the rest of the config file as it is.
let saveProfile getEnv (p: Profile) : Result<string, SemctlError> =
    check p
    |> Result.bind (fun p ->
        needConfig getEnv
        |> Result.bind (fun file ->
            readJson file
            |> Result.bind (fun root ->
                if not (root.ContainsKey "formatVersion") then
                    root.["formatVersion"] <- JsonValue.Create 1

                let ps =
                    match root.["profiles"] with
                    | :? JsonObject as ps -> ps
                    | _ ->
                        let ps = JsonObject()
                        root.["profiles"] <- ps
                        ps

                ps.[p.Name] <- profileJson p
                writeJson file root |> Result.map (fun () -> file))))

/// Remove a profile. False if there was none of that name.
let removeProfile getEnv (name: string) : Result<bool, SemctlError> =
    needConfig getEnv
    |> Result.bind (fun file ->
        readJson file
        |> Result.bind (fun root ->
            match root.["profiles"] with
            | :? JsonObject as ps when ps.ContainsKey name ->
                ps.Remove name |> ignore
                writeJson file root |> Result.map (fun () -> true)
            | _ -> Ok false))

// ---------------------------------------------------------------------------------------------
// The terminal's session

module private Native =
    [<DllImport("libc", EntryPoint = "getsid")>]
    extern int getsid(int pid)

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type ProcessBasicInformation =
        val mutable ExitStatus: nativeint
        val mutable PebBaseAddress: nativeint
        val mutable AffinityMask: nativeint
        val mutable BasePriority: nativeint
        val mutable UniqueProcessId: nativeint
        val mutable InheritedFromUniqueProcessId: nativeint

    [<DllImport("ntdll.dll")>]
    extern int NtQueryInformationProcess(
        nativeint processHandle,
        int processInformationClass,
        ProcessBasicInformation& processInformation,
        int processInformationLength,
        int& returnLength
    )

/// The process that stands for this terminal, if it can be found: on Linux and macOS the session
/// leader (the terminal's shell: pipelines and subshells share it), on Windows the parent process.
let terminalProcessId () =
    try
        if OperatingSystem.IsWindows() then
            let mutable info = Native.ProcessBasicInformation()
            let mutable returned = 0
            use me = Process.GetCurrentProcess()

            let status =
                Native.NtQueryInformationProcess(me.Handle, 0, &info, Marshal.SizeOf info, &returned)

            if status = 0 then
                Some(int info.InheritedFromUniqueProcessId)
            else
                None
        else
            match Native.getsid 0 with
            | sid when sid > 0 -> Some sid
            | _ -> None
    with _ ->
        None

let private startTime (pid: int) =
    try
        use p = Process.GetProcessById pid
        Some(p.StartTime.ToUniversalTime().ToString("o", Globalization.CultureInfo.InvariantCulture))
    with _ ->
        None

/// Two start times of the same process (as read at different times: Linux derives them from the
/// boot time, which can come out a little different on each read).
let private sameStart (a: string) (b: string) =
    let styles = Globalization.DateTimeStyles.RoundtripKind
    let culture = Globalization.CultureInfo.InvariantCulture

    match DateTime.TryParse(a, culture, styles), DateTime.TryParse(b, culture, styles) with
    | (true, x), (true, y) -> abs (x - y).TotalSeconds < 2.0
    | _ -> a = b

/// The folder holding session state files, if a location can be found.
let stateDirectory (getEnv: string -> string option) =
    match getEnv "SM_STATE_DIR" with
    | Some d -> Some d
    | None when OperatingSystem.IsWindows() ->
        getEnv "LOCALAPPDATA"
        |> rooted
        |> Option.map (fun d -> Path.Combine(d, "semctl", "sessions"))
    | None ->
        match getEnv "XDG_STATE_HOME" |> rooted with
        | Some d -> Some(Path.Combine(d, "semctl", "sessions"))
        | None ->
            getEnv "HOME"
            |> rooted
            |> Option.map (fun h -> Path.Combine(h, ".local", "state", "semctl", "sessions"))

/// Which session this is: a SM_SESSION name, or the terminal (the parent process).
type SessionKey =
    | Named of string
    | Terminal of pid: int * started: string option

module SessionKey =
    let current (getEnv: string -> string option) =
        match getEnv "SM_SESSION" with
        | Some name -> Some(Named name)
        | None -> terminalProcessId () |> Option.map (fun pid -> Terminal(pid, startTime pid))

    let fileName =
        function
        | Named name ->
            let safe =
                name
                |> String.map (fun c ->
                    if Char.IsLetterOrDigit c || c = '-' || c = '_' then
                        c
                    else
                        '_')

            $"name-{safe}.json"
        | Terminal(pid, _) -> $"pid-{pid}.json"

    let describe =
        function
        | Named name -> $"SM_SESSION={name}"
        | Terminal(pid, _) -> $"this terminal (process {pid})"

/// A session's active connection.
type ActiveConnection = {
    /// The model argument it stands for (a path or xmla:<server>;<database>).
    Model: string
    /// The profile it was made from, if any.
    Profile: string option
    /// When it was made (UTC, ISO 8601).
    Since: string
}

type SessionState = {
    Key: SessionKey
    File: string
    Active: ActiveConnection option
}

let private stateFile getEnv =
    match stateDirectory getEnv, SessionKey.current getEnv with
    | Some dir, Some key -> Some(key, Path.Combine(dir, SessionKey.fileName key))
    | _ -> None

let private stateOf (key: SessionKey) (file: string) : SessionState =
    let active =
        match readJson file with
        | Ok o ->
            // A terminal's file is its own only while its process is the one that wrote it.
            let sameTerminal =
                match key with
                | Terminal(_, Some started) -> text o "terminalStarted" |> Option.forall (sameStart started)
                | _ -> true

            match text o "model" with
            | Some model when sameTerminal ->
                Some {
                    Model = model
                    Profile = text o "profile"
                    Since = text o "since" |> Option.defaultValue ""
                }
            | _ -> None
        | Error _ -> None

    {
        Key = key
        File = file
        Active = active
    }

/// This session's state (None: no location for state files, or no way to tell the terminal).
let session getEnv =
    stateFile getEnv |> Option.map (fun (key, file) -> stateOf key file)

/// Make a connection the session's active one.
let activate getEnv (active: ActiveConnection) : Result<SessionState, SemctlError> =
    match stateFile getEnv with
    | None ->
        Error(
            InvalidConfig(
                "the session state",
                "no location for it: set SM_STATE_DIR, or HOME (LOCALAPPDATA on Windows); or name a session with SM_SESSION"
            )
        )
    | Some(key, file) ->
        let o = JsonObject()
        o.["model"] <- JsonValue.Create active.Model
        active.Profile |> Option.iter (fun p -> o.["profile"] <- JsonValue.Create p)
        o.["since"] <- JsonValue.Create active.Since

        match key with
        | Terminal(pid, started) ->
            o.["terminal"] <- JsonValue.Create pid
            started |> Option.iter (fun s -> o.["terminalStarted"] <- JsonValue.Create s)
        | Named name -> o.["session"] <- JsonValue.Create name

        writeJson file o
        |> Result.map (fun () -> {
            Key = key
            File = file
            Active = Some active
        })

/// Forget the session's active connection. False if it had none.
let clear getEnv : Result<bool, SemctlError> =
    match stateFile getEnv with
    | Some(_, file) when File.Exists file ->
        try
            File.Delete file
            Ok true
        with e ->
            Error(InvalidConfig(file, e.Message))
    | _ -> Ok false

/// Every state file: its path, the model it holds, and whether its terminal is gone (stale).
let allSessions getEnv : (string * string option * bool) list =
    match stateDirectory getEnv with
    | Some dir when Directory.Exists dir -> [
        for file in Directory.GetFiles(dir, "*.json") |> Array.sort do
            match readJson file with
            | Ok o ->
                let stale =
                    match o.["terminal"] with
                    | :? JsonValue as v when v.GetValueKind() = JsonValueKind.Number ->
                        let pid = v.GetValue<int>()

                        match startTime pid with
                        | None -> true
                        | Some started -> text o "terminalStarted" |> Option.exists (fun s -> not (sameStart s started))
                    | _ -> false

                file, text o "model", stale
            | Error _ -> file, None, true
      ]
    | _ -> []

// ---------------------------------------------------------------------------------------------
// Model arguments

/// The model argument for the active connection.
let activeArgument = "active:"

/// The prefix of a profile's model argument.
let profilePrefix = "profile:"

/// The active connection's model argument and profile.
let activeModel getEnv : Result<ActiveConnection, SemctlError> =
    match session getEnv with
    | None ->
        Error(
            NoActiveConnection
                "sm can't tell which terminal this is, or where its state goes (set SM_STATE_DIR or HOME, or SM_SESSION)"
        )
    | Some s ->
        match s.Active with
        | Some a -> Ok a
        | None ->
            Error(
                NoActiveConnection(
                    $"{SessionKey.describe s.Key} has none; `sm connect <model or server>` or `sm connect --profile <name>` makes one"
                )
            )

/// Rewrite `active:` and `profile:<name>` model arguments, and add the overrides of the profiles
/// they use (or of the profile the active connection came from) to the environment: a variable
/// set in the environment wins. Reads only local files, and only when a line uses them.
let prepare (getEnv: string -> string option) (argv: string array) =
    let uses (a: string) =
        a = activeArgument
        || (a.StartsWith profilePrefix && a.Length > profilePrefix.Length)

    if not (argv |> Array.exists uses) then
        Ok(argv, getEnv)
    else
        let resolved =
            argv
            |> Array.fold
                (fun (acc: Result<string list * Profile list, SemctlError>) a ->
                    acc
                    |> Result.bind (fun (args, used) ->
                        if a = activeArgument then
                            activeModel getEnv
                            |> Result.bind (fun active ->
                                match active.Profile with
                                | Some name ->
                                    match tryProfile getEnv name with
                                    | Ok p -> Ok(args @ [ active.Model ], used @ [ p ])
                                    // A profile removed since: the connection still stands.
                                    | Error _ -> Ok(args @ [ active.Model ], used)
                                | None -> Ok(args @ [ active.Model ], used))
                        elif uses a then
                            tryProfile getEnv (a.Substring profilePrefix.Length)
                            |> Result.bind (fun p ->
                                Profile.modelArgument p |> Result.map (fun m -> args @ [ m ], used @ [ p ]))
                        else
                            Ok(args @ [ a ], used)))
                (Ok([], []))

        resolved
        |> Result.map (fun (args, used) ->
            let overrides =
                used
                |> List.fold
                    (fun (m: Map<string, string>) p -> Map.fold (fun m k v -> Map.add k v m) m (Profile.environment p))
                    Map.empty

            let env (name: string) =
                match getEnv name with
                | Some v -> Some v
                | None -> Map.tryFind name overrides

            Array.ofList args, env)
