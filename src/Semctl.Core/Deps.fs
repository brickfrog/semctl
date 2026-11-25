/// Which objects refer to which: DAX references plus structural links (sort-by columns,
/// hierarchy levels, relationships). Resolution follows TE2's FormulaFixup, plus VAR/parameter
/// shadowing and locally defined column names.
module Semctl.Deps

open System
open System.Collections.Generic
open Microsoft.AnalysisServices.Tabular
open Semctl.Dax
open Semctl.Inventory

type Target =
    | Resolved of ObjectRef
    /// A reference that names no object in the model; Text is the reference as written.
    | Unresolved of text: string

type Reference = {
    From: ObjectRef
    Target: Target
    /// Where the reference lives: a DAX property path ("expression",
    /// "formatStringDefinition.expression", "tablePermissions.Sales.filterExpression") or a
    /// structural property ("sortByColumn", "column", "fromColumn", "toColumn").
    Via: string
    /// 1-based line in the DAX, for DAX references.
    Line: int option
    /// How the reference is written in the source (for DAX), or the property value.
    Text: string
    /// Offset of the reference's first character in the DAX (None for structural links).
    Start: int option
    /// Length of the reference in the DAX, so Text = dax.Substring(Start, Length).
    Length: int option
}

/// A DAX expression held by an object.
type DaxSource = {
    Owner: ObjectEntry
    Property: string
    Dax: string
    /// The table unqualified [Column] references resolve against: the object's own table for
    /// measures, columns, calculation items and RLS filters. None for table-level expressions
    /// (calculated tables, detail rows, calculation group expressions) and functions: there, as in
    /// TE2, [X] is a measure or a column the expression itself produces (CALENDAR(...)[Date]).
    ContextTable: Table option
    /// Column names the expression itself produces (a calculated table's own columns): an
    /// unqualified [X] naming one of them is local, not a model reference.
    ProducedColumns: string list
}

let private nonEmpty (s: string) = not (String.IsNullOrWhiteSpace s)

/// Every DAX expression in the model, attributed to the object a user would think owns it.
let daxSources (entries: ObjectEntry list) : DaxSource list =
    let srcProducing owner property dax ctx produced =
        if nonEmpty dax then
            Some {
                Owner = owner
                Property = property
                Dax = dax
                ContextTable = ctx
                ProducedColumns = produced
            }
        else
            None

    let src owner property dax ctx = srcProducing owner property dax ctx []

    let tableEntries =
        entries
        |> List.choose (fun e ->
            match e.Object with
            | :? Table as t -> Some(t.Name, e)
            | _ -> None)
        |> dict

    [
        for e in entries do
            match e.Object with
            | :? Measure as m ->
                src e "expression" m.Expression (Some m.Table)

                if not (isNull m.FormatStringDefinition) then
                    src e "formatStringDefinition.expression" m.FormatStringDefinition.Expression (Some m.Table)

                if not (isNull m.DetailRowsDefinition) then
                    src e "detailRowsDefinition.expression" m.DetailRowsDefinition.Expression (Some m.Table)

                if not (isNull m.KPI) then
                    src e "kpi.statusExpression" m.KPI.StatusExpression (Some m.Table)
                    src e "kpi.targetExpression" m.KPI.TargetExpression (Some m.Table)
                    src e "kpi.trendExpression" m.KPI.TrendExpression (Some m.Table)
            | :? CalculatedColumn as c -> src e "expression" c.Expression (Some c.Table)
            | :? Table as t ->
                if not (isNull t.DefaultDetailRowsDefinition) then
                    src e "defaultDetailRowsDefinition.expression" t.DefaultDetailRowsDefinition.Expression None

                if not (isNull t.CalculationGroup) then
                    let cg = t.CalculationGroup

                    for name, x in
                        [
                            "calculationGroup.noSelectionExpression", cg.NoSelectionExpression
                            "calculationGroup.multipleOrEmptySelectionExpression", cg.MultipleOrEmptySelectionExpression
                        ] do
                        if not (isNull x) then
                            src e (name + ".expression") x.Expression None

                            if not (isNull x.FormatStringDefinition) then
                                src
                                    e
                                    (name + ".formatStringDefinition.expression")
                                    x.FormatStringDefinition.Expression
                                    None
            | :? Partition as p ->
                match p.Source with
                // A calculated table's DAX: attributed to the table, which is what people mean.
                | :? CalculatedPartitionSource as cps ->
                    match tableEntries.TryGetValue p.Table.Name with
                    | true, te ->
                        let produced = [ for c in p.Table.Columns -> c.Name ]
                        srcProducing te "expression" cps.Expression None produced
                    | _ -> None
                | _ -> None
            | :? CalculationItem as ci ->
                let table = ci.CalculationGroup.Table
                src e "expression" ci.Expression (Some table)

                if not (isNull ci.FormatStringDefinition) then
                    src e "formatStringDefinition.expression" ci.FormatStringDefinition.Expression (Some table)
            | :? ModelRole as r ->
                for tp in r.TablePermissions do
                    src e $"tablePermissions.{tp.Table.Name}.filterExpression" tp.FilterExpression (Some tp.Table)
            | :? Function as f -> src e "expression" f.Expression None
            | _ -> ()
    ]
    |> List.choose id

