#!/usr/bin/env bash
# build.sh — compile a demo of this repository to WebAssembly GC:  dist/<Name>.wasm
#
#   bash build/build.sh [--module server|canvas] [--out dist]
#
#   server (default)  demos/ghost-todo         -> GhostTodo.wasm        the Ghost Todo, run by a host (fsharp-wasm-edge).  Its strings cross the host boundary in the
#                     Exports ABI (WASMGC_STRING_ABI=exports): the module imports no `wasm:js-string`, so it also runs where a module is precompiled
#                     without the JS String Builtins (Cloudflare Workers, Vercel Edge).
#   canvas            demos/ghost-todo-canvas -> GhostTodoCanvas.wasm   the client with widgets on a canvas, run by a browser (runtime/boot.js, which compiles it with
#                     the JS String Builtins).  Needs fsharp-wasm-graphics and fsharp-wasm-runtime (FSHARP_GRAPHICS_DIR, FSHARP_RUNTIME_DIR; see env.sh).
# The compiler is not part of this repository; first hit wins:
#   FSHARP_WASM_BIN   the self-contained `fsharp-wasm` executable of a release (it carries FSharp.Wasm.Core beside it)
#   FSHARP_WASM_DIR   a checkout of fsharp-wasm (revision 73131d3 or later: the Exports ABI): its CLI runs through `dotnet run` and
#                     needs FSHARP_COMPILER = the patched fsc.dll built from the checkout's vendor/fsharp
# `wasm-tools` must be on the PATH.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/build/env.sh"
OUT="$ROOT/dist"
MODULE=server
while [[ $# -gt 0 ]]; do
  case $1 in
    --out) mkdir -p "$2"; OUT="$(cd "$2" && pwd)"; shift 2 ;;
    --module) MODULE="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 1 ;;
  esac
done
case $MODULE in
  server) PROJECT="$ROOT/demos/ghost-todo";        NAME=GhostTodo;       export WASMGC_STRING_ABI=exports ;;
  canvas) PROJECT="$ROOT/demos/ghost-todo-canvas"; NAME=GhostTodoCanvas ;;
  *) echo "unknown module: $MODULE (server or canvas)" >&2; exit 1 ;;
esac
command -v wasm-tools >/dev/null || { echo "wasm-tools not found (cargo install wasm-tools)" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
if [[ -n "${FSHARP_WASM_BIN:-}" ]]; then
  "$FSHARP_WASM_BIN" "$PROJECT" --outDir "$WORK" --noCache
elif [[ -n "${FSHARP_WASM_DIR:-}" ]]; then
  dotnet run -c Release --project "$FSHARP_WASM_DIR/tools/FSharp.Wasm.Cli" -- "$PROJECT" --outDir "$WORK" --noCache
else
  echo "no compiler: set FSHARP_WASM_BIN (a release binary) or FSHARP_WASM_DIR (a fsharp-wasm checkout)" >&2
  exit 1
fi
wasm-tools validate "$WORK/$NAME.wasm" --features all
if [[ $MODULE == server ]]; then
  if wasm-tools print "$WORK/$NAME.wasm" | grep -q 'import "wasm:js-string"'; then
    echo "$NAME.wasm imports wasm:js-string: the compiler did not take WASMGC_STRING_ABI=exports (revision 73131d3 or later needed)" >&2
    exit 1
  fi
  ABI="string ABI: exports"
else
  ABI="string ABI: JS String Builtins"
fi
mkdir -p "$OUT"
cp "$WORK/$NAME.wasm" "$OUT/$NAME.wasm"
echo "built: $OUT/$NAME.wasm ($(wc -c < "$OUT/$NAME.wasm") bytes, $ABI)"
if [[ $MODULE == canvas ]]; then
  # the page and what it loads, as one directory that any static host can serve at /canvas/ (and the host of fsharp-wasm-edge packs): index.html, the module, runtime/*.js
  PAGE="$OUT/canvas"
  rm -rf "$PAGE" && mkdir -p "$PAGE/runtime"
  cp "$PROJECT/web/index.html" "$PAGE/index.html"
  cp "$OUT/$NAME.wasm" "$PAGE/$NAME.wasm"
  cp "$FSHARP_RUNTIME_DIR"/runtime/*.js "$PAGE/runtime/"
  echo "page:  $PAGE ($(ls "$PAGE/runtime" | wc -l) runtime modules)"
fi
