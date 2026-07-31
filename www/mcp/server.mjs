#!/usr/bin/env node
// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

/**
 * `vixen-mcp` — the documentation graph as MCP tools. docs/plan/25 § Part 10.
 *
 * The graph is one artefact with three consumers: the site, the gates, and this. Nothing here reads
 * the engine's source or the published site — it reads exactly what `nuke Docs` emitted, so the
 * answers match the version in the checkout rather than whatever a docs scraper last saw.
 *
 * ⚠ An engine with 3 679 public types is the case where an agent guessing a type name is the whole
 * failure mode. That is what `vixen_search` is for, and why the skill's first rule is to call it.
 */

import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import FlexSearch from 'flexsearch';
import { z } from 'zod';

const here = dirname(fileURLToPath(import.meta.url));

/**
 * Where the graph is, in the order the answers get less specific: what the caller said, what the
 * environment says, the copy a published package carries, and the checkout this file sits in.
 */
function resolveDocs(argv) {
  const flag = argv.indexOf('--docs');
  const candidates = [
    flag >= 0 ? argv[flag + 1] : null,
    process.env.VIXEN_DOCS_DIR,
    join(here, 'data'),
    resolve(here, '..', '..', 'artifacts', 'docs')
  ];

  for (const candidate of candidates) {
    if (candidate && existsSync(join(candidate, 'graph.json'))) {
      return candidate;
    }
  }

  return null;
}

const docs = resolveDocs(process.argv);

if (!docs) {
  console.error(
    'vixen-mcp: no documentation graph.\n' +
      '  Run `./build.sh Docs` in a Vixen checkout, or point at one with --docs <dir> or VIXEN_DOCS_DIR.'
  );
  process.exit(2);
}

const read = (...parts) => JSON.parse(readFileSync(join(docs, ...parts), 'utf8'));
const graph = read('graph.json');

const guideIndex = existsSync(join(docs, 'guide', 'index.json')) ? read('guide', 'index.json') : [];
const releaseIndex = existsSync(join(docs, 'releases', 'index.json')) ? read('releases', 'index.json') : [];

// ── The index ─────────────────────────────────────────────────────────────────────────────────
//
// Built at startup from the index tier: 3 679 nodes and their summaries, which is a second of work
// and no dependency on the site's build. Guide pages go in the same index so one query answers
// "where is this written about" as well as "what is this called".

const search = new FlexSearch.Document({
  tokenize: 'forward',
  document: {
    id: 'key',
    index: ['name', 'qualifiedName', 'summary', 'tags'],
    store: ['key', 'kind', 'name', 'qualifiedName', 'namespace', 'area', 'slug', 'summary', 'usedBy', 'type']
  }
});

for (const node of graph.Nodes) {
  search.add({
    key: node.Id,
    type: 'symbol',
    kind: node.Kind,
    name: node.Name,
    qualifiedName: node.QualifiedName,
    namespace: node.Namespace,
    area: node.Area,
    slug: node.Slug,
    summary: node.Summary ?? '',
    tags: '',
    usedBy: node.UsedByCount ?? 0
  });
}

for (const page of guideIndex) {
  search.add({
    key: `guide:${page.Slug}`,
    type: 'guide',
    kind: page.Kind,
    name: page.Title,
    qualifiedName: page.Slug,
    namespace: page.Area,
    area: page.Area,
    slug: page.Slug,
    summary: page.Summary ?? '',
    tags: (page.Tags ?? []).join(' '),
    usedBy: 0
  });
}

const nodesById = new Map(graph.Nodes.map(node => [node.Id, node]));
const chunkCache = new Map();

