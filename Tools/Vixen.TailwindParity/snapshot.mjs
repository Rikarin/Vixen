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

// ⚠ A variant is a prefix and not a class, so "does Vixen support `before`?" cannot be asked of the
// name — it has to be asked of a class the name appears in. The probe is found by trying forms in
// order and keeping the first v4 itself compiles, which is why none of this file claims to know what
// `supports-[…]` takes: v4 is asked, and a variant it refuses every form of is recorded with a null
// probe rather than with a guess.
// ⚠ The arbitrary forms are LAST and that ordering is load-bearing. `group-[3]` compiles in v4 and
// Vixen refuses every arbitrary `group-`, so a probe that reached for the arbitrary form first would
// record `group` as a variant Vixen does not have — while `group-hover:` works. The bare form, then
// every value v4 lists, then arbitrary: the first form v4 accepts wins, so the probe is the most
// ordinary spelling of the variant that exists rather than the most exotic.
const bodies = ['-[3]', '-[a=b]', '-[display:grid]', '-[&_p]'];
const variants = [];

for (const variant of ds.getVariants()) {
    const dash = variant.hasDash ? '-' : '';
    const forms = [
        ...(variant.hasDash ? [variant.name] : []),
        ...(variant.values ?? []).map((value) => variant.name + dash + value),
        ...bodies.map((body) => variant.name + (variant.hasDash ? body : body.slice(1))),
    ];

    const css = ds.candidatesToCss(forms.map((form) => `${form}:p-4`));
    const index = css.findIndex((rule) => rule !== null);

    variants.push({ name: variant.name, probe: index < 0 ? null : forms[index] });
}

variants.sort((a, b) => ordinal(a.name, b.name));

const snapshot = {
    package: 'tailwindcss',
    version,
    taken: new Date().toISOString().slice(0, 10),
    staticRoots: Array.from(ds.utilities.keys('static')).sort(ordinal),
    functionalRoots: Array.from(ds.utilities.keys('functional')).sort(ordinal),
    variants,
    checked,
    refused,
};

const out = path.join(root, 'docs', 'plan', 'tailwind-registry.json');
await fs.writeFile(out, JSON.stringify(snapshot, null, 4) + '\n');

console.log(
    `tailwindcss@${version}: ${snapshot.staticRoots.length} static roots, `
        + `${snapshot.functionalRoots.length} functional roots, ${variants.length} variants `
        + `(${variants.filter((v) => v.probe === null).length} with no probe), `
        + `${checked.length} ledger classes checked, ${refused.length} refused -> ${out}`,
);
