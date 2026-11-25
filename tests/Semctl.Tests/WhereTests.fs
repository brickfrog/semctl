/// `get <container> --where`: Semctl.Where and the CLI.
module Semctl.Tests.WhereTests

open System.Text.Json
open Expecto
open Semctl
open Semctl.Tests.Fixtures
open Semctl.Tests.CliHarness
open Semctl.Cli.Common

let private ok =
    function
    | Ok v -> v
    | Error e -> failtestf "%s" (SemctlError.message e)

let private cond text =
    match Where.Condition.parse text with
    | Ok c -> c
    | Error message -> failtestf "%s" message

let private found (db: Microsoft.AnalysisServices.Tabular.Database) container conditions =
    let c = Inventory.Selector.resolve (Inventory.objects db) container |> ok

    Where.filter db c (conditions |> List.map cond)
    |> Result.map (List.map (fun e -> ObjectRef.format e.Ref))

let private refs (r: Run) =
    r.Out.Split('\n', System.StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

[<Tests>]
let tests =
    testList "get --where" [
        testList "conditions" [
            testCase "= and !=; the first = splits, so the pattern may hold ="
            <| fun () ->
                let c = cond "isHidden=true"
                Expect.equal (c.Path, c.Op, c.Pattern) ("isHidden", Where.Op.Matches, "true") ""

                Expect.equal (cond "objectKind!=measure").Op Where.Op.DoesNotMatch ""
                Expect.equal (cond "objectKind!=measure").Path "objectKind" ""
                Expect.equal (cond "expression=a=b").Pattern "a=b" ""
                Expect.equal (cond "description=").Pattern "" "empty pattern"
                Expect.isError (Where.Condition.parse "isHidden") "no ="
                Expect.isError (Where.Condition.parse "=x") "no path"
                Expect.isError (Where.Condition.parse "!=x") "no path"

            testCase "wildcards: * spans lines, ? one character, the rest literal, case-insensitive"
            <| fun () ->
                Expect.isTrue (Where.glob "*calculate*" "VAR x = 1\nRETURN CALCULATE([Total])") ""
                Expect.isTrue (Where.glob "t?tal" "TOTAL") ""
                Expect.isFalse (Where.glob "t?tal" "ttal") ""
                Expect.isFalse (Where.glob "a.b" "axb") "a dot is literal"
                Expect.isTrue (Where.glob "[x](1)" "[X](1)") "brackets are literal"
                Expect.isFalse (Where.glob "total" "total measure") "the whole value"
        ]

        testList "filter" [
            testCase "the model holds every object; a table its own; defaults have values"
            <| fun () ->
                let db = sampleDatabase ()

                Expect.equal
                    (found db "model" [ "isHidden=true" ] |> ok)
                    [ "column:Sales/DateKey"; "measure:Sales/Total (hidden)" ]
                    ""

                // isHidden=false is TOM's default, which its JSON leaves out: still a value.
                Expect.equal
                    (found db "Sales" [ "isHidden=false" ] |> ok)
                    [
                        "column:Sales/Amount"
                        "column:Sales/Path\\/With\\\\Slashes"
                        "measure:Sales/Total"
                    ]
                    ""

                Expect.equal
                    (found db "Date" [ "objectKind=level" ] |> ok)
                    [ "level:Date/Calendar/Year"; "level:Date/Calendar/Month" ]
                    ""

                Expect.equal
                    (found db "hierarchy:Date/Calendar" [ "objectKind=*" ] |> ok)
                    [ "level:Date/Calendar/Year"; "level:Date/Calendar/Month" ]
                    ""

            testCase "several conditions all hold; != leaves out objects without the property"
            <| fun () ->
                let db = sampleDatabase ()

                Expect.equal
                    (found db "model" [ "objectKind=measure"; "expression=*sum(*" ] |> ok)
                    [ "measure:Sales/Total" ]
                    ""

                // Relationships, roles, cultures... have no isHidden: neither = nor != picks them.
                let visible = found db "model" [ "isHidden!=true" ] |> ok
                Expect.isFalse (visible |> List.exists (fun r -> r.StartsWith "relationship:")) ""
                Expect.contains visible "measure:Sales/Total" ""
                Expect.isFalse (List.contains "measure:Sales/Total (hidden)" visible) ""

            testCase "an unset property reads as empty; annotations by name; nested paths"
            <| fun () ->
                let db = sampleDatabase ()

                db.Model.Tables.["Sales"].Measures.["Total"].Annotations.Add(
                    new Microsoft.AnalysisServices.Tabular.Annotation(Name = "Owner", Value = "Finance")
                )

                Expect.equal
                    (found db "Sales" [ "objectKind=measure"; "description=" ] |> ok)
                    [ "measure:Sales/Total"; "measure:Sales/Total (hidden)" ]
                    ""

                Expect.equal (found db "Sales" [ "annotations.owner=fin*" ] |> ok) [ "measure:Sales/Total" ] ""
                // A measure without the annotation reads it as "": != picks it.
                Expect.equal
                    (found db "Sales" [ "objectKind=measure"; "annotations.Owner!=finance" ] |> ok)
                    [ "measure:Sales/Total (hidden)" ]
                    ""

                Expect.equal (found db "Sales" [ "source.expression=let*" ] |> ok) [ "partition:Sales/Sales" ] ""
                Expect.equal (found db "model" [ "formatString=#,0" ] |> ok) [ "measure:Sales/Total" ] "a literal #"

            testCase "objectKind is the object's kind; a real `kind` property (named expressions) stays itself"
            <| fun () ->
                let db = sampleDatabase ()
                Expect.equal (found db "model" [ "kind=m" ] |> ok) [ "expression:ServerName" ] "TOM's ExpressionKind"
                Expect.equal (found db "model" [ "objectKind=expression" ] |> ok) [ "expression:ServerName" ] ""
                Expect.equal (found db "model" [ "objectKind=function" ] |> ok) [ "function:Double" ] ""

            testCase "a path no object inside has is an error with close matches"
            <| fun () ->
                let db = sampleDatabase ()

                match found db "Sales" [ "isHiden=true" ] with
                | Error(UnknownProperty("table:Sales", "isHiden", suggestions)) ->
                    Expect.contains suggestions "isHidden" ""
                | other -> failtestf "%A" other

                // A path some objects have is fine: the rest just don't match.
                Expect.equal (found db "model" [ "summarizeBy=none" ] |> ok) [] ""
        ]

        testList "CLI" [
            testCase "lists the matches like `list`; JSON too; nothing matched is exit 0"
            <| fun () ->
                use t = project ()
                let r = semctl [ "get"; t.Path; "model"; "--where"; "isHidden=true" ]
                Expect.equal r.Code ExitCode.ok r.Err
                Expect.equal (refs r) [ "column:Sales/DateKey  (hidden)"; "measure:Sales/Total (hidden)  (hidden)" ] ""

                let j =
                    semctl [
                        "get"
                        t.Path
                        "Sales"
                        "--where"
                        "objectKind=measure"
                        "--where"
                        "isHidden=false"
                        "--json"
                    ]

                Expect.equal j.Code ExitCode.ok j.Err
                let doc = json j
                let items = doc.EnumerateArray() |> List.ofSeq

                Expect.equal
                    (items |> List.map (fun i -> i.GetProperty("ref").GetString()))
                    [ "measure:Sales/Total" ]
                    ""

                Expect.equal (items.Head.GetProperty("kind").GetString()) "measure" ""

                let none = semctl [ "get"; t.Path; "model"; "--where"; "objectKind=nothing" ]
                Expect.equal none.Code ExitCode.ok none.Err
                Expect.equal none.Out "" ""

            testCase "-p prints those properties of each match"
            <| fun () ->
                use t = project ()

                let r =
                    semctl [
                        "get"
                        t.Path
                        "model"
                        "--where"
                        "objectKind=measure"
                        "-p"
                        "expression"
                        "-p"
                        "formatString"
                    ]

                Expect.equal r.Code ExitCode.ok r.Err

                Expect.equal
                    (refs r)
                    [
                        "measure:Sales/Total  expression: SUM(Sales[Amount])"
                        "measure:Sales/Total  formatString: #,0"
                        "measure:Sales/Total (hidden)  expression: [Total]"
                    ]
                    ""

                let j =
                    semctl [
                        "get"
                        t.Path
                        "model"
                        "--where"
                        "objectKind=measure"
                        "-p"
                        "expression"
                        "--json"
                    ]

                let doc = json j
                let first = doc.[0]
                Expect.equal (first.GetProperty("ref").GetString()) "measure:Sales/Total" ""

                Expect.equal
                    (first.GetProperty("properties").GetProperty("expression").GetString())
                    "SUM(Sales[Amount])"
                    ""

            testCase "errors: unknown path (exit 2, suggestions), bad condition, incompatible options"
            <| fun () ->
                use t = project ()
                let r = semctl [ "get"; t.Path; "model"; "--where"; "isHiden=true"; "--json" ]
                Expect.equal r.Code ExitCode.usage r.Out
                let doc = json r
                let error = doc.GetProperty("error")
                Expect.equal (error.GetProperty("code").GetString()) "unknown-property" ""

                Expect.equal
                    [ for s in error.GetProperty("suggestions").EnumerateArray() -> s.GetString() ]
                    [ "isHidden" ]
                    ""

                let bad = semctl [ "get"; t.Path; "model"; "--where"; "isHidden" ]
                Expect.equal bad.Code ExitCode.usage bad.Err
                Expect.stringContains bad.Err "<path>=<pattern>" ""

                let combined =
                    semctl [ "get"; t.Path; "model"; "--where"; "objectKind=measure"; "--properties" ]

                Expect.equal combined.Code ExitCode.usage combined.Err

                let missing = semctl [ "get"; t.Path; "Nope"; "--where"; "objectKind=measure" ]
                Expect.equal missing.Code ExitCode.input missing.Err
        ]
    ]
