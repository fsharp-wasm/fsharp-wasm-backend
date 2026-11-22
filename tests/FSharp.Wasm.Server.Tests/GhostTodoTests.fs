/// The Ghost Todo application without a host: the model, its patches, and the routes with an in-memory store.
module GhostTodoTests

open System.Collections.Generic
open Xunit
open FSharp.Wasm.Data
open FSharp.Wasm.Server
open FSharp.Wasm.Server.Ggbp
open GhostTodoCore

/// A store over a dictionary, and how often it was written.
let private memory () : Store * Dictionary<string, string> =
    let d = Dictionary<string, string>()
    { Get = fun k -> (match d.TryGetValue k with | true, v -> Some v | _ -> None)
      Put = fun k v -> d.[k] <- v
      Delete = fun k -> d.Remove k |> ignore }, d

let private req meth path body : Request = { Method = meth; Path = path; Query = []; Headers = []; Body = body }
let private bodyOf (r: Response) = match r.Body with Text t -> t | Bytes b -> failwithf "bytes: %A" b
let private jsonOf (r: Response) = (Json.tryParse (bodyOf r)).Value

let private decoded (hex: string) =
    match decode (Hex.decode hex).Value with
    | Decoded p -> p
    | other -> failwithf "%A" other

// ── the model ───────────────────────────────────────────────────────────────

[<Fact>]
let ``adding gives a list insert at the end, one version step`` () =
    let model, patch, todo = (Todos.add "  buy milk " None Todos.empty).Value
    Assert.Equal(1u, model.Version)
    Assert.Equal("buy milk", todo.Text)
    Assert.Equal(2, model.NextId)
    match patch.Ops with
    | [ ListMutation(0, 0, ListOp.Insert, 0, record) ] -> Assert.Equal(Some todo, Todos.decodeRecord record)
    | ops -> failwithf "%A" ops

[<Fact>]
let ``empty and overlong texts are refused`` () =
    Assert.True((Todos.add "" None Todos.empty).IsNone)
    Assert.True((Todos.add "   \t " None Todos.empty).IsNone)
    Assert.True((Todos.add (System.String('x', 201)) None Todos.empty).IsNone)
    Assert.True((Todos.add (System.String('x', 200)) None Todos.empty).IsSome)

[<Fact>]
let ``toggle flips done with a leaf patch, remove deletes the node`` () =
    let m1, _, t = (Todos.add "a" None Todos.empty).Value
    let m2, p2, t2 = (Todos.toggle t.Id None m1).Value
    Assert.True t2.Done
    Assert.Equal<PatchOp list>([ UpdateLeaf(t.Id, 1, WasmType.I32, [| 1uy |]) ], p2.Ops)
    let m3, p3, _ = (Todos.remove t.Id None m2).Value
    Assert.Equal<PatchOp list>([ ListMutation(0, 0, ListOp.Remove, 0, [||]); DeleteNode t.Id ], p3.Ops)
    Assert.Empty m3.Todos
    Assert.True((Todos.toggle 99 None m3).IsNone)
    Assert.True((Todos.remove 99 None m3).IsNone)

[<Fact>]
let ``the transaction of the client is kept, the version is the server's`` () =
    let _, patch, _ = (Todos.add "a" (Some 77u) Todos.empty).Value
    Assert.Equal(77u, patch.TransactionId)
    Assert.Equal(1u, patch.Version)
    let _, patch2, _ = (Todos.add "b" None Todos.empty).Value
    Assert.Equal(1u, patch2.TransactionId)           // none given: the version

[<Fact>]
let ``the log keeps the last 64 patches`` () =
    let mutable model = Todos.empty
    for i in 1..100 do
        model <- (let m, _, _ = (Todos.add (string i) None model).Value in m)
    Assert.Equal(64, model.Log.Length)
    Assert.Equal(100u, model.Log.Head.Version)
    Assert.Equal(37u, (List.last model.Log).Version)

[<Fact>]
let ``patchesSince gives the missing patches oldest first, or None when the log does not reach back`` () =
    let mutable model = Todos.empty
    for i in 1..100 do
        model <- (let m, _, _ = (Todos.add (string i) None model).Value in m)
    Assert.Equal(Some [], Todos.patchesSince 100u model)
    Assert.Equal(Some [ 99u; 100u ], Todos.patchesSince 98u model |> Option.map (List.map (fun e -> e.Version)))
    // the log holds 37..100: a client at 36 needs exactly those, a client at 35 needs 36, which is gone
    Assert.Equal(Some 64, Todos.patchesSince 36u model |> Option.map List.length)
    Assert.True((Todos.patchesSince 35u model).IsNone)
    Assert.True((Todos.patchesSince 0u model).IsNone)

