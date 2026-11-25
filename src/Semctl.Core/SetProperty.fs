/// Reading and writing an object's properties by the paths `get -p` prints (TOM's JSON names): the
/// one descriptor behind `sm set` and `sm get --properties`, so the two can't disagree.
///
/// The descriptor is built by reflection over the object's runtime TOM type. Scalars (strings,
/// booleans, integers, numbers, enums), references to other addressable objects (`sortByColumn`),
/// annotations and extended properties are properties; sub-objects (`kpi`, `formatStringDefinition`,
/// `source`, ...) are walked into, and created when a nested path is set. Child collections
/// (`columns`, `measures`, ...) are not properties: `add`/`remove`/`move` edit those.
module Semctl.SetProperty

open System
open System.Collections
open System.Collections.Generic
open System.ComponentModel
open System.Globalization
open System.Reflection
open Microsoft.AnalysisServices.Tabular
open Semctl.Inventory
open Semctl.PropertyCatalog

/// A property's current value.
[<RequireQualifiedAccess>]
type PropertyValue =
    | Null
    | String of string
    | Bool of bool
    | Int of int64
    | Number of float
    | Reference of ObjectRef
    /// A sub-object that exists.
    | Present

/// One property of one object, as `get --properties` lists it.
type Descriptor = {
    /// The path, as `get -p` and `set -p` take it: "formatString", "kpi.targetExpression",
    /// "annotations.PBI_FormatHint". `annotations.<name>` stands for any new annotation.
    Path: string
    Type: PropertyType
    Writable: bool
    /// Why it can't be written (read-only properties only).
    Reason: string option
    Value: PropertyValue
    /// Enum members valid at the model's compatibility level, as TOM's JSON writes them.
    Allowed: string list
    /// For references: the kind of object it points at.
    RefersTo: ObjectKind option
}

/// A value to assign.
[<RequireQualifiedAccess>]
type Value =
    /// Clear a string or reference, reset anything else to TOM's default, remove a sub-object,
    /// annotation or extended property.
    | Null
    /// Text, read according to the property's type. Never taken to mean null.
    | Text of string

type Assignment = { Path: string; Value: Value }

module Assignment =

    /// `path=value`. A value of `null` clears; `-` reads the value from `stdin` (taken literally,
    /// minus one trailing line break).
    let parse (stdin: unit -> string) (text: string) : Result<Assignment, string> =
        match text.IndexOf '=' with
        | -1 -> Error $"expected <path>=<value>, got '{text}'"
        | 0 -> Error $"missing property path in '{text}'"
        | i ->
            let path = text.Substring(0, i).Trim()
            let value = text.Substring(i + 1)

            let value =
                match value with
                | "null" -> Value.Null
                | "-" ->
                    let s = stdin ()

                    let s =
                        if s.EndsWith "\r\n" then s.Substring(0, s.Length - 2)
                        elif s.EndsWith "\n" then s.Substring(0, s.Length - 1)
                        else s

                    Value.Text s
                | v -> Value.Text v

            Ok { Path = path; Value = value }

    /// Clear or reset a property (`--unset path`).
    let unset (path: string) = {
        Path = path.Trim()
        Value = Value.Null
    }


/// A sub-object that isn't there, or (for property bags TOM always returns) is empty: either way,
/// TOM's JSON leaves it out.
let private absent (o: obj) =
    match o with
    | null -> true
    | o ->
        match o.GetType().GetProperty("IsEmpty", typeof<bool>) with
        | null -> false
        | p -> p.GetValue o :?> bool

let private unwrap (e: exn) =
    match e with
    | :? TargetInvocationException as t when not (isNull t.InnerException) -> t.InnerException
    | e -> e

// ---------------------------------------------------------------------------------------------
// The descriptor

type private Write = unit -> Result<unit, SemctlError>

type private Entry = {
    Info: Descriptor
    /// Check and resolve a value; the result writes it.
    Prepare: Value -> Result<Write, SemctlError>
}

/// Paths that exist but aren't properties (child collections, sub-objects that can't be created).
type private Refusal = { Path: string; Reason: string }

/// Where new named items (annotations, extended properties) can go: "annotations." -> make one.
type private Template = {
    Prefix: string
    Make: string -> Entry
}

type private Catalog = {
    Entries: Entry list
    Refusals: Refusal list
    Templates: Template list
}

