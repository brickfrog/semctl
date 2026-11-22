/// Undo the file operations of an in-place save if a later model or report file fails.
module internal Tessera.SaveJournal

open System
open System.IO
open Tessera

type Journal = ResizeArray<unit -> unit>

let private remember journal undo =
    journal |> Option.iter (fun (entries: Journal) -> entries.Add undo)

let rec createDirectory journal path =
    if not (Directory.Exists path) then
        let parent = Path.GetDirectoryName(path: string)

        if not (String.IsNullOrEmpty parent) then
            createDirectory journal parent

        Directory.CreateDirectory path |> ignore

        remember journal (fun () ->
            if Directory.Exists path && Seq.isEmpty (Directory.EnumerateFileSystemEntries path) then
                Directory.Delete path)

let deleteDirectory journal path =
    Directory.Delete path
    remember journal (fun () -> Directory.CreateDirectory path |> ignore)

let deleteFile journal path =
    match journal with
    | None -> File.Delete path
    | Some(entries: Journal) ->
        let previous = File.ReadAllBytes path

        let options =
            FileStreamOptions(Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read)

        if not (OperatingSystem.IsWindows()) then
            options.UnixCreateMode <- File.GetUnixFileMode path

        File.Delete path

        entries.Add(fun () ->
            use stream = new FileStream(path, options)
            stream.Write previous)

let writeChecked (entries: Journal) path (validate: (byte[] -> unit) option) (bytes: byte[]) =
    let existed = validate.IsSome || File.Exists path

    use stream =
        new FileStream(
            path,
            (if existed then FileMode.Open else FileMode.CreateNew),
            FileAccess.ReadWrite,
            FileShare.Read
        )

    let previous = Array.zeroCreate<byte>(int stream.Length)
    stream.ReadExactly previous
    validate |> Option.iter (fun check -> check previous)

    let restore () =
        if existed then
            File.WriteAllBytes(path, previous)
        else
            File.Delete path

    try
        stream.Position <- 0L
        stream.Write bytes
        stream.SetLength(int64 bytes.Length)
        stream.Flush()
    with _ ->
        stream.Dispose()

        try
            restore ()
        with restoreEx ->
            entries.Add(fun () -> failwith $"{path}: {restoreEx.Message}")

        reraise ()

    entries.Add(fun () ->
        if File.ReadAllBytes path <> bytes then
            failwith $"{path} changed during save"

        restore ())

let write journal path (bytes: byte[]) =
    match journal with
    | None -> File.WriteAllBytes(path, bytes)
    | Some entries -> writeChecked entries path None bytes

let private transact path action =
    let entries = ResizeArray()

    let result =
        try
            action entries
        with e ->
            Error(SaveFailed(path, e.Message))

    match result with
    | Ok _ -> result
    | Error error ->
        let failures = [
            for undo in Seq.rev entries do
                try
                    undo ()
                with e ->
                    yield e.Message
        ]

        if failures.IsEmpty then
            result
        else
            Error(SaveFailed(path, TessError.message error + ". Rollback failed: " + String.concat "; " failures))


// An overlapping in-process save must not commit in the middle of another save's rollback.
let private gate = obj ()

let run path action =
    lock gate (fun () -> transact path action)
