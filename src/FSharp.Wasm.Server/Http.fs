namespace FSharp.Wasm.Server

open FSharp.Wasm.Data

/// The body of a response: text (JSON, HTML) or bytes (a binary patch).
type Payload =
    | Text of string
    | Bytes of byte[]

/// What a handler sees of a request.  The host has read the body (a handler is synchronous); `Query` is decoded.
type Request =
    { Method: string
      Path: string
      Query: (string * string) list
      Headers: (string * string) list
      Body: string }

type Response =
    { Status: int
      Headers: (string * string) list
      Body: Payload }

module Http =
    /// The non-empty segments of a path:  "/api/todos/3"  ->  ["api"; "todos"; "3"].
    let segments (path: string) : string list =
        path.Split('/') |> Array.filter (fun s -> s <> "") |> List.ofArray

    /// `a=1&b=&c` -> ["a", "1"; "b", ""; "c", ""] (the text between the pairs and after the `=` as it is: a host decodes before, a page's `location.search` is the raw one).
    let parseQuery (raw: string) : (string * string) list =
        if raw = "" then []
        else
            raw.Split('&')
            |> Array.filter (fun s -> s <> "")
            |> Array.map (fun pair ->
                match pair.IndexOf '=' with
                | -1 -> pair, ""
                | i -> pair.Substring(0, i), pair.Substring(i + 1))
            |> List.ofArray

    let header (name: string) (req: Request) : string option =
        let lower = name.ToLowerInvariant()
        req.Headers |> List.tryFind (fun (k, _) -> k.ToLowerInvariant() = lower) |> Option.map snd

    let query (name: string) (req: Request) : string option =
        req.Query |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

    let respond (status: int) (contentType: string) (body: Payload) : Response =
        { Status = status; Headers = [ "content-type", contentType ]; Body = body }

    let text (status: int) (s: string) = respond status "text/plain; charset=utf-8" (Text s)
    let html (s: string) = respond 200 "text/html; charset=utf-8" (Text s)
    let json (status: int) (v: Json) = respond status "application/json; charset=utf-8" (Text(Json.stringify v))
    let bytes (status: int) (b: byte[]) = respond status "application/octet-stream" (Bytes b)
    let notFound = json 404 (JObj [ "error", JStr "not found" ])
    let badRequest (message: string) = json 400 (JObj [ "error", JStr message ])
    let methodNotAllowed = json 405 (JObj [ "error", JStr "method not allowed" ])
    let withHeader (name: string) (value: string) (r: Response) = { r with Headers = r.Headers @ [ name, value ] }