/** The page tier, one namespace chunk at a time — the same unit the site loads. */
function chunkFor(node) {
  const directory = join(docs, 'pages');
  const candidates = readdirSync(directory).filter(file => {
    const base = file.slice(0, -'.json'.length).toLowerCase();
    const namespace = node.Namespace.toLowerCase();

    return base === namespace || base.startsWith(`${namespace}.`) || base.startsWith(`${namespace}-`);
  });

  for (const file of candidates) {
    if (!chunkCache.has(file)) {
      chunkCache.set(file, JSON.parse(readFileSync(join(directory, file), 'utf8')));
    }

    const found = chunkCache.get(file).find(entry => entry.Id === node.Id);

    if (found) {
      return found;
    }
  }

  return null;
}

function guide(slug) {
  const file = join(docs, 'guide', `${slug.replaceAll('/', '.')}.json`);

  return existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : null;
}

/**
 * The fenced blocks of a guide page, with the info string that says whether the build compiles them.
 * Read here rather than stored: the body already carries them, and a second copy is a second thing
 * that can drift.
 */
function examplesOf(page) {
  const examples = [];
  const lines = page.Body.split('\n');

  for (let index = 0; index < lines.length; index++) {
    if (!lines[index].startsWith('```')) {
      continue;
    }

    const info = lines[index].slice(3).trim();
    const start = index + 1;

    index++;

    while (index < lines.length && !lines[index].startsWith('```')) {
      index++;
    }

    examples.push({
      page: page.Slug,
      line: start,
      language: info.split(' ')[0] || 'text',
      compiled: info.includes('compile') && !info.includes('no-compile'),
      quotedFromSource: info.includes('snippet'),
      code: lines.slice(start, index).join('\n')
    });
  }

  return examples;
}

const text = value => ({ content: [{ type: 'text', text: typeof value === 'string' ? value : JSON.stringify(value, null, 2) }] });

// ── The tools ─────────────────────────────────────────────────────────────────────────────────

const server = new McpServer({ name: 'vixen-mcp', version: '0.1.0' });

server.registerTool(
  'vixen_meta',
  {
    title: 'What this index is',
    description:
      'Provenance and shape of the documentation graph: the commit and configuration it was read ' +
      'from, how many nodes of each kind it holds, which guide pages exist and which versions are ' +
      'archived. Call this first — the counts tell you what the engine actually offers.',
    inputSchema: {}
  },
  async () => text({
    solution: graph.Solution,
    configuration: graph.Configuration,
    commit: graph.Commit ?? null,
    projects: graph.ProjectCount,
    types: graph.Nodes.length,
    kinds: graph.Nodes.reduce((counts, node) => ({ ...counts, [node.Kind]: (counts[node.Kind] ?? 0) + 1 }), {}),
    namespaces: graph.Namespaces.length,
    guidePages: guideIndex.map(page => page.Slug),
    releases: releaseIndex.map(release => release.Version),
    source: docs
  })
);

server.registerTool(
  'vixen_search',
  {
    title: 'Find a symbol or a page',
    description:
      'Search the engine by name, qualified name or summary. Filter by kind to ask a shape of ' +
      'question rather than a name one: kind="scene-component" is "what can a scene put on an ' +
      'entity", kind="system" is "what runs in the frame". Never guess a type name — this is the ' +
      'tool that stops you having to.',
    inputSchema: {
      query: z.string().describe('What to look for — "entity query", "Vector3", "importer".'),
      kind: z
        .string()
        .optional()
        .describe('One of the taxonomy kinds: class, struct, interface, enum, delegate, component, scene-component, system, behavior, replicated-component, ui-control, graph-node, importer, annotation, generator, shader, diagnostic, log-event — or "guide" for written pages.'),
      area: z.string().optional().describe('Core, Platform, Editor, Tools or Raven.'),
      namespace: z.string().optional().describe('Exact namespace, e.g. Vixen.Ecs.'),
      limit: z.number().int().min(1).max(100).optional()
    }
  },
  async ({ query, kind, area, namespace, limit = 20 }) => {
    const found = search.search(query, { limit: 500, enrich: true });
    const seen = new Map();

    for (const field of found) {
      for (const hit of field.result) {
        if (!seen.has(hit.doc.key)) {
          seen.set(hit.doc.key, hit.doc);
        }
      }
    }

    const rows = [...seen.values()]
      .filter(row => (kind ? row.kind === kind || (kind === 'guide' && row.type === 'guide') : true))
      .filter(row => (area ? row.area === area : true))
      .filter(row => (namespace ? row.namespace === namespace : true))
      // Exact name first, then how much of the engine uses it: a name query for `World` should not
      // rank `WorldRendererOptions` above it, and `Vector3` (788 users) is what somebody means.
      .sort((left, right) => {
        const exact = other => (other.name.toLowerCase() === query.toLowerCase() ? 1 : 0);

        return exact(right) - exact(left) || right.usedBy - left.usedBy;
      })
      .slice(0, limit)
      .map(row => ({
        id: row.key.startsWith('guide:') ? row.key.slice('guide:'.length) : row.key,
        type: row.type,
        kind: row.kind,
        name: row.name,
        qualifiedName: row.qualifiedName,
        area: row.area,
        summary: row.summary || null,
        usedBy: row.usedBy || undefined,
        url: row.type === 'guide' ? `/docs/guide/${row.slug}` : `/docs/api/${row.slug}`
      }));

    return text({ query, matches: rows.length, results: rows });
  }
);

