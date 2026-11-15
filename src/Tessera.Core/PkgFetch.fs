/// The network code of `tess pkg`, used only when the user names daxlib.org as the package source
/// (`--from daxlib`, or TESS_PKG_SOURCE=daxlib) and hasn't passed --offline. It GETs URLs on
/// https://api.daxlib.org and nothing else: any other host, plain http, or a redirect anywhere
/// is refused. The request carries nothing about the model or the user: no cookies, no
/// credentials, no body, only the User-Agent "tessera".
module Tessera.PkgFetch

open System
open System.IO
open System.Net
open System.Net.Http
open System.Threading.Tasks

/// How long a request may take, connection included.
let timeout = TimeSpan.FromSeconds 30.0

/// Whether tess may fetch this URL: https, on the daxlib host, its default port, no credentials.
let refuse (url: Uri) : string option =
    if url.Scheme <> Uri.UriSchemeHttps then
        Some "only https:// URLs are fetched"
    elif not (url.Host.Equals(PkgSource.daxlibHost, StringComparison.OrdinalIgnoreCase)) then
        Some $"only {PkgSource.daxlibHost} is contacted"
    elif not url.IsDefaultPort then
        Some "only the default https port is used"
    elif not (String.IsNullOrEmpty url.UserInfo) then
        Some "URLs with credentials are not fetched"
    else
        None

let private client =
    lazy
        (let handler =
            new HttpClientHandler(
                UseCookies = false,
                UseDefaultCredentials = false,
                Credentials = null,
                PreAuthenticate = false,
                // A redirect could lead off the host: refused (the API answers directly).
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            )

         let c =
             new HttpClient(handler, Timeout = timeout, MaxResponseContentBufferSize = Pkg.maxPackageBytes)

         c.DefaultRequestHeaders.UserAgent.ParseAdd "tessera"
         c)

/// GET a daxlib URL: the body, None for 404, or why it failed.
let get (url: Uri) : Result<byte array option, string> =
    match refuse url with
    | Some reason -> Error reason
    | None ->
        try
            use request = new HttpRequestMessage(HttpMethod.Get, url)
            use response = client.Value.Send request
            let status = int response.StatusCode

            if status = 404 then
                Ok None
            elif status >= 300 && status < 400 then
                Error $"HTTP {status}: redirects are not followed"
            elif not response.IsSuccessStatusCode then
                Error $"HTTP {status} {response.ReasonPhrase}"
            else
                use stream = response.Content.ReadAsStream()
                use ms = new MemoryStream()
                stream.CopyTo ms
                Ok(Some(ms.ToArray()))
        with
        | :? TaskCanceledException
        | :? OperationCanceledException -> Error $"timed out after {int timeout.TotalSeconds} s"
        | :? HttpRequestException as e -> Error e.Message
        | e -> Error e.Message
