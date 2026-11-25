namespace Semctl.DaxEngine

// Aggregating by group: `CALCULATE ( SUM ( F[x] ) )` evaluated in a row context of a model table
// T, row after row of an iteration, looks its value up in buckets of F's rows instead of making a
// filter context per row. See "Aggregating by group" in the author's guide (Types.fs) for why the
// value is the same and where it falls back to the row-by-row evaluation.

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Threading
open Semctl
open Semctl.DaxAst

/// Set while check mode works a value out row by row (per thread).
[<Sealed>]
type internal GroupAggSuppress private () =
    [<ThreadStatic; DefaultValue>]
    static val mutable private on: bool

    static member On
        with get () = GroupAggSuppress.on
        and set (v: bool) = GroupAggSuppress.on <- v

module GroupAgg =

    [<RequireQualifiedAccess>]
    type private EntryKind =
        | Unusable
        | Usable of
            fact: BTable *
            agg: Expr *
            kept: FilterCtx *
            buckets: Dictionary<int, ResizeArray<int>> *
            unique: bool

    /// What one iteration's CALCULATE (or measure) needs, made on its first row: the rows of F
    /// under the filters the transition keeps, by the row of T they belong to, and the values
    /// worked out.
    [<Sealed; AllowNullLiteral>]
    type private Entry(table: TableVal, source: SourceText, kind: EntryKind) =
        member _.Table = table
        /// The text the aggregation is written in (its names resolve there).
        member _.Source = source
        member _.Kind = kind
        /// By the first row of the row set: the value (for a row set with fact rows).
        member val Values = Dictionary<int, DaxValue>() with get
        /// The value over no fact rows, once worked out.
        member val Empty = ValueNone with get, set

    /// The aggregations done by group: each reads nothing of its context but the visible stored
    /// rows of one table (`Filters.visibleStored`), here F's (Functions/Aggregation.fs). One
    /// that reads anything else (DISTINCTCOUNT reads whether the blank row is visible) must not
    /// be added.
    let private columnAggregations = [ "SUM"; "MIN"; "MAX"; "AVERAGE"; "COUNT"; "COUNTA" ]

    [<RequireQualifiedAccess>]
    type private Over =
        /// `F[x]`.
        | Column of Expr
        /// `F` (COUNTROWS).
        | Table of Expr

    /// The expression without the CALCULATEs around it that have no filter argument: with no row
    /// context such a CALCULATE changes nothing (no transition, no filter, no modifier).
    let rec private reduce (e: Expr) : Expr =
        let e = Fn.unparen e

        match e.Kind with
        | Call(name, [ arg ]) when name.Name.ToUpperInvariant() = "CALCULATE" -> reduce arg
        | _ -> e

    /// The aggregation an expression is, if it is one done by group: `SUM ( F[x] )` (and the other
    /// `columnAggregations` of one column), `COUNTROWS ( F )`.
    let private aggregation (e: Expr) : (Expr * Over) option =
        let e = reduce e

        match e.Kind with
        | Call(name, [ arg ]) ->
            let n = name.Name.ToUpperInvariant()

            match (Fn.unparen arg).Kind with
            | Qualified _ when columnAggregations |> List.contains n -> Some(e, Over.Column arg)
            | QuotedTable _
            | Identifier _ when n = "COUNTROWS" -> Some(e, Over.Table arg)
            | _ -> None
        | _ -> None

    let private unusable (table: TableVal) (source: SourceText) =
        Entry(table, source, EntryKind.Unusable)

    /// The entry for the iteration `rc` is a row of, or one that says to fall back. `inner`: the
    /// context the aggregation is evaluated in, from the caller's and a filter context.
    let private make
        (ev: IEval)
        (ctx: Ctx)
        (rc: RowCtx)
        (expr: Expr)
        (source: SourceText)
        (inner: Ctx -> FilterCtx -> Ctx)
        : Entry =
        let model = ctx.Model
        let fc = ctx.Filter
        let cfg = fc.Config
        let t = rc.Table
        let no () = unusable t source

        match aggregation expr with
        | None -> no ()
        | Some(agg, over) ->
            if
                model.Computing.Count > 0
                || model.CalcGroups.Length > 0
                || (CalendarRegistry.tryGet model).IsSome
                || not t.IsPureBase
                || not (obj.ReferenceEquals(t.Config, cfg))
            then
                no ()
            else
                let table = t.BaseTable

                // T's expanded table is T alone: the transition's filter then keeps exactly the
                // rows it names (with relationships of its own, the filter's image in a related
                // table can drop rows that have no blank row to go to).
                // T is no date table: a transition on one has a rule of its own (`DateTable`).
                if
                    (cfg.Expanded table).Length <> 1
                    || table.Columns
                       |> Array.exists (fun c -> DateTable.standing cfg c <> DateTable.No)
                then
                    no ()
                else
                    let at = inner ctx fc

                    let fact =
                        match over with
                        | Over.Column c -> ev.Column(at, c).Table
                        | Over.Table x ->
                            match ev.TryModelTable(at, x) with
                            | Some f -> f
                            | None -> null

                    let shared = VisCache.Of cfg

                    if
                        isNull fact
                        || not (obj.ReferenceEquals(fact, table) || cfg.IsExpanded(fact, table))
                        || (cfg.AmbiguousFrom fact).IsSome
                        || not shared.Usable
                        || not (shared.Intersective fact)
                    then
                        no ()
                    else
                        let kept = Filters.keptBy fc [ Rows(table, RowSet.All table.Size, cfg) ]

                        if not (shared.Safe(fact, kept.Filters)) then
                            no ()
                        else
                            let rows = Filters.visible kept fact
                            let map = cfg.RowMap(fact, table)
                            let stored = fact.RowCount
                            let tRows = table.RowCount
                            let buckets = Dictionary<int, ResizeArray<int>>()

                            for f in rows.Rows do
                                if f < stored then
                                    let r = map.[f]

                                    if r < tRows then
                                        match buckets.TryGetValue r with
                                        | true, list -> list.Add f
                                        | _ ->
                                            let list = ResizeArray<int>(1)
                                            list.Add f
                                            buckets.[r] <- list

                            Entry(t, source, EntryKind.Usable(fact, agg, kept, buckets, table.UniqueColumn.IsSome))

    /// The fact rows of a row set of T, ascending.
    let private factRows (buckets: Dictionary<int, ResizeArray<int>>) (rows: RowSet) : int array =
        if rows.Count = 1 then
            match buckets.TryGetValue rows.Rows.[0] with
            | true, list -> list.ToArray()
            | _ -> [||]
        else
            let all = ResizeArray<int>()

            for r in rows.Rows do
                match buckets.TryGetValue r with
                | true, list -> all.AddRange list
                | _ -> ()

            let a = all.ToArray()
            Array.sortInPlace a
            a

    /// The aggregation's value over these fact rows: the registered function, evaluated in a
    /// context with the row's filters whose rows of F are these.
    let private valueOver
        (ev: IEval)
        (ctx: Ctx)
        (inner: Ctx -> FilterCtx -> Ctx)
        (e: Entry)
        (fact: BTable)
        (agg: Expr)
        (kept: FilterCtx)
        (rows: RowSet)
        (facts: int array)
        =
        let cfg = ctx.Filter.Config
        let table = e.Table.BaseTable
        let fc = FilterCtx(kept.Filters @ [ Rows(table, rows, cfg) ], cfg)
        fc.Visible.[fact.Id * (fact.Model.Rels.Length + 1)] <- RowSet.OfSorted(fact.Size, facts)
        ev.Scalar(inner ctx fc, agg)

    /// Check mode: the value and the fact rows compared with the row-by-row evaluation's.
    let private check
        (ctx: Ctx)
        (span: Span)
        (slow: unit -> DaxValue)
        (fact: BTable)
        (facts: int array)
        (value: DaxValue)
        =
        let stats = ctx.Model.Incremental
        let all = IncrementalStats.Global
        GroupAggSuppress.On <- true

        let expected, rows =
            try
                let v =
                    try
                        Ok(slow ())
                    with ex ->
                        Error ex.Message

                let rows =
                    try
                        let moved = Context.transition ctx span
                        Some(Filters.visibleStored moved.Filter fact)
                    with
                    | EngineException _
                    | DaxRuntimeException _ -> None

                v, rows
            finally
                GroupAggSuppress.On <- false

        Interlocked.Increment(&stats.GroupChecked) |> ignore
        Interlocked.Increment(&all.GroupChecked) |> ignore

        let sameRows =
            match rows with
            | Some r -> r.Length = facts.Length && Array.forall2 (=) r facts
            | None -> false

        let sameValue =
            match expected with
            | Ok v -> ValueKey.same v value
            | Error _ -> false

        if not (sameRows && sameValue) then
            Interlocked.Increment(&stats.GroupMismatched) |> ignore
            Interlocked.Increment(&all.GroupMismatched) |> ignore

            let says =
                match expected with
                | Ok v -> $"%A{v}"
                | Error m -> $"an error ({m})"

            raise (
                InvalidOperationException(
                    $"the value by group at {Fail.where ctx span} differs from the row-by-row evaluation: %A{value} over {facts.Length} rows of '{fact.Name}', row by row {says} over {rows |> Option.map _.Length} rows"
                )
            )

    /// Whether anything by group may be tried in this context.
    let private allowed (ctx: Ctx) =
        not GroupAggSuppress.On
        && IncrementalStats.Global.GroupEnabled
        && ctx.Model.Incremental.GroupEnabled
        && isNull ctx.Group
        && not ctx.CallerRows
        && ctx.Model.Computing.Count = 0

    let private entriesOf (state: FilterState) : Dictionary<obj, Entry> =
        match state.GroupAgg with
        | :? Dictionary<obj, Entry> as d -> d
        | _ ->
            let d = Dictionary<obj, Entry>(HashIdentity.Reference)
            state.GroupAgg <- d
            d

    /// The value by group of the aggregation `expr` (kept under `key`) in the row `rc`, with
    /// the fact rows it is over (in check mode, or when worked out); ValueNone to fall back.
    let private lookup
        (ev: IEval)
        (ctx: Ctx)
        (rc: RowCtx)
        (key: obj)
        (expr: Expr)
        (source: SourceText)
        (inner: Ctx -> FilterCtx -> Ctx)
        : struct (Entry * BTable * DaxValue * int array) voption =
        let stats = ctx.Model.Incremental
        let all = IncrementalStats.Global
        let entries = entriesOf ctx.Filter.State

        let entry =
            match entries.TryGetValue key with
            | true, e when obj.ReferenceEquals(e.Table, rc.Table) && obj.ReferenceEquals(e.Source, source) -> e
            | _ ->
                let e =
                    try
                        make ev ctx rc expr source inner
                    with
                    | EngineException _
                    | DaxRuntimeException _ -> unusable rc.Table source

                entries.[key] <- e
                e

        match entry.Kind with
        | EntryKind.Unusable -> ValueNone
        | EntryKind.Usable(fact, agg, kept, buckets, unique) when
            rc.Table.BaseRows.[rc.Row] < entry.Table.BaseTable.RowCount
            ->
            let row = rc.Table.BaseRows.[rc.Row]

            try
                let table = entry.Table.BaseTable

                let rows =
                    if unique then
                        RowSet.One(table.Size, row)
                    else
                        Filters.closeRows table [| row |]

                let first = rows.Rows.[0]
                let checking = all.Check || stats.Check

                let cached =
                    match entry.Values.TryGetValue first with
                    | true, v -> ValueSome v
                    | _ -> ValueNone

                let facts =
                    if cached.IsNone || checking then
                        factRows buckets rows
                    else
                        null

                let value =
                    match cached with
                    | ValueSome v -> v
                    | ValueNone when facts.Length = 0 ->
                        match entry.Empty with
                        | ValueSome v -> v
                        | ValueNone ->
                            let v = valueOver ev ctx inner entry fact agg kept rows facts
                            entry.Empty <- ValueSome v
                            v
                    | ValueNone ->
                        let v = valueOver ev ctx inner entry fact agg kept rows facts
                        entry.Values.[first] <- v
                        v

                Interlocked.Increment(&stats.GroupApplied) |> ignore
                Interlocked.Increment(&all.GroupApplied) |> ignore
                ValueSome(struct (entry, fact, value, facts))
            with
            | EngineException _
            | DaxRuntimeException _ ->
                // The row-by-row evaluation raises what it raises.
                Interlocked.Increment(&stats.GroupErrors) |> ignore
                Interlocked.Increment(&all.GroupErrors) |> ignore
                ValueNone
        | EntryKind.Usable _ -> ValueNone

    let private calculateInner (ctx: Ctx) (fc: FilterCtx) : Ctx = { ctx with Filter = fc; Rows = [] }

    /// `CALCULATE ( <aggregation> )` with no filter argument: by group where that is exact (see the
    /// top of the file), else `slow` (the row-by-row evaluation).
    let calculate (ev: IEval) (ctx: Ctx) (call: Call) (slow: IEval -> Ctx -> Call -> DaxValue) : DaxValue =
        match ctx.Rows with
        | [ rc ] when call.Args.Length = 1 && allowed ctx ->
            let arg = call.Args.[0]

            match lookup ev ctx rc arg arg ctx.Scope.Source calculateInner with
            | ValueNone -> slow ev ctx call
            | ValueSome(struct (_, fact, value, facts)) ->
                if IncrementalStats.Global.Check || ctx.Model.Incremental.Check then
                    check ctx call.Span (fun () -> slow ev ctx call) fact facts value

                value
        | _ -> slow ev ctx call

    /// A reference to a measure whose expression is an aggregation done by group (perhaps inside
    /// CALCULATEs with no filter argument), in a row context: by group where that is exact, else
    /// `slow` (the reference evaluated as written: Eval.fs). `inQuery`: whether the measure is
    /// the query's.
    let measure
        (ev: IEval)
        (ctx: Ctx)
        (span: Span)
        (m: BMeasure)
        (inQuery: bool)
        (slow: Ctx -> Span -> BMeasure -> DaxValue)
        : DaxValue =
        match ctx.Rows with
        | [ rc ] when
            allowed ctx
            && ctx.Model.CalcGroups.Length = 0
            && not (ctx.Measures |> List.exists (fun x -> obj.ReferenceEquals(x, m)))
            ->
            let expr =
                try
                    ValueSome m.Expr
                with EngineException _ ->
                    ValueNone

            // The measure's expression, evaluated as `Eval.evalMeasure` does after its transition.
            let inner (c: Ctx) (fc: FilterCtx) : Ctx = {
                c with
                    Filter = fc
                    Rows = []
                    Scope = {
                        Vars = Map.empty
                        Source = m.Source
                        InQuery = inQuery
                    }
                    Measures = m :: c.Measures
                    Group = null
                    Items = { c.Items with Current = None }
            }

            match expr with
            | ValueNone -> slow ctx span m
            | ValueSome expr ->
                match lookup ev ctx rc m expr m.Source inner with
                | ValueNone -> slow ctx span m
                | ValueSome(struct (_, fact, value, facts)) ->
                    if IncrementalStats.Global.Check || ctx.Model.Incremental.Check then
                        check ctx span (fun () -> slow ctx span m) fact facts value

                    value
        | _ -> slow ctx span m

    // ---- Iterations whose rows have nothing in their buckets ------------------------------------

    /// An iterator's expression that reads nothing but literals, operators other than AND, OR and
    /// IN, columns, and `CALCULATE ( <aggregation> )`, ready to evaluate from values.
    type private Node =
        | Value of DaxValue
        /// The n-th column reference.
        | Column of int
        /// The n-th CALCULATE.
        | Calculate of int
        | Unary of UnaryOp * Span * Node
        /// The span is the right operand's (as the evaluator gives it).
        | Binary of BinaryOp * Span * Node * Node

    /// The columns of a body and the values of its CALCULATEs over no rows: what decides whether
    /// the body is BLANK for every value of its column (see `Body.Blank`).
    [<Sealed>]
    type private BlankKey(columns: BColumn array, values: DaxValue array) =
        member _.Columns = columns
        member _.Values = values

        override _.GetHashCode() =
            let mutable h = 17

            for c in columns do
                h <- h * 31 + RuntimeHelpers.GetHashCode c

            for v in values do
                h <- h * 31 + ValueKey.hash v

            h

        override _.Equals(o: obj) =
            match o with
            | :? BlankKey as k ->
                k.Columns.Length = columns.Length
                && k.Values.Length = values.Length
                && Array.forall2 (fun (a: BColumn) b -> obj.ReferenceEquals(a, b)) k.Columns columns
                && Array.forall2 ValueKey.same k.Values values
            | _ -> false

    type private Body = {
        Root: Node
        Columns: Expr array
        Calculates: Expr array
        /// Whether the body is BLANK for every stored value of its one column, given its
        /// CALCULATEs' values over no rows (worked out once, for every iteration).
        Blank: ConcurrentDictionary<BlankKey, bool>
    }

    let private bodies = ConditionalWeakTable<Expr, Body option>()

    let private bodyOf (e: Expr) : Body option =
        match bodies.TryGetValue e with
        | true, b -> b
        | _ ->
            let columns = ResizeArray<Expr>()
            let calculates = ResizeArray<Expr>()

            let rec walk (e: Expr) : Node option =
                RuntimeHelpers.EnsureSufficientExecutionStack()

                match e.Kind with
                // What the evaluator makes of a literal (Eval.fs).
                | IntegerLiteral(i, _) -> Some(Value(DaxValue.Integer i))
                | NumberLiteral(d, _) -> Some(Value(DaxValue.Decimal d))
                | StringLiteral s -> Some(Value(DaxValue.String s))
                | BooleanLiteral b -> Some(Value(DaxValue.Boolean b))
                | Paren inner -> walk inner
                | ExprKind.Unary(op, operand) -> walk operand |> Option.map (fun n -> Unary(op, e.Span, n))
                | ExprKind.Binary(op, left, right) when op <> BinaryOp.In && op <> BinaryOp.And && op <> BinaryOp.Or ->
                    match walk left, walk right with
                    | Some l, Some r -> Some(Binary(op, right.Span, l, r))
                    | _ -> None
                | Qualified _ ->
                    columns.Add e
                    Some(Column(columns.Count - 1))
                | Call(name, [ arg ]) when name.Name.ToUpperInvariant() = "CALCULATE" && (aggregation arg).IsSome ->
                    calculates.Add arg
                    Some(Calculate(calculates.Count - 1))
                | _ -> None

            let b =
                match walk e with
                | Some root when calculates.Count > 0 ->
                    Some {
                        Root = root
                        Columns = columns.ToArray()
                        Calculates = calculates.ToArray()
                        Blank = ConcurrentDictionary<BlankKey, bool>()
                    }
                | _ -> None

            bodies.AddOrUpdate(e, b)
            b

    /// The expression's value from the values of its columns and CALCULATEs: what the evaluator
    /// computes in a row with those values, in the same order, with the same operators
    /// (Operators.fs).
    let rec private eval (ctx: Ctx) (values: DaxValue array) (calcs: DaxValue array) (n: Node) =
        match n with
        | Value v -> v
        | Column i -> values.[i]
        | Calculate i -> calcs.[i]
        | Unary(op, span, x) -> Operators.unary ctx op span (eval ctx values calcs x)
        | Binary(op, span, l, r) ->
            let a = eval ctx values calcs l
            Operators.binary ctx op span a (eval ctx values calcs r)

    let private tryEval (ctx: Ctx) (values: DaxValue array) (calcs: DaxValue array) (n: Node) =
        try
            ValueSome(eval ctx values calcs n)
        with
        | EngineException _
        | DaxRuntimeException _ -> ValueNone

    /// Whether the body is BLANK for every stored value of its one column (and its CALCULATEs'
    /// values over no rows): then a row with nothing in its buckets is BLANK, whatever its value.
    let private blankEverywhere (ctx: Ctx) (body: Body) (columns: BColumn array) (calcs: DaxValue array) =
        if columns.Length <> 1 then
            false
        else
            body.Blank.GetOrAdd(
                BlankKey(columns, calcs),
                fun _ ->
                    try
                        let c = columns.[0]
                        let data = c.Data
                        let values = [| DaxValue.Blank |]
                        let mutable blank = true
                        let mutable id = 0

                        while blank && id < data.DistinctCount do
                            values.[0] <- data.Values.[id]

                            match eval ctx values calcs body.Root with
                            | DaxValue.Blank -> ()
                            | _ -> blank <- false

                            id <- id + 1

                        blank
                    with
                    | EngineException _
                    | DaxRuntimeException _ -> false
            )

    let private checkRow (ctx: Ctx) (real: int -> DaxValue) (t: BTable) (expr: Expr) (row: int) (value: DaxValue) =
        let stats = ctx.Model.Incremental
        let all = IncrementalStats.Global
        GroupAggSuppress.On <- true

        let expected =
            try
                try
                    Ok(real row)
                with ex ->
                    Error ex.Message
            finally
                GroupAggSuppress.On <- false

        Interlocked.Increment(&stats.GroupChecked) |> ignore
        Interlocked.Increment(&all.GroupChecked) |> ignore

        match expected with
        | Ok x when ValueKey.same x value -> ()
        | _ ->
            Interlocked.Increment(&stats.GroupMismatched) |> ignore
            Interlocked.Increment(&all.GroupMismatched) |> ignore

            raise (
                InvalidOperationException(
                    $"the value of row {row} of an iteration over '{t.Name}' at {Fail.where ctx expr.Span}, evaluated from its buckets, differs from the row-by-row evaluation: %A{value}, row by row %A{expected}"
                )
            )

    /// The rows of an iteration, evaluated in order, from their buckets where they have nothing in
    /// them (see `iterate`).
    [<Sealed>]
    type private Rows
        (ev: IEval, ctx: Ctx, table: TableVal, expr: Expr, body: Body, columns: BColumn array, skipBlank: bool) =
        let stats = ctx.Model.Incremental
        let all = IncrementalStats.Global
        let checking = all.Check || stats.Check
        let t = table.BaseTable
        let mutable row = -1
        let mutable current = DaxValue.Blank
        // The CALCULATEs' entries, once each has been made (by a row evaluated in full) and has
        // its value over no rows.
        let mutable entries: Entry array = null
        let mutable empty: DaxValue array = null
        let mutable off = isNull columns
        // Every row with nothing in its buckets is BLANK (for an iterator that skips BLANKs).
        let mutable blank = false

        let values =
            if isNull columns then
                [||]
            else
                Array.zeroCreate<DaxValue> columns.Length

        let real (row: int) =
            ev.Scalar(Context.pushRow ctx table row, expr)

        let fetch () =
            match ctx.Filter.State.GroupAgg with
            | :? Dictionary<obj, Entry> as d ->
                let found =
                    body.Calculates
                    |> Array.map (fun a ->
                        match d.TryGetValue a with
                        | true, e when obj.ReferenceEquals(e.Table, table) -> e
                        | _ -> null)

                if found |> Array.forall (fun e -> not (isNull e)) then
                    if
                        found
                        |> Array.forall (fun e ->
                            match e.Kind with
                            | EntryKind.Usable(_, _, _, _, unique) -> unique
                            | EntryKind.Unusable -> false)
                    then
                        if found |> Array.forall (fun e -> e.Empty.IsSome) then
                            entries <- found
                            empty <- found |> Array.choose (fun e -> Option.ofValueOption e.Empty)
                            blank <- skipBlank && blankEverywhere ctx body columns empty
                    else
                        off <- true
            | _ -> ()

        /// Whether no CALCULATE has anything in its bucket for this row of T.
        let nothing (r: int) =
            if r >= t.RowCount then
                false
            else
                let mutable none = true
                let mutable i = 0

                while none && i < entries.Length do
                    match entries.[i].Kind with
                    | EntryKind.Usable(_, _, _, buckets, _) -> none <- not (buckets.ContainsKey r)
                    | EntryKind.Unusable -> none <- false

                    i <- i + 1

                none

        let counted () =
            Interlocked.Increment(&stats.GroupRows) |> ignore
            Interlocked.Increment(&all.GroupRows) |> ignore

        member _.MoveNext() : bool =
            let mutable found = false

            while not found && row < table.Count - 1 do
                row <- row + 1

                if off then
                    current <- real row
                    found <- true
                elif isNull entries then
                    current <- real row
                    fetch ()
                    found <- true
                else
                    let r = table.BaseRows.[row]

                    if not (nothing r) then
                        current <- real row
                        found <- true
                    elif blank then
                        // BLANK: the iterator skips it.
                        counted ()

                        if checking then
                            checkRow ctx real t expr row DaxValue.Blank
                    else
                        for i in 0 .. columns.Length - 1 do
                            values.[i] <- columns.[i].ValueAt r

                        match tryEval ctx values empty body.Root with
                        | ValueNone ->
                            // The evaluator raises what it raises.
                            current <- real row
                        | ValueSome v ->
                            counted ()

                            if checking then
                                checkRow ctx real t expr row v

                            current <- v

                        found <- true

            found

        member _.Current = current

        interface IEnumerator<DaxValue> with
            member this.Current: DaxValue = this.Current
            member this.Current: obj = box this.Current
            member this.MoveNext() = this.MoveNext()
            member _.Reset() = raise (NotSupportedException())
            member _.Dispose() = ()

    /// The columns a body reads, resolved as the evaluator resolves them: columns of the table
    /// iterated, read from the row (`Context.tryColumnValue`: the table has all of its columns).
    /// Null if one is anything else.
    let private columnsOf (ev: IEval) (ctx: Ctx) (t: BTable) (body: Body) : BColumn array =
        try
            let cols = body.Columns |> Array.map (fun e -> ev.TryColumn(ctx, e))

            if
                cols
                |> Array.forall (fun c -> c.IsSome && obj.ReferenceEquals(c.Value.Table, t))
            then
                cols |> Array.map Option.get
            else
                null
        with
        | EngineException _
        | DaxRuntimeException _ -> null

    /// The values of `expr` in each row of `table`, in order, as an iterator evaluates them. In a
    /// row whose CALCULATEs all have nothing in their buckets, the expression is evaluated from
    /// their value over no rows and the row's columns, without the evaluator; with `skipBlank`
    /// (for an iterator that skips BLANKs, as SUMX does) such rows are left out where the
    /// expression is BLANK for every value of its column (see "Aggregating by group" in the
    /// author's guide).
    let iterate (ev: IEval) (ctx: Ctx) (table: TableVal) (expr: Expr) (skipBlank: bool) : DaxValue seq =
        let stats = ctx.Model.Incremental
        let all = IncrementalStats.Global

        let body =
            if
                all.GroupEnabled
                && stats.GroupEnabled
                && not GroupAggSuppress.On
                && ctx.Rows.IsEmpty
                && isNull ctx.Group
                && not ctx.CallerRows
                && ctx.Model.Computing.Count = 0
                && table.Count > 1
                && table.IsPureBase
            then
                bodyOf expr
            else
                None

        match body with
        | None ->
            seq {
                for row in 0 .. table.Count - 1 do
                    yield ev.Scalar(Context.pushRow ctx table row, expr)
            }
        | Some body -> {
            new IEnumerable<DaxValue> with
                member _.GetEnumerator() : IEnumerator<DaxValue> =
                    new Rows(ev, ctx, table, expr, body, columnsOf ev ctx table.BaseTable body, skipBlank)

                member _.GetEnumerator() : Collections.IEnumerator =
                    new Rows(ev, ctx, table, expr, body, columnsOf ev ctx table.BaseTable body, skipBlank)
          }
