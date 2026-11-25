/// `sm schemacheck <model> [table...] --source <src>`: compare the model's data columns with what
/// their sources return (see Semctl.SchemaCheck), and with `--update`, bring the model in line.
module Semctl.Cli.SchemaCheckCommand

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Xml.Linq
open Argu
open Semctl
open Semctl.SchemaCheck
open Semctl.Cli.Output
open Semctl.Cli.Common

/// One-line description, shown in `sm --help`.
let summary =
    "compare the model's columns with their source's schema (exit 1: errors); --update applies the differences."

type Args =
    | [<MainCommand; ExactlyOnce>] Target of model_and_tables: string list
    | Source of source: string
    | Fail_On of level: string
    | Ci of system: string
    | Trx of file: string
    | Sarif of file: string
    | Update
    | Drop_Removed_Columns
    | Save
    | Save_To of path: string
    | Force
    | Stat
    | Name_Only

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Target _ -> "the model, then the tables to check (default: every table with a source)."
            | Source _ ->
                "where the schema comes from: a schema file (*.json), a fixture directory (`sm data` layout; "
                + "names only), or sql:<server>;<database> (SQL Server, Azure SQL, a Fabric SQL endpoint)."
            | Fail_On _ -> "exit 1 if any finding is this severe or more: error, warning or info (default: error)."
            | Ci _ -> $"also write CI annotations to stderr: {Findings.Ci.names}."
            | Trx _ -> "also write the results as a VSTest .trx file (one result per finding)."
            | Sarif _ -> Semctl.Cli.Sarif.usage
            | Update ->
                "add the source's new columns and update changed data types, keeping everything else about the "
                + "columns (a dry run unless --save or --save-to)."
            | Drop_Removed_Columns ->
                "with --update: also remove columns whose source column is gone (otherwise they are only reported)."
            | Save -> "with --update: " + Mutation.Usage.save
            | Save_To _ -> "with --update: " + Mutation.Usage.saveTo
            | Force -> "with --update: " + Mutation.Usage.force
            | Stat -> "with --update: " + Mutation.Usage.stat
            | Name_Only -> "with --update: " + Mutation.Usage.nameOnly

type SqlConnect = SchemaCheckSource.SqlConnect

let private toSeverity =
    function
    | Severity.Error -> Findings.Severity.Error
    | Severity.Warning -> Findings.Severity.Warning
    | Severity.Info -> Findings.Severity.Info

let toFinding (i: Issue) : Findings.Finding =
    Findings.about i.Ref (toSeverity i.Severity) "schemaCheck" i.Code i.Message

let private plural (n: int) (word: string) =
    if n = 1 then $"1 {word}" else $"{n} {word}s"

let private statusToken =
    function
    | TableStatus.Checked -> "checked"
    | TableStatus.Skipped -> "skipped"
    | TableStatus.Failed -> "error"

let private counts (r: Report) =
    let count s =
        r.Tables |> List.filter (fun t -> t.Status = s) |> List.length

    count TableStatus.Checked, count TableStatus.Skipped, r.Tables |> List.filter TableResult.hasDrift |> List.length

let reportJson (duration: TimeSpan) (r: Report) =
    let checkedCount, skipped, drift = counts r

    Findings.envelope "schemacheck" duration (r.Issues |> List.map toFinding) [
        "source", str r.Source
        "tablesChecked", num checkedCount
        "tablesSkipped", num skipped
        "tablesWithDrift", num drift
        "tables",
        arr [
            for t in r.Tables ->
                obj [
                    yield "table", str t.Table
                    yield "ref", str (ObjectRef.format (ObjectRef.table t.Table))
                    yield "status", str (statusToken t.Status)

                    match t.Reason with
                    | Some reason -> yield "reason", str reason
                    | None -> ()

                    match t.Origin with
                    | Some o -> yield "origin", str o
                    | None -> ()

                    if t.Status = TableStatus.Checked then
                        yield "typesChecked", bool t.TypesChecked
                        yield "drift", bool (TableResult.hasDrift t)
                ]
        ]
    ]

let reportText (out: TextWriter) (r: Report) =
    let issues = r.Issues

    let sev (i: Issue) =
        Findings.Severity.token (toSeverity i.Severity)

    let width f =
        issues |> List.map (fun i -> String.length (f i)) |> List.fold max 0

    let sevWidth = width sev
    let refWidth = width (fun i -> ObjectRef.format i.Ref)

    for i in issues do
        out.WriteLine(
            String.concat "  " [
                (sev i).PadRight sevWidth
                (ObjectRef.format i.Ref).PadRight refWidth
                i.Message
                $"[{i.Code}]"
            ]
        )

    let checkedCount, skipped, drift = counts r

    let namesOnly =
        r.Tables
        |> List.filter (fun t -> t.Status = TableStatus.Checked && not t.TypesChecked)
        |> List.length

    let findings =
        match Findings.summaryText (Findings.summarize (issues |> List.map toFinding)) with
        | "" -> "no findings"
        | s -> s

    let typesNote =
        if namesOnly > 0 then
            let tablesText = plural namesOnly "table"
            $"; data types not checked for {tablesText} (the source has none)"
        else
            ""

    let checkedText = plural checkedCount "table"
    let driftText = plural drift "table"

    out.WriteLine(
        $"Checked {checkedText} against {r.Source}: {driftText} with drift, {skipped} skipped{typesNote}; {findings}."
    )