/// Name lookups, case-insensitive like Tabular itself.
type private Index(entries: ObjectEntry list) =
    let ci = StringComparer.OrdinalIgnoreCase
    let tables = Dictionary<string, ObjectEntry>(ci)
    let calendars = Dictionary<string, ObjectEntry>(ci)
    let functions = Dictionary<string, ObjectEntry>(ci)
    let measures = Dictionary<string, ObjectEntry>(ci)
    let tableMembers = Dictionary<string, ObjectEntry>(ci)
    let byObject = Dictionary<MetadataObject, ObjectEntry>(HashIdentity.Reference)
    let memberKey (table: string) (name: string) = table + "\u0000" + name

    do
        for e in entries do
            byObject.[e.Object] <- e

            match e.Object with
            | :? Table as t -> tables.[t.Name] <- e
            | :? Calendar as c -> calendars.[c.Name] <- e
            | :? Function as f -> functions.[f.Name] <- e
            | :? Measure as m ->
                measures.[m.Name] <- e
                // Columns win over measures for Table[Name]; add measures only where free.
                tableMembers.TryAdd(memberKey m.Table.Name m.Name, e) |> ignore
            | :? Column as c -> tableMembers.[memberKey c.Table.Name c.Name] <- e
            | _ -> ()

    member _.Table name =
        match tables.TryGetValue name with
        | true, e -> Some e
        | _ ->
            match calendars.TryGetValue name with
            | true, e -> Some e
            | _ -> None

    member _.Function name =
        match functions.TryGetValue name with
        | true, e -> Some e
        | _ -> None

    member _.Member (table: string) (name: string) =
        match tableMembers.TryGetValue(memberKey table name) with
        | true, e -> Some e
        | _ -> None

    member _.Measure name =
        match measures.TryGetValue name with
        | true, e -> Some e
        | _ -> None

    member _.Entry(o: MetadataObject) =
        match o with
        | null -> None
        | o ->
            match byObject.TryGetValue o with
            | true, e -> Some e
            | _ -> None

/// VAR names and function parameters that can stand for a table.
let private locals (isFunction: bool) (tokens: Token array) =
    let names = HashSet<string>(StringComparer.OrdinalIgnoreCase)

    let lambda =
        tokens
        |> Array.tryFindIndex (fun t -> t.Kind = TokenKind.Operator && t.Text = "=>")

    for i in 0 .. tokens.Length - 1 do
        let t = tokens.[i]
        let next = if i + 1 < tokens.Length then Some tokens.[i + 1] else None

        match t.Kind, next with
        | TokenKind.Identifier, Some n when
            t.Text.Equals("VAR", StringComparison.OrdinalIgnoreCase)
            && n.Kind = TokenKind.Identifier
            ->
            names.Add n.Text |> ignore
        // UDF parameter list: (name: TYPE, ...) before the first =>
        | TokenKind.Identifier, Some n when
            isFunction
            && n.Kind = TokenKind.Operator
            && n.Text = ":"
            && lambda |> Option.exists (fun l -> i < l)
            ->
            names.Add t.Text |> ignore
        | _ -> ()

    names