[<Fact>]
let ``a snapshot restores the model exactly`` () =
    let mutable model = Todos.empty
    for t in [ "one"; "zwei ✓"; "🚀 three \"quoted\"" ] do
        model <- (let m, _, _ = (Todos.add t None model).Value in m)
    model <- (let m, _, _ = (Todos.toggle 2 (Some 9u) model).Value in m)
    Assert.Equal(Some model, Todos.restore (Todos.snapshot model))

[<Fact>]
let ``restore refuses what is not a snapshot`` () =
    for bad in [ ""; "{}"; "[]"; "{\"version\":1}"; "{\"version\":1,\"nextId\":2,\"todos\":[{\"id\":1}],\"log\":[]}"; "not json" ] do
        Assert.Equal(None, Todos.restore bad)

// ── the routes ──────────────────────────────────────────────────────────────

[<Fact>]
let ``add, toggle, remove over HTTP, with the patches a client applies`` () =
    let store, _ = memory ()
    let state = GhostTodo.State()
    let r1 = GhostTodo.route state store (req "POST" "/api/todos" "{\"text\":\"hello\"}")
    Assert.Equal(201, r1.Status)
    let j1 = jsonOf r1
    Assert.Equal(Some 1.0, Json.asNumber (Json.field "version" j1))
    let patch = decoded (Json.asString (Json.field "patch" j1)).Value
    Assert.Equal(1u, patch.Version)
    let r2 = GhostTodo.route state store (req "POST" "/api/todos/1/toggle" "")
    Assert.Equal(200, r2.Status)
    let r3 = GhostTodo.route state store (req "GET" "/api/state" "")
    let todos = Json.field "todos" (jsonOf r3)
    Assert.Equal(Some "hello", todos |> Option.bind (function JArr [ t ] -> Json.asString (Json.field "text" t) | _ -> None))
    let r4 = GhostTodo.route state store (req "DELETE" "/api/todos/1" "")
    Assert.Equal(200, r4.Status)
    Assert.Equal(Some(JArr []), Json.field "todos" (jsonOf (GhostTodo.route state store (req "GET" "/api/state" ""))))

[<Fact>]
let ``bad requests are 400, unknown things 404, known paths with another method 405`` () =
    let store, _ = memory ()
    let state = GhostTodo.State()
    let status meth path body = (GhostTodo.route state store (req meth path body)).Status
    Assert.Equal(400, status "POST" "/api/todos" "{oops")
    Assert.Equal(400, status "POST" "/api/todos" "{\"nottext\":1}")
    Assert.Equal(400, status "POST" "/api/todos" "{\"text\":\"\"}")
    Assert.Equal(400, status "GET" "/api/patches" "")
    Assert.Equal(404, status "POST" "/api/todos/99/toggle" "")
    Assert.Equal(404, status "DELETE" "/api/todos/abc" "")
    Assert.Equal(404, status "GET" "/api/nope" "")
    Assert.Equal(404, status "GET" "/elsewhere" "")
    Assert.Equal(405, status "DELETE" "/healthz" "")
    Assert.Equal(405, status "GET" "/api/todos" "")
    Assert.Equal(405, status "PUT" "/api/state" "")
    Assert.Equal(204, status "OPTIONS" "/api/todos" "")
    Assert.Equal(200, status "GET" "/healthz" "")

/// A script of mutations over HTTP: n adds, then toggles and removes in turn.
let private mutate (state: GhostTodo.State) (store: Store) (n: int) =
    for i in 1 .. n do
        GhostTodo.route state store (req "POST" "/api/todos" (sprintf "{\"text\":\"todo %d\"}" i)) |> ignore
        if i % 3 = 0 then GhostTodo.route state store (req "POST" (sprintf "/api/todos/%d/toggle" (i - 1)) "") |> ignore
        if i % 5 = 0 then GhostTodo.route state store (req "DELETE" (sprintf "/api/todos/%d" (i - 2)) "") |> ignore

let private coldStart (store: Store) : GhostTodo.State =
    let s = GhostTodo.State()
    s.EnsureLoaded store
    s

[<Fact>]
let ``a mutation persists its patch, a few dozen bytes however long the list, and not the state`` () =
    let store, d = memory ()
    let state = GhostTodo.State()
    mutate state store 300
    Assert.False(d.ContainsKey "state")                                   // the checkpoint is written by `compact`, not by a request
    let logKeys = d.Keys |> Seq.filter (fun k -> k.StartsWith "log/") |> Seq.toList
    Assert.Equal(int state.Model.Version, logKeys.Length)
    Assert.True(d.Values |> Seq.forall (fun v -> v.Length < 200), "every persisted value is one patch")
    let first = decoded d.["log/1"]
    Assert.Equal(1u, first.Version)

