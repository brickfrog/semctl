# Scripting in sm: measurements and a recommendation

Tabular Editor users automate models with C# scripts. This note measures what F# scripting
(FSharp.Compiler.Service) and C# scripting (Roslyn) would add to sm's release binary and start-up,
whether each works from the single-file binary, and recommends what to ship. It also sets out the security
model a scripting command must keep.

Measured 2026-11-15 on Linux x64 (CachyOS, .NET SDK 10.0.114), on sm at `9cafc6b` (branch feat/pkg).
The spike's code lived in scratch copies of the repository, not in the release build.

## How it was measured

- **Binaries** were published as `.github/workflows/release.yml` publishes them:
  `dotnet publish src/Semctl.Cli -c Release -r <rid> --self-contained -p:PublishSingleFile=true`. The
  fsproj adds compression, an embedded PDB, no trimming, and leaves out MSAL's native broker.
- **The .NET tool package** was built with `dotnet pack src/Semctl.Cli -c Release`.
- **Three variants:**
  - the baseline;
  - `FSharp.Compiler.Service` 43.10.114, the FCS that matches the SDK's FSharp.Core 10.0.114, with a hidden
    `__fsx <model> <script>` that evaluates an `.fsx` in an `FsiEvaluationSession` with the TOM `Model` bound;
  - Roslyn: first `Microsoft.CodeAnalysis.CSharp.Scripting` 5.0.0, then plain `Microsoft.CodeAnalysis.CSharp`
    5.0.0 (the compiler API) with a hidden `__csx <model> <script>`.
- **Start-up** is the best of 5 runs, wall clock, warm file cache: `sm --version` and `sm info` on the
  spaceparts-analytics model.
- **Script timings** come from stopwatches inside the process and from wall-clock runs of a script that lists
  measures and counts functions.

## Size

Compressed single-file binary, in bytes, and the difference from the baseline:

| variant | linux-x64 | win-x64 | osx-arm64 | .NET tool nupkg |
|---|---|---|---|---|
| baseline | 45,418,864 | 43,740,310 | 42,567,269 | 26,964,135 |
| + FSharp.Compiler.Service | 53,083,173 (**+7.7 MB**) | 51,404,615 (+7.7 MB) | 50,291,525 (+7.7 MB) | 34,850,589 (+7.9 MB) |
| + Roslyn Scripting API | 48,979,579 (+3.6 MB) | - | - | - |
| + Roslyn compiler API (`Microsoft.CodeAnalysis.CSharp`) | 48,897,420 (**+3.5 MB**) | 47,217,647 (+3.5 MB) | 46,071,797 (+3.5 MB) | 30,520,602 (+3.6 MB) |

Both are well under the brief's ~15 MB bar. The win-x64 and osx-arm64 binaries were cross-published from Linux
for their size only. **They were not run**, so nothing here shows that scripting works on Windows or macOS.

## Start-up

The scripting assemblies load only when a script runs, so other commands don't slow down:

| command | baseline | + FCS | + Roslyn compiler |
|---|---|---|---|
| `sm --version` | 459 ms | 455 ms | 445 ms |
| `sm info <spaceparts>` | 818 ms | 819 ms | 834 ms |

Running a script costs, on top of loading the model:

- **FCS:**
  - creating the `FsiEvaluationSession` and binding `Model`: about 2.7 s;
  - evaluating the script: about 1.1 s more;
  - total wall clock about 4.1-4.4 s, against about 0.8 s for `sm info`.
- **Roslyn compiler API:**
  - compiling: 0.8-1.5 s, cold JIT of Roslyn in every process;
  - running: about 1 ms;
  - total wall clock about 1.9 s, against the TOM-only references of the spike.
  - As built, every assembly of the shared framework is loaded and referenced first, so a script can use
    System.Xml.Linq, System.IO.Compression, System.Net.Http and the rest. That makes it about 2.6 s wall clock
    from the release binary.
- **Not measured:** ReadyToRun-compiling the Roslyn assemblies would likely cut the compile time, at a size cost.

## Does it work from the single-file binary?

### F# (FSharp.Compiler.Service): no, not without self-extraction

