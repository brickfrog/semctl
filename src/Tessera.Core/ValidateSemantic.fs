/// Checks from Tabular Editor 3's semantic analyzer that read DAX as a tree (DaxParser), plus the
/// model's cultures and calendars. `tess validate` runs them with Validate's rules
/// (`validateGraph`). The save gate (Mutation) and `deploy` run only `Validate.validate`, so these
/// never block an edit or a deploy.
///
/// Sources, by rule (TE3's release notes):
///   - invalid-culture: 3.27.1 "an invalid culture name was accepted for a model's Culture and a
///     translation's Language property. These values are now rejected with an error such as
///     `'en US' is an invalid culture specifier`."
///   - calculate-variable: 3.18.0 "a warning if a variable is used directly as the 1st argument of
///     CALCULATE or CALCULATETABLE. Since variables are unaffected by context transitions or
///     modified filter contexts, using a variable this way is most likely unintentional."
///   - ignore-outside-summarizecolumns: 3.26.2 "an error when IGNORE is used anywhere other than
///     directly as an expression argument of SUMMARIZECOLUMNS, matching engine behavior."
///   - week-without-weekly-calendar: 3.24.0 "a warning ... of any week-based DAX time intelligence
///     functions, if the current model does not contain a calendar with weekly granularity
///     (without which these functions will not work)."
module Tessera.ValidateSemantic

open System
open System.Collections.Generic
open System.Globalization
open Microsoft.AnalysisServices.Tabular
open Tessera.Dax
open Tessera.DaxAst
open Tessera.DaxEngine
open Tessera.Inventory
open Tessera.Validate

/// These checks' rule ids, with a one-line description, in the order they run.
let rules = [
    "invalid-culture", "a model culture or translation language that isn't a valid culture name"
    "calculate-variable",
    "a variable as the first argument of CALCULATE or CALCULATETABLE, where their filters can't change it"
    "ignore-outside-summarizecolumns", "IGNORE anywhere but directly as an expression argument of SUMMARIZECOLUMNS"
    "week-without-weekly-calendar",
    "a week-based time intelligence function, in a model with calendars but none with weeks (or over a calendar without weeks)"
]

let private issueAt rule severity (src: Deps.DaxSource) (span: Span) message = {
    Rule = rule
    Severity = severity
    Ref = src.Owner.Ref
    Property = Some src.Property
    Line = Some(lineOf src.Dax span.Start)
    Column = Some(columnOf src.Dax span.Start)
    Message = message
}

// --- invalid-culture -----------------------------------------------------------------------------

/// Whether this system's culture data can tell known cultures from unknown ones (it can't in .NET's
/// invariant globalization mode).
let private knowsCultures =
    lazy
        (try
            CultureInfo.GetCultureInfo("en-US", true) |> ignore
            true
         with _ ->
             false)

/// None when the name is fine; else the severity and why. Only a name .NET can't parse at all is an
/// error ('en US'): that is the same on every system. A well-formed name this system's culture
/// data doesn't list may still be one a server knows (the lists differ by OS and ICU version), so
/// it is a warning.
let private cultureProblem (name: string) =
    let malformed =
        try
            CultureInfo.GetCultureInfo name |> ignore
            None
        with :? CultureNotFoundException ->
            Some(Severity.Error, $"'{name}' is not a valid culture name")

    match malformed with
    | Some p -> Some p
    | None when knowsCultures.Value ->
        try
            CultureInfo.GetCultureInfo(name, true) |> ignore
            None
        with :? CultureNotFoundException ->
            Some(Severity.Warning, $"'{name}' is not a culture this system knows (e.g. en-US, fr-FR)")
    | None -> None

let private invalidCultures (g: Deps.Graph) (db: Database) = [
    let culture = db.Model.Culture

    if not (String.IsNullOrEmpty culture) then
        match cultureProblem culture with
        | Some(severity, message) -> {
            Rule = "invalid-culture"
            Severity = severity
            Ref = ObjectRef.model
            Property = Some "culture"
            Line = None
            Column = None
            Message = $"the model's culture: {message}"
          }
        | None -> ()

    for e in g.Entries do
        match e.Object with
        | :? Culture as c ->
            match cultureProblem c.Name with
            | Some(severity, message) -> {
                Rule = "invalid-culture"
                Severity = severity
                Ref = e.Ref
                Property = Some "name"
                Line = None
                Column = None
                Message = $"translation language: {message}"
              }
            | None -> ()
        | _ -> ()
]

