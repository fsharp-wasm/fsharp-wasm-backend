/// What a client keeps of the Ghost Todo: the patches of the server, applied to a copy of the list, give the list of the server.
module ShadowTests

open Xunit
open FSharp.Wasm.Data
open FSharp.Wasm.Server
open FSharp.Wasm.Server.Ggbp
open GhostTodoCore

/// The model one step on, with the patch of that step.
let private step (model: Model, patches: Patch list) (op: Model -> (Model * Patch * Todo) option) =
    let model', patch, _ = (op model).Value
    model', patches @ [ patch ]

let private replay (patches: Patch list) =
    patches |> List.fold (fun s p -> Result.bind (Shadow.apply p) s) (Ok Shadow.empty)

[<Fact>]
let ``the patches of a session, applied in order, give the list of the server`` () =
    let model, patches =
        [ Todos.add "milk" None; Todos.add "bread" None; Todos.add "ünïcode ✓" None
          Todos.toggle 2 None; Todos.remove 1 None; Todos.add "later" None; Todos.toggle 3 None; Todos.toggle 2 None ]
        |> List.fold step (Todos.empty, [])
    match replay patches with
    | Ok shadow ->
        Assert.Equal(model.Version, shadow.Version)
        Assert.Equal<Todo list>(model.Todos, shadow.Todos)
    | Error e -> failwithf "%A" e

[<Fact>]
let ``any session of adds, toggles and removes ends with the shadow equal to the model`` () =
    // a deterministic pseudo-random walk: 300 steps over a list that grows and shrinks
    let mutable seed = 12345u
    let next (n: int) = seed <- seed * 1664525u + 1013904223u; int ((seed >>> 8) % uint32 n)
    let mutable model = Todos.empty
    let mutable shadow = Shadow.empty
    for i in 1 .. 300 do
        let ids = model.Todos |> List.map (fun t -> t.Id)
        let result =
            match next 3, ids with
            | 0, _ | _, [] -> Todos.add (sprintf "todo %d" i) None model
            | 1, _ -> Todos.toggle (List.item (next ids.Length) ids) None model
            | _ -> Todos.remove (List.item (next ids.Length) ids) None model
        let model', patch, _ = result.Value
        model <- model'
        shadow <- (Shadow.apply patch shadow) |> function Ok s -> s | Error e -> failwithf "step %d: %A" i e
        Assert.Equal<Todo list>(model.Todos, shadow.Todos)
    Assert.Equal(300u, shadow.Version)

[<Fact>]
let ``a patch that does not follow the version is refused and the shadow stays`` () =
    let m1, p1, _ = (Todos.add "a" None Todos.empty).Value
    let _, p2, _ = (Todos.add "b" None m1).Value
    Assert.Equal(Error(VersionGap(0u, 2u)), Shadow.apply p2 Shadow.empty)
    let shadow = (Shadow.apply p1 Shadow.empty) |> function Ok s -> s | Error e -> failwithf "%A" e
    Assert.Equal(Error(VersionGap(1u, 1u)), Shadow.apply p1 shadow)

[<Fact>]
let ``what cannot be applied is an error, never a half applied patch`` () =
    let m1, p1, _ = (Todos.add "a" None Todos.empty).Value
    let shadow = (Shadow.apply p1 Shadow.empty) |> function Ok s -> s | Error e -> failwithf "%A" e
    let patch ops = { Version = 2u; TransactionId = 2u; Ops = ops }
    // the first operation is good, the second names a todo that is not there: the good one must not show
    Assert.Equal(Error(NoSuchTodo 99),
                 Shadow.apply (patch [ UpdateLeaf(1, Todos.DoneField, WasmType.I32, [| 1uy |]); UpdateLeaf(99, Todos.DoneField, WasmType.I32, [| 1uy |]) ]) shadow)
    Assert.Equal(Error(BadIndex 5), Shadow.apply (patch [ ListMutation(Todos.Root, Todos.ListField, ListOp.Remove, 5, [||]) ]) shadow)
    Assert.Equal(Error(BadIndex 3), Shadow.apply (patch [ ListMutation(Todos.Root, Todos.ListField, ListOp.Insert, 3, Todos.encodeRecord { Id = 2; Text = "x"; Done = false }) ]) shadow)
    Assert.Equal(Error BadRecord, Shadow.apply (patch [ ListMutation(Todos.Root, Todos.ListField, ListOp.Insert, 0, [||]) ]) shadow)
    Assert.Equal(Error(Unsupported "operation"), Shadow.apply (patch [ ReplaceBranch(1, [||]) ]) shadow)
    Assert.Equal(Error(Unsupported "leaf"), Shadow.apply (patch [ UpdateLeaf(1, 7, WasmType.I32, [| 1uy |]) ]) shadow)
    Assert.Equal(1u, shadow.Version)

[<Fact>]
let ``the text of a todo can be a leaf of a patch, and a node that is deleted twice is gone`` () =
    let m1, p1, _ = (Todos.add "old" None Todos.empty).Value
    let shadow = (Shadow.apply p1 Shadow.empty) |> function Ok s -> s | Error e -> failwithf "%A" e
    let renamed = { Version = 2u; TransactionId = 2u; Ops = [ UpdateLeaf(1, Todos.TextField, WasmType.RefString, Utf8.encode "new ✓") ] }
    let s2 = (Shadow.apply renamed shadow) |> function Ok s -> s | Error e -> failwithf "%A" e
    Assert.Equal("new ✓", s2.Todos.Head.Text)
    let gone = { Version = 3u; TransactionId = 3u; Ops = [ ListMutation(Todos.Root, Todos.ListField, ListOp.Remove, 0, [||]); DeleteNode 1 ] }
    Assert.True((Shadow.apply gone s2) |> function Ok s -> s.Todos.IsEmpty | Error _ -> false)
    let cleared = { Version = 3u; TransactionId = 3u; Ops = [ ListMutation(Todos.Root, Todos.ListField, ListOp.Clear, 0, [||]) ] }
    Assert.True((Shadow.apply cleared s2) |> function Ok s -> s.Todos.IsEmpty | Error _ -> false)

[<Fact>]
let ``a patch arrives as hex, and what is not a patch is an error`` () =
    let _, p1, _ = (Todos.add "a" None Todos.empty).Value
    let hex = Hex.encode (Ggbp.encode p1)
    Assert.Equal(Ok { Version = 1u; Todos = [ { Id = 1; Text = "a"; Done = false } ] }, Shadow.applyHex hex Shadow.empty)
    Assert.Equal(Error(Undecodable "not hexadecimal"), Shadow.applyHex "xyz" Shadow.empty)
    Assert.Equal(Error(Undecodable "invalid magic"), Shadow.applyHex "00" Shadow.empty)
    Assert.True(Shadow.applyHex "47" Shadow.empty |> function Error(Undecodable _) -> true | _ -> false)

[<Fact>]
let ``the state of the server is the shadow it starts from`` () =
    let model =
        [ Todos.add "a" None; Todos.add "b" None; Todos.toggle 1 None ] |> List.fold step (Todos.empty, []) |> fst
    let state = Todos.stateJson model |> Json.stringify |> Json.tryParse |> Option.bind Shadow.ofState
    Assert.Equal(Some { Version = 3u; Todos = model.Todos }, state)
    Assert.Equal(None, Json.tryParse "{\"version\":1}" |> Option.bind Shadow.ofState)
    Assert.Equal(None, Json.tryParse "{\"version\":1,\"todos\":[{\"id\":1}]}" |> Option.bind Shadow.ofState)
