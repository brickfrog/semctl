/// `tess add calendar:T/N`: calendars (calendar-based time intelligence metadata) through the add
/// pipeline, checked end to end with the local engine.
module Tessera.Tests.AddCalendarTests

open System
open System.IO
open System.Text.Json
open Expecto
open Microsoft.AnalysisServices.Tabular
open Tessera
open Tessera.Tests.Fixtures
open Tessera.Tests.CliHarness
open Tessera.Tests.EngineHelpers
open Tessera.Cli.Common

let private days =
    [ 0..58 ] |> List.map (fun i -> DateTime(2025, 1, 1).AddDays(float i))

let private model = {
    Tables = [
        table "Date" [
            "Date", DataType.DateTime
            "Year", DataType.Int64
            "YearMonth", DataType.Int64
            "MonthOfYear", DataType.Int64
            "Weekday", DataType.String
            "MonthName", DataType.String
        ] [
            for d in days ->
                [
                    box d
                    box d.Year
                    box (d.Year * 100 + d.Month)
                    box d.Month
                    box (d.DayOfWeek.ToString().Substring(0, 3))
                    box (d.ToString("MMM", Globalization.CultureInfo.InvariantCulture))
                ]
        ]
        table "Sales" [ "Date", DataType.DateTime; "Amount", DataType.Int64 ] [ for d in days -> [ box d; box 1 ] ]
    ]
    Rels = [ rel ("Sales", "Date") ("Date", "Date") ]
    Measures = [ "Sales", "Total", "SUM ( Sales[Amount] )" ]
    Functions = []
}

/// The model as a .bim file (at `level`), with its data as a fixture directory.
let private setupAt (level: int) =
    let t = new TempDir()
    let db, store = build model
    db.CompatibilityLevel <- level
    // MonthName sorts by MonthOfYear, as a month name column does.
    let date = db.Model.Tables.["Date"]
    date.Columns.["MonthName"].SortByColumn <- date.Columns.["MonthOfYear"]
    let path = t.Combine "model.bim"
    File.WriteAllText(path, JsonSerializer.SerializeDatabase db)
    let dir = t.Combine "data"
    Directory.CreateDirectory dir |> ignore

    for table in store.Tables do
        use writer =
            new StreamWriter(Path.Combine(dir, FixtureName.encode table.Name + ".csv"))

        Fixture.writeCsv writer table

    t, path, dir

let private setup () = setupAt 1702

let private load (path: string) =
    match ModelIO.loadPath path with
    | Ok(_, db) -> db
    | Error e -> failtestf "%s" (TessError.message e)

let private gregorian = [
    "--category"
    "Year=Year"
    "--category"
    "Month=YearMonth"
    "--category"
    "month of year=MonthOfYear"
    "--category"
    "Date=Date"
]

let private errorCode (r: Run) =
    use doc = JsonDocument.Parse r.Out
    doc.RootElement.GetProperty("error").GetProperty("code").GetString()

