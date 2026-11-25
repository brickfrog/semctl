module Semctl.Tests.BpaFixTests

open Expecto
open Microsoft.AnalysisServices.Tabular
open Semctl
open Semctl.BpaFix
open Semctl.Tests.Fixtures

/// The FixExpressions of the published rule files, verbatim (as the JSON decodes them):
/// TabularEditor/BestPracticeRules (BPARules-standard.json, -standard-lax.json, -PowerBI.json) and
/// microsoft/Analysis-Services (BestPracticeRules/BPARules.json), as of 2026-11.
let communityFixes = [
    "IsAvailableInMDX = false", Ok(Action.Assign("IsAvailableInMDX", Literal.Bool false))
    "DataType = DataType.Decimal", Ok(Action.Assign("DataType", Literal.Enum("DataType", "Decimal")))
    "SummarizeBy = AggregateFunction.None", Ok(Action.Assign("SummarizeBy", Literal.Enum("AggregateFunction", "None")))
    "IsHidden = true", Ok(Action.Assign("IsHidden", Literal.Bool true))
    "TranslatedDisplayFolders.Reset()", Ok Action.ResetDisplayFolderTranslations
    "Delete()", Ok Action.Delete
    "IsAvailableInMDX = true", Ok(Action.Assign("IsAvailableInMDX", Literal.Bool true))
    "FormatString = \"mm/dd/yyyy\"", Ok(Action.Assign("FormatString", Literal.Text "mm/dd/yyyy"))
    "FormatString = \"MMMM yyyy\"", Ok(Action.Assign("FormatString", Literal.Text "MMMM yyyy"))
    // C#'s \u escape: the rule writes ; this way because TE skips fix expressions containing ;.
    "FormatString = \"#,0.0%\\u003B-#,0.0%\\u003B#,0.0%\"",
    Ok(Action.Assign("FormatString", Literal.Text "#,0.0%;-#,0.0%;#,0.0%"))
    "FormatString = \"#,0\"", Ok(Action.Assign("FormatString", Literal.Text "#,0"))
    "IsKey = true", Ok(Action.Assign("IsKey", Literal.Bool true))
]

/// The ones sm refuses, with a word of the reason.
let refusedCommunityFixes = [
    // BPARules-standard-lax.json's typo: TE's C# wouldn't compile either.
    "TranslatedDisplayFolder.Reset()", "not a method"
    "Name = string.Concat( it.Name.ToCharArray().Select( c => (char.IsControl(c) && !char.IsWhiteSpace(c)) ? ' ': c ))",
    "only literal values"
    "Description = string.Concat( it.Description.ToCharArray().Select( c => (char.IsControl(c) && !char.IsWhiteSpace(c)) ? ' ': c ))",
    "only literal values"
    "Partitions[0].Name = it.Name", "expected"
]

/// A rule with a fix expression, scoped as given.
let private rule (id: string) (scope: string) (expression: string) (fix: string) =
    let json =
        BpaTests.ruleJson [
            [
                "ID", box id
                "Scope", box scope
                "Expression", box expression
                "FixExpression", box fix
            ]
        ]

    match BpaRules.parse BpaRules.BuiltIn json with
    | Ok [ r ] -> r
    | other -> failtestf "%A" other

/// Analyze with these rules, apply their fixes; the result and each fixed or refused finding.
let private fixWith (db: Database) (rules: BpaRules.Rule list) =
    let findings = Bpa.analyze db rules |> List.collect (fun r -> r.Findings)

    match BpaFix.apply db findings with
    | Ok r -> r
    | Error e -> failtest (SemctlError.message e)

let private refusals (r: FixResult) =
    FixResult.refused r
    |> List.map (fun o -> o.Finding.Rule.Id, ObjectRef.format o.Finding.Ref, o.Refused.Value)

let private column (db: Database) table name = db.Model.Tables.[table].Columns.[name]
let private measure (db: Database) table name = db.Model.Tables.[table].Measures.[name]

