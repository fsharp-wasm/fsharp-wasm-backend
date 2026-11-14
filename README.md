# fsharp-wasm-backend

Backend modules written in F#, compiled to WebAssembly GC by [fsharp-wasm](https://github.com/fsharp-wasm/fsharp-wasm), and run on
**Cloudflare Workers (with a Durable Object), Vercel, Netlify, Deno, Bun and Node** — one module, one small adapter per platform.

```text
F# (FSharp.Wasm.Server + the application)  ──fsharp-wasm──▶  GhostTodo.wasm   (47 KB, WebAssembly GC, string ABI: exports)
                                                                  │  handle()   reads the request, writes the response, one at a time
              server/runtime/server.js  (the core: Request ⇄ module, store, strings)  ← web-standard Request / Response
                  ├─ adapters/node        http.createServer                     ├─ adapters/cloudflare  Worker + Durable Object
                  ├─ adapters/bun         Bun.serve                              ├─ adapters/vercel      Edge + Node functions
                  └─ adapters/deno        Deno.serve + Deno KV                   └─ adapters/netlify     Edge function + Function
```

The demo application, **Ghost Todo** (`demos/ghost-todo`), is a todo list whose every change is a **GGBP patch** (Ghost-Graph Binary Patch): a
client keeps a *shadow* of the list and applies the patches the server sends (`server/client/index.html`, open it in two tabs).

## What is verified

`node build/package.mjs && node tests/platforms/run.mjs` runs **the same 26 checks on every platform** and compares what the platforms
answer: they give identical answers in 20 steps (unicode text, the patches decoded and applied to a shadow, 12 parallel requests, a log of
64 patches that rolls over, 404 / 405 / 400, CORS, the page). Where a platform has storage, the state survives a restart.

| platform | what runs here | how real | state |
|---|---|---|---|
| Node 24.22 | the packaged adapter, as a process | the platform's own runtime | a JSON file, survives a restart |
| Bun 1.4 | the packaged adapter, as a process | the platform's own runtime | a JSON file, survives a restart |
| Deno 2.9 / Deno Deploy | the packaged adapter, as a process | the platform's own runtime | Deno KV, survives a restart |
| Cloudflare Workers | the Worker and the Durable Object on **workerd** (Miniflare 4.20260730, workerd 1.20260730) | the platform's runtime and its Durable Object storage; **not deployed** | Durable Object storage, survives a restart |
| Vercel Edge Functions | the edge adapter, bundled with `?module` as a ready `WebAssembly.Module`, in the Edge Runtime sandbox (`@edge-runtime/vm` 5.0) | an emulation; Vercel's bundler and platform are not exercised; **not deployed** | memory per instance |
| Vercel Functions (Node) | the Node adapter's handlers, called in a Node 24 process | the handler contract; **not deployed** | memory per instance |
| Netlify Edge Functions | the edge adapter under Deno, given a `Request` | Netlify's edge runtime is Deno; Netlify's bundler is not exercised; **not deployed** | memory per instance |
| Netlify Functions | the function's handler, called in a Node 24 process | the handler contract; **not deployed** | memory per instance |

Not supported: **Fastly Compute, Fermyon Spin, WasmEdge** run WASI *components*, not a module with a JavaScript host; the shim generators of
`FSharp.Wasm.Host/Platform.fs` for them are sketches, not working adapters. Nothing has been deployed to a platform from here: the
packages (`dist/platforms/<name>`) carry the configuration (`wrangler.toml`, `vercel.json`, `netlify.toml`, `deno.json`) for you to
deploy; see [docs/platforms.md](docs/platforms.md).

## Why the string ABI is "exports"

The compiler's default lets a string cross the module boundary through the JS String Builtins (`wasm:js-string`). A platform that hands the
module over **precompiled** — Cloudflare Workers and Vercel Edge import a `.wasm` as a ready `WebAssembly.Module` — compiles it without those
builtins, and instantiating fails (`Import "wasm:js-string": module is not an object or function`; checked in workerd). So these modules are
built with `WASMGC_STRING_ABI=exports` (fsharp-wasm 8976acc or later): no `wasm:js-string` import at all, strings read and built through four
exports of the module. It works on every engine with WebAssembly GC. See `docs/host-interop.md` in fsharp-wasm.

