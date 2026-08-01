# Vixen — Implementation Plan

Vixen is a .NET 10 / C# 14 game engine **and** application framework: the same stack that ships a
shipped game also ships Photoshop- or Blender-class desktop tooling. The editor is written in the
engine, using the engine's own UI framework, and is the primary proof that the framework is
general-purpose.

This directory is the authoritative design record: what Vixen is meant to be, and why each decision
was taken. Read 00–02 first; after that, treat each file as the spec for its subsystem.

**These documents do not say what is built.** [`../overview.md`](../overview.md) does, and it is
checked against the code — so where it and a document here disagree, it says so and it wins. Keeping
the two apart is what lets a design record stay useful for its reasoning without also having to be a
status board.

Documents 18 and above amend or extend an earlier one rather than opening new ground; each says which,
and the document it amends points back. **A ⚠️ means the amendment changes a decision, so read the pair
together; a ✅ means it has since been carried out.**

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
| 18 | [Raven Parser Migration](18-raven-parser-migration.md) | ✅ Amends ADR-009 — why ANTLR gave way to a hand-written parser, and why the `.g4` files were kept as a differential oracle |
| 19 | [Lighting and Global Illumination](19-lighting-and-global-illumination.md) | ⚠️ Amends 06 — retires baked lightmaps and tetrahedral probes for a Lumen-shaped dynamic path: distance fields, an irradiance field, screen probes, surface cache |
| 20 | [Editor Parity](20-editor-parity.md) | ⚠️ Extends 11 — the editor's *surface*: every panel, window, menu line and verb an Unreal or Unity user reaches for, with the milestones to build them |
| 21 | [Realtime Collaboration](21-realtime-collaboration.md) | ⚠️ Extends 11 and 20 — what Unreal's Multi-User Editing is, why intent replicates better than diffs, and what the five milestones cost. Post-1.0 except the first |
| 22 | [Virtualized Geometry](22-virtualized-geometry.md) | ⚠️ Extends 06 — a Nanite-class pipeline: a cluster DAG built offline, streamed pages, hierarchical culling on the device |
| 23 | [Bindless Materials](23-bindless-materials.md) | ⚠️ Extends 05 and 06 — one descriptor array for the frame, so a draw is an index rather than a set; what compacted draws and GPU-driven submission were waiting for |
| 24 | [Blockout Tools](24-blockout-tools.md) | ⚠️ Extends 11 and 20 — in-viewport grey-boxing: a mesh kernel that survives editing, sub-object selection, the fifteen verbs, snapping, and the handoff to an artist |
| 25 | [Documentation Generator and Site](25-documentation-generator-and-site.md) | ⚠️ Amends 02 and 12 — DocFX gives way to a Roslyn generator that classifies what the engine actually offers (components, systems, shaders, annotations), and a versioned, searchable site built on xUI and served from Cloudflare. Coverage and examples become build gates |
| 26 | [Virtual Cameras](26-virtual-cameras.md) | ✅ Extends 04 — a Cinemachine-shaped camera system: many authored shots, composable body and aim stages, a director that picks one on priority and blends, noise and impulse |
| 27 | [MMO Framework](27-mmo-framework.md) | ⚠️ Extends 16 and 17 — the substrate: an Orleans orchestrator that places, scales and upgrades map instances; realms as headless heads; the three network planes and why real-time traffic never meets a gateway; seamless transfer by overlapping sessions and a lease |
| 28 | [Gameplay Framework](28-gameplay-framework.md) | ⚠️ Extends 27, 08 and 16 — the opinionated library set on top: tags, definitions and the modifier algebra, then items, combat, quests, guilds, economy, matchmaking and the rest, so a new item is a content build rather than a release |
| 29 | [Players and Possession](29-players-and-possession.md) | ⚠️ Extends 04, 26 and 16 — who the player *is*: a controller that outlives its pawn, possession as a runtime edge, and `MoveIntent` as the one seam between input, physics and the wire |

### Not design documents

| Document | What it is |
|---|---|
| [Implementation Overview](../overview.md) | **The state, not the design.** Every feature and library with a status, a dependency tree over what is left so independent tracks can be scheduled in parallel, and one table of what is owed. Reconciled against the code |
| [RHI Backend Mapping](../rhi-backend-mapping.md) | A reference table: every `Vixen.Graphics` concept against Vulkan, D3D12, GL/GLES/WebGL2, WebGPU and Metal. The fourth of ADR-001's five measures, reviewed whenever the RHI surface changes |
| [spikes/web-webgl2](spikes/web-webgl2/RESULT.md) | ✅ Executed spike: `Silk.NET.OpenGLES` drives real WebGL2 from `browser-wasm`, in ~40 lines of Emscripten bridge, trimming to 0.93 MB Brotli. Retired risk R1 and corrected a size estimate that was an order of magnitude wrong |
| [manual/](../manual/) | Reader-facing: building a game and a server, the diagnostic-code register, the log-event register |

## Four corrections to the original brief

All four are settled. They are kept here because each changed what got built, and the reasoning is in
[15-risks-and-open-questions.md](15-risks-and-open-questions.md).

1. **There is no Silk.NET Metal binding.** Metal on macOS and iOS is delivered through **MoltenVK**
   — a layered implementation of Vulkan 1.4 that converts SPIR-V to MSL with SPIRV-Cross internally
   (ADR-011). A native Metal backend would be a hand-written binding, not a package away, and is
   post-1.0 if ever.
2. **"No Mono" means no Cecil.** The constraint is *no IL weaving, no Mono.Cecil post-processing, no
   embedded Mono scripting host; Roslyn source generators for all metaprogramming* (ADR-002),
   enforced by a `CheckArchitecture` gate. The Mono-based WASM runtime is acceptable, so **Web stays
   in scope** — the engine constrains its compile-time toolchain, not the runtime host the SDK picks.
3. **Yoga has no CSS Grid**, and the Flexbox C# port is not usable as a dependency (.NET Framework
   4.6, class-per-node, allocation-heavy). It is an *algorithm reference* (ADR-006). Grid is a
   separate algorithm, still unbuilt.
4. **Scale.** ~48 engineer-months for the twelve original phases, all of which are now built or
   part-built — [14-roadmap.md](14-roadmap.md) carries the per-phase state, the four publishable
   milestones (M1 and M2 are passed), and a cut list ordered in advance. The amendments in 19–24 carry
   their own budgets. Plan against milestones, not a completion date.
