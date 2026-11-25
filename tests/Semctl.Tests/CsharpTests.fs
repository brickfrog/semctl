/// `sm csharp`: C# scripts compiled in memory with Roslyn and run against the model's TOM objects,
/// then shown and saved like any editing command.
module Semctl.Tests.CsharpTests

open System.IO
open Expecto
open Semctl
open Semctl.Tests.Fixtures
open Semctl.Tests.CliHarness

module ExitCode = Semctl.Cli.Common.ExitCode

let private withModel (f: string -> TempDir -> unit) =
    use p = project ()
    f (Path.Combine(p.Path, "Sales.SemanticModel")) p

let private script (p: TempDir) (lines: string list) =
    let path = p.Combine "s.csx"
    File.WriteAllText(path, String.concat "\n" lines + "\n")
    path


// Scripts compile with Roslyn (about a second each), and runs share Console.Out: run in sequence.
[<Tests>]
let tests =
    testSequenced
    <| testList "csharp" [
        test "a script's changes are a dry run; --save writes them" {
            withModel (fun model p ->
                let s =
                    script p [
                        "using System.Text;"
                        "var m = Model.Tables[\"Sales\"].Measures[\"Total\"];"
                        "m.FormatString = Fmt();"
                        "Info($\"set {m.Name}\");"
                        "Console.WriteLine(new StringBuilder(\"to stderr\").ToString());"
                        "static string Fmt() => \"#,0.00\";"
                    ]

                let dry = semctl [ "csharp"; model; s ]
                Expect.equal dry.Code ExitCode.ok dry.Err
                Expect.stringContains dry.Out "~ measure:Sales/Total" ""
                Expect.stringContains dry.Out "formatString: \"#,0\" -> \"#,0.00\"" ""
                Expect.stringContains dry.Err "info: set Total" "Info goes to stderr"
                Expect.stringContains dry.Err "to stderr" "so does Console"
                Expect.isFalse (dry.Out.Contains "to stderr") "never stdout"

                let _, before =
                    ModelIO.loadPath model |> Result.defaultWith (fun _ -> failtest "load")

                Expect.equal before.Model.Tables.["Sales"].Measures.["Total"].FormatString "#,0" "not written"

                let saved = semctl [ "csharp"; model; s; "--save"; "--json" ]
                Expect.equal saved.Code ExitCode.ok saved.Err
                let doc = json saved
                Expect.isFalse (doc.GetProperty("dryRun").GetBoolean()) "--json stays clean"

                let _, after =
                    ModelIO.loadPath model |> Result.defaultWith (fun _ -> failtest "load")

                Expect.equal after.Model.Tables.["Sales"].Measures.["Total"].FormatString "#,0.00" "written")
        }

        test "compile errors carry the script's own lines; nothing is saved" {
            withModel (fun model p ->
                let s = script p [ "var n = Model.Tables.Count;"; "Model.Tabels.Clear();" ]
                let r = semctl [ "csharp"; model; s; "--save" ]
                Expect.equal r.Code ExitCode.input r.Err
                Expect.stringContains r.Err "s.csx(2,7): error CS1061" "line 2, column 7 of the script"
                Expect.equal (errorCode (semctl [ "csharp"; model; s; "--json" ])) "script-compile-failed" "")
        }

        test "Error() and exceptions fail the script, and nothing is saved" {
            withModel (fun model p ->
                for body in
                    [
                        [
                            "Model.Tables[\"Sales\"].Measures[\"Total\"].FormatString = \"x\";"
                            "Error(\"stop here\");"
                        ]
                        [
                            "Model.Tables[\"Sales\"].Measures[\"Total\"].FormatString = \"x\";"
                            "throw new InvalidOperationException(\"boom\");"
                        ]
                    ] do
                    let s = script p body
                    let r = semctl [ "csharp"; model; s; "--save"; "--json" ]
                    Expect.equal r.Code ExitCode.input r.Out
                    Expect.equal (errorCode r) "script-failed" r.Out
                    let _, m = ModelIO.loadPath model |> Result.defaultWith (fun _ -> failtest "load")
                    Expect.equal m.Model.Tables.["Sales"].Measures.["Total"].FormatString "#,0" "unchanged")
        }

        test "the save gate applies to a script's changes" {
            withModel (fun model p ->
                let s =
                    script p [
                        "Model.Tables[\"Sales\"].Measures[\"Total\"].Expression = \"SUM(Sales[Nope])\";"
                    ]

                let r = semctl [ "csharp"; model; s; "--save" ]
                Expect.equal r.Code ExitCode.findings $"{r.Out}{r.Err}"
                let forced = semctl [ "csharp"; model; s; "--save"; "--force" ]
                Expect.equal forced.Code ExitCode.ok forced.Err)
        }

        test "the SM symbol is defined; Output prints values" {
            withModel (fun model p ->
                let s =
                    script p [
                        "#if SM"
                        "Output(Model.Tables.Count);"
                        "#else"
                        "Error(\"no SM\");"
                        "#endif"
                    ]

                let r = semctl [ "csharp"; model; s ]
                Expect.equal r.Code ExitCode.ok r.Err
                Expect.stringContains r.Err "3\n" "three tables"
                Expect.stringContains r.Out "No changes." "")
        }

        test "the framework's assemblies are there before the script loads any" {
            withModel (fun model p ->
                let s =
                    script p [
                        "using System.Xml.Linq;"
                        "using System.IO.Compression;"
                        "var x = new XElement(\"m\", new XAttribute(\"name\", Model.Name));"
                        "using var ms = new System.IO.MemoryStream();"
                        "using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) { z.CreateEntry(\"a\"); }"
                        "var json = System.Text.Json.JsonSerializer.Serialize(new { n = Model.Tables.Count });"
                        "Output($\"{x} {ms.Length > 0} {typeof(System.Net.Http.HttpClient).Name} {new System.Data.DataTable().GetType().Name} {json}\");"
                    ]

                let r = semctl [ "csharp"; model; s ]
                Expect.equal r.Code ExitCode.ok r.Err
                Expect.stringContains r.Err "<m name=\"Model\" /> True HttpClient DataTable {\"n\":3}" "")
        }

        test "scripts are local files: a URL is refused, a missing file is an error" {
            withModel (fun model p ->
                let url = semctl [ "csharp"; model; "https://example.com/s.csx" ]
                Expect.equal url.Code ExitCode.usage url.Err
                let missing = semctl [ "csharp"; model; p.Combine "nope.csx" ]
                Expect.equal missing.Code ExitCode.input missing.Err)
        }
    ]