// --- calendars -----------------------------------------------------------------------------------

/// TOM's time units as the engine's calendar categories (Engine.fs reads them the same way).
let private categoryOf (u: TimeUnit) : Category option =
    match u with
    | TimeUnit.Year -> Some Category.Year
    | TimeUnit.Quarter -> Some Category.Quarter
    | TimeUnit.QuarterOfYear -> Some Category.QuarterOfYear
    | TimeUnit.Month -> Some Category.Month
    | TimeUnit.MonthOfYear -> Some Category.MonthOfYear
    | TimeUnit.MonthOfQuarter -> Some Category.MonthOfQuarter
    | TimeUnit.Week -> Some Category.Week
    | TimeUnit.WeekOfYear -> Some Category.WeekOfYear
    | TimeUnit.WeekOfQuarter -> Some Category.WeekOfQuarter
    | TimeUnit.WeekOfMonth -> Some Category.WeekOfMonth
    | TimeUnit.Date -> Some Category.Date
    | TimeUnit.DayOfYear -> Some Category.DayOfYear
    | TimeUnit.DayOfQuarter -> Some Category.DayOfQuarter
    | TimeUnit.DayOfMonth -> Some Category.DayOfMonth
    | TimeUnit.DayOfWeek -> Some Category.DayOfWeek
    | _ -> None

/// Whether a calendar can tell weeks apart: it tags one of the sets of categories that identify a
/// week (Learn's "Period Uniqueness", as the engine uses them).
let private weekly (c: Calendar) =
    let has =
        set [
            for g in c.CalendarColumnGroups do
                match g with
                | :? TimeUnitColumnAssociation as a ->
                    match categoryOf a.TimeUnit with
                    | Some cat -> cat
                    | None -> ()
                | _ -> ()
        ]

    Category.routes CalendarPeriod.Week |> List.exists (List.forall has.Contains)

/// The week-based time intelligence functions (the calendar-based ones: README, "Calendar-based
/// time intelligence"), with the index of their dates argument.
let private weekFunctions =
    dict [
        "DATESWTD", 0
        "TOTALWTD", 1
        "STARTOFWEEK", 0
        "ENDOFWEEK", 0
        "PREVIOUSWEEK", 0
        "NEXTWEEK", 0
        "OPENINGBALANCEWEEK", 1
        "CLOSINGBALANCEWEEK", 1
    ]

type private Calendars = {
    /// By name, whether each has weeks.
    Weekly: IDictionary<string, bool>
}

// --- the DAX checks ------------------------------------------------------------------------------

let rec private unparen (e: Expr) =
    match e.Kind with
    | Paren inner -> unparen inner
    | _ -> e

let private upper (s: string) = s.ToUpperInvariant()

