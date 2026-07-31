/// <reference lib="webworker" />
// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import FlexSearch from 'flexsearch';
import type { SearchHit, SearchRequest, SearchResponse } from '../core/search';

/**
 * The full-text tier — docs/plan/25 § Part 7.
 *
 * In a worker because it holds 33 045 documents and answers on every keystroke, and neither of those
 * belongs on the thread that is scrolling the page. It **imports** an index built at site-build time
 * rather than indexing anything: tokenising 33 000 documents in a browser takes seconds, and a
 * search box that is not ready for four seconds is a search box people stop opening.
 */

const index = new FlexSearch.Document({
  tokenize: 'forward',
  document: {
    id: 'id',
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

/** Resolves once every part is in. Queries arriving before then wait rather than answering wrong. */
const ready = (async () => {
  const manifest = (await (await fetch('/search/manifest.json')).json()) as {
    parts: { key: string; file: string }[];
  };

  // Sequential on purpose: `import` mutates one structure, and the register has to be in before the
  // maps that reference it.
  for (const part of manifest.parts) {
    const data = await (await fetch(`/search/${part.file}`)).text();

    index.import(part.key, data as never);
  }
})();

addEventListener('message', async ({ data }: MessageEvent<SearchRequest>) => {
  await ready;

  const { id, query, tags, limit } = data;
  const response: SearchResponse = { id, hits: hitsFor(query, tags, limit ?? 30) };

  postMessage(response);
});

function hitsFor(query: string, tags: string[], limit: number): SearchHit[] {
  if (query.trim().length === 0) {
    return [];
  }

  // Kinds and areas are separate facets, and a chip's id says which it is — `kind:system`,
  // `area:Editor` — so one chip row can filter two tag fields without the caller sorting them out.
  const kinds = tags.filter(tag => tag.startsWith('kind:')).map(tag => tag.slice('kind:'.length));
  const areas = tags.filter(tag => tag.startsWith('area:')).map(tag => tag.slice('area:'.length));

  const found = index.search({
    query,
    limit: limit * 4,
    enrich: true,
    ...(kinds.length > 0 || areas.length > 0
      ? { tag: { ...(kinds.length > 0 ? { kind: kinds } : {}), ...(areas.length > 0 ? { area: areas } : {}) } }
      : {})
  } as never) as unknown as { field: string; result: { id: number; doc: Record<string, unknown> }[] }[];

  const seen = new Map<number, SearchHit>();
  const lowered = query.trim().toLowerCase();

  for (const field of found) {
    for (const row of field.result) {
      if (seen.has(row.id) || !row.doc) {
        continue;
      }

      const label = String(row.doc['label'] ?? '');

      seen.set(row.id, {
        url: String(row.doc['url'] ?? ''),
        label,
        context: String(row.doc['context'] ?? ''),
        kind: String(row.doc['kind'] ?? ''),
        // The field that matched is the strongest ranking signal FlexSearch gives, and it is in the
        // shape of the result rather than in a score: a name hit is a better answer than a body hit.
        field: field.field,
        rank: Number(row.doc['rank'] ?? 0)
      });
    }
  }

  const weight = (hit: SearchHit) =>
    (hit.label.toLowerCase() === lowered ? 1_000_000 : 0) +
    (hit.label.toLowerCase().startsWith(lowered) ? 100_000 : 0) +
    fieldWeight(hit.field) * 1_000 +
    Math.min(hit.rank, 999);

  return [...seen.values()].sort((left, right) => weight(right) - weight(left)).slice(0, limit);
}

function fieldWeight(field: string): number {
  switch (field) {
    case 'name':
      return 5;
    case 'qualifiedName':
      return 4;
    case 'summary':
      return 3;
    case 'body':
      return 2;
    default:
      return 1;
  }
}
