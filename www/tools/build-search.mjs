// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

/**
 * The search index — docs/plan/25 § Part 7.
 *
 * Built here, at site-build time, and shipped as an **exported** FlexSearch index. Nothing is
 * tokenised in the browser: the worker imports the parts this writes and answers queries against
 * them, which is the difference between a search box that works on the first keystroke and one that
 * spends four seconds indexing 33 000 documents while the reader waits.
 *
 * Two tiers, because one index that answers everything is one that loads too slowly to be used:
 *
 *   * **names** — every type and guide page, name and kind and url. Small enough to answer the first
 *     keystroke, and loaded when the palette opens rather than with the application, so a reader who
 *     never searches pays nothing for it.
 *   * **full** — the same documents plus every member, every guide section, and their summaries,
 *     used-by lists and declared access. Loaded in a Web Worker behind the first query.
 *
 * Run by `pnpm generate`, after `import-graph.mjs` has put the graph where this can read it.
 */

import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { brotliCompressSync } from 'node:zlib';
import FlexSearch from 'flexsearch';

const here = dirname(fileURLToPath(import.meta.url));
const generated = join(here, '..', 'src', 'generated');
const assets = join(here, '..', 'public', 'search');
const graph = JSON.parse(readFileSync(join(generated, 'graph.json'), 'utf8'));

/**
 * `Vixen.Rendering.MeshRenderer` → `Vixen Rendering Mesh Renderer`.
 *
 * ⚠ **This is the field that makes the index usable rather than merely present.** A reader types
 * `meshrend`, or `mesh renderer`, or the namespace — and none of those match a single token of
 * `Vixen.Rendering.MeshRenderer`. Splitting on dots *and* case boundaries at build time costs
 * nothing at query time and is what turns three failed searches into one hit.
 */
