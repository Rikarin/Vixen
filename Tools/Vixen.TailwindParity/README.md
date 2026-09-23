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
| `docs/plan/43-web-styling-variants-unsupported.txt` | Variants v4 has that `Variants.TryResolve` refuses. Also shrinking. |

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

⚠ **And it asks two control questions nobody needed answered.** Every other name it asks about comes
out of the ledger, so on a healthy ledger `refused` is *empty* — and an empty array cannot be told
apart from a generator that stopped recording refusals, or a compiler that answered every question
with success. So `p-4` and `vixen-parity-control-no-such-utility` are always asked, the first must
compile and the second must not, and `ParityAuditTests` spends both. Neither is a ledger name, so
neither can produce a finding of its own — what they buy is that `refused: []` means *nothing was
refused* rather than *nothing was measured*.

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
| `TWP008` | A variant v4 has that Vixen refuses and the unsupported file does not name. |
| `TWP009` | An unsupported-file entry Vixen resolves now, so the file must shrink. |
| `TWP010` | A variant v4 compiles no class from, so nothing here can ask about it. |
| `TWP011` | An unsupported-file entry v4 does not register at all. |
| `TWP012` | A count doc 43's prose states that the artefact holding it disagrees with. |
| `TWP013` | A gated sentence of doc 43 that matches zero times or twice — reworded, so nothing checks it. |

⚠ **`TWP003` is the one that costs something.** `ParityLedger.Derive` demotes a row from `works` to
`partial` when any class in its `classes` column fails to resolve, and never promotes a row on the
strength of that column. A class Tailwind does not ship is therefore a *permanent* demotion of a root
that may well be finished — the pessimistic error `ParityLedger`'s own remarks call the expensive
kind, arriving through the one column nothing in the tree could read.

⚠ **But it is a two-step, and the first step is `TWP004`.** The snapshot records a refusal only for
a class the ledger named *when it was taken*, so putting a Vixen-only spelling back into a row today
reddens the gate as TWP004 — "the snapshot was never asked about it, re-take it" — and TWP003 only
follows once somebody has. Each message names the next step, and
`A_ledger_row_naming_a_class_the_real_snapshot_refuses_is_TWP003` exercises the second half against
the real snapshot without needing npm, by naming the control class the generator always asks about.

The first run of this tool found two: the `backdrop-blur-*` row listed `backdrop-blur-2` and
`backdrop-blur-4`. Those are real classes *in Vixen* — `UtilityFamilies` answers a blur with a named
step **or** a spacing count, and the count is this engine's own extension, written down in its
remarks — and they are not Tailwind classes at all. A Vixen-only spelling had got into the column
that describes Tailwind.

## The variants, which the ledger has no row for

A variant emits no property, so `UtilityConsumptionGateTests` never sees one and doc 43's `.tsv` —
one row per utility root — has nowhere to put it. What stood in for a measurement was prose: A12's
row saying seven pseudo-element variants "wait behind the generated box", carried through six audits
of issue #233 and never checked.

It is **21 of v4's 88**, and they are in `docs/plan/43-web-styling-variants-unsupported.txt` with the
reason grouped above each block. ⚠ The answer comes from `Variants.TryResolve` — the call the
generator itself makes — over a **probe class** rather than over a name, because a variant is a
prefix: "does Vixen have `before`?" is only answerable as "does `before:p-4` resolve?". The probe is
chosen by the snapshot generator from forms **v4 accepts**, bare first and arbitrary last, so nothing
here claims to know what any variant takes.

⚠ **That ordering is load-bearing and the first draft had it wrong.** `group-[3]` compiles in v4 and
Vixen refuses every arbitrary `group-`, so an arbitrary-first probe recorded `group` as a variant
Vixen does not have — while `group-hover:` works. Two variants were libelled that way before the
forms were ordered.

⚠ This is doc 43's `expires-on` mechanism in the one form that cannot be walked past. A refusal in a
comment expires unobserved; a refusal here stops the build the moment it stops being true, because
deleting the line is what makes it green again.

## What it deliberately does not check

**A static row's `root` is a survey heading, not a Tailwind name.** `pointer-events`,
`container (max-w+w)` and eighty-four others group several v4 utilities under one readable label, so
the static half is compared through the class names a row lists rather than through its root.

**Negative spellings.** `-inset-full` is generated from `inset-full` and is the positive root's row's
business; listing both would double the ledger to say nothing.

**Whether a root is implemented well.** That is the ledger's `emits`, `engine_reads` and `state`
columns, and they are re-derived from the engine on every run of
`Core/Vixen.Ui.Styling.Utilities.Tests`.

## The document's own numbers

⚠ **Doc 43 states counts in prose that these artefacts measure, and `ProseAudit` checks them.** It is
here because the batch that made these numbers measurable left one of them asserted: one commit wrote
"163 of v4's 890 static utilities are named by no row", the next shrank that list to 150 and did not
revisit the sentence, a third edited the same document and did not either. Nothing could see it,
because the audit read the `.txt` and never the `.md`.

⚠ **A prose gate's real failure is going quiet, not going wrong.** Reword a gated sentence and a regex
stops matching, and a check that simply compares whatever it found reports nothing. So each claim must
match **exactly once**: zero matches and two matches are both `TWP013`. Rewording one of these
sentences is therefore meant to cost an edit in `ProseAudit.cs` — that is the price of the number
staying checked. The patterns are whitespace-tolerant because the document is hard-wrapped and a
sentence moves across the wrap column whenever a word above it changes.

Only claims whose source is a file in `RepositoryFiles` are gated. Doc 43's other numbers come from
suites elsewhere and are their business.
