// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

// Takes the snapshot `Vixen.TailwindParity` compares the ledger against.
//
// ⚠ This is the ONLY part of doc 43's cross product that cannot be a test: it needs the
// `tailwindcss` package installed, and that package is not in this repository and never will be.
// So the measurement is taken here, by hand, and committed — and everything downstream of it is a
// test over a committed file.
//
//     npm install --no-save tailwindcss@<version>
//     node Tools/Vixen.TailwindParity/snapshot.mjs <repo-root>
//
// It writes `docs/plan/tailwind-registry.json`, and the version it records is whatever `npm`
// actually resolved rather than whatever the command line asked for.
//
// ⚠ `checked` is written out in full, and that is the whole reason this file is not just the two
// root lists. A snapshot that recorded only the refusals would answer "is this class real?" with
// silence for a class it had never seen, so the day somebody adds a row the gate would pass without
// having looked. Recording what was asked lets the tool tell "v4 refuses this" from "nobody has
// asked v4 about this yet", and the second is a failure too.

import { __unstable__loadDesignSystem } from 'tailwindcss';
import { createRequire } from 'node:module';
import fs from 'node:fs/promises';
import path from 'node:path';

const root = process.argv[2] ?? process.cwd();
const require = createRequire(import.meta.url);
const version = require('tailwindcss/package.json').version;

const ds = await __unstable__loadDesignSystem('@import "tailwindcss";', {
    loadStylesheet: async (id, base) => {
        const resolved = require.resolve(id === 'tailwindcss' ? 'tailwindcss/index.css' : id, { paths: [base] });
        return { base, path: resolved, content: await fs.readFile(resolved, 'utf8') };
    },
    loadModule: async () => {
        // A `@plugin` or `@config` would make the snapshot depend on a JS file nobody committed.
        throw new Error('the snapshot is taken against stock tailwindcss and loads no modules');
    },
});

const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

// The ledger names the classes it was surveyed against, so those are the ones to ask about. Reading
// them here rather than enumerating `getClassList()` is deliberate: `getClassList()` is 23 286
// names, it omits every arbitrary value (`aspect-16/9`, `bg-size-[auto]`) and it omits v4's
// compatibility spellings (`bg-gradient-to-t`, `flex-shrink-0`) — all of which compile. Asking the
// compiler about the names the ledger actually uses is the question the ledger needs answered.
const tsv = await fs.readFile(path.join(root, 'docs', 'plan', '43-web-styling-parity.tsv'), 'utf8');
const lines = tsv.split('\n').filter((line) => line.length > 0);
const header = lines[0].split('\t');
const column = Object.fromEntries(header.map((name, index) => [name, index]));

const asked = new Set();

for (const line of lines.slice(1)) {
    const cells = line.split('\t');

    if (cells[column.example]) {
        asked.add(cells[column.example]);
    }

    for (const name of (cells[column.classes] ?? '').split(' ').filter(Boolean)) {
        asked.add(name);
    }
}

const checked = [...asked].sort(ordinal);
const compiled = ds.candidatesToCss(checked);
const refused = checked.filter((_, index) => compiled[index] === null);

const snapshot = {
    package: 'tailwindcss',
    version,
    taken: new Date().toISOString().slice(0, 10),
    staticRoots: Array.from(ds.utilities.keys('static')).sort(ordinal),
    functionalRoots: Array.from(ds.utilities.keys('functional')).sort(ordinal),
    variants: Array.from(ds.variants.keys()).sort(ordinal),
    checked,
    refused,
};

const out = path.join(root, 'docs', 'plan', 'tailwind-registry.json');
await fs.writeFile(out, JSON.stringify(snapshot, null, 4) + '\n');

console.log(
    `tailwindcss@${version}: ${snapshot.staticRoots.length} static roots, `
        + `${snapshot.functionalRoots.length} functional roots, ${snapshot.variants.length} variants, `
        + `${checked.length} ledger classes checked, ${refused.length} refused -> ${out}`,
);
