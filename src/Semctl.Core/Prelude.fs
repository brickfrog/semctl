/// Shared foundational helpers across semctl: Result computation expression, case-insensitive
/// comparisons, formatting, JSON/encoding defaults, and Windows reserved file names.
module Semctl.Prelude

open System
open System.Collections.Generic
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

type ResultBuilder() =
    member _.Bind(r: Result<'a, 'e>, f: 'a -> Result<'b, 'e>) = Result.bind f r
    member _.Return(x: 'a) : Result<'a, 'e> = Ok x
    member _.ReturnFrom(r: Result<'a, 'e>) = r
    member _.Zero() : Result<unit, 'e> = Ok()
    member _.Delay(f: unit -> Result<'a, 'e>) = f
    member _.Run(f: unit -> Result<'a, 'e>) = f ()
    member _.Combine(r: Result<unit, 'e>, f: unit -> Result<'a, 'e>) = Result.bind f r

    member _.For(xs: 'a seq, body: 'a -> Result<unit, 'e>) =
        use e = xs.GetEnumerator()

        let rec loop () =
            if e.MoveNext() then
                match body e.Current with
                | Ok() -> loop ()
                | Error err -> Error err
            else
                Ok()

        loop ()

    member _.While(guard: unit -> bool, body: unit -> Result<unit, 'e>) =
        let rec loop () =
            if guard () then
                match body () with
                | Ok() -> loop ()
                | Error err -> Error err
            else
                Ok()

        loop ()

    member _.TryWith(body: unit -> Result<'a, 'e>, handler: exn -> Result<'a, 'e>) =
        try
            body ()
        with ex ->
            handler ex

    member _.TryFinally(body: unit -> Result<'a, 'e>, compensation: unit -> unit) =
        try
            body ()
        finally
            compensation ()

    member _.Using(resource: #IDisposable, body: #IDisposable -> Result<'a, 'e>) =
        use r = resource
        body r

let result = ResultBuilder()

let resultTraverse (f: 'a -> Result<'b, 'e>) (xs: 'a list) : Result<'b list, 'e> =
    let rec loop acc =
        function
        | [] -> Ok(List.rev acc)
        | x :: rest ->
            match f x with
            | Ok y -> loop (y :: acc) rest
            | Error e -> Error e

    loop [] xs

let optionSequence (xs: 'a option list) : 'a list option =
    let rec loop acc =
        function
        | [] -> Some(List.rev acc)
        | None :: _ -> None
        | Some x :: rest -> loop (x :: acc) rest

    loop [] xs

let ci = StringComparer.OrdinalIgnoreCase

let eqCi (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

let nonNull (s: string) = if isNull s then "" else s

let pluralOf (n: int) (one: string) (many: string) =
    if n = 1 then $"1 {one}" else $"{n} {many}"

let plural (n: int) (singular: string) = pluralOf n singular $"{singular}s"

let utf8NoBom = UTF8Encoding false

let documentOptions =
    JsonDocumentOptions(AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip)

let relaxedJsonOptions =
    JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let indentedJsonOptions =
    JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NewLine = "\n")

module WindowsFileName =
    let reservedNames =
        let numbered = [
            for prefix in [ "COM"; "LPT" ] do
                for suffix in [ for d in 0..9 -> string d ] @ [ "\u00B9"; "\u00B2"; "\u00B3" ] do
                    yield prefix + suffix
        ]

        HashSet<string>([ "CON"; "PRN"; "AUX"; "NUL" ] @ numbered, StringComparer.OrdinalIgnoreCase)

    let isReserved (name: string) =
        let stem =
            match name.IndexOf '.' with
            | -1 -> name
            | i -> name.Substring(0, i)

        reservedNames.Contains(stem.TrimEnd ' ')
