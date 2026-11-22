namespace GhostTodoCore

open FSharp.Wasm.Data
open FSharp.Wasm.Server
open FSharp.Wasm.Server.Ggbp

type Todo = { Id: int; Text: string; Done: bool }

/// One applied mutation as it can be fetched again: the version it produced, its transaction, and the GGBP patch in hex.
type LogEntry = { Version: uint32; Transaction: uint32; Hex: string }

/// The authoritative model (the "ghost graph" of this demo): a list of todos.  `Log` holds the last patches, newest first.
type Model =
    { Version: uint32
      NextId: int
      Todos: Todo list
      Log: LogEntry list }

/// The model as a graph, the way GGBP addresses it:
///   node 0         the list; field 0 holds the todos
///   node <todo id> a todo; field 0 = text (RefString), field 1 = done (I32)
/// A mutation is one patch, one version step:
///   add     ListMutation(0, 0, Insert, index, record)           record = id (varint) | done (1) | text (UTF-8)
///   toggle  UpdateLeaf(id, 1, I32, [0|1])
///   remove  ListMutation(0, 0, Remove, index, [])  +  DeleteNode id
module Todos =
    [<Literal>]
    let Root = 0
    [<Literal>]
    let ListField = 0
    [<Literal>]
    let TextField = 0
    [<Literal>]
    let DoneField = 1
    [<Literal>]
    let MaxLog = 64
    [<Literal>]
    let MaxTextLength = 200

    let empty : Model = { Version = 0u; NextId = 1; Todos = []; Log = [] }

    /// The payload of an Insert: what a client needs to build the todo.
    let encodeRecord (t: Todo) : byte[] =
        let w = ByteWriter()
        w.Varint t.Id
        w.Byte(if t.Done then 1uy else 0uy)
        w.Bytes(Utf8.encode t.Text)
        w.ToArray()

    let decodeRecord (data: byte[]) : Todo option =
        let r = ByteReader data
        let id = r.Varint()
        let isDone = r.Byte() <> 0uy
        if r.Failed then None
        else Some { Id = id; Text = Utf8.decode (r.Bytes r.Remaining); Done = isDone }

    /// Apply a mutation: the model one version further, the patch that says what changed, and the log with it.
    let private commit (model: Model) (transaction: uint32 option) (nextId: int) (todos: Todo list) (ops: PatchOp list) : Model * Patch =
        let version = model.Version + 1u
        let patch = { Version = version; TransactionId = defaultArg transaction version; Ops = ops }
        let entry = { Version = version; Transaction = patch.TransactionId; Hex = Hex.encode (Ggbp.encode patch) }
        { Version = version; NextId = nextId; Todos = todos; Log = List.truncate MaxLog (entry :: model.Log) }, patch

    /// None for an empty or too long text.
    let add (text: string) (transaction: uint32 option) (model: Model) : (Model * Patch * Todo) option =
        let trimmed = text.Trim()
        if trimmed = "" || trimmed.Length > MaxTextLength then None
        else
            let todo = { Id = model.NextId; Text = trimmed; Done = false }
            let op = ListMutation(Root, ListField, ListOp.Insert, List.length model.Todos, encodeRecord todo)
            let model', patch = commit model transaction (model.NextId + 1) (model.Todos @ [ todo ]) [ op ]
            Some(model', patch, todo)

    let toggle (id: int) (transaction: uint32 option) (model: Model) : (Model * Patch * Todo) option =
        match List.tryFind (fun t -> t.Id = id) model.Todos with
        | None -> None
        | Some t ->
            let t' = { t with Done = not t.Done }
            let todos = model.Todos |> List.map (fun x -> if x.Id = id then t' else x)
            let op = UpdateLeaf(id, DoneField, WasmType.I32, [| (if t'.Done then 1uy else 0uy) |])
            let model', patch = commit model transaction model.NextId todos [ op ]
            Some(model', patch, t')

    let remove (id: int) (transaction: uint32 option) (model: Model) : (Model * Patch * Todo) option =
        match List.tryFindIndex (fun t -> t.Id = id) model.Todos with
        | None -> None
        | Some index ->
            let t = List.item index model.Todos
            let todos = model.Todos |> List.filter (fun x -> x.Id <> id)
            let ops = [ ListMutation(Root, ListField, ListOp.Remove, index, [||]); DeleteNode id ]
            let model', patch = commit model transaction model.NextId todos ops
            Some(model', patch, t)

    let todoJson (t: Todo) : Json = JObj [ "id", JNum(float t.Id); "text", JStr t.Text; "done", JBool t.Done ]

    let entryJson (e: LogEntry) : Json =
        JObj [ "version", JNum(float e.Version); "transaction", JNum(float e.Transaction); "hex", JStr e.Hex ]

    /// The state a client fetches to start (or to resynchronise): the version its next patch follows.
    let stateJson (m: Model) : Json =
        JObj [ "version", JNum(float m.Version); "todos", JArr(List.map todoJson m.Todos) ]

    /// What is persisted: the state, the next id and the log.
    let snapshot (m: Model) : string =
        Json.stringify (
            JObj [ "version", JNum(float m.Version)
                   "nextId", JNum(float m.NextId)
                   "todos", JArr(List.map todoJson m.Todos)
                   "log", JArr(List.map entryJson m.Log) ]
        )

    let internal todoOfJson (j: Json) : Todo option =
        match Json.asNumber (Json.field "id" j), Json.asString (Json.field "text" j), Json.asBool (Json.field "done" j) with
        | Some id, Some text, Some isDone -> Some { Id = int id; Text = text; Done = isDone }
        | _ -> None

    let private entryOfJson (j: Json) : LogEntry option =
        match Json.asNumber (Json.field "version" j), Json.asNumber (Json.field "transaction" j), Json.asString (Json.field "hex" j) with
        | Some v, Some t, Some hex -> Some { Version = uint32 v; Transaction = uint32 t; Hex = hex }
        | _ -> None

    let internal items (convert: Json -> 'a option) (j: Json option) : 'a list option =
        match j with
        | Some(JArr xs) ->
            let converted = List.map convert xs
            if List.forall Option.isSome converted then Some(List.choose id converted) else None
        | _ -> None

    /// None when the text is not a snapshot of this model.
    let restore (text: string) : Model option =
        match Json.tryParse text with
        | None -> None
        | Some j ->
            match Json.asNumber (Json.field "version" j), Json.asNumber (Json.field "nextId" j),
                  items todoOfJson (Json.field "todos" j), items entryOfJson (Json.field "log" j) with
            | Some v, Some n, Some todos, Some log -> Some { Version = uint32 v; NextId = int n; Todos = todos; Log = log }
            | _ -> None

    /// The log entries after `since`, oldest first; None when the log no longer reaches back that far (the client must fetch the state).
    let patchesSince (since: uint32) (m: Model) : LogEntry list option =
        if since >= m.Version then Some []
        else
            let wanted = m.Log |> List.filter (fun e -> e.Version > since) |> List.rev
            match wanted with
            | first :: _ when first.Version = since + 1u && uint32 (List.length wanted) = m.Version - since -> Some wanted
            | _ -> None
