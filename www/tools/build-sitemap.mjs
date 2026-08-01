// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

/**
 * `sitemap.xml` and `robots.txt` — docs/plan/25 § 8.5.
 *
 * ⚠ **Walked out of the build output rather than derived from the route table.** The two would agree
 * almost always, and the interesting case is the one where they do not: a route that failed to
 * prerender is a page that does not exist, and listing it in a sitemap is asking a crawler to fetch
 * a 404 — the one thing a sitemap is supposed to prevent. What is on disk is what is on the site.
 *
 * Run after `ng build`, by `pnpm bundle`.
 */

import { readdirSync, statSync, writeFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

const ORIGIN = 'https://vixenengine.org';
const directory = process.argv[2] ?? 'dist/vixen-docs/browser';

/**
 * Priorities, because a sitemap that says everything is equally important says nothing.
 *
 * The guide is where a reader starts and the API is what they come back to, so the written pages
 * outrank the generated ones — which is also the order the sweep will make true.
 */
function priorityOf(path) {
  if (path === '') {
    return '1.0';
  }

  if (path === 'docs' || path.startsWith('docs/guide')) {
    return '0.9';
  }

  if (path === 'docs/api' || path.startsWith('docs/releases')) {
    return '0.7';
  }

  return path.split('/').length > 3 ? '0.5' : '0.6';
}

function pages(root, current = root) {
  const found = [];

  for (const entry of readdirSync(current, { withFileTypes: true })) {
    const child = join(current, entry.name);

    if (entry.isDirectory()) {
      found.push(...pages(root, child));

      continue;
    }

    if (entry.name !== 'index.html') {
      continue;
    }

    const path = relative(root, current).split(sep).join('/');

    // The 404 is a page the server hands out, not one anybody should be sent to.
    if (path === '404') {
      continue;
    }

    found.push(path);
  }

  return found;
}

const paths = pages(directory).sort();
const stamp = new Date(statSync(join(directory, 'index.html')).mtime).toISOString().slice(0, 10);

const urls = paths
  .map(path => {
    const url = path.length === 0 ? `${ORIGIN}/` : `${ORIGIN}/${path}`;

    return (
      `  <url>\n    <loc>${url}</loc>\n    <lastmod>${stamp}</lastmod>\n` +
      `    <priority>${priorityOf(path)}</priority>\n  </url>`
    );
  })
  .join('\n');

writeFileSync(
  join(directory, 'sitemap.xml'),
  `<?xml version="1.0" encoding="UTF-8"?>\n` +
    `<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${urls}\n</urlset>\n`
);

writeFileSync(
  join(directory, 'robots.txt'),
  [
    '# vixenengine.org — every page here is prerendered and safe to crawl.',
    'User-agent: *',
    'Allow: /',
    '',
    '# The exported search index: 33 000 documents of JSON that answer nothing on their own.',
    'Disallow: /search/',
    '',
    `Sitemap: ${ORIGIN}/sitemap.xml`,
    ''
  ].join('\n')
);

console.log(`sitemap: ${paths.length} pages, robots.txt written to ${directory}`);
