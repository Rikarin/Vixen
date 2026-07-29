# Vixen — Implementation Plan

Vixen is a .NET 10 / C# 14 game engine **and** application framework: the same stack that ships a
shipped game also ships Photoshop- or Blender-class desktop tooling. The editor is written in the
engine, using the engine's own UI framework, and is the primary proof that the framework is
general-purpose.

This directory is the authoritative design record. Read it in order the first time; after that,
treat each file as the spec for its subsystem.

**For *state* rather than design, read [`../overview.md`](../overview.md) first.** These documents say
what Vixen is meant to be and record why each decision was taken; the overview says which of it
exists. It carries every feature and every library with a status, a dependency tree over what is left
so independent tracks can be scheduled in parallel, and one table of what is owed. Where it and a
document here disagree, the overview is checked against the code and says so — which is how a design
record that is kept for its reasoning stays useful without also having to be a status board.

| # | Document | Scope |
|---|---|---|
| 00 | [Vision and Principles](00-vision-and-principles.md) | Non-negotiables, quality bars, what Vixen is *not* |
| 01 | [Technology Decisions](01-technology-decisions.md) | Every dependency, pinned version, and the ADR register |
| 02 | [Repository Layout](02-repository-layout.md) | Folder tree, project graph, naming, monorepo migration |
| 03 | [Core Foundation](03-core-foundation.md) | Math, memory, collections, jobs, VFS, serialization, reflection |
| 04 | [ECS and Scripting](04-ecs-and-scripting.md) | Archetype ECS, `Behavior` layer, system scheduler, transforms |
| 05 | [Graphics RHI](05-graphics-rhi.md) | Backend-agnostic device layer over Silk.NET; Vulkan/D3D12/GL/WebGPU |
| 06 | [Rendering Pipeline](06-rendering-pipeline.md) | Render features/stages, forward+, deferred, PBR, shadows, post FX |
| 07 | [Raven Shader Pipeline](07-raven-shader-pipeline.md) | Raven → SPIR-V → all targets, effects, permutations, generated bindings |
| 08 | [Asset Pipeline and Addressables](08-asset-pipeline-and-addressables.md) | `.meta` sidecars, importers, ODB, bundles, remote content |
| 09 | [UI Framework](09-ui-framework.md) | VXML markup, signals, flexbox, VCSS + utility preprocessor, hot reload |
| 10 | [Platforms](10-platforms.md) | Windows, Linux, macOS, Android, iOS, Web — per-target reality check |
| 11 | [Editor](11-editor.md) | Editor architecture, docking, inspectors, node graphs (shader + VFX) |
| 12 | [Build, CI and Testing](12-build-ci-and-testing.md) | Nuke, GitHub Actions, NuGet layout, test strategy and gates |
| 13 | [Diagnostics](13-diagnostics.md) | Logging, profiling, tracing, debug rendering, remote inspector |
| 14 | [Roadmap](14-roadmap.md) | Phases, exit criteria, sequencing, effort |
| 15 | [Risks and Open Questions](15-risks-and-open-questions.md) | Ranked risks, mitigations, decisions that need your input |
| 16 | [Networking](16-networking.md) | Transports, tick, replication, interest management, lag compensation, security |
| 17 | [App Heads and Shipping](17-app-heads-and-shipping.md) | What a shipped game *is*, build variants, dedicated server, play-mode topology, trimming policy |
| 18 | [Raven Parser Migration](18-raven-parser-migration.md) | ⚠️ Amends ADR-009 — why ANTLR should give way to a hand-written parser, and the plan to swap it safely |
| — | [spikes/web-webgl2](spikes/web-webgl2/RESULT.md) | ✅ Executed spike: Silk.NET.OpenGLES on `browser-wasm`, with working code and measurements |
| — | [Implementation Overview](../overview.md) | **Not a design document.** What is built, what is not, what blocks what, and what is owed — reconciled against the code |

## Read this first

Four things in the brief need correcting or narrowing before work starts. They are detailed in
[15-risks-and-open-questions.md](15-risks-and-open-questions.md), summarised here:

1. **There is no Silk.NET Metal binding** — ✅ *MoltenVK approach confirmed.* Verified against the live
   package index: Silk.NET ships Vulkan, D3D11/12, OpenGL, OpenGL ES, EGL, WebGPU, SPIRV, Shaderc, but
   nothing for Metal. Metal on macOS/iOS is delivered via **MoltenVK** — verified at **v1.4.2**
   (released 2026-07-24), a layered implementation of **Vulkan 1.4** covering macOS/iOS/tvOS/Catalyst
   and the Simulators, which internally converts SPIR-V to MSL with SPIRV-Cross. A native Metal
   backend, if ever wanted, is a post-1.0 hand-written binding — not a Silk.NET package away.
2. **"No Mono" means no Cecil** — ✅ *confirmed, settled.* The constraint is *no IL weaving, no
   Mono.Cecil post-processing, no embedded Mono scripting host; Roslyn source generators for all
   metaprogramming* (ADR-002), enforced by a build gate. The Mono-based WASM runtime is acceptable, so
   **Web stays in scope**. The engine constrains its compile-time toolchain; the runtime host the .NET
   SDK picks for `browser-wasm` is not the engine's choice.
3. **Yoga has no CSS Grid.** The Flexbox C# port is a flexbox-only algorithm (and is .NET Framework
   4.6, class-per-node, allocation-heavy). It is used as an *algorithm reference*, not a dependency.
   Grid is a separate layout algorithm implemented after flexbox lands.
4. **Scale.** Honest sizing is in [14-roadmap.md](14-roadmap.md): **~53 engineer-months** of work
   including Raven's remainder. Since this is being built **solo with AI assistance** (Q8), that document
   now carries a *Delivering this solo* section: four publishable milestones, a recommended swap of
   Phases 4 and 5 so a working engine ships before the largest phase, and a cut list ordered in advance.
   Plan against milestones, not a completion date.

**Already verified by executed spike** — [spikes/web-webgl2](spikes/web-webgl2/RESULT.md): the Web
target's core unknown (risk R1) is retired. `Silk.NET.OpenGLES` renders a WebGL2 triangle from
`browser-wasm` on .NET 10 via a ~40-line Emscripten bridge, with a trimmed payload of **0.93 MB
Brotli** — an order of magnitude better than this plan's first estimate. The write-up includes the
working project, the required emcc flags, and a silent-WebGL1-downgrade trap worth knowing about.

## Current state of the repo

```
/Users/jiu/Projects/Vixen
├── .DS_Store
├── Raven/                  ← existing, own git repo, net10.0, RootNamespace = Vixen.Raven
│   ├── Compiler/           ← ANTLR grammar + Roslyn-style green/red syntax trees; parse only
│   ├── Cli/                ← `raven compile --target glsl`
│   ├── Syntax/Syntax.xml      ← node model; the generator lives in Core/
│   ├── Tests/              ← xunit; golden syntax, round-trip, red/green tree tests
│   └── Feed/               ← .rvn samples
└── docs/plan/              ← this directory
```

Raven's parser front end is real and well-structured (green/red trees, full trivia, golden tests,
`Vixen.Raven` root namespace already chosen). Semantic analysis and code generation are the
outstanding work, and per your brief they complete before engine work starts. Phase 0 of the roadmap
absorbs Raven into the monorepo with history intact and adds the compiler contract the engine
depends on.
