// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

/**
 * The deployment budgets — docs/plan/25 § 6.3 and § Part 5's `Budgets` row.
 *
 * ⚠ **What these watch changed twice.** They began as a hosting platform's caps — 20 000 files of
 * 25 MiB each per deployment — which is what fixed retention at four versions. P6 removed the
 * retention arithmetic (pinned versions are not prerendered; the archived graph is one 2.4 MB file),
 * and the move to a container image removed the caps themselves: the site is a directory inside an
 * image now, and nothing outside this file counts its files.
 *
 * They are still gates, because the numbers they watch are still the ones that go wrong. The file
 * count is what the sweep grows — a page per type documented — and every one of those files is a
 * layer a cluster pulls. The largest-file check is unchanged in spirit: a single file that big in a
 * prerendered documentation site is a mistake, not a page.
 *
 * The initial JavaScript budget is not here. Angular already fails the build on it
 * (`angular.json` → `budgets`), and a second opinion in a second place is how two numbers start
 * disagreeing.
 */

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

/** What the site should be. Measured at 4 248; the headroom is for the sweep's pages. */
const FILES_PER_VERSION = 5_000;

/** No page is this big. A file that is, is a generator bug that would ship inside the image. */
const MAX_FILE_BYTES = 25 * 1024 * 1024;

/** § Part 7's two tiers, Brotli. The builder fails on them too; this is what CI reads back. */
const EAGER_BUDGET = 300 * 1024;
const LAZY_BUDGET = 2 * 1024 * 1024;

const directory = process.argv[2] ?? 'dist/vixen-docs/browser';

/** @returns {{files: number, bytes: number, largest: {path: string, bytes: number}}} */
function walk(path) {
  let files = 0;
  let bytes = 0;
  let largest = { path: '', bytes: 0 };

  for (const entry of readdirSync(path, { withFileTypes: true })) {
    const child = join(path, entry.name);

    if (entry.isDirectory()) {
      const inner = walk(child);

      files += inner.files;
      bytes += inner.bytes;

      if (inner.largest.bytes > largest.bytes) {
        largest = inner.largest;
      }

      continue;
    }

    const size = statSync(child).size;

    files += 1;
    bytes += size;

    if (size > largest.bytes) {
      largest = { path: child, bytes: size };
    }
  }

  return { files, bytes, largest };
}

let measured;

try {
  measured = walk(directory);
} catch {
  console.error(`error: ${directory} is not there — run \`pnpm build\` first.`);
  process.exit(2);
}

const mb = bytes => (bytes / 1024 / 1024).toFixed(1) + ' MB';

let search = null;

try {
  search = JSON.parse(readFileSync(join(directory, 'search', 'budget.json'), 'utf8'));
} catch {
  // A build with no index is a build that never ran `pnpm generate`; the row says so rather than
  // passing silently.
}

const rows = [
  ['files, budgeted', measured.files, FILES_PER_VERSION],
  ['largest file', measured.largest.bytes, MAX_FILE_BYTES],
  ['search, eager', search ? search.eager : Number.POSITIVE_INFINITY, EAGER_BUDGET],
  ['search, lazy', search ? search.lazy : Number.POSITIVE_INFINITY, LAZY_BUDGET]
];

const failures = rows.filter(([, value, budget]) => value > budget);

for (const [name, value, budget] of rows) {
  const bytes = name === 'largest file' || name.startsWith('search');
  const shown = bytes
    ? `${(value / 1024).toFixed(0)} kB${name === 'largest file' ? ` (${measured.largest.path})` : ' Brotli'}`
    : value.toLocaleString('en-US');
  const cap = bytes ? `${(budget / 1024).toFixed(0)} kB` : budget.toLocaleString('en-US');

  console.log(`${value > budget ? '✘' : '✔'} ${name.padEnd(20)} ${shown} of ${cap}`);
}

console.log(`  ${mb(measured.bytes)} total in ${directory}`);

if (failures.length > 0) {
  console.error('');
  console.error(
    'The deployment is over budget. The question to ask first is which pages multiplied — the ' +
      'budget is per version and the count is meant to move with the sweep, not with a route.'
  );
  process.exit(1);
}