function expand(qualifiedName) {
  return qualifiedName
    .replace(/[.`+]/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2');
}

const documents = [];
const names = [];

/**
 * How much of a summary is indexed and stored.
 *
 * ⚠ **Measured, not guessed.** Whole summaries put the index 687 kB over § Part 7's 2 MB: the
 * summary field alone was 823 kB Brotli and the stored copy another 735 kB, across 33 000 documents
 * of which 29 000 are members. A result row shows one line, and a query that only matches the fifth
 * sentence of a member's remarks is not a query anybody typed — so both the index and the store keep
 * the first sentence's worth and the page keeps the rest.
 */
const SUMMARY_LIMIT = 120;

const clamp = text => (text.length > SUMMARY_LIMIT ? `${text.slice(0, SUMMARY_LIMIT)}…` : text);

// ── Types ─────────────────────────────────────────────────────────────────────────────────────

for (const node of graph.Nodes) {
  const id = documents.length;

  documents.push({
    id,
    name: node.Name,
    qualifiedName: `${node.QualifiedName} ${expand(node.QualifiedName)}`,
    summary: clamp(node.Summary ?? ''),
    body: '',
    related: '',
    kind: node.Kind,
    area: node.Area,
    url: `/docs/api/${node.Slug}`,
    label: node.Name,
    context: node.Namespace,
    rank: node.UsedByCount ?? 0
  });

  // The eager tier: what a name query needs and nothing else. One tuple per type, so the whole of
  // it is a few hundred kilobytes rather than a few megabytes.
  names.push([node.Name, node.QualifiedName, node.Kind, node.Slug, node.UsedByCount ?? 0]);
}

// ── Members ───────────────────────────────────────────────────────────────────────────────────
//
// In the full tier only. 29 000 members would swamp both the eager tier and the result list, and
// "which method was it" is a second question rather than a first — but it is a real one, so the
// member carries the anchor that lands the reader on its row.

const pages = join(generated, 'pages');

for (const file of existsSync(pages) ? readdirSync(pages).filter(name => name.endsWith('.json')) : []) {
  for (const node of JSON.parse(readFileSync(join(pages, file), 'utf8'))) {
    for (const member of node.Members ?? []) {
      documents.push({
        id: documents.length,
        name: member.Name,
        // The type's own words are not repeated here: a query for the type finds the type, and
        // 29 000 copies of `Vixen Rendering Mesh Renderer` cost more index than they answer.
        qualifiedName: `${node.QualifiedName}.${member.Name} ${expand(member.Name)}`,
        // ⚠ Empty, and this is the line that fits the index inside § Part 7's 2 MB. Indexing every
        // member's summary cost 808 kB Brotli on its own — 88% of the documents for a query that
        // reads "find the member whose one-line description says…", which nobody types. The member
        // is found by its name; its summary is on the page it links to.
        summary: '',
        body: '',
        // Empty for a member: "who uses this" is a question about a type, and 29 000 copies of a
        // type's user list is 99 kB of index answering it in the wrong place.
        related: '',
        kind: member.MemberKind,
        area: node.Area,
        url: `/docs/api/${node.Slug}#${member.Name.toLowerCase()}`,
        label: `${node.Name}.${member.Name}`,
        context: node.QualifiedName,
        rank: 0
      });
    }

    // The facets are the answer to questions no prose contains — "which system writes Velocity",
    // "what opens an .fbx", "where is the Add node in the menu" — so they go into the type's own
    // document rather than being left to a reader who would have to know the type's name already.
    const facets = node.Facets;

    if (facets) {
      const document = documents.find(candidate => candidate.url === `/docs/api/${node.Slug}`);
      const words = [
        ...[...(facets.Reads ?? []), ...(facets.Writes ?? [])]
          .map(id => id.replace(/^T:/, ''))
          .flatMap(name => [name, expand(name)]),
        // With and without the dot, because a reader types `.fbx` and also types `fbx`.
        ...(facets.Extensions ?? []).flatMap(extension => [extension, extension.replace('.', '')]),
        ...(facets.Permutations ?? []),
        ...(facets.Stages ?? []),
        ...(facets.EmittedBy ?? []),
        ...(facets.MenuPath ? [facets.MenuPath, facets.MenuPath.replace(/[/]/g, ' ')] : []),
        ...(facets.Phase ? [facets.Phase] : []),
        ...(facets.Level ? [facets.Level] : [])
      ];

      if (document && words.length > 0) {
        document.related = words.join(' ');
      }
    }
  }
}

// ── Guide sections ────────────────────────────────────────────────────────────────────────────
//
// One document per *section*, not per page: a reader asking "how do I iterate entities" should land
// on the heading that answers it rather than at the top of a page that mentions it.

const guide = join(generated, 'guide');
const guidePages = existsSync(guide)
  ? readdirSync(guide).filter(name => name.endsWith('.json') && name !== 'index.json')
  : [];

for (const file of guidePages) {
  const page = JSON.parse(readFileSync(join(guide, file), 'utf8'));
  const sections = splitSections(page.Body);

  documents.push({
    id: documents.length,
    name: page.Title,
    qualifiedName: `${page.Title} ${page.Slug.replace(/[/-]/g, ' ')}`,
    summary: clamp(page.Summary),
    body: page.Body.slice(0, 2000),
    related: (page.Tags ?? []).join(' '),
    kind: 'guide',
    area: page.Area,
    url: `/docs/guide/${page.Slug}`,
    label: page.Title,
    context: page.Area,
    rank: 5
  });

  names.push([page.Title, page.Slug, 'guide', page.Slug, 5]);

  for (const section of sections) {
    documents.push({
      id: documents.length,
      name: section.heading,
      qualifiedName: `${page.Title} ${section.heading}`,
      summary: clamp(section.text),
      body: section.text,
      related: (page.Tags ?? []).join(' '),
      kind: 'guide',
      area: page.Area,
      url: `/docs/guide/${page.Slug}#${section.id}`,
      label: `${page.Title} — ${section.heading}`,
      context: page.Area,
      rank: 4
    });
  }
}