/// The DAX checks over one parsed expression. A function's parameters are not variables (an EXPR
/// parameter is evaluated inside CALCULATE), so only VARs count.
let private checkExpr (calendars: Calendars) (src: Deps.DaxSource) (exprs: Expr list) : Issue list =
    let hits = ResizeArray<Issue>()
    let anyWeekly = calendars.Weekly.Values |> Seq.exists id

    // `vars`: the variables in scope (upper case). `ignoreOk`: e is directly an expression
    // argument of SUMMARIZECOLUMNS (parentheses around it don't change that).
    let rec go (vars: Set<string>) (ignoreOk: bool) (e: Expr) =
        match e.Kind with
        | VarBlock(decls, body) ->
            let mutable scope = vars

            for d in decls do
                go scope false d.Value
                scope <- scope.Add(upper d.Name.Name)

            go scope false body
        | Paren inner -> go vars ignoreOk inner
        | Call(name, args) ->
            let fn = upper name.Name

            if fn = "IGNORE" && not ignoreOk then
                hits.Add(
                    issueAt
                        "ignore-outside-summarizecolumns"
                        Severity.Error
                        src
                        name.Span
                        "IGNORE can only be used directly as an expression argument of SUMMARIZECOLUMNS"
                )

            if fn = "CALCULATE" || fn = "CALCULATETABLE" then
                match args with
                | first :: rest ->
                    match (unparen first).Kind with
                    | Identifier v when vars.Contains(upper v) ->
                        let effect =
                            if rest.IsEmpty then
                                "it does nothing"
                            else
                                "its filters don't change the result"

                        hits.Add(
                            issueAt
                                "calculate-variable"
                                Severity.Warning
                                src
                                name.Span
                                $"the first argument of {name.Name} is the variable {v}, which was evaluated where it was defined, so {effect}"
                        )
                    | _ -> ()
                | [] -> ()

            match weekFunctions.TryGetValue fn with
            | true, index when calendars.Weekly.Count > 0 ->
                let named =
                    match args |> List.tryItem index |> Option.map (fun a -> (unparen a).Kind) with
                    | Some(Identifier n) when not (vars.Contains(upper n)) -> Some n
                    | Some(QuotedTable n) -> Some n
                    | _ -> None

                let calendar =
                    named
                    |> Option.bind (fun n ->
                        match calendars.Weekly.TryGetValue n with
                        | true, w -> Some w
                        | _ -> None)

                match calendar with
                | Some false ->
                    hits.Add(
                        issueAt
                            "week-without-weekly-calendar"
                            Severity.Warning
                            src
                            name.Span
                            $"{name.Name} needs a calendar with weeks, and the calendar '{named.Value}' has none (tag a Week, or Week of Year and Year, ...)"
                    )
                | Some true -> ()
                | None when not anyWeekly ->
                    hits.Add(
                        issueAt
                            "week-without-weekly-calendar"
                            Severity.Warning
                            src
                            name.Span
                            $"{name.Name} needs a calendar with weeks, and no calendar in the model has them"
                    )
                | None -> ()
            | _ -> ()

            args
            |> List.iteri (fun i a ->
                let expressionArgument =
                    fn = "SUMMARIZECOLUMNS"
                    && i > 0
                    && (match (unparen args.[i - 1]).Kind with
                        | StringLiteral _ -> true
                        | _ -> false)

                go vars expressionArgument a)
        | _ ->
            for c in children e do
                go vars false c

    for e in exprs do
        go Set.empty false e

    List.ofSeq hits

let private daxChecks (g: Deps.Graph) (db: Database) =
    let byName = Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)

    for t in db.Model.Tables do
        for c in t.Calendars do
            byName.[c.Name] <- weekly c

    let calendars = { Weekly = byName }

    [
        for src in Deps.daxSources g.Entries do
            let parsed =
                if src.Owner.Ref.Kind = ObjectKind.Function && src.Property = "expression" then
                    DaxParser.parseFunction src.Dax
                    |> Result.map (fun f -> (f.Parameters |> List.choose (fun p -> p.Default)) @ [ f.Body ])
                else
                    DaxParser.parseExpression src.Dax |> Result.map List.singleton

            // DAX that doesn't parse is reported by other rules (or by the server); nothing to add.
            match parsed with
            | Ok exprs -> yield! checkExpr calendars src exprs
            | Error _ -> ()
    ]

/// These checks' issues.
let check (g: Deps.Graph) (db: Database) : Issue list = invalidCultures g db @ daxChecks g db

/// Validate's issues and these checks', in model-tree order (issues on one object keep rule order).
let validateGraph (g: Deps.Graph) (db: Database) : Issue list =
    let order = g.Entries |> List.mapi (fun i e -> e.Ref, i) |> dict

    let position (i: Issue) =
        match order.TryGetValue i.Ref with
        | true, n -> n
        | _ -> Int32.MaxValue

    Validate.validateGraph g db @ check g db |> List.sortBy position

/// Every rule `tess validate` runs.
let allRules = Validate.rules @ rules