/// A (sub-)object being described: how to read it, and how to make it exist.
type private Node = {
    Path: string
    Type: Type
    Current: unit -> obj
    Ensure: unit -> obj
}

type private Ctx = {
    Db: Database
    Ref: string
    Level: int
    Mode: Microsoft.AnalysisServices.CompatibilityMode
    Objects: Lazy<ObjectEntry list>
    /// The object's properties as `get -p` prints them.
    Flat: Lazy<(string * string) list>
}

let private join (prefix: string) (name: string) =
    if prefix = "" then name else prefix + "." + name

let private valueText =
    function
    | Value.Null -> "null"
    | Value.Text s -> s

let private invalid (ctx: Ctx) path value reason =
    Error(InvalidPropertyValue(ctx.Ref, path, valueText value, reason))

/// Run a write, reporting TOM's exceptions (e.g. a value the compatibility level doesn't allow).
let private guarded (ctx: Ctx) (path: string) (value: Value) (write: unit -> unit) : Write =
    fun () ->
        try
            write ()
            Ok()
        with e ->
            Error(InvalidPropertyValue(ctx.Ref, path, valueText value, (unwrap e).Message))

let private enumMembers (ctx: Ctx) (t: Type) = enumMembers ctx.Level ctx.Mode t

let private parseScalar (ctx: Ctx) (path: string) (t: Type) (pt: PropertyType) (text: string) =
    let fail reason =
        invalid ctx path (Value.Text text) reason

    let s = text.Trim()

    match pt with
    | PropertyType.String
    | PropertyType.Expression -> Ok(box text)
    | PropertyType.Bool ->
        match s.ToLowerInvariant() with
        | "true" -> Ok(box true)
        | "false" -> Ok(box false)
        | _ -> fail "expected true or false"
    | PropertyType.Int when t = typeof<int> ->
        match Int32.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
        | true, n -> Ok(box n)
        | _ -> fail "expected a whole number (32-bit)"
    | PropertyType.Int ->
        match Int64.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
        | true, n -> Ok(box n)
        | _ -> fail "expected a whole number"
    | PropertyType.Number ->
        match Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, n when Double.IsFinite n -> Ok(box n)
        | _ -> fail "expected a number"
    | PropertyType.Enum ->
        let members = enumMembers ctx t

        let allowed =
            members
            |> List.filter (fun (_, c) -> c = Supported)
            |> List.map (fun (f, _) -> enumJsonName f.Name)
            |> String.concat ", "

        match
            members
            |> List.tryFind (fun (f, _) -> String.Equals(f.Name, s, StringComparison.OrdinalIgnoreCase))
        with
        | Some(f, Supported) -> Ok(f.GetValue null)
        | Some(f, Needs why) -> fail $"{enumJsonName f.Name} {why}"
        | _ ->
            let names =
                members
                |> List.filter (fun (_, c) -> c = Supported)
                |> List.map (fun (f, _) -> enumJsonName f.Name)

            fail $"expected one of: {allowed}{Suggest.hint (Suggest.closest names s)}"
    | PropertyType.Reference
    | PropertyType.Object -> fail "not a scalar"

let private scalarValue (pt: PropertyType) (v: obj) =
    match v with
    | null -> PropertyValue.Null
    // TOM reads an unset string as "", and its JSON leaves it out, as it does null.
    | :? string as s when s = "" -> PropertyValue.Null
    | v ->
        match pt, v with
        | PropertyType.Bool, (:? bool as b) -> PropertyValue.Bool b
        | PropertyType.Int, (:? int as n) -> PropertyValue.Int(int64 n)
        | PropertyType.Int, (:? int64 as n) -> PropertyValue.Int n
        | PropertyType.Number, (:? float as n) -> PropertyValue.Number n
        | PropertyType.Enum, v -> PropertyValue.String(enumJsonName (string v))
        | _, v -> PropertyValue.String(string v)

let private refOf (ctx: Ctx) (o: obj) =
    match o with
    | null -> PropertyValue.Null
    | o ->
        match ctx.Objects.Value |> List.tryFind (fun e -> obj.ReferenceEquals(e.Object, o)) with
        | Some e -> PropertyValue.Reference e.Ref
        | None ->
            match o with
            | :? NamedMetadataObject as n -> PropertyValue.String n.Name
            | _ -> PropertyValue.Null

