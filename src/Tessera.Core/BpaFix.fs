/// Applying Best Practice Analyzer rules' `FixExpression`s, as Tabular Editor 2 does: its BPA window
/// turns each finding into a line of C# script, `<the object's path>.` + FixExpression + `;`, and runs
/// it (TabularEditor/BestPracticeAnalyzer/BPAForm.cs, GetFixScript). So a fix expression is a C#
/// statement about the object: `IsHidden = true`, `FormatString = "0.0"`, `Delete()`.
///
/// tess doesn't run C#. It reads the statements the published rule files use and applies them
/// through `SetProperty` and `RemoveObject`, with the side effects TE's TOMWrapper gives them
/// (TOMWrapper/Column.cs, Measure.cs, TabularNamedObject.cs, Indexers/TranslationIndexer.cs):
///   - `<Property> = <literal>` for the properties in `assignable`; a literal is `true`, `false`,
///     `null`, a C# string ("..." with C#'s escapes, or @"..."), or `EnumType.Member`.
///     Setting FormatString removes the object's `Format` annotation (Power BI's format hint);
///     setting IsKey to true clears IsKey on the table's other columns.
///   - `Delete()`: the object goes, with the relationships, levels, sort-by links, perspective
///     memberships, security and translations that point at it (RemoveObject). Unlike TE, it is
///     refused while other DAX refers to the object, rather than leaving that DAX broken.
///   - `TranslatedDisplayFolders.Reset()`: every culture's display folder translation becomes the
///     object's own display folder.
/// Anything else is refused with a reason, and so is every expression TE itself skips (those with
/// `{`, `}` or `;`).
module Tessera.BpaFix

open System
open System.Collections.Generic
open System.Globalization
open System.Text
open Microsoft.AnalysisServices.Tabular
open Tessera.Inventory

/// A literal on the right of `=`.
[<RequireQualifiedAccess>]
type Literal =
    | Bool of bool
    | Text of string
    | Null
    /// `DataType.Decimal`: the enum's type name and member, as written.
    | Enum of typeName: string * memberName: string

[<RequireQualifiedAccess>]
type Action =
    /// `Property = literal`, with the property as TE names it.
    | Assign of property: string * value: Literal
    | Delete
    | ResetDisplayFolderTranslations

module Literal =
    let text =
        function
        | Literal.Bool b -> if b then "true" else "false"
        | Literal.Text s -> "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        | Literal.Null -> "null"
        | Literal.Enum(t, m) -> $"{t}.{m}"

// ---- Parsing ------------------------------------------------------------------------------------

/// A C# regular string literal starting at `i` (the opening quote): its value and the index after
/// the closing quote.
let private regularString (s: string) (i: int) : Result<string * int, string> =
    let sb = StringBuilder()
    let mutable k = i + 1
    let mutable result = None

    let hex (start: int) (count: int) =
        if start + count <= s.Length then
            match Int32.TryParse(s.Substring(start, count), NumberStyles.HexNumber, CultureInfo.InvariantCulture) with
            | true, v -> Some v
            | _ -> None
        else
            None

    while result.IsNone do
        if k >= s.Length then
            result <- Some(Error "unterminated string")
        else
            match s.[k] with
            | '"' -> result <- Some(Ok(sb.ToString(), k + 1))
            | '\r'
            | '\n' -> result <- Some(Error "a string can't span lines")
            | '\\' when k + 1 < s.Length ->
                let c = s.[k + 1]
                k <- k + 2

                let simple =
                    match c with
                    | '\'' -> Some "'"
                    | '"' -> Some "\""
                    | '\\' -> Some "\\"
                    | '0' -> Some "\u0000"
                    | 'a' -> Some "\a"
                    | 'b' -> Some "\b"
                    | 'f' -> Some "\f"
                    | 'n' -> Some "\n"
                    | 'r' -> Some "\r"
                    | 't' -> Some "\t"
                    | 'v' -> Some "\v"
                    | _ -> None

                match simple, c with
                | Some x, _ -> sb.Append x |> ignore
                | None, 'u' ->
                    match hex k 4 with
                    | Some v ->
                        sb.Append(char v) |> ignore
                        k <- k + 4
                    | None -> result <- Some(Error "\\u needs four hex digits")
                | None, 'U' ->
                    match hex k 8 with
                    | Some v when v <= 0x10FFFF ->
                        sb.Append(Char.ConvertFromUtf32 v) |> ignore
                        k <- k + 8
                    | _ -> result <- Some(Error "\\U needs eight hex digits")
                | None, 'x' ->
                    // One to four hex digits.
                    let mutable n = 0

                    while n < 4 && k + n < s.Length && Uri.IsHexDigit s.[k + n] do
                        n <- n + 1

                    match hex k n with
                    | Some v when n > 0 ->
                        sb.Append(char v) |> ignore
                        k <- k + n
                    | _ -> result <- Some(Error "\\x needs a hex digit")
                | None, c -> result <- Some(Error $"unknown escape \\{c}")
            | c ->
                sb.Append c |> ignore
                k <- k + 1

    result.Value

