// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

/**
 * What the deployment measures — docs/plan/25 § 6.3.
 *
 * ⚠ **This is not a gate, and it used to be one.** The numbers began as a hosting platform's caps —
 * 20 000 files of 25 MiB each per deployment — which is what fixed retention at four versions. P6
 * removed the retention arithmetic (pinned versions are not prerendered; the archived graph is one
 * 2.4 MB file), the move to a container image removed the caps, and with them went the reason for a
 * build to fail: a threshold nothing is holding the site to is one nobody should be woken up by.
 *
 * So it is a command somebody runs — `pnpm check` — and it still exits non-zero, because a script
 * that reports a number and always succeeds is one nobody reads the output of. What it is worth
 * running for is the sweep: the file count is what a page per documented type grows, and every one
 * of those files is weight in the image a cluster pulls.
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

/**
 * § Part 7's two tiers, Brotli, and this file is the only place that holds them to a number — the
 * builder reports and no longer exits on them, which is what Part 7 asked for in the first place.
 *
 * ⚠ The lazy ceiling is 4 MB rather than Part 7's original 2 MB, and the reason is load time rather
 * than hosting: it is fetched in a Web Worker on the *first query*, off the path of every keystroke
 * before it, so the cost of an extra megabyte is paid once by a reader who has already decided to
 * search. The eager tier keeps 300 kB untouched, because that one is on the first keystroke and is
 * the only one of the two a reader can feel.
 */
const EAGER_BUDGET = 300 * 1024;
const LAZY_BUDGET = 4 * 1024 * 1024;

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
