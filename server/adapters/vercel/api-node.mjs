// Vercel Functions on the Node.js runtime (24): web-standard handlers.  State is in memory per instance.
import fs from "node:fs/promises";
import { createServer } from "../runtime/server.js";
import { memoryStore } from "../runtime/stores.js";
import { assets } from "../runtime/assets.js";

const app = createServer({
  module: await fs.readFile(new URL("../GhostTodo.wasm", import.meta.url)),
  hostName: "vercel-node",
  store: memoryStore(),
  assets,
});
const handle = (request) => app.fetch(request);
export { handle as GET, handle as POST, handle as DELETE, handle as OPTIONS, handle as HEAD };
