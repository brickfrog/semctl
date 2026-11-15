module Tessera.Tests.ValidateSemanticTests

open Expecto
open Microsoft.AnalysisServices.Tabular
open Tessera
open Tessera.Tests.Fixtures

let private check (db: Database) =
    ValidateSemantic.check (Deps.build db) db

/// (rule, severity, ref, line, column) of each issue, for one rule.
let private issuesOf (rule: string) (db: Database) =
    check db
    |> List.filter (fun i -> i.Rule = rule)
    |> List.map (fun i -> ObjectRef.format i.Ref, i.Severity, i.Line, i.Column)

/// The sample model with measures M1, M2, ... in Sales holding these expressions.
let private withMeasures (exprs: string list) =
    let db = sampleDatabase ()

    exprs
    |> List.iteri (fun i e -> db.Model.Tables.["Sales"].Measures.Add(new Measure(Name = $"M{i + 1}", Expression = e)))

    db

let private flagged (rule: string) (expr: string) =
    withMeasures [ expr ] |> issuesOf rule |> List.isEmpty |> not

/// Add a calendar to the Date table, tagging (time unit, column) pairs.
let private addCalendar (db: Database) (name: string) (groups: (TimeUnit * string) list) =
    let t = db.Model.Tables.["Date"]
    let cal = new Calendar(Name = name)

    for unit, column in groups do
        cal.CalendarColumnGroups.Add(new TimeUnitColumnAssociation(unit, PrimaryColumn = t.Columns.[column]))

    t.Calendars.Add cal

let private addWeekColumn (db: Database) =
    db.Model.Tables.["Date"].Columns.Add(
        new DataColumn(Name = "Week", DataType = DataType.Int64, SourceColumn = "Week")
    )

