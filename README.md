<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen

A .NET 10 / C# 14 game engine **and** application framework: the same stack that ships a game also
ships Photoshop- or Blender-class desktop tooling. The editor is written in the engine, in the
engine's own UI framework, and is the primary proof that the framework is general-purpose.

```bash
./build.sh Compile      # or build.cmd on Windows
./build.sh Test
```

The SDK version is pinned in [`global.json`](global.json); nothing else is required to build the
solution. `./build.sh` is the entry point CI and developers share. There are thirty-eight targets:
`Clean Restore RestoreNativeDeps Compile CompileRelease Test Coverage Pack CheckPackages CheckTemplates
GoldenImages Benchmark CheckBenchmarks CheckArchitecture CheckApi CheckFormat CheckWhitespace
CheckAttribution CheckStrings CheckShaders CheckDocs CheckDocsCoverage Docs CheckAot CheckAotIos
CompileMobile CompileWeb PublishWeb BrowserSmoke PublishEditor Release ContentBytes RemeshBytes
SampleFrame AffectedProjects AffectedTests TestOrder PruneWorktrees` — with
[`docs/plan/12`](docs/plan/12-build-ci-and-testing.md) saying what each does and which are gates.

`--workers <n>` bounds how many projects compile and how many test assemblies run at once. It
defaults to 4 locally and to unbounded in CI, which has the machine to itself; `--workers 0` asks for
unbounded anywhere. The cap costs about five minutes on a whole-solution `Test` and is what keeps the
run from taking the machine away from everything else on it.

Some backends need a native binary that no package ships. `./build.sh RestoreNativeDeps` fetches each
one pinned and SHA-256-verified from [`build/native-dependencies.json`](build/native-dependencies.json),
and commits nothing.

## What is in here

| | |
|---|---|
| `Core/` | The engine: math, memory, jobs, VFS, serialization, ECS, the RHI, rendering, assets, audio, physics, animation, navigation, networking, video, XR, and the `Vixen.Ui` framework |
| `Platform/` | Per-target implementations of `Core/`'s contracts — Windows, Linux, macOS, Android, iOS, Web — and the graphics backends: Vulkan, OpenGL/GLES/WebGL2, WebGPU, Null |
| `Editor/` | The editor, built on `Vixen.Ui` and nothing else |
| `Raven/` | The shader language: a hand-written parser over a Roslyn-shaped syntax tree, a semantic phase, an IR, and GLSL + SPIR-V emitters |
| `Tools/` | The `vixen` CLI, the MSBuild SDK, the asset and shader compilers, the content server, the templates |
| `Samples/` | Eleven runnable samples, each the proof for one phase of the plan |
| `Benchmarks/` | The performance gates |
| `docs/` | The design record, the state of it, and the manual |

Every project keeps its tests as a sibling (`Vixen.Ecs` / `Vixen.Ecs.Tests`) rather than in a mirror
tree, and its own `README.md` — which is where the reasoning behind that subsystem lives, including
what it deliberately does not do. Those READMEs are the best entry point into any one area.

## Documentation

Three kinds, kept apart on purpose, because three places recording the same thing is how they come to
disagree.

| | |
|---|---|
| [`docs/plan/`](docs/plan/) | **The design record** — what Vixen is meant to be and why each decision was taken, in 25 documents plus the ADR register. It does not say what is built |
| [`docs/overview.md`](docs/overview.md) | **The state** — every feature and library with a status, a dependency tree over what is left, and one table of what is owed. Reconciled against the code, and it wins where a design document disagrees |
| [`docs/manual/`](docs/manual/) | **Reader-facing** — building a game and a server, the diagnostic-code and log-event registers, and the [third-party attribution manifest](docs/manual/third-party.md) |

Start with [`docs/plan/README.md`](docs/plan/README.md) for the index, or
[`docs/overview.md`](docs/overview.md) if the question is "what works today".

## The non-negotiables

Stated because each one shapes code you will read, and each is enforced by the build rather than by
convention. The reasoning is in [`docs/plan/00`](docs/plan/00-vision-and-principles.md) and the ADR
register in [`docs/plan/01`](docs/plan/01-technology-decisions.md).

- **All metaprogramming is Roslyn source generators.** No IL weaving, no `Mono.Cecil`, no
  post-processing — so NativeAOT and full trimming work, and generated code is ordinary steppable C#.
  `CheckArchitecture` fails the build if an IL rewriter appears in the restore graph (ADR-002).
- **iOS is NativeAOT-only, and that is gated.** `CheckAotIos` publishes every runtime assembly
  *rooted* and fails on any trim or AOT warning. Reflection debt is caught before it is expensive.
- **`Vixen.Ui` never references `Vixen.Engine`.** The moment it does, the application-framework claim
  is dead. Checked from Phase 0.
- **`internal` by default.** `public` needs a reason and a `PublicAPI.Unshipped.txt` entry, and
  `CheckApi` fails on an unapproved addition *and* on a silent removal.
- **Warnings are errors**, at `AnalysisLevel=latest-recommended`. A rule that conflicts with a
  deliberate decision is disabled **by name with a written reason** in `.editorconfig`; the level
  itself is never lowered, because an exclusion is reviewable and a lowered level is not.
- **Correctness is judged by something other than us where possible** — the Yoga conformance suite,
  the Unicode Consortium's test data, `spirv-val`, golden images, Arch's benchmarks. And gates are
  themselves checked by sabotage: break the thing on purpose and confirm the suite goes red.

## Status

The engine boots on three desktops, the iOS Simulator and the Android emulator; renders a forward+ PBR
pipeline with shadows and post FX; runs a UI framework that passes the full Yoga and UAX conformance
suites; hosts an editor that opens a project, imports assets, builds content, edits a scene and runs
the game; and carries a server-authoritative networking stack that meets all five of its exit criteria.
What is unfinished, and what blocks it, is in [`docs/overview.md`](docs/overview.md) — including a
dependency tree so independent work can be scheduled in parallel.

## Licence

Apache-2.0 — see [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE). Apache's express patent grant is the
reason over MIT: a studio shipping a commercial title on a third-party engine cares about patent peace,
and legal review is materially easier (ADR-015). The design-time audit is in
[`docs/plan/01`](docs/plan/01-technology-decisions.md) § ADR-015; **what is actually depended on** is
[`docs/manual/third-party.md`](docs/manual/third-party.md), where every licence names the artefact it
was read from and a build gate keeps the inventory honest.

⚠ That page **corrects** a claim this section used to make — that every dependency is permissive and
"no shipped game links anything that is not". `Silk.NET.OpenAL.Soft.Native` declares
**LGPL-2.0-or-later** and ships `libopenal` in every build with sound. Dynamically linked against a
separately-shipped library, which is what it is, that is dischargeable — but it is copyleft, its
obligations are not discharged today, and four further packages ship no licence statement at all.
Read the page before making a binary distribution.