server.registerTool(
  'vixen_symbol_get',
  {
    title: 'Read one symbol',
    description:
      'The whole node: signature, doc comment, members, the kind-specific facts (a component\'s ' +
      'size, a system\'s phase and declared access, a shader\'s bindings), what uses it, its guide ' +
      'page if one claims it, and a GitHub link at the documented commit.',
    inputSchema: {
      id: z.string().describe('A documentation id (T:Vixen.Ecs.World) or a qualified name (Vixen.Ecs.World).')
    }
  },
  async ({ id }) => {
    const node =
      nodesById.get(id) ??
      nodesById.get(`T:${id}`) ??
      graph.Nodes.find(candidate => candidate.QualifiedName === id) ??
      graph.Nodes.find(candidate => candidate.Name === id);

    if (!node) {
      return text(`No symbol named ${id}. Use vixen_search — the graph has ${graph.Nodes.length} of them.`);
    }

    const detail = chunkFor(node) ?? node;
    const signature = spans => (spans ?? []).map(span => span[0]).join('');

    return text({
      id: detail.Id,
      kind: detail.Kind,
      qualifiedName: detail.QualifiedName,
      assembly: detail.Assembly,
      area: detail.Area,
      signature: signature(detail.Signature),
      summary: detail.Summary ?? null,
      remarks: detail.Remarks ?? null,
      obsolete: detail.Obsolete ?? null,
      baseType: detail.BaseType ?? null,
      interfaces: detail.Interfaces ?? [],
      facets: detail.Facets ?? null,
      members: (detail.Members ?? []).map(member => ({
        id: member.Id,
        kind: member.MemberKind,
        signature: signature(member.Signature),
        summary: member.Summary ?? null,
        obsolete: member.Obsolete ?? null
      })),
      usedBy: (detail.UsedBy ?? []).map(reference => reference.Name),
      usedByCount: detail.UsedByCount ?? 0,
      guide: detail.Docs ? `/docs/guide/${detail.Docs}` : null,
      source: detail.Source?.Url ?? detail.Source?.Path ?? null,
      url: `/docs/api/${detail.Slug}`
    });
  }
);

server.registerTool(
  'vixen_guide_get',
  {
    title: 'Read a written page',
    description:
      'A guide page in full, as markdown: what the feature is, what it is for, how to use it, ' +
      'examples, and where to go next. This is the half of the documentation nobody can generate.',
    inputSchema: {
      slug: z.string().describe('A guide slug, e.g. ecs/queries. vixen_meta lists them.')
    }
  },
  async ({ slug }) => {
    const page = guide(slug);

    if (!page) {
      return text(
        `No guide page at ${slug}. There are ${guideIndex.length}: ${guideIndex.map(entry => entry.Slug).join(', ')}`
      );
    }

    return text({
      title: page.Title,
      slug: page.Slug,
      area: page.Area,
      kind: page.Kind,
      summary: page.Summary,
      documents: page.Api,
      tags: page.Tags,
      status: page.Status,
      related: page.Related,
      body: page.Body,
      edit: page.Edit ?? null,
      url: `/docs/guide/${page.Slug}`
    });
  }
);

