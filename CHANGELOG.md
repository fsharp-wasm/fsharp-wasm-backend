# Changelog

All notable changes to this repository are documented here (newest first).

## [Unreleased]

### Added
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
- `FSharp.Wasm.Components` did not build (the interfaces of the durable state and the queue lacked the `not null` constraint their
  implementations declare); the solution builds without warnings.

### Changed
- The module is built with `WASMGC_STRING_ABI=exports` (fsharp-wasm 8976acc): the JS String Builtins are not available to a module a platform
  precompiles (workerd, Vercel Edge), where instantiating it fails.
- Apache License 2.0 (`LICENSE`, `NOTICE`); the repository was restored from a backup and its histories joined.