[<Tests>]
let parseTests =
    testList "BPA fix expressions: parsing" [
        test "every community fix expression sm applies" {
            for text, expected in communityFixes do
                Expect.equal (parse text) expected text
        }

        test "the community fix expressions sm refuses" {
            for text, reason in refusedCommunityFixes do
                match parse text with
                | Error e -> Expect.stringContains e reason text
                | Ok a -> failtestf "%s: parsed as %A" text a
        }

        test "C# string literals" {
            let text (fix: string) =
                match parse fix with
                | Ok(Action.Assign(_, Literal.Text t)) -> t
                | other -> failtestf "%s: %A" fix other

            Expect.equal (text "FormatString = \"a\\\"b\\\\c\\td\"") "a\"b\\c\td" "simple escapes"
            Expect.equal (text "FormatString = \"\\x41\\U0001F600\"") "A\U0001F600" "\\x and \\U"
            Expect.equal (text "FormatString = @\"C:\\path \"\"q\"\"\"") "C:\\path \"q\"" "verbatim"
            Expect.equal (text "FormatString=\"0\"") "0" "no spaces"
        }

        test "what TE itself won't run, or isn't a plain statement" {
            for fix in
                [
                    "IsHidden = true; IsKey = true" // TE skips ; { }
                    "{ IsHidden = true }"
                    "IsHidden == true"
                    "IsHidden = True" // C# is case-sensitive
                    "IsHidden = !IsHidden"
                    "IsHidden = it.IsHidden"
                    "FormatString = \"0\" + \"%\""
                    "FormatString = \"unterminated"
                    "FormatString = \"\\q\""
                    "Table.IsHidden = true"
                    "Delete(true)"
                    "Clear()"
                    ""
                ] do
                Expect.isError (parse fix) fix
        }
    ]