/// Track virtual column names through table expressions and VAR bindings. Record occurrences,
/// not names: a projected column is visible in an iterator over that table, even when another
/// branch of the same expression reads a model measure with the same spelling.
let private localColumnOffsets isFunction (dax: string) =
    let offsets = HashSet<int>()

    let rec unparen (e: DaxAst.Expr) =
        match e.Kind with
        | DaxAst.Paren inner -> unparen inner
        | _ -> e

    let rec visit (vars: Map<string, Set<string>>) (row: Set<string>) (expr: DaxAst.Expr) =
        let visitHere = visit vars row

        match expr.Kind with
        | DaxAst.Bracketed name ->
            if row.Contains(name.ToUpperInvariant()) then
                offsets.Add expr.Span.Start |> ignore

            Set.empty
        | DaxAst.Identifier name -> Map.tryFind (name.ToUpperInvariant()) vars |> Option.defaultValue Set.empty
        | DaxAst.Paren inner -> visitHere inner
        | DaxAst.TableConstructor rows ->
            rows |> List.iter (visitHere >> ignore)

            match rows with
            | [] -> Set.empty
            | first :: _ ->
                match (unparen first).Kind with
                | DaxAst.Tuple cols -> [ for k in 1 .. cols.Length -> $"VALUE{k}" ] |> Set.ofList
                | _ -> Set.singleton "VALUE"
        | DaxAst.VarBlock(decls, body) ->
            let mutable scope = vars

            for decl in decls do
                scope <- scope.Add(decl.Name.Name.ToUpperInvariant(), visit scope row decl.Value)

            visit scope row body
        | DaxAst.Call(name, args) ->
            let fn = name.Name.ToUpperInvariant()

            let projection input (args: DaxAst.Expr list) =
                let mutable output = Set.empty

                let rec pairs (rest: DaxAst.Expr list) =
                    match rest with
                    | { Kind = DaxAst.StringLiteral alias } :: value :: tail ->
                        visit vars (Set.union row input) value |> ignore
                        output <- output.Add(alias.ToUpperInvariant())
                        pairs tail
                    | arg :: tail ->
                        let produced = visit vars (Set.union row input) arg
                        // A grouping column, an unnamed SELECTCOLUMNS projection, or ROLLUPADDISSUBTOTAL keeps its name.
                        match (unparen arg).Kind with
                        | DaxAst.Bracketed n
                        | DaxAst.Qualified({ Name = ""; Quoted = true }, n) when input.Contains(n.ToUpperInvariant()) ->
                            output <- output.Add(n.ToUpperInvariant())
                        | DaxAst.Call(c, _) when
                            c.Name.Equals("ROLLUPADDISSUBTOTAL", StringComparison.OrdinalIgnoreCase)
                            ->
                            output <- Set.union output produced
                        | _ -> ()

                        pairs tail
                    | [] -> ()

                pairs args
                output

            match fn, args with
            | "GENERATESERIES", _ ->
                args |> List.iter (visitHere >> ignore)
                Set.singleton "VALUE"
            | ("CALENDAR" | "CALENDARAUTO"), _ ->
                args |> List.iter (visitHere >> ignore)
                Set.singleton "DATE"
            | "ROLLUPADDISSUBTOTAL", _ ->
                let mutable output = Set.empty

                for arg in args do
                    match (unparen arg).Kind with
                    | DaxAst.StringLiteral alias -> output <- output.Add(alias.ToUpperInvariant())
                    | _ -> visitHere arg |> ignore

                output
            | "IF", cond :: branches ->
                visitHere cond |> ignore
                branches |> List.map visitHere |> Set.unionMany
            | "SWITCH", expr :: rest ->
                visitHere expr |> ignore

                let rec collect acc =
                    function
                    | value :: result :: tail ->
                        visitHere value |> ignore
                        collect (Set.union acc (visitHere result)) tail
                    | [ fallback ] -> Set.union acc (visitHere fallback)
                    | [] -> acc

                collect Set.empty rest
            | ("ROW" | "DATATABLE" | "SUMMARIZECOLUMNS"), _ -> projection Set.empty args
            | ("ADDCOLUMNS" | "SELECTCOLUMNS" | "SUMMARIZE" | "GROUPBY"), source :: rest ->
                let input = visitHere source
                let output = projection input rest
                if fn = "ADDCOLUMNS" then Set.union input output else output
            | ("FILTER" | "SUMX" | "AVERAGEX" | "COUNTX" | "COUNTAX" | "MINX" | "MAXX" | "PRODUCTX" | "MEDIANX" | "RANKX" | "GEOMEANX" | "PERCENTILEX.INC" | "PERCENTILEX.EXC" | "STDEVX.S" | "STDEVX.P" | "VARX.S" | "VARX.P"),
              source :: value :: rest ->
                let input = visitHere source
                visit vars (Set.union row input) value |> ignore
                rest |> List.iter (visitHere >> ignore)
                if fn = "FILTER" then input else Set.empty
            | "CONCATENATEX", source :: value :: rest ->
                let inRow = visit vars (Set.union row (visitHere source))
                inRow value |> ignore
                // Delimiter, then pairs of order expression and direction.
                rest
                |> List.iteri (fun i arg -> (if i % 2 = 1 then inRow else visitHere) arg |> ignore)

                Set.empty
            | ("TOPN" | "SAMPLE"), count :: source :: order ->
                visitHere count |> ignore
                let input = visitHere source

                order
                |> List.iteri (fun i arg ->
                    (if i % 2 = 0 then
                         visit vars (Set.union row input)
                     else
                         visitHere)
                        arg
                    |> ignore)

                input
            | ("XIRR" | "XNPV"), source :: values :: dates :: rest ->
                let input = visitHere source
                let inRow = visit vars (Set.union row input)
                inRow values |> ignore
                inRow dates |> ignore
                rest |> List.iter (visitHere >> ignore)
                Set.empty
            | "LINESTX", source :: rest ->
                let input = visitHere source
                let inRow = visit vars (Set.union row input)
                rest |> List.iter (inRow >> ignore)
                Set.empty
            | "SUBSTITUTEWITHINDEX", source :: nameArg :: indexTable :: order ->
                let input = visitHere source
                let indexCols = visitHere indexTable
                let inRow = visit vars (Set.union row (Set.union input indexCols))

                order
                |> List.iteri (fun i arg -> (if i % 2 = 0 then inRow else visitHere) arg |> ignore)

                match (unparen nameArg).Kind with
                | DaxAst.StringLiteral alias -> input.Add(alias.ToUpperInvariant())
                | _ -> input
            | ("INDEX" | "OFFSET" | "WINDOW" | "RANK" | "ROWNUMBER"), _ ->
                let isWindowClause (e: DaxAst.Expr) =
                    match (unparen e).Kind with
                    | DaxAst.Call(c, inner) when
                        c.Name.Equals("ORDERBY", StringComparison.OrdinalIgnoreCase)
                        || c.Name.Equals("PARTITIONBY", StringComparison.OrdinalIgnoreCase)
                        || c.Name.Equals("MATCHBY", StringComparison.OrdinalIgnoreCase)
                        ->
                        Some inner
                    | _ -> None

                let clauses, other =
                    args
                    |> List.fold
                        (fun (cs, os) arg ->
                            match isWindowClause arg with
                            | Some inner -> (inner :: cs), os
                            | None -> cs, (arg :: os))
                        ([], [])

                let input = other |> List.rev |> List.map visitHere |> Set.unionMany
                let inRow = visit vars (Set.union row input)

                for inner in List.rev clauses do
                    inner |> List.iter (inRow >> ignore)

                if fn = "RANK" || fn = "ROWNUMBER" then Set.empty else input
            | ("GENERATE" | "GENERATEALL"), [ outer; inner ] ->
                let input = visitHere outer
                Set.union input (visit vars (Set.union row input) inner)
            | ("UNION" | "INTERSECT" | "EXCEPT" | "CALCULATETABLE" | "DISTINCT"), source :: rest ->
                let input = visitHere source
                rest |> List.iter (visitHere >> ignore)
                input
            | ("CROSSJOIN" | "NATURALINNERJOIN" | "NATURALLEFTOUTERJOIN"), _ ->
                args |> List.map visitHere |> Set.unionMany
            | _ ->
                args |> List.iter (visitHere >> ignore)
                Set.empty
        | _ ->
            DaxAst.children expr |> List.iter (visitHere >> ignore)
            Set.empty

    let parse read =
        match read DaxParser.ParseOptions.defaults dax with
        | Error _ when dax.Contains(';') ->
            read
                {
                    DaxParser.ParseOptions.Semicolons = true
                }
                dax
        | result -> result

    if isFunction then
        parse DaxParser.parseFunctionWith
        |> Result.iter (fun f ->
            for p in f.Parameters do
                p.Default |> Option.iter (visit Map.empty Set.empty >> ignore)

            visit Map.empty Set.empty f.Body |> ignore)
    else
        parse DaxParser.parseExpressionWith
        |> Result.iter (visit Map.empty Set.empty >> ignore)

    offsets

