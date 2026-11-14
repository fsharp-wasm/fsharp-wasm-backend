// The same scenario on every platform, and the transcripts compared.
//   node build/package.mjs && node tests/platforms/run.mjs [platform …]
// Needs Node 24 (the packaged `node` and the in-process targets use the string builtins only if the module asks for them; the
// Exports ABI module needs none, but Node 24 is what Vercel and Netlify run), `deno` and `bun` on the PATH or in $TOOLS_BIN (deno).
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { runScenario } from "./scenario.mjs";
import { targets } from "./targets.mjs";

const only = process.argv.slice(2);
const selected = targets.filter((t) => !only.length || only.includes(t.name));
if (!selected.length) { console.error(`no such platform; known: ${targets.map((t) => t.name).join(" ")}`); process.exit(2); }

const dir = await mkdtemp(join(tmpdir(), "ghost-platforms-"));
const results = [];
for (const target of selected) {
  const t0 = Date.now();
  const outcome = { name: target.name, kind: target.kind, host: target.host };
  try {
    let run = await target.start({ dir });
    const result = await runScenario(run.send, { host: target.host });
    Object.assign(outcome, result);
    if (target.persistent) {
      await run.stop();                                             // a restart: the state must come back from the platform's storage
      run = await target.start({ dir });
      const after = await run.send("GET", "api/state");
      outcome.persisted = JSON.stringify(JSON.parse(after.text)) === JSON.stringify(result.finalState);
    }
    await run.stop();
  } catch (e) {
    outcome.error = String(e?.stack ?? e).split("\n").slice(0, 8).join("\n");
  }
  outcome.seconds = ((Date.now() - t0) / 1000).toFixed(1);
  results.push(outcome);
}
await rm(dir, { recursive: true, force: true });

let failed = 0;
console.log("");
for (const r of results) {
  if (r.error) { failed++; console.log(`✗ ${r.name.padEnd(18)} ${r.kind.padEnd(9)} did not run:\n    ${r.error.split("\n").join("\n    ")}`); continue; }
  const bad = r.checks.filter((c) => !c.ok);
  const persisted = r.persisted === undefined ? "persistence n/a" : r.persisted ? "persisted across a restart" : "NOT persisted";
  if (bad.length || r.persisted === false) failed++;
  console.log(`${bad.length || r.persisted === false ? "✗" : "✓"} ${r.name.padEnd(18)} ${r.kind.padEnd(9)} ${r.checks.length - bad.length}/${r.checks.length} checks, ${persisted}, ${r.seconds}s`);
  for (const c of bad) console.log(`    ✗ ${c.name}  ${c.extra}`);
}

// identical behaviour: the transcripts of the platforms that ran are the same
const ran = results.filter((r) => r.transcript);
if (ran.length > 1) {
  const reference = ran[0];
  for (const r of ran.slice(1)) {
    const a = reference.transcript, b = r.transcript;
    let diff = a.length !== b.length ? `lengths ${a.length} / ${b.length}` : "";
    for (let i = 0; !diff && i < a.length; i++) if (JSON.stringify(a[i]) !== JSON.stringify(b[i])) diff = `step "${a[i][0]}":\n      ${reference.name}: ${JSON.stringify(a[i]).slice(0, 220)}\n      ${r.name}: ${JSON.stringify(b[i]).slice(0, 220)}`;
    if (diff) { failed++; console.log(`✗ ${r.name} answers differently from ${reference.name}: ${diff}`); }
  }
  if (!failed) console.log(`✓ all ${ran.length} platforms give identical answers (${reference.transcript.length} steps)`);
}
console.log(failed ? `\n${failed} problem(s)` : "\nall platforms passed");
process.exit(failed ? 1 : 0);