/// The table a plain column name is looked up in first: the object's own (or the relationship end's).
let private localTable (owner: obj) (prop: string) : Table option =
    match owner with
    | :? Column as c -> Option.ofObj c.Table
    | :? Level as l when not (isNull l.Hierarchy) -> Option.ofObj l.Hierarchy.Table
    | :? SingleColumnRelationship as r when prop = "FromColumn" -> Option.ofObj r.FromTable
    | :? SingleColumnRelationship as r when prop = "ToColumn" -> Option.ofObj r.ToTable
    | :? AlternateOf as a when not (isNull a.BaseTable) -> Some a.BaseTable
    | _ -> None

let private resolveReference (ctx: Ctx) (path: string) (kind: ObjectKind) (local: Table option) (text: string) =
    let all = ctx.Objects.Value
    let ofKind = all |> List.filter (fun e -> e.Ref.Kind = kind)

    let inLocal =
        match local, Selector.parse text with
        | Some t, Ok(Bare _ | Member _) ->
            match ofKind |> List.filter (fun e -> ObjectEntry.table e = Some t.Name) with
            | [] -> None
            | own ->
                match Selector.resolve own text with
                | Ok e -> Some e
                | Error _ -> None
        | _ -> None

    match inLocal with
    | Some e -> Ok e
    | None ->
        match Selector.resolve ofKind text with
        | Ok e -> Ok e
        | Error((ObjectNotFound _ | ObjectNotFoundSuggest _) as notFound) ->
            match Selector.resolve all text with
            | Ok other ->
                invalid
                    ctx
                    path
                    (Value.Text text)
                    $"that is {ObjectRef.format other.Ref}, not a {ObjectKind.token kind}"
            | Error _ -> Error notFound
        | Error e -> Error e

/// Whether a sub-object holds nothing: every writable property at its default, no named items.
let rec private isEmpty (o: obj) =
    let t = o.GetType()

    members t
    |> Array.forall (fun m ->
        let p = m.Info

        match m.Kind with
        | Scalar _ when isSettable p -> obj.Equals(p.GetValue o, defaultOf t p)
        | Ref _ -> isNull (p.GetValue o)
        | NamedItems _ ->
            match p.GetValue o with
            | :? IEnumerable as items -> not (items.GetEnumerator().MoveNext())
            | _ -> true
        | Sub when isSettable p ->
            match p.GetValue o with
            | null -> true
            | sub -> isEmpty sub
        | _ -> true)

type private NamedItem = { Name: string; Item: obj }

let private namedItems (coll: obj) =
    match coll with
    | :? IEnumerable as items -> [
        for item in items do
            match item with
            | :? Annotation as a -> { Name = a.Name; Item = a }
            | :? ExtendedProperty as x -> { Name = x.Name; Item = x }
            | _ -> ()
      ]
    | _ -> []

let private callWith (coll: obj) (methodName: string) (arg: obj) =
    let m =
        coll.GetType().GetMethods()
        |> Array.find (fun m ->
            m.Name = methodName
            && (let ps = m.GetParameters()
                ps.Length = 1 && ps.[0].ParameterType.IsAssignableFrom(arg.GetType())))

    m.Invoke(coll, [| arg |]) |> ignore

let private namedValue (item: obj) =
    match item with
    | :? Annotation as a -> a.Value
    | :? StringExtendedProperty as x -> x.Value
    | :? JsonExtendedProperty as x -> x.Value
    | _ -> null

