# Vixen.TailwindParity

Checks the Tailwind side of [doc 43's parity ledger](../../docs/plan/43-web-styling-parity.md)
against a committed snapshot of what `tailwindcss` actually registers.

```bash
dotnet run --project Tools/Vixen.TailwindParity
```

It reads three files, prints what disagrees on stderr in MSBuild's diagnostic shape, and exits 1 if
anything does. `Vixen.TailwindParity.Tests` calls the same code against the same three files on every
test run, which is what makes this a gate rather than a script somebody remembers.

| File | What it is |
| --- | --- |
| `docs/plan/43-web-styling-parity.tsv` | The ledger. Only its `root`, `kind`, `example` and `classes` columns are read here. |
| `docs/plan/tailwind-registry.json` | The snapshot: v4's static roots, functional roots, variants, and the verdict on every class the ledger names. |
| `docs/plan/43-web-styling-unlisted.txt` | Static utilities v4 ships that no row names. A shrinking list, not a waiver. |

## Why the snapshot is committed rather than measured

⚠ **This is the one half of doc 43's cross product that cannot be a test.** Part 0 of the document
sets out three axes: what Tailwind is, which Vixen family answers each root, and what the engine does
with what that family emits. The third is measured on every run by `ParityLedger` in
`Vixen.Ui.Styling.Utilities.Tests` — that is what stops the ledger drifting against the engine. The
first was a hand transcription of `__unstable__loadDesignSystem()` dated **2026-08-07**, and nothing
in this repository could read it, because `tailwindcss` is an npm package and this is not a
JavaScript repository.

So the measurement is taken by hand, into a file, and everything downstream of it is a test over that
file. The snapshot is 890 static roots, 315 functional roots, 88 variants and a compile verdict for
each of the ~950 class names the ledger mentions — about 50 KB, and it changes when the pinned
version does.

⚠ **It records what it was asked as well as what it refused.** A snapshot holding only the refusals
would answer "is this a real class?" with silence for a name it had never seen, so a row added after
the snapshot was taken would pass the check with nothing having looked at it. A class outside
`checked` fails as TWP004 — *the snapshot is stale* — which is a different sentence from *this class
is wrong*, and both are failures.

## Re-taking the snapshot

```bash
cd Tools/Vixen.TailwindParity
npm install          # pulls the tailwindcss version pinned in package.json
npm run snapshot     # writes docs/plan/tailwind-registry.json
```

`node_modules/` is git-ignored and the package is deliberately not vendored. The version written into
the snapshot is whatever npm resolved, not whatever `package.json` asked for, so a lockfile drift
shows up in the diff of the committed file.

Bumping the pinned version is a deliberate act: it will move `staticRoots`, `functionalRoots` and
`variants`, and the findings it produces are the list of what v4 changed under the ledger.

## The findings

| Code | What it means |
| --- | --- |
| `TWP001` | The ledger surveys a functional root v4 does not register. |
| `TWP002` | v4 registers a functional root no ledger row surveys. |
| `TWP003` | A class a row lists compiles to nothing in v4. |
| `TWP004` | A class a row lists that the snapshot was never asked about — re-take it. |
| `TWP005` | A static utility v4 ships that neither the ledger nor the unlisted file names. |
| `TWP006` | An unlisted-file entry a row now lists, so the file must shrink. |
| `TWP007` | An unlisted-file entry v4 does not register at all. |

⚠ **`TWP003` is the one that costs something.** `ParityLedger.Derive` demotes a row from `works` to
`partial` when any class in its `classes` column fails to resolve, and never promotes a row on the
strength of that column. A class Tailwind does not ship is therefore a *permanent* demotion of a root
that may well be finished — the pessimistic error `ParityLedger`'s own remarks call the expensive
kind, arriving through the one column nothing in the tree could read.

The first run of this tool found two: the `backdrop-blur-*` row listed `backdrop-blur-2` and
`backdrop-blur-4`. Those are real classes *in Vixen* — `UtilityFamilies` answers a blur with a named
step **or** a spacing count, and the count is this engine's own extension, written down in its
remarks — and they are not Tailwind classes at all. A Vixen-only spelling had got into the column
that describes Tailwind.

## What it deliberately does not check

**A static row's `root` is a survey heading, not a Tailwind name.** `pointer-events`,
`container (max-w+w)` and eighty-four others group several v4 utilities under one readable label, so
the static half is compared through the class names a row lists rather than through its root.

**Negative spellings.** `-inset-full` is generated from `inset-full` and is the positive root's row's
business; listing both would double the ledger to say nothing.

**Whether a root is implemented well.** That is the ledger's `emits`, `engine_reads` and `state`
columns, and they are re-derived from the engine on every run of
`Core/Vixen.Ui.Styling.Utilities.Tests`.