/// Keywords an expression can follow: in `RETURN [Total]` or `NOT [Flag]`, the keyword is not a table.
let private operandKeywords =
    HashSet<string>([ "RETURN"; "NOT"; "IN" ], StringComparer.OrdinalIgnoreCase)

/// Functions whose argument is a reference to a model object.
let private nameOfFunctions =
    HashSet<string>([ "NAMEOF"; "TABLEOF" ], StringComparer.OrdinalIgnoreCase)

let private resolveSource (index: Index) (s: DaxSource) : Reference list =
    let tokens = tokenize s.Dax |> significant |> Array.ofList
    let isFunction = s.Owner.Ref.Kind = ObjectKind.Function
    let names = locals isFunction tokens

    let mayHaveLocalColumns =
        s.Dax.Contains '"'
        || s.Dax.Contains '{'
        || s.Dax.Contains("GENERATESERIES", StringComparison.OrdinalIgnoreCase)
        || s.Dax.Contains("CALENDAR", StringComparison.OrdinalIgnoreCase)

    let localColumns =
        lazy
            (if mayHaveLocalColumns then
                 localColumnOffsets isFunction s.Dax
             else
                 HashSet<int>())

    let reference (target: Target) (first: Token) (last: Token) =
        let length = last.Start + last.Length - first.Start

        {
            From = s.Owner.Ref
            Target = target
            Via = s.Property
            Line = Some(lineOf s.Dax first.Start)
            Text = s.Dax.Substring(first.Start, length)
            Start = Some first.Start
            Length = Some length
        }

    let resolved (e: ObjectEntry) = Resolved e.Ref

    let isOpenParen (t: Token) =
        t.Kind = TokenKind.Punctuation && t.Text = "("

    let out = ResizeArray<Reference>()
    let mutable i = 0

    while i < tokens.Length do
        let t = tokens.[i]
        let next = if i + 1 < tokens.Length then Some tokens.[i + 1] else None

        match t.Kind, next with
        // Table[Name]: a column, else a measure, in that table.
        | (TokenKind.Identifier | TokenKind.QuotedTable), Some n when
            n.Kind = TokenKind.Bracketed
            && not (t.Kind = TokenKind.Identifier && operandKeywords.Contains t.Text)
            ->
            // ''[Col] names a column of an anonymous table (e.g. rows a function returns): local.
            let isLocal =
                (t.Kind = TokenKind.Identifier && names.Contains t.Text)
                || (t.Kind = TokenKind.QuotedTable && t.Text = "")

            if not isLocal then
                let target =
                    match index.Member t.Text n.Text with
                    | Some e -> resolved e
                    | None -> Unresolved(s.Dax.Substring(t.Start, n.Start + n.Length - t.Start))

                out.Add(reference target t n)

            i <- i + 1
        // Name( : a function call; only user-defined functions are model objects.
        | TokenKind.Identifier, Some n when isOpenParen n ->
            index.Function t.Text
            |> Option.iter (fun f -> out.Add(reference (resolved f) t t))
        // A bare name: a table (or calendar), unless the expression defines it.
        | TokenKind.Identifier, _ ->
            if not (names.Contains t.Text) && not (operandKeywords.Contains t.Text) then
                index.Table t.Text |> Option.iter (fun e -> out.Add(reference (resolved e) t t))
        // 'Quoted' is always meant as a table.
        | TokenKind.QuotedTable, _ ->
            let target =
                match index.Table t.Text with
                | Some e -> resolved e
                | None -> Unresolved(s.Dax.Substring(t.Start, t.Length))

            out.Add(reference target t t)
        // [Name]: a column of the context table, else a measure there, else a measure anywhere.
        | TokenKind.Bracketed, _ ->
            let produced =
                s.ProducedColumns
                |> List.exists (fun c -> String.Equals(c, t.Text, StringComparison.OrdinalIgnoreCase))

            // NAMEOF([X]) and TABLEOF([X]) take a model column or measure, never a local name (in a
            // field parameter, the label next to it is often the same text).
            let modelArgument =
                i >= 2
                && isOpenParen tokens.[i - 1]
                && tokens.[i - 2].Kind = TokenKind.Identifier
                && nameOfFunctions.Contains tokens.[i - 2].Text

            if modelArgument || (not (localColumns.Value.Contains t.Start) && not produced) then
                let inContext =
                    s.ContextTable |> Option.bind (fun table -> index.Member table.Name t.Text)

                let target =
                    match inContext |> Option.orElseWith (fun () -> index.Measure t.Text) with
                    | Some e -> resolved e
                    | None -> Unresolved(s.Dax.Substring(t.Start, t.Length))

                out.Add(reference target t t)
        | _ -> ()

        i <- i + 1

    List.ofSeq out

