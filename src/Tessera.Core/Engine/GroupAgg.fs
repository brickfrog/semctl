namespace Tessera.DaxEngine

// Aggregating by group: `CALCULATE ( SUM ( F[x] ) )` evaluated in a row context of a model table
// T, row after row of an iteration, looks its value up in buckets of F's rows instead of making a
// filter context per row. See "Aggregating by group" in the author's guide (Types.fs) for why the
// value is the same and where it falls back to the row-by-row evaluation.

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Threading
open Tessera
open Tessera.DaxAst

/// Set while check mode works a value out row by row (per thread).
[<Sealed>]
type internal GroupAggSuppress private () =
    [<ThreadStatic; DefaultValue>]
    static val mutable private on: bool

    static member On
        with get () = GroupAggSuppress.on
        and set (v: bool) = GroupAggSuppress.on <- v

module GroupAgg =

    /// What one iteration's CALCULATE needs, made on its first row: the rows of F under the
    /// filters the transition keeps, by the row of T they belong to, and the values worked out.
    [<Sealed; AllowNullLiteral>]
    type private Entry
        (
            table: TableVal,
            source: SourceText,
            usable: bool,
            fact: BTable,
            kept: FilterCtx,
            buckets: Dictionary<int, ResizeArray<int>>,
            unique: bool
        ) =
        member _.Table = table
        member _.Source = source
        member _.Usable = usable
        member _.Fact = fact
        member _.Kept = kept
        member _.Buckets = buckets
        /// Whether T has a key (`BTable.UniqueColumn`): a row is then its own row set.
        member _.Unique = unique
        /// By the first row of the row set: the value (for a row set with fact rows).
        member val Values = Dictionary<int, DaxValue>() with get
        /// The value over no fact rows, once worked out.
        member val Empty = ValueNone with get, set

    let private aggregations = [ "SUM" ]

    /// The aggregation a CALCULATE argument is, if it is one this does: `SUM ( Table[Column] )`.
    let private aggregation (e: Expr) : (Expr * Expr) option =
        let e = Fn.unparen e

        match e.Kind with
        | Call(name, [ arg ]) when aggregations |> List.contains (name.Name.ToUpperInvariant()) ->
            match (Fn.unparen arg).Kind with
            | Qualified _ -> Some(e, arg)
            | _ -> None
        | _ -> None

    let private unusable (table: TableVal) (source: SourceText) =
        Entry(table, source, false, null, null, null, false)

    /// The entry for the iteration `rc` is a row of, or one that says to fall back.
    let private make (ev: IEval) (ctx: Ctx) (rc: RowCtx) (arg: Expr) : Entry =
        let model = ctx.Model
        let fc = ctx.Filter
        let cfg = fc.Config
        let t = rc.Table
        let no () = unusable t ctx.Scope.Source

        match aggregation arg with
        | None -> no ()
        | Some(_, column) ->
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
                    let c = ev.Column({ ctx with Rows = [] }, column)
                    let fact = c.Table
                    let shared = VisCache.Of cfg

                    if
                        not (obj.ReferenceEquals(fact, table) || cfg.IsExpanded(fact, table))
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

                            Entry(t, ctx.Scope.Source, true, fact, kept, buckets, table.UniqueColumn.IsSome)

    /// The fact rows of a row set of T, ascending.
    let private factRows (e: Entry) (rows: RowSet) : int array =
        if rows.Count = 1 then
            match e.Buckets.TryGetValue rows.Rows.[0] with
            | true, list -> list.ToArray()
            | _ -> [||]
        else
            let all = ResizeArray<int>()

            for r in rows.Rows do
                match e.Buckets.TryGetValue r with
                | true, list -> all.AddRange list
                | _ -> ()

            let a = all.ToArray()
            Array.sortInPlace a
            a

    /// The aggregation's value over these fact rows: the registered function, evaluated in a
    /// context with the row's filters whose rows of F are these.
    let private valueOver (ev: IEval) (ctx: Ctx) (e: Entry) (agg: Expr) (rows: RowSet) (facts: int array) =
        let cfg = ctx.Filter.Config
        let table = e.Table.BaseTable
        let fc = FilterCtx(e.Kept.Filters @ [ Rows(table, rows, cfg) ], cfg)
        let fact = e.Fact
        fc.Visible.[fact.Id * (fact.Model.Rels.Length + 1)] <- RowSet.OfSorted(fact.Size, facts)
        ev.Scalar({ ctx with Filter = fc; Rows = [] }, agg)

    let private check
        (ev: IEval)
        (ctx: Ctx)
        (call: Call)
        (slow: IEval -> Ctx -> Call -> DaxValue)
        (e: Entry)
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
                        Ok(slow ev ctx call)
                    with ex ->
                        Error ex.Message

                let rows =
                    try
                        let moved = Context.transition ctx call.Span
                        Some(Filters.visibleStored moved.Filter e.Fact)
                    with _ ->
                        None

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
                    $"the value of CALCULATE by group at {Fail.where ctx call.Span} differs from the row-by-row evaluation: %A{value} over {facts.Length} rows of '{e.Fact.Name}', row by row {says} over {rows |> Option.map _.Length} rows"
                )
            )

    /// `CALCULATE ( <aggregation> )` with no filter argument: by group where that is exact (see the
    /// top of the file), else `slow` (the row-by-row evaluation).
    let calculate (ev: IEval) (ctx: Ctx) (call: Call) (slow: IEval -> Ctx -> Call -> DaxValue) : DaxValue =
        let stats = ctx.Model.Incremental
        let all = IncrementalStats.Global

        match ctx.Rows with
        | [ rc ] when
            not GroupAggSuppress.On
            && all.GroupEnabled
            && stats.GroupEnabled
            && isNull ctx.Group
            && not ctx.CallerRows
            && call.Args.Length = 1
            ->
            let state = ctx.Filter.State

            let entries =
                match state.GroupAgg with
                | :? Dictionary<Expr, Entry> as d -> d
                | _ ->
                    let d = Dictionary<Expr, Entry>(HashIdentity.Reference)
                    state.GroupAgg <- d
                    d

            let arg = call.Args.Head

            let entry =
                match entries.TryGetValue arg with
                | true, e when
                    obj.ReferenceEquals(e.Table, rc.Table)
                    && obj.ReferenceEquals(e.Source, ctx.Scope.Source)
                    ->
                    e
                | _ ->
                    let e =
                        try
                            make ev ctx rc arg
                        with _ ->
                            unusable rc.Table ctx.Scope.Source

                    entries.[arg] <- e
                    e

            if not entry.Usable || rc.Table.BaseRows.[rc.Row] >= entry.Table.BaseTable.RowCount then
                slow ev ctx call
            else
                let row = rc.Table.BaseRows.[rc.Row]

                let attempt =
                    try
                        let table = entry.Table.BaseTable

                        let rows =
                            if entry.Unique then
                                RowSet.One(table.Size, row)
                            else
                                Filters.closeRows table [| row |]

                        let first = rows.Rows.[0]
                        let agg = Fn.unparen arg
                        let checking = all.Check || stats.Check

                        let cached =
                            match entry.Values.TryGetValue first with
                            | true, v -> ValueSome v
                            | _ -> ValueNone

                        let facts =
                            if cached.IsNone || checking then
                                factRows entry rows
                            else
                                null

                        let value =
                            match cached with
                            | ValueSome v -> v
                            | ValueNone when facts.Length = 0 ->
                                match entry.Empty with
                                | ValueSome v -> v
                                | ValueNone ->
                                    let v = valueOver ev ctx entry agg rows facts
                                    entry.Empty <- ValueSome v
                                    v
                            | ValueNone ->
                                let v = valueOver ev ctx entry agg rows facts
                                entry.Values.[first] <- v
                                v

                        ValueSome(value, facts)
                    with _ ->
                        Interlocked.Increment(&stats.GroupErrors) |> ignore
                        Interlocked.Increment(&all.GroupErrors) |> ignore
                        ValueNone

                match attempt with
                | ValueNone -> slow ev ctx call
                | ValueSome(value, facts) ->
                    Interlocked.Increment(&stats.GroupApplied) |> ignore
                    Interlocked.Increment(&all.GroupApplied) |> ignore

                    if all.Check || stats.Check then
                        check ev ctx call slow entry facts value

                    value
        | _ -> slow ev ctx call

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
        with _ ->
            ValueNone

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
                    with _ ->
                        false
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
            | :? Dictionary<Expr, Entry> as d ->
                let found =
                    body.Calculates
                    |> Array.map (fun a ->
                        match d.TryGetValue a with
                        | true, e when obj.ReferenceEquals(e.Table, table) -> e
                        | _ -> null)

                if found |> Array.forall (fun e -> not (isNull e)) then
                    if found |> Array.forall (fun e -> e.Usable && e.Unique) then
                        if found |> Array.forall (fun e -> e.Empty.IsSome) then
                            entries <- found
                            empty <- found |> Array.map (fun e -> e.Empty.Value)
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
                    none <- not (entries.[i].Buckets.ContainsKey r)
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
        with _ ->
            null

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
