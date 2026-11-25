module Semctl.Tests.BpaCommandTests

open System.IO
open System.Text.Json
open System.Xml.Linq
open Expecto
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.Tests.Fixtures
open Semctl.Tests.CliHarness
open Semctl.Tests.BpaTests
open Semctl.Cli.Common

/// A temp folder with the BPA fixture saved as model.bim (after `change`).
let private model (change: Database -> unit) =
    let t = new TempDir()
    let db = bpaDatabase ()
    change db

    match ModelIO.save ModelIO.BimFile ModelIO.SaveOptions.defaults false db (t.Combine "model.bim") with
    | Ok _ -> t
    | Error e -> failtest (SemctlError.message e)

let private rulesFile (t: TempDir) (json: string) =
    let path = t.Combine "rules.json"
    write path json
    path

let private ids (doc: JsonElement) (property: string) =
    [
        for f in doc.GetProperty(property).EnumerateArray() -> f.GetProperty("code").GetString()
    ]
    |> List.distinct

[<Tests>]
let tests =
    testList "CLI bpa" [
        testCase "built-in rules: one finding per line, a summary, exit 1"
        <| fun () ->
            use t = model ignore
            let r = semctl [ "bpa"; t.Combine "model.bim" ]
            Expect.equal r.Code ExitCode.findings r.Err
            Expect.stringContains r.Out "warning  measure:Sales/Ratio" ""
            Expect.stringContains r.Out "SM_DIVIDE  Use DIVIDE" ""
            Expect.stringContains r.Out "error    relationship:Sales-Product" "errors sort first"

            let last = r.Out.TrimEnd().Split('\n') |> Array.last
            Expect.stringContains last "findings (" ""
            Expect.stringContains last "rules checked" ""

        testCase "--json: findings, unsupported rules and a summary"
        <| fun () ->
            use t = model ignore

            let rules =
                rulesFile t (ruleJson [ [ "ID", box "BROKEN"; "Scope", box "Measure"; "Expression", box "Nope" ] ])

            let r = semctl [ "bpa"; t.Combine "model.bim"; "--rules"; rules; "--json" ]
            Expect.equal r.Code ExitCode.findings r.Err
            Expect.equal r.Err "" "nothing on stderr in JSON mode"
            let doc = json r
            let root = doc
            let first = root.GetProperty("findings").[0]

            Expect.equal (root.GetProperty("command").GetString()) "bpa" ""

            for p in
                [
                    "severity"
                    "source"
                    "code"
                    "message"
                    "object"
                    "objectType"
                    "objectPath"
                    "ruleName"
                    "category"
                    "fixable"
                ] do
                Expect.isTrue (fst (first.TryGetProperty p)) p

            Expect.equal (first.GetProperty("source").GetString()) "bpa" ""
            let ruleError = root.GetProperty("ruleErrors").[0]
            Expect.equal (ruleError.GetProperty("ruleId").GetString()) "BROKEN" ""
            Expect.equal (ruleError.GetProperty("stage").GetString()) "compile" ""
            Expect.stringContains (ruleError.GetProperty("error").GetString()) "Nope" ""
            Expect.equal (root.GetProperty("rules").GetProperty("errors").GetInt32()) 1 ""
            let summary = root.GetProperty "summary"

            Expect.equal (summary.GetProperty("total").GetInt32()) (root.GetProperty("findings").GetArrayLength()) ""
            Expect.equal (root.GetProperty("violations").GetInt32()) (summary.GetProperty("total").GetInt32()) ""

        testCase "unsupported rules are reported on stderr in text mode"
        <| fun () ->
            use t = model ignore

            let rules =
                rulesFile t (ruleJson [ [ "ID", box "BROKEN"; "Scope", box "Measure"; "Expression", box "Nope" ] ])

            let r = semctl [ "bpa"; t.Combine "model.bim"; "--no-default-rules"; "-r"; rules ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Err "rule BROKEN" ""
            Expect.stringContains r.Err "not supported" ""
            Expect.stringContains r.Out "No findings" ""
            Expect.stringContains r.Out "1 rule unsupported" ""

        testCase "--no-default-rules with a passing rule file exits 0"
        <| fun () ->
            use t = model ignore

            let rules =
                rulesFile t (ruleJson [ [ "ID", box "NONE"; "Scope", box "Model"; "Expression", box "false" ] ])

            let r =
                semctl [ "bpa"; t.Combine "model.bim"; "--no-default-rules"; "--rules"; rules ]

            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringStarts r.Out "No findings; 1 rule checked" ""

        testCase "--min-severity and --category select rules"
        <| fun () ->
            use t = model ignore
            let r = semctl [ "bpa"; t.Combine "model.bim"; "--min-severity"; "3"; "--json" ]
            let doc = json r

            for f in doc.GetProperty("findings").EnumerateArray() do
                Expect.equal (f.GetProperty("severity").GetString()) "error" ""

            let r = semctl [ "bpa"; t.Combine "model.bim"; "-c"; "dax,naming"; "--json" ]
            let doc = json r

            for f in doc.GetProperty("findings").EnumerateArray() do
                Expect.contains [ "DAX"; "Naming" ] (f.GetProperty("category").GetString()) ""

        testCase "--list-rules lists rules with their support status"
        <| fun () ->
            use t = model ignore

            let rules =
                rulesFile
                    t
                    (ruleJson [
                        [ "ID", box "BROKEN"; "Scope", box "Measure"; "Expression", box "Nope" ]
                        [ "ID", box "FINE"; "Scope", box "Measure"; "Expression", box "IsHidden" ]
                    ])

            let r = semctl [ "bpa"; t.Combine "model.bim"; "--list-rules"; "-r"; rules ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Out "SM_DIVIDE" ""
            Expect.stringContains r.Out "[unsupported:" ""

            let r =
                semctl [
                    "bpa"
                    t.Combine "model.bim"
                    "--list-rules"
                    "-r"
                    rules
                    "--no-default-rules"
                    "-j"
                ]

            let doc = json r

            let support = [
                for x in doc.EnumerateArray() ->
                    x.GetProperty("id").GetString(), x.GetProperty("supported").GetBoolean()
            ]

            Expect.equal support [ "BROKEN", false; "FINE", true ] ""

        testCase "an object's ignore annotation moves its finding to \"ignored\""
        <| fun () ->
            use t =
                model (fun db ->
                    db.Model.Tables.["Sales"].Measures.["Ratio"].Annotations.Add(
                        new Annotation(
                            Name = "BestPracticeAnalyzer_IgnoreRules",
                            Value = """{"RuleIDs":["SM_DIVIDE"]}"""
                        )
                    ))

            let r = semctl [ "bpa"; t.Combine "model.bim"; "-c"; "DAX"; "--json" ]
            let doc = json r
            Expect.isFalse (ids doc "findings" |> List.contains "SM_DIVIDE") ""
            Expect.equal (ids doc "ignored") [ "SM_DIVIDE" ] ""
            Expect.equal (doc.GetProperty("rules").GetProperty("ignoredFindings").GetInt32()) 1 ""

        testCase "rules embedded in the model run too, unless --no-model-rules"
        <| fun () ->
            use t =
                model (fun db ->
                    db.Model.Annotations.Add(
                        new Annotation(
                            Name = "BestPracticeAnalyzer",
                            Value = ruleJson [ [ "ID", box "MINE"; "Scope", box "Model"; "Expression", box "true" ] ]
                        )
                    ))

            let r = semctl [ "bpa"; t.Combine "model.bim"; "--no-default-rules"; "--json" ]
            let doc = json r
            Expect.equal (ids doc "findings") [ "MINE" ] ""

            let r =
                semctl [ "bpa"; t.Combine "model.bim"; "--no-default-rules"; "--no-model-rules" ]

            Expect.equal r.Code ExitCode.ok r.Err

        testCase "bad rule files are input errors"
        <| fun () ->
            use t = model ignore

            let r =
                semctl [ "bpa"; t.Combine "model.bim"; "-r"; rulesFile t "{ nope"; "--json" ]

            Expect.equal r.Code ExitCode.input ""
            let doc = json r
            Expect.equal (doc.GetProperty("error").GetProperty("code").GetString()) "invalid-rules" ""

            let r = semctl [ "bpa"; t.Combine "model.bim"; "-r"; t.Combine "missing.json" ]
            Expect.equal r.Code ExitCode.input ""
            Expect.stringContains r.Err "missing.json" ""

        testCase "--trx writes one test per rule"
        <| fun () ->
            use t = model ignore

            let rules =
                rulesFile
                    t
                    (ruleJson [
                        [ "ID", box "PASS"; "Scope", box "Model"; "Expression", box "false" ]
                        [ "ID", box "FAIL"; "Scope", box "Measure"; "Expression", box "IsHidden" ]
                        [ "ID", box "BROKEN"; "Scope", box "Measure"; "Expression", box "Nope" ]
                        [
                            "ID", box "NEWER"
                            "Scope", box "Model"
                            "Expression", box "true"
                            "CompatibilityLevel", box 9999
                        ]
                    ])

            let trx = t.Combine "out.trx"

            let r =
                semctl [
                    "bpa"
                    t.Combine "model.bim"
                    "--no-default-rules"
                    "-r"
                    rules
                    "--trx"
                    trx
                    "--fail-on"
                    "info"
                ]

            Expect.equal r.Code ExitCode.findings r.Err
            let doc = XDocument.Load trx
            let ns = XNamespace.Get "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

            let outcomes = [
                for x in doc.Descendants(ns + "UnitTestResult") ->
                    x.Attribute(XName.Get "testName").Value.Split(':').[0], x.Attribute(XName.Get "outcome").Value
            ]

            Expect.equal
                outcomes
                [
                    "PASS", "Passed"
                    "FAIL", "Failed"
                    "BROKEN", "Inconclusive"
                    "NEWER", "NotExecuted"
                ]
                ""

            let counters = doc.Descendants(ns + "Counters") |> Seq.exactlyOne
            Expect.equal (counters.Attribute(XName.Get "total").Value) "4" ""
            Expect.equal (counters.Attribute(XName.Get "failed").Value) "1" ""

        testCase "bpa works on a PBIP project"
        <| fun () ->
            use p = project ()
            let r = semctl [ "bpa"; p.Path; "--json" ]
            // The fixture project has warnings and info but no errors, so the default --fail-on passes.
            Expect.equal r.Code ExitCode.ok r.Err
            let doc = json r
            Expect.isGreaterThan (doc.GetProperty("rules").GetProperty("total").GetInt32()) 0 ""

        testCase "help lists bpa"
        <| fun () -> Expect.stringContains (semctl [ "--help" ]).Out "bpa" ""

        testCase "--fail-on: by default only errors fail the run"
        <| fun () ->
            use t = model ignore

            let rules =
                rulesFile
                    t
                    (ruleJson [
                        [
                            "ID", box "WARN"
                            "Scope", box "Measure"
                            "Expression", box "true"
                            "Severity", box 2
                        ]
                    ])

            let run extra =
                (semctl ([ "bpa"; t.Combine "model.bim"; "--no-default-rules"; "-r"; rules ] @ extra)).Code

            Expect.equal (run []) ExitCode.ok "warnings alone pass"
            Expect.equal (run [ "--fail-on"; "warning" ]) ExitCode.findings ""
            Expect.equal (run [ "--fail-on"; "nope" ]) ExitCode.usage ""

        testCase "--ci annotations, most severe first"
        <| fun () ->
            use t = model ignore
            let r = semctl [ "bpa"; t.Combine "model.bim"; "--ci"; "github" ]
            let first = r.Err.Split('\n') |> Array.find (fun l -> l.StartsWith "::")
            Expect.stringStarts first "::error title=SM_" ""
    ]
