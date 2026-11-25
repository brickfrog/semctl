/// The unknown-function rule (Validate), its catalog (DaxFunctions), and the save gate and deploy
/// with ValidateSemantic's rules (SaveGate).
module Semctl.Tests.UnknownFunctionTests

open System
open Expecto
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.DaxAst
open Semctl.Tests.Fixtures

let private ok =
    function
    | Ok v -> v
    | Error e -> failtestf "%s" (SemctlError.message e)

let private unknown (db: Database) =
    Validate.validate db |> List.filter (fun i -> i.Rule = "unknown-function")

/// The sample model with measures M1, M2, ... in Sales holding these expressions.
let private withMeasures (exprs: string list) =
    let db = sampleDatabase ()

    exprs
    |> List.iteri (fun i e -> db.Model.Tables.["Sales"].Measures.Add(new Measure(Name = $"M{i + 1}", Expression = e)))

    db

let private addFunction (db: Database) (name: string) (expression: string) =
    db.Model.Functions.Add(new Function(Name = name, Expression = expression))

let private addMeasure (expression: string) (db: Database) =
    db.Model.Tables.["Sales"].Measures.Add(new Measure(Name = "Added", Expression = expression))
    Ok Mutation.Effect.none

let private samplePath = sampleModelPath ()

/// The calls in a parsed query: its definitions' and its EVALUATEs' expressions.
let private queryCalls (q: Query) =
    let calls refs =
        refs |> List.filter (fun (r: NameRef) -> r.Kind = NameKind.Function)

    [
        for d in q.Definitions do
            match d.Kind with
            | DefinitionKind.Measure(_, _, v)
            | DefinitionKind.Column(_, _, v)
            | DefinitionKind.Table(_, v)
            | DefinitionKind.Var(_, v)
            | DefinitionKind.MParameter(_, v) -> yield! calls (references v)
            | DefinitionKind.Function(_, f) -> yield! calls (functionReferences f)

        for e in q.Evaluates do
            for x in e.Table :: (e.OrderBy |> List.map (fun k -> k.Expr)) @ e.StartAt do
                yield! calls (references x)
    ]