// ---- TRX --------------------------------------------------------------------------------------------

let private guidOf (s: string) =
    Guid(MD5.HashData(Encoding.UTF8.GetBytes s))

/// One result per finding (as the Tabular Editor CLI writes it), plus a passed result for each table
/// checked without one. Errors fail; warnings are `Warning`; info (a skipped table) is NotExecuted.
let trx (modelPath: string) (started: DateTime) (r: Report) : string =
    let ns = XNamespace.Get "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

    let el (name: string) (content: obj list) =
        XElement(ns + name, Array.ofList content)

    let at (name: string) (value: obj) =
        XAttribute(XName.Get name, value) :> obj

    let listId = "8c84fa94-04c1-424b-9868-57a2d4851a1d"
    let unitTestType = "13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b"
    let time (d: DateTime) = d.ToString("o")
    let finished = DateTime.Now

    let rows = [
        for t in r.Tables do
            if t.Issues.IsEmpty then
                $"{t.Table}: schema matches", t.Table, "Passed", None
            else
                for n, i in List.indexed t.Issues do
                    let outcome =
                        match i.Severity with
                        | Severity.Error -> "Failed"
                        | Severity.Warning -> "Warning"
                        | Severity.Info -> "NotExecuted"

                    $"{t.Table}: {i.Code} #{n + 1}", t.Table, outcome, Some i.Message
    ]

    let rows =
        rows
        |> List.map (fun (name, table, outcome, message) ->
            name, table, outcome, message, guidOf ("test:" + name), guidOf ("execution:" + name + time started))

    let count o =
        rows |> List.filter (fun (_, _, x, _, _, _) -> x = o) |> List.length

    let summaryOutcome =
        if count "Failed" > 0 then "Failed"
        elif count "Warning" > 0 then "Warning"
        else "Completed"

    let doc =
        XDocument(
            XDeclaration("1.0", "utf-8", null),
            box (
                el "TestRun" [
                    at "id" (Guid.NewGuid())
                    at "name" $"sm schemacheck {Path.GetFileName modelPath}"
                    el "Times" [
                        at "creation" (time started)
                        at "queuing" (time started)
                        at "start" (time started)
                        at "finish" (time finished)
                    ]
                    el "Results" [
                        for name, _, outcome, message, testId, executionId in rows ->
                            el "UnitTestResult" [
                                at "executionId" executionId
                                at "testId" testId
                                at "testName" name
                                at "computerName" Environment.MachineName
                                at "duration" "00:00:00"
                                at "startTime" (time started)
                                at "endTime" (time finished)
                                at "testType" unitTestType
                                at "outcome" outcome
                                at "testListId" listId
                                match message with
                                | Some m -> el "Output" [ el "ErrorInfo" [ el "Message" [ m ] ] ]
                                | None -> ()
                            ]
                    ]
                    el "TestDefinitions" [
                        for name, table, _, _, testId, executionId in rows ->
                            el "UnitTest" [
                                at "name" name
                                at "storage" modelPath
                                at "id" testId
                                el "Execution" [ at "id" executionId ]
                                el "TestMethod" [
                                    at "codeBase" modelPath
                                    at "adapterTypeName" "executor://semctl/schemacheck"
                                    at "className" "SchemaCheck"
                                    at "name" table
                                ]
                            ]
                    ]
                    el "TestEntries" [
                        for _, _, _, _, testId, executionId in rows ->
                            el "TestEntry" [ at "testId" testId; at "executionId" executionId; at "testListId" listId ]
                    ]
                    el "TestLists" [
                        el "TestList" [ at "name" "Results Not in a List"; at "id" listId ]
                        el "TestList" [
                            at "name" "All Loaded Results"
                            at "id" "19431567-8539-422a-85d7-44ee4e166bda"
                        ]
                    ]
                    el "ResultSummary" [
                        at "outcome" summaryOutcome
                        el "Counters" [
                            at "total" rows.Length
                            at "executed" (rows.Length - count "NotExecuted")
                            at "passed" (count "Passed")
                            at "failed" (count "Failed")
                            at "error" 0
                            at "timeout" 0
                            at "aborted" 0
                            at "inconclusive" 0
                            at "passedButRunAborted" 0
                            at "notRunnable" 0
                            at "notExecuted" (count "NotExecuted")
                            at "disconnected" 0
                            at "warning" (count "Warning")
                            at "completed" 0
                            at "inProgress" 0
                            at "pending" 0
                        ]
                    ]
                ]
            )
        )

    doc.Declaration.ToString() + "\n" + doc.ToString()