[<Tests>]
let tests =
    testList "Validate (semantic)" [
        test "the sample model has none of these issues" { Expect.isEmpty (check (sampleDatabase ())) "" }

        test "every rule is listed once, after Validate's" {
            let ids = ValidateSemantic.allRules |> List.map fst
            Expect.equal (List.distinct ids) ids "unique"

            Expect.equal
                (ids |> List.take Validate.rules.Length)
                (Validate.rules |> List.map fst)
                "Validate's rules first"
        }

        testList "invalid-culture" [
            // TE3 3.27.1: "rejected with an error such as `'en US' is an invalid culture specifier`".
            test "a malformed model culture or translation language is an error" {
                let db = sampleDatabase ()
                db.Model.Culture <- "en US"
                db.Model.Cultures.Add(new Culture(Name = "fr FR"))

                let issues = check db |> List.filter (fun i -> i.Rule = "invalid-culture")

                Expect.equal
                    (issues |> List.map (fun i -> ObjectRef.format i.Ref, i.Severity, i.Property))
                    [
                        "model", Validate.Severity.Error, Some "culture"
                        "culture:fr FR", Validate.Severity.Error, Some "name"
                    ]
                    ""

                Expect.stringContains issues.Head.Message "'en US' is not a valid culture name" ""
            }

            test "malformed: decided by the characters, the same on every system" {
                for name in
                    [
                        "en US"
                        "en-"
                        "-US"
                        "en--US"
                        "en.US"
                        "en-US "
                        "énglish"
                        "toolongsubtag-US"
                    ] do
                    let db = sampleDatabase ()
                    db.Model.Culture <- name

                    Expect.equal
                        (issuesOf "invalid-culture" db |> List.map (fun (_, s, _, _) -> s))
                        [ Validate.Severity.Error ]
                        name
            }

            test "well-formed names are fine, in any case" {
                let db = sampleDatabase ()
                db.Model.Culture <- "EN-us"
                db.Model.Cultures.Add(new Culture(Name = "fr-FR"))
                db.Model.Cultures.Add(new Culture(Name = "pt-BR"))
                Expect.isEmpty (issuesOf "invalid-culture" db) ""
            }

            test "an empty model culture is fine" {
                let db = sampleDatabase ()
                db.Model.Culture <- ""
                Expect.isEmpty (issuesOf "invalid-culture" db) ""
            }

            test "the errors are the same with or without the system's culture list: only malformed names" {
                let db = sampleDatabase ()
                db.Model.Culture <- "English"

                // Well-formed, so never an error (it may be a warning where the system knows its cultures).
                Expect.isFalse
                    (issuesOf "invalid-culture" db
                     |> List.exists (fun (_, s, _, _) -> s = Validate.Severity.Error))
                    ""
            }
        ]

        testList "calculate-variable" [
            // TE3 3.18.0: "a warning if a variable is used directly as the 1st argument of CALCULATE or
            // CALCULATETABLE".
            test "a variable as CALCULATE's or CALCULATETABLE's first argument" {
                let db =
                    withMeasures [
                        "VAR x = [Total] RETURN CALCULATE(x, 'Date'[Year] = 2025)"
                        "VAR t = FILTER(Sales, Sales[Amount] > 1)\nRETURN COUNTROWS(CALCULATETABLE(t, 'Date'[Year] = 2025))"
                        "VAR x = [Total] RETURN calculate ( ( x ) )"
                    ]

                Expect.equal
                    (issuesOf "calculate-variable" db)
                    [
                        "measure:Sales/M1", Validate.Severity.Warning, Some 1, Some 24
                        "measure:Sales/M2", Validate.Severity.Warning, Some 2, Some 18
                        "measure:Sales/M3", Validate.Severity.Warning, Some 1, Some 24
                    ]
                    ""
            }

            test "variables as filter arguments, nested in the first argument, or out of scope are fine" {
                for expr in
                    [
                        "VAR t = FILTER(Sales, Sales[Amount] > 1) RETURN CALCULATE([Total], t)"
                        "VAR x = 1 RETURN CALCULATE(x + [Total])"
                        "VAR x = 1 RETURN CALCULATE(SUMX(Sales, x))"
                        "CALCULATE(Sales, 'Date'[Year] = 2025)"
                        "CALCULATE((VAR x = 1 RETURN x) + [Total])"
                        "CALCULATE([Total])"
                    ] do
                    Expect.isFalse (flagged "calculate-variable" expr) expr
            }

            test "a function's parameter is not a variable, but a VAR that shadows it is" {
                let db = sampleDatabase ()
                db.Model.Functions.Add(new Function(Name = "F1", Expression = "(e: EXPR) => CALCULATE(e, ALL(Sales))"))

                db.Model.Functions.Add(
                    new Function(Name = "F2", Expression = "(e: EXPR) => VAR e = 1 RETURN CALCULATE(e, ALL(Sales))")
                )

                Expect.equal (issuesOf "calculate-variable" db |> List.map (fun (r, _, _, _) -> r)) [ "function:F2" ] ""
            }
        ]

        testList "ignore-outside-summarizecolumns" [
            // TE3 3.26.2: "an error when IGNORE is used anywhere other than directly as an expression
            // argument of SUMMARIZECOLUMNS, matching engine behavior".
            test "IGNORE anywhere else is an error" {
                for expr in
                    [
                        "CALCULATE(IGNORE([Total]))"
                        "IGNORE([Total])"
                        "COUNTROWS(SUMMARIZECOLUMNS('Date'[Year], \"a\", IGNORE([Total]) + 1))"
                        "COUNTROWS(SUMMARIZECOLUMNS('Date'[Year], \"a\", CALCULATE(IGNORE([Total]))))"
                        "COUNTROWS(SUMMARIZECOLUMNS(IGNORE('Date'[Year])))"
                        "COUNTROWS(ADDCOLUMNS(VALUES('Date'[Year]), \"a\", IGNORE([Total])))"
                    ] do
                    let issues = withMeasures [ expr ] |> issuesOf "ignore-outside-summarizecolumns"
                    Expect.equal (issues |> List.map (fun (_, s, _, _) -> s)) [ Validate.Severity.Error ] expr
            }

            test "directly as an expression argument of SUMMARIZECOLUMNS it is fine" {
                for expr in
                    [
                        "COUNTROWS(SUMMARIZECOLUMNS('Date'[Year], \"a\", IGNORE([Total])))"
                        "COUNTROWS(SUMMARIZECOLUMNS('Date'[Year], \"a\", [Total], \"b\", ( IGNORE([Total]) )))"
                        "COUNTROWS(summarizecolumns('Date'[Year], \"a\", ignore([Total])))"
                    ] do
                    Expect.isFalse (flagged "ignore-outside-summarizecolumns" expr) expr
            }

            test "DAX that doesn't parse is left to other rules" {
                Expect.isFalse (flagged "ignore-outside-summarizecolumns" "IGNORE([Total]") ""
            }
        ]

        testList "week-without-weekly-calendar" [
            // TE3 3.24.0: a warning on "week-based DAX time intelligence functions, if the current
            // model does not contain a calendar with weekly granularity".
            test "nothing without calendars" {
                Expect.isFalse (flagged "week-without-weekly-calendar" "TOTALWTD([Total], 'Date'[DateKey])") ""
            }

            test "a model whose calendars have no weeks: every week function" {
                let db =
                    withMeasures [
                        "TOTALWTD([Total], 'Date'[DateKey])"
                        "COUNTROWS(DATESWTD('Date'[DateKey]))"
                        "CLOSINGBALANCEWEEK([Total], 'Date'[DateKey])"
                        "TOTALMTD([Total], 'Date'[DateKey])"
                    ]

                addCalendar db "Gregorian" [ TimeUnit.Year, "Year"; TimeUnit.Date, "DateKey" ]

                Expect.equal
                    (issuesOf "week-without-weekly-calendar" db
                     |> List.map (fun (r, s, _, _) -> r, s))
                    [
                        "measure:Sales/M1", Validate.Severity.Warning
                        "measure:Sales/M2", Validate.Severity.Warning
                        "measure:Sales/M3", Validate.Severity.Warning
                    ]
                    ""
            }

            test "a weekly calendar somewhere: only a week function over a calendar without weeks" {
                let db =
                    withMeasures [
                        "TOTALWTD([Total], 'Gregorian')"
                        "TOTALWTD([Total], Weekly)"
                        "TOTALWTD([Total], 'Date'[DateKey])"
                        "VAR Gregorian = 1 RETURN TOTALWTD([Total], Gregorian)"
                    ]

                addWeekColumn db
                addCalendar db "Gregorian" [ TimeUnit.Year, "Year"; TimeUnit.Date, "DateKey" ]
                addCalendar db "Weekly" [ TimeUnit.Week, "Week"; TimeUnit.Date, "DateKey" ]

                let issues =
                    check db |> List.filter (fun i -> i.Rule = "week-without-weekly-calendar")

                Expect.equal (issues |> List.map (fun i -> ObjectRef.format i.Ref)) [ "measure:Sales/M1" ] ""
                Expect.stringContains issues.Head.Message "'Gregorian' has none" ""
            }

            test "Week of Year needs a Year to tell weeks apart" {
                let db = withMeasures [ "TOTALWTD([Total], 'Fiscal')" ]
                addWeekColumn db
                addCalendar db "Fiscal" [ TimeUnit.WeekOfYear, "Week"; TimeUnit.Date, "DateKey" ]
                Expect.isNonEmpty (issuesOf "week-without-weekly-calendar" db) "no Year"

                let db = withMeasures [ "TOTALWTD([Total], 'Fiscal')" ]
                addWeekColumn db

                addCalendar db "Fiscal" [ TimeUnit.Year, "Year"; TimeUnit.WeekOfYear, "Week"; TimeUnit.Date, "DateKey" ]

                Expect.isEmpty (issuesOf "week-without-weekly-calendar" db) "with Year"
            }
        ]

        test "validateGraph merges both rule sets in model order" {
            let db = withMeasures [ "VAR x = 1 RETURN CALCULATE(x)" ]
            db.Model.Tables.["Sales"].Measures.Add(new Measure(Name = "Broken", Expression = "[Nope]"))
            let g = Deps.build db

            Expect.equal
                (ValidateSemantic.validateGraph g db |> List.map (fun i -> i.Rule))
                [ "calculate-variable"; "unresolved-reference" ]
                ""
        }

        test "the conformance corpus's models have no errors from these rules" {
            match Conformance.corpusDirectory with
            | None -> skiptest "no corpus"
            | Some dir ->
                let corpus = Conformance.load dir
                Expect.isNonEmpty corpus.Cases "cases"

                let mutable checkedModels = 0

                let errors = [
                    for c in corpus.Cases |> List.distinctBy (fun c -> c.ModelKey) do
                        match Conformance.prepared c with
                        | Ok p ->
                            checkedModels <- checkedModels + 1

                            for i in check p.Database do
                                if i.Severity = Validate.Severity.Error then
                                    $"{c.Id}: {ObjectRef.format i.Ref}: {i.Message}"
                        | Error _ -> ()
                ]

                Expect.isGreaterThan checkedModels 10 "models checked"
                Expect.isEmpty errors ""
        }
    ]
