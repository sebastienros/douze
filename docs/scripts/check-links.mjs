// Checks the links of the built site: every internal href and src resolves to a file of the output, and every
// fragment to an id of its target page.
//
//   node scripts/check-links.mjs <output folder> <base folder>      node scripts/check-links.mjs _site douze

import { readFile, readdir, stat } from "node:fs/promises";
import path from "node:path";

const [outputArg = "_site", baseArg = "douze"] = process.argv.slice(2);
const output = path.resolve(outputArg);
const base = `/${baseArg}/`;

async function* walk(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* walk(full);
    else yield full;
  }
}

async function exists(file) {
  try {
    return (await stat(file)).isFile();
  } catch {
    return false;
  }
}

// The file a url is served from: folder urls are their index.html.
function fileOf(url) {
  const clean = decodeURIComponent(url.split(/[?#]/)[0]);
  return path.join(output, clean.endsWith("/") ? `${clean}index.html` : clean);
}

const idCache = new Map();
async function idsOf(file) {
  if (!idCache.has(file)) {
    const html = await readFile(file, "utf8");
    idCache.set(file, new Set([...html.matchAll(/\sid="([^"]+)"/g)].map((m) => m[1])));
  }
  return idCache.get(file);
}

const errors = [];
let checked = 0;

for await (const file of walk(path.join(output, baseArg))) {
  if (!file.endsWith(".html") || file.includes(`${path.sep}pagefind${path.sep}`)) continue;
  const html = await readFile(file, "utf8");
  const page = `/${path.relative(output, file).split(path.sep).join("/")}`;
  // Code blocks show markup as text: only attributes of real elements count.
  const markup = html.replace(/<pre[\s\S]*?<\/pre>/g, "");

  for (const [, attr, raw] of markup.matchAll(/\s(href|src)="([^"]*)"/g)) {
    const url = raw.replaceAll("&amp;", "&");
    if (/^(?:[a-z][a-z0-9+.-]*:|\/\/)/i.test(url)) continue;
    checked++;

    const resolved = url.startsWith("#")
      ? page + url
      : new URL(url, `http://site${page}`).pathname + (url.includes("#") ? url.slice(url.indexOf("#")) : "");

    if (!resolved.startsWith(base)) {
      errors.push(`${page}: ${attr}="${url}" is outside ${base}`);
      continue;
    }

    const target = fileOf(resolved);
    if (!(await exists(target))) {
      errors.push(`${page}: ${attr}="${url}" does not exist`);
      continue;
    }

    const hash = resolved.includes("#") ? decodeURIComponent(resolved.slice(resolved.indexOf("#") + 1)) : "";
    if (hash && target.endsWith(".html") && !(await idsOf(target)).has(hash)) {
      errors.push(`${page}: ${attr}="${url}" has no element with id "${hash}"`);
    }
  }
}

if (errors.length) {
  console.error(errors.join("\n"));
  console.error(`\n${errors.length} broken links out of ${checked} checked`);
  process.exit(1);
}

console.log(`${checked} links checked, none broken`);
