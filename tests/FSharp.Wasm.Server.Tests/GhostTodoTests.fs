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

[<Fact>]
let ``the state is persisted after a mutation and loaded by a new instance`` () =
    let store, d = memory ()
    let first = GhostTodo.State()
    GhostTodo.route first store (req "POST" "/api/todos" "{\"text\":\"keep me\"}") |> ignore
    Assert.True(d.ContainsKey "state")
    let second = GhostTodo.State()                      // a cold start: nothing in memory, the store has it
    let r = GhostTodo.route second store (req "GET" "/api/state" "")
    Assert.Contains("keep me", bodyOf r)
    // Reset makes an instance read the store again (a stateless platform whose instances share it)
    GhostTodo.route second store (req "POST" "/api/todos" "{\"text\":\"two\"}") |> ignore
    first.Reset()
    Assert.Contains("two", bodyOf (GhostTodo.route first store (req "GET" "/api/state" "")))

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