/// An entry for one named item (existing or new) in a collection property of `node`.
let private namedEntry (ctx: Ctx) (node: Node) (p: PropertyInfo) (named: Named) (writable: Compat) (name: string) =
    let segment = jsonName p.Name
    let path = join (join node.Path segment) name

    let find () =
        match node.Current() with
        | null -> None
        | o ->
            let items = namedItems (p.GetValue o)

            items
            |> List.tryFind (fun i -> i.Name = name)
            |> Option.orElse (
                items
                |> List.tryFind (fun i -> String.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
            )

    let current = find ()

    {
        Info = {
            Path = path
            Type = PropertyType.String
            Writable = (writable = Supported)
            Reason =
                match writable with
                | Needs why -> Some why
                | _ -> None
            Value =
                match current with
                | Some i -> PropertyValue.String(namedValue i.Item)
                | None -> PropertyValue.Null
            Allowed = []
            RefersTo = None
        }
        Prepare =
            fun value ->
                match value with
                | Value.Null ->
                    Ok(
                        guarded ctx path value (fun () ->
                            match find (), node.Current() with
                            | Some i, o when not (isNull o) -> callWith (p.GetValue o) "Remove" i.Item
                            | _ -> ())
                    )
                | Value.Text text ->
                    let isJson =
                        match current with
                        | Some { Item = :? JsonExtendedProperty } -> true
                        | _ -> false

                    let jsonOk =
                        not isJson
                        || (try
                                use _ = Text.Json.JsonDocument.Parse text
                                true
                            with _ ->
                                false)

                    if not jsonOk then
                        invalid ctx path value "this is a JSON extended property; expected JSON"
                    else
                        Ok(
                            guarded ctx path value (fun () ->
                                match find () with
                                | Some { Item = :? Annotation as a } -> a.Value <- text
                                | Some {
                                           Item = :? StringExtendedProperty as x
                                       } -> x.Value <- text
                                | Some { Item = :? JsonExtendedProperty as x } -> x.Value <- text
                                | _ ->
                                    let coll = p.GetValue(node.Ensure())

                                    let item: obj =
                                        match named with
                                        | Annotations -> Annotation(Name = name, Value = text)
                                        | ExtendedProperties -> StringExtendedProperty(Name = name, Value = text)

                                    callWith coll "Add" item)
                        )
    }

let private readOnly (info: Descriptor) reason = {
    Info = {
        info with
            Writable = false
            Reason = Some reason
    }
    Prepare = fun _ -> Error(ReadOnlyProperty("", info.Path, reason))
}

let rec private describe (ctx: Ctx) (depth: int) (node: Node) : Catalog =
    let current = node.Current()

    let parts =
        members node.Type
        |> Array.map (fun m ->
            let p = m.Info
            let path = join node.Path m.Segment
            let compat = compatibility ctx.Level ctx.Mode p

            let info pt value = {
                Path = path
                Type = pt
                Writable = true
                Reason = None
                Value = value
                Allowed = []
                RefersTo = None
            }

            let restrict (entry: Entry) =
                match compat with
                | Needs why when entry.Info.Writable -> readOnly entry.Info why
                | _ -> entry

            let read () =
                match current with
                | null -> null
                | o -> p.GetValue o

            let single entry = {
                Entries = [ restrict entry ]
                Refusals = []
                Templates = []
            }

            match compat, m.Kind with
            | Hidden, _
            | _, Skip -> {
                Entries = []
                Refusals = []
                Templates = []
              }
            | _, Children -> {
                Entries = []
                Refusals = [
                    {
                        Path = path
                        Reason = "it holds child objects; point at the child itself (`sm list` shows them)"
                    }
                ]
                Templates = []
              }
            | _, Scalar pt ->
                let value =
                    if secretNames.Contains p.Name then
                        PropertyValue.Null
                    elif maskedNames.Contains p.Name then
                        match ctx.Flat.Value |> List.tryFind (fun (k, _) -> k = path) with
                        | Some(_, v) -> PropertyValue.String v
                        | None -> PropertyValue.Null
                    else
                        scalarValue pt (read ())

                let i = {
                    info pt value with
                        Allowed =
                            if pt = PropertyType.Enum then
                                enumMembers ctx p.PropertyType
                                |> List.filter (fun (_, c) -> c = Supported)
                                |> List.map (fun (f, _) -> enumJsonName f.Name)
                            else
                                []
                }

                if p.Name = "Name" && depth = 0 then
                    let reason =
                        match current with
                        | :? Model -> "sm doesn't rename the model"
                        | _ -> "use `sm rename`, which also updates the DAX that refers to it"

                    single (readOnly i reason)
                elif secretNames.Contains p.Name then
                    single (readOnly i "a secret: TOM's JSON masks it, so sm can't show or diff a change")
                elif not (isSettable p) then
                    single (readOnly i "read-only in TOM")
                else
                    single {
                        Info = i
                        Prepare =
                            fun value ->
                                match value with
                                | Value.Null ->
                                    let d = defaultOf node.Type p

                                    Ok(
                                        guarded ctx path value (fun () ->
                                            match node.Current() with
                                            | null -> ()
                                            | o -> p.SetValue(o, d))
                                    )
                                | Value.Text text ->
                                    parseScalar ctx path p.PropertyType pt text
                                    |> Result.map (fun v ->
                                        guarded ctx path value (fun () -> p.SetValue(node.Ensure(), v)))
                    }
            | _, Ref kind ->
                let i = {
                    info PropertyType.Reference (refOf ctx (read ())) with
                        RefersTo = Some kind
                }

                single {
                    Info = i
                    Prepare =
                        fun value ->
                            match value with
                            | Value.Null ->
                                Ok(
                                    guarded ctx path value (fun () ->
                                        match node.Current() with
                                        | null -> ()
                                        | o -> p.SetValue(o, null))
                                )
                            | Value.Text text when text.Trim() = "" ->
                                invalid ctx path value $"expected a {ObjectKind.token kind} (use null to clear)"
                            | Value.Text text ->
                                let local =
                                    match node.Current() with
                                    | null -> None
                                    | o -> localTable o p.Name

                                let ownTableOnly =
                                    match node.Current() with
                                    | :? Column when p.Name = "SortByColumn" -> true
                                    | :? Level when p.Name = "Column" -> true
                                    | _ -> false

                                resolveReference ctx path kind local text
                                |> Result.bind (fun target ->
                                    match local, target.Object with
                                    | Some t, (:? Column as c) when
                                        ownTableOnly && not (obj.ReferenceEquals(c.Table, t))
                                        ->
                                        invalid ctx path value $"it must be a column of the same table ({t.Name})"
                                    | _ -> Ok target)
                                |> Result.map (fun target ->
                                    guarded ctx path value (fun () -> p.SetValue(node.Ensure(), target.Object)))
                }
            | _, PropertyBag -> {
                Entries = []
                Refusals = [
                    {
                        Path = path
                        Reason = "it's Power Query connection data; `set` doesn't edit it (edit the model's M/TMDL)"
                    }
                ]
                Templates = []
              }
            | _, Unaddressable what ->
                single (
                    readOnly
                        (info PropertyType.Reference (refOf ctx (read ())))
                        $"it refers to one of the model's {what}, which sm can't address yet"
                )
            | _, NamedItems named ->
                let existing =
                    match current with
                    | null -> []
                    | o -> namedItems (p.GetValue o) |> List.map (fun i -> i.Name)

                let make = namedEntry ctx node p named compat

                {
                    Entries = (make "<name>" :: List.map make existing)
                    Refusals = []
                    Templates = [
                        {
                            Prefix = join node.Path (jsonName p.Name) + "."
                            Make = make
                        }
                    ]
                }
            | _, Sub ->
                let value = read ()
                let canCreate = creatable p
                let canRemove = isSettable p && not (structural.Contains p.Name)

                let self =
                    let i =
                        info
                            PropertyType.Object
                            (if absent value then
                                 PropertyValue.Null
                             else
                                 PropertyValue.Present)

                    if not canRemove then
                        readOnly
                            i
                            (if structural.Contains p.Name then
                                 "it can't be removed or replaced by `set`; set the properties under it"
                             else
                                 "read-only in TOM")
                    else
                        {
                            Info = i
                            Prepare =
                                fun v ->
                                    match v with
                                    | Value.Null ->
                                        Ok(
                                            guarded ctx path v (fun () ->
                                                match node.Current() with
                                                | null -> ()
                                                | o -> p.SetValue(o, null))
                                        )
                                    | Value.Text _ ->
                                        invalid
                                            ctx
                                            path
                                            v
                                            $"it's an object: set the properties under it ({path}.<property>), or null to remove it"
                        }

                if depth >= 3 then
                    single self
                elif isNull value && not canCreate then
                    // Nothing to describe, and nothing `set` can make.
                    {
                        Entries = []
                        Refusals = [
                            {
                                Path = path
                                Reason =
                                    if compat = Supported then
                                        $"there is no {path}, and `set` can't create one"
                                    else
                                        match compat with
                                        | Needs why -> why
                                        | _ -> "not supported"
                            }
                        ]
                        Templates = []
                    }
                else
                    let child = {
                        Path = path
                        Type = (if isNull value then p.PropertyType else value.GetType())
                        Current =
                            fun () ->
                                match node.Current() with
                                | null -> null
                                | o -> p.GetValue o
                        Ensure =
                            fun () ->
                                let o = node.Ensure()

                                match p.GetValue o with
                                | null ->
                                    let created = Activator.CreateInstance p.PropertyType
                                    p.SetValue(o, created)
                                    created
                                | existing -> existing
                    }

                    let inner = describe ctx (depth + 1) child

                    let inner =
                        match compat with
                        | Needs why -> {
                            inner with
                                Entries =
                                    inner.Entries
                                    |> List.map (fun e -> if e.Info.Writable then readOnly e.Info why else e)
                                Templates = []
                          }
                        | _ -> inner

                    // Unsetting the last property of a sub-object removes the sub-object.
                    let tidy (e: Entry) =
                        if not canRemove || not canCreate then
                            e
                        else
                            {
                                e with
                                    Prepare =
                                        fun v ->
                                            e.Prepare v
                                            |> Result.map (fun write ->
                                                fun () ->
                                                    write ()
                                                    |> Result.map (fun () ->
                                                        match v, node.Current() with
                                                        | Value.Null, o when not (isNull o) ->
                                                            match p.GetValue o with
                                                            | null -> ()
                                                            | sub when isEmpty sub -> p.SetValue(o, null)
                                                            | _ -> ()
                                                        | _ -> ()))
                            }

                    let settable =
                        inner.Entries |> List.exists (fun e -> e.Info.Writable)
                        || not inner.Templates.IsEmpty

                    // An absent sub-object with nothing to set under it isn't worth listing.
                    if isNull value && not settable then
                        {
                            Entries = []
                            Refusals = []
                            Templates = []
                        }
                    else

                        {
                            Entries = restrict self :: (inner.Entries |> List.map tidy)
                            Refusals = inner.Refusals
                            Templates =
                                inner.Templates
                                |> List.map (fun t -> {
                                    t with
                                        Make = fun name -> tidy (t.Make name)
                                })
                        })

    {
        Entries = parts |> Seq.collect (fun c -> c.Entries) |> List.ofSeq
        Refusals = parts |> Seq.collect (fun c -> c.Refusals) |> List.ofSeq
        Templates = parts |> Seq.collect (fun c -> c.Templates) |> List.ofSeq
    }

let private build (db: Database) (target: ObjectEntry) =
    let ctx = {
        Db = db
        Ref = ObjectRef.format target.Ref
        Level = db.CompatibilityLevel
        Mode = ModelIO.effectiveCompatibilityMode db
        Objects = lazy (objects db)
        Flat = lazy (Serialize.flatten (Serialize.objectJson db false target.Object))
    }

    let root = {
        Path = ""
        Type = target.Object.GetType()
        Current = fun () -> box target.Object
        Ensure = fun () -> box target.Object
    }

    let c = describe ctx 0 root

    let entries =
        c.Entries
        |> List.distinctBy (fun e -> e.Info.Path)
        |> List.sortBy (fun e -> e.Info.Path.ToLowerInvariant())

    // Paths `get -p` shows that other commands edit.
    let elsewhere = [
        if not (Serialize.translatableProperties target.Object).IsEmpty then
            {
                Path = "translations"
                Reason = "translations are edited with `sm translations import` (export a file to fill in first)"
            }
        if target.Ref.Kind = ObjectKind.Perspective then
            {
                Path = "members"
                Reason = "use `sm perspective add` or `sm perspective remove`"
            }
    ]

    ctx,
    {
        c with
            Entries = entries
            Refusals = c.Refusals @ elsewhere
    }

/// Every property `set` knows on an object, writable or not, sorted by path.
let catalog (db: Database) (target: ObjectEntry) : Descriptor list =
    let _, c = build db target
    c.Entries |> List.map (fun e -> e.Info)

// ---------------------------------------------------------------------------------------------
// Suggestions

let private distance (a: string) (b: string) =
    let a = a.ToLowerInvariant()
    let b = b.ToLowerInvariant()
    let d = Array2D.zeroCreate (a.Length + 1) (b.Length + 1)

    for i in 0 .. a.Length do
        d.[i, 0] <- i

    for j in 0 .. b.Length do
        d.[0, j] <- j

    for i in 1 .. a.Length do
        for j in 1 .. b.Length do
            let cost = if a.[i - 1] = b.[j - 1] then 0 else 1
            d.[i, j] <- min (min (d.[i - 1, j] + 1) (d.[i, j - 1] + 1)) (d.[i - 1, j - 1] + cost)

            if i > 1 && j > 1 && a.[i - 1] = b.[j - 2] && a.[i - 2] = b.[j - 1] then
                d.[i, j] <- min d.[i, j] (d.[i - 2, j - 2] + 1)

    d.[a.Length, b.Length]

let private suggestions (paths: string list) (input: string) =
    let leaf (p: string) =
        match p.LastIndexOf '.' with
        | -1 -> p
        | i -> p.Substring(i + 1)

    let inputLeaf = leaf input
    let limit = max 2 (input.Length / 3)

    paths
    |> List.filter (fun p -> not (p.EndsWith "<name>"))
    |> List.choose (fun p ->
        let full = distance p input
        // "expression" on a partition: "source.expression".
        let byLeaf =
            if p.Contains '.' then
                distance (leaf p) inputLeaf + 1
            else
                Int32.MaxValue

        let d = min full byLeaf
        if d <= limit then Some(p, d) else None)
    |> List.sortBy (fun (p, d) -> d, p)
    |> List.truncate 3
    |> List.map fst

// ---------------------------------------------------------------------------------------------
// Setting

let private eqPath (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

let private startsWith (prefix: string) (s: string) =
    s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)

/// The entry an assignment's path names.
let private lookup (ctx: Ctx) (c: Catalog) (path: string) : Result<Entry, SemctlError> =
    let path = path.Trim()

    let template =
        c.Templates
        |> List.filter (fun t -> startsWith t.Prefix path && path.Length > t.Prefix.Length)
        |> List.sortByDescending (fun t -> t.Prefix.Length)
        |> List.tryHead

    match
        c.Entries
        |> List.tryFind (fun e -> eqPath e.Info.Path path && not (e.Info.Path.EndsWith "<name>"))
    with
    | Some e -> Ok e
    | None ->
        match template with
        | Some t -> Ok(t.Make(path.Substring t.Prefix.Length))
        | None ->
            match
                c.Refusals
                |> List.tryFind (fun r -> eqPath r.Path path || startsWith (r.Path + ".") path)
            with
            | Some r -> Error(ReadOnlyProperty(ctx.Ref, path, r.Reason))
            | None ->
                let known =
                    (c.Entries |> List.map (fun e -> e.Info.Path)) @ (c.Refusals |> List.map _.Path)

                Error(UnknownProperty(ctx.Ref, path, suggestions known path))

let private describeAssignments (target: ObjectEntry) (applied: (Descriptor * Value) list) =
    let names value =
        applied
        |> List.filter (fun (_, v) -> (v = Value.Null) = value)
        |> List.map (fun (d, _) -> d.Path)
        |> String.concat ", "

    let set = names false
    let cleared = names true

    let parts = [
        if set <> "" then
            $"set {set}"
        if cleared <> "" then
            $"cleared {cleared}"
    ]

    let text = String.concat "; " parts
    $"{string (Char.ToUpperInvariant text.[0])}{text.Substring 1} on {ObjectRef.format target.Ref}"

/// Apply assignments to one object, all or nothing as far as checks go: every path and value is
/// checked (and references resolved) before anything is written. Use inside `Mutation.apply`.
let set (db: Database) (target: ObjectEntry) (assignments: Assignment list) : Result<Mutation.Effect, SemctlError> =
    let ctx, c = build db target

    let prepared =
        assignments
        |> List.fold
            (fun acc a ->
                acc
                |> Result.bind (fun done' ->
                    lookup ctx c a.Path
                    |> Result.bind (fun e ->
                        if e.Info.Writable then
                            Ok e
                        else
                            Error(ReadOnlyProperty(ctx.Ref, e.Info.Path, defaultArg e.Info.Reason "read-only")))
                    |> Result.bind (fun e ->
                        if done' |> List.exists (fun (d: Descriptor, _, _) -> eqPath d.Path e.Info.Path) then
                            invalid ctx e.Info.Path a.Value "this property is set more than once"
                        else
                            e.Prepare a.Value |> Result.map (fun w -> (e.Info, a.Value, w) :: done'))))
            (Ok [])
        |> Result.map List.rev

    prepared
    |> Result.bind (fun steps ->
        let rec run =
            function
            | [] -> Ok()
            | (info: Descriptor, value, write: Write) :: rest ->
                write ()
                |> Result.bind (fun () ->
                    // TOM rejects some values only when it writes the object out.
                    try
                        Serialize.objectJson db false target.Object |> ignore
                        Ok()
                    with e ->
                        Error(InvalidPropertyValue(ctx.Ref, info.Path, valueText value, (unwrap e).Message)))
                |> Result.bind (fun () -> run rest)

        run steps
        |> Result.map (fun () ->
            match steps with
            | [] -> Mutation.Effect.none
            | steps -> {
                Mutation.Effect.none with
                    Notes = [ describeAssignments target (steps |> List.map (fun (d, v, _) -> d, v)) ]
              }))
