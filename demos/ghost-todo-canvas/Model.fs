namespace GhostTodoCanvas

open FSharp.Wasm.Data
open FSharp.Wasm.Server
open FSharp.Wasm.Server.Ggbp
open FSharp.Graphics.Widgets
open GhostTodoCore

/// What a request got back: the status (0 when no answer arrived at all: the network failed) and the body as text.
type Reply = { Status: int; Body: string }

/// A request the program wants made.  `Path` is below the address of the API (`Model.Api`).
type Call = { Method: string; Path: string; Body: string }

/// What the program asks of the world.  The answer of each comes back as a message, so `update` is a pure function of the model and the message
/// and the whole synchronisation can be tested without a browser or a server.
type Fx<'Msg> =
    | Fetch of Call * (Reply -> 'Msg)
    | After of delayMs: int * 'Msg

/// Whether the server answers.
type Link =
    | Connecting
    | Online
    | Offline

type Msg =
    | UiMsg of UiMsg
    /// A key that the toolkit did not use itself (typing into the field).
    | Key of string
    | Add
    | Toggle of int
    | Remove of int
    | Scrolled of float32
    // answers
    | GotInfo of Reply
    | GotState of Reply
    | GotAdded of text: string * Reply
    | GotMutation of Reply
    | GotPatches of Reply
    // timers
    | Sync
    | Tick

type Model =
    { /// The list as this client holds it: the server's list at `Shadow.Version`, kept current by the patches.
      Shadow: Shadow
      /// Where the API is, relative to the page (or an absolute address): ends with "/".
      Api: string
      /// The room of the page (`?room=`), as it was written in the address; "" for the default one.
      Room: string
      Host: string
      Draft: string
      Ui: UiState
      Link: Link
      /// The timer that asks for patches is running (there is exactly one).
      Polling: bool
      /// How many patches were applied, and what the last one was.
      Applied: int
      Last: string
      /// What went wrong that the person should know (a text that was refused, no connection); "" when nothing.
      Notice: string
      Scroll: float32 }

module Model =
    [<Literal>]
    let PollMs = 1000

    let private query (name: string) (pairs: (string * string) list) : string =
        pairs |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd |> Option.defaultValue ""

    let private withSlash (address: string) = if address.EndsWith "/" then address else address + "/"

    // ---- requests --------------------------------------------------------------------------------------------------

    let private url (m: Model) (path: string) : string =
        m.Api + path + (if m.Room = "" then "" else (if path.Contains "?" then "&" else "?") + "room=" + m.Room)

    let private request (m: Model) (meth: string) (path: string) (body: string) (toMsg: Reply -> Msg) : Fx<Msg> =
        Fetch({ Method = meth; Path = url m path; Body = body }, toMsg)

    let private succeeded (r: Reply) = r.Status >= 200 && r.Status < 300

    /// The JSON of an answer that succeeded.
    let private json (r: Reply) : Json option = if succeeded r then Json.tryParse r.Body else None

    /// What the server said was wrong, for a person: its `error`, or the status.
    let private complaint (r: Reply) : string =
        match Json.tryParse r.Body |> Option.bind (Json.field "error") |> Json.asString with
        | Some e -> e
        | None -> if r.Status = 0 then "no connection to the server" else "the server answered " + string r.Status

    /// The model at the start and the first requests, from the query of the page's address:
    /// `?room=` picks the room (as for the page of the server), `?api=` an API at another address (the module answers CORS).
    let init (search: string) : Model * Fx<Msg> list =
        let pairs = Http.parseQuery (if search.StartsWith "?" then search.Substring 1 else search)
        let model =
            { Shadow = Shadow.empty
              Api = (match query "api" pairs with "" -> "../" | address -> withSlash address)
              Room = query "room" pairs
              Host = "…"
              Draft = ""
              Ui = { UiState.empty with Focus = Some "draft" }
              Link = Connecting
              Polling = false
              Applied = 0
              Last = "—"
              Notice = ""
              Scroll = 0.0f }
        model, [ request model "GET" "api/info" "" GotInfo; request model "GET" "api/state" "" GotState ]

    /// The server answers.  What was said about a lost connection goes with it; what was said about a refused request stays until the next change succeeds.
    let private online (m: Model) : Model =
        { m with Link = Online; Notice = (if m.Link = Offline then "" else m.Notice) }

    // ---- the shadow ------------------------------------------------------------------------------------------------

    let private opName (op: PatchOp) : string =
        match op with
        | UpdateLeaf _ -> "updateLeaf"
        | ReplaceBranch _ -> "replaceBranch"
        | ListMutation _ -> "listMutation"
        | DeleteNode _ -> "deleteNode"

    let private describe (patch: Patch) (bytes: int) : string =
        "v" + string patch.Version + ": " + (patch.Ops |> List.map opName |> String.concat ", ") + " (" + string bytes + " bytes)"

    /// One patch (hexadecimal text) onto the shadow.  A patch the shadow has seen already changes nothing (a poll that overlaps the answer of a mutation brings it a second time);
    /// one that does not apply means the shadow cannot be trusted any more.
    let private receive (hex: string) (m: Model) : Result<Model, ApplyError> =
        match Shadow.decodeHex hex with
        | Error e -> Error e
        | Ok(patch, bytes) ->
            match Shadow.apply patch m.Shadow with
            | Ok shadow -> Ok { m with Shadow = shadow; Applied = m.Applied + 1; Last = describe patch bytes }
            | Error(VersionGap(have, got)) when got <= have -> Ok m
            | Error e -> Error e

    /// Patches in order; the first one that does not apply stops it, and the state is fetched again.
    let private receiveAll (hexes: string list) (m: Model) : Model * Fx<Msg> list =
        let folded = hexes |> List.fold (fun acc hex -> Result.bind (receive hex) acc) (Ok m)
        match folded with
        | Ok m' -> m', []
        | Error _ -> m, [ request m "GET" "api/state" "" GotState ]

    // ---- update ----------------------------------------------------------------------------------------------------

    let rec update (m: Model) (msg: Msg) : Model * Fx<Msg> list =
        match msg with
        | UiMsg u -> { m with Ui = UiState.update m.Ui u }, []
        | Scrolled v -> { m with Scroll = v }, []
        | Key k ->
            match m.Ui.Focus with
            | Some "draft" ->
                match k with
                | "Enter" -> update m Add
                | "Escape" -> { m with Ui = UiState.update m.Ui UiMsg.Blur }, []
                | _ ->
                    let text = TextEdit.apply m.Ui.Shift k m.Draft
                    (if text.Length > Todos.MaxTextLength then m else { m with Draft = text }), []
            | _ -> m, []
        | Add ->
            let text = m.Draft.Trim()
            if text = "" then m, []
            else { m with Draft = "" }, [ request m "POST" "api/todos" (Json.stringify (JObj [ "text", JStr text ])) (fun r -> GotAdded(text, r)) ]
        | Toggle id -> m, [ request m "POST" ("api/todos/" + string id + "/toggle") "" GotMutation ]
        | Remove id -> m, [ request m "DELETE" ("api/todos/" + string id) "" GotMutation ]
        | GotInfo r ->
            match json r |> Option.bind (Json.field "host") |> Json.asString with
            | Some host -> { m with Host = host }, []
            | None -> m, []
        | GotState r ->
            match json r |> Option.bind Shadow.ofState with
            | Some shadow ->
                let m' = { online m with Shadow = shadow; Polling = true }
                m', (if m.Polling then [] else [ After(PollMs, Tick) ])
            | None -> { m with Link = Offline }, [ After(PollMs * 2, Sync) ]
        | Sync -> m, [ request m "GET" "api/state" "" GotState ]
        | Tick -> m, [ request m "GET" ("api/patches?since=" + string m.Shadow.Version) "" GotPatches ]
        | GotPatches r ->
            let again = After(PollMs, Tick)
            match json r with
            | None -> { m with Link = Offline }, [ again ]
            | Some j ->
                let m' = online m
                if (Json.field "resync" j |> Json.asBool) = Some true then m', [ request m "GET" "api/state" "" GotState; again ]
                else
                    let hexes =
                        match Json.field "patches" j with
                        | Some(JArr entries) -> entries |> List.choose (fun e -> Json.asString (Json.field "hex" e))
                        | _ -> []
                    let m'', fx = receiveAll hexes m'
                    m'', fx @ [ again ]
        | GotAdded(text, r) ->
            if succeeded r then update m (GotMutation r)
            else
                // nothing was added: the text goes back into the field (unless something else is being typed), and the person is told why
                { m with Draft = (if m.Draft = "" then text else m.Draft); Notice = complaint r }, []
        | GotMutation r ->
            match json r |> Option.bind (Json.field "patch") |> Json.asString with
            | Some hex ->
                receiveAll [ hex ] { online m with Notice = "" }
            | None ->
                // the server refused (a todo that is gone, a text that is too long) or did not answer: say so, and look again at what is true
                { m with Notice = complaint r; Link = (if r.Status = 0 then Offline else m.Link) },
                (if r.Status = 0 then [] else [ request m "GET" "api/state" "" GotState ])
