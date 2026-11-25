/// C# scripts against a model (`sm csharp`): a local .csx file, compiled in memory with Roslyn's
/// compiler API as a top-level-statements program, run in-process against the model's TOM objects.
/// See docs/scripting-design.md for why this shape (it works from the single-file binary: every
/// reference comes from loaded assemblies' in-memory metadata, nothing is read from or written to
/// disk) and for the security model: a script runs with the user's rights, is never fetched, and
/// changes the model only in memory; the caller shows the diff and decides about saving.
module Semctl.Scripting.CsScript

#nowarn "9" // NativePtr: reading an assembly's metadata in place.

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open Microsoft.AnalysisServices.Tabular
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Semctl

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
        "  static Microsoft.AnalysisServices.Tabular.Model Model => Semctl.Scripting.CsScript.Host.Model;"
        "  static void Info(string message) => Semctl.Scripting.CsScript.Host.Info(message);"
        "  static void Warning(string message) => Semctl.Scripting.CsScript.Host.Warning(message);"
        "  static void Output(object value) => Semctl.Scripting.CsScript.Host.Output(value);"
        "  static void Error(string message) => Semctl.Scripting.CsScript.Host.Error(message);"
        "}"
    ]

/// The preprocessor symbol scripts can test (`#if SM`), like TE CLI's TECLI.
let symbol = "SM"

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

