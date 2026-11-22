namespace GhostTodoCore

open FSharp.Wasm.Data
open FSharp.Wasm.Server
open FSharp.Wasm.Server.Ggbp

/// What a client keeps of the model: the version it has applied, and the todos at that version.  A patch moves it exactly one version on.
type Shadow = { Version: uint32; Todos: Todo list }

/// Why a patch could not be applied.  All of them mean the same to a client: the shadow cannot be trusted any more, fetch the state again.
type ApplyError =
    | Undecodable of string
    | VersionGap of have: uint32 * patch: uint32
    | NoSuchTodo of id: int
    | BadIndex of int
    | BadRecord
    | Unsupported of string

/// The client side of the patch format, for the schema of Todos (the list is node 0, a todo is the node of its id): the same changes a patch of the server carries,
/// applied to a copy of the list.  `fsharp-wasm-edge/host/client/ggbp.js` does the same in JavaScript for the page that is not compiled.
module Shadow =
    let empty : Shadow = { Version = 0u; Todos = [] }

    /// The state as `GET /api/state` sends it; None when the JSON is not that.
    let ofState (j: Json) : Shadow option =
        match Json.asNumber (Json.field "version" j), Todos.items Todos.todoOfJson (Json.field "todos" j) with
        | Some v, Some todos -> Some { Version = uint32 v; Todos = todos }
        | _ -> None

    let private mapTodo (id: int) (f: Todo -> Todo) (todos: Todo list) : Todo list =
        todos |> List.map (fun t -> if t.Id = id then f t else t)

    let private applyOp (todos: Todo list) (op: PatchOp) : Result<Todo list, ApplyError> =
        match op with
        | ListMutation(node, field, listOp, index, value) when node = Todos.Root && field = Todos.ListField ->
            if listOp = ListOp.Insert then
                match Todos.decodeRecord value with
                | None -> Error BadRecord
                | Some todo ->
                    if index < 0 || index > List.length todos then Error(BadIndex index)
                    else Ok(List.truncate index todos @ [ todo ] @ List.skip index todos)
            elif listOp = ListOp.Remove then
                if index < 0 || index >= List.length todos then Error(BadIndex index)
                else Ok(List.truncate index todos @ List.skip (index + 1) todos)
            elif listOp = ListOp.Clear then Ok []
            else Error(Unsupported "list operation")
        | UpdateLeaf(id, field, wasmType, value) ->
            if not (List.exists (fun t -> t.Id = id) todos) then Error(NoSuchTodo id)
            elif field = Todos.DoneField && wasmType = WasmType.I32 && value.Length = 1 then
                Ok(mapTodo id (fun t -> { t with Done = value.[0] <> 0uy }) todos)
            elif field = Todos.TextField && wasmType = WasmType.RefString then
                Ok(mapTodo id (fun t -> { t with Text = Utf8.decode value }) todos)
            else Error(Unsupported "leaf")
        // the list mutation of the same patch has taken the todo out of the list; one that is still listed goes with its node
        | DeleteNode id -> Ok(todos |> List.filter (fun t -> t.Id <> id))
        | ReplaceBranch _
        | ListMutation _ -> Error(Unsupported "operation")

    /// One patch: it must follow the shadow's version, and every operation must apply (otherwise the shadow stays as it was).
    let apply (patch: Patch) (shadow: Shadow) : Result<Shadow, ApplyError> =
        if patch.Version <> shadow.Version + 1u then Error(VersionGap(shadow.Version, patch.Version))
        else
            patch.Ops
            |> List.fold (fun acc op -> Result.bind (fun todos -> applyOp todos op) acc) (Ok shadow.Todos)
            |> Result.map (fun todos -> { Version = patch.Version; Todos = todos })

    /// A patch as the server sends it in `patch` / `hex` (hexadecimal text of the bytes), with its size in bytes.
    let decodeHex (hex: string) : Result<Patch * int, ApplyError> =
        match Hex.decode hex with
        | None -> Error(Undecodable "not hexadecimal")
        | Some bytes ->
            match Ggbp.decode bytes with
            | Decoded patch -> Ok(patch, bytes.Length)
            | InvalidMagic -> Error(Undecodable "invalid magic")
            | Corrupted why -> Error(Undecodable why)

    let applyHex (hex: string) (shadow: Shadow) : Result<Shadow, ApplyError> =
        decodeHex hex |> Result.bind (fun (patch, _) -> apply patch shadow)

/// The server's side of the same patch format: the model one patch further, by the transition a client uses (`Shadow.apply`).  A model rebuilt from a checkpoint and the patches after it
/// is therefore the model a client holds at that version, and what a mutation persists is what it sends.
module Replay =
    /// The next id after `patch`: one more than any id it inserts (a removed todo's id is never given again).
    let private nextIdAfter (nextId: int) (patch: Patch) : int =
        patch.Ops
        |> List.fold
            (fun n op ->
                match op with
                | ListMutation(node, field, listOp, _, value) when node = Todos.Root && field = Todos.ListField && listOp = ListOp.Insert ->
                    (match Todos.decodeRecord value with
                     | Some todo -> max n (todo.Id + 1)
                     | None -> n)
                | _ -> n)
            nextId

    /// The model after the patch of `hex`; None when it is not the next version or does not apply (the log ends there).
    let step (model: Model) (hex: string) : Model option =
        match Shadow.decodeHex hex with
        | Error _ -> None
        | Ok(patch, _) ->
            match Shadow.apply patch { Version = model.Version; Todos = model.Todos } with
            | Error _ -> None
            | Ok shadow ->
                let entry = { Version = patch.Version; Transaction = patch.TransactionId; Hex = hex }
                Some { Version = shadow.Version
                       NextId = nextIdAfter model.NextId patch
                       Todos = shadow.Todos
                       Log = List.truncate Todos.MaxLog (entry :: model.Log) }

    /// The model after every consecutive patch the store holds beyond `model`'s version (it stops at the first one that is missing or does not apply).
    let rec fromStore (get: string -> string option) (model: Model) : Model =
        match get (Todos.logKey (model.Version + 1u)) |> Option.bind (step model) with
        | Some next -> fromStore get next
        | None -> model
