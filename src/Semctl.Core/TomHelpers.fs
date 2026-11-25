/// Shared helpers for inspecting and updating Tabular Object Model (TOM) objects safely.
module Semctl.Tom

open System
open System.Collections
open System.Text.Json
open Microsoft.AnalysisServices.Tabular
open Semctl.Prelude

/// An object's Annotations collection via reflection (TOM defines Annotations on most, not all,
/// metadata types — e.g. calculation items have none).
let annotationSeq (o: MetadataObject) : Annotation seq =
    match o.GetType().GetProperty("Annotations") with
    | null -> Seq.empty
    | p ->
        match p.GetValue o with
        | :? IEnumerable as coll ->
            coll
            |> Seq.cast<obj>
            |> Seq.choose (function
                | :? Annotation as a -> Some a
                | _ -> None)
        | _ -> Seq.empty

/// Try to read a named annotation's value from any MetadataObject.
let tryAnnotation (name: string) (o: MetadataObject) : string option =
    annotationSeq o
    |> Seq.tryPick (fun a ->
        if a.Name = name && not (String.IsNullOrEmpty a.Value) then
            Some a.Value
        else
            None)

let isCalculationGroup (t: Table) = not (isNull t.CalculationGroup)

let hasEndpoints (r: SingleColumnRelationship) =
    not (isNull r.FromColumn) && not (isNull r.ToColumn)

let queryOrderAnnotation = "PBI_QueryOrder"

let private tryQueryOrder (m: Model) : (Annotation * string array) option =
    match m.Annotations.Find queryOrderAnnotation with
    | null -> None
    | a ->
        try
            match JsonSerializer.Deserialize<string array>(a.Value) with
            | null -> None
            | names -> Some(a, names)
        with :? JsonException ->
            None

let appendQueryOrder (m: Model) (name: string) =
    match tryQueryOrder m with
    | Some(a, names) when not (Array.contains name names) ->
        a.Value <- JsonSerializer.Serialize(Array.append names [| name |], relaxedJsonOptions)
    | _ -> ()

let removeQueryOrder (m: Model) (removedNames: Set<string>) : bool =
    match tryQueryOrder m with
    | Some(a, names) when names |> Array.exists removedNames.Contains ->
        let kept = names |> Array.filter (removedNames.Contains >> not)
        a.Value <- JsonSerializer.Serialize(kept, relaxedJsonOptions)
        true
    | _ -> false

let renameQueryOrder (m: Model) (oldName: string) (newName: string) : (string * string) option =
    match tryQueryOrder m with
    | Some(a, names) when Array.contains oldName names ->
        let renamed = names |> Array.map (fun n -> if n = oldName then newName else n)
        Some(a.Value, JsonSerializer.Serialize(renamed, relaxedJsonOptions))
    | _ -> None