/// A C# verbatim string literal at `i` (the @): "" is a quote, nothing else is escaped.
let private verbatimString (s: string) (i: int) : Result<string * int, string> =
    let sb = StringBuilder()
    let mutable k = i + 2
    let mutable result = None

    while result.IsNone do
        if k >= s.Length then
            result <- Some(Error "unterminated string")
        elif s.[k] = '"' && k + 1 < s.Length && s.[k + 1] = '"' then
            sb.Append '"' |> ignore
            k <- k + 2
        elif s.[k] = '"' then
            result <- Some(Ok(sb.ToString(), k + 1))
        else
            sb.Append s.[k] |> ignore
            k <- k + 1

    result.Value

let private isIdentStart (c: char) = Char.IsLetter c || c = '_'
let private isIdentPart (c: char) = Char.IsLetterOrDigit c || c = '_'

/// A dotted name at `i`: its parts and the index after it.
let private dotted (s: string) (i: int) =
    let parts = ResizeArray<string>()
    let mutable k = i
    let mutable go = k < s.Length && isIdentStart s.[k]

    while go do
        let start = k

        while k < s.Length && isIdentPart s.[k] do
            k <- k + 1

        parts.Add(s.Substring(start, k - start))

        if k + 1 < s.Length && s.[k] = '.' && isIdentStart s.[k + 1] then
            k <- k + 1
        else
            go <- false

    List.ofSeq parts, k

let private skipSpace (s: string) (i: int) =
    let mutable k = i

    while k < s.Length && Char.IsWhiteSpace s.[k] do
        k <- k + 1

    k

/// Read a fix expression. Error: why tess won't apply it.
let parse (fixExpression: string) : Result<Action, string> =
    let s = fixExpression.Trim()

    if s.IndexOfAny [| '{'; '}'; ';' |] >= 0 then
        // BPAForm.cs: `if (FixExpression.IndexOfAny(new[] { '{','}',';' }) >= 0) continue;`
        Error "Tabular Editor doesn't run fix expressions containing {, } or ;"
    else
        let target, k = dotted s 0
        let k = skipSpace s k

        let rest = if k < s.Length then s.Substring k else ""
        let targetName = String.concat "." target

        match target with
        | [] -> Error "expected a property assignment or a method call"
        | _ when rest.StartsWith "(" ->
            let afterOpen = skipSpace s (k + 1)

            if
                afterOpen < s.Length
                && s.[afterOpen] = ')'
                && skipSpace s (afterOpen + 1) = s.Length
            then
                match target with
                | [ "Delete" ] -> Ok Action.Delete
                | [ "TranslatedDisplayFolders"; "Reset" ] -> Ok Action.ResetDisplayFolderTranslations
                | _ ->
                    Error
                        $"{targetName}() is not a method tess applies (Delete() and TranslatedDisplayFolders.Reset() are)"
            else
                Error $"{targetName}(...) with arguments is not supported"
        | [ property ] when rest.StartsWith "=" && not (rest.StartsWith "==") ->
            let v = skipSpace s (k + 1)

            let literal =
                if v >= s.Length then
                    Error "expected a value after ="
                elif s.[v] = '"' then
                    regularString s v |> Result.map (fun (text, e) -> Literal.Text text, e)
                elif s.[v] = '@' && v + 1 < s.Length && s.[v + 1] = '"' then
                    verbatimString s v |> Result.map (fun (text, e) -> Literal.Text text, e)
                else
                    match dotted s v with
                    | [ "true" ], e -> Ok(Literal.Bool true, e)
                    | [ "false" ], e -> Ok(Literal.Bool false, e)
                    | [ "null" ], e -> Ok(Literal.Null, e)
                    | [ t; m ], e when not (t = "it" || t = "this" || t = "outerIt" || t = "Model") ->
                        Ok(Literal.Enum(t, m), e)
                    | _ -> Error "only literal values (true, false, null, a string, or an enum member) are applied"

            literal
            |> Result.bind (fun (value, e) ->
                if skipSpace s e = s.Length then
                    Ok(Action.Assign(property, value))
                else
                    Error "only literal values (true, false, null, a string, or an enum member) are applied")
        | _ when rest.StartsWith "=" -> Error $"{targetName} = ... sets a property of another object"
        | _ -> Error "expected a property assignment or a method call"

