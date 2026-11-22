// End-to-end: the canvas client of the Ghost Todo in a real Chromium, against the real Ghost Todo module on the host of fsharp-wasm-edge, next to the page of the host
// (the first todo, DOM).  Both are clients of one server, so what one does the other shows.
//   bash build/build.sh && bash build/build.sh --module canvas && node tests/e2e/canvas.mjs
//   EDGE_DIR            a checkout of fsharp-wasm-edge (default ../fsharp-wasm-edge)
//   FSHARP_RUNTIME_DIR  a checkout of fsharp-wasm-runtime (default lib/runtime): its e2e helpers start Chromium
import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join, normalize } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = fileURLToPath(new URL('../..', import.meta.url));
const edge = process.env.EDGE_DIR ?? join(root, '../fsharp-wasm-edge');
const runtime = process.env.FSHARP_RUNTIME_DIR ?? join(root, 'lib/runtime');
const { createServer } = await import(pathToFileURL(join(edge, 'host/runtime/server.js')).href);
const { memoryStore } = await import(pathToFileURL(join(edge, 'host/runtime/stores.js')).href);
const { assets } = await import(pathToFileURL(join(edge, 'host/runtime/assets.js')).href);
const { launch, checker } = await import(pathToFileURL(join(runtime, 'tests/e2e/lib.mjs')).href);

// the host: the module answers the API and the page of the host; /canvas/ is the packed page of the canvas client
const app = createServer({ module: await readFile(join(root, 'dist/GhostTodo.wasm')), hostName: 'e2e', store: memoryStore(), assets });
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.wasm': 'application/wasm' };
const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname.startsWith('/canvas/')) {
    try {
      const path = normalize(join(root, 'dist', url.pathname.endsWith('/') ? url.pathname + 'index.html' : url.pathname));
      if (!path.startsWith(join(root, 'dist/canvas'))) throw new Error('outside');
      res.writeHead(200, { 'content-type': types[extname(path)] ?? 'application/octet-stream' });
      return res.end(await readFile(path));
    } catch { res.writeHead(404); return res.end('not found'); }
  }
  const chunks = [];
  for await (const c of req) chunks.push(c);
  const hasBody = req.method !== 'GET' && req.method !== 'HEAD';
  const response = await app.fetch(new Request(`http://${req.headers.host}${req.url}`, {
    method: req.method, headers: Object.entries(req.headers).map(([k, v]) => [k, String(v)]), body: hasBody && chunks.length ? Buffer.concat(chunks) : undefined }));
  res.writeHead(response.status, Object.fromEntries(response.headers));
  res.end(Buffer.from(await response.arrayBuffer()));
});
await new Promise((resolve) => server.listen(0, resolve));
const base = `http://127.0.0.1:${server.address().port}`;
const api = (path, init) => fetch(base + path, init).then((r) => r.json());

const { check, failures } = checker();
const { browser, page, errors } = await launch();
const dom = await browser.newPage();                            // the first todo, on the same server
const waitFor = (fn, what, ms = 8000) => page.waitForFunction(fn, null, { timeout: ms }).then(() => true, () => false);
// what the module tells the page when its list changes: the version, and the todos one per line ("o " open, "x " done)
await page.addInitScript(() => {
  window.__sync = [];
  document.addEventListener('DOMContentLoaded', () => document.getElementById('gfx').addEventListener('gfx', (e) => { if (e.detail.kind === 'sync') window.__sync.push({ version: Number(e.detail.a), todos: e.detail.b ? e.detail.b.split('\n') : [] }); }));
});
const last = () => page.evaluate(() => window.__sync.at(-1) ?? null);