[<Tests>]
let tests =
    testList "unknown-function" [
        testList "the catalog" [
            test "is upper case, sorted and has no duplicates" {
                Expect.equal DaxFunctions.all (DaxFunctions.all |> List.distinct |> List.sort) "sorted, distinct"

                for name in DaxFunctions.all do
                    Expect.equal name (name.ToUpperInvariant()) name

                    Expect.isTrue
                        (name
                         |> Seq.forall (fun c -> Char.IsAsciiLetterUpper c || Char.IsAsciiDigit c || c = '.'))
                        name

                // Learn's TOC has 460; dax.guide adds 24, the formatter's list 1.
                Expect.equal DaxFunctions.all.Length 485 "count"
            }

            test "has the newer and the deprecated functions, and none of DAX's keywords" {
                for name in
                    [
                        "WINDOW"
                        "OFFSET"
                        "INDEX"
                        "ROWNUMBER"
                        "INFO.VIEW.TABLES"
                        "INFO.USERDEFINEDFUNCTIONS"
                        "EVALUATEANDLOG"
                        "TOTALWTD"
                        "CLOSINGBALANCEWEEK"
                        "NAMEOF"
                        "TABLEOF"
                        "ISO.CEILING"
                        "IF.EAGER"
                        "KEYWORDMATCH"
                        "COLLAPSEALL"
                        "NOT"
                        "CURRENCY"
                        "TRUE"
                        "DISTINCT"
                        "sum"
                        "KMEANSCLUSTERING"
                        "INFO.SOMETHINGNEWER"
                    ] do
                    Expect.isTrue (DaxFunctions.isBuiltIn name) name

                for word in
                    [
                        "EVALUATE"
                        "DEFINE"
                        "VAR"
                        "RETURN"
                        "MEASURE"
                        "INTEGER"
                        "STRING"
                        "DENSIFY"
                        "IN"
                    ] do
                    Expect.isFalse (DaxFunctions.isBuiltIn word) word
            }

            test "has every function the engine implements" {
                // Names with a space are the engine's own (e.g. FORMAT STRING OF): DAX can't call them.
                let missing =
                    DaxEngine.Registry.byName.Keys
                    |> Seq.filter (fun n -> not (n.Contains ' ') && not (DaxFunctions.isBuiltIn n))
                    |> List.ofSeq

                Expect.isEmpty missing ""
            }
        ]

        testList "the rule" [
            test "a call to a function that doesn't exist is an error, at the call" {
                let issues = unknown (withMeasures [ "1 +\n  NoSuchFn ( 1 )" ])

                Expect.equal
                    (issues
                     |> List.map (fun i -> ObjectRef.format i.Ref, i.Severity, i.Property, i.Line, i.Column))
                    [
                        "measure:Sales/M1", Validate.Severity.Error, Some "expression", Some 2, Some 3
                    ]
                    ""

                Expect.equal issues.Head.Message "NoSuchFn is not a DAX function or a function of this model" ""
            }

            test "built-in functions in any case are fine, dotted ones too" {
                Expect.isEmpty
                    (unknown (
                        withMeasures [
                            "sum ( Sales[Amount] ) + Iso.Ceiling ( 2 ) + COUNTROWS ( info.view.tables () )"
                            "IF.EAGER ( TRUE (), 1, 2 )"
                        ]
                    ))
                    ""
            }

            test "the model's user-defined functions are fine, by any case, dots and all" {
                let db = withMeasures [ "My.Lib.Twice ( 2 ) + my.lib.TWICE ( 3 )"; "Other ( 1 )" ]
                addFunction db "My.Lib.Twice" "(x: INT64) => x * 2"

                Expect.equal
                    (unknown db |> List.map (fun i -> ObjectRef.format i.Ref, i.Message))
                    [
                        "measure:Sales/M2", "Other is not a DAX function or a function of this model"
                    ]
                    ""
            }

            test "a user-defined function's body and parameter defaults are checked" {
                let db = sampleDatabase ()
                addFunction db "F" "(x: INT64 = Nope1 ()) => Nope2 ( x )"

                Expect.equal
                    (unknown db |> List.map (fun i -> ObjectRef.format i.Ref, i.Message))
                    [
                        "function:F", "Nope1 is not a DAX function or a function of this model"
                        "function:F", "Nope2 is not a DAX function or a function of this model"
                    ]
                    ""
            }

            test "row-level security filters and calculated columns are checked" {
                let db = sampleDatabase ()
                db.Model.Tables.["Sales"].Columns.Add(new CalculatedColumn(Name = "Calc", Expression = "Nope ()"))
                let role = new ModelRole(Name = "R")
                db.Model.Roles.Add role

                role.TablePermissions.Add(
                    new TablePermission(Table = db.Model.Tables.["Sales"], FilterExpression = "Gone ( 1 )")
                )

                Expect.equal
                    (unknown db |> List.map (fun i -> ObjectRef.format i.Ref, i.Property))
                    [
                        "column:Sales/Calc", Some "expression"
                        "role:R", Some "tablePermissions.Sales.filterExpression"
                    ]
                    ""
            }

            test "DAX that doesn't parse isn't reported by this rule" {
                Expect.isEmpty (unknown (withMeasures [ "NoSuchFn ( 1" ])) ""
            }

            test "is listed with Validate's rules" {
                Expect.isTrue (Validate.rules |> List.exists (fun (id, _) -> id = "unknown-function")) ""
            }
        ]

        testList "the save gate" [
            test "an edit that adds a call to an unknown function introduces an error" {
                let applied = Mutation.apply (sampleDatabase ()) (addMeasure "NoSuchFn ( 1 )") |> ok

                Expect.equal
                    (applied.IntroducedErrors |> List.map (fun i -> i.Rule, ObjectRef.format i.Ref))
                    [ "unknown-function", "measure:Sales/Added" ]
                    ""
            }

            test "removing a function that DAX still calls introduces an error" {
                let db = withMeasures [ "My.Lib.Twice ( 2 )" ]
                addFunction db "My.Lib.Twice" "(x: INT64) => x * 2"
                Expect.isEmpty (Mutation.gatedErrors db) "before"

                let applied =
                    Mutation.apply db (fun db ->
                        db.Model.Functions.Remove "My.Lib.Twice" |> ignore
                        Ok Mutation.Effect.none)
                    |> ok

                Expect.equal
                    (applied.IntroducedErrors |> List.map (fun i -> i.Rule, ObjectRef.format i.Ref))
                    [ "unknown-function", "measure:Sales/M1" ]
                    ""
            }

            test "an unknown call the model already had is not introduced" {
                let applied =
                    Mutation.apply (withMeasures [ "NoSuchFn ( 1 )" ]) (addMeasure "1") |> ok

                Expect.isEmpty applied.IntroducedErrors ""
            }

            test "SaveGate adds ValidateSemantic's errors; Mutation alone doesn't" {
                let ignored = "COUNTROWS ( { IGNORE ( 1 ) } )"
                let viaSaveGate = SaveGate.apply (sampleDatabase ()) (addMeasure ignored) |> ok
                let viaMutation = Mutation.apply (sampleDatabase ()) (addMeasure ignored) |> ok

                Expect.equal
                    (viaSaveGate.IntroducedErrors |> List.map (fun i -> i.Rule))
                    [ "ignore-outside-summarizecolumns" ]
                    "SaveGate"

                Expect.isEmpty viaMutation.IntroducedErrors "Mutation"
            }

            test "a malformed culture gates; ValidateSemantic's warnings don't" {
                let culture =
                    SaveGate.apply (sampleDatabase ()) (fun db ->
                        db.Model.Culture <- "en US"
                        Ok Mutation.Effect.none)
                    |> ok

                Expect.equal (culture.IntroducedErrors |> List.map (fun i -> i.Rule)) [ "invalid-culture" ] "culture"

                let warning =
                    SaveGate.apply (sampleDatabase ()) (addMeasure "VAR x = 1 RETURN CALCULATE ( x )")
                    |> ok

                Expect.isEmpty warning.IntroducedErrors "calculate-variable is a warning"
            }

            test "a semantic error the model already had is not introduced, and is resolved when fixed" {
                let db = withMeasures [ "COUNTROWS ( { IGNORE ( 1 ) } )" ]

                Expect.equal
                    (SaveGate.gatedErrors db |> List.map (fun i -> i.Rule))
                    [ "ignore-outside-summarizecolumns" ]
                    ""

                let applied, resolved =
                    SaveGate.applyResolving db (fun db ->
                        db.Model.Tables.["Sales"].Measures.["M1"].Expression <- "1"
                        Ok Mutation.Effect.none)
                    |> ok

                Expect.isEmpty applied.IntroducedErrors "introduced"
                Expect.equal (resolved |> List.map (fun i -> i.Rule)) [ "ignore-outside-summarizecolumns" ] "resolved"
            }

            test "deploy counts ValidateSemantic's errors and unknown functions" {
                let target: Deploy.Target = {
                    Database = "D"
                    Deployed = None
                    CompatibilityMode = None
                    SupportedCompatibilityLevels = []
                }

                let local = withMeasures [ "COUNTROWS ( { IGNORE ( 1 ) } )"; "NoSuchFn ( 1 )" ]

                let rules (p: Deploy.Plan) =
                    p.IntroducedErrors |> List.map (fun i -> i.Rule) |> List.sort

                let full =
                    SaveGate.deployPlan Deploy.Options.defaults Diff.Options.none target local |> ok

                let plain = Deploy.plan Deploy.Options.defaults Diff.Options.none target local |> ok

                Expect.equal (rules full) [ "ignore-outside-summarizecolumns"; "unknown-function" ] "SaveGate"
                Expect.equal (rules plain) [ "unknown-function" ] "Deploy alone"
            }
        ]

        testList "the CLI's editing commands" [
            test "add --save refuses a syntax-tree rule's error, and an unknown function" {
                use p = CliHarness.project ()
                let model = IO.Path.Combine(p.Path, "Sales.SemanticModel")

                for expression, rule in
                    [
                        "COUNTROWS ( { IGNORE ( 1 ) } )", "ignore-outside-summarizecolumns"
                        "NoSuchFn ( 1 )", "unknown-function"
                    ] do
                    let r =
                        CliHarness.semctl [
                            "add"
                            model
                            "measure:Sales/Bad"
                            "--expression"
                            expression
                            "--save"
                            "--json"
                        ]

                    Expect.equal r.Code Cli.Common.ExitCode.findings (r.Out + r.Err)
                    let doc = CliHarness.json r
                    let introduced = doc.GetProperty("introducedErrors")
                    Expect.equal (introduced.GetArrayLength()) 1 r.Out
                    Expect.equal (introduced.[0].GetProperty("code").GetString()) rule r.Out

                    Expect.equal
                        (doc.GetProperty("error").GetProperty("code").GetString())
                        "change-introduces-errors"
                        r.Out

                let _, db = ModelIO.loadPath model |> ok
                Expect.isNull (db.Model.Tables.["Sales"].Measures.Find "Bad") "not saved"
            }
        ]

        testList "no false alarms" [
            test "the conformance corpus: every model and every query" {
                match Conformance.corpusDirectory with
                | None -> skiptest "no corpus"
                | Some dir ->
                    let corpus = Conformance.load dir
                    let mutable queries = 0
                    let mutable models = 0

                    let problems = [
                        for c in corpus.Cases do
                            match Conformance.prepared c with
                            | Error _ -> ()
                            | Ok p ->
                                let udfs = [ for f in p.Database.Model.Functions -> f.Name ]

                                match DaxParser.parseQuery c.Query with
                                | Error e -> $"{c.Id}: the query doesn't parse: {e}"
                                | Ok q ->
                                    queries <- queries + 1

                                    let defined = [
                                        for d in q.Definitions do
                                            match d.Kind with
                                            | DefinitionKind.Function(name, _) -> name.Name
                                            | _ -> ()
                                    ]

                                    for call in Validate.unknownCalls (udfs @ defined) (queryCalls q) do
                                        $"{c.Id}: {call.Name}"

                        for c in corpus.Cases |> List.distinctBy (fun c -> c.ModelKey) do
                            match Conformance.prepared c with
                            | Error _ -> ()
                            | Ok p ->
                                models <- models + 1

                                for i in unknown p.Database do
                                    $"{c.Id}: model: {ObjectRef.format i.Ref}: {i.Message}"
                    ]

                    Expect.isEmpty problems ""
                    Expect.isGreaterThan queries 800 "queries checked"
                    Expect.isGreaterThan models 10 "models checked"
            }

            test "the sample model (SEMCTL_SAMPLE_MODEL): every expression parses and calls only known functions" {
                match samplePath with
                | None -> skiptest "SEMCTL_SAMPLE_MODEL is not set"
                | Some path ->
                    let _, db = ModelIO.loadPath path |> ok
                    let g = Deps.build db
                    let sources = Deps.daxSources g.Entries

                    let unparsed = [
                        for s in sources do
                            if not (String.IsNullOrWhiteSpace s.Dax) && (Validate.callsIn s).IsNone then
                                $"{ObjectRef.format s.Owner.Ref} {s.Property}"
                    ]

                    Expect.isEmpty unparsed "every expression parses"
                    Expect.isGreaterThan sources.Length 50 "expressions checked"
                    Expect.isEmpty (unknown db |> List.map (fun i -> i.Message)) ""
            }
        ]
    ]
