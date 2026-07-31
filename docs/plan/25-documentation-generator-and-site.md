<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Documentation generator and site

Two things, built as one: **a generator** that reads the engine's own source with Roslyn and emits a
typed graph of everything it offers — every public type, but also every *component*, *system*,
*behaviour*, *control*, *shader*, *importer*, *node*, *attribute*, *diagnostic* and *CLI verb*, each
classified as what it actually is — and **a site** that renders that graph beside hand-written prose,
searchable, versioned, and served as static files from Cloudflare.

This document is the plan and the argument for it. It is a separate file for the same reason
[22](22-virtualized-geometry.md), [23](23-bindless-materials.md) and [24](24-blockout-tools.md) are:
it is larger than a row in a status table, it reverses a decision already recorded, and the first
part of it is an argument rather than a schedule.

⚠️ **Amends [02](02-repository-layout.md) § Top level** — `docs/manual/ # user-facing docs (DocFX)`
— **and [12](12-build-ci-and-testing.md) § Nuke**, whose `Docs` target reads *"DocFX over XML doc
comments + `docs/manual`; publishes to GitHub Pages"* and whose `docs.yml` reads *"DocFX → Pages on
main"*. Neither the generator nor the host survives this document.

---

## The row this overturns

[02 § Top level](02-repository-layout.md#top-level) lists, in the tree:

> `└── manual/ # user-facing docs (DocFX)`

DocFX is the obvious choice and it is the wrong one, for a reason that has nothing to do with its
quality. **DocFX documents a .NET library. Vixen is not a .NET library — it is a .NET library with
eight vocabularies layered on top of it**, and every one of those vocabularies is invisible to a tool
that only knows about classes and members:

| What a reader needs to see | What DocFX would show |
|---|---|
| `Velocity` is an **ECS component**: 8 bytes, written by `MovementSystem` in `SimulationPhase`, replicated unreliably at 20 Hz, quantised to 16 bits | A public struct with two float fields |
| `MovementSystem` is a **system**: phase `Update`, after `InputSystem`, reads `Input`, writes `Position` — and here is the resulting order | A public class with an `OnUpdate` method |
| `Lambert.rvn` is a **shader**: two stages, three permutation keys, one `compose` slot, set 0 binding 2, requires `Texture3D` | Nothing. It is not C# |
| `[Component]` is **the attribute that makes a scene able to place a type**, but only next to `[DataContract]` | A sealed class deriving from `Attribute` |
| `VXML1004` is a **recoverable parse diagnostic** whose producer is `Vixen.Ui.Markup.Generators` | Nothing |
| "Which of these 2 372 types do I actually touch to make a thing move?" | An alphabetical member list |

That last row is the whole problem. An engine's documentation fails at the point where a reader who
has installed it cannot tell which forty of two thousand types are the ones they were meant to reach
for. **Classification is not decoration on top of API reference; for an engine it is the reference.**
And classification is derivable — `[Component]` is on the type, `UpdateInGroup` is on the system,
the shader's reflection is already emitted as JSON — so it is derivable *without anybody maintaining
a second list*, which is the only kind of classification that stays true.

Three smaller reasons, each sufficient on its own:

- **The XML file is not the source.** `GenerateDocumentationFile` is `true` only for the RUNTIME
  profile — `Editor/**`, `Tools/**` and `Raven/**` have it off ([Directory.Build.props](../../Directory.Build.props)).
  A tool that reads `.xml` sidecars documents two thirds of the repository and silently skips the
  editor and the CLI. Roslyn over source has no such hole.
- **There is no gate.** DocFX renders what exists and says nothing about what does not. The
  requirement here is *every feature documented*, and a requirement without a build gate is a wish —
  this repository already knows that, which is why [`Vixen.ApiCheck`](../../Tools/Vixen.ApiCheck/README.md)
  exists at all.
- **The output is a site somebody else designed.** xUI is in-house, the reference site
  ([xuijs.org](https://xuijs.org)) is already built out of it, and the components the docs need are
  either there or are a specification away. Taking a fixed template to avoid two weeks of front-end
  work, and then living inside it for the life of the engine, is the wrong trade.

**What DocFX would have been right about**, and what is kept: XML doc comments are the source of
per-symbol prose, and their [documentation-comment IDs](#22-identity-tvixencoregametime) are the
stable identifier. Both are reused verbatim below, so a `<see cref="…"/>` written today resolves to
a link tomorrow without anybody writing a URL.

---

## Part 0 — What the documentation has to answer

Four readers, and a page that serves one badly is not saved by serving another well:

| Reader | Arrives asking | Landing surface |
|---|---|---|
| **The evaluator** | "What is this? What does it do that Unity doesn't? Can it draw a triangle on my phone?" | The overview, the taxonomy indexes, the sample gallery |
| **The newcomer** | "How do I make a cube move?" | Getting started, then tutorials, in an order |
| **The practitioner** | "What is the signature of `World.Query`, and what phase does my system land in?" | The symbol page, reached from search in under five seconds |
| **The agent** | Whatever its user asked it | [The MCP server](#part-10--the-agent-surface), reading the same graph |

Out of that falls **the page contract**, which every feature page must satisfy and which the build
checks:

| Section | Answers | Rule |
|---|---|---|
| `## What it is` | *What is this thing* | One paragraph. No API, no code |
| `## What it is for` | *Why does it exist, what problem does it solve, when do I not want it* | One or two paragraphs |
| `## Using it` | *The onboarding path* — first working use, then the next three things you will need | Prose with code |
| `## Examples` | *Code that runs* | Every fence compiled by the build |
| `## See also` | Where to go next | Links resolved by the build |

Two standing rules govern everything below.

1. **Nothing that the code already states is written by hand.** Signatures, kinds, phases, ports,
   descriptor sets, diagnostic codes, source locations, versions added — all extracted. A page cannot
   describe an API the compiler does not have, because the page does not contain the API.
2. **Nothing a human should have written is generated.** *What it is* and *what it is for* cannot be
   derived from a type declaration and must not be faked from one. The generator's job is to make
   their absence a build failure, not to invent them.

---

## Part 1 — The shape of the system

```
  SOURCES                        GENERATOR                    ARTEFACTS              SITE
┌──────────────────┐
│ Core/ Platform/  │──┐
│ Editor/ Tools/   │  │      ┌──────────────────┐        ┌──────────────┐      ┌──────────────┐
│   (C# via slnx)  │  ├─────▶│  Vixen.DocGen    │───────▶│ graph.json   │─────▶│  www/        │
├──────────────────┤  │      │                  │        │  nodes+edges │      │  Angular 22  │
│ Raven/Library    │──┤      │  Roslyn symbols  │        ├──────────────┤      │  Tailwind 4  │
│   *.rvn + reflect│  │      │  + classifier    │───────▶│ pages/*.json │      │  @xui/*      │
├──────────────────┤  │      │  + Markdig       │        │  compiled md │      │              │
│ docs/guide/**.md │──┤      │  + example check │        ├──────────────┤      │  prerender   │
├──────────────────┤  │      │                  │───────▶│ search docs  │─────▶│  → static    │
│ registers, CLI,  │──┘      └──────────────────┘        └──────────────┘      └──────┬───────┘
│ samples, tests   │                  │                          │                    │
└──────────────────┘                  │                          ▼                    ▼
                                      │                  ┌──────────────┐      ┌──────────────┐
                                      └─── CheckDocs ───▶│ flexsearch   │      │  Cloudflare  │
                                           (CI gate)     │  index (JS)  │      │  assets only │
                                                         └──────────────┘      └──────────────┘
```

Two halves, joined by one identifier.

- **The generated half** is the graph: what exists, what kind of thing it is, how it connects, where
  it lives in the source. It is a fact about the tree at a commit.
- **The written half** is `docs/guide/**`: what a thing is *for*, and how to start using it. It is
  the part nobody can generate.

They join on the documentation-comment ID. A guide page declares `api: [T:Vixen.Ecs.World]`, and from
that one line the site puts the prose on the symbol page, the symbol's signature on the guide page,
the guide in the symbol's breadcrumb, and both into the search index pointing at each other. **A
symbol with no guide page and a guide page with no symbol are both build failures**
([Part 5](#part-5--the-gates)).

---

## Part 2 — The graph

### 2.1 Why source symbols, and not the assembly

`Vixen.ApiCheck` reads the *assembly*, deliberately and correctly: a baseline is a promise about a
shipped package, and the assembly is what a consumer references. Documentation wants four things the
assembly has thrown away:

| Wanted | Assembly | Source symbols |
|---|---|---|
| Doc comments, with `<see cref>` resolvable to a symbol | Only via the `.xml` sidecar, absent for `Editor/`, `Tools/`, `Raven/` | On the symbol, always |
| File and line of the declaration → **a GitHub source link** | No (a PDB, at best, and not for every profile) | `symbol.Locations[0]`, exactly |
| Attribute *arguments* as written — `[Importer(".fbx", ".obj")]`, `[Quantize(-1, 1, 16)]` | Present but awkward, and constants are folded | `AttributeData`, with the syntax beside it |
| "Who uses this" | Not derivable | One semantic walk over every document |

So the two tools read the same surface for different reasons and must agree about it. **That
agreement is a test**: `Vixen.DocGen.Tests` compares the set of public type IDs it produced against
the `PublicAPI.*.txt` baselines, and fails if either side has one the other does not. A generator
that silently drops an assembly is otherwise indistinguishable from an engine that does not have it.

### 2.2 Identity: `T:Vixen.Core.GameTime`

Node IDs are the ECMA-334 documentation-comment ID format — `T:` for types, `M:` for methods, `P:`,
`F:`, `E:`, `N:` for namespaces — which Roslyn produces from any symbol
(`ISymbol.GetDocumentationCommentId()`) and parses back (`DocumentationCommentId.GetFirstSymbolForDeclarationId`).

The format is not chosen for elegance. It is chosen because **`<see cref="World.Query"/>` in a doc
comment is compiled by Roslyn into exactly this string**, so every cross-reference an engineer has
already written for the IDE becomes a working link with no additional syntax, no URL to keep in step,
and no way to link to something that does not exist. The same string is what a guide page's `api:`
list holds, what the search index stores, what the version diff compares, and what the MCP server
takes as an argument.

URLs are derived from the ID, not stored: `T:Vixen.Ecs.World` → `/docs/api/vixen.ecs/world`. The
derivation is one function, tested for round-tripping and for the collisions that matter (arity —
`List'1` and `List'2` — and case, because Cloudflare's asset paths are case-sensitive and Windows
checkouts are not).

### 2.3 The taxonomy — the opinionated half

**A node's kind is a fact about the code, not a label somebody maintains.** Every rule below is
mechanical, and each one is a test in `Vixen.DocGen.Tests` with a fixture type that must classify.

| Kind | Rule | What the page additionally shows |
|---|---|---|
| **Component** | `[Component]` on a struct or class | Size in bytes and the resulting chunk capacity; which systems read and write it; whether the ECS or a bridge owns it |
| **Scene component** | `[Component]` **and** `[DataContract]` | The above, plus: it appears in the Add Component menu and in a `.vxscene`; its serialised member names and aliases; the inspector rows `[Inspector]` produces |
| **System** | Implements `ISystem` or derives `SystemBase` | Phase from `[UpdateInGroup]`; ordering edges from `[UpdateBefore]`/`[UpdateAfter]`; the declared `[Reads]`/`[Writes]` sets and the components they name; the parallelism that follows |
| **Behaviour** | Derives `Vixen.Engine.Behavior` | Lifecycle callbacks it overrides; coroutine support; the component it is stored against |
| **Replicated component** | `[Replicated]` | Channel, send rate, priority; per-field `[Quantize]` ranges and the resulting bits on the wire |
| **RPC** | `[ServerRpc]` / `[ClientRpc]` | Direction, target, channel, the generated sender |
| **UI control** | Public type in `Vixen.Ui.Controls*` deriving the control base | Its VXML tag; `[Parameter]` inputs; events; the VCSS classes and utilities that style it |
| **Graph node** | `[Node("Category/Title")]` | Menu path; input and output ports with types, from the generated port metadata |
| **Asset importer** | `[Importer(".ext", …)]` | Extensions claimed; settings type; artefacts produced; the `.meta` block it reads |
| **Shader** | A `.rvn` in `Raven/Library` | Stages, entry points, `[Permutation]` keys, `compose` slots, descriptor sets and bindings, required capabilities — all from `--emit-reflection` |
| **Annotation** | Derives `Attribute` | Valid targets; **which generator or system reads it** and what that produces — an attribute nobody reads is documented as inert |
| **Generator / analyzer** | Implements `IIncrementalGenerator` or `DiagnosticAnalyzer` | What it emits; its diagnostic codes, joined to [the register](../manual/diagnostic-codes.md) |
| **CLI verb** | A `System.CommandLine` node in `Vixen.Cli`, `raven`, `Vixen.AssetCompiler` | Full usage, options, defaults, exit codes |
| **Job / hot path** | `[HotPath]`, or a type in the job system | Allocation and threading constraints |
| **Type** | Everything else | The ordinary API page |

Ten of those thirteen rules read an attribute this repository already defines and already relies on
at compile time. That is the point: **the taxonomy is free because the engine was already
opinionated enough to declare it.**

### 2.4 Edges

| Edge | From → to | Feeds |
|---|---|---|
| `declares` | namespace → type → member | [Breadcrumbs](#84-breadcrumbs), the nav tree, the URL |
| `inherits`, `implements` | type → type | "Derived types", "Implementations" |
| `reads`, `writes` | system → component | The system-order diagram; a component's "who touches this" |
| `orders-before`, `orders-after` | system → system | The schedule view |
| `replicates` | replicator → component | The networking page |
| `emits` | generator → diagnostic code | The diagnostics index |
| `documents` | guide page ⇄ symbol | The join in [Part 1](#part-1--the-shape-of-the-system) |
| `references` / **`used-by`** | symbol → symbol, weighted by site | Search ranking, and the "used by" facet |
| `demonstrated-by` | symbol → sample or test | "Real usage", linked to GitHub |
| `since`, `removed-in`, `deprecated-in` | symbol → version | [Version badges and the release diff](#part-6--versioning-and-the-release-diff) |

**`used-by` is the highest-value edge and the only expensive one.** `SymbolFinder.FindReferencesAsync`
per symbol over a 200-project solution is quadratic and not affordable. Instead: one pass over every
syntax tree, and for each identifier resolved by the semantic model, record
(enclosing declaration → referenced symbol). One traversal of the whole tree, edges in both
directions, and the *site* of the reference is kept — engine, editor, sample or test — because
"used by `Samples/03-PbrShowcase`" is worth ten times "referenced 400 times".

### 2.5 The node

```jsonc
{
  "id": "T:Vixen.Rendering.Ecs.MeshRenderer",
  "kind": "scene-component",              // Part 2.3
  "name": "MeshRenderer",
  "qualifiedName": "Vixen.Rendering.Ecs.MeshRenderer",
  "namespace": "Vixen.Rendering.Ecs",
  "assembly": "Vixen.Rendering",
  "area": "Rendering",                    // top-level folder + project grouping
  "signature": [ /* classified spans — Part 3.4 */ ],
  "summary": "…",                         // <summary> from the doc comment
  "remarks": "…",
  "docs": "guide/rendering/mesh-renderer", // the `documents` edge, if any
  "members": [ /* nested nodes, same shape */ ],
  "attributes": [ { "id": "T:Vixen.Core.ComponentAttribute", "args": [] } ],
  "facets": {                              // Part 2.6 — everything the search index filters on
    "sizeBytes": 24, "chunkCapacity": 680,
    "writtenBy": ["T:Vixen.Rendering.MeshExtractionSystem"],
    "platforms": ["all"], "obsolete": null, "since": "0.1"
  },
  "source": {                              // Part 2.7
    "path": "Core/Vixen.Rendering/Ecs/MeshComponents.cs",
    "startLine": 41, "endLine": 58,
    "url": "https://github.com/rikarin/Vixen/blob/<sha>/Core/…#L41-L58"
  },
  "examples": [ { "from": "Samples/03-PbrShowcase/Program.cs", "region": "docs:mesh" } ]
}
```

Emitted as one `graph.json` (the index: ids, kinds, names, edges, facets — everything nav, search and
breadcrumbs need) plus one `pages/<slug>.json` per documented node (the body: members, prose,
examples, classified signatures). The split exists so the site can hold the whole index in memory and
load a page's detail only when it is opened.

### 2.6 Facets — what search filters and ranks on

Beyond the obvious (`kind`, `area`, `assembly`, `namespace`): **`tags`** from guide front matter,
**`usedBy`** names, **`platforms`** (from the project's profile and any `#if` guard on the
declaration), **`since`** / **`deprecated`**, **`stability`** (`stable` / `preview` / `internal-ish`),
**`phase`** for systems, **`extensions`** for importers, **`stages`** and **`capabilities`** for
shaders, **`diagnosticCodes`** for generators, and **`hasExample`** — because "show me only the things
with runnable code" is the query a newcomer actually has.

### 2.7 Source links

Every node carries a GitHub URL built from `PackageProjectUrl`, the commit SHA being documented, the
repo-relative path and the line span — an icon on every declaration, opening the file at the line.
Three cases that need deciding once:

- **Generated code** has no file a reader can open. The link points at the *generator* instead, with
  the tooltip saying so. `EmitCompilerGeneratedFiles` output under `obj/` is never linked.
- **Partial types** span files. The link goes to the part carrying the doc comment; the others are
  listed under "Also declared in".
- **The SHA** is the release tag's commit for a released version, and the branch head for `next`. It
  is recorded in the graph, not computed by the site, so an old version's links keep pointing at the
  code that version actually had.

### 2.8 What else becomes a node

Not everything the engine offers is a C# symbol, and the four below are exactly the parts a
symbol-only tool would have left undocumented:

| Source | Becomes | How |
|---|---|---|
| `Raven/Library/**.rvn` | Shader nodes | `raven compile --emit-reflection` in the same build step; the JSON is already the compiler's own contract |
| [`docs/manual/diagnostic-codes.md`](../manual/diagnostic-codes.md), the `AnalyzerReleases.*.md` pairs | Diagnostic nodes | Parsed from the registers, which stay the source of truth — the site is a view, not a second copy |
| [`docs/manual/log-events.md`](../manual/log-events.md) | Log-event nodes | As above, joined to the `[LoggerMessage]` declarations by id |
| `Vixen.Cli`, `raven`, `Vixen.AssetCompiler` | CLI pages | Walk the `System.CommandLine` tree in-process |
| `Samples/**` | Sample pages | `README.md` + the `docs:` regions the guide includes + a screenshot from the golden-image suite where one exists |

---

## Part 3 — Extraction, mechanically

### 3.1 The tool

`Tools/Vixen.DocGen` and `Tools/Vixen.DocGen.Tests`, a `net10.0` console app, driven by
`nuke Docs`:

```bash
./build.sh Docs                       # graph + content + search input, into artifacts/docs
./build.sh Docs --version 0.2.0       # stamp it as a release and write it into the version store
./build.sh CheckDocs                  # the gate: coverage, links, examples, front matter
```

Loading is `MSBuildWorkspace.OpenSolutionAsync("Vixen.slnx")`, measured at **34 s to open and 45 s to
compile and walk all 243 projects** ([the spike](spikes/docs-graph/RESULT.md)). Two facts that decide
the packaging:

- **`.slnx` needs Roslyn 5.x** — `MSBuildWorkspace` gained XML-solution support there
  ([dotnet/roslyn#77326](https://github.com/dotnet/roslyn/pull/77326)); 4.x throws *"No file format
  header found"* ([#73004](https://github.com/dotnet/roslyn/issues/73004)). The repository pins
  `Microsoft.CodeAnalysis.CSharp` at **4.11.0** for the generators, which target the compiler and
  should not be dragged forward by a documentation tool.
- **CPM forbids the override** — `CentralPackageVersionOverrideEnabled` is `false`
  ([Directory.Packages.props](../../Directory.Packages.props)), so a per-project `VersionOverride` is
  not available and raising the shared pin would raise the minimum compiler for every generator.

So **`Vixen.DocGen` sets `ManagePackageVersionsCentrally=false` in its own `.csproj`** and pins its
Roslyn there, with a comment saying why. It is a build-time tool that nothing references and nothing
ships; it is the one place where opting out costs nothing and the alternative costs the generators.

✅ The fallback the earlier draft reserved — read the project list out of the `.slnx` XML and compile
each project independently — **is not needed**, and neither are two packages the draft did not name:
`Microsoft.CodeAnalysis.CSharp.Workspaces`, without which every `GetCompilationAsync()` returns
`null` and the tool emits an empty graph from a green build, and `Microsoft.Build` /
`Microsoft.Build.Framework` with `ExcludeAssets="runtime"`, without which the build fails MSBL001.

### 3.2 Generated symbols are part of the surface, and the configuration decides whether they exist

⚠️ **Corrected by [the spike](spikes/docs-graph/RESULT.md) § F1 and § F4.** Both halves of the earlier
draft were wrong in the same direction — towards a graph that looks complete and is not.

**The workspace already runs the generators.** `Project.GetCompilationAsync()` hands back a
compilation with the generated trees in it; adding them again throws *"Syntax tree already present"*.
So the call to `GetSourceGeneratedDocumentsAsync()` stays, as an **assertion** — a generator that
stopped running looks exactly like a feature that was deleted.

**But it runs them for the configuration it was told about, and the default is Debug.** This
repository resolves its own generators through `ProjectReference` with `OutputItemType="Analyzer"`,
so the analyzer paths point at `bin/<Configuration>/…`. Against a Release-built tree those files do
not exist and the generators silently do not run:

| | Default (Debug) | `Configuration=Release` |
|---|---|---|
| Source-generated documents | 27 | **539** |
| Projects with compile errors | 40 | **1** |
| Public types in the graph | 4 452 | **4 750** |

**298 types and four of the taxonomy's kinds vanished, and the output said nothing.** So the tool
passes `Configuration` explicitly, defaults it to **Release** — matching `CheckApi`'s subject, for
the same reason — requires the tree to have been built in that configuration, and **fails on any
project that reports a compile error**, because from that point the graph describes an engine that
does not exist. The `ApiCheck` cross-check in [2.1](#21-why-source-symbols-and-not-the-assembly) is
the second net under the same hole.

One residue is accepted and documented rather than fixed: **sources produced by an MSBuild task
rather than a Roslyn generator are invisible to a design-time build.** `Vixen.Raven.Tests` and its
ANTLR visitors are the only case, it is a test project, and [18](18-raven-parser-migration.md) is why
it exists at all.

### 3.3 Doc comments

Parsed from the symbol, not from an XML file. `<summary>`, `<remarks>`, `<param>`, `<returns>`,
`<value>`, `<example>`, `<exception>`, `<seealso>` and `<typeparam>` map to fields;
`<see cref>`/`<paramref>` become links; `<c>` and `<code>` become classified code. `<inheritdoc/>` is
resolved against base types and implemented interfaces (breadth-first, base class before interfaces,
cycle-guarded), because the engine uses it and an unresolved one renders as a blank page.

### 3.4 Highlighting comes from the engine's own lexers

Code on the site is **already tokenised when it arrives**. C# spans come from Roslyn's
`Classifier.GetClassifiedSpansAsync`, Raven from the Raven lexer, VXML from `VxmlLexer`, VCSS from the
VCSS one. The site renders spans to `<span class>` and never parses a language.

Three things fall out of that, all of them the reason to do it:

1. **No JavaScript grammar for a language nobody else has.** Shiki has no Raven grammar and no VXML
   grammar, and writing TextMate grammars for two languages whose real lexers are in this repository
   would be a third implementation to keep in step. It would also be the *worst* of the three.
2. **Tokenising is a parse.** A C# fence that Roslyn cannot classify without errors is a broken
   example, and [the gate](#part-5--the-gates) says so.
3. **The browser ships no highlighter at all** — no WASM grammar payload, no highlight pass on
   hydration, and the prerendered HTML is coloured for readers without JavaScript.

---

## Part 4 — The written half

### 4.1 Where it lives

`docs/guide/`, in the repository, versioned and reviewed with the code it describes. Not beside each
project: a reader-facing tutorial that crosses four assemblies has no single project to live in, and
"documentation lives next to code" has already been solved here for the *engineering*-facing text.

Which settles a question this repository has answered twice before and should answer the same way
again — **three places recording the same thing is how they come to disagree**
([overview.md](../overview.md)):

| Text | Lives in | Audience | On the site |
|---|---|---|---|
| Why a subsystem is built this way | The project's `README.md` (136 of them) | Whoever changes it | **Linked**, never copied |
| What is built, and what is owed | [`docs/overview.md`](../overview.md) | The maintainer | Not published |
| Why a decision was taken | `docs/plan/**` (this directory) | The maintainer | Not published |
| What a feature is, what it is for, how to use it | **`docs/guide/**`** | The user of the engine | **The site** |

### 4.2 The page

```markdown
---
title: Entity queries
slug: ecs/queries
kind: guide                      # guide | tutorial | concept | reference
area: ECS
summary: Iterating the entities that have a given set of components.
api: [T:Vixen.Ecs.QueryDescription, M:Vixen.Ecs.World.Query, T:Vixen.Ecs.Chunk]
tags: [ecs, iteration, performance]
since: 0.1
status: stable                   # stable | preview | deprecated
related: [ecs/systems, ecs/change-filtering]
---

## What it is
## What it is for
## Using it
## Examples
## See also
```

Front matter is schema-checked; the five headings are checked for presence, order and non-emptiness.
The contract is deliberately blunt — it is what turns "every feature must say what it is and what it
is for" from an instruction into a build failure.

### 4.3 Examples that cannot rot

Two forms, both compiled:

````markdown
Include a region of a real file — the sample is in the solution and CI builds it:

{{ snippet Samples/04-EcsStressTest/Program.cs#docs:query }}

Or write it inline, and the build compiles it:

```csharp compile
var moving = new QueryDescription().WithAll<Position, Velocity>();
world.Query(moving, static (ref Position p, ref Velocity v) => p.X += v.X);
```
````

`CheckDocs` extracts every `compile`-marked fence into a generated project that references the engine
and builds it; a fence that needs surrounding context declares `compile:fragment` and is wrapped.
`no-compile` is allowed and requires a reason attribute, which the gate prints in its summary so the
exemptions stay visible. `.rvn` fences go to Raven, `.vxml` fences to the VXML binder.

**This is the single most valuable gate in the document.** Documentation examples rot silently and
are the first thing a new user copies.

### 4.4 The tree

```
docs/guide/
├── index.md                    # "Everything Vixen offers" — the evaluator's page
├── getting-started/            # install, first project, first scene, first build to each platform
├── tutorials/                  # ordered, cumulative, each ending in something that runs
├── concepts/                   # ECS, the frame, assets, the render graph, signals — the vocabulary
├── ecs/  rendering/  ui/  audio/  physics/  networking/  animation/  vfx/  assets/  input/  xr/
├── shaders/                    # Raven the language, plus the library
├── editor/                     # panels, verbs, workflows
├── tools/                      # CLI, SDK, templates, the asset compiler
└── platforms/                  # per-target reality, mapped to 10
```

Sequenced by [P7](#p7--the-coverage-sweep-3040-em-continuous), which is where the real cost is.

---

## Part 5 — The gates

`nuke CheckDocs`, in CI beside `CheckApi` and `CheckFormat`, failing on any of:

| Check | Fails when |
|---|---|
| **Coverage** | A public type has neither a guide page (`api:`) nor an entry in `docs/DocsExempt.txt` with a reason. Same shape as the PublicAPI baselines, same review discipline, same file format |
| **Contract** | A guide page is missing one of the five headings, has an empty one, or has no `summary` |
| **Resolution** | An `api:` ID, a `related:` slug, a `<see cref>`, a snippet region or an internal link does not resolve |
| **Examples** | A `compile` fence does not compile; a `no-compile` fence has no reason |
| **Agreement** | The public type set disagrees with the `PublicAPI.*.txt` baselines ([2.1](#21-why-source-symbols-and-not-the-assembly)) |
| **Budgets** | The search index or the initial JS bundle exceeds [its budget](#part-7--search) |
| **Orphans** | A guide page nothing links to and nothing lists |

⚠ **Coverage is turned on per area, not all at once.** Switched on for 2 372 types on day one it
would block every merge for a quarter and be disabled within a week. `DocsExempt.txt` starts holding
every existing type with the reason `sweep-pending`, and [P7](#p7--the-coverage-sweep-3040-em-continuous)
empties it area by area. **The gate is live from day one for anything *new*** — which is the half
that actually prevents the backlog from growing.

---

## Part 6 — Versioning and the release diff

### 6.1 The store

Documentation is versioned from the first release, because retrofitting versions to a site whose URLs
assumed one is a migration nobody schedules.

```
docs/api-history/
├── 0.1.0/graph.json.br        # committed, compressed — the shape of the API at that tag
├── 0.2.0/graph.json.br
└── index.json                 # versions, dates, tags, retention state
```

Committed rather than rebuilt from tags: rebuilding 0.1 in two years means restoring an old SDK, old
native dependencies and an old MSBuild, and the first release where that fails is the release where
the changelog quietly stops being generated. A compressed graph is a few megabytes; it is the cheapest
insurance in this document. If it exceeds ~10 MB per release the store moves to GitHub release assets
fetched at site build — decided by measurement in [P6](#p6--versioning-and-the-release-diff-10-em), not now.

### 6.2 The diff is generated at the release, and it is the same moment as the API fold

`Vixen.ApiCheck` already has a release ritual: `PublicAPI.Unshipped.txt` folds into
`PublicAPI.Shipped.txt` ([its README](../../Tools/Vixen.ApiCheck/README.md)). **That fold is exactly
the set of changes the release note has to describe**, so the same build step emits both, and the two
cannot disagree because they are computed once:

| Class | Rule (on graph nodes) |
|---|---|
| **Added** | An ID present now and absent before |
| **Removed** | ⚠ Breaking. An ID present before and absent now |
| **Deprecated** | `[Obsolete]` gained, without removal. Carries the message and the replacement `cref` |
| **Breaking — signature** | Same ID, changed return type, parameter type, arity or generic constraint |
| **Breaking — shape** | `sealed` or `abstract` gained; base type changed; an interface dropped; struct → `ref struct`; enum underlying type narrowed. *The ApiCheck line format already carries all five, which is why it records base types and interfaces at all* |
| **Breaking — semantic** | Hand-written, from a `breaking:` front-matter block on a guide page. The only entry a human writes, because "this now defaults to linear space" is not in any signature |
| **Engine-specific** | A component's size or field order changed (scene compatibility); a system's phase or ordering changed (frame behaviour); a shader's descriptor set layout changed (recompile required); a diagnostic's severity changed |

Rendered as a table on `/docs/releases/<version>`, and as the `CHANGELOG.md` section for the tag. The
last row is the one no generic tool would produce and the one an engine user most needs.

### 6.3 URLs and retention

**Decided: the site is [vixenengine.org](https://vixenengine.org), on Cloudflare's free plan.** Both
settle numbers the rest of this section had left open.

- `https://vixenengine.org/docs/...` — the latest release. Stable, canonical, indexed.
- `/docs/0.1/...` — a pinned version. `rel=canonical` to latest, `noindex`.
- `/next/...` — built from `main`, banner-marked, `noindex`.

⚠ **The free plan caps a deployment at
[20 000 files, 25 MiB each](https://developers.cloudflare.com/workers/platform/limits/)** (paid is
100 000), and that cap is now the binding constraint on how many versions the site publishes. Against
the graph as it stands — **3 588 documented types in 364 namespaces** — one version prerenders to
roughly:

| | Files |
|---|---|
| Type pages | 3 588 |
| Namespace pages | 364 |
| Page-data chunks | 266 |
| Guide, tutorials, taxonomy indexes, releases | ~200 |
| App assets, search index shards, sitemap, 404 | ~100 |
| **One version** | **≈ 4 500** |

**So retention is four: the current release, the two before it, and `next`** — ≈ 18 000 files against
a 20 000 ceiling. That is deliberately close, and it is why the file count is a `CheckDocs` budget
that fails the build rather than a note in this document: the deploy that first exceeds it would
otherwise be the one that discovers it. Anything older is reachable as the archived JSON, rendered
client-side, which costs one file per version rather than 4 500.

Two consequences worth stating now, because they are cheap to design for and expensive to retrofit:
**a major release that grows the API by half costs a version of retention**, and **moving to the paid
plan buys 100 000 files rather than a bigger site** — the site is not near any other limit.

⚠ **Scope the API tree by area, not by packability.** The whole solution carries 4 750 public types,
and the 2 378 outside the baselined assemblies are the editor, the tools and the samples — which is
where `graph-node`, `importer` and `replicated-component` live. Three of the taxonomy's most
distinctive kinds have no packable column at all ([the spike](spikes/docs-graph/RESULT.md#the-taxonomy-measured)),
so a tree filtered on `PublicAPI.*.txt` would document the engine and hide the editor.

---

## Part 7 — Search

[FlexSearch](https://github.com/nextapps-de/flexsearch) 0.8, `Document` index, built at site-build
time and shipped as an exported index — never tokenised in the browser.

**Documents** are one per symbol (types *and* members, members carrying an `anchor`), one per guide
*section* rather than page (so a hit lands on the heading that answers the question), one per shader,
diagnostic, CLI verb and sample.

| Field | Indexed | Weight | Why |
|---|---|---|---|
| `name` | ✔ | highest | `MeshRenderer` typed exactly should win |
| `qualifiedName` | ✔ | high | Split on `.` **and** camel case by a custom `Encoder`, so `meshrend`, `Mesh Renderer` and `Vixen.Rendering.Mesh` all hit |
| `summary` | ✔ | medium | The one-line answer, also the result subtitle |
| `body` | ✔ | low | Guide prose |
| `usedBy`, `readsWrites` | ✔ | low | "which system writes Velocity" is a real query |
| `kind`, `area`, `version`, `stability`, `hasExample` | `tag` | — | The filter chips, not the ranking |
| `url`, `breadcrumb`, `signature` | `store` | — | Rendered in the result row |

**Two tiers, because one index that answers everything is one that loads too slowly to be used.**
A names-only index (ids, names, kinds, urls — a few hundred kilobytes) ships with the app and answers
the first keystroke instantly. The full-text index loads in a Web Worker on the first query.
Budget: **≤ 300 kB Brotli eager, ≤ 2 MB lazy, per version**, enforced by `CheckDocs`. Measured
against it: the entire type index — 4 750 types with summaries, attributes and source spans — is
**2.00 MB raw and 0.18 MB Brotli** ([the spike](spikes/docs-graph/RESULT.md#b-the-graph)), so the
eager tier has room for the search structures on top of the data.

The UI is `@xui/omnibar` — a ⌘K palette that already exists — with the deltas specified in
[Part 9](#part-9--what-xui-needs). Results grouped by kind, filter chips bound to the tag fields,
match ranges highlighted, arrow-key navigation, and the last-visited pages shown on an empty query.

---

## Part 8 — The site

### 8.1 Stack, and why it is the reference site's stack

`www/`, a new top level excluded from the .NET solution and from every MSBuild glob, with its own
pnpm workspace. Angular 22 (standalone, signals, zoneless, `OnPush`), Tailwind 4, `@xui/*`, deployed
by Wrangler.

**Copy [xuijs.org](https://xuijs.org)'s deployment shape exactly**, because it has already solved the
two problems this site would otherwise discover the hard way — from
[`apps/app/wrangler.jsonc`](https://github.com/Rikarin/xui/blob/main/apps/app/wrangler.jsonc):

- `outputMode: "static"` with `RenderMode.Prerender` on every route: no Worker, no cold start, no SSR
  runtime, and complete HTML for readers and crawlers.
- **No `main` in `wrangler.jsonc`** — the deployment is assets alone, which takes the site out from
  under the Worker size limit that `@angular/ssr` fills by inlining every prerendered page into the
  bundle.
- `not_found_handling: "404-page"`, with the prerendered 404 copied into place by a `bundle` step.

Two deltas from that site, both forced by scale: content is **compiled markdown loaded per route**
rather than hand-written Angular pages, and there are ~2 550 API pages rather than 111.

✅ **Data delivery is settled, and it is per namespace.** xUI generates one committed module per
component and code-splits with `import()`; at this scale that would be 4 750 chunks with a **median of
428 bytes** — the chunking costing more than the content. Grouped per namespace it is **364 chunks,
median 2 959 B, p95 23 412 B** ([the spike](spikes/docs-graph/RESULT.md#c-chunking)). So: JSON modules
under `src/generated/`, dynamically imported per route, one chunk per namespace, resolved by a route
resolver so the prerendered HTML is complete.

⚠ **With one refinement the measurement forces**: the largest namespace chunk is already **92 kB in
the index tier alone**, and the page tier multiplies it by the member ratio. The emitter groups per
namespace and **splits any group past a byte budget**, and that budget is a `CheckDocs` check beside
the search-index ones.

**Use the xUI skill and the `@xui/mcp` server while building this.** Ninety packages exist; the site
should contain almost no bespoke components, and `xui_components_search` / `xui_components_get` are
how to avoid rebuilding one that ships. Never guess a selector.

### 8.2 Routes

| Route | Page |
|---|---|
| `/` | Landing: what Vixen is, in one screen |
| `/docs` | The overview — every area, what it offers, where to start |
| `/docs/getting-started/*`, `/docs/tutorials/*`, `/docs/guide/*` | The written half |
| `/docs/api` | API root: areas → assemblies → namespaces |
| `/docs/api/:namespace`, `/docs/api/:namespace/:type` | Generated pages |
| **`/docs/components`**, **`/systems`**, **`/controls`**, **`/shaders`**, **`/attributes`**, **`/diagnostics`**, **`/cli`** | **The taxonomy indexes** — filterable, sortable tables, one per kind |
| `/docs/samples`, `/docs/releases`, `/docs/releases/:version` | Sample gallery; the version diff tables |
| `/0.1/**`, `/next/**` | The same tree, pinned |

The taxonomy indexes are the answer to *"see what the engine can offer"*, and they cost nothing —
each is one filter over `graph.json`.

### 8.3 The symbol page

Kind badge and stability chip · breadcrumb · **source-link icon** · classified signature · *What it
is* (from the guide, if any) · declaration facts (assembly, namespace, since, platforms) · members,
grouped and collapsible · the kind-specific panel from [2.3](#23-the-taxonomy--the-opinionated-half)
(a component's systems, a system's schedule, a shader's bindings) · examples · used-by, with samples
first · see-also. Sticky in-page TOC on the right, nav tree on the left, both from the graph.

### 8.4 Breadcrumbs

Derived by walking `declares` upward — root → area → assembly → namespace → type → member — and the
guide's folder tree for written pages. A page with two parents (a symbol that a guide also documents)
shows the guide path and offers the API path, never both stacked. Emitted as `BreadcrumbList` JSON-LD
as well as markup, since these pages exist to be found by search engines.

### 8.5 Non-negotiables for the site itself

Dark and light from xUI's semantic tokens, never a raw colour. Keyboard-complete: ⌘K, `/`, `[`/`]`
between pages, arrow keys in results. Readable with JavaScript off — the prerendered HTML carries the
prose, the tables and the highlighted code. `prefers-reduced-motion` respected. Budget: **≤ 250 kB
initial JS** (xUI's own budget is 700 kB and it is a component gallery; a documentation site that
needs more than a third of that has done something wrong). A copy button on every fence, an anchor on
every heading, a per-page "edit on GitHub" link.

---

## Part 9 — What xUI needs

xUI is in-house, so the honest answer to a missing component is a specification rather than a
workaround. Everything below is **shared with xuijs.org**, which already carries four of these as
app-local files that would be deleted when the package lands
([`apps/app/src/app/shared/`](https://github.com/Rikarin/xui/blob/main/apps/app/src/app/shared)).

| # | Package / change | Why | Sketch | Blocks |
|---|---|---|---|---|
| **X1** | **`@xui/code-block`** — promote `apps/app/src/app/shared/code-block.ts` | Both sites render code; Vixen needs it to accept **pre-tokenised spans** so highlighting can come from Roslyn and Raven rather than a browser grammar ([3.4](#34-highlighting-comes-from-the-engines-own-lexers)) | `code`, **`tokens: Token[][]`**, `language`, `filename`, `highlightLines`, `showLineNumbers`, `wrap`, `tabs: {label, code}[]`; copy action slot; `(copied)` output | [P3](#p3--site-mvp-15-em) |
| **X2** | **`@xui/prose`** — typography for compiled markdown | `@tailwindcss/typography` knows nothing about xUI's semantic tokens; both sites need headings, lists, tables, callouts and code to read as one system in both themes | `<article xuiProse size="sm\|md" [density]>`; styles the raw HTML of a markdown render; no per-element classes needed | [P3](#p3--site-mvp-15-em) |
| **X3** | **`@xui/toc`** — promote `table-of-contents.ts`, add scroll-spy | Vixen's symbol pages are long enough that a static outline is not enough | `entries`, `activeId` (model, IntersectionObserver-driven), `minLevel`/`maxLevel`, `label`; `nav` + `aria-current` | [P3](#p3--site-mvp-15-em) |
| **X4** | **`@xui/omnibar` deltas** | It is a ⌘K palette over a synchronous item list; docs search is async, grouped, filtered and large | **(a)** async provider input returning a promise/observable, with a loading state; **(b)** grouped results with sticky group headers; **(c)** an empty/no-results template slot; **(d)** filter chips bound to a `tags` model; **(e)** match-range highlighting (`ranges: [start, end][]` per item, rendered as `<mark>`); **(f)** virtualisation past ~200 rows; **(g)** recent items on an empty query; **(h)** `⌘K`/`/` wiring via `@xui/core/hotkeys` | [P4](#p4--search-05-em) |
| **X5** | **`@xui/breadcrumb` delta** | Six-level API breadcrumbs overflow on mobile | Collapse the middle via `@xui/overflow-list` into a `@xui/menu`; `maxItems`, `itemsBeforeCollapse`, `itemsAfterCollapse` | [P3](#p3--site-mvp-15-em) |
| **X6** | **Router-aware nav tree** | `@xui/tree` and `@xui/navigation-menu` exist; neither syncs its active node and expansion to the router | A directive over `@xui/tree`: active node from `RouterLink` matching, expansion persisted, `aria-current="page"` | [P3](#p3--site-mvp-15-em) |
| **X7** | **`heading-anchor`, `clipboard`** | Trivial, duplicated in both sites | Fold into X1/X2 rather than making packages of them | — |

None is architectural, none blocks [P1](#p1--the-graph-15-em) or [P2](#p2--the-content-pipeline-10-em),
and X1–X3 and X5–X6 are the ones a docs site cannot do without. Budget: **0.5 EM inside xUI**, which
xuijs.org gets back immediately.

---

## Part 10 — The agent surface

The graph is one artefact with three consumers, and the third is nearly free: `@xui/mcp` proves the
pattern — an index extracted from sources, so the answers match the version installed rather than
whatever a docs scraper last saw.

**`vixen-mcp`** (Node, published from `www/`, reading the same `graph.json`):
`vixen_meta` (version, provenance, counts) · `vixen_search` (the same FlexSearch index, filterable by
kind) · `vixen_symbol_get` (signature, doc comment, facets, kind panel, source link) ·
`vixen_guide_get` · `vixen_examples` · `vixen_diff` (what changed between two versions).

And a **`vixen` skill** mirroring [xUI's](https://github.com/Rikarin/xui/blob/main/skills/xui/SKILL.md):
the engine's principles, how to declare a component and a system, the taxonomy, and *never guess a
type name — search the index*. An engine with 4 750 public types is exactly the case where an agent
guessing is the failure mode.

Last phase, cut first if the schedule slips — but designed for now, because it costs one JSON contract
and nothing else.

---

## Part 11 — Phases

| Phase | Cost | Exit criterion |
|---|---|---|
| [P0 — The spike](#p0--the-spike-03-em) | 0.3 EM | ✅ **Done** — [RESULT.md](spikes/docs-graph/RESULT.md). Three measurements, three decisions, five findings |
| [P1 — The graph](#p1--the-graph-15-em) | 1.5 EM | 🟡 **Half built** — `Tools/Vixen.DocGen` emits the graph and agrees with `CheckApi`; the `used-by` pass, classified signatures and the non-C# nodes are owed |
| [P2 — The content pipeline](#p2--the-content-pipeline-10-em) | 1.0 EM | Markdown → JSON with front matter, snippets, classified fences |
| [P3 — Site MVP](#p3--site-mvp-15-em) | 1.5 EM | Prerendered on Cloudflare: guide, API, taxonomy indexes, nav, breadcrumbs, TOC |
| [P4 — Search](#p4--search-05-em) | 0.5 EM | ⌘K over everything, inside budget |
| [P5 — Gates and CI](#p5--gates-and-ci-05-em) | 0.5 EM | `CheckDocs` red on a violation; `docs.yml` deploying; PR previews |
| [P6 — Versioning and the release diff](#p6--versioning-and-the-release-diff-10-em) | 1.0 EM | Two versions live; a release emits its diff table |
| [P7 — The coverage sweep](#p7--the-coverage-sweep-3040-em-continuous) | 3.0–4.0 EM | `DocsExempt.txt` empty |
| [P8 — The agent surface](#p8--the-agent-surface-05-em) | 0.5 EM | `vixen-mcp` published; skill in the repo |
| **Total** | **9.8–10.8 EM** + 0.5 EM in xUI | |

#### P0 — The spike (0.3 EM)

✅ **Done.** Three questions, answered with numbers before anything was designed around a guess, written up
in [spikes/docs-graph/RESULT.md](spikes/docs-graph/RESULT.md) the way
[the WebGL2 spike](spikes/web-webgl2/RESULT.md) was:

**(a)** `MSBuildWorkspace` opens `Vixen.slnx` on Roslyn 5.6 — 34 s to open, 45 s to compile and walk
243 projects. The `.slnx`-parsing fallback is not needed. **(b)** The index tier is 2.00 MB raw and
0.18 MB Brotli for 4 750 types and 57 077 members. **(c)** Per namespace: 364 chunks at a 2 959 B
median, against 4 750 chunks at a 428 B median.

And one finding worth more than the three answers: **the workspace's design-time build defaults to
Debug, and against a Release tree that silently removed 298 types and four kinds from the graph**
([3.2](#32-generated-symbols-are-part-of-the-surface-and-the-configuration-decides-whether-they-exist)).

#### P1 — The graph (1.5 EM)

`Vixen.DocGen` end to end for C#: workspace load, source-generated documents, symbol walk, doc
comments with `<inheritdoc/>`, the [taxonomy](#23-the-taxonomy--the-opinionated-half) with a fixture
test per rule, the reference pass, source links, and the `CheckApi` agreement test. Then the
non-C# nodes of [2.8](#28-what-else-becomes-a-node): Raven reflection, the two registers, the CLI
trees. Ends with `nuke Docs` producing an artefact and no site to read it.

**Built** ([`Tools/Vixen.DocGen`](../../Tools/Vixen.DocGen/README.md), 108 tests): the workspace load
with its configuration invariant, the thirteen taxonomy rules with a fixture each, doc comments with
`<inheritdoc/>` resolved by walking the base chain — *Roslyn does not expand it*, and every type that
inherits its prose would otherwise render blank — source links, the two-tier emitter with its
per-namespace chunking and byte budget, the `CheckApi` agreement check, the `nuke Docs` target, and
**the kind-specific facets** — a component's size and rows per chunk, a system's phase and ordering,
a replicated component's channel and per-field bit cost, an importer's extensions, a node's menu
path, an annotation's targets. A size is null rather than guessed when the layout is the runtime's
business, because somebody reads that number to decide whether to split a component.

⚠ **The facets immediately said something about the engine rather than about the graph: none of the
36 systems declare `[Reads]` or `[Writes]`.** The attributes exist, the scheduler is built to use
them, and no system carries one — the same gap [`overview.md`](../overview.md) records against
`VIXEN_JOB_SAFETY`. A page that says a system reads nothing is the documentation making a missing
declaration visible, which is what the coverage argument in
[the row this overturns](#the-row-this-overturns) claimed it would do.

**The `used-by` edge is built and it is cheap**: one pass over every syntax tree rather than a
per-symbol search, **18.5 s for the solution**, and 3 494 of 3 588 types come out with a user.
[2.4](#24-edges) said the site of a reference matters more than the count, and the data agrees —
`Vixen.Ecs.World` has 209 users, of which the six a reader sees first are all samples, and 312 types
have at least one sample use. `Vector3`, at 788, is what a name query should rank above everything
else.

**Signatures arrive classified**, so the site ships no highlighter and the prerendered HTML is
coloured with JavaScript off. One correction to [3.4](#34-highlighting-comes-from-the-engines-own-lexers)
that the code forced: a signature is *synthesised* from symbols rather than quoted from source, so
there is no span for the classifier to run over — `ToDisplayParts` hands the classification out with
the text, and the classifier is the right answer only for the quoted code P2 compiles. Cost, measured:
the page tier went 21.6 → 30.0 MB when signatures became runs, and back to **26.0 MB** once a run is
written as `["public","keyword"]` rather than as an object with two property names.

Over the tree today: **120 of 243 projects documented, 3 588 types, 29 354 members, 3 504 of them
with prose, in 57 s** — a 1.86 MB index and 18 MB of pages in 258 chunks, and the graph agrees with
every `PublicAPI.*.txt` baseline.

**Owed**, and each is a section of this document that has no code yet:

| Owed | Where |
|---|---|
| Raven shaders, the diagnostic and log-event registers, the CLI trees | [2.8](#28-what-else-becomes-a-node) |
| Member-level baseline agreement | [2.1](#21-why-source-symbols-and-not-the-assembly) |

Three things the build found that the plan had not: **a documentation id is unique inside an assembly
and not across a solution** (this repository links `Vixen.Core.Syntax` into a generator assembly as
source, so 96 declarations claimed a page twice — the packable copy keeps it); **samples and
benchmarks are demonstrations rather than surface** (and seven of them declare a type called
`Program`); and **C# 14 extension blocks** appear in the baselines under two notations and in the
graph under a third.

#### P2 — The content pipeline (1.0 EM)

Markdig, front matter, the five-heading contract, snippet regions, `compile` fences and their
generated project, classified fences via [3.4](#34-highlighting-comes-from-the-engines-own-lexers),
link resolution, TOC extraction, search-document emission. `docs/guide/` seeded with getting-started
and one page per area — enough to prove every mechanism, not the sweep.

#### P3 — Site MVP (1.5 EM)

The Angular app: layout, nav, breadcrumbs, TOC, theme, guide page, symbol page, taxonomy indexes, 404,
sitemap. Prerendered, deployed to Cloudflare, inside the JS budget. X1–X3, X5, X6 land in xUI here.

#### P4 — Search (0.5 EM)

Index build, two tiers, the Worker, the omnibar with X4's deltas, filter chips, budgets in `CheckDocs`.

#### P5 — Gates and CI (0.5 EM)

`CheckDocs` with every check in [Part 5](#part-5--the-gates); `DocsExempt.txt` seeded; `docs.yml`
building and deploying on `main` and on tags; a preview deployment per PR touching `docs/` or `www/`.

#### P6 — Versioning and the release diff (1.0 EM)

The version store, the URL scheme, the switcher, retention with the file-count budget, and the diff
generator wired into the `PublicAPI` fold so a release emits its own table.

#### P7 — The coverage sweep (3.0–4.0 EM, continuous)

Writing. Area by area, each ending with that area's exemptions deleted and its gate live:
`concepts` → `ecs` → `rendering` → `assets` → `ui` → `shaders` → `physics`/`audio`/`animation` →
`networking` → `editor` → `tools`/`platforms`, plus the tutorial track. **This is the largest single
number in the document and the one most likely to be underestimated**; it is also the only phase that
delivers value on the day each page lands rather than at the end.

#### P8 — The agent surface (0.5 EM)

[Part 10](#part-10--the-agent-surface).

---

## Risks

| Risk | Mitigation |
|---|---|
| ~~**`MSBuildWorkspace` is slow or flaky over 200 projects**~~ | ✅ Closed by [P0](#p0--the-spike-03-em): 34 s open, 45 s compile, 243/243 projects. The fallback was not needed. The graph is still built once per CI run and cached as an artefact |
| ~~**Roslyn version conflict with CPM**~~ | ✅ Closed by [P0](#p0--the-spike-03-em): `ManagePackageVersionsCentrally=false` in the tool's own `.csproj` builds against Roslyn 5.6.0 while every generator stays on 4.11.0 |
| ⚠️ **The generator misses source-generated API** | **Not theoretical — it happened.** On default settings the graph lost 298 types and four kinds and looked complete ([3.2](#32-generated-symbols-are-part-of-the-surface-and-the-configuration-decides-whether-they-exist)). Three nets: an explicit `Configuration`, a build that fails on any project with compile errors, and the `CheckApi` agreement test |
| **The coverage gate blocks every merge** | Exemptions with reasons, seeded full, emptied per area. Live for new API from day one — the backlog is allowed to shrink slowly and forbidden to grow |
| **Nobody writes the prose, so the site is 2 372 signature dumps** | The five-heading contract, and P7 sequenced by area with a gate that turns on behind it. A symbol page with no guide renders its doc comment and is *marked* undocumented — visibly, on the page |
| **Examples rot** | Every fence compiles in CI ([4.3](#43-examples-that-cannot-rot)) |
| **Prerendering 2 700 pages × 4 versions overruns the file limit or the build** | Measured, budgeted and gated ([6.3](#63-urls-and-retention)); retention is three versions plus `next`; older versions render client-side from archived JSON |
| **The search index outgrows the budget** | Two tiers, per-version shards, `CheckDocs` budget. Measured headroom: the whole type index is 0.18 MB Brotli against a 300 kB eager budget |
| **The site drifts from `overview.md` and the READMEs** | They have different audiences and the site copies neither ([4.1](#41-where-it-lives)). READMEs are linked, `overview.md` is not published |
| **xUI churn (Angular 22, ~90 packages at 0.x) breaks the build** | Pinned versions, one deliberate upgrade pass per engine release, and the library is in-house — a break is a fix, not a ticket |
| **Two toolchains (.NET and pnpm) in one repository** | The boundary is one directory and one JSON contract: `Vixen.DocGen` emits, `www/` consumes. Neither builds the other; CI runs them as separate jobs |
| **Doc IDs churn when types move** | They are namespace-qualified, so a move *is* a rename and shows up in the release diff as removed + added. Redirects come from the diff, generated with it |

---

## Open questions

1. ~~**Domain.**~~ ✅ **`vixenengine.org`.**
2. ~~**Cloudflare plan.**~~ ✅ **Free** — 20 000 files per deployment, which fixes retention at four
   versions ([6.3](#63-urls-and-retention)) and makes the file count a build gate.
3. **Publish `next` from `main`?** Recommended yes, `noindex`, banner-marked — it is how contributors
   check their own pages, and how the docs get read before a release.
4. ~~**Retention.**~~ ✅ Settled by the free plan's cap: current + two + `next`. More is not available
   rather than merely expensive.
5. **Analytics.** Recommendation: none, or Cloudflare's own request analytics. A documentation site
   does not need a third-party script, and the CSP is simpler without one.
6. **`docs/manual/`.** Its three files are the diagnostic register, the log-event register and the
   build-a-game walkthrough. The registers stay where they are and become graph nodes
   ([2.8](#28-what-else-becomes-a-node)); the walkthrough moves into `docs/guide/getting-started/`.

---

## Proposed ADRs

To be added to [01](01-technology-decisions.md) — 016 is the next free number.

| ADR | Decision |
|---|---|
| **ADR-016** | **Documentation is generated from Roslyn source symbols, not from DocFX and not from XML sidecars.** The engine's vocabularies — components, systems, shaders, annotations — are the reference, and only source symbols carry them ([the row this overturns](#the-row-this-overturns)) |
| **ADR-017** | **The site is a fully prerendered Angular application served as Cloudflare static assets.** No Worker, no SSR at runtime, no cold start; readable without JavaScript ([8.1](#81-stack-and-why-it-is-the-reference-sites-stack)) |
| **ADR-018** | **Documentation coverage and examples are build gates.** `CheckDocs` sits beside `CheckApi`: a new public type without a page fails, and every example compiles ([Part 5](#part-5--the-gates)) |
| **ADR-019** | **Syntax highlighting is produced by the engine's own lexers at build time**, never by a JavaScript grammar in the browser ([3.4](#34-highlighting-comes-from-the-engines-own-lexers)) |
| **ADR-020** | **Markdown is Markdig; the languages Vixen defines keep their hand-written parsers.** ADR-009's rule is about languages this project *owns* — Markdown is not one, has no engine semantics, and a fourth hand-written front end would buy nothing |

New package entries for [Directory.Packages.props](../../Directory.Packages.props):
`Microsoft.CodeAnalysis.Workspaces.MSBuild` and `Microsoft.Build.Locator` (pinned inside
`Vixen.DocGen` per [3.1](#31-the-tool)), and `Markdig`. New JS dependencies in `www/`: Angular 22,
Tailwind 4, `@xui/*`, `flexsearch`, `wrangler`.

---

## Documents this changes

| Document | Change |
|---|---|
| [02 § Top level](02-repository-layout.md#top-level) | `docs/manual/ (DocFX)` becomes `docs/guide/` — reader-facing markdown, no DocFX. Two new entries in the tree: `Tools/Vixen.DocGen/` (+ tests) and `www/`, the latter excluded from the solution and from every MSBuild glob |
| [12 § Nuke](12-build-ci-and-testing.md) | The `Docs` target is no longer DocFX and no longer publishes to GitHub Pages; `CheckDocs` joins it. `docs.yml` builds, gates and deploys to Cloudflare on `main` and on tags, with a preview per PR |
| [14 § Phase 11](14-roadmap.md) | The documentation line's "DocFX API reference" becomes this document, and its cost is stated here rather than folded into a polish phase |
| [01](01-technology-decisions.md) | Five ADRs (016–020) and the package entries above |
| [13](13-diagnostics.md) | The diagnostic-code and log-event registers stay the source of truth and gain a second consumer; neither moves |
| [14](14-roadmap.md) | A documentation phase, ~10 EM, of which the writing is a third and is continuous |
| [00](00-vision-and-principles.md) § Non-negotiables | Gains one: **a public type without a documentation page is a build failure**, in the same sentence as `internal` by default |
| [`Tools/Vixen.ApiCheck`](../../Tools/Vixen.ApiCheck/README.md) | Gains a peer that must agree with it, and a release ritual that now emits the changelog from the same fold |

Licensed under Apache-2.0.