// ---- Applying -----------------------------------------------------------------------------------

type private Assignable = {
    /// The property as TOM's JSON (and `tess set`) names it.
    Path: string
    /// The objects it applies to.
    Applies: MetadataObject -> bool
    /// The enum type the value must name, or None for a bool/string property.
    EnumType: string option
    IsBool: bool
}

let private isColumn (o: MetadataObject) = o :? Column
let private isMeasure (o: MetadataObject) = o :? Measure

/// The properties a fix expression may set: those the published rule files set, plus their
/// neighbours, each a plain TOM property in TE's wrapper (WrapperBase.generated.cs).
let private assignable =
    dict [
        "IsHidden",
        {
            Path = "isHidden"
            Applies = fun o -> o :? Column || o :? Measure || o :? Table || o :? Hierarchy
            EnumType = None
            IsBool = true
        }
        "IsAvailableInMDX",
        {
            Path = "isAvailableInMdx"
            Applies = isColumn
            EnumType = None
            IsBool = true
        }
        "IsKey",
        {
            Path = "isKey"
            Applies = isColumn
            EnumType = None
            IsBool = true
        }
        "DataType",
        {
            Path = "dataType"
            Applies = isColumn
            EnumType = Some "DataType"
            IsBool = false
        }
        "SummarizeBy",
        {
            Path = "summarizeBy"
            Applies = isColumn
            EnumType = Some "AggregateFunction"
            IsBool = false
        }
        "FormatString",
        {
            Path = "formatString"
            Applies = fun o -> isColumn o || isMeasure o
            EnumType = None
            IsBool = false
        }
        "Description",
        {
            Path = "description"
            Applies = fun o -> o :? NamedMetadataObject
            EnumType = None
            IsBool = false
        }
        "DisplayFolder",
        {
            Path = "displayFolder"
            Applies = fun o -> isColumn o || isMeasure o || o :? Hierarchy
            EnumType = None
            IsBool = false
        }
    ]

/// A finding fixed, or not.
type Outcome = {
    Finding: Bpa.Finding
    /// None: fixed. Some reason: refused, model untouched for this finding.
    Refused: string option
}

type FixResult = {
    Outcomes: Outcome list
    /// What the deletions also removed or changed (RemoveObject's notes).
    Notes: string list
}

module FixResult =
    let fixedFindings (r: FixResult) =
        r.Outcomes |> List.filter (fun o -> o.Refused.IsNone)

    let refused (r: FixResult) =
        r.Outcomes |> List.filter (fun o -> o.Refused.IsSome)

let private formatAnnotation = "Format"

