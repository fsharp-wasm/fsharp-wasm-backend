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
client keeps a *shadow* of the list and applies the patches the server sends (the page is in fsharp-wasm-edge, `host/client/index.html`; open it in two tabs).

## What is verified

- `dotnet test FSharp.Wasm.slnx -c Release`: 96 tests of the .NET libraries and 54 of the portable layer (the encoding equals the .NET reference's
  bytes for 2000 random patches; decoding bad input never raises; UTF-8, hex, JSON and numbers against System.Text; the application's model and routes).
- **The contract with the host.** CI compiles `GhostTodo.wasm` and hands it to the host of fsharp-wasm-edge (at a fixed revision, `EDGE_REF`), which runs
  **the same 26 checks on every platform** that can run on a runner and compares the answers: identical in 20 steps. What each platform row means
  (a runtime or an emulation) and what is deployed is in the [fsharp-wasm-edge README](https://github.com/fsharp-wasm/fsharp-wasm-edge#what-is-verified);
  nothing is deployed yet.

## Layout

| path | what it is |
|---|---|
| `src/FSharp.Wasm.Server` | the portable layer, in the subset fsharp-wasm compiles: HTTP types, JSON, the GGBP wire format (byte for byte the format of `FSharp.Wasm.Protocol`), UTF-8, hex, number parsing, the imports of a module (`HostImports`) |
| `demos/ghost-todo` | the application: the model and its patches (`Todos.fs`), the routes and the exports `handle` / `reset` (`GhostTodo.fs`) |
| `build/` | `build.sh` compiles the module to `dist/GhostTodo.wasm` (string ABI: exports; `wasm-tools` must be on the PATH) |
| `tests/FSharp.Wasm.Server.Tests` | 54 tests of the portable layer against the .NET reference, the application, and the platform table |
| `src/FSharp.Wasm.*` (the other nine) | the .NET reference libraries: Protocol (GGBP), Ghost (the shadow graph), Reactive, Components (KV, durable state, queue, storage, LLM), Auth, Contract, Observability, Dev, Sdk (build pipeline, WIT, project config), Host |

The .NET libraries are the **reference** the portable layer is checked against; they use `System.IO`, `System.Threading` and `Async`, which the
WebAssembly build does not, so a module is built on `FSharp.Wasm.Server`.

## Build and run

```bash
export FSHARP_WASM_CORE_DIR=/path/to/fsharp-wasm-core            # a checkout at cc6b1c0 or later: the F# base library
dotnet test FSharp.Wasm.slnx -c Release                           # the .NET libraries and the portable layer

# the module: a compiler, and wasm-tools on the PATH
export FSHARP_WASM_BIN=/path/to/fsharp-wasm                       # the binary of a release, or
export FSHARP_WASM_DIR=/path/to/fsharp-wasm                       # a checkout at 73131d3 or later (needs FSHARP_COMPILER, the patched fsc.dll)
bash build/build.sh                                               # dist/GhostTodo.wasm
```

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