/// The managed assemblies of the .NET 10 shared framework (Microsoft.NETCore.App), by name: the
/// same on every OS. A single-file bundle doesn't list them (TRUSTED_PLATFORM_ASSEMBLIES is empty),
/// and a script can only use what is referenced, and only loaded assemblies can be referenced, so
/// they are all loaded first. One the runtime doesn't have is skipped.
let private framework = [
    "Microsoft.CSharp"
    "Microsoft.VisualBasic"
    "Microsoft.VisualBasic.Core"
    "Microsoft.Win32.Primitives"
    "Microsoft.Win32.Registry"
    "System"
    "System.AppContext"
    "System.Buffers"
    "System.Collections"
    "System.Collections.Concurrent"
    "System.Collections.Immutable"
    "System.Collections.NonGeneric"
    "System.Collections.Specialized"
    "System.ComponentModel"
    "System.ComponentModel.Annotations"
    "System.ComponentModel.DataAnnotations"
    "System.ComponentModel.EventBasedAsync"
    "System.ComponentModel.Primitives"
    "System.ComponentModel.TypeConverter"
    "System.Configuration"
    "System.Console"
    "System.Core"
    "System.Data"
    "System.Data.Common"
    "System.Data.DataSetExtensions"
    "System.Diagnostics.Contracts"
    "System.Diagnostics.Debug"
    "System.Diagnostics.DiagnosticSource"
    "System.Diagnostics.FileVersionInfo"
    "System.Diagnostics.Process"
    "System.Diagnostics.StackTrace"
    "System.Diagnostics.TextWriterTraceListener"
    "System.Diagnostics.Tools"
    "System.Diagnostics.TraceSource"
    "System.Diagnostics.Tracing"
    "System.Drawing"
    "System.Drawing.Primitives"
    "System.Dynamic.Runtime"
    "System.Formats.Asn1"
    "System.Formats.Tar"
    "System.Globalization"
    "System.Globalization.Calendars"
    "System.Globalization.Extensions"
    "System.IO"
    "System.IO.Compression"
    "System.IO.Compression.Brotli"
    "System.IO.Compression.FileSystem"
    "System.IO.Compression.ZipFile"
    "System.IO.FileSystem"
    "System.IO.FileSystem.AccessControl"
    "System.IO.FileSystem.DriveInfo"
    "System.IO.FileSystem.Primitives"
    "System.IO.FileSystem.Watcher"
    "System.IO.IsolatedStorage"
    "System.IO.MemoryMappedFiles"
    "System.IO.Pipelines"
    "System.IO.Pipes"
    "System.IO.Pipes.AccessControl"
    "System.IO.UnmanagedMemoryStream"
    "System.Linq"
    "System.Linq.AsyncEnumerable"
    "System.Linq.Expressions"
    "System.Linq.Parallel"
    "System.Linq.Queryable"
    "System.Memory"
    "System.Net"
    "System.Net.Http"
    "System.Net.Http.Json"
    "System.Net.HttpListener"
    "System.Net.Mail"
    "System.Net.NameResolution"
    "System.Net.NetworkInformation"
    "System.Net.Ping"
    "System.Net.Primitives"
    "System.Net.Quic"
    "System.Net.Requests"
    "System.Net.Security"
    "System.Net.ServerSentEvents"
    "System.Net.ServicePoint"
    "System.Net.Sockets"
    "System.Net.WebClient"
    "System.Net.WebHeaderCollection"
    "System.Net.WebProxy"
    "System.Net.WebSockets"
    "System.Net.WebSockets.Client"
    "System.Numerics"
    "System.Numerics.Vectors"
    "System.ObjectModel"
    "System.Private.CoreLib"
    "System.Private.DataContractSerialization"
    "System.Private.Uri"
    "System.Private.Xml"
    "System.Private.Xml.Linq"
    "System.Reflection"
    "System.Reflection.DispatchProxy"
    "System.Reflection.Emit"
    "System.Reflection.Emit.ILGeneration"
    "System.Reflection.Emit.Lightweight"
    "System.Reflection.Extensions"
    "System.Reflection.Metadata"
    "System.Reflection.Primitives"
    "System.Reflection.TypeExtensions"
    "System.Resources.Reader"
    "System.Resources.ResourceManager"
    "System.Resources.Writer"
    "System.Runtime"
    "System.Runtime.CompilerServices.Unsafe"
    "System.Runtime.CompilerServices.VisualC"
    "System.Runtime.Extensions"
    "System.Runtime.Handles"
    "System.Runtime.InteropServices"
    "System.Runtime.InteropServices.JavaScript"
    "System.Runtime.InteropServices.RuntimeInformation"
    "System.Runtime.Intrinsics"
    "System.Runtime.Loader"
    "System.Runtime.Numerics"
    "System.Runtime.Serialization"
    "System.Runtime.Serialization.Formatters"
    "System.Runtime.Serialization.Json"
    "System.Runtime.Serialization.Primitives"
    "System.Runtime.Serialization.Xml"
    "System.Security"
    "System.Security.AccessControl"
    "System.Security.Claims"
    "System.Security.Cryptography"
    "System.Security.Cryptography.Algorithms"
    "System.Security.Cryptography.Cng"
    "System.Security.Cryptography.Csp"
    "System.Security.Cryptography.Encoding"
    "System.Security.Cryptography.OpenSsl"
    "System.Security.Cryptography.Primitives"
    "System.Security.Cryptography.X509Certificates"
    "System.Security.Principal"
    "System.Security.Principal.Windows"
    "System.Security.SecureString"
    "System.ServiceModel.Web"
    "System.ServiceProcess"
    "System.Text.Encoding"
    "System.Text.Encoding.CodePages"
    "System.Text.Encoding.Extensions"
    "System.Text.Encodings.Web"
    "System.Text.Json"
    "System.Text.RegularExpressions"
    "System.Threading"
    "System.Threading.AccessControl"
    "System.Threading.Channels"
    "System.Threading.Overlapped"
    "System.Threading.Tasks"
    "System.Threading.Tasks.Dataflow"
    "System.Threading.Tasks.Extensions"
    "System.Threading.Tasks.Parallel"
    "System.Threading.Thread"
    "System.Threading.ThreadPool"
    "System.Threading.Timer"
    "System.Transactions"
    "System.Transactions.Local"
    "System.ValueTuple"
    "System.Web"
    "System.Web.HttpUtility"
    "System.Windows"
    "System.Xml"
    "System.Xml.Linq"
    "System.Xml.ReaderWriter"
    "System.Xml.Serialization"
    "System.Xml.XDocument"
    "System.Xml.XPath"
    "System.Xml.XPath.XDocument"
    "System.Xml.XmlDocument"
    "System.Xml.XmlSerializer"
    "WindowsBase"
    "mscorlib"
    "netstandard"
]

let private references () =
    for name in framework do
        try
            System.Reflection.Assembly.Load name |> ignore
        with _ ->
            ()

    typeof<Model>.Assembly |> ignore
    typeof<Host>.Assembly |> ignore

    AppDomain.CurrentDomain.GetAssemblies()
    |> Array.filter (fun a -> not a.IsDynamic)
    |> Array.distinctBy (fun a -> a.GetName().Name)
    |> Array.choose reference
    |> List.ofArray

/// Compile a script: the program's bytes, or its errors as "<file>(<line>,<col>): error CSxxxx: ...".
let compile (path: string) (text: string) : Result<byte array, string list> =
    let options =
        CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols = [ symbol ])

    let compilation =
        CSharpCompilation.Create(
            "sm-script",
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
let run (model: Model) (path: string) (out: TextWriter) : Result<unit, SemctlError> =
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