`FsiEvaluationSession.Create` fails at once:

```
error FS0078: Unable to find the file '<app dir>/FSharp.Core.dll' in any of <app dir> ...
System.Exception: Error creating evaluation session: StopProcessingExn
```

FCS resolves `FSharp.Core.dll` and the framework's assemblies as files beside the runtime. A single-file bundle
has no such files, because `Assembly.Location` is empty.

With `-p:IncludeAllContentForSelfExtract=true` it works: the script ran and printed the model's measures. But the
bundle then unpacks itself on first run, 94 MB of it, under `~/.net` (or `DOTNET_BUNDLE_EXTRACT_BASE_DIR`). The
fsproj already rules that out for sm as a whole: extraction fails where HOME isn't writable (CI containers,
locked-down machines), and it would apply to every command, not only to scripts.

The remaining fix would be to embed reference assemblies (FSharp.Core and the framework's ref pack) and write
them to a temp folder before compiling. That adds size, puts files on disk, and still needs on-disk copies of
TOM's assemblies to reference. It wasn't tried.

### C# with the Roslyn Scripting API: no

`CSharpScript.RunAsync` fails, with or without a globals object:

```
System.NotSupportedException: Can't create a metadata reference to an assembly without location.
   at Microsoft.CodeAnalysis.MetadataReference.GetAssemblyFilePath(...)
   at Microsoft.CodeAnalysis.Scripting.Script.CreateFromAssembly(...)
   at Microsoft.CodeAnalysis.Scripting.Script.GetReferencesForCompilation(...)
```

The scripting layer always adds a reference to the core library by its file path, and no option turns that off.

### C# with the Roslyn compiler API: yes

`CSharpCompilation` doesn't need files:

1. **References.** A single-file bundle doesn't list the framework's assemblies: `TRUSTED_PLATFORM_ASSEMBLIES` is
   empty. So the shared framework's managed assemblies are loaded first, by name: about 170 names, the same on
   every OS. Then every assembly loaded in the process is referenced from its in-memory metadata:
   `Assembly.TryGetRawMetadata` → `ModuleMetadata.CreateFromMetadata` → `AssemblyMetadata.GetReference()`.
   That covers the framework, TOM and Semctl.Core, including assemblies inside the bundle. Collecting the 35
   references takes 13-30 ms.
2. **The script.** The `.csx` file is compiled as a C# top-level-statements program, in its own syntax tree, so
   diagnostics carry the script's own file name, line and column, for example
   `bad.csx(1,25): error CS1061: 'Model' does not contain a definition for 'Tabels'`. `using` directives,
   `using var`, local functions and classes all work as in any C# file.
3. **The preamble.** A second tree holds the `global using`s (System, System.Linq, System.Collections.Generic,
   Microsoft.AnalysisServices.Tabular) and a `partial class Program`. That class gives the script its globals as
   static members: `Model`, plus `Info()` and `Warning()`, which TE scripts call.
   - Members of the enclosing class take precedence over imported type names, so `Model` means the model, not
     the TOM type `Model`.
   - Declaring `Model` as a global (a `global using static`) was tried first, and fails with CS0229, ambiguous
     between the property and the type.
4. **The symbol `SM`** is defined, like TE CLI's `TECLI`, so a script can tell where it runs.
5. **Emit and run.** The program is emitted to memory, loaded with `Assembly.Load(byte[])`, and its entry point
   is invoked.

This ran from the single-file binary with `HOME=/nonexistent`: no extraction and nothing written to disk.

### TE's own sample scripts

The samples on the [C# scripts page](https://docs.tabulareditor.com/en/getting-started/cs-scripts-and-macros.html)
of TE's docs were run against the prototype, with table and measure names adapted to spaceparts:

| sample | result |
|---|---|
| `if(Model != null) Info("The name of the current model is: " + Model.Name); else Warning(...)` | runs: `info: The name of the current model is: Invoiced Sales` |
| `var myMeasure = Model.Tables["..."].Measures["..."]; myMeasure.Description = "..." + myMeasure.Expression;` | runs: TOM has the same name indexers |
| `foreach(var m in Model.AllMeasures) m.FormatString = "0.00";` | CS1061: `AllMeasures` belongs to TE's wrapper API, not to TOM |

So only scripts that stay within raw TOM run unchanged. TE scripts are written against TE's own object model
(`TabularEditor.TOMWrapper`: `Model.AllMeasures`, `Selected`, `Output()`, `FormatDax()`, the `ScriptHelper`
methods). That wrapper isn't open source, and sm can't reuse it. A compatibility layer would mean
reimplementing the commonly used part of that API on top of TOM: `AllMeasures`, `AllColumns`, `Output`,
`Error`, perhaps `FormatDax` through `sm format`. It could grow piece by piece, the way the DAX engine did, but
it is its own project.

## Security model

A scripting command must keep these rules:

1. **A script is a local file named on the command line.** sm never fetches one: an `http(s)://` argument is
   refused, not downloaded, and no script is ever read from the model, its annotations, or a package. Running a
   script is always an explicit act of the user.
2. **A script runs in-process, with the user's rights.** It can do anything the user can: read and write files,
   start processes, open network connections. sm doesn't sandbox it, and .NET offers no in-process sandbox
   since Code Access Security was removed. The scripting guide says so plainly: treat a script like any program you run.
3. **sm's no-network guarantee covers sm, not the code a script brings.** sm adds no network access for
   scripts, and the offline tests keep checking sm's own assemblies. A script's own `HttpClient` is the
   script's business.
4. **The model is changed only through the usual pipeline.** The script edits the in-memory TOM model. sm
   then shows the change set as a diff (a dry run by default), applies the save gate (refused if the script
   introduced errors, unless `--force`), and writes only with `--save` or `--save-to`. A failing script, whether
   a compile error, an exception or `Error()`, saves nothing.
5. **Script output goes to stderr**, so it never corrupts `--json` on stdout.
6. **No scripts run implicitly:** no start-up scripts, no macros file read by default, no scripts inside other
   commands. A later macros feature would still need the file named explicitly.

## Recommendation

**Ship C# scripting in the binary, on the Roslyn compiler API (not the Scripting API).**

- **Cost:** +3.5 MB per binary and +3.6 MB for the tool package, no start-up cost for other commands, about 1-1.5 s
  of compiling per script run.
- **It works from the single-file binary** on Linux, needing neither extraction nor a writable HOME.
- **It meets users where TE's are:** C# is what TE users write.
- **Its limit:** only raw-TOM scripts run unchanged. A TE wrapper layer is a separate, incremental project, and is
  the thing to decide next.

**Don't ship F# scripting in the binary.** It is small enough (+7.7 MB), but it doesn't run single-file without
self-extraction, which sm has ruled out. If F# scripting is wanted, ship it as a separate, optional
`sm-script` .NET tool. A framework-dependent tool package has real files on disk, so FCS works there. That
tool can reference Semctl.Core and live in this repo, outside the release binaries. A separate tool is also the
fallback for C#, should the in-binary approach fail on Windows or macOS.

## What was built

The recommendation's C# path is built as `sm csharp <model> <script.csx>`. The command name is provisional,
and `script` is taken by DAX scripts. It lives in its own project, `src/Semctl.Scripting`, and its own command
file, so it can be dropped by removing one project reference and one command. See the [C# scripting guide](dax-tools.md#c-scripts-csharp). The F# variant and the Scripting-API variant were spike code only, and were not kept.

## Open

- **Windows and macOS: not executed.** `TryGetRawMetadata` and in-memory emit are not platform-specific, but nobody
  has run them there. Run `sm csharp` from the win-x64 and osx-arm64 release binaries before announcing it.
- **No TE wrapper API.** `Selected` has no meaning in a CLI, and `AllMeasures`, `Output`, `FormatDax` and the
  rest don't exist yet.
- **No ReadyToRun build of Roslyn**, so each run compiles with a cold JIT.
- **No `#r` and no NuGet packages.** References are the shared framework, TOM and sm's own assemblies. The
  framework's list of names is fixed in `CsScript.fs` for .NET 10, and a name the runtime lacks is skipped.
