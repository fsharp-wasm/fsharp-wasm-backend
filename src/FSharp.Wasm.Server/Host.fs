namespace FSharp.Wasm.Server

open Fable.Core

/// The functions the host (server/runtime/server.js) provides to a module: the request, the response, the store and a few facts.
/// Every function is an import of "runtime/server.js"; strings cross as JS strings (the engine's string builtins, or the polyfill the
/// host passes where an engine has none); numbers cross as numbers.  A module can send strings out but not receive them, so the
/// request is read by calling these, and a binary body leaves byte by byte.  Under .NET they are `nativeOnly` stubs: tests call the
/// handler of the application directly instead of `Host.run`.
module HostImports =
    [<Import("reqMethod", "runtime/server.js")>]
    let reqMethod () : string = nativeOnly
    [<Import("reqPath", "runtime/server.js")>]
    let reqPath () : string = nativeOnly
    /// The raw query string without the "?".
    [<Import("reqQuery", "runtime/server.js")>]
    let reqQuery () : string = nativeOnly
    /// "name\nvalue\nname\nvalue…" — all headers of the request, lower-case names.
    [<Import("reqHeaders", "runtime/server.js")>]
    let reqHeaders () : string = nativeOnly
    [<Import("reqBody", "runtime/server.js")>]
    let reqBody () : string = nativeOnly

    [<Import("resStatus", "runtime/server.js")>]
    let resStatus (code: int) : unit = nativeOnly
    [<Import("resHeader", "runtime/server.js")>]
    let resHeader (name: string) (value: string) : unit = nativeOnly
    [<Import("resText", "runtime/server.js")>]
    let resText (body: string) : unit = nativeOnly
    /// A binary body: resBytesBegin, one resByte per byte, resBytesEnd.
    [<Import("resBytesBegin", "runtime/server.js")>]
    let resBytesBegin (length: int) : unit = nativeOnly
    [<Import("resByte", "runtime/server.js")>]
    let resByte (b: int) : unit = nativeOnly
    [<Import("resBytesEnd", "runtime/server.js")>]
    let resBytesEnd () : unit = nativeOnly

    /// The value stored under `key`, or "" (a store never holds the empty string).
    [<Import("storeGet", "runtime/server.js")>]
    let storeGet (key: string) : string = nativeOnly
    [<Import("storePut", "runtime/server.js")>]
    let storePut (key: string) (value: string) : unit = nativeOnly
    [<Import("storeDelete", "runtime/server.js")>]
    let storeDelete (key: string) : unit = nativeOnly

    /// Where this module runs: "node", "bun", "deno", "cloudflare-workers", "vercel-edge", …
    [<Import("hostName", "runtime/server.js")>]
    let hostName () : string = nativeOnly
    /// Milliseconds since the epoch.
    [<Import("nowMs", "runtime/server.js")>]
    let nowMs () : float = nativeOnly
    [<Import("log", "runtime/server.js")>]
    let log (message: string) : unit = nativeOnly

/// The key-value store a handler persists its state in.  `Get` gives None for a missing key.
type Store =
    { Get: string -> string option
      Put: string -> string -> unit
      Delete: string -> unit }

module Host =
    /// The store of the host: what the platform provides (Durable Object storage, Deno KV, a file, memory).
    let store : Store =
        { Get = fun key -> (match HostImports.storeGet key with | "" -> None | v -> Some v)
          Put = fun key value -> HostImports.storePut key value
          Delete = fun key -> HostImports.storeDelete key }

    let private parseQuery (raw: string) : (string * string) list =
        if raw = "" then []
        else
            raw.Split('&')
            |> Array.filter (fun s -> s <> "")
            |> Array.map (fun pair ->
                match pair.IndexOf '=' with
                | -1 -> pair, ""
                | i -> pair.Substring(0, i), pair.Substring(i + 1))
            |> List.ofArray

    let private parseHeaders (raw: string) : (string * string) list =
        let parts = raw.Split('\n')
        [ for i in 0 .. 2 .. parts.Length - 2 -> parts.[i], parts.[i + 1] ]

    /// The request the host is delivering.
    let request () : Request =
        { Method = HostImports.reqMethod ()
          Path = HostImports.reqPath ()
          Query = parseQuery (HostImports.reqQuery ())
          Headers = parseHeaders (HostImports.reqHeaders ())
          Body = HostImports.reqBody () }

    /// Hand a response to the host.
    let respond (res: Response) : unit =
        HostImports.resStatus res.Status
        for (name, value) in res.Headers do HostImports.resHeader name value
        match res.Body with
        | Text s -> HostImports.resText s
        | Bytes b ->
            HostImports.resBytesBegin b.Length
            for x in b do HostImports.resByte (int x)
            HostImports.resBytesEnd ()

    /// Read the request, run the application, hand over the response — what the exported `handle` of a module does.
    let run (app: Request -> Response) : unit = respond (app (request ()))