function splitSections(body) {
  const sections = [];
  let current = null;

  for (const line of body.replaceAll('\r\n', '\n').split('\n')) {
    const heading = /^#{2,3}\s+(.+)$/.exec(line);

    if (heading) {
      current = {
        heading: heading[1].trim(),
        id: heading[1]
          .trim()
          .toLowerCase()
          .replace(/[^\w\- ]/g, '')
          .replaceAll(' ', '-'),
        text: ''
      };

      sections.push(current);

      continue;
    }

    if (current) {
      current.text += `${line}\n`;
    }
  }

  return sections.map(section => ({ ...section, text: section.text.trim() }));
}

// ── The index ─────────────────────────────────────────────────────────────────────────────────

const index = new FlexSearch.Document({
  tokenize: 'forward',
  document: {
    id: 'id',
    // Weighted by the order a match matters in: an exact name beats a qualified name beats a
    // summary beats the body it appears somewhere in.
    index: [
      { field: 'name', tokenize: 'forward', resolution: 9 },
      { field: 'qualifiedName', tokenize: 'forward', resolution: 7 },
      { field: 'summary', tokenize: 'forward', resolution: 5 },
      { field: 'body', tokenize: 'strict', resolution: 3 },
      { field: 'related', tokenize: 'strict', resolution: 2 }
    ],
    tag: ['kind', 'area'],
    store: ['label', 'context', 'kind', 'url', 'rank']
  }
});

for (const document of documents) {
  index.add(document);
}

rmSync(assets, { recursive: true, force: true });
mkdirSync(assets, { recursive: true });

const parts = [];

await index.export((key, data) => {
  // One file per part, and the manifest lists them: the worker cannot know the keys FlexSearch
  // chose, and guessing them is how an index silently loads half of itself.
  const name = String(key).replace(/[^a-zA-Z0-9._-]/g, '_');

  parts.push({ key: String(key), file: `${name}.json` });
  writeFileSync(join(assets, `${name}.json`), data === undefined ? '' : String(data));
});

writeFileSync(join(assets, 'manifest.json'), JSON.stringify({ parts, documents: documents.length }));

writeFileSync(
  join(generated, 'search-names.ts'),
  `// Generated by tools/build-search.mjs. Do not edit.\n` +
    `import type { SearchName } from '../app/core/model';\n\n` +
    `/** [name, qualifiedName, kind, slug, usedBy] — the eager tier of § Part 7. */\n` +
    `export const NAMES: SearchName[] = ${JSON.stringify(names)};\n`
);

// ── The budgets — § Part 7, enforced rather than noted ─────────────────────────────────────────

const brotli = value => brotliCompressSync(Buffer.from(value)).length;
const eager = brotli(readFileSync(join(generated, 'search-names.ts')));
const lazy = readdirSync(assets).reduce((total, file) => total + brotli(readFileSync(join(assets, file))), 0);

const EAGER_BUDGET = 300 * 1024;
const LAZY_BUDGET = 2 * 1024 * 1024;
const kb = value => `${(value / 1024).toFixed(0)} kB`;

console.log(
  `search: ${documents.length} documents, ${names.length} names — ` +
    `eager ${kb(eager)} of ${kb(EAGER_BUDGET)} Brotli, lazy ${kb(lazy)} of ${kb(LAZY_BUDGET)} Brotli ` +
    `in ${parts.length} parts`
);

if (eager > EAGER_BUDGET || lazy > LAZY_BUDGET) {
  console.error(
    'error: the search index is over the budget in docs/plan/25 § Part 7. An index nobody waits for ' +
      'is the whole point of the two tiers.'
  );

  process.exit(1);
}

// Kept for the site build's own budget check, which sees the numbers rather than recomputing them.
writeFileSync(
  join(assets, 'budget.json'),
  JSON.stringify({ eager, lazy, documents: documents.length, parts: parts.length })
);
