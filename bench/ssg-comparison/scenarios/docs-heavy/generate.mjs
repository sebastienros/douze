// Generates the pages of the docs-heavy scenario, deterministically: page i only depends on i, so the first N pages of
// a larger set are the N pages of a smaller one. Each page is about 6 to 10 KB of Markdown with a title and a
// description in front matter, h2 and h3 headings, lists, a table, inline code and 3 to 6 code fences in mixed
// languages (js, ts, csharp, yaml, bash, json, html).
//
// Usage: node generate.mjs <count> <out folder>
// Writes <out>/page-00001.md ... and <out>/../sidebar.json (title and url of the 50 first pages).
// The code avoids "{{", "{%" and "{#", which Liquid (Douze, Eleventy, Jekyll) and Hugo would read in the Markdown.
import fs from "node:fs";
import path from "node:path";

const count = Number(process.argv[2] ?? 1000);
const out = process.argv[3] ?? "content";
fs.mkdirSync(out, { recursive: true });

function mulberry32(seed) {
  return () => {
    seed |= 0; seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const words = ("system cache request worker queue route token build render config deploy service client server module " +
  "package option handler stream buffer record schema index layout template session event signal timer metric trace " +
  "policy filter output input source target volume network storage binding resource factory provider adapter").split(" ");
const verbs = "configure deploy render cache validate parse serialize schedule retry resolve register compile publish".split(" ");
const langs = ["js", "ts", "csharp", "yaml", "bash", "json", "html"];

function page(i) {
  const rnd = mulberry32(i * 7919 + 17);
  const pick = a => a[Math.floor(rnd() * a.length)];
  const int = (a, b) => a + Math.floor(rnd() * (b - a + 1));
  const ident = () => pick(words) + pick(words).replace(/^./, c => c.toUpperCase());
  const Ident = () => ident().replace(/^./, c => c.toUpperCase());
  const sentence = () => {
    const n = int(10, 20);
    const ws = [];
    for (let k = 0; k < n; k++) ws.push(k === int(2, n - 1) ? "`" + ident() + "()`" : pick(words));
    const s = ws.join(" ");
    return s[0].toUpperCase() + s.slice(1) + ".";
  };
  const para = () => Array.from({ length: int(2, 3) }, sentence).join(" ");

  const code = {
    js: () => {
      const f = ident(), l = [];
      l.push(`import { ${ident()}, ${ident()} } from "./${pick(words)}.js";`, "");
      l.push(`export async function ${f}(options = {}) {`);
      for (let k = 0; k < int(4, 9); k++) l.push(`  const ${ident()}${k} = await options.${pick(words)}?.(${int(1, 500)}, "${pick(words)}");`);
      l.push(`  if (!options.${pick(words)}) {`, `    throw new Error(\`missing ${pick(words)}: \${options.${pick(words)}}\`);`, "  }");
      l.push(`  return [${int(1, 9)}, ${int(10, 99)}].map((x) => x * ${int(2, 8)});`, "}");
      return l.join("\n");
    },
    ts: () => {
      const T = Ident(), l = [];
      l.push(`export interface ${T} {`);
      for (let k = 0; k < int(3, 6); k++) l.push(`  ${ident()}${rnd() < 0.3 ? "?" : ""}: ${pick(["string", "number", "boolean", "string[]", "Map<string, number>"])};`);
      l.push("}", "");
      l.push(`export class ${T}Service<T extends ${T}> {`, `  private readonly items = new Map<string, T>();`, "");
      l.push(`  constructor(private readonly ${ident()}: number = ${int(1, 100)}) {}`, "");
      l.push(`  ${pick(verbs)}(key: string, value: T): T | undefined {`, `    const previous = this.items.get(key);`, `    this.items.set(key, value);`, `    return previous;`, "  }", "}");
      return l.join("\n");
    },
    csharp: () => {
      const C = Ident(), l = [];
      l.push("using System;", "using System.Collections.Generic;", "using System.Threading.Tasks;", "");
      l.push(`namespace Contoso.${Ident()};`, "");
      l.push(`public sealed class ${C}(I${Ident()} ${ident()})`, "{");
      l.push(`    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);`, "");
      l.push(`    public async Task<int> ${Ident()}Async(string name, int retries = ${int(1, 5)})`, "    {");
      for (let k = 0; k < int(3, 7); k++) l.push(`        var ${ident()}${k} = await Task.FromResult(name.Length * ${int(2, 40)});`);
      l.push(`        _counts[name] = _counts.TryGetValue(name, out var n) ? n + 1 : 1;`, `        return retries > 0 ? _counts[name] : -1;`, "    }", "}");
      return l.join("\n");
    },
    yaml: () => {
      const l = [`# ${sentence()}`, `${pick(words)}:`, `  name: ${ident()}`, `  replicas: ${int(1, 9)}`, `  enabled: ${rnd() < 0.5}`, `  labels:`];
      for (let k = 0; k < int(2, 5); k++) l.push(`    ${pick(words)}: "${pick(words)}-${int(1, 99)}"`);
      l.push(`  ports:`);
      for (let k = 0; k < int(2, 4); k++) l.push(`    - containerPort: ${int(1000, 9999)}`, `      protocol: TCP`);
      return l.join("\n");
    },
    bash: () => {
      const l = ["#!/usr/bin/env bash", "set -euo pipefail", "", `NAME="${ident()}"`];
      l.push(`for dir in ${pick(words)} ${pick(words)} ${pick(words)}; do`, `  mkdir -p "./out/$dir"`, `  echo "${pick(verbs)} $NAME in $dir"`, "done");
      l.push(`if [ -f ./${pick(words)}.lock ]; then`, `  rm -f ./${pick(words)}.lock && exit ${int(1, 3)}`, "fi");
      l.push(`curl -fsSL "https://example.com/${pick(words)}?id=${int(1, 999)}" | grep -c "${pick(words)}"`);
      return l.join("\n");
    },
    json: () => {
      const o = { name: ident(), version: `${int(1, 9)}.${int(0, 20)}.${int(0, 9)}`, private: rnd() < 0.5, scripts: {}, dependencies: {} };
      for (let k = 0; k < int(2, 4); k++) o.scripts[pick(verbs)] = `node ${pick(words)}.js --${pick(words)}`;
      for (let k = 0; k < int(2, 5); k++) o.dependencies[pick(words)] = `^${int(1, 9)}.${int(0, 9)}.0`;
      return JSON.stringify(o, null, 2);
    },
    html: () => {
      const l = [`<section class="${pick(words)}">`, `  <h4>${Ident()}</h4>`, `  <ul>`];
      for (let k = 0; k < int(2, 5); k++) l.push(`    <li><a href="/${pick(words)}/${int(1, 99)}">${pick(words)} ${pick(words)}</a></li>`);
      l.push(`  </ul>`, `  <button type="button" data-${pick(words)}="${int(1, 9)}" disabled>${Ident()}</button>`, `</section>`);
      return l.join("\n");
    },
  };

  const title = `${Ident()} ${pick(words)} ${i}`;
  const parts = [`---`, `title: "${title}"`, `description: "${sentence().replace(/[`"]/g, "")}"`, `---`, "", para(), ""];
  const fences = int(3, 6);
  const sections = int(3, 4);
  let fenced = 0, tabled = false;
  for (let s = 0; s < sections; s++) {
    parts.push(`## ${Ident()} ${pick(words)} ${s + 1}`, "", para(), "");
    if (s === 1 || (!tabled && s === sections - 1)) {
      tabled = true;
      parts.push("| Option | Type | Default | Description |", "|---|---|---|---|");
      for (let r = 0; r < int(3, 6); r++) parts.push(`| \`${ident()}\` | ${pick(["string", "number", "boolean"])} | \`${int(0, 100)}\` | ${sentence()} |`);
      parts.push("");
    }
    parts.push(...Array.from({ length: int(2, 4) }, () => `- ${sentence()}`), "");
    for (let h = 0; h < (rnd() < 0.6 ? 1 : 2); h++) {
      parts.push(`### ${Ident()} ${pick(verbs)} ${s + 1}.${h + 1}`, "", para(), "");
      if (rnd() < 0.5) parts.push(...Array.from({ length: int(2, 4) }, (_, k) => `${k + 1}. ${sentence()}`), "");
    }
    // spread the fences over the sections, the remaining ones in the last section
    const here = s === sections - 1 ? fences - fenced : Math.min(fences - fenced, rnd() < 0.7 ? 1 : 0);
    for (let f = 0; f < here; f++, fenced++) {
      const lang = langs[(i + fenced) % langs.length];
      parts.push("```" + lang, code[lang](), "```", "");
    }
  }
  return { title, md: parts.join("\n") };
}

const sidebar = [];
for (let i = 1; i <= count; i++) {
  const slug = `page-${String(i).padStart(5, "0")}`;
  const { title, md } = page(i);
  fs.writeFileSync(path.join(out, `${slug}.md`), md);
  if (i <= 50) sidebar.push({ title, url: `/docs/${slug}/` });
}
fs.writeFileSync(path.join(out, "..", "sidebar.json"), JSON.stringify(sidebar, null, 2) + "\n");