server.registerTool(
  'vixen_examples',
  {
    title: 'Code that compiles',
    description:
      'Every fenced example in the guide, with whether the build compiles it. A block marked ' +
      'compiled is checked against the engine on every CI run, so it is safe to copy; one that is ' +
      'not carries the reason it is exempt. Filter by page or by the symbol a page documents.',
    inputSchema: {
      slug: z.string().optional().describe('A guide slug, e.g. ecs/queries.'),
      symbol: z.string().optional().describe('A documentation id — returns examples from the page that documents it.'),
      compiledOnly: z.boolean().optional().describe('Only blocks the build compiles. Default false.')
    }
  },
  async ({ slug, symbol, compiledOnly = false }) => {
    const slugs = slug
      ? [slug]
      : symbol
        ? guideIndex
            .map(entry => guide(entry.Slug))
            .filter(page => page?.Api?.includes(symbol) || page?.Api?.includes(`T:${symbol}`))
            .map(page => page.Slug)
        : guideIndex.map(entry => entry.Slug);

    const examples = slugs
      .map(guide)
      .filter(Boolean)
      .flatMap(examplesOf)
      .filter(example => !compiledOnly || example.compiled);

    return text({ pages: slugs, count: examples.length, examples });
  }
);

server.registerTool(
  'vixen_diff',
  {
    title: 'What changed between two versions',
    description:
      'The release table: added, removed, deprecated, and the four kinds of breaking change — ' +
      'signature, shape, behaviour, and the engine-specific one (a component whose size changed, a ' +
      'system whose phase moved, a shader whose bindings moved) that has an identical signature and ' +
      'breaks anyway. Use it before telling somebody to upgrade.',
    inputSchema: {
      version: z.string().optional().describe('The release to read. Default: the newest archived one.'),
      kind: z
        .string()
        .optional()
        .describe('added, removed, deprecated, signature-break, shape-break, semantic-break or engine-break.'),
      breakingOnly: z.boolean().optional()
    }
  },
  async ({ version, kind, breakingOnly = false }) => {
    if (releaseIndex.length === 0) {
      return text('Nothing has been released yet, so there is nothing to diff.');
    }

    const chosen = version ?? releaseIndex[releaseIndex.length - 1].Version;
    const file = join(docs, 'releases', `${chosen}.json`);

    if (!existsSync(file)) {
      return text(
        `No table for ${chosen}. Archived versions: ${releaseIndex.map(release => release.Version).join(', ')}`
      );
    }

    const detail = JSON.parse(readFileSync(file, 'utf8'));
    const breaking = change => change.Kind !== 'added' && change.Kind !== 'deprecated';

    return text({
      version: detail.Release.Version,
      date: detail.Release.Date,
      previous: detail.Previous ?? null,
      counts: detail.Counts,
      changes: detail.Changes.filter(change => (kind ? change.Kind === kind : true)).filter(
        change => !breakingOnly || breaking(change)
      )
    });
  }
);

// ── Start, or check ───────────────────────────────────────────────────────────────────────────

if (process.argv.includes('--self-test')) {
  // Enough to catch the failure that matters — a graph the server cannot read — without a client.
  const hits = search.search('world', { limit: 5, enrich: true });

  console.log(
    `vixen-mcp: ${graph.Nodes.length} types, ${guideIndex.length} guide pages, ` +
      `${releaseIndex.length} releases from ${docs}; "world" matches ${hits.reduce((total, field) => total + field.result.length, 0)}`
  );

  process.exit(0);
}

await server.connect(new StdioServerTransport());