try {
  await page.setViewportSize({ width: 1000, height: 700 });
  await page.goto(`${base}/canvas/`);
  await page.waitForFunction(() => document.getElementById('status')?.textContent === '', null, { timeout: 30000 });
  check('the module loads and runs', true);
  const painted = async () => page.evaluate(() => {
    const c = document.getElementById('gfx'), d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data, colours = new Set();
    for (let i = 0; i < d.length; i += 4 * 17) colours.add((d[i] << 16) | (d[i + 1] << 8) | d[i + 2]);
    return colours.size;
  });
  await page.waitForTimeout(800);
  check('the card is painted', (await painted()) > 6);

  // a todo that another client adds: the client learns of it by polling the patches
  const added = await api('/api/todos', { method: 'POST', body: JSON.stringify({ text: 'from the api' }), headers: { 'content-type': 'application/json' } });
  check('another client adds a todo: it arrives as a patch and shows in the shadow', await waitFor(() => window.__sync.some((s) => s.todos.includes('o from the api')), 'sync'), JSON.stringify(await last()));

  // typing into the field and Enter: the request goes out of the module, the patch of the answer is applied
  for (const ch of 'buy milk') await page.keyboard.press(ch);
  await page.keyboard.press('Enter');
  check('typed text and Enter add a todo', await waitFor(() => window.__sync.some((s) => s.todos.includes('o buy milk')), 'sync'), JSON.stringify(await last()));
  const state = await api('/api/state');
  check('… the server has it, at the version the client shows', state.todos.some((t) => t.text === 'buy milk') && (await last()).version === state.version, JSON.stringify({ state, last: await last() }));

  // a click on the check box of the first row (the layout of a 1000 x 700 window: the card is 640 wide, the rows are 44 high, the first one's box is at 213, 179)
  await page.mouse.click(213, 179);
  check('a click on the check box toggles the todo on the server', await waitFor(() => window.__sync.some((s) => s.todos.includes('x from the api')), 'sync'), JSON.stringify(await last()));
  check('… which the server says too', (await api('/api/state')).todos.find((t) => t.id === added.todo.id)?.done === true);

  // the keyboard does the same: from the field Tab goes to the scrollable list (the Add button is off while the field is empty), the next Tab to the first check box,
  // Space toggles it; another Tab goes to its Delete button and Enter deletes the todo
  const done = async () => (await api('/api/state')).todos.map((t) => t.done);
  await page.keyboard.press('Tab');                              // the keys come as fast as a script can send them: each meets the state the one before made
  await page.keyboard.press('Tab');
  await page.keyboard.press(' ');
  check('Tab, Tab and Space toggle the next todo (no mouse)', await waitFor(() => window.__sync.some((s) => s.todos.filter((t) => t.startsWith('x ')).length === 2), 'sync'), JSON.stringify([await done(), await last()]));
  await page.keyboard.press('Tab');
  await page.keyboard.press('Enter');
  check('Tab to Delete and Enter remove it', await waitFor(() => window.__sync.at(-1)?.todos.length === 1 && window.__sync.at(-1).todos[0] === 'x from the api', 'sync'), JSON.stringify(await last()));
  await api('/api/todos', { method: 'POST', body: JSON.stringify({ text: 'second' }), headers: { 'content-type': 'application/json' } });
  await waitFor(() => window.__sync.at(-1)?.todos.length === 2, 'sync');

  // both pages show the same list: the first todo (DOM) polls the same patches
  await dom.goto(`${base}/`);
  await dom.waitForFunction(() => document.querySelectorAll('#list li').length === 2, null, { timeout: 8000 }).catch(() => {});
  const domTexts = await dom.$$eval('#list li span', (els) => els.map((e) => e.textContent));
  check('the page of the host shows the todos of the canvas', domTexts.length === 2 && domTexts.includes('second'), JSON.stringify(domTexts));
  // and what the DOM page does shows on the canvas
  await dom.click('#list li:last-child button');
  check('a todo deleted on the DOM page disappears from the canvas', await waitFor(() => window.__sync.at(-1)?.todos.length === 1, 'sync'), JSON.stringify(await last()));

  check('no page errors', errors.length === 0, JSON.stringify(errors).slice(0, 300));
} catch (e) {
  check('ran to the end', false, String(e.message).split('\n')[0]);
  await page.screenshot({ path: join(root, 'dist/canvas-failure.png') }).catch(() => {});
}

await browser.close();
server.close();
const failed = failures();
console.log(failed ? `\n${failed} check(s) failed` : '\nall e2e checks passed');
process.exit(failed ? 1 : 0);
