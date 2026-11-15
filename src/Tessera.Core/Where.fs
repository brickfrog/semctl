/// `get <container> --where Prop=pattern`: the objects inside a container whose property matches a
/// wildcard. Values are read the way `get --properties` shows them (so a default such as
/// `isHidden=false` has a value even though TOM's JSON leaves it out), falling back to the paths
/// `get -p` prints (translations, a perspective's members, nested JSON).
module Tessera.Where

open System
open System.Globalization
open System.Text.RegularExpressions
open Microsoft.AnalysisServices.Tabular
open Tessera.Inventory

[<RequireQualifiedAccess>]
type Op =
    /// `Prop=pattern`
    | Matches
    /// `Prop!=pattern`
    | DoesNotMatch

type Condition = {
    /// A property path (case-insensitive), or `objectKind` for the object's kind (`measure`, ...).
    /// Not `kind`: named expressions have a TOM property of that name.
    Path: string
    Op: Op
    /// A wildcard: `*` any run of characters (line breaks included), `?` one character; the rest is
    /// literal. Matched case-insensitively against the whole value.
    Pattern: string
}

module Condition =
    /// `path=pattern` or `path!=pattern`. The first `=` splits it, so the pattern may contain `=`.
    let parse (text: string) : Result<Condition, string> =
        match text.IndexOf '=' with
        | -1 -> Error $"--where expects <path>=<pattern> or <path>!=<pattern>, got '{text}'"
        | i ->
            let negated = i > 0 && text.[i - 1] = '!'
            let path = text.Substring(0, (if negated then i - 1 else i)).Trim()

            if path = "" then
                Error $"--where: missing property path in '{text}'"
            else
                Ok {
                    Path = path
                    Op = (if negated then Op.DoesNotMatch else Op.Matches)
                    Pattern = text.Substring(i + 1)
                }

    let format (c: Condition) =
        let op =
            match c.Op with
            | Op.Matches -> "="
            | Op.DoesNotMatch -> "!="

        c.Path + op + c.Pattern

/// The wildcard as a whole-value, case-insensitive matcher.
let glob (pattern: string) : string -> bool =
    let escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".")

    let rx =
        Regex(
            "^" + escaped + "$",
            RegexOptions.IgnoreCase
            ||| RegexOptions.CultureInvariant
            ||| RegexOptions.Singleline
        )

    fun value -> rx.IsMatch value

/// The objects inside a container: those whose TOM parent chain reaches it. For the model that is
/// every other object; for a table its columns, measures, hierarchies, levels, partitions,
/// calculation items and calendars; for a hierarchy its levels. In inventory order.
let within (entries: ObjectEntry list) (container: ObjectEntry) : ObjectEntry list =
    let rec inside (o: MetadataObject) =
        match o.Parent with
        | null -> false
        | p when obj.ReferenceEquals(p, container.Object) -> true
        | p -> inside p

    entries
    |> List.filter (fun e ->
        not (obj.ReferenceEquals(e.Object, container.Object))
        && (container.Ref.Kind = ObjectKind.Model || inside e.Object))

let private eq (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

/// The text a catalog value is matched as. A value that isn't there reads as "".
let private valueText (v: SetProperty.PropertyValue) =
    match v with
    | SetProperty.PropertyValue.Null -> ""
    | SetProperty.PropertyValue.String s -> s
    | SetProperty.PropertyValue.Bool b -> if b then "true" else "false"
    | SetProperty.PropertyValue.Int n -> n.ToString(CultureInfo.InvariantCulture)
    | SetProperty.PropertyValue.Number n -> n.ToString("R", CultureInfo.InvariantCulture)
    | SetProperty.PropertyValue.Reference r -> ObjectRef.format r
    | SetProperty.PropertyValue.Present -> "{}"

/// An object's properties as --where sees them: the catalog's (with defaults), then the paths
/// `get -p` prints that the catalog doesn't have. And the named-item prefixes ("annotations.") the
/// object can hold, where a missing item reads as "".
type private View = {
    Values: (string * string) list
    Prefixes: string list
}

let private view (db: Database) (e: ObjectEntry) : View =
    let catalog = SetProperty.catalog db e

    let named, plain = catalog |> List.partition (fun d -> d.Path.EndsWith ".<name>")

    let own = plain |> List.map (fun d -> d.Path, valueText d.Value)

    let flat =
        Serialize.properties db e
        |> List.filter (fun (p, _) -> not (own |> List.exists (fun (q, _) -> eq p q)))

    {
        Values = own @ flat @ [ "objectKind", ObjectKind.token e.Ref.Kind ]
        Prefixes =
            named
            |> List.map (fun d -> d.Path.Substring(0, d.Path.Length - "<name>".Length))
    }

/// The value at a path, if the object has that property (case-insensitive).
let private lookup (v: View) (path: string) : string option =
    match v.Values |> List.tryFind (fun (p, _) -> eq p path) with
    | Some(_, value) -> Some value
    | None ->
        if
            v.Prefixes
            |> List.exists (fun prefix ->
                path.Length > prefix.Length
                && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        then
            Some ""
        else
            None

/// The objects inside `container` that meet every condition. An object without the property at
/// all (a relationship has no `isHidden`) meets neither `=` nor `!=`; a property that holds nothing
/// reads as "", so `description=` finds the objects without one. A path no object inside has is an
/// error, with close matches.
let filter (db: Database) (container: ObjectEntry) (conditions: Condition list) : Result<ObjectEntry list, TessError> =
    let candidates = within (objects db) container
    let views = candidates |> List.map (fun e -> e, lazy (view db e))

    let unknown =
        conditions
        |> List.tryFind (fun c -> not (views |> List.exists (fun (_, v) -> (lookup v.Value c.Path).IsSome)))

    match unknown with
    | Some c ->
        let known =
            views
            |> List.collect (fun (_, v) ->
                (v.Value.Values |> List.map fst)
                @ (v.Value.Prefixes |> List.map (fun p -> p + "<name>")))
            |> List.distinct

        Error(UnknownProperty(ObjectRef.format container.Ref, c.Path, Suggest.closest known c.Path))
    | None ->
        let tests =
            conditions
            |> List.map (fun c ->
                let m = glob c.Pattern

                fun (v: View) ->
                    match lookup v c.Path, c.Op with
                    | None, _ -> false
                    | Some value, Op.Matches -> m value
                    | Some value, Op.DoesNotMatch -> not (m value))

        Ok(
            views
            |> List.filter (fun (_, v) -> tests |> List.forall (fun t -> t v.Value))
            |> List.map fst
        )
