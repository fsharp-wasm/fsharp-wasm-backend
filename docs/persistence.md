# How the Ghost Todo persists its state

Measured on 2026-11-22 (Node 22, Bun 1.4, workerd; the numbers are in `bench/README.md` of fsharp-wasm-edge).

## What was wrong

Ghost's idea is that a change is a patch: the server sends clients the patch, they apply it to a shadow copy of the list. But the server persisted something else, the whole list as one string, after every change.
That is two forms of the same history, and the second one is the expensive one: a toggle in a list of 5 000 todos wrote 218 000 characters (JSON of the tree, the string across the host boundary, the write) and took 9.78 ms
on Node, nearly what the whole state costs (6.81 ms). The work was on the response's path, and edge platforms limit or bill CPU time per request. It is not an error of GGBP; it is a persistence that did not use the
change stream the protocol already had.

## The design: a log and a checkpoint

- **A mutation persists its patch**: `log/<version>` holds the hex the clients receive (a few dozen bytes). The key behind it, `log/<version + 1>`, is deleted, so nothing stands behind the head of the log (an entry of an
  older line of history, left after a gap or by a store that was behind, would otherwise be replayed as the continuation of a newer one; a test found that).
- **A checkpoint** (`state`, the same snapshot as before: version, next id, todos, the last 64 patches) is written by the module's second entry point, `idle`.
- **A cold start** reads the checkpoint and replays `log/<v+1>`, `log/<v+2>`, … with `Shadow.apply`, the transition the clients use (`Replay.fromStore`). A model rebuilt from its log is therefore the model a client holds at that version, and the next
  id follows from the patches. A log that has a gap, or an entry that does not apply, ends there. No key listing is needed: the versions are consecutive, so the store interface stays `Get`, `Put`, `Delete`.
- **The checkpoint is not on the response's path.** When the log has grown by 64 versions, `handle` asks its host for a call of `idle` (`Host.requestIdle`). The host runs it after the response has gone out, as a task of its own in the same queue as the requests
  (the module has one state), after one turn of the event loop so that the response is written first; on Cloudflare the object hands `settled()` to `waitUntil`. `idle` writes the checkpoint and then drops the log entries it covers, in that order, so a
  call that is cut short leaves entries that the next load skips.

## Why not a second task or a second module

A mutation needs no second task: what it must do is write a few dozen bytes. The checkpoint needs the model in memory, so it belongs to the instance that has it: a second wasm module would have to read the state back to fold it, which is the
cost that is to be avoided, and the two would have to agree on who writes what. So it is **one module with two entry points**, and the host schedules the second one after the response. Work that does not need the model of the first (an index, an export) is
what a second module, a Durable Object alarm or a queue would be for; compaction is not.

## What the host does

The file store of Node and Bun is a journal now (a request appends a line with what it changed; it is rewritten as one line when it has outgrown twice its content), because a store that rewrites its file at every flush would put the cost back at the
layer below. Deno KV and Durable Object storage write the changed keys only.

## What it does not solve

- **Two instances at once** (a stateless platform whose instances share one storage): two requests can both write `log/<v+1>`, and one is lost, as one snapshot overwrote the other before. The log would make a conditional write (`put if absent`) enough to detect it
  and retry; that is a store operation the platforms differ on (a Durable Object serialises its requests and does not need it; whether a storage offers a conditional write differs) and it is not built.
- **The checkpoint itself is slow**: for 5 000 todos it is the JSON writer of the compiled code (7 ms). It is off the response, but a request that arrives while it runs waits for it. A JSON writer without the tree (`docs/STATUS.md` of fsharp-wasm) makes it shorter.
- **A real storage write** is not in the benchmark (its store is memory). The bytes per request are: at most 58 characters, where it was 218 000.
