# Changelog

All notable changes to this repository are documented here (newest first).

## [Unreleased]

### Added
- **The canvas client of the Ghost Todo** (`demos/ghost-todo-canvas`): a second todo for the same server, written in F# with the widgets of fsharp-wasm-graphics and compiled to WebAssembly GC (`GhostTodoCanvas.wasm`, 197 KB):
  a field and a button, a scrollable list of check boxes with Delete, and the state of the shadow (host, version, patches applied, the last patch). It keeps a shadow of the server's list, applies the GGBP
  patches of its own requests and of a poll once a second, copes with a server that is gone (it says so and keeps asking), a patch it has seen already and a gap in the patches (it fetches the state), and
  can be driven by the keyboard alone. The model is a pure function whose requests are data, so the whole synchronisation is tested without a browser (`tests/FSharp.Wasm.Server.Tests/CanvasClientTests.fs`:
  10 tests against the real `GhostTodo.route` in the same process), and in Chromium against the real module (`tests/e2e/canvas.mjs`). `build/build.sh --module canvas` makes the module and `dist/canvas/`, the page with
  its runtime, which any static host can serve.
- **`GhostTodoCore`** (`demos/ghost-todo-core`), shared by the server and its clients: the model (`Todos.fs`, moved out of `demos/ghost-todo`) and `Shadow.fs`, the client side of the patch format (`Shadow.apply`,
  `applyHex`, `ofState`) for this schema, with 7 tests (the patches of 300 random steps give the server's list; a gap, an unknown todo, a bad index and a bad record are errors and leave the shadow as it was).

### Changed
- CI takes the host of fsharp-wasm-edge at the revision that serves the canvas client below `/canvas/` and runs 29 checks on every platform (it was 26).
- **`FSharp.Wasm.Server` is split in two.** The host binding (`HostImports`, the store, `Host.run`) is `FSharp.Wasm.Server.Host`, a project of its own with the same namespace; `FSharp.Wasm.Server` keeps the protocol (HTTP types,
  GGBP, UTF-8, hex). A module that is a client of the protocol must not carry the imports of a host, and it did: a top-level value of the host module (`Host.store`) was initialised in every program that referenced the project,
  which put `runtime/server.js` into the imports of the browser module. A module that runs on a host references both (`demos/ghost-todo` does).
- `Http.parseQuery` (it was private to `Host`) is public: the canvas client reads its address with it.
- **JSON and number parsing come from [fsharp-wasm-core](https://github.com/fsharp-wasm/fsharp-wasm-core) (`FSharp.Wasm.Data`).** `FSharp.Wasm.Server` no
  longer has its own `Json.fs` and `Numbers.fs`; it references the shared project, which has the union of what this repository and the newsletter
  had (the 64-level nesting limit and strict string rules from here; error positions, a pretty printer and accessors with fallbacks from there).
  The namespace of `Json`, `JNull`…`JObj` and `Numbers` is `FSharp.Wasm.Data` now (add `open FSharp.Wasm.Data`); `Json.parse` of the old module is
  `Json.tryParse` (`Json.parse` returns a `Result` with the position of the first problem). The tests of both stay here as the check against
  System.Text. `GhostTodo.wasm` grows from 47,146 to 49,625 bytes. CI pins core at 52afb74 and the compiler at 7a79ad6.
- **The host moved to [fsharp-wasm-edge](https://github.com/fsharp-wasm/fsharp-wasm-edge)**: the JavaScript core and stores (`server/runtime`), the adapters for
  Node, Bun, Deno, Cloudflare Workers, Vercel and Netlify, the GGBP client, the packager, the platform matrix, the live runner, the deploy workflow and
  its documentation. This repository keeps the modules, the portable layer and the .NET reference libraries; its CI compiles the module and runs it on
  the host at a fixed revision (the contract between module and host). The older entries below describe the code as it was written here.

### Added
- **Continuous deployment** (`.github/workflows/deploy.yml`, `docs/deploy.md`): the `platforms` artifact of a CI run goes to Cloudflare (wrangler),
  Vercel and Netlify, then `tests/platforms/live.mjs` runs the 26 checks against the live URLs and compares the answers. Nothing is deployed
  yet; `docs/deploy.md` lists accounts, tokens and settings, and what was checked without one (wrangler `--dry-run`, `netlify build --offline`,
  actionlint, the runner against local servers over HTTP). `tests/platforms/http.mjs` and `report.mjs` are shared by both runners.
- **Backend modules on Cloudflare Workers (with a Durable Object), Vercel, Netlify, Deno, Bun and Node.** `src/FSharp.Wasm.Server` is the
  portable layer (HTTP types, JSON, the GGBP wire format, UTF-8, hex, number parsing, the host imports of a module), `demos/ghost-todo`
  the demo application (a todo list whose changes are GGBP patches), `server/` the JavaScript core, the stores (memory, JSON file, Deno KV,
  Durable Object storage), the platform adapters and the GGBP client; `build/package.mjs` lays out a deployable directory per platform.
- **A platform matrix** (`tests/platforms`): the same 26 checks on Node 24, Bun 1.4, Deno 2.9, workerd (Cloudflare, with real Durable Object
  storage), the Vercel Edge Runtime sandbox, Vercel Node and Netlify function handlers, and Netlify's edge runtime (Deno); the platforms give
  identical answers, and the state survives a restart where there is storage. Nothing is deployed from here; the README says what is a runtime and
  what an emulation.
- 54 tests of the portable layer (`tests/FSharp.Wasm.Server.Tests`): the GGBP encoding equals the .NET reference's bytes for 2000 random patches,
  decoding never raises, UTF-8, hex, JSON and number parsing against System.Text, the application's model and routes, the platform table.
- `PlatformKind` and `WasmPlatform` know Vercel, Netlify, Deno and Bun; `PlatformCapabilities` says which platform needs the Exports string
  ABI, where its adapter is and whether an instance keeps state.

### Fixed
- The Vercel Node.js function had no base path, so at its documented address `/api/node` every request was a 404; it is now mounted there
  (`basePath`, a rewrite for the sub-paths) and the matrix calls it under that path. Netlify's package has a publish directory
  (`netlify deploy` needs one).
- `FSharp.Wasm.Components` did not build (the interfaces of the durable state and the queue lacked the `not null` constraint their
  implementations declare); the solution builds without warnings.

### Changed
- The module is built with `WASMGC_STRING_ABI=exports` (fsharp-wasm 73131d3): the JS String Builtins are not available to a module a platform
  precompiles (workerd, Vercel Edge), where instantiating it fails.
- Apache License 2.0 (`LICENSE`, `NOTICE`); the repository was restored from a backup and its histories joined.
