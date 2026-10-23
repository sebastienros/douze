// A static server for the built site, as GitHub Pages serves it: folder urls are their index.html, a folder without a
// trailing slash redirects, missing files get /douze/404.html with a 404 status.
//
//   node scripts/serve.mjs [output folder] [port]

import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";

const root = path.resolve(process.argv[2] ?? "_site");
const port = Number(process.argv[3] ?? 4173);

const types = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".xml": "application/xml",
  ".txt": "text/plain; charset=utf-8",
  ".wasm": "application/wasm",
  ".pf_meta": "application/octet-stream",
  ".pf_fragment": "application/octet-stream",
  ".pf_index": "application/octet-stream",
  ".pf_filter": "application/octet-stream"
};

async function kind(file) {
  try {
    const s = await stat(file);
    return s.isDirectory() ? "dir" : "file";
  } catch {
    return null;
  }
}

createServer(async (req, res) => {
  const url = new URL(req.url, "http://localhost");
  const pathname = decodeURIComponent(url.pathname);
  let file = path.join(root, pathname);

  if (!file.startsWith(root)) {
    res.writeHead(403).end();
    return;
  }

  let k = await kind(file);
  if (k === "dir") {
    if (!pathname.endsWith("/")) {
      res.writeHead(301, { location: `${pathname}/${url.search}` }).end();
      return;
    }
    file = path.join(file, "index.html");
    k = await kind(file);
  }

  if (k !== "file") {
    const notFound = path.join(root, "douze", "404.html");
    res.writeHead(404, { "content-type": types[".html"] });
    res.end(await readFile(notFound).catch(() => "Not found"));
    return;
  }

  res.writeHead(200, { "content-type": types[path.extname(file)] ?? "application/octet-stream" });
  res.end(await readFile(file));
}).listen(port, () => console.log(`Serving ${root} on http://localhost:${port}/douze/`));