/// Apply each finding's rule's fix expression to `db` (mutating it; use inside `Mutation.apply`).
/// Property assignments come first, then translation resets, then deletions, each in the order of
/// `findings`. A finding is refused (and nothing done for it) when its fix expression isn't one tess
/// applies, its object can't be addressed, or two rules set one property of one object to different
/// values. An Error means the model was left part-changed: discard it.
let apply (db: Database) (findings: Bpa.Finding list) : Result<FixResult, TessError> =
    let entries = objects db
    let byObject = Dictionary<MetadataObject, ObjectEntry>(HashIdentity.Reference)

    for e in entries do
        byObject.[e.Object] <- e

    let outcomes = Dictionary<Bpa.Finding, string option>(HashIdentity.Reference)
    let refuse (f: Bpa.Finding) (reason: string) = outcomes.[f] <- Some reason

    let withFix =
        findings
        |> List.choose (fun f ->
            match f.Rule.FixExpression with
            | None -> None
            | Some fx ->
                match parse fx with
                | Error reason ->
                    refuse f reason
                    None
                | Ok action ->
                    match byObject.TryGetValue f.Object with
                    | true, e -> Some(f, e, action)
                    | _ ->
                        refuse f $"a {f.ObjectType} isn't an object tess can edit on its own"
                        None)

    // Assignments, checked against the property catalog before anything is written.
    let assignments =
        withFix
        |> List.choose (fun (f, e, action) ->
            match action with
            | Action.Assign(property, value) ->
                match assignable.TryGetValue property with
                | false, _ when property = "Name" ->
                    refuse f "renaming isn't applied from a fix expression (use tess rename, which updates references)"
                    None
                | false, _ ->
                    let known = assignable.Keys |> String.concat ", "
                    refuse f $"{property} is not a property tess sets from a fix expression (it sets {known})"
                    None
                | true, a when not (a.Applies e.Object) ->
                    refuse f $"a {f.ObjectType} has no {property} that tess sets"
                    None
                | true, a ->
                    let descriptor =
                        SetProperty.catalog db e
                        |> List.tryFind (fun d -> String.Equals(d.Path, a.Path, StringComparison.OrdinalIgnoreCase))

                    let valueText =
                        match value, a.EnumType, a.IsBool with
                        | Literal.Bool b, None, true -> Ok(SetProperty.Value.Text(if b then "true" else "false"))
                        | Literal.Text t, None, false -> Ok(SetProperty.Value.Text t)
                        | Literal.Null, None, false -> Ok SetProperty.Value.Null
                        | Literal.Enum(t, m), Some expected, _ when t = expected -> Ok(SetProperty.Value.Text m)
                        | Literal.Enum(t, _), Some expected, _ when t <> expected ->
                            Error $"{property} takes a {expected} value, not a {t}"
                        | _, Some expected, _ -> Error $"{property} takes a {expected} value"
                        | _, None, true -> Error $"{property} takes true or false"
                        | _ -> Error $"{property} takes a string"

                    match descriptor, valueText with
                    | _, Error reason ->
                        refuse f reason
                        None
                    | None, _ ->
                        refuse f $"this {f.ObjectType} has no {a.Path} property"
                        None
                    | Some d, _ when not d.Writable ->
                        let reason = defaultArg d.Reason "read-only"
                        refuse f $"{a.Path} can't be set here: {reason}"
                        None
                    | Some d, Ok(SetProperty.Value.Text m) when
                        d.Type = PropertyCatalog.PropertyType.Enum
                        && not (
                            d.Allowed
                            |> List.exists (fun x -> String.Equals(x, m, StringComparison.OrdinalIgnoreCase))
                        )
                        ->
                        let allowed = String.Join(", ", d.Allowed)
                        refuse f $"{Literal.text value} isn't a value {a.Path} takes here ({allowed})"
                        None
                    | Some d, Ok v -> Some(f, e, property, d.Path, v)
            | _ -> None)

    // Two rules setting one property of one object differently: neither is applied.
    let conflicts =
        assignments
        |> List.groupBy (fun (_, e, _, path, _) -> e.Ref, path.ToUpperInvariant())
        |> List.filter (fun (_, xs) -> xs |> List.map (fun (_, _, _, _, v) -> v) |> List.distinct |> List.length > 1)
        |> List.collect snd

    for f, _, property, _, _ in conflicts do
        let others =
            conflicts
            |> List.filter (fun (g, e, _, _, _) ->
                not (obj.ReferenceEquals(g, f)) && obj.ReferenceEquals(e.Object, f.Object))
            |> List.map (fun (g, _, _, _, _) -> g.Rule.Id)
            |> List.distinct
            |> String.concat ", "

        refuse f $"{others} also sets {property} on this object, to a different value; neither is applied"

    let notes = ResizeArray<string>()

    let run () =
        // Same object, path and value from several rules: one write, every finding fixed.
        let written = HashSet<ObjectRef * string>()

        // A loop, not recursion: a model can have thousands of findings.
        let assign (steps: (Bpa.Finding * ObjectEntry * string * string * SetProperty.Value) list) =
            let mutable failure = None

            for f, e, property, path, value in steps do
                if failure.IsNone && not (outcomes.ContainsKey f) then
                    if written.Add((e.Ref, path.ToUpperInvariant())) then
                        let current () =
                            SetProperty.catalog db e
                            |> List.tryFind (fun d -> d.Path = path)
                            |> Option.map (fun d -> d.Value)

                        let before = current ()

                        match SetProperty.set db e [ { Path = path; Value = value } ] with
                        | Error err -> failure <- Some err
                        | Ok _ ->
                            // TE's wrapper: a changed format string drops Power BI's format hint, and a
                            // column that becomes the key stops the table's other columns being keys.
                            if before <> current () then
                                match property, e.Object with
                                | "FormatString", (:? Column as c) when c.Annotations.Contains formatAnnotation ->
                                    c.Annotations.Remove formatAnnotation |> ignore
                                | "FormatString", (:? Measure as m) when m.Annotations.Contains formatAnnotation ->
                                    m.Annotations.Remove formatAnnotation |> ignore
                                | "IsKey", (:? Column as c) when c.IsKey ->
                                    for other in c.Table.Columns do
                                        if
                                            not (obj.ReferenceEquals(other, c)) && other.Type <> ColumnType.RowNumber
                                        then
                                            other.IsKey <- false
                                | _ -> ()

                            outcomes.[f] <- None
                    else
                        outcomes.[f] <- None

            match failure with
            | Some err -> Error err
            | None -> Ok()

        let resets () =
            for f, e, action in withFix do
                if action = Action.ResetDisplayFolderTranslations && not (outcomes.ContainsKey f) then
                    let folder =
                        match e.Object with
                        | :? Measure as m -> Some m.DisplayFolder
                        | :? Column as c -> Some c.DisplayFolder
                        | :? Hierarchy as h -> Some h.DisplayFolder
                        | _ -> None

                    match folder, e.Object with
                    | Some folder, (:? NamedMetadataObject as o) ->
                        for culture in db.Model.Cultures do
                            let existing = culture.ObjectTranslations.[o, TranslatedProperty.DisplayFolder]

                            match existing with
                            | null when String.IsNullOrEmpty folder -> ()
                            | null ->
                                culture.ObjectTranslations.Add(
                                    new ObjectTranslation(
                                        Object = o,
                                        Property = TranslatedProperty.DisplayFolder,
                                        Value = folder
                                    )
                                )
                            | t when isNull folder -> culture.ObjectTranslations.Remove t |> ignore
                            | t when t.Value <> folder -> t.Value <- folder
                            | _ -> ()

                        outcomes.[f] <- None
                    | _ -> refuse f $"a {f.ObjectType} has no display folder"

        let deletes () =
            for f, e, action in withFix do
                if action = Action.Delete && not (outcomes.ContainsKey f) then
                    let rec isGone (o: MetadataObject) =
                        not (isNull o) && (isNull o.Parent && not (o :? Model) || isGone o.Parent)

                    if isGone e.Object then
                        // Removed with something an earlier Delete() removed.
                        outcomes.[f] <- None
                    else
                        match
                            RemoveObject.remove db (RemoveObject.RemoveSpec.ofSelectors [ ObjectRef.format e.Ref ])
                        with
                        | Ok effect ->
                            // What else went with it (the diff shows the object itself).
                            notes.AddRange(effect.Notes |> List.filter (fun n -> n.StartsWith "Also removed"))
                            outcomes.[f] <- None
                        | Error(UnsupportedRemove(_, reason)) -> refuse f $"it can't be removed: {reason}"
                        | Error(RemoveBlocked(_, dependents)) ->
                            let n = dependents.Length
                            let refs = if n = 1 then "1 reference" else $"{n} references"
                            refuse f $"other DAX still refers to it ({refs}; `tess deps --referenced-by` lists them)"
                        | Error err -> refuse f (TessError.message err)

        assign assignments
        |> Result.map (fun () ->
            resets ()
            deletes ())

    run ()
    |> Result.map (fun () -> {
        Outcomes = [
            for f in findings do
                if f.Rule.FixExpression.IsSome then
                    {
                        Finding = f
                        Refused =
                            match outcomes.TryGetValue f with
                            | true, r -> r
                            | _ -> Some "not applied"
                    }
        ]
        Notes = List.ofSeq notes
    })