[<Tests>]
let tests =
    testList "add calendar" [
        testCase "a dry run by default; --save writes it; the engine then evaluates over it"
        <| fun () ->
            let t, path, dir = setup ()
            use _ = t
            let before = File.ReadAllText path

            let r = tess ([ "add"; path; "calendar:date/Gregorian" ] @ gregorian)
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains r.Out "calendar:Date/Gregorian" "the change is shown"
            Expect.equal (File.ReadAllText path) before "a dry run writes nothing"

            let r =
                tess (
                    [
                        "add"
                        path
                        "calendar:Date/Gregorian"
                        "--time-related"
                        "Weekday"
                        "--save"
                    ]
                    @ gregorian
                )

            Expect.equal r.Code ExitCode.ok r.Err

            let db = load path
            let cal = db.Model.Tables.["Date"].Calendars.["Gregorian"]
            Expect.isNotEmpty cal.LineageTag "a lineage tag"

            let groups = [
                for g in cal.CalendarColumnGroups do
                    match g with
                    | :? TimeUnitColumnAssociation as a -> $"{a.TimeUnit}={a.PrimaryColumn.Name}"
                    | :? TimeRelatedColumnGroup as rg ->
                        "related=" + String.Join(",", [ for c in rg.Columns -> c.Name ])
                    | _ -> "?"
            ]

            Expect.equal
                groups
                [
                    "Year=Year"
                    "Month=YearMonth"
                    "MonthOfYear=MonthOfYear"
                    "Date=Date"
                    "related=Weekday"
                ]
                ""

            // DATESMTD over the calendar with 10 February filtered: 1 to 10 February (Learn's
            // calendar-based DATESMTD; the engine's rule in Engine/Calendars.fs).
            let q =
                "EVALUATE CALCULATETABLE ( ROW ( \"n\", COUNTROWS ( DATESMTD ( Gregorian ) ) ), 'Date'[Date] = dt\"2025-02-10\" )"

            let r = tess [ "query"; path; "--data"; dir; q; "--csv" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.equal (r.Out.Replace("\r", "")) "[n]\n10\n" ""

            // The name is taken now, by a calendar or a table, in any table.
            let again = tess ([ "add"; path; "calendar:Sales/gregorian" ] @ gregorian)
            Expect.equal again.Code ExitCode.input again.Err
            let clash = tess ([ "add"; path; "calendar:Date/Sales" ] @ gregorian)
            Expect.equal clash.Code ExitCode.input clash.Err

            let quiet =
                tess ([ "add"; path; "calendar:Date/Gregorian"; "--if-not-exists" ] @ gregorian)

            Expect.equal quiet.Code ExitCode.ok quiet.Err

        testCase "refused: below compatibility level 1701, a calculation group, a category twice, a missing column"
        <| fun () ->
            let t, path, _ = setupAt 1600
            use _ = t
            let r = tess ([ "add"; path; "calendar:Date/C"; "--json" ] @ gregorian)
            Expect.equal r.Code ExitCode.input r.Out
            Expect.stringContains r.Out "1701" ""

            let t, path, _ = setup ()
            use _ = t

            let r =
                tess [
                    "add"
                    path
                    "calendar:Date/C"
                    "--category"
                    "Year=Year"
                    "--category"
                    "year=YearMonth"
                ]

            Expect.equal r.Code ExitCode.input r.Err
            Expect.stringContains r.Err "twice" ""

            let r = tess [ "add"; path; "calendar:Date/C"; "--category"; "Date=Dat"; "--json" ]
            Expect.equal r.Code ExitCode.input r.Out
            Expect.equal (errorCode r) "object-not-found" ""
            Expect.stringContains r.Out "column:Date/Date" "suggested"

            let r = tess [ "add"; path; "calendar:Nope/C"; "--category"; "Date=Date" ]
            Expect.equal r.Code ExitCode.input r.Err

        testCase "refused: a column with another category in another calendar, or both categorised and time-related"
        <| fun () ->
            let t, path, _ = setup ()
            use _ = t
            Expect.equal (tess ([ "add"; path; "calendar:Date/Gregorian"; "--save" ] @ gregorian)).Code 0 ""

            let r =
                tess [ "add"; path; "calendar:Date/Fiscal"; "--category"; "Quarter=YearMonth" ]

            Expect.equal r.Code ExitCode.input r.Err
            Expect.stringContains r.Err "same category in every calendar" ""

            let r =
                tess [
                    "add"
                    path
                    "calendar:Date/Other"
                    "--category"
                    "Date=Date"
                    "--time-related"
                    "Year"
                ]

            Expect.equal r.Code ExitCode.input r.Err
            Expect.stringContains r.Err "Year" ""

            let r =
                tess [
                    "add"
                    path
                    "calendar:Date/Self"
                    "--category"
                    "Date=Date"
                    "--time-related"
                    "Date"
                ]

            Expect.equal r.Code ExitCode.input r.Err

            let r =
                tess [
                    "add"
                    path
                    "calendar:Date/Twice"
                    "--category"
                    "Date=Date"
                    "--category"
                    "Month=YearMonth,Date"
                ]

            Expect.equal r.Code ExitCode.input r.Err
            Expect.stringContains r.Err "more than once" ""

            // The same category for a column in a second calendar is fine.
            let r =
                tess [
                    "add"
                    path
                    "calendar:Date/Days"
                    "--category"
                    "Date=Date"
                    "--description"
                    "Just days"
                ]

            Expect.equal r.Code ExitCode.ok r.Err

        testCase "a sort-by column goes with its column: it takes its category (SQLBI; TE3 3.25.5)"
        <| fun () ->
            let t, path, dir = setup ()
            use _ = t

            // MonthOfYear is MonthName's sort-by column, so it is in the category already: it can't be
            // time-related, nor in another category, in this calendar or another.
            for args in
                [
                    [
                        "--category"
                        "Month of Year=MonthName"
                        "--category"
                        "Date=Date"
                        "--time-related"
                        "MonthOfYear"
                    ]
                    [ "--category"; "Month of Year=MonthName"; "--category"; "Month=MonthOfYear" ]
                ] do
                let r = tess ([ "add"; path; "calendar:Date/C" ] @ args)
                Expect.equal r.Code ExitCode.input (String.concat " " args + r.Err)
                Expect.stringContains r.Err "MonthOfYear" ""

            // Naming it in the same category is fine (it is there anyway).
            let ok =
                tess [
                    "add"
                    path
                    "calendar:Date/C"
                    "--category"
                    "Year=Year"
                    "--category"
                    "Month=YearMonth"
                    "--category"
                    "Month of Year=MonthName,MonthOfYear"
                    "--category"
                    "Date=Date"
                    "--save"
                ]

            Expect.equal ok.Code ExitCode.ok ok.Err

            let r =
                tess [
                    "add"
                    path
                    "calendar:Date/D"
                    "--category"
                    "Date=Date"
                    "--time-related"
                    "MonthOfYear"
                ]

            Expect.equal r.Code ExitCode.input r.Err

            // The engine takes the calendar as it was added: 1 to 10 February, as before.
            let q =
                "EVALUATE CALCULATETABLE ( ROW ( \"n\", COUNTROWS ( DATESMTD ( C ) ) ), 'Date'[Date] = dt\"2025-02-10\" )"

            let r = tess [ "query"; path; "--data"; dir; q; "--csv" ]
            Expect.equal r.Code ExitCode.ok r.Err
            Expect.equal (r.Out.Replace("\r", "")) "[n]\n10\n" ""

        testCase "usage: unknown categories (with a hint), no category, calendar options on other kinds"
        <| fun () ->
            let t, path, _ = setup ()
            use _ = t
            let r = tess [ "add"; path; "calendar:Date/C"; "--category"; "Mnth=YearMonth" ]
            Expect.equal r.Code ExitCode.usage r.Err
            Expect.stringContains r.Err "Month" "did you mean"

            for args in
                [
                    [ "add"; path; "calendar:Date/C" ]
                    [ "add"; path; "calendar:Date/C"; "--category"; "Date" ]
                    [ "add"; path; "calendar:Date/C"; "--category"; "Date=" ]
                    [ "add"; path; "calendar:Date/C"; "--category"; "SemesterOfYear=MonthOfYear" ]
                    [ "add"; path; "measure:Sales/M"; "-e"; "1"; "--category"; "Date=Date" ]
                    [ "add"; path; "calendar:Date/C"; "--category"; "Date=Date"; "--hidden" ]
                ] do
                let r = tess args
                Expect.isTrue (r.Code = ExitCode.usage || r.Code = ExitCode.input) (String.concat " " args)
                Expect.notEqual r.Code ExitCode.ok (String.concat " " args)

        testCase "a calendar without a Date category is added, with a warning"
        <| fun () ->
            let t, path, _ = setup ()
            use _ = t

            let r =
                tess [
                    "add"
                    path
                    "calendar:Date/Months"
                    "--category"
                    "Month=YearMonth"
                    "--save"
                ]

            Expect.equal r.Code ExitCode.ok r.Err
            Expect.stringContains (r.Out + r.Err) "no Date category" ""
    ]