// ---- Running -------------------------------------------------------------------------------------

let sarifRules: Sarif.Rule list = [ for code, _, description in Codes.all -> Sarif.Rule.simple code description ]

let runWith (connect: SqlConnect) (ctx: Ctx) (args: ParseResults<Args>) =
    let started = DateTime.Now
    let clock = Diagnostics.Stopwatch.StartNew()

    match args.GetResult Args.Target, args.TryGetResult Args.Source with
    | [], _ -> UsageError(args.Parser.PrintUsage(message = "missing the model path"))
    | _, None -> UsageError "--source is required: a schema file, a fixture directory, or sql:<server>;<database>"
    | modelPath :: tables, Some source ->
        let updating = args.Contains Args.Update

        let editSwitches =
            [
                args.Contains Args.Save, "--save"
                args.Contains Args.Save_To, "--save-to"
                args.Contains Args.Force, "--force"
                args.Contains Args.Stat, "--stat"
                args.Contains Args.Name_Only, "--name-only"
                args.Contains Args.Drop_Removed_Columns, "--drop-removed-columns"
            ]
            |> List.filter fst
            |> List.map snd

        let reportSwitches =
            [
                args.Contains Args.Fail_On, "--fail-on"
                args.Contains Args.Ci, "--ci"
                args.Contains Args.Trx, "--trx"
                args.Contains Args.Sarif, "--sarif"
            ]
            |> List.filter fst
            |> List.map snd

        let failOn =
            ValidateCommand.severityArgWith "--fail-on" (args.TryGetResult Args.Fail_On) Validate.Severity.Error

        match failOn, ValidateCommand.ciArgWith (args.TryGetResult Args.Ci) with
        | Error problem, _
        | _, Error problem -> usage problem
        | _ when not updating && not editSwitches.IsEmpty ->
            let names = String.concat ", " editSwitches
            UsageError $"{names}: only with --update"
        | _ when updating && not reportSwitches.IsEmpty ->
            let names = String.concat ", " reportSwitches
            UsageError $"{names} report findings; they don't combine with --update"
        | Ok _, Ok _ when updating ->
            match
                Mutation.options
                    (args.Contains Args.Save)
                    (args.TryGetResult Args.Save_To)
                    (args.Contains Args.Force)
                    (args.Contains Args.Stat)
                    (args.Contains Args.Name_Only)
            with
            | Error message -> UsageError message
            | Ok mopts ->
                let drop = args.Contains Args.Drop_Removed_Columns

                // A column a PBIR report next to the model uses is kept, as `remove` keeps it; with
                // --save-to the reports keep their model, so they don't stop a copy.
                Mutation.runWith ctx "schemacheck" mopts modelPath (fun modelSource db ->
                    let check =
                        match mopts.Target with
                        | Mutation.SaveTo _ -> fun _ -> []
                        | _ -> Staging.reportCheckFor modelSource

                    SchemaCheckSource.update ctx connect source tables drop check db
                    |> Result.map (fun effect -> effect, []))
        | Ok failOn, Ok ci ->
            match
                Source.load ctx modelPath
                |> Result.bind (fun (ms, db) ->
                    SchemaCheckSource.check connect db source tables |> Result.map (fun r -> ms, r))
            with
            | Error e -> Failed e
            | Ok(modelSource, report) ->
                let findings = report.Issues |> List.map toFinding

                let written =
                    match args.TryGetResult Args.Trx with
                    | None -> Ok()
                    | Some path ->
                        try
                            File.WriteAllText(path, trx modelPath started report)
                            Ok()
                        with e ->
                            Error(SaveFailed(path, e.Message))
                    |> Result.bind (fun () ->
                        match args.TryGetResult Args.Sarif with
                        | None -> Ok()
                        | Some path -> ValidateCommand.writeSarif path modelPath modelSource sarifRules findings)

                match written with
                | Error e -> Failed e
                | Ok() ->
                    if ctx.Json then
                        writeJson ctx.Out (reportJson clock.Elapsed report)
                    else
                        reportText ctx.Out report

                    Findings.annotate ci ctx.Err findings

                    let failing (i: Issue) =
                        let s =
                            match i.Severity with
                            | Severity.Error -> Validate.Severity.Error
                            | Severity.Warning -> Validate.Severity.Warning
                            | Severity.Info -> Validate.Severity.Info

                        Validate.Severity.atLeast failOn s

                    if report.Issues |> List.exists failing then
                        DoneWith ExitCode.findings
                    else
                        Done

/// With connections to a SQL endpoint made for real (only for a `sql:` source).
let run (ctx: Ctx) (args: ParseResults<Args>) =
    runWith (SchemaCheckSource.live ctx) ctx args