## Layout

| path | what it is |
|---|---|
| `src/FSharp.Wasm.Server` | the portable layer, in the subset fsharp-wasm compiles: HTTP types, JSON, the GGBP wire format (byte for byte the format of `FSharp.Wasm.Protocol`), UTF-8, hex, number parsing, the imports of a module (`HostImports`) |
| `demos/ghost-todo` | the application: the model and its patches (`Todos.fs`), the routes and the exports `handle` / `reset` (`GhostTodo.fs`) |
| `server/runtime` | the core (`server.js`), the stores (`stores.js`: memory, JSON file, Deno KV, Durable Object storage) |
| `server/adapters` | the platform adapters, each a few lines over the core, and their configuration |
| `server/client` | the GGBP client (`ggbp.js`) and the page that keeps a shadow |
| `build/` | `build.sh` compiles the module, `package.mjs` lays out `dist/platforms/<name>/` for each platform, `assets.mjs` |
| `tests/platforms` | the platform matrix |
| `tests/FSharp.Wasm.Server.Tests` | 54 tests of the portable layer against the .NET reference, the application, and the platform table |
| `src/FSharp.Wasm.*` (the other nine) | the .NET reference libraries: Protocol (GGBP), Ghost (the shadow graph), Reactive, Components (KV, durable state, queue, storage, LLM), Auth, Contract, Observability, Dev, Sdk (build pipeline, WIT, project config), Host |

The .NET libraries are the **reference** the portable layer is checked against (the tests compare encoded bytes for 2000 random patches); they
use `System.IO`, `System.Threading` and `Async`, which the WebAssembly build does not, so a module is built on `FSharp.Wasm.Server`.

## Build and run

```bash
export FSHARP_WASM_CORE_DIR=/path/to/fsharp-wasm-core            # a checkout at 1c592e7 or later: the F# base library
dotnet test FSharp.Wasm.slnx -c Release                           # 96 tests of the .NET libraries + 54 of the portable layer

# the module: a compiler, and wasm-tools on the PATH
export FSHARP_WASM_BIN=/path/to/fsharp-wasm                       # the binary of a release, or
export FSHARP_WASM_DIR=/path/to/fsharp-wasm                       # a checkout at 8976acc or later (needs FSHARP_COMPILER, the patched fsc.dll)
bash build/build.sh                                               # dist/GhostTodo.wasm

npm install && node build/package.mjs                             # dist/platforms/{node,bun,deno,cloudflare,vercel,netlify}
node dist/platforms/node/index.mjs                                # http://localhost:8787/
node tests/platforms/run.mjs [node bun deno netlify-edge cloudflare vercel-edge vercel-node netlify-functions]
```

The matrix needs Node 24, `deno` and `bun` on the PATH (or `TOOLS_BIN` for `deno`).

## What a module cannot do

A module is **synchronous**: it cannot wait for a network call (no JSPI yet), so the core reads the request body and loads the store before the
call and flushes the store after it. State that outlives a request lives in the module's memory and is persisted through the store; an instance
serves requests one at a time. On Cloudflare a Durable Object per room is exactly that. On Vercel and Netlify an instance forgets between cold
starts and instances do not share memory: the memory store is what those platforms give; for state that outlives an instance, put a store of
`server/runtime/stores.js` over Vercel KV / Blob or Netlify Blobs (not written: nothing here can test it).

## Later

The five submodules `lib/*` (Fable, fsharp, fsharp-wasm, fsharp-wasm-core, fsharp-wasmecto) are to go, and the compiler to be taken as a release
binary. Nothing reads them today: the build takes the F# base library from `FSHARP_WASM_CORE_DIR` and the compiler from `FSHARP_WASM_BIN` or
`FSHARP_WASM_DIR`, and the CI checks both out at fixed revisions.

Apache License 2.0 (`LICENSE`, `NOTICE`). The .NET libraries and their tests were written as a specification (the branch
`copilot/fsharp-wasmgc-backend-library-spec` of the former repository); the portable layer, the demo, the adapters and the matrix are new.
