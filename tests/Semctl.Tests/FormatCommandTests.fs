module Semctl.Tests.FormatCommandTests

open System.IO
open System.Text.Json
open Expecto
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.Tests.Fixtures
open Semctl.Tests.CliHarness
open Semctl.Cli.Common

/// The sample model plus two unformatted measures, as a .bim file.
let private model (t: TempDir) =
    let db = sampleDatabase ()
    let sales = db.Model.Tables.["Sales"]
    sales.Measures.Add(new Measure(Name = "Ugly", Expression = "sum(Sales[Amount])+1"))
    sales.Measures.Add(new Measure(Name = "Fine", Expression = "SUM ( Sales[Amount] ) + 2"))
    sales.Measures.Add(new Measure(Name = "Broken", Expression = "SUM(Sales[Amount]"))
    let path = t.Combine "model.bim"
    File.WriteAllText(path, JsonSerializer.SerializeDatabase db)
    path

let private measure (path: string) (name: string) =
    let _, db =
        ModelIO.loadPath path
        |> Result.defaultWith (fun e -> failtest (SemctlError.message e))

    db.Model.Tables.["Sales"].Measures.[name].Expression

[<Tests>]
let looseTests =
    testList "format command: loose DAX" [
        testCase "--expr prints the formatted DAX"
        <| fun () ->
            let r = semctl [ "format"; "--expr"; "sum(Sales[Amount])+1" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.equal r.Out "SUM ( Sales[Amount] ) + 1\n" ""

        testCase "options: --long, --width, --no-space-after-function"
        <| fun () ->
            let r =
                semctl [
                    "format"
                    "--expr"
                    "DIVIDE(SUM(x), 2)"
                    "--long"
                    "--no-space-after-function"
                ]

            Expect.equal r.Out "DIVIDE(\n    SUM( x ),\n    2\n)\n" ""

            let r =
                semctl [ "format"; "-e"; "CALCULATE([Sales], 'Date'[Year] = 2024)"; "--width"; "25" ]

            Expect.equal r.Out "CALCULATE (\n    [Sales],\n    'Date'[Year] = 2024\n)\n" ""

        testCase "a .dax file"
        <| fun () ->
            use t = new TempDir()
            let path = t.Combine "q.dax"
            File.WriteAllText(path, "evaluate row(\"a\", 1)")
            let r = semctl [ "format"; path ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.equal r.Out "EVALUATE\nROW ( \"a\", 1 )\n" ""
            Expect.equal (File.ReadAllText path) "evaluate row(\"a\", 1)" "the file isn't changed"

        testCase "--save writes a .dax file back, keeping its byte order mark and line endings"
        <| fun () ->
            use t = new TempDir()
            let path = t.Combine "q.dax"
            File.WriteAllBytes(path, Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] "var x = sum(T[a])\r\nreturn x"B)
            let r = semctl [ "format"; path; "--save" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.equal r.Out "" "nothing on stdout"
            Expect.stringContains r.Err "Formatted" ""
            let bytes = File.ReadAllBytes path
            Expect.equal bytes.[0..2] [| 0xEFuy; 0xBBuy; 0xBFuy |] "BOM kept"
            Expect.equal (System.Text.Encoding.UTF8.GetString bytes.[3..]) "VAR x = SUM ( T[a] )\r\nRETURN\r\n    x" ""

            // Formatted already: nothing is written, and JSON has no "saved".
            let stamp = File.GetLastWriteTimeUtc path
            let r = semctl [ "format"; path; "--save"; "--json" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.isFalse ((json r).TryGetProperty "saved" |> fst) ""
            Expect.equal (File.GetLastWriteTimeUtc path) stamp "not rewritten"

        testCase "--save --json says where it saved; no BOM is added to a file without one"
        <| fun () ->
            use t = new TempDir()
            let path = t.Combine "m.dax"
            File.WriteAllText(path, "sum(T[a])")
            let r = semctl [ "format"; path; "--save"; "--json" ]
            Expect.equal r.Code ExitCode.ok r.Err
            let j = (json r)
            Expect.equal (j.GetProperty("formatted").GetString()) "SUM ( T[a] )" ""
            Expect.equal (j.GetProperty("saved").GetProperty("path").GetString()) (Path.GetFullPath path) ""
            Expect.equal (File.ReadAllBytes path) "SUM ( T[a] )"B ""

        testCase "UTF-16 files are read as before, and --save writes them back in UTF-16"
        <| fun () ->
            use t = new TempDir()

            for encoding, bom in
                [
                    System.Text.UnicodeEncoding(false, true), [| 0xFFuy; 0xFEuy |]
                    System.Text.UnicodeEncoding(true, true), [| 0xFEuy; 0xFFuy |]
                ] do
                let path = t.Combine "w.dax"
                File.WriteAllText(path, "sum(T[a])", encoding)
                let r = semctl [ "format"; path ]
                Expect.equal r.Code ExitCode.ok r.Err
                Expect.equal r.Out "SUM ( T[a] )\n" "read with its byte order mark"

                let r = semctl [ "format"; path; "--save" ]
                Expect.equal r.Code ExitCode.ok r.Err
                let bytes = File.ReadAllBytes path
                Expect.equal bytes.[0..1] bom "the same byte order mark"
                Expect.equal (encoding.GetString(bytes, 2, bytes.Length - 2)) "SUM ( T[a] )" "the same encoding"

        testCase "a file that isn't UTF-8 is shown as before, but --save won't rewrite it"
        <| fun () ->
            use t = new TempDir()
            let path = t.Combine "latin1.dax"
            // "sum(T[a]) // café" in Latin-1: 0xE9 is not UTF-8.
            let bytes = Array.append "sum(T[a]) // caf"B [| 0xE9uy |]
            File.WriteAllBytes(path, bytes)
            Expect.equal (semctl [ "format"; path ]).Code ExitCode.ok "shown, as File.ReadAllText reads it"
            let r = semctl [ "format"; path; "--save" ]
            Expect.equal r.Code ExitCode.input r.Err
            Expect.stringContains r.Err "UTF-8" ""
            Expect.equal (File.ReadAllBytes path) bytes "left alone"

        testCase "--save on malformed DAX: exit 3, the file is left alone"
        <| fun () ->
            use t = new TempDir()
            let path = t.Combine "bad.dax"
            File.WriteAllText(path, "sum(T[a]")
            let r = semctl [ "format"; path; "--save" ]
            Expect.equal r.Code ExitCode.input ""
            Expect.equal (File.ReadAllText path) "sum(T[a]" ""

        testCase "--save needs a file: not with --expr or stdin, nor with --check or the model-only switches"
        <| fun () ->
            use t = new TempDir()
            let path = t.Combine "q.dax"
            File.WriteAllText(path, "sum(T[a])")

            for args in
                [
                    [ "format"; "--expr"; "1"; "--save" ]
                    [ "format"; "-"; "--save" ]
                    [ "format"; path; "--save"; "--check" ]
                    [ "format"; path; "--save"; "--stat" ]
                    [ "format"; path; "--save-to"; t.Combine "out.dax" ]
                ] do
                let r = semctl args
                Expect.equal r.Code ExitCode.usage (String.concat " " args)

            Expect.equal (File.ReadAllText path) "sum(T[a])" "unchanged"

        testCase "--semicolons keeps the dialect"
        <| fun () ->
            let r = semctl [ "format"; "--expr"; "IF([a] > 1,5;2;3)"; "--semicolons" ]
            Expect.equal r.Out "IF ( [a] > 1,5; 2; 3 )\n" r.Err

        testCase "malformed DAX: exit 3, errors with line and column, nothing on stdout"
        <| fun () ->
            let r = semctl [ "format"; "--expr"; "SUM(x" ]
            Expect.equal r.Code ExitCode.input ""
            Expect.equal r.Out "" ""
            Expect.equal r.Err "sm: --expr: line 1, column 4: '(' is never closed\n" ""

        testCase "--json: {success, formatted, errors}"
        <| fun () ->
            let r = semctl [ "format"; "--expr"; "sum(x)"; "--json" ]
            Expect.equal r.Code ExitCode.ok ""
            let j = (json r)
            Expect.isTrue (j.GetProperty("success").GetBoolean()) ""
            Expect.equal (j.GetProperty("formatted").GetString()) "SUM ( x )" ""
            Expect.equal (j.GetProperty("errors").GetArrayLength()) 0 ""

            let r = semctl [ "format"; "--expr"; "1 +\n  )"; "--json" ]
            Expect.equal r.Code ExitCode.input ""
            let j = (json r)
            Expect.isFalse (j.GetProperty("success").GetBoolean()) ""
            Expect.isFalse (j.TryGetProperty "formatted" |> fst) ""
            let e = j.GetProperty("errors").[0]
            Expect.equal (e.GetProperty("line").GetInt32(), e.GetProperty("column").GetInt32()) (2, 3) ""
            Expect.equal (e.GetProperty("message").GetString()) "')' without a matching '('" ""

        testCase "--check: exit 1 if it would change, 0 if it's formatted"
        <| fun () ->
            let r = semctl [ "format"; "--expr"; "sum(x)"; "--check" ]
            Expect.equal r.Code ExitCode.findings ""
            Expect.equal r.Out "" ""
            Expect.stringContains r.Err "not formatted" ""
            let r = semctl [ "format"; "--expr"; "SUM ( x )"; "--check" ]
            Expect.equal r.Code ExitCode.ok r.Err

            let r = semctl [ "format"; "--expr"; "sum(x)"; "--check"; "--json" ]
            Expect.isTrue ((json r).GetProperty("changed").GetBoolean()) ""

        testCase "usage errors: model options with loose DAX, --expr with a file, nothing to format"
        <| fun () ->
            for args in
                [
                    [ "format"; "--expr"; "1"; "--save" ]
                    [ "format"; "--expr"; "1"; "-k"; "measure" ]
                    [ "format"; "--expr"; "1"; "x.bim" ]
                    [ "format" ]
                    [ "format"; "--expr"; "1"; "--width"; "0" ]
                ] do
                Expect.equal (semctl args).Code ExitCode.usage (String.concat " " args)
    ]

[<Tests>]
let stdinTests =
    testList "format command: stdin" [
        testCase "- reads stdin"
        <| fun () ->
            let r = semctlWithStdin "var a = 1 return a" [ "format"; "-" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.equal r.Out "VAR a = 1\nRETURN\n    a\n" ""
    ]

[<Tests>]
let modelTests =
    testList "format command: models" [
        testCase "a dry run by default: shows the change, saves nothing, warns about what it can't format"
        <| fun () ->
            use t = new TempDir()
            let path = model t
            let r = semctl [ "format"; path ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Out "measure:Sales/Ugly" ""
            Expect.stringContains r.Out "SUM ( Sales[Amount] ) + 1" ""
            Expect.isFalse (r.Out.Contains "measure:Sales/Fine") "already formatted"
            Expect.stringContains r.Err "Dry run" ""
            Expect.stringContains r.Err "measure:Sales/Broken expression: not formatted: line 1, column 4" ""
            Expect.equal (measure path "Ugly") "sum(Sales[Amount])+1" "not saved"

        testCase "--save writes it; a second run has nothing to do"
        <| fun () ->
            use t = new TempDir()
            let path = model t
            let r = semctl [ "format"; path; "--save" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.equal (measure path "Ugly") "SUM ( Sales[Amount] ) + 1" ""
            Expect.equal (measure path "Broken") "SUM(Sales[Amount]" "left as it was"
            let r = semctl [ "format"; path; "--check" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.equal r.Out "" ""

        testCase "selectors, -k and -t"
        <| fun () ->
            use t = new TempDir()
            let path = model t
            let r = semctl [ "format"; path; "[Ugly]"; "--name-only" ]
            Expect.equal r.Out "measure:Sales/Ugly\n" r.Err
            let r = semctl [ "format"; path; "-k"; "function"; "--name-only" ]
            Expect.equal r.Out "function:Double\n" r.Err
            let r = semctl [ "format"; path; "-t"; "Time Intelligence"; "--name-only" ]
            let names = r.Out.TrimEnd().Split('\n') |> Array.sort |> List.ofArray
            Expect.equal names [ "calcitem:Time Intelligence/Current"; "calcitem:Time Intelligence/YTD" ] r.Err
            let r = semctl [ "format"; path; "[Nope]" ]
            Expect.equal r.Code ExitCode.input ""

        testCase "--check: lists what would change, exit 1; changes nothing"
        <| fun () ->
            use t = new TempDir()
            let path = model t
            let r = semctl [ "format"; path; "--check" ]
            Expect.equal r.Code ExitCode.findings r.Err
            let lines = r.Out.TrimEnd().Split('\n') |> List.ofArray
            Expect.contains lines "measure:Sales/Ugly expression" ""
            Expect.isFalse (List.contains "measure:Sales/Fine expression" lines) ""
            Expect.stringContains r.Err "would be reformatted" ""
            Expect.equal (measure path "Ugly") "sum(Sales[Amount])+1" ""

        testCase "--check --json"
        <| fun () ->
            use t = new TempDir()
            let path = model t
            let r = semctl [ "format"; path; "--check"; "--json" ]
            Expect.equal r.Code ExitCode.findings ""
            let j = (json r)
            Expect.isTrue (j.GetProperty("check").GetBoolean()) ""

            let unformatted = [
                for x in j.GetProperty("unformatted").EnumerateArray() -> x.GetProperty("ref").GetString()
            ]

            Expect.contains unformatted "measure:Sales/Ugly" ""
            let failed = j.GetProperty("failed").[0]
            Expect.equal (failed.GetProperty("ref").GetString()) "measure:Sales/Broken" ""
            Expect.equal (failed.GetProperty("errors").[0].GetProperty("column").GetInt32()) 4 ""

        testCase "--json without --check: the editing pipeline's change set"
        <| fun () ->
            use t = new TempDir()
            let path = model t
            let r = semctl [ "format"; path; "[Ugly]"; "--json" ]
            Expect.equal r.Code ExitCode.ok r.Err
            let j = (json r)
            Expect.isTrue (j.GetProperty("dryRun").GetBoolean()) ""
            Expect.equal (j.GetProperty("changes").GetArrayLength()) 1 ""
            Expect.equal (j.GetProperty("introducedErrors").GetArrayLength()) 0 ""

        testCase "usage errors: --semicolons or --check with saving, on a model"
        <| fun () ->
            use t = new TempDir()
            let path = model t
            Expect.equal (semctl [ "format"; path; "--semicolons" ]).Code ExitCode.usage ""
            Expect.equal (semctl [ "format"; path; "--check"; "--save" ]).Code ExitCode.usage ""
            Expect.equal (semctl [ "format"; path; "--save"; "--save-to"; t.Combine "x.bim" ]).Code ExitCode.usage ""

        testCase "a PBIP project folder is a model, not DAX"
        <| fun () ->
            use p = project ()
            let r = semctl [ "format"; p.Path; "--check" ]
            Expect.equal r.Code ExitCode.findings r.Err
            Expect.stringContains r.Out "measure:Sales/Total expression" ""
    ]
