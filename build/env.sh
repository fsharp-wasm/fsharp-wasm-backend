# source build/env.sh — the environment of a build in this repository.
#   FSHARP_WASM_CORE_DIR  a checkout of fsharp-wasm-core (default: the submodule lib/fsharp-wasm-core)
#   FSHARP_GRAPHICS_DIR   a checkout of fsharp-wasm-graphics (default: lib/graphics)  — only the canvas client
#   FSHARP_RUNTIME_DIR    a checkout of fsharp-wasm-runtime (default: lib/runtime)    — only the canvas client
_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export FSHARP_WASM_CORE_DIR="${FSHARP_WASM_CORE_DIR:-$_root/lib/fsharp-wasm-core}"
export FSHARP_GRAPHICS_DIR="${FSHARP_GRAPHICS_DIR:-$_root/lib/graphics}"
export FSHARP_RUNTIME_DIR="${FSHARP_RUNTIME_DIR:-$_root/lib/runtime}"
unset _root
