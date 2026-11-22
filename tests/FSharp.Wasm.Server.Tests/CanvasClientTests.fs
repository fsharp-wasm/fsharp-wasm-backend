/// The canvas client of the Ghost Todo against the real application: every request of `Model.update` is answered by `GhostTodo.route` in the same process,
/// so the synchronisation (the shadow, the patches, the polling, a lost connection, a stale client) is checked without a browser or a network.
module CanvasClientTests

open System.Collections.Generic
open Xunit
open FSharp.Wasm.Data
open FSharp.Wasm.Server
open FSharp.Graphics.Widgets
open GhostTodoCanvas

/// One server, which clients talk to.  `Down`: nothing answers (status 0).
type private World() =
    let data = Dictionary<string, string>()
    let store : Store =
        { Get = fun k -> (match data.TryGetValue k with | true, v -> Some v | _ -> None)
          Put = fun k v -> data.[k] <- v
          Delete = fun k -> data.Remove k |> ignore }
    let state = GhostTodo.State()
    member val Down = false with get, set
    member val Requests = 0 with get, set

    member w.Serve(call: Call) : Reply =
        w.Requests <- w.Requests + 1
        if w.Down then { Status = 0; Body = "" }
        else
            // the client's addresses are relative to its page ("../api/state"): the server sees "/api/state"
            let target = call.Path.Substring 2
            let path, query = (match target.IndexOf '?' with -1 -> target, "" | i -> target.Substring(0, i), target.Substring(i + 1))
            // what the host itself answers (the name of the platform is a host import, which does not exist under .NET)
            if path = "/api/info" then { Status = 200; Body = "{\"module\":\"ghost-todo\",\"host\":\"test-host\"}" }
            else
                let res = GhostTodo.route state store { Method = call.Method; Path = path; Query = Http.parseQuery query; Headers = []; Body = call.Body }
                { Status = res.Status; Body = (match res.Body with Text t -> t | Bytes _ -> "") }

/// A client: the model, the timers it asked for (kept, to be fired by the test), and the effects carried out against the world.
type private Client(world: World, ?search: string) =
    let mutable model = Unchecked.defaultof<Model>
    let timers = Queue<Msg>()
    member _.Model = model
    member _.Timers = timers.Count

    member c.Dispatch(msg: Msg) =
        let m, effects = Model.update model msg
        model <- m
        for fx in effects do
            match fx with
            | Fetch(call, toMsg) -> c.Dispatch(toMsg (world.Serve call))
            | After(_, msg) -> timers.Enqueue msg

    member c.Start() =
        let m, effects = Model.init (defaultArg search "")
        model <- m
        for fx in effects do
            match fx with
            | Fetch(call, toMsg) -> c.Dispatch(toMsg (world.Serve call))
            | After(_, msg) -> timers.Enqueue msg

    /// The timers that are due now.
    member c.Fire() =
        let due = timers.ToArray()
        timers.Clear()
        for msg in due do c.Dispatch msg

    member c.Type(text: string) = for ch in text do c.Dispatch(Key(string ch))
    member c.Texts = model.Shadow.Todos |> List.map (fun t -> t.Text)

let private started () =
    let w = World()
    let c = Client(w)
    c.Start()
    w, c

[<Fact>]
let ``the client starts from the state of the server, learns the host and starts one timer`` () =
    let w, c = started ()
    Assert.Equal(Online, c.Model.Link)
    Assert.Equal(1, c.Timers)
    Assert.Equal(0u, c.Model.Shadow.Version)
    Assert.Equal("test-host", c.Model.Host)

[<Fact>]
let ``typing and Enter add a todo: the patch of the answer is applied, the field is empty again`` () =
    let w, c = started ()
    c.Type "milk"
    Assert.Equal("MILK", c.Model.Draft.ToUpperInvariant())
    c.Dispatch(Key "Enter")
    Assert.Equal<string list>([ "milk" ], c.Texts |> List.map (fun t -> t.ToLowerInvariant()))
    Assert.Equal("", c.Model.Draft)
    Assert.Equal(1u, c.Model.Shadow.Version)
    Assert.Equal(1, c.Model.Applied)
    Assert.Contains("listMutation", c.Model.Last)
    // an empty draft adds nothing and asks nothing
    let before = w.Requests
    c.Dispatch Add
    Assert.Equal(before, w.Requests)

