# source build/env.sh — the environment of a build in this repository.
#   FSHARP_WASM_CORE_DIR  a checkout of fsharp-wasm-core (default: the submodule lib/fsharp-wasm-core)
_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export FSHARP_WASM_CORE_DIR="${FSHARP_WASM_CORE_DIR:-$_root/lib/fsharp-wasm-core}"
unset _root
