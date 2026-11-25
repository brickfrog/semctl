/// Shared VSTest (.trx) XML writer for `sm test`, `sm bpa`, and `sm schemacheck`.
module Semctl.Cli.Trx

open System
open System.Security.Cryptography
open System.Text
open System.Xml.Linq

/// A deterministic GUID derived from `text` so test definitions keep stable IDs across runs.
let stableGuid (text: string) =
    Guid(MD5.HashData(Encoding.UTF8.GetBytes text))

/// Characters XML 1.0 cannot hold replaced with U+FFFD.
let xmlSafe (s: string) =
    if isNull s then
        ""
    else
        String(
            s.ToCharArray()
            |> Array.map (fun c ->
                if c = '\t' || c = '\n' || c = '\r' || (c >= ' ' && c <> '\uFFFE' && c <> '\uFFFF') then
                    c
                else
                    '\uFFFD')
        )

[<RequireQualifiedAccess>]
type EntryOutput =
    | None
    | StdOut of string
    | ErrorInfo of message: string * details: string option

type Entry = {
    TestName: string
    Storage: string
    CodeBase: string
    AdapterTypeName: string
    ClassName: string
    MethodName: string
    Outcome: string
    Duration: TimeSpan
    Output: EntryOutput
    TestId: Guid
    ExecutionId: Guid
}

type RunInfo = { Timestamp: DateTime; Text: string }

type Report = {
    RunId: Guid
    RunName: string
    Started: DateTime
    Finished: DateTime
    ComputerName: string
    Entries: Entry list
    RunInfos: RunInfo list
}

let private listId = "8c84fa94-04c1-424b-9868-57a2d4851a1d"
let private allLoadedListId = "19431567-8539-422a-85d7-44ee4e166bda"
let private unitTestType = "13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b"

let private ns =
    XNamespace.Get "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

let document (r: Report) : XDocument =
    let x (name: string) = ns + name
    let el (name: string) (content: obj list) = XElement(x name, Array.ofList content)

    let attr (name: string) (value: obj) =
        XAttribute(XName.Get name, value) :> obj

    let iso (d: DateTime) = d.ToString("o")

    let count (outcome: string) =
        r.Entries |> List.filter (fun e -> e.Outcome = outcome) |> List.length

    let failed = count "Failed"
    let error = count "Error"
    let warning = count "Warning"
    let inconclusive = count "Inconclusive"
    let notExecuted = count "NotExecuted"

    let summaryOutcome =
        if failed + error > 0 || not r.RunInfos.IsEmpty then
            "Failed"
        elif warning > 0 then
            "Warning"
        else
            "Completed"

    XDocument(
        XDeclaration("1.0", "utf-8", null),
        box (
            el "TestRun" [
                attr "id" r.RunId
                attr "name" (xmlSafe r.RunName)
                el "Times" [
                    attr "creation" (iso r.Started)
                    attr "queuing" (iso r.Started)
                    attr "start" (iso r.Started)
                    attr "finish" (iso r.Finished)
                ]
                el "ResultSummary" [
                    attr "outcome" summaryOutcome
                    el "Counters" [
                        attr "total" r.Entries.Length
                        attr "executed" (r.Entries.Length - notExecuted)
                        attr "passed" (count "Passed")
                        attr "failed" failed
                        attr "error" error
                        attr "timeout" 0
                        attr "aborted" 0
                        attr "inconclusive" inconclusive
                        attr "passedButRunAborted" 0
                        attr "notRunnable" 0
                        attr "notExecuted" notExecuted
                        attr "disconnected" 0
                        attr "warning" warning
                        attr "completed" 0
                        attr "inProgress" 0
                        attr "pending" 0
                    ]
                    if not r.RunInfos.IsEmpty then
                        el "RunInfos" [
                            for info in r.RunInfos ->
                                el "RunInfo" [
                                    attr "computerName" r.ComputerName
                                    attr "outcome" "Error"
                                    attr "timestamp" (iso info.Timestamp)
                                    el "Text" [ xmlSafe info.Text ]
                                ]
                        ]
                ]
                el "TestDefinitions" [
                    for e in r.Entries ->
                        el "UnitTest" [
                            attr "name" (xmlSafe e.TestName)
                            attr "storage" e.Storage
                            attr "id" e.TestId
                            el "Execution" [ attr "id" e.ExecutionId ]
                            el "TestMethod" [
                                attr "codeBase" e.CodeBase
                                attr "adapterTypeName" e.AdapterTypeName
                                attr "className" (xmlSafe e.ClassName)
                                attr "name" (xmlSafe e.MethodName)
                            ]
                        ]
                ]
                el "TestLists" [
                    el "TestList" [ attr "name" "Results Not in a List"; attr "id" listId ]
                    el "TestList" [ attr "name" "All Loaded Results"; attr "id" allLoadedListId ]
                ]
                el "TestEntries" [
                    for e in r.Entries ->
                        el "TestEntry" [
                            attr "testId" e.TestId
                            attr "executionId" e.ExecutionId
                            attr "testListId" listId
                        ]
                ]
                el "Results" [
                    for e in r.Entries ->
                        el "UnitTestResult" [
                            attr "executionId" e.ExecutionId
                            attr "testId" e.TestId
                            attr "testName" (xmlSafe e.TestName)
                            attr "computerName" r.ComputerName
                            attr "duration" (e.Duration.ToString "c")
                            attr "startTime" (iso r.Started)
                            attr "endTime" (iso r.Finished)
                            attr "testType" unitTestType
                            attr "outcome" e.Outcome
                            attr "testListId" listId
                            match e.Output with
                            | EntryOutput.None -> ()
                            | EntryOutput.StdOut text -> el "Output" [ el "StdOut" [ xmlSafe text ] ]
                            | EntryOutput.ErrorInfo(message, details) ->
                                el "Output" [
                                    el "ErrorInfo" [
                                        el "Message" [ xmlSafe message ]
                                        match details with
                                        | Some d -> el "StackTrace" [ xmlSafe d ]
                                        | None -> ()
                                    ]
                                ]
                        ]
                ]
            ]
        )
    )

let render (r: Report) : string =
    let doc = document r
    doc.Declaration.ToString() + "\n" + doc.ToString()
