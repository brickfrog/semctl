/// The property catalog: what `set` can write on each TOM object type, found once by reflection and
/// cached per type. One classifier decides, for every public property of a TOM type, whether it is a
/// scalar (string, expression, bool, int, number, enum), a reference to another addressable object,
/// a set of named items (annotations, extended properties), a sub-object to walk into, a child
/// collection (edited by add/remove/move, not set), or plumbing to ignore.
///
/// SetProperty builds an object's descriptor (`get --properties`, the checks behind `set`) from these
/// cached rows, so the two can't disagree. `forType` is the same table without an object: per TOM
/// type, at a compatibility level and mode, the paths and their types, writability and enum members
/// (what an MCP client's tool schema, or a user without the object at hand, needs).
module Tessera.PropertyCatalog

open System
open System.Collections
open System.Collections.Concurrent
open System.Collections.Generic
open System.ComponentModel
open System.Globalization
open System.Reflection
open Microsoft.AnalysisServices.Tabular

[<RequireQualifiedAccess>]
type PropertyType =
    | String
    /// A string holding code: DAX, M or a query.
    | Expression
    | Bool
    /// Int32 or Int64.
    | Int
    /// A floating-point number.
    | Number
    /// One of `Allowed`, as TOM's JSON writes it.
    | Enum
    /// Another object in the model, of kind `RefersTo`.
    | Reference
    /// A sub-object (`kpi`, `formatStringDefinition`, ...): its own properties are listed under it.
    /// It can only be set to null, which removes it.
    | Object

module PropertyType =
    let token =
        function
        | PropertyType.String -> "string"
        | PropertyType.Expression -> "expression"
        | PropertyType.Bool -> "bool"
        | PropertyType.Int -> "int"
        | PropertyType.Number -> "number"
        | PropertyType.Enum -> "enum"
        | PropertyType.Reference -> "reference"
        | PropertyType.Object -> "object"

// ---------------------------------------------------------------------------------------------
// Reflection

/// Properties that are TOM plumbing, engine state or timestamps, never part of a model's definition.
let internal internalNames =
    set [
        "ObjectType"
        "Parent"
        "Model"
        "Database"
        "Server"
        "IsRemoved"
        "HasLocalChanges"
        "IsEmpty"
        "State"
        "ErrorMessage"
        "ModifiedTime"
        "StructureModifiedTime"
        "RefreshedTime"
        // Partition: shown under `source` as `source.type`.
        "SourceType"
        // Engine-built structures.
        "AttributeHierarchy"
        "StringIndex"
        "FullTextIndex"
    ]

/// Secrets: TOM's JSON masks them, so a change can't be shown or diffed.
let internal secretNames = set [ "Password" ]

/// Values TOM's JSON partly masks (a connection string's password): shown as `get -p` shows them.
let internal maskedNames = set [ "ConnectionString" ]

/// Sub-objects `set` won't create or remove: a partition always has a source, and a calculation group
/// makes its table a calculation-group table.
let internal structural = set [ "Source"; "CalculationGroup" ]

/// TOM's JSON property name: lower-case first letter, except where TOM spells it differently.
let internal jsonName (name: string) =
    match name with
    | "IsAvailableInMDX" -> "isAvailableInMdx"
    | "KPI" -> "kpi"
    | _ -> string (Char.ToLowerInvariant name.[0]) + name.Substring 1

/// Enum member names are written the same way (`int64`, `powerBI_V3`).
let internal enumJsonName (name: string) =
    string (Char.ToLowerInvariant name.[0]) + name.Substring 1

/// Addressable kinds a reference property can point at, by TOM type.
let internal referenceKinds = [
    typeof<Column>, ObjectKind.Column
    typeof<Measure>, ObjectKind.Measure
    typeof<Table>, ObjectKind.Table
    typeof<Hierarchy>, ObjectKind.Hierarchy
    typeof<Level>, ObjectKind.Level
    typeof<Partition>, ObjectKind.Partition
    typeof<DataSource>, ObjectKind.DataSource
    typeof<NamedExpression>, ObjectKind.Expression
    typeof<Function>, ObjectKind.Function
    typeof<Relationship>, ObjectKind.Relationship
    typeof<ModelRole>, ObjectKind.Role
    typeof<Perspective>, ObjectKind.Perspective
    typeof<Culture>, ObjectKind.Culture
    typeof<Calendar>, ObjectKind.Calendar
    typeof<CalculationItem>, ObjectKind.CalculationItem
]

