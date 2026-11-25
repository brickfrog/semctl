/// The formatter's regression corpus (tests/format-corpus, documented in its README.md): cases
/// taken from public formatter bug reports and SQLBI's rules, each with its source.
module Semctl.Tests.FormatCorpusTests

open System
open System.IO
open System.Text.Json.Nodes
open Expecto
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.Format
open Semctl.Tests.Fixtures

type private Case = {
    Name: string
    Input: string
    Expected: string
    Options: FormatOptions
    /// "same": sm writes the expected output; "differs": a known difference, with a note.
    Expect: string
    Source: string
    Note: string option
}

let private corpusDirectory =
    Conformance.repositoryRoot
    |> Option.map (fun root -> Path.Combine(root, "tests", "format-corpus"))
    |> Option.filter Directory.Exists

/// Line endings as LF, so a checkout with CRLF reads the same.
let private lf (s: string) = s.Replace("\r\n", "\n")

let private readCase (dir: string) : Case =
    let json = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "case.json")))
    let options = json.["options"]

    let flag (name: string) =
        match options.[name] with
        | null -> false
        | v -> v.GetValue<bool>()

    {
        Name = Path.GetFileName dir
        Input = lf (File.ReadAllText(Path.Combine(dir, "input.dax")))
        Expected = lf (File.ReadAllText(Path.Combine(dir, "expected.dax")))
        Options = {
            FormatOptions.defaults with
                Long = flag "long"
                Semicolons = flag "semicolons"
                SpaceAfterFunction = not (flag "noSpaceAfterFunction")
                Width =
                    match options.["width"] with
                    | null -> FormatOptions.defaults.Width
                    | v -> v.GetValue<int>()
        }
        Expect = json.["expect"].GetValue<string>()
        Source = json.["source"].GetValue<string>()
        Note =
            match json.["note"] with
            | null -> None
            | v -> Some(v.GetValue<string>())
    }

let private cases () =
    match corpusDirectory with
    | None -> failtest "tests/format-corpus not found"
    | Some dir ->
        Directory.GetDirectories dir
        |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> Array.map readCase
        |> List.ofArray

let private formatted (c: Case) (dax: string) =
    match format c.Options dax with
    | Ok s -> s
    | Error e -> failtestf "%s: format failed: %s" c.Name (FormatError.message e)

let private runCase (c: Case) =
    let actual = formatted c c.Input
    Expect.equal (formatted c actual) actual $"{c.Name}: formatting the result again changes nothing"

    match c.Expect with
    | "same" ->
        Expect.equal actual c.Expected $"{c.Name}: formatting input.dax gives expected.dax ({c.Source})"
        Expect.equal (formatted c c.Expected) c.Expected $"{c.Name}: expected.dax is formatted already"
    | "differs" ->
        Expect.notEqual
            actual
            c.Expected
            $"{c.Name} is listed as a known difference but now matches: set \"expect\": \"same\" and drop the note"

        Expect.isSome c.Note $"{c.Name}: a known difference says why"
    | other -> failtestf "%s: \"expect\" must be \"same\" or \"differs\", not %s" c.Name other

[<Tests>]
let tests =
    testList "Format: regression corpus (tests/format-corpus)" [
        testCase "the corpus is there, and every case cites its source"
        <| fun () ->
            let all = cases ()
            Expect.isGreaterThanOrEqual all.Length 10 "cases"

            for c in all do
                Expect.isTrue (c.Source.Contains "https://") $"{c.Name}: source is a URL"

        testCase "every case formats as its expected output says (or differs as recorded)"
        <| fun () ->
            for c in cases () do
                runCase c

        testCase "a call after a closing bracket starts a row, unless comments come first"
        <| fun () ->
            let opts = {
                FormatOptions.defaults with
                    Width = 40
            }

            let fmt dax =
                match format opts dax with
                | Ok s -> s
                | Error e -> failtestf "%s" (FormatError.message e)

            Expect.equal
                (fmt "calculate(sum(Sales[Amount]),all(Sales[Region]))+calculate(sum(Sales[Cost]))")
                (String.concat "\n" [
                    "CALCULATE ("
                    "    SUM ( Sales[Amount] ),"
                    "    ALL ( Sales[Region] )"
                    ")"
                    "    + CALCULATE ( SUM ( Sales[Cost] ) )"
                ])
                "the call starts a row after its operator"

            let withComment =
                fmt "calculate(sum(Sales[Amount]),all(Sales[Region]))+\n// the cost\ncalculate(sum(Sales[Cost]))"

            Expect.equal
                withComment
                (String.concat "\n" [
                    "CALCULATE ("
                    "    SUM ( Sales[Amount] ),"
                    "    ALL ( Sales[Region] )"
                    ") +"
                    "    // the cost"
                    "    CALCULATE ( SUM ( Sales[Cost] ) )"
                ])
                "the operator stays after the bracket, above the comment"

            Expect.equal (fmt withComment) withComment "settles"

        // https://github.com/TabularEditor/TabularEditor/issues/638: a formatter removed the line
        // break at the start of a measure; Tabular Editor's fix was to "retain any line breaks at
        // the start of the original expression".
        testCase "TE#638: a model expression that starts on the next line keeps starting there"
        <| fun () ->
            let db = sampleDatabase ()
            let sales = db.Model.Tables.["Sales"]
            sales.Measures.Add(new Measure(Name = "Below", Expression = "\nvar x = sum(Sales[Amount]) return x"))
            sales.Measures.Add(new Measure(Name = "BelowCrlf", Expression = "  \r\n\r\nsum(Sales[Amount])"))
            sales.Measures.Add(new Measure(Name = "Inline", Expression = "sum(Sales[Amount])"))

            let result =
                match formatModel FormatOptions.defaults db Selection.all with
                | Ok r -> r
                | Error e -> failtestf "%s" (SemctlError.message e)

            let after name =
                result.Outcomes
                |> List.find (fun o -> ObjectRef.format o.Source.Owner.Ref = $"measure:Sales/{name}")
                |> fun o -> o.Result

            Expect.equal (after "Below") (Ok "\nVAR x = SUM ( Sales[Amount] )\nRETURN\n    x") "one LF kept"
            Expect.equal (after "BelowCrlf") (Ok "\r\nSUM ( Sales[Amount] )") "one CRLF kept, blank lines dropped"
            Expect.equal (after "Inline") (Ok "SUM ( Sales[Amount] )") "nothing added"

            apply result.Outcomes
            let again = formatModel FormatOptions.defaults db Selection.all

            match again with
            | Ok r ->
                Expect.isEmpty
                    (r.Outcomes
                     |> List.filter Outcome.changed
                     |> List.filter (fun o -> o.Source.Owner.Ref.Kind = ObjectKind.Measure))
                    "settled"
            | Error e -> failtestf "%s" (SemctlError.message e)
    ]
