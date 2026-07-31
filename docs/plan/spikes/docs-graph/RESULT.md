<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Spike: a documentation graph out of `MSBuildWorkspace` — ✅ **PASSED**

Run on macOS arm64 (Darwin 25.6.0), .NET SDK 10.0.302, `Microsoft.CodeAnalysis.*` 5.6.0,
`Microsoft.Build.Locator` 1.11.2, against `Vixen.slnx` — 243 C# projects, built Release first.

This is [25](../../25-documentation-generator-and-site.md) § P0. It asked three questions and got
three numbers, and it found **five things that change the document** — four of which would have been
found inside P1 as bugs rather than here as decisions, and one of which silently deleted a fifth of
the engine from the graph.

| | Question | Answer |
|---|---|---|
| **(a)** | Does `MSBuildWorkspace` open `Vixen.slnx` on Roslyn 5.x, and how long does the solution take? | **Yes. 34 s to open, 45 s to compile and walk all 243 projects.** The fallback the plan reserved is not needed |
| **(b)** | How large is the emitted graph? | **2.00 MB raw, 0.18 MB Brotli** for the index tier — 4 750 types, 57 077 members |
| **(c)** | Per-type or per-namespace page chunks? | **Per namespace, with a split for outliers.** Per type the median chunk is 428 bytes, which is not a chunk |

---

## (a) The load

```
MSBuild      10.0.302 at /usr/local/share/dotnet/sdk/10.0.302
Opened       243 C# projects in 34.0 s
Failures     26
Compiled     243 projects in 45.1 s (539 source-generated documents)
Errors in    1 projects
```

Eighty seconds, cold, for the whole solution — and it is one process, so a CI job can build the graph
and cache it as an artefact. Three runs measured 46.0 s, 24.3 s and 34.0 s for the open; the spread
is the OS file cache.

**The 26 workspace failures are all one benign shape** and none of them loses a project:

```
Duplicate source file '…/Vixen.Core.IO.Analyzers/AnalyzerReleases.Shipped.md' in project …
```

The analyzer projects list their `AnalyzerReleases.*.md` as `AdditionalFiles` and the SDK adds them
again. It is noise — but it is *the same* noise every run, so `Vixen.DocGen` should classify workspace
diagnostics and fail on any shape it does not recognise. A load that starts dropping projects must not
be able to look like a clean run.

**The one remaining compile failure is `Vixen.Raven.Tests`** (546 errors, all
`RavenParserBaseVisitor` and friends). Those types are produced by ANTLR's MSBuild task, not by a
Roslyn generator, and a design-time build does not run it — the `.g4` files are the differential
oracle [18](../../18-raven-parser-migration.md) kept. It is a test project, so it is outside the
documented surface either way, but it is the proof that **MSBuild-task-generated sources are invisible
to the workspace** where Roslyn-generated ones are not.

## (b) The graph

4 750 public types, 57 077 public members, 364 namespaces, 236 assemblies.

| | Raw | Brotli |
|---|---|---|
| Index tier — id, kind, name, namespace, assembly, summary, attributes, member count, source span | **2.00 MB** | **0.18 MB** |

