/// The module that becomes GhostTodoCanvas.wasm: a client of the Ghost Todo whose whole interface is FSharp.Graphics widgets drawn onto one canvas.
/// The exports are what runtime/gfx.js (`mount`) calls:
///   start()                         bind the canvas, read the address of the page, ask the server for its state
///   onResize(w, h, ratio)           CSS size of the canvas and the device pixel ratio
///   onPointer(kind, x, y, button)   kind 0 down, 1 up, 2 move, 3 leave
///   onWheel(dx, dy)
///   onKey(kind)                     kind 0 down, 1 up; the key name is fetched with Gfx.pendingKey
///   frame(nowMs)                    build, resolve, draw (when the model changed, an event was taken, or a caret is due: see CanvasHost.Frame)
module GhostTodoCanvas.Entry

open FSharp.Math
open FSharp.Graphics.Core
open FSharp.Graphics.Scene
open FSharp.Graphics.Widgets
open FSharp.Graphics.Canvas
open Browser

/// All mutable state of the page, in one object (a module-level `let mutable` is a wasm global; this keeps it together).
/// It is where the effects of `Model.update` are carried out: a request goes out through `Browser.Net`, its answer comes back as a message.
type Page() =
    let text = CanvasTextEngine() :> ITextEngine
    let host = CanvasHost text
    let mutable model = fst (Model.init "")

    member this.Run(fx: Fx<Msg>) : unit =
        match fx with
        | Fetch(call, toMsg) ->
            Net.request call.Method call.Path call.Body (if call.Body = "" then [] else [ "content-type", "application/json" ])
                (fun status body -> this.Dispatch(toMsg { Status = status; Body = body }))
        | After(ms, msg) -> Timing.setTimeout ms (fun () -> this.Dispatch msg) |> ignore

    member this.Dispatch(msg: Msg) : unit =
        let before = model.Shadow
        let model', effects = Model.update model msg
        // an answer that changes nothing (the poll that finds no news, every second) draws nothing
        if model' <> model then host.Invalidate()
        model <- model'
        // a canvas has no DOM to read the list from: the page is told when it changes (the `gfx` event of the canvas: the version, and the todos one per line, "x " done and "o " open)
        if model'.Shadow <> before then
            Gfx.announce "sync" (string model'.Shadow.Version)
                (model'.Shadow.Todos |> List.map (fun t -> (if t.Done then "x " else "o ") + t.Text) |> String.concat "\n")
        List.iter this.Run effects

    member this.Start(search: string) : unit =
        let model', effects = Model.init search
        model <- model'
        List.iter this.Run effects

    member _.Resize(w: float32, h: float32, r: float32) = host.Resize(w, h, r)
    member _.Input(evt: InputEvent) = host.Input evt

    member this.Frame(nowMs: float) =
        host.Frame(nowMs, false, 0.0f, fun time size ->
            let ui : Ui<Msg> = { Theme = Theme.dark; Text = text; State = model.Ui; Time = time; Lift = UiMsg; Send = this.Dispatch }
            View.view ui size model)

let private page = Page()

[<CompiledName("start")>]
let start () : unit =
    Gfx.init "gfx"
    page.Start(Location.jsGetSearch ())

[<CompiledName("onResize")>]
let onResize (w: float) (h: float) (ratio: float) : unit = page.Resize(float32 w, float32 h, float32 ratio)

[<CompiledName("onPointer")>]
let onPointer (kind: int) (x: float) (y: float) (button: int) : unit =
    let p = Vec2.create (float32 x) (float32 y)
    let evt =
        match kind with
        | 0 -> PointerDown(p, button)
        | 1 -> PointerUp(p, button)
        | 2 -> PointerMove p
        | _ -> PointerLeave p
    page.Input evt |> ignore

[<CompiledName("onWheel")>]
let onWheel (dx: float) (dy: float) : unit = page.Input(Scroll(Vec2.create (float32 dx) (float32 dy))) |> ignore

[<CompiledName("onKey")>]
let onKey (kind: int) : unit =
    let key = Gfx.pendingKey ()
    page.Input(if kind = 0 then KeyDown key else KeyUp key) |> ignore

[<CompiledName("frame")>]
let frame (nowMs: float) : unit = page.Frame nowMs
