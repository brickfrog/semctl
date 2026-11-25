/// Turning what a client library throws into errors a person can act on. The texts matched here
/// are Microsoft's and may change; anything unrecognised is still reported, just without a hint.
module Semctl.Xmla.Failures

open System
open System.Text.RegularExpressions
open Semctl

/// The messages of an exception and its causes, outermost first: one line each, without repeats,
/// and without the "Technical Details" (activity IDs, timestamps) the service appends.
let messages (e: exn) : string list =
    let rec chain (e: exn | null) =
        match e with
        | null -> []
        | :? AggregateException as a when a.InnerExceptions.Count > 0 ->
            a.InnerExceptions |> Seq.collect (fun inner -> chain inner) |> List.ofSeq
        | e -> e.Message :: chain e.InnerException

    let tidy (m: string | null) =
        let m =
            match m with
            | null -> ""
            | s -> s

        let cut =
            match m.IndexOf("Technical Details", StringComparison.OrdinalIgnoreCase) with
            | -1 -> m
            | i -> m.Substring(0, i)

        // AADSTS messages carry trace and correlation IDs and a timestamp after the sentence.
        let cut =
            match cut.IndexOf(" Trace ID:", StringComparison.OrdinalIgnoreCase) with
            | -1 -> cut
            | i -> cut.Substring(0, i)

        cut.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        // The service's "Description:" line is often empty.
        |> Array.filter (fun l -> l <> "" && l <> "Description:")
        |> String.concat " "

    chain e |> List.map tidy |> List.filter (fun m -> m <> "") |> List.distinct

let private has (text: string) (part: string) =
    text.Contains(part, StringComparison.OrdinalIgnoreCase)

let private aadsts =
    Regex(@"AADSTS(\d+)", RegexOptions.CultureInvariant ||| RegexOptions.Compiled, TimeSpan.FromSeconds 1.0)

let private aadHint (code: string) =
    match code with
    | "90002" -> Some "The tenant wasn't found: check SM_XMLA_TENANT_ID (a tenant ID or domain)."
    | "700016" -> Some "The application isn't in this tenant: check SM_XMLA_CLIENT_ID and SM_XMLA_TENANT_ID."
    | "7000215" -> Some "The client secret is wrong: use the secret's value (not its ID) in SM_XMLA_CLIENT_SECRET."
    | "7000222" -> Some "The client secret has expired: create a new one for the application."
    | "50034" -> Some "The user doesn't exist in this tenant."
    | "50076"
    | "50079"
    | "50158" ->
        Some
            "Multi-factor authentication is required: sign in with SM_XMLA_AUTH=device-code, or use a service principal."
    | "65001" -> Some "The application hasn't been granted consent in this tenant."
    | "500011" -> Some "The token's resource isn't known in this tenant: is this the right cloud?"
    | "70016"
    | "70020" -> Some "The device code wasn't entered in time: run the command again."
    | _ -> None

let private windowsOnly =
    "SSAS over TCP (host, host:port, host\\instance, and Power BI Desktop's localhost:port) only works on "
    + "Windows: Microsoft's client library has no TCP transport on other systems. Use an HTTP endpoint "
    + "(https://host/olap/msmdpump.dll), powerbi:// or asazure://, or run sm on Windows."

/// Why SSAS over TCP can't be used here.
let unsupportedTcp (server: string) =
    UnsupportedConnection(server, windowsOnly)

/// A failure to connect or authenticate, as a SemctlError. `secrets` are removed from every text.
let classify (address: XmlaAddress) (secrets: string list) (e: exn) : SemctlError =
    let server = XmlaAddress.server address
    let lines = messages e |> List.map (Secrets.scrub secrets)
    // "Outer message: inner message." rather than "Outer message.: inner message."
    let text =
        match List.rev lines with
        | [] -> ""
        | last :: outer ->
            (outer |> List.rev |> List.map (fun l -> l.TrimEnd('.', ':', ' '))) @ [ last ]
            |> String.concat ": "

    let reason = if text = "" then e.GetType().Name else text
    let has = has text

    let rec notSupported (e: exn | null) =
        match e with
        | null -> false
        | :? NotSupportedException -> true
        | e -> notSupported e.InnerException

    let status (code: string) (name: string) = has $"({code})" || has name

    if notSupported e && has "only on Windows" then
        if address.Endpoint = XmlaEndpoint.Tcp then
            unsupportedTcp server
        else
            UnsupportedConnection(
                server,
                reason
                + " (on this system an HTTP server needs a user name and password: SM_XMLA_USER and SM_XMLA_PASSWORD)"
            )
    else
        match aadsts.Match text with
        | m when m.Success -> AuthenticationFailed(server, reason, aadHint m.Groups.[1].Value)
        | _ when has "Unable to obtain authentication token" || has "authentication failed" ->
            AuthenticationFailed(server, reason, None)
        | _ when has "access-token is required" || has "access token is required" ->
            AuthenticationRequired(server, "the client library needs a token or a service principal for this server")
        | _ when has "Resolving the PBI workspace" && status "403" "Forbidden" ->
            ConnectionFailed(
                server,
                reason,
                Some(
                    "Power BI refused access to the workspace. Check the workspace name; that the identity is a "
                    + "Contributor, Member or Admin of it; and, for a service principal, that the tenant setting "
                    + "\"Service principals can use Fabric APIs\" allows it."
                )
            )
        | _ when has "Resolving the PBI workspace" && status "404" "Not Found" ->
            ConnectionFailed(server, reason, Some "No such workspace: check its name (and the tenant in the URL).")
        | _ when
            has "XMLA"
            && (has "read/write"
                || has "readwrite"
                || has "read-only"
                || has "readonly"
                || has "read only")
            ->
            ConnectionFailed(
                server,
                reason,
                Some
                    "The capacity's XMLA endpoint is read-only or off: an admin sets \"XMLA Endpoint\" to \"Read Write\" in the capacity settings (Power BI workloads)."
            )
        | _ when
            (has "Premium" || has "dedicated capacity" || has "capacity")
            && (has "workspace" || has "XMLA")
            ->
            ConnectionFailed(
                server,
                reason,
                Some
                    "XMLA endpoints need the workspace to be on a Premium, Premium Per User, Embedded or Fabric capacity, with the XMLA endpoint enabled."
            )
        | _ when status "401" "Unauthorized" ->
            AuthenticationFailed(
                server,
                reason,
                Some
                    "The server refused the credentials. For a token: is it still valid, and issued for this server's resource (Power BI: https://analysis.windows.net/powerbi/api)?"
            )
        | _ when status "403" "Forbidden" ->
            ConnectionFailed(server, reason, Some "The identity is known but not allowed to do this on the server.")
        | _ when address.Endpoint = XmlaEndpoint.AzureAS && has "is not found" ->
            ConnectionFailed(server, reason, Some "No such Azure Analysis Services server: check the region and name.")
        | _ when
            has "No such host"
            || has "Name or service not known"
            || has "nodename nor servname"
            || has "Connection refused"
            || has "actively refused"
            || has "timed out"
            || has "A connection cannot be made"
            ->
            ConnectionFailed(server, reason, Some "Check the server address and that it can be reached from here.")
        | _ -> ConnectionFailed(server, reason, None)