let private structural (index: Index) (entries: ObjectEntry list) : Reference list =
    let link (from: ObjectEntry) via (target: MetadataObject) (text: string) =
        index.Entry target
        |> Option.map (fun t -> {
            From = from.Ref
            Target = Resolved t.Ref
            Via = via
            Line = None
            Text = text
            Start = None
            Length = None
        })

    [
        for e in entries do
            match e.Object with
            | :? Column as c when not (isNull c.SortByColumn) ->
                link e "sortByColumn" c.SortByColumn c.SortByColumn.Name
            | :? Level as l when not (isNull l.Column) -> link e "column" l.Column l.Column.Name
            | :? SingleColumnRelationship as r ->
                if not (isNull r.FromColumn) then
                    link e "fromColumn" r.FromColumn $"{r.FromTable.Name}[{r.FromColumn.Name}]"

                if not (isNull r.ToColumn) then
                    link e "toColumn" r.ToColumn $"{r.ToTable.Name}[{r.ToColumn.Name}]"
            | _ -> ()
    ]
    |> List.choose id

type Graph = {
    Entries: ObjectEntry list
    References: Reference list
}

let build (db: Database) : Graph =
    let entries = objects db
    let index = Index entries

    {
        Entries = entries
        References =
            (daxSources entries |> List.collect (resolveSource index))
            @ structural index entries
    }