Brotli at 9 % is what highly repetitive JSON compresses to, and it means the whole index — every type
in the engine — fits inside [25 § Part 7](../../25-documentation-generator-and-site.md#part-7--search)'s
**300 kB eager budget** with room for the search index's own structures. The two-tier search design
survives contact with the real numbers.

⚠ **This is the index, not the pages.** Doc-comment bodies, classified signature spans, member detail
and examples are not in it, and there are 57 077 members to 4 750 types. P1 should expect the page
tier to be an order of magnitude larger, and to be loaded per route rather than eagerly.

### The taxonomy, measured

Every kind rule in [25 § 2.3](../../25-documentation-generator-and-site.md#23-the-taxonomy--the-opinionated-half)
fired against real code, and none of them needed a list to be maintained:

| Kind | All 243 projects | Packable surface |
|---|---|---|
| `class` | 3 040 | 1 091 |
| `struct` | 705 | 581 |
| `enum` | 405 | 286 |
| `ui-control` | 188 | 153 |
| `interface` | 179 | 153 |
| `graph-node` | 70 | — |
| `delegate` | 38 | 37 |
| `system` | 37 | 36 |
| `annotation` | 36 | 22 |
| `generator` | 14 | — |
| `scene-component` | 12 | 8 |
| `importer` | 12 | — |
| `component` | 6 | 4 |
| `replicated-component` | 5 | — |
| `behavior` | 3 | 1 |
| **Total** | **4 750** | **2 372** |

The right-hand column is the 77 assemblies carrying a `PublicAPI.Unshipped.txt` — the surface
`CheckApi` gates and the surface the site's API tree covers: **2 372 types, 28 953 members, 157
namespaces.** The difference between the columns is the editor, the tools, the tests and the samples,
which the *guide* still links into.

⚠ **`graph-node`, `importer` and `replicated-component` have no packable column**, because they live
in `Editor/**` and in assemblies without baselines. Three of the taxonomy's most distinctive kinds are
therefore invisible to an API tree scoped to packable assemblies — **the site must scope by *area*,
not by packability**, or it documents the engine and hides the editor.

### Doc comments already exist

**2 371 of 2 372 packable public types carry a `<summary>`.** Not a sample — all but one, and that one
is generated code. `GenerateDocumentationFile` is on for the RUNTIME profile and
`TreatWarningsAsErrors` makes CS1591 fatal, so the compiler has enforced this since the first commit.

That changes what P7 costs. The [page contract](../../25-documentation-generator-and-site.md#part-0--what-the-documentation-has-to-answer)'s
*what it is* has a first draft on every symbol already; what is missing is *what it is for* and the
onboarding — the half no generator was ever going to write.

## (c) Chunking

Size of the JSON one page would load, per grouping:

| Grouping | Chunks | Median | p95 | Max |
|---|---|---|---|---|
| Per type | 4 750 | 428 B | 578 B | 1 070 B |
| Per namespace | 364 | 2 959 B | 23 412 B | 92 331 B |

**Per namespace wins and it is not close.** 4 750 chunks averaging 428 bytes means 4 750 build
outputs and 4 750 entries in the chunk manifest to deliver 2 MB — the chunking would cost more than
the content it carries.

⚠ **The 92 kB maximum is the number to watch**, because the page tier multiplies these by roughly the
member ratio. P1's emitter should **group per namespace and split any group past a byte budget**
rather than picking one grouping for the whole site, and that budget belongs in `CheckDocs` beside the
search-index ones.

---

## Findings that change the plan

### F1 — The workspace's configuration decides whether a fifth of the engine exists

The finding of the spike, and it fails silently in the direction that looks like success.

`MSBuildWorkspace.Create()` defaults its design-time build to **Debug**. This repository resolves its
own generators through `ProjectReference` with `OutputItemType="Analyzer"`, so the analyzer paths
point at `bin/<Configuration>/…`. Against a Release-built tree those files do not exist, the
generators never run, and every consumer of generated API fails to compile — while the tool happily
emits a graph.

| | `Configuration` unset (Debug) | `Configuration=Release` |
|---|---|---|
| Source-generated documents | **27** | **539** |
| Projects with compile errors | **40** | **1** |
| Public types in the graph | 4 452 | **4 750** |
| Public members | 52 469 | **57 077** |

The 40 failures were all missing generated members — `QueryDescription.WithAll` (the ECS query
arities), `Vixen.Net.Generated`, `Vixen.Shaders.Generated`, `Node.Bind` (the node-graph ports).
**298 types, 4 608 members and four of the taxonomy's kinds were simply absent from the graph**, with
nothing in the output to say so.

**→ `Vixen.DocGen` passes `Configuration` explicitly, defaults it to Release to match `CheckApi`'s
subject, requires the tree to have been built in that configuration, and fails when a project reports
compile errors** — because at that point the graph is describing an engine that does not exist.

### F2 — `Microsoft.CodeAnalysis.CSharp.Workspaces` is not optional, and its absence is silent too

The first run loaded all 243 projects and produced **zero** types. `Project.GetCompilationAsync()`
returned `null` for every one: `SupportsCompilation` is false when the C# language service is not in
the workspace's MEF composition, and referencing `Microsoft.CodeAnalysis.CSharp` is not enough.
Nothing throws.

**→ The package is referenced, and P1 asserts a non-zero type count per project.**

### F3 — MSBL001: the MSBuild assemblies must be excluded at runtime

`Microsoft.Build.Locator` fails the build outright unless `Microsoft.Build` and
`Microsoft.Build.Framework` are referenced with `ExcludeAssets="runtime"` and `PrivateAssets="all"`.
The locator loads them from the SDK; a copy in the output directory is the classic route to an
assembly-load failure at the first `OpenSolutionAsync`.

### F4 — The workspace already runs the generators, so § 3.2's mechanism is wrong

[25 § 3.2](../../25-documentation-generator-and-site.md#32-generated-symbols-are-part-of-the-surface)
says to call `GetSourceGeneratedDocumentsAsync` and add the documents to the compilation. Doing that
throws:

```
System.ArgumentException: Syntax tree already present (Parameter 'trees[0]')
   at Microsoft.CodeAnalysis.CSharp.CSharpCompilation.AddSyntaxTrees(IEnumerable`1 trees)
```

On Roslyn 5.6 the compilation the workspace hands out already contains them — 0 of 539 were missing.
The intent stays and matters more than ever after [F1](#f1--the-workspaces-configuration-decides-whether-a-fifth-of-the-engine-exists);
the mechanism becomes an **assertion**, not an addition.

### F5 — CPM opt-out works, and the version split is real

`ManagePackageVersionsCentrally=false` in the tool's own `.csproj` restores and builds against Roslyn
5.6.0 while every generator in the repository stays on the pinned 4.11.0. No `VersionOverride`, no
change to `Directory.Packages.props`, no minimum-compiler bump.

---

## What this retires

| Risk in [25 § Risks](../../25-documentation-generator-and-site.md#risks) | State |
|---|---|
| **`MSBuildWorkspace` is slow or flaky over 200 projects** | ✅ Retired. 34 s open, 45 s compile, 243/243 projects, one explainable failure |
| **Roslyn version conflict with CPM** | ✅ Retired ([F5](#f5--cpm-opt-out-works-and-the-version-split-is-real)) |
| **The generator misses source-generated API** | ⚠️ **Sharpened, not retired.** It is not a theoretical risk: it happened, on the default settings, and produced a plausible graph. [F1](#f1--the-workspaces-configuration-decides-whether-a-fifth-of-the-engine-exists) is the mitigation and the `CheckApi` agreement test is the gate |
| **The search index outgrows the budget** | 🟡 Index tier 0.18 MB Brotli against a 300 kB budget. The page tier is unmeasured |
| **Prerendering overruns the file limit** | 🟡 Refined: 2 372 packable types + 157 namespaces ≈ 2 550 API pages per version, close to the plan's estimate |

## The reproduction

```bash
dotnet build Vixen.slnx -c Release
dotnet run --project docs/plan/spikes/docs-graph/docsgraph.csproj -c Release
```

Writes `artifacts/docs-spike/graph.json`. `DOCGEN_CONFIGURATION` overrides the design-time
configuration, which is the switch [F1](#f1--the-workspaces-configuration-decides-whether-a-fifth-of-the-engine-exists)
is about. The project is not in `Vixen.slnx`, is not built by CI, and opts out of central package
management deliberately — that opt-out is one of the things being proven.

Licensed under Apache-2.0.
