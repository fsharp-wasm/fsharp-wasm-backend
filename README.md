# fsharp-wasm-backend

Backend modules written in F#, compiled to WebAssembly GC by [fsharp-wasm](https://github.com/fsharp-wasm/fsharp-wasm). The **host** that runs them on
**Cloudflare Workers (with a Durable Object), Vercel, Netlify, Deno, Bun and Node** — the core, the adapters, the platform matrix and the deploy
workflow — lives in [fsharp-wasm-edge](https://github.com/fsharp-wasm/fsharp-wasm-edge). This repository is where the modules are written.

```text
F# (FSharp.Wasm.Server + the application)  ──fsharp-wasm──▶  GhostTodo.wasm   (47 KB, WebAssembly GC, string ABI: exports)
                                                                   │  handle()   reads the request, writes the response, one at a time
                                          fsharp-wasm-edge: host/runtime/server.js + one small adapter per platform
```

The demo application, **Ghost Todo** (`demos/ghost-todo`), is a todo list whose every change is a **GGBP patch** (Ghost-Graph Binary Patch): a
client keeps a *shadow* of the list and applies the patches the server sends. There are two clients of the same server: the page of the host
(fsharp-wasm-edge, `host/client/index.html`, JavaScript and the DOM) and the **canvas client** (`demos/ghost-todo-canvas`), written in F# with the widgets of
[fsharp-wasm-graphics](https://github.com/fsharp-wasm/fsharp-wasm-graphics), compiled to WebAssembly GC too and drawn onto one `<canvas>`. Open both against one server:
what one does, the other shows.

```text
F# (GhostTodoCore: the model, the shadow)  ──┬── + FSharp.Wasm.Server.Host ──▶ GhostTodo.wasm        run by a host: the API
                                             └── + widgets, Browser.Net     ──▶ GhostTodoCanvas.wasm  run by a browser: the canvas client (≈ 197 KB)
```

## What is verified

- `dotnet test FSharp.Wasm.slnx -c Release`: 96 tests of the .NET libraries and 71 of the portable layer, the application and its clients (the encoding equals the .NET reference's
  bytes for 2000 random patches; decoding bad input never raises; UTF-8, hex, and JSON and number parsing (FSharp.Wasm.Data) against System.Text; the application's model and routes; the
  shadow: the patches of 300 random steps applied in order give the server's list; the canvas client against the real application in the same process: starting, adding, a second client that polls,
  a patch that comes twice, a gap in the patches, a server that is gone, a stale client).
- **The canvas client in Chromium** (`tests/e2e/canvas.mjs`): the compiled module against the real `GhostTodo.wasm` on the host of fsharp-wasm-edge, next to the page of the host: a todo added by
  another client arrives as a patch, typing and Enter add one, a click and the keyboard (Tab, Space, Enter, sent as fast as a script can) toggle and delete, what the canvas does shows on the page of the host and
  the other way round. 12 checks, run three times in a row on 2026-11-22.
- **The contract with the host.** CI compiles `GhostTodo.wasm` and hands it to the host of fsharp-wasm-edge (at a fixed revision, `EDGE_REF`), which runs
  **the same 26 checks on every platform** that can run on a runner and compares the answers: identical in 20 steps. What each platform row means
  (a runtime or an emulation) and what is deployed is in the [fsharp-wasm-edge README](https://github.com/fsharp-wasm/fsharp-wasm-edge#what-is-verified);
  nothing is deployed yet.

## Layout

| path | what it is |
|---|---|
| `src/FSharp.Wasm.Server` | the portable layer, in the subset fsharp-wasm compiles: HTTP types, the GGBP wire format (byte for byte the format of `FSharp.Wasm.Protocol`), UTF-8, hex; JSON and number parsing come from `FSharp.Wasm.Data` in fsharp-wasm-core. A client builds on this one |
| `src/FSharp.Wasm.Server.Host` | what a module that runs on a host needs of it: the imports from the host (`HostImports`), the key-value store, the loop that reads the request and hands over the response. Apart on purpose: a browser has none of these imports |
| `demos/ghost-todo-core` | what the server and its clients share: the model and its patches (`Todos.fs`) and the client side of the format, the **shadow** (`Shadow.fs`) |
| `demos/ghost-todo` | the application: the routes and the exports `handle` / `reset` (`GhostTodo.fs`) |
| `demos/ghost-todo-canvas` | the canvas client: `Model.fs` (a pure function; the requests it wants are data), `View.fs` (widgets), `GhostTodoCanvas.fs` (the exports; where the requests are made), `web/index.html` (the page) |
| `build/` | `build.sh` compiles a module to `dist/` (`--module server`, the default: `GhostTodo.wasm`, string ABI exports; `--module canvas`: `GhostTodoCanvas.wasm` and `dist/canvas/`, the page with its runtime, to be served at `/canvas/`); `wasm-tools` must be on the PATH |
| `tests/FSharp.Wasm.Server.Tests` | 71 tests of the portable layer against the .NET reference, the application, the shadow, the canvas client and the platform table |
| `tests/e2e/canvas.mjs` | the canvas client in Chromium against the real module on the host of fsharp-wasm-edge |
| `src/FSharp.Wasm.*` (the other nine) | the .NET reference libraries: Protocol (GGBP), Ghost (the shadow graph), Reactive, Components (KV, durable state, queue, storage, LLM), Auth, Contract, Observability, Dev, Sdk (build pipeline, WIT, project config), Host |

The .NET libraries are the **reference** the portable layer is checked against; they use `System.IO`, `System.Threading` and `Async`, which the
WebAssembly build does not, so a module is built on `FSharp.Wasm.Server`.

## Build and run

```bash
export FSHARP_WASM_CORE_DIR=/path/to/fsharp-wasm-core            # a checkout at 52afb74 or later: the F# base library and FSharp.Wasm.Data (JSON, numbers)
dotnet test FSharp.Wasm.slnx -c Release                           # the .NET libraries and the portable layer

# the module: a compiler, and wasm-tools on the PATH
export FSHARP_WASM_BIN=/path/to/fsharp-wasm                       # the binary of a release, or
export FSHARP_WASM_DIR=/path/to/fsharp-wasm                       # a checkout at 7a79ad6 or later (needs FSHARP_COMPILER, the patched fsc.dll)
bash build/build.sh                                               # dist/GhostTodo.wasm

# the canvas client: also the widgets and the browser glue (FSHARP_GRAPHICS_DIR, FSHARP_RUNTIME_DIR; the submodules lib/graphics and lib/runtime by default)
bash build/build.sh --module canvas                               # dist/GhostTodoCanvas.wasm and dist/canvas/ (index.html, the module, runtime/*.js)
EDGE_DIR=/path/to/fsharp-wasm-edge FSHARP_RUNTIME_DIR=/path/to/fsharp-wasm-runtime node tests/e2e/canvas.mjs
```
`dist/canvas/` is a directory any static host can serve at `/canvas/`, next to the API (it talks to `../api/…`; `?room=` picks a room as on the page of the host, `?api=https://host/` another server).

To run the module, in a checkout of fsharp-wasm-edge: `GHOST_WASM=/path/to/dist/GhostTodo.wasm node build/package.mjs`, then
`node dist/platforms/node/index.mjs` (http://localhost:8787/) or `node tests/platforms/run.mjs`. To record a module as that repository's fixture:
`node tools/fixture.mjs set <module> --backend <sha> --compiler <sha> --core <sha>`.

A module is **synchronous** and one request at a time: what that means for state is in the
[fsharp-wasm-edge README](https://github.com/fsharp-wasm/fsharp-wasm-edge#what-a-module-cannot-do). The string ABI is "exports" so that the module also runs
where a platform hands it over precompiled (Cloudflare Workers, Vercel Edge); `build.sh` checks that no `wasm:js-string` import is left.

## Later

The five submodules `lib/*` (Fable, fsharp, fsharp-wasm, fsharp-wasm-core, fsharp-wasmecto) are to go, and the compiler to be taken as a release
binary. Nothing reads them today: the build takes the F# base library from `FSHARP_WASM_CORE_DIR` and the compiler from `FSHARP_WASM_BIN` or
`FSHARP_WASM_DIR`, and the CI checks both out at fixed revisions.

Apache License 2.0 (`LICENSE`, `NOTICE`). The .NET libraries and their tests were written as a specification (the branch
`copilot/fsharp-wasmgc-backend-library-spec` of the former repository); the portable layer, the demo, the adapters and the matrix are new.
