/// C# scripts against a model (`tess csharp`): a local .csx file, compiled in memory with Roslyn's
/// compiler API as a top-level-statements program, run in-process against the model's TOM objects.
/// See docs/scripting-design.md for why this shape (it works from the single-file binary: every
/// reference comes from loaded assemblies' in-memory metadata, nothing is read from or written to
/// disk) and for the security model: a script runs with the user's rights, is never fetched, and
/// changes the model only in memory; the caller shows the diff and decides about saving.
module Tessera.Scripting.CsScript

#nowarn "9" // NativePtr: reading an assembly's metadata in place.

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open Microsoft.AnalysisServices.Tabular
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Tessera

/// A script called Error(message).
exception ScriptError of string

/// What a script sees through the preamble's `Program` members: the model and TE-style helpers.
/// Set for the duration of one run (runs are serialized).
[<AbstractClass; Sealed>]
type Host private () =
    static member val Model: Model = null with get, set
    static member val Out: TextWriter = TextWriter.Null with get, set
    static member Info(message: string) = Host.Out.WriteLine("info: " + message)

    static member Warning(message: string) =
        Host.Out.WriteLine("warning: " + message)

    static member Output(value: obj) = Host.Out.WriteLine(string value)
    static member Error(message: string) : unit = raise (ScriptError message)

/// The script's environment: usings, and `Model`, `Info`, `Warning`, `Output`, `Error` as members
/// of the top-level program (members of the enclosing class win over imported type names, so
/// `Model` is the model, not TOM's `Model` type).
let private preamble =
    String.concat "\n" [
        "global using System;"
        "global using System.Linq;"
        "global using System.Collections.Generic;"
        "global using Microsoft.AnalysisServices.Tabular;"
        "partial class Program {"
        "  static Microsoft.AnalysisServices.Tabular.Model Model => Tessera.Scripting.CsScript.Host.Model;"
        "  static void Info(string message) => Tessera.Scripting.CsScript.Host.Info(message);"
        "  static void Warning(string message) => Tessera.Scripting.CsScript.Host.Warning(message);"
        "  static void Output(object value) => Tessera.Scripting.CsScript.Host.Output(value);"
        "  static void Error(string message) => Tessera.Scripting.CsScript.Host.Error(message);"
        "}"
    ]

/// The preprocessor symbol scripts can test (`#if TESS`), like TE CLI's TECLI.
let symbol = "TESS"

/// A reference to a loaded assembly from its metadata in memory: works inside a single-file bundle,
/// where Assembly.Location is empty.
let private reference (a: System.Reflection.Assembly) : MetadataReference option =
    let mutable blob = NativeInterop.NativePtr.nullPtr<byte>
    let mutable length = 0

    if a.IsDynamic then
        None
    else
        try
            if a.TryGetRawMetadata(&blob, &length) then
                let m =
                    ModuleMetadata.CreateFromMetadata(NativeInterop.NativePtr.toNativeInt blob, length)

                Some(AssemblyMetadata.Create(m).GetReference())
            else
                None
        with _ ->
            None

let private references () =
    // Load what scripts commonly use, then reference every loaded assembly.
    for name in
        [
            "System.Runtime"
            "System.Collections"
            "System.Console"
            "System.Linq"
            "System.Text.RegularExpressions"
            "netstandard"
        ] do
        try
            System.Reflection.Assembly.Load name |> ignore
        with _ ->
            ()

    typeof<Model>.Assembly |> ignore
    typeof<Host>.Assembly |> ignore

    AppDomain.CurrentDomain.GetAssemblies()
    |> Array.choose reference
    |> List.ofArray

/// Compile a script: the program's bytes, or its errors as "<file>(<line>,<col>): error CSxxxx: ...".
let compile (path: string) (text: string) : Result<byte array, string list> =
    let options =
        CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols = [ symbol ])

    let compilation =
        CSharpCompilation.Create(
            "tess-script",
            [
                CSharpSyntaxTree.ParseText(preamble, options, "<preamble>")
                CSharpSyntaxTree.ParseText(text, options, path)
            ],
            references (),
            CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                nullableContextOptions = NullableContextOptions.Disable
            )
        )

    use pe = new MemoryStream()
    let emitted = compilation.Emit pe

    if emitted.Success then
        Ok(pe.ToArray())
    else
        Error [
            for d in emitted.Diagnostics do
                if d.Severity = DiagnosticSeverity.Error then
                    yield d.ToString()
        ]

let private gate = obj ()

/// Run a script file against `model`, mutating it. The script's output (Console, Info, Warning,
/// Output) goes to `out`.
let run (model: Model) (path: string) (out: TextWriter) : Result<unit, TessError> =
    if not (File.Exists path) then
        Error(PathNotFound path)
    else
        let text = File.ReadAllText path

        match compile path text with
        | Error diagnostics -> Error(CSharpCompileFailed(path, diagnostics))
        | Ok bytes ->
            lock gate (fun () ->
                let assembly = System.Reflection.Assembly.Load(bytes: byte array)
                let entry = assembly.EntryPoint
                let original = Console.Out
                Host.Model <- model
                Host.Out <- out
                Console.SetOut out

                try
                    try
                        let args: obj array =
                            if entry.GetParameters().Length = 0 then
                                [||]
                            else
                                [| box ([||]: string array) |]

                        match entry.Invoke(null, args) with
                        | :? Threading.Tasks.Task as t -> t.GetAwaiter().GetResult()
                        | _ -> ()

                        Ok()
                    with
                    | :? TargetInvocationException as e ->
                        match e.InnerException with
                        | ScriptError message -> Error(CSharpScriptFailed(path, message))
                        | inner -> Error(CSharpScriptFailed(path, $"{inner.GetType().Name}: {inner.Message}"))
                    | ScriptError message -> Error(CSharpScriptFailed(path, message))
                finally
                    out.Flush()
                    Console.SetOut original
                    Host.Model <- null
                    Host.Out <- TextWriter.Null)