[<Fact>]
let ``a cold start replays the patches after the checkpoint and holds the model of the instance that wrote them`` () =
    let store, _ = memory ()
    let first = GhostTodo.State()
    mutate first store 40
    Assert.Equal(first.Model, (coldStart store).Model)
    Assert.Contains("todo 40", bodyOf (GhostTodo.route (coldStart store) store (req "GET" "/api/state" "")))
    // Reset makes an instance read the store again (a stateless platform whose instances share it)
    let second = coldStart store
    GhostTodo.route second store (req "POST" "/api/todos" "{\"text\":\"two\"}") |> ignore
    first.Reset()
    Assert.Contains("two", bodyOf (GhostTodo.route first store (req "GET" "/api/state" "")))

[<Fact>]
let ``folding the log into a checkpoint writes the state and drops the entries it covers`` () =
    let store, d = memory ()
    let state = GhostTodo.State()
    mutate state store 45                                                 // 45 adds, 15 toggles and 9 removes: 69 versions, beyond CompactEvery
    Assert.True(state.CompactionDue)
    GhostTodo.compact state store
    Assert.True(d.ContainsKey "state")
    Assert.Empty(d.Keys |> Seq.filter (fun k -> k.StartsWith "log/"))
    Assert.False(state.CompactionDue)
    Assert.Equal(state.Model.Version, state.Checkpoint)
    Assert.Equal(state.Model, (coldStart store).Model)                    // the checkpoint alone is the model (it carries the log window)
    mutate state store 3                                                  // entries after the checkpoint, on top of it
    Assert.Equal(state.Model, (coldStart store).Model)
    GhostTodo.compact state store                                         // not due: nothing is written or dropped
    Assert.Equal(4, d.Keys |> Seq.filter (fun k -> k.StartsWith "log/") |> Seq.length)   // the 3 adds and the toggle

[<Fact>]
let ``a removed todo's id is not given again after a restart, with or without a checkpoint`` () =
    let store, _ = memory ()
    let state = GhostTodo.State()
    for t in [ "a"; "b" ] do GhostTodo.route state store (req "POST" "/api/todos" (sprintf "{\"text\":\"%s\"}" t)) |> ignore
    GhostTodo.route state store (req "DELETE" "/api/todos/2" "") |> ignore
    Assert.Equal(3, (coldStart store).Model.NextId)                       // from the log alone
    mutate state store 45
    GhostTodo.compact state store
    Assert.True(state.Checkpoint > 0u)
    Assert.Equal(state.Model.NextId, (coldStart store).Model.NextId)      // from the checkpoint

[<Fact>]
let ``a log that has a gap or a corrupt entry ends there, and the next mutation continues from that version`` () =
    let store, d = memory ()
    let state = GhostTodo.State()
    mutate state store 8
    let full = state.Model.Version
    Assert.True(full > 5u)
    d.Remove "log/4" |> ignore
    Assert.Equal(3u, (coldStart store).Model.Version)                     // 1, 2, 3 replay; 5 and later are beyond the gap
    d.["log/4"] <- "not hexadecimal"
    Assert.Equal(3u, (coldStart store).Model.Version)
    let again = coldStart store
    let r = GhostTodo.route again store (req "POST" "/api/todos" "{\"text\":\"after the gap\"}")
    Assert.Equal(Some 4.0, Json.asNumber (Json.field "version" (jsonOf r)))   // it takes the place of the lost entry
    Assert.Equal(again.Model, (coldStart store).Model)                    // the entries of the old line of history behind the gap are gone, not replayed as its continuation
    Assert.False(d.ContainsKey "log/5")

[<Fact>]
let ``patches since a version, and the binary patch`` () =
    let store, _ = memory ()
    let state = GhostTodo.State()
    for t in [ "a"; "b"; "c" ] do GhostTodo.route state store (req "POST" "/api/todos" (sprintf "{\"text\":\"%s\"}" t)) |> ignore
    let r = GhostTodo.route state store { req "GET" "/api/patches" "" with Query = [ "since", "1" ] }
    match Json.field "patches" (jsonOf r) with
    | Some(JArr patches) -> Assert.Equal(2, patches.Length)
    | other -> failwithf "%A" other
    let binary = GhostTodo.route state store (req "GET" "/api/patch/2" "")
    match binary.Body with
    | Bytes b -> Assert.Equal(Magic, b.[0])
    | other -> failwithf "%A" other
    Assert.Equal(404, (GhostTodo.route state store (req "GET" "/api/patch/99" "")).Status)

[<Fact>]
let ``the transaction header is passed to the patch`` () =
    let store, _ = memory ()
    let state = GhostTodo.State()
    let r = GhostTodo.route state store { req "POST" "/api/todos" "{\"text\":\"x\"}" with Headers = [ "X-Transaction", "123" ] }
    Assert.Equal(Some 123.0, Json.asNumber (Json.field "transaction" (jsonOf r)))

[<Fact>]
let ``responses carry the CORS header`` () =
    let store, _ = memory ()
    let r = GhostTodo.route (GhostTodo.State()) store (req "GET" "/api/state" "")
    Assert.Contains(("access-control-allow-origin", "*"), r.Headers)