/// Named objects TOM references that tess can't address.
let internal unaddressable = [ typeof<QueryGroup>, "query groups" ]

type internal Compat =
    | Supported
    | Hidden
    | Needs of string

/// TOM's (internal) CompatibilityRequirement attribute, read by name: the level a property or enum
/// member needs in each compatibility mode.
let private requirements = ConcurrentDictionary<MemberInfo, obj option>()

let internal compatibility (level: int) (mode: Microsoft.AnalysisServices.CompatibilityMode) (m: MemberInfo) =
    let attr =
        requirements.GetOrAdd(
            m,
            fun (m: MemberInfo) ->
                m.GetCustomAttributes(true)
                |> Array.tryFind (fun a -> a.GetType().Name = "CompatibilityRequirementAttribute")
        )

    match attr with
    | None -> Supported
    | Some a ->
        let field =
            match mode with
            | Microsoft.AnalysisServices.CompatibilityMode.PowerBI -> "Pbi"
            | Microsoft.AnalysisServices.CompatibilityMode.Excel -> "Excel"
            | _ -> "Box"

        let modeName =
            match mode with
            | Microsoft.AnalysisServices.CompatibilityMode.PowerBI -> "Power BI"
            | Microsoft.AnalysisServices.CompatibilityMode.Excel -> "Excel"
            | _ -> "Analysis Services"

        match a.GetType().GetProperty(field) with
        | null -> Supported
        | p ->
            match p.GetValue a with
            | :? string as required ->
                match Int32.TryParse(required, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, n when n <= level -> Supported
                | true, n -> Needs $"needs compatibility level {n} (the model is at {level})"
                | _ when required = "Unsupported" -> Needs $"not supported in {modeName} models"
                | _ -> Hidden // Internal, Preview
            | _ -> Supported

let internal hiddenMember (m: MemberInfo) =
    m.IsDefined(typeof<ObsoleteAttribute>, true)
    || (match m.GetCustomAttribute<EditorBrowsableAttribute>(true) with
        | null -> false
        | a -> a.State = EditorBrowsableState.Never)

type internal Named =
    | Annotations
    | ExtendedProperties

type internal Kind =
    | Scalar of PropertyType
    | Ref of ObjectKind
    | Unaddressable of string
    /// A Power Query property bag (a structured data source's credential, connection details, ...):
    /// its JSON keys are whatever M put there, not the TOM properties.
    | PropertyBag
    | NamedItems of Named
    | Children
    | Sub
    | Skip

let internal isSettable (p: PropertyInfo) = not (isNull (p.GetSetMethod()))

/// Property bags whose typed properties are exactly their JSON (checked by the tests).
let internal typedBags = [ typeof<DataAccessOptions>; typeof<AutomaticAggregationOptions> ]

let internal isPropertyBag (t: Type) =
    not (List.contains t typedBags)
    && t.GetProperties() |> Array.exists (fun p -> p.GetIndexParameters().Length > 0)

let internal classify (p: PropertyInfo) =
    let t = p.PropertyType

    if
        p.GetIndexParameters().Length > 0
        || internalNames.Contains p.Name
        || hiddenMember p
    then
        Skip
    elif t = typeof<DateTime> then
        Skip
    elif p.Name = "Annotations" && typeof<IEnumerable>.IsAssignableFrom t then
        NamedItems Annotations
    elif p.Name = "ExtendedProperties" && typeof<IEnumerable>.IsAssignableFrom t then
        NamedItems ExtendedProperties
    elif t = typeof<string> then
        if p.Name.EndsWith("Expression", StringComparison.Ordinal) || p.Name = "Query" then
            Scalar PropertyType.Expression
        else
            Scalar PropertyType.String
    elif t = typeof<bool> then
        Scalar PropertyType.Bool
    elif t = typeof<int> || t = typeof<int64> then
        Scalar PropertyType.Int
    elif t = typeof<float> then
        Scalar PropertyType.Number
    elif t.IsEnum then
        Scalar PropertyType.Enum
    elif typeof<IEnumerable>.IsAssignableFrom t then
        Children
    else
        match referenceKinds |> List.tryFind (fun (rt, _) -> rt = t) with
        | Some(_, kind) -> if isSettable p then Ref kind else Skip
        | None ->
            match unaddressable |> List.tryFind (fun (ut, _) -> ut = t) with
            | Some(_, what) -> if isSettable p then Unaddressable what else Skip
            | None when t.IsClass ->
                // A read-only TOM object property is a back-pointer (or engine state), not a sub-object.
                if typeof<MetadataObject>.IsAssignableFrom t && not (isSettable p) then
                    Skip
                elif isPropertyBag t then
                    PropertyBag
                else
                    Sub
            | None -> Skip

/// Public properties in a stable order: the type's own first, then its bases'.
let internal propertiesOf (t: Type) =
    t.GetProperties(BindingFlags.Instance ||| BindingFlags.Public)
    |> Array.sortBy (fun p -> p.MetadataToken)
    |> Array.distinctBy (fun p -> p.Name)

let internal freshInstances = Dictionary<Type, obj option>()

/// A new instance of `t`, to read TOM's default values from.
let internal fresh (t: Type) =
    lock freshInstances (fun () ->
        match freshInstances.TryGetValue t with
        | true, v -> v
        | _ ->
            let v =
                if not t.IsAbstract && not (isNull (t.GetConstructor Type.EmptyTypes)) then
                    try
                        Some(Activator.CreateInstance t)
                    with _ ->
                        None
                else
                    None

            freshInstances.[t] <- v
            v)

/// TOM's default for a property: what a new object of that type has.
let internal defaultOf (owner: Type) (p: PropertyInfo) =
    match fresh owner with
    | Some o -> p.GetValue o
    | None ->
        if p.PropertyType.IsValueType then
            Activator.CreateInstance p.PropertyType
        else
            null

let internal creatable (p: PropertyInfo) =
    let t = p.PropertyType

    isSettable p
    && not (structural.Contains p.Name)
    && not t.IsAbstract
    && not (isNull (t.GetConstructor Type.EmptyTypes))

/// Enum members TOM accepts but stores as something else (an Automatic data type reads back as Unknown).
let private unsettableMembers = set [ typeof<DataType>.FullName, "Automatic" ]

let private enumFields = ConcurrentDictionary<Type, FieldInfo list>()

/// An enum's members, with what the compatibility level and mode say about each (hidden ones left out).
let internal enumMembers (level: int) (mode: Microsoft.AnalysisServices.CompatibilityMode) (t: Type) =
    enumFields.GetOrAdd(
        t,
        fun (t: Type) ->
            t.GetFields(BindingFlags.Public ||| BindingFlags.Static)
            |> Array.filter (fun f -> not (hiddenMember f) && not (unsettableMembers.Contains(t.FullName, f.Name)))
            |> List.ofArray
    )
    |> List.map (fun f -> f, compatibility level mode f)
    |> List.filter (fun (_, c) -> c <> Hidden)

/// The members a model at this level and mode accepts, as TOM's JSON writes them.
let internal allowedMembers (level: int) (mode: Microsoft.AnalysisServices.CompatibilityMode) (t: Type) =
    enumMembers level mode t
    |> List.filter (fun (_, c) -> c = Supported)
    |> List.map (fun (f, _) -> enumJsonName f.Name)

/// One public property of a TOM type, classified once.
type internal Member = {
    Info: PropertyInfo
    /// TOM's JSON name: the path segment.
    Segment: string
    Kind: Kind
}

let private memberTable = ConcurrentDictionary<Type, Member array>()

/// A TOM type's properties, classified, in a stable order (the type's own first, then its bases'),
/// leaving out what is never a property (plumbing, timestamps, indexers). Built once per type.
let internal members (t: Type) : Member array =
    memberTable.GetOrAdd(
        t,
        fun (t: Type) ->
            propertiesOf t
            |> Array.map (fun p -> {
                Info = p
                Segment = jsonName p.Name
                Kind = classify p
            })
            |> Array.filter (fun m -> m.Kind <> Skip)
    )

// ---------------------------------------------------------------------------------------------
// The table, per type

/// One path of a TOM type, as `set` sees it at a compatibility level and mode.
type Row = {
    /// The path, as `get -p` and `set -p` take it ("formatString", "kpi.targetExpression",
    /// "annotations.<name>" for any annotation).
    Path: string
    Type: PropertyType
    /// Whether `set` accepts it on an object of this type (at this level and mode).
    Writable: bool
    /// Why not, when it isn't.
    Reason: string option
    /// Enum members valid at the level and mode.
    Allowed: string list
    /// For references: the kind of object it points at.
    RefersTo: ObjectKind option
    /// The concrete TOM types the row holds for, when the path runs through a sub-object whose type
    /// varies (a partition's `source`: "MPartitionSource", ...); empty when it holds for every object.
    Only: string list
}

/// The concrete TOM types a (possibly abstract) sub-object type can be.
let private concreteTypes (t: Type) =
    if not t.IsAbstract then
        [ t ]
    else
        t.Assembly.GetExportedTypes()
        |> Array.filter (fun c -> not c.IsAbstract && t.IsAssignableFrom c && not (hiddenMember c))
        |> Array.sortBy _.Name
        |> List.ofArray

let rec private rowsOf
    (level: int)
    (mode: Microsoft.AnalysisServices.CompatibilityMode)
    (depth: int)
    (prefix: string)
    (blocked: string option)
    (only: string list)
    (t: Type)
    : Row list = [
    for m in members t do
        let path = if prefix = "" then m.Segment else prefix + "." + m.Segment

        let compat =
            match blocked, compatibility level mode m.Info with
            | Some why, _ -> Needs why
            | None, c -> c

        let row pt = {
            Path = path
            Type = pt
            Writable = true
            Reason = None
            Allowed = []
            RefersTo = None
            Only = only
        }

        let restrict (r: Row) =
            match compat with
            | Needs why when r.Writable -> {
                r with
                    Writable = false
                    Reason = Some why
              }
            | _ -> r

        let readOnly (r: Row) why = {
            r with
                Writable = false
                Reason = Some why
        }

        match compat, m.Kind with
        | Hidden, _ -> ()
        | _, Scalar pt ->
            let r = {
                row pt with
                    Allowed =
                        if pt = PropertyType.Enum then
                            allowedMembers level mode m.Info.PropertyType
                        else
                            []
            }

            if m.Info.Name = "Name" && depth = 0 then
                yield
                    readOnly
                        r
                        (if t = typeof<Model> then
                             "tess doesn't rename the model"
                         else
                             "use `tess rename`, which also updates the DAX that refers to it")
            elif secretNames.Contains m.Info.Name then
                yield readOnly r "a secret: TOM's JSON masks it, so tess can't show or diff a change"
            elif not (isSettable m.Info) then
                yield readOnly r "read-only in TOM"
            else
                yield restrict r
        | _, Ref kind ->
            yield
                restrict {
                    row PropertyType.Reference with
                        RefersTo = Some kind
                }
        | _, Unaddressable what ->
            yield
                readOnly
                    (row PropertyType.Reference)
                    $"it refers to one of the model's {what}, which tess can't address yet"
        | _, NamedItems _ -> yield restrict (row PropertyType.String |> fun r -> { r with Path = path + ".<name>" })
        | _, Sub ->
            let canRemove = isSettable m.Info && not (structural.Contains m.Info.Name)

            let self =
                if canRemove then
                    restrict (row PropertyType.Object)
                elif structural.Contains m.Info.Name then
                    readOnly
                        (row PropertyType.Object)
                        "it can't be removed or replaced by `set`; set the properties under it"
                else
                    readOnly (row PropertyType.Object) "read-only in TOM"

            yield self

            if depth < 3 then
                let inner =
                    match compat with
                    | Needs why -> Some why
                    | _ -> None

                match concreteTypes m.Info.PropertyType with
                | [ c ] -> yield! rowsOf level mode (depth + 1) path inner only c
                | cs ->
                    for c in cs do
                        yield! rowsOf level mode (depth + 1) path inner (only @ [ c.Name ]) c
        | _, (Children | PropertyBag | Skip) -> ()
]

let private tables =
    ConcurrentDictionary<struct (Type * int * Microsoft.AnalysisServices.CompatibilityMode), Row list>()

/// What `set` can write on an object of TOM type `t` in a model at this compatibility level and mode:
/// every path, writable or not, sorted by path. Sub-objects whose type varies list each concrete
/// type's paths, marked with `Only`. Built once per (type, level, mode).
let forType (level: int) (mode: Microsoft.AnalysisServices.CompatibilityMode) (t: Type) : Row list =
    tables.GetOrAdd(
        struct (t, level, mode),
        fun _ ->
            rowsOf level mode 0 "" None [] t
            |> List.sortBy (fun r -> r.Path.ToLowerInvariant(), r.Only)
    )

/// `forType`, for an object of a model.
let forObject (db: Database) (o: MetadataObject) : Row list =
    forType db.CompatibilityLevel (ModelIO.effectiveCompatibilityMode db) (o.GetType())