[<Tests>]
let applyTests =
    testList "BPA fix expressions: applying" [
        test "assignments, with TE's side effects: a new format string drops the Format annotation" {
            let db = sampleDatabase ()
            let amount = column db "Sales" "Amount"
            amount.Annotations.Add(new Annotation(Name = "Format", Value = "<Format />"))
            amount.SummarizeBy <- AggregateFunction.Sum

            let r =
                fixWith db [
                    rule "FMT" "DataColumn" "Name = \"Amount\"" "FormatString = \"#,0.0%\\u003B-#,0.0%\""
                    rule "SUM" "DataColumn" "Name = \"Amount\"" "SummarizeBy = AggregateFunction.None"
                    rule "TYPE" "DataColumn" "Name = \"Amount\"" "DataType = DataType.Double"
                    rule "HIDE" "DataColumn" "Name = \"Amount\"" "IsHidden = true"
                    rule "MDX" "DataColumn" "Name = \"Amount\"" "IsAvailableInMDX = false"
                ]

            Expect.isEmpty (refusals r) ""
            Expect.equal (FixResult.fixedFindings r).Length 5 ""
            Expect.equal amount.FormatString "#,0.0%;-#,0.0%" ""
            Expect.isFalse (amount.Annotations.Contains "Format") "Format annotation removed"
            Expect.equal amount.SummarizeBy AggregateFunction.None ""
            Expect.equal amount.DataType DataType.Double ""
            Expect.isTrue amount.IsHidden ""
            Expect.isFalse amount.IsAvailableInMDX ""
        }

        // META_AVOID_FLOAT's scope includes calculated and calculated table columns. TE's wrapper sets
        // DataType on any column (no IsEditable override in TOMWrapper/Column.cs), and so does sm.
        test "DataType on calculated and calculated table columns, as TE sets it" {
            let db = sampleDatabase ()
            let sales = db.Model.Tables.["Sales"]

            sales.Columns.Add(
                new CalculatedColumn(Name = "Twice", DataType = DataType.Double, Expression = "Sales[Amount] * 2")
            )

            let ct = new Table(Name = "Copy")

            ct.Columns.Add(
                new CalculatedTableColumn(
                    Name = "X",
                    DataType = DataType.Double,
                    SourceColumn = "[X]",
                    IsNameInferred = true
                )
            )

            ct.Partitions.Add(
                new Partition(
                    Name = "Copy",
                    Source = new CalculatedPartitionSource(Expression = "SELECTCOLUMNS(Sales, \"X\", Sales[Amount])")
                )
            )

            db.Model.Tables.Add ct

            let r =
                fixWith db [
                    rule
                        "FLOAT"
                        "CalculatedColumn, CalculatedTableColumn"
                        "DataType = \"Double\""
                        "DataType = DataType.Decimal"
                ]

            Expect.isEmpty (refusals r) ""
            Expect.equal (FixResult.fixedFindings r).Length 2 ""
            Expect.equal (column db "Sales" "Twice").DataType DataType.Decimal ""
            Expect.equal (column db "Copy" "X").DataType DataType.Decimal ""
        }

        test "IsKey = true clears IsKey on the table's other columns" {
            let db = sampleDatabase ()
            (column db "Date" "Year").IsKey <- true

            let r =
                fixWith db [
                    rule "KEY" "DataColumn" "Name = \"DateKey\" and Table.Name = \"Date\"" "IsKey = true"
                ]

            Expect.isEmpty (refusals r) ""
            Expect.isTrue (column db "Date" "DateKey").IsKey ""
            Expect.isFalse (column db "Date" "Year").IsKey ""
        }

        test "values the property doesn't take, and properties sm doesn't set, are refused untouched" {
            let db = sampleDatabase ()

            let r =
                fixWith db [
                    rule "ENUM" "DataColumn" "Name = \"Amount\"" "DataType = AggregateFunction.Sum"
                    rule "MEMBER" "DataColumn" "Name = \"Amount\"" "DataType = DataType.Automatic"
                    rule "NOPE" "DataColumn" "Name = \"Amount\"" "SummarizeBy = AggregateFunction.Nope"
                    rule "BOOL" "DataColumn" "Name = \"Amount\"" "IsHidden = \"yes\""
                    rule "OTHER" "DataColumn" "Name = \"Amount\"" "SourceColumn = \"x\""
                    rule "NAME" "DataColumn" "Name = \"Amount\"" "Name = \"Total\""
                    rule "WRONG" "Measure" "Name = \"Total\"" "IsAvailableInMDX = false"
                ]

            let reasons = refusals r |> List.map (fun (id, _, why) -> id, why) |> Map.ofList
            Expect.equal reasons.Count 7 (sprintf "%A" reasons)
            Expect.stringContains reasons.["ENUM"] "takes a DataType value" ""
            Expect.stringContains reasons.["MEMBER"] "isn't a value dataType takes" ""
            Expect.stringContains reasons.["NOPE"] "isn't a value summarizeBy takes" ""
            Expect.stringContains reasons.["BOOL"] "true or false" ""
            Expect.stringContains reasons.["OTHER"] "not a property sm sets" ""
            Expect.stringContains reasons.["NAME"] "sm rename" ""
            Expect.stringContains reasons.["WRONG"] "has no IsAvailableInMDX" ""
            let amount = column db "Sales" "Amount"
            Expect.equal (amount.DataType, amount.IsHidden) (DataType.Decimal, false) "unchanged"
        }

        test "two rules setting one property differently: neither applies; the same value: once" {
            let db = sampleDatabase ()

            let r =
                fixWith db [
                    rule "A" "Measure" "Name = \"Total\"" "FormatString = \"0.0\""
                    rule "B" "Measure" "Name = \"Total\"" "FormatString = \"0.00\""
                    rule "C" "Measure" "Name = \"Total\"" "IsHidden = true"
                    rule "D" "Measure" "Name = \"Total\"" "IsHidden = true"
                ]

            Expect.equal
                (refusals r |> List.map (fun (id, _, why) -> id, why.Contains "different value"))
                [ "A", true; "B", true ]
                ""

            Expect.equal (measure db "Sales" "Total").FormatString "#,0" "unchanged"
            Expect.isTrue (measure db "Sales" "Total").IsHidden ""

            Expect.equal (FixResult.fixedFindings r |> List.map (fun o -> o.Finding.Rule.Id)) [ "C"; "D" ] ""
        }

        test "Delete() removes what nothing refers to, with its links, and refuses the rest" {
            let db = sampleDatabase ()

            let r =
                fixWith db [
                    rule "DEL" "Measure" "Name = \"Total (hidden)\" or Name = \"Total\"" "Delete()"
                    rule "DELCOL" "DataColumn" "Name = \"Month\"" "Delete()"
                ]

            // Findings come in model order: Total is refused while Total (hidden) still refers to it.
            Expect.equal (refusals r |> List.map (fun (id, ref, _) -> id, ref)) [ "DEL", "measure:Sales/Total" ] ""

            Expect.isFalse (db.Model.Tables.["Sales"].Measures.ContainsName "Total (hidden)") "removed"
            Expect.isFalse (db.Model.Tables.["Date"].Columns.ContainsName "Month") "removed"

            Expect.isFalse
                (db.Model.Tables.["Date"].Hierarchies.["Calendar"].Levels.ContainsName "Month")
                "its hierarchy level went with it"

            Expect.exists r.Notes (fun n -> n.StartsWith "Also removed") "notes say what else went"
        }

        test "Delete() on a KPI, a table permission or a role member: refused, its parent left alone" {
            let db = sampleDatabase ()
            let total = measure db "Sales" "Total"
            total.KPI <- new KPI(TargetExpression = "1")
            let role = db.Model.Roles.["Readers"]

            role.TablePermissions.Add(
                new TablePermission(Table = db.Model.Tables.["Sales"], FilterExpression = "TRUE()")
            )

            let r =
                fixWith db [
                    rule "KPI" "KPI" "true" "Delete()"
                    rule "TP" "TablePermission" "true" "Delete()"
                ]

            Expect.equal
                (refusals r
                 |> List.map (fun (id, ref, why) -> id, ref, why.Contains "isn't an object sm can edit"))
                [ "KPI", "measure:Sales/Total", true; "TP", "role:Readers", true ]
                ""

            Expect.isNotNull total.KPI "the measure and its KPI stay"
            Expect.equal role.TablePermissions.Count 1 "the role and its permission stay"
        }

        test "TranslatedDisplayFolders.Reset(): every culture's folder becomes the base folder" {
            let db = sampleDatabase ()
            let total = measure db "Sales" "Total"
            total.DisplayFolder <- "Base"
            db.Model.Cultures.Add(new Culture(Name = "fr-FR"))
            let da = db.Model.Cultures.["da-DK"]

            da.ObjectTranslations.Add(
                new ObjectTranslation(Object = total, Property = TranslatedProperty.DisplayFolder, Value = "Grund")
            )

            let r =
                fixWith db [ rule "DF" "Measure" "Name = \"Total\"" "TranslatedDisplayFolders.Reset()" ]

            Expect.isEmpty (refusals r) ""

            for c in db.Model.Cultures do
                Expect.equal c.ObjectTranslations.[total, TranslatedProperty.DisplayFolder].Value "Base" c.Name
        }

        test "findings without a fix expression aren't outcomes; ignored findings never reach apply" {
            let db = sampleDatabase ()

            let noFix =
                match
                    BpaRules.parse
                        BpaRules.BuiltIn
                        (BpaTests.ruleJson [ [ "ID", box "X"; "Scope", box "Measure"; "Expression", box "true" ] ])
                with
                | Ok [ r ] -> r
                | other -> failtestf "%A" other

            let r = fixWith db [ noFix ]
            Expect.isEmpty r.Outcomes ""
        }
    ]