/// What an object refers to (resolved or not), in source order.
let dependsOn (g: Graph) (r: ObjectRef) =
    g.References |> List.filter (fun x -> x.From = r)

/// What refers to an object.
let referencedBy (g: Graph) (r: ObjectRef) =
    g.References |> List.filter (fun x -> x.Target = Resolved r)

/// References that name no object.
let unresolved (g: Graph) =
    g.References
    |> List.filter (fun x ->
        match x.Target with
        | Unresolved _ -> true
        | Resolved _ -> false)

type Direction =
    | DependsOn
    | ReferencedBy

/// One neighbour in a dependency tree: every place the link occurs, and (to the requested depth)
/// the neighbour's own neighbours.
type Node = {
    Target: Target
    /// (property, line) for each occurrence of this link.
    Via: (string * int option) list
    Children: Node list
    /// The target is already on the path from the root (a circular reference); not expanded.
    Cycle: bool
}

/// The neighbours of an object, grouped by target, expanded to `depth` levels (0 = unlimited).
let tree (direction: Direction) (depth: int) (g: Graph) (root: ObjectRef) : Node list =
    let rec expand (path: Set<ObjectRef>) (level: int) (r: ObjectRef) =
        let links =
            match direction with
            | DependsOn -> dependsOn g r |> List.map (fun x -> x.Target, (x.Via, x.Line))
            | ReferencedBy -> referencedBy g r |> List.map (fun x -> Resolved x.From, (x.Via, x.Line))

        links
        |> List.groupBy fst
        |> List.map (fun (target, occurrences) ->
            let via = occurrences |> List.map snd |> List.distinct

            match target with
            | Resolved t when path.Contains t -> {
                Target = target
                Via = via
                Children = []
                Cycle = true
              }
            | Resolved t when depth = 0 || level < depth -> {
                Target = target
                Via = via
                Children = expand (path.Add t) (level + 1) t
                Cycle = false
              }
            | _ -> {
                Target = target
                Via = via
                Children = []
                Cycle = false
              })

    expand (Set.singleton root) 1 root

/// Resolve DAX as if it were a source's expression, against a fixed set of entries: for checking
/// what a rewritten expression would refer to before applying it (lint fixes).
let resolver (entries: ObjectEntry list) : DaxSource -> Reference list =
    let index = Index entries
    resolveSource index
