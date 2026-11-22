namespace GhostTodoCanvas

open FSharp.Math
open FSharp.Graphics.Core
open FSharp.Graphics.Scene
open FSharp.Graphics.SceneDsl
open FSharp.Graphics.Widgets

module View =
    [<Literal>]
    let CardMax = 640.0f
    [<Literal>]
    let Margin = 24.0f
    [<Literal>]
    let Pad = 24.0f
    [<Literal>]
    let RowH = 44.0f
    [<Literal>]
    let FooterH = 112.0f

    /// A part of the page for assistive technology, in the order the keyboard walks them.
    let private region (id: string) (role: Role) (name: string) (order: int) (content: Scene) : Scene =
        Semantic({ Semantics.create id role name with Order = order }, content)

    let private chip (ui: Ui<Msg>) (text: string) (dot: Color) : Scene =
        let w = Ui.textWidth ui 12.0f text
        Group [ rectangle { bounds 0.0f 0.0f (w + 30.0f) 24.0f; radius 12; fill (Color.withAlpha 0.16f dot) }
                circle { bounds 10 8 8 8; fill dot }
                shift 24 4 (Text(text, Ui.textStyle ui 12.0f ui.Theme.Text)) ]

    /// The mark: a tile with a checked list in it.
    let private logo (th: Theme) : Scene =
        Group [ rectangle { bounds 0 0 36 36; radius 10; vgradient th.AccentHover th.AccentPressed; shadow 0 2 6 (rgba 0 0 0 90) }
                rectangle { bounds 9 10 6 6; radius 2; fill th.OnAccent }
                rectangle { bounds 19 11 9 4; radius 2; fill (Color.withAlpha 0.8f th.OnAccent) }
                rectangle { bounds 9 21 6 6; radius 2; fill (Color.withAlpha 0.6f th.OnAccent) }
                rectangle { bounds 19 22 9 4; radius 2; fill (Color.withAlpha 0.6f th.OnAccent) } ]

    /// The state of the connection, in words and colour (the words carry it; the colour only helps), and how wide the chip is.
    let private linkChip (ui: Ui<Msg>) (m: Model) : Scene * float32 =
        let th = ui.Theme
        let text, colour =
            match m.Link with
            | Connecting -> "connecting…", th.TextMuted
            | Online -> "online · v" + string m.Shadow.Version, th.Success
            | Offline -> "offline · trying again", th.Danger
        Semantic({ Semantics.create "link" Role.StaticText ("Server: " + text) with Live = Live.Polite }, chip ui text colour),
        Ui.textWidth ui 12.0f text + 30.0f

    // ---- the list ----------------------------------------------------------------------------------------------------

    let private todoRow (ui: Ui<Msg>) (w: float32) (t: GhostTodoCore.Todo) : Scene =
        let th = ui.Theme
        let delete =
            Button.view ui ("del-" + string t.Id)
                { Button.make "Delete" (fun () -> ui.Send(Remove t.Id)) with Variant = Button.Ghost }
        let bw = (Ui.measure ui delete).W
        // the text is clipped where the button begins; a done todo is dimmed (the tick says it too)
        let check = CheckBox.view ui ("todo-" + string t.Id) t.Text t.Done (fun _ -> ui.Send(Toggle t.Id))
        Semantic(
            { Semantics.create ("item-" + string t.Id) Role.ListItem t.Text with Order = 2 },
            Group [ Clip(Rect.create 0.0f 0.0f (w - bw - 12.0f) RowH, shift 0 10 (if t.Done then Opacity(0.55f, check) else check))
                    shift (w - bw) ((RowH - ui.Theme.ControlHeight) * 0.5f) delete
                    shift 0 (RowH - 1.0f) (Divider.horizontal ui w) ])

    let private todoList (ui: Ui<Msg>) (w: float32) (h: float32) (m: Model) : Scene =
        let th = ui.Theme
        match m.Shadow.Todos with
        | [] ->
            region "todos" Role.List "Todos" 2
                (Group [ rectangle { bounds 0.0f 0.0f w h; fill Color.Transparent }
                         shift 0 24 (Label.view ui Label.Muted (if m.Link = Online then "Nothing to do. Type above and press Enter." else "Waiting for the server…")) ])
        | todos ->
            let rows = Group(todos |> List.mapi (fun i t -> shift 0.0f (float32 i * RowH) (todoRow ui (w - 12.0f) t)))
            region "todos" Role.List "Todos" 2
                (ScrollView.viewNamed ui "todo-scroll" "Todos, scrollable" { Width = w; Height = h; Offset = m.Scroll; OnScroll = fun v -> ui.Send(Scrolled v) } rows)

    // ---- the page ----------------------------------------------------------------------------------------------------

    let private composer (ui: Ui<Msg>) (w: float32) (m: Model) : Scene =
        let add = Button.view ui "add" { Button.make "Add" (fun () -> ui.Send Add) with Disabled = (m.Draft.Trim() = "") }
        let addW = (Ui.measure ui add).W
        region "composer" Role.Region "New todo" 1
            (Group [ TextField.viewNamed ui "draft" "New todo" ignore { Value = m.Draft; Placeholder = "What needs doing?"; Width = w - addW - 8.0f }
                     shift (w - addW) 0 add ])

    /// What the shadow is: the host, the version it is at, how many patches got it there, and the last one.
    let private footer (ui: Ui<Msg>) (w: float32) (m: Model) : Scene =
        let th = ui.Theme
        let stats =
            [ "runs on", m.Host
              "shadow version", string m.Shadow.Version
              "patches applied", string m.Applied
              "last patch", m.Last ]
        let lines =
            stats |> List.mapi (fun i (name, value) ->
                Group [ shift 0.0f (float32 i * 20.0f) (Text(name, Ui.textStyle ui 12.0f th.TextMuted))
                        shift 120.0f (float32 i * 20.0f) (Text(value, Ui.textStyle ui 12.0f th.Text)) ])
        region "sync" Role.Region "Synchronisation" 3
            (Group [ Divider.horizontal ui w
                     shift 0 14 (Group lines)
                     (if m.Notice = "" then Empty
                      else shift 0 98 (Semantic({ Semantics.create "notice" Role.StaticText m.Notice with Live = Live.Assertive },
                                                Text(m.Notice, Ui.textStyle ui 12.0f (hex 0xFCA5A5))))) ])

    /// Whole-window view.  `ui` carries the theme, the text engine and the dispatch; the interaction state is the model's.
    let view (ui: Ui<Msg>) (size: Vec2) (m: Model) : Scene =
        let ui = { ui with State = m.Ui }
        let th = ui.Theme
        let w, h = size.X, size.Y
        let cardW = min CardMax (w - 2.0f * Margin)
        let cardH = h - 2.0f * Margin
        let innerW = cardW - 2.0f * Pad
        let headerH = 52.0f
        let composerY = Pad + headerH + 12.0f
        let listY = composerY + th.ControlHeight + 16.0f
        let listH = max RowH (cardH - Pad - listY - FooterH)
        let open' = m.Shadow.Todos |> List.filter (fun t -> not t.Done) |> List.length
        let summary =
            match m.Shadow.Todos with
            | [] -> "no todos yet"
            | todos -> string open' + " of " + string (List.length todos) + " open"
        let link, linkW = linkChip ui m
        let card =
            Group [ rectangle { bounds 0.0f 0.0f cardW cardH; radius 14; fill th.Surface; stroke th.Border 1.0f; shadow 0 8 28 (rgba 0 0 0 110) }
                    // the header: the mark, the name, what it is, the state of the connection
                    region "header" Role.Region "Ghost Todo" 0
                        (Group [ shift Pad Pad (logo th)
                                 shift (Pad + 48.0f) (Pad - 3.0f) (Label.view ui Label.Title "Ghost Todo")
                                 shift (Pad + 48.0f) (Pad + 28.0f) (Label.view ui Label.Caption ("canvas client · " + summary))
                                 shift (cardW - Pad - linkW) (Pad + 6.0f) link ])
                    shift Pad composerY (composer ui innerW m)
                    shift Pad listY (todoList ui innerW listH m)
                    shift Pad (cardH - Pad - FooterH + 8.0f) (footer ui innerW m) ]
        let page = Group [ rectangle { bounds 0.0f 0.0f w h; fill th.Background }; shift ((w - cardW) * 0.5f) Margin card ]
        Ui.root ui (fun k -> ui.Send(Key k)) page
