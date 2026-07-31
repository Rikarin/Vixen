<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.DocGen

The subject of `nuke Docs`. It reads the engine's own source with Roslyn and emits the graph the
documentation site is rendered from: every public type, classified as **what it actually is** — a
component, a system, a control, an importer, a node, an annotation — rather than as a class with an
attribute on it.

Spec: [docs/plan/25](../../docs/plan/25-documentation-generator-and-site.md).
Measured: [docs/plan/spikes/docs-graph/RESULT.md](../../docs/plan/spikes/docs-graph/RESULT.md).

```bash
./build.sh Docs                     # build Release, emit the graph, check it against the baselines
```

```bash
dotnet run --project Tools/Vixen.DocGen -- Vixen.slnx --output artifacts/docs \
    --configuration Release --commit $(git rev-parse HEAD) --excuse Vixen.Raven.Tests
```

| Option | |
|---|---|
| `--output <dir>` | Where the graph goes. Required. |
| `--configuration <name>` | The **design-time build** configuration. Default `Release`. |
| `--commit <sha>` | The commit source links point at. Without it there are paths and no URLs. |
| `--repository-url <url>` | Default `https://github.com/rikarin/Vixen`. |
| `--verify-baselines` | Also fail when the graph and the `PublicAPI.*.txt` baselines disagree. |
| `--excuse <project>` | Tolerate compile errors in one project, and print that it did. Repeatable. |

## ⚠ The configuration is not cosmetic

**The tree must be built in the configuration this is told about, and `Release` is the default for
the same reason [`Vixen.ApiCheck`](../Vixen.ApiCheck/README.md) uses it.**

The engine resolves its own generators through `ProjectReference` with `OutputItemType="Analyzer"`,
so the analyzer paths a design-time build resolves point at `bin/<Configuration>/…`. Against a tree
built in another configuration those files do not exist, no generator runs, and the graph comes out
plausible and wrong:

| | Debug design-time build, Release tree | `Configuration=Release` |
|---|---|---|
| Source-generated documents | 27 | **539** |
| Projects with compile errors | 40 | **1** |
| Public types | 4 452 | **4 750** |

298 types and four whole kinds vanished, with nothing in the output to say so. Three things now stand
between that and a shipped page: the configuration is passed explicitly, **a project with compile
errors fails the run**, and `--verify-baselines` compares the result with what `CheckApi` has
approved.

The one project that cannot compile in a design-time build is `Vixen.Raven.Tests`, whose parser
visitors come from ANTLR's MSBuild task rather than from a Roslyn generator — a workspace runs
generators, not tasks. It is excused by name, and the excuse is printed.

## What it emits

```
artifacts/docs/
├── graph.json              # the index: every type, enough of each for nav, breadcrumbs and search
└── pages/<namespace>.json  # the detail: members, doc comments, attributes, source spans
```

Split in two because they are loaded at different times — the site holds the whole index and loads
one page chunk per route. Chunks are **per namespace**: per type the median chunk is 428 bytes, so
the chunking would cost more than the content. A namespace past a byte budget is split, because the
largest is already 92 kB in the index tier alone.

## What it documents, and what it does not

`Core`, `Platform`, `Editor`, `Tools` and `Raven`, minus test projects. **Samples and benchmarks are
demonstrations rather than surface** — they come back as examples and sample pages, which is where a
reader wants them, and leaving them in also gives seven types called `Program`.

Scoping is by *area* rather than by packability, deliberately: `graph-node`, `importer` and
`replicated-component` live in assemblies with no baseline, so a tree filtered on `PublicAPI.*.txt`
would document the engine and hide the editor.

⚠ **A documentation id is unique inside an assembly, not across a solution.** This repository links
shared source across project boundaries where an assembly boundary would be the wrong shape —
`Vixen.Core.Syntax` is compiled into `Vixen.Ui.Markup.Generators` as well as its own assembly — so
the same qualified name is two symbols claiming one URL. The packable copy keeps the page, ties break
on the assembly name so the output does not depend on load order, and the assemblies that lost are
recorded on the survivor.

## The taxonomy

Thirteen rules, each reading something the engine already relies on at compile time, most-specific
first. `[Component]` alone is a component; with `[DataContract]` beside it, it is a *scene* component
— the pair is what puts a type in the Add Component menu and into a `.vxscene`, so the pair is its
own kind. `Vixen.DocGen.Tests` has a fixture per rule.

## The facets

The half of the taxonomy that earns it. A kind is a label; these are the facts a page shows, and all
of them are derived from a declaration the compiler already reads:

| Kind | What the page gets | Live example |
|---|---|---|
| Component | Size in bytes, and rows per 16 KB chunk with it alone on the archetype | `RigidBody` — 32 B, 372/chunk |
| System | Phase, ordering, declared reads and writes | `MeshExtractionSystem` — `PreRender` |
| Replicated | Channel, send rate, priority, per-field quantisation and its bit cost | |
| Importer | The extensions it claims | `ModelImporter` — `.fbx .gltf .glb .obj .dae .3ds .ply .stl .blend` |
| Graph node | Create-menu path, which is also the key a saved graph stores | `BurstNode` — `Vfx/Spawn/Burst` |
| Annotation | What it may be put on, and whether it repeats | `ReadsAttribute` — Class, Struct, multiple |

⚠ **A size is null rather than guessed.** A struct holding a reference, a generic parameter or an
explicit layout is one whose size is the runtime's business, and somebody reads this number to decide
whether to split a component.

**And one thing the graph says about the engine rather than about itself: none of the 36 systems
declare `[Reads]` or `[Writes]`.** The attributes exist and the scheduler is built to use them; no
system has been annotated, and `overview.md` carries the same gap as `VIXEN_JOB_SAFETY` access
declarations. A page that says "reads nothing, writes nothing" about a system that plainly writes
something is the documentation making a missing declaration visible.

## Used by

One pass over every syntax tree, recording (enclosing declaration → referenced type) as it goes.
`SymbolFinder.FindReferencesAsync` per symbol over 243 projects is quadratic and unaffordable; this
is **18.5 s for the whole solution**, and 3 494 of 3 588 types come out with at least one user.

⚠ **The pass runs over the projects the graph does not document, deliberately.** A use in a sample is
a worked example and a use in the engine is an implementation detail, so samples are ranked first and
tests and benchmarks are read even though neither gets a page. `Vixen.Ecs.World` has 209 users and the
first six a reader sees are `Arena`, `GameClient`, `GameServer`, `LocalMatch`, `OrbitSystem` and
`Program` — every one of them a sample. 312 types have at least one sample use.

A reference to a member counts as a reference to its type: somebody reading `World` wants to know
that `MovementSystem` calls `World.Query`, not to find `Query` listed on its own. A type referring to
itself is not a use of it. The count is uncapped and feeds search ranking — `Vector3` at 788 is what
a name query should rank first.

## Known gaps

Owed by [25](../../docs/plan/25-documentation-generator-and-site.md) § P1 and not yet built:

- **Classified signature spans** — Roslyn's classifier, so the site ships no highlighter.
- **Non-C# nodes** — Raven shaders from `--emit-reflection`, the diagnostic and log-event registers,
  the `System.CommandLine` trees.
- **Member-level baseline agreement.** Only type declarations are compared; the baseline's member
  format would mean a second signature formatter kept identical to the first.

Licensed under Apache-2.0.
