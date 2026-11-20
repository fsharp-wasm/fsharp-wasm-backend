/// The module that becomes GhostTodo.wasm: a todo list that talks the GGBP wire format, on every backend.
///
///   GET    /api/info                the host, the version
///   GET    /api/state               { version, todos }         — the state a client starts from
///   POST   /api/todos               { "text": "…" }            -> { version, transaction, patch, todo }
///   POST   /api/todos/:id/toggle                               -> { version, transaction, patch, todo }
///   DELETE /api/todos/:id                                      -> { version, transaction, patch, todo }
///   GET    /api/patches?since=N     { version, patches: [{ version, transaction, hex }] }  — or { resync: true } when N is too old
///   GET    /api/patch/:version      the GGBP patch of that version as bytes
///   GET    /healthz
/// `patch` is the GGBP patch in hex; a mutation may carry the id of the client's transaction in the header x-transaction.
/// The export `handle` is what the host calls for every request; `reset` makes the next request reload the state from the store
/// (what a host on a stateless platform calls before each request).
module GhostTodo

open FSharp.Wasm.Data
open FSharp.Wasm.Server
open GhostTodoCore

/// The state of the module, in one object (a module-level `let mutable` is a wasm global; this keeps it together).
type State() =
    let mutable loaded = false
    let mutable model = Todos.empty

    member _.Model = model
    member _.Reset() = loaded <- false
    member _.Set(m: Model) = model <- m
    /// Load the persisted state once; a snapshot that does not parse is ignored (the list starts empty).
    member _.EnsureLoaded(store: Store) =
        if not loaded then
            loaded <- true
            match store.Get "state" |> Option.bind Todos.restore with
            | Some m -> model <- m
            | None -> ()

let private mutation (model: Model) (patch: Ggbp.Patch) (todo: Todo) : Json =
    JObj [ "version", JNum(float model.Version)
           "transaction", JNum(float patch.TransactionId)
           "patch", JStr(Hex.encode (Ggbp.encode patch))
           "todo", Todos.todoJson todo ]

/// Persist what a mutation produced and answer.
let private commit (state: State) (store: Store) (status: int) (result: (Model * Ggbp.Patch * Todo) option) : Response =
    match result with
    | None -> Http.notFound
    | Some(model, patch, todo) ->
        state.Set model
        store.Put "state" (Todos.snapshot model)
        Http.json status (mutation model patch todo)

let private transaction (req: Request) : uint32 option =
    Http.header "x-transaction" req |> Option.bind Numbers.parseUInt32

/// The paths the application answers (with some method): what is 405 rather than 404 when the method is not one of theirs.
let private isRoute (segments: string list) : bool =
    match segments with
    | [ "healthz" ] | [ "api"; "info" ] | [ "api"; "state" ] | [ "api"; "todos" ] | [ "api"; "patches" ] -> true
    | [ "api"; "patch"; _ ] | [ "api"; "todos"; _ ] | [ "api"; "todos"; _; "toggle" ] -> true
    | _ -> false

/// The application: a pure function of the state, the store and the request (the tests call it without a host).
let route (state: State) (store: Store) (req: Request) : Response =
    state.EnsureLoaded store
    let model = state.Model
    let txn = transaction req
    let response =
        match req.Method, Http.segments req.Path with
        | "OPTIONS", _ ->
            { Status = 204
              Headers = [ "access-control-allow-methods", "GET, POST, DELETE, OPTIONS"
                          "access-control-allow-headers", "content-type, x-transaction" ]
              Body = Text "" }
        | "GET", [ "healthz" ] -> Http.text 200 "ok"
        | "GET", [ "api"; "info" ] ->
            Http.json 200 (JObj [ "module", JStr "ghost-todo"
                                  "host", JStr(HostImports.hostName ())
                                  "version", JNum(float model.Version)
                                  "todos", JNum(float (List.length model.Todos)) ])
        | "GET", [ "api"; "state" ] -> Http.json 200 (Todos.stateJson model)
        | "POST", [ "api"; "todos" ] ->
            match Json.tryParse req.Body |> Option.bind (Json.field "text") |> Json.asString with
            | None -> Http.badRequest "expected {\"text\": \"…\"}"
            | Some text ->
                match Todos.add text txn model with
                | None -> Http.badRequest "the text must not be empty or longer than 200 characters"
                | added -> commit state store 201 added
        | "POST", [ "api"; "todos"; id; "toggle" ] ->
            match Numbers.parseInt id with
            | Some n -> commit state store 200 (Todos.toggle n txn model)
            | None -> Http.notFound
        | "DELETE", [ "api"; "todos"; id ] ->
            match Numbers.parseInt id with
            | Some n -> commit state store 200 (Todos.remove n txn model)
            | None -> Http.notFound
        | "GET", [ "api"; "patches" ] ->
            let since = Http.query "since" req |> Option.bind Numbers.parseUInt32
            match since with
            | None -> Http.badRequest "expected ?since=<version>"
            | Some s ->
                match Todos.patchesSince s model with
                | Some entries ->
                    Http.json 200 (JObj [ "version", JNum(float model.Version); "patches", JArr(List.map Todos.entryJson entries) ])
                | None -> Http.json 200 (JObj [ "version", JNum(float model.Version); "resync", JBool true ])
        | "GET", [ "api"; "patch"; v ] ->
            match Numbers.parseInt v |> Option.bind (fun n -> model.Log |> List.tryFind (fun e -> int e.Version = n)) |> Option.bind (fun e -> Hex.decode e.Hex) with
            | Some bytes -> Http.bytes 200 bytes
            | None -> Http.notFound
        // a path that exists, with a method it does not answer, is 405; anything else is 404
        | _, segments when isRoute segments -> Http.methodNotAllowed
        | _ -> Http.notFound
    Http.withHeader "access-control-allow-origin" "*" response

let private state = State()

[<CompiledName("handle")>]
let handle () : unit = Host.run (route state Host.store)

[<CompiledName("reset")>]
let reset () : unit = state.Reset()