[<Fact>]
let ``what one client does, another gets through the patches it polls`` () =
    let w, a = started ()
    let b = Client(w)
    b.Start()
    a.Type "x"
    a.Dispatch Add
    a.Type "y"
    a.Dispatch Add
    a.Dispatch(Toggle 1)
    a.Dispatch(Remove 2)
    // b has seen none of it; its timer asks for the patches since its version, and applies them in order
    b.Fire()
    Assert.Equal(a.Model.Shadow, b.Model.Shadow)
    Assert.Equal(4u, b.Model.Shadow.Version)
    Assert.Equal(4, b.Model.Applied)
    Assert.Equal(1, b.Timers)

[<Fact>]
let ``a patch that was applied from the answer of a mutation and comes again from a poll changes nothing`` () =
    let w, c = started ()
    c.Type "a"
    c.Dispatch Add
    // the poll that was already in flight when the answer came: since=0 gives the same patch again
    let stale = w.Serve { Method = "GET"; Path = "../api/patches?since=0"; Body = "" }
    let applied = c.Model.Applied
    c.Dispatch(GotPatches stale)
    Assert.Equal(applied, c.Model.Applied)
    Assert.Equal(1u, c.Model.Shadow.Version)
    Assert.Equal(Online, c.Model.Link)

[<Fact>]
let ``a gap in the patches means the state is fetched again`` () =
    let w, a = started ()
    let b = Client(w)
    b.Start()
    a.Type "one"
    a.Dispatch Add
    a.Type "two"
    a.Dispatch Add
    // b is told only about the second patch (it missed the first): its shadow cannot take it
    let all = w.Serve { Method = "GET"; Path = "../api/patches?since=0"; Body = "" }
    let second =
        match Json.tryParse all.Body |> Option.bind (Json.field "patches") with
        | Some(JArr [ _; e2 ]) -> Json.stringify (JObj [ "patches", JArr [ e2 ] ])
        | other -> failwithf "%A" other
    b.Dispatch(GotPatches { Status = 200; Body = second })
    Assert.Equal(a.Model.Shadow, b.Model.Shadow)
    Assert.Equal(2u, b.Model.Shadow.Version)

[<Fact>]
let ``a server that is gone is a state, not a failure: offline, and the timer keeps asking`` () =
    let w, c = started ()
    w.Down <- true
    c.Fire()
    Assert.Equal(Offline, c.Model.Link)
    Assert.Equal(1, c.Timers)
    w.Down <- false
    c.Fire()
    Assert.Equal(Online, c.Model.Link)
    Assert.Equal(1, c.Timers)

[<Fact>]
let ``a client that starts while the server is down tries again and ends up with one timer`` () =
    let w = World()
    w.Down <- true
    let c = Client(w)
    c.Start()
    Assert.Equal(Offline, c.Model.Link)
    w.Down <- false
    c.Fire()                                 // Sync: the state
    Assert.Equal(Online, c.Model.Link)
    Assert.Equal(1, c.Timers)
    c.Fire()                                 // Tick: the patches
    Assert.Equal(1, c.Timers)

[<Fact>]
let ``adding while offline puts the text back and says why`` () =
    let w, c = started ()
    c.Type "milk"
    w.Down <- true
    c.Dispatch Add
    Assert.NotEqual<string>("", c.Model.Draft)
    Assert.Equal("no connection to the server", c.Model.Notice)
    Assert.Empty c.Texts

[<Fact>]
let ``a stale client that acts on a todo that is gone is told, and looks again`` () =
    let w, a = started ()
    let b = Client(w)
    a.Type "x"
    a.Dispatch Add
    b.Start()
    Assert.Equal(1, b.Texts.Length)
    a.Dispatch(Remove 1)                     // b still shows it
    b.Dispatch(Toggle 1)
    Assert.Equal("not found", b.Model.Notice)
    Assert.Empty b.Texts
    Assert.Equal(a.Model.Shadow.Version, b.Model.Shadow.Version)

[<Fact>]
let ``a text over the limit cannot be typed, and the address of the page picks the room and the API`` () =
    let w, c = started ()
    c.Type (System.String('a', 210))
    Assert.Equal(200, c.Model.Draft.Length)
    let m, _ = Model.init "?room=ab%20c&api=http://localhost:8787"
    Assert.Equal("ab%20c", m.Room)
    Assert.Equal("http://localhost:8787/", m.Api)
    Assert.Equal("../", (fst (Model.init "")).Api)
    let _, fx = Model.init "?room=r1"
    match fx with
    | Fetch(call, _) :: _ -> Assert.Equal("../api/info?room=r1", call.Path)
    | other -> failwithf "%A" other
