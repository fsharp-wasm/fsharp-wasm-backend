#!/usr/bin/env bash
# build.sh — compile the Ghost Todo module to WebAssembly GC:  dist/GhostTodo.wasm
#
#   bash build/build.sh [--out dist]
#
# The strings cross the host boundary in the Exports ABI (WASMGC_STRING_ABI=exports): the module imports no `wasm:js-string`, so it
# also runs where a module is precompiled without the JS String Builtins (Cloudflare Workers, Vercel Edge).
# The compiler is not part of this repository; first hit wins:
#   FSHARP_WASM_BIN   the self-contained `fsharp-wasm` executable of a release (it carries FSharp.Wasm.Core beside it)
#   FSHARP_WASM_DIR   a checkout of fsharp-wasm (revision 8976acc or later: the Exports ABI): its CLI runs through `dotnet run` and
#                     needs FSHARP_COMPILER = the patched fsc.dll built from the checkout's vendor/fsharp
# `wasm-tools` must be on the PATH.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/build/env.sh"
OUT="$ROOT/dist"
while [[ $# -gt 0 ]]; do
  case $1 in
    --out) mkdir -p "$2"; OUT="$(cd "$2" && pwd)"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 1 ;;
  esac
done
command -v wasm-tools >/dev/null || { echo "wasm-tools not found (cargo install wasm-tools)" >&2; exit 1; }

PROJECT="$ROOT/demos/ghost-todo"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
export WASMGC_STRING_ABI=exports
if [[ -n "${FSHARP_WASM_BIN:-}" ]]; then
  "$FSHARP_WASM_BIN" "$PROJECT" --outDir "$WORK" --noCache
elif [[ -n "${FSHARP_WASM_DIR:-}" ]]; then
  dotnet run -c Release --project "$FSHARP_WASM_DIR/tools/FSharp.Wasm.Cli" -- "$PROJECT" --outDir "$WORK" --noCache
else
  echo "no compiler: set FSHARP_WASM_BIN (a release binary) or FSHARP_WASM_DIR (a fsharp-wasm checkout)" >&2
  exit 1
fi
wasm-tools validate "$WORK/GhostTodo.wasm" --features all
if wasm-tools print "$WORK/GhostTodo.wasm" | grep -q 'import "wasm:js-string"'; then
  echo "GhostTodo.wasm imports wasm:js-string: the compiler did not take WASMGC_STRING_ABI=exports (revision 8976acc or later needed)" >&2
  exit 1
fi
mkdir -p "$OUT"
cp "$WORK/GhostTodo.wasm" "$OUT/GhostTodo.wasm"
echo "built: $OUT/GhostTodo.wasm ($(wc -c < "$OUT/GhostTodo.wasm") bytes, string ABI: exports)"
