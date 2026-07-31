// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

/**
 * The deployment budgets — docs/plan/25 § 6.3 and § Part 5's `Budgets` row.
 *
 * Cloudflare's free plan caps a deployment at 20 000 files of 25 MiB each, and this site is ~4 250
 * of them. The file count is a build failure rather than a note because the deploy that first
 * exceeded the cap would otherwise be the one that discovered it, at the moment the site stopped
 * publishing.
 *
 * ⚠ **What it watches changed in P6.** § 6.3 budgeted four prerendered versions at ~4 500 files
 * each; pinned versions are not prerendered (they would be `noindex`, and the archived graph is one
 * 2.4 MB file), so the deployment does not grow with the release count. What grows it is the sweep —
 * a page per type documented — which is what the per-version budget below is now for.
 *
 * The initial JavaScript budget is not here. Angular already fails the build on it
 * (`angular.json` → `budgets`), and a second opinion in a second place is how two numbers start
 * disagreeing.
 */

import { readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

/** What the site should be. Measured at 4 248; the headroom is for the sweep's pages. */
const FILES_PER_VERSION = 5_000;

/** Cloudflare's free plan, per deployment — the wall, not the target. */
const FILES_PER_DEPLOYMENT = 20_000;

/** Cloudflare's per-file cap, in bytes. */
const MAX_FILE_BYTES = 25 * 1024 * 1024;

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

const rows = [
  ['files, budgeted', measured.files, FILES_PER_VERSION],
  ['files, Cloudflare', measured.files, FILES_PER_DEPLOYMENT],
  ['largest file', measured.largest.bytes, MAX_FILE_BYTES]
];

const failures = rows.filter(([, value, budget]) => value > budget);

for (const [name, value, budget] of rows) {
  const shown = name === 'largest file' ? `${mb(value)} (${measured.largest.path})` : value.toLocaleString('en-US');
  const cap = name === 'largest file' ? mb(budget) : budget.toLocaleString('en-US');

  console.log(`${value > budget ? '✘' : '✔'} ${name.padEnd(20)} ${shown} of ${cap}`);
}

console.log(`  ${mb(measured.bytes)} total in ${directory}`);

if (failures.length > 0) {
  console.error('');
  console.error(
    'The deployment is over budget. § 6.3: the paid plan buys 100 000 files rather than a bigger ' +
      'site, so the question to ask first is which pages multiplied.'
  );
  process.exit(1);
}
