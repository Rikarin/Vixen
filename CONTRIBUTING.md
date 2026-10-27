<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Working on Vixen

This page is for anyone changing the engine itself: how the repository is laid out, how the build is
run, where the documentation lives and which rules the build enforces. If you want to *use* Vixen,
start with the [README](README.md) instead.

## Building

```bash
./build.sh Compile      # or build.cmd on Windows
./build.sh Test
```

The SDK version is pinned in [`global.json`](global.json); nothing else is required to build the
solution. `./build.sh` is the entry point CI and developers share. [The README](README.md#building-from-source)
lists every target, and [`docs/plan/12`](docs/plan/12-build-ci-and-testing.md) says what each does and
which are gates. While working, prefer one test project at a time:

```bash
dotnet test Core/Vixen.Ecs.Tests -c Debug --nologo
```

`--workers <n>` bounds how many projects compile and how many test assemblies run at once. It
defaults to 4 locally and to unbounded in CI, which has the machine to itself; `--workers 0` asks for
unbounded anywhere. It is what keeps the run from taking the machine away from everything else on
it, and it costs less than it reads: measured from the 178 TRX of the 2026-09-05 sweep, a capped
`Test` reached a 3.93× speed-up on four lanes — 98% of the ideal, nine seconds off the optimal
packing of the same assemblies. ⚠ Raising the cap **cannot** buy more than two and a half minutes and
buys nothing at all above six, because the floor is one 412-second assembly and not the fan-out.

The second multiplier is `xunit.runner.json`'s `maxParallelThreads`, which sizes each assembly's own
collection pool and which `--workers` cannot reach. It is `0.5x` — five threads on a ten-core box —
and that number is now measured by the same interval arithmetic over the same 178 TRX. ⚠ **No
assembly in that sweep ever ran more than five collections at once**, which is the instrument check
rather than a curiosity: a run where the file failed to be copied beside the assembly would look
identical in every other respect and would report ten. Doubling the pool to `1.0x` is worth **36
seconds spread over six assemblies** and nothing at all to the other 172, because every large one is
floored by its longest *collection* rather than by the pool. ⚠ And the long pole is not on that list
whatever the arithmetic says: `Vixen.Editor.App.Tests` disables collection parallelism outright, so
neither multiplier can touch the 382 seconds it contributes. It was disabled over a real data race on
the process-wide `Strings` signal (#365); since #1413 that node is per thread and the race is gone, but
the language is still process-wide and one class there changes it, so re-enabling is its own change.

⚠ **A multiplier truncates, so `0.5x` is the wrong value for a small machine and CI runs on small
machines.** xunit computes it as `(int)(m × ProcessorCount)`, a cast that rounds toward zero: ten
cores give the five that was measured, four give two, and **three give one** — not a halved pool but
collection parallelism switched off. `macos-14` is a three-core M1 runner. So the file is chosen
rather than fixed: `xunit.runner.ci.json` (`1.0x`, which is what CI ran at for its whole history
before the cap existed) under `GITHUB_ACTIONS`, and the measured `xunit.runner.json` everywhere else.
`Tools/Vixen.ApiCheck.Tests/TestParallelismTests.cs` holds both to that arithmetic, and checks the
copy beside its own assembly — which is the only evidence anywhere that the link works at all.

Agent worktrees under `.claude/worktrees` are never cleaned up by anything, and each carries its own
`bin`/`obj` — about 25 GB apiece once the solution has been built in both configurations.
`./build.sh PruneWorktrees` lists which of them are merged into master, clean, unlocked and unwritten
for `--idle-minutes` (30 by default), and `--remove-merged` removes those and only those; anything
failing one of the four conditions, and any directory in there that is not a registered worktree at
all, is reported and left alone. ⚠ The fourth condition is there because the lock is the runner's
habit and not its promise — two of thirteen live agent worktrees carried none on 2026-09-05, and
merged-and-clean is exactly what a live agent looks like between its branch being merged and its
process ending. It costs the merge-then-prune sweep nothing but a delay: `--idle-minutes 0` reclaims
the disk immediately for an operator who knows what else is running.

Some backends need a native binary that no package ships. `./build.sh RestoreNativeDeps` fetches each
one pinned and SHA-256-verified from [`build/native-dependencies.json`](build/native-dependencies.json),
and commits nothing.

## What is in here

| | |
|---|---|
| `Core/` | The engine: math, memory, jobs, VFS, serialization, ECS, the RHI, rendering, assets, audio, physics, animation, navigation, networking, video, XR, and the `Vixen.Ui` framework |
| `Platform/` | Per-target implementations of `Core/`'s contracts — Windows, Linux, macOS, Android, iOS, Web — and the graphics backends: Vulkan, OpenGL/GLES/WebGL2, WebGPU, Null |
| `Editor/` | The editor, built on `Vixen.Ui` and nothing else |
| `Gameplay/` | The gameplay libraries games compose: combat, inventory, items, quests, economy, crafting, PvP and the rest |
| `Live/` | The live-service stack: realms, clusters, gates, matchmaking, persistence, and placement on Docker, Kubernetes or plain processes |
| `Raven/` | The shader language: a hand-written parser over a Roslyn-shaped syntax tree, a semantic phase, an IR, and GLSL + SPIR-V emitters |
| `Tools/` | The `vixen` CLI, the MSBuild SDK, the asset and shader compilers, the content server, the templates |
| `Samples/` | Runnable samples, each the proof for one part of the plan |
| `Benchmarks/` | The performance gates |
| `Testing/` | Test-only source shared across test assemblies |
| `docs/` | The design record, the state of it, the guide and the manual |

Every project keeps its tests as a sibling (`Vixen.Ecs` / `Vixen.Ecs.Tests`) rather than in a mirror
tree, and its own `README.md` — which is where the reasoning behind that subsystem lives, including
what it deliberately does not do. Those READMEs are the best entry point into any one area.

`Vixen.slnx` holds every project that builds without a workload. The Android, iOS and Web heads, the
`dotnet new` templates and `build/_build.csproj` are outside it on purpose, and the solution says why
beside each one. Files that no project owns — the root configuration and everything under `docs/` —
are in solution folders that mirror the directory tree, so an IDE shows them.

## Documentation

Four kinds, kept apart on purpose, because several places recording the same thing is how they come
to disagree.

| | |
|---|---|
| [`docs/plan/`](docs/plan/) | **The design record** — what Vixen is meant to be and why each decision was taken, in 51 documents plus the ADR register. It does not say what is built |
| [`docs/overview.md`](docs/overview.md) | **The state** — every feature and library with a status, a dependency tree over what is left, and one table of what is owed. Reconciled against the code, and it wins where a design document disagrees |
| [`docs/guide/`](docs/guide/) | **How to use it** — tutorials and how-tos, one page per feature, grouped by area |
| [`docs/manual/`](docs/manual/) | **Reference** — building a game and a server, the diagnostic-code and log-event registers, and the [third-party attribution manifest](docs/manual/third-party.md) |

Start with [`docs/plan/README.md`](docs/plan/README.md) for the index, or
[`docs/overview.md`](docs/overview.md) if the question is "what works today".

## The non-negotiables

Stated because each one shapes code you will read, and each is enforced by the build rather than by
convention. The reasoning is in [`docs/plan/00`](docs/plan/00-vision-and-principles.md) and the ADR
register in [`docs/plan/01`](docs/plan/01-technology-decisions.md).

- **All metaprogramming is Roslyn source generators.** No IL weaving, no `Mono.Cecil`, no
  post-processing — so NativeAOT and full trimming work, and generated code is ordinary steppable C#.
  `CheckArchitecture` fails the build if an IL rewriter appears in the restore graph (ADR-002).
- **iOS is NativeAOT-only, and that is gated.** `CheckAotIos` publishes the 21 assemblies a phone
  links, *rooted*, and fails on any trim or AOT warning. Reflection debt is caught before it is
  expensive. ⚠ The gate now also asserts, before publishing, that the probe still roots every
  assembly it references and still declares `PublishAot` — but it does **not** yet read the output,
  so a publish that quietly stopped being ahead-of-time for a reason outside the project file would
  still pass ([#634](https://github.com/Rikarin/Vixen/issues/634)), and no workflow runs it
  ([#327](https://github.com/Rikarin/Vixen/issues/327)).
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

## Issues

GitHub issues on [Rikarin/Vixen](https://github.com/Rikarin/Vixen/issues) are the task tracker. An
issue is closed when its work is merged to master, with the real outcome — including "refuted, not
fixed" — and an issue that is only partly done says in a comment what is left.
