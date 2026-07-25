# 00 — Vision and Principles

## What Vixen is

A single .NET stack that covers three product shapes with one codebase. **The audience order is
decided and it matters** — where these compete for effort, earlier wins:

1. **A game engine, for game developers.** ✅ *This is the primary audience.* Vulkan-first renderer,
   ECS, physics, audio, animation, VFX, input, six platforms, and a build pipeline that ships games.
2. **An editor** — built *on* the engine, using the engine's own UI. Not a separate WPF/Avalonia
   application. **The editor is the first large-scale application built on Vixen-as-application-platform**,
   and it is therefore the proof of (3) rather than a sample being the proof.
3. **An application framework** — the retained-mode, reactive, web-technology-shaped UI layer that makes
   (2) possible and that third parties can use for Photoshop/Blender-class tooling: dockable
   multi-window shells, thousands of live widgets, virtualised lists, canvas-heavy viewports,
   custom-drawn everything.

The consequence of that ordering: the UI framework is scoped and validated by **what the editor
actually needs**, not by a hypothetical general-purpose widget toolkit. General-purpose capability is a
welcome outcome of building the editor well, not a parallel goal with its own budget. Where a
general-framework feature has no editor consumer (a full CSS inline formatting context, an
accessibility bridge, exotic text layout), it is P2 and says so.

Stride supplies the architectural spine (asset pipeline, render feature model, effect system). Unity
supplies the ergonomics (`.meta` files, addressables, input system, node graph UX, component
scripting). Neither is copied — both are read carefully and re-derived on modern .NET.

## What Vixen is not

- **Not a Stride fork.** No `Stride.*` code is copied. Stride is read, its design decisions are
  understood, and the good ones are re-implemented with modern C#. Stride is MIT-licensed so
  copying would be legal; it is rejected because Stride carries fifteen years of .NET Framework
  assumptions (WPF editor, Mono.Cecil IL weaving, `MicroThreading`, XML/Yaml duality) that are
  exactly what this project exists to shed.
- **Not reflection-driven.** Every place Stride uses `Reflection.Emit`, an assembly post-processor,
  or runtime type scanning, Vixen uses a Roslyn source generator. This is what makes iOS
  (NativeAOT-only) and trimming work at all.
- **Not a scripting-language host.** No embedded Mono, no Lua, no IronPython. C# compiled by Roslyn,
  with .NET Hot Reload (`MetadataUpdateHandler`) for iteration.
- **Not "engine first, tools later."** The UI framework is a phase-1 deliverable because the editor
  depends on it and the editor is how the engine gets used.

## Non-negotiables

These are checked by CI, not by good intentions. See [12-build-ci-and-testing.md](12-build-ci-and-testing.md).

| Principle | Enforcement |
|---|---|
| Zero steady-state allocation in the frame loop | `dotnet-counters` gate + BenchmarkDotNet `MemoryDiagnoser` regression tests; `Gen0` collections per 1000 frames asserted at 0 in the empty-scene and 10k-entity benchmarks |
| AOT- and trim-clean | `IsAotCompatible=true` on every runtime project; `PublishTrimmed` + `PublishAot` smoke publish in CI; zero IL2xxx/IL3xxx warnings, `TreatWarningsAsErrors` |
| No runtime IL generation | Analyzer bans `System.Reflection.Emit`, `Expression.Compile`, `Activator.CreateInstance(Type)` outside editor-only assemblies |
| Public API is deliberate | `PublicApiAnalyzers` with checked-in `PublicAPI.Shipped.txt` per package; API changes are a reviewed diff |
| Determinism | Deterministic builds, `ContinuousIntegrationBuild`, fixed-seed simulation tests, content hashes reproducible across OS |
| Every subsystem has tests | xunit v3 + NSubstitute + Shouldly; coverage floor per project in the Nuke `Test` target |
| Warnings are errors | `TreatWarningsAsErrors`, `AnalysisLevel=latest-all`, nullable enabled everywhere, no `#pragma warning disable` without a linked issue |

## Layer discipline

Dependencies flow strictly downward. A violation is a build break, enforced by a Nuke target that
walks the project graph.

```
                    Editor.App
                        │
        ┌───────────────┼───────────────┐
   Editor.Ui      Editor.NodeGraph   Editor.Assets
        │               │                │
        └──────── Editor.Core ───────────┘
                        │
    ┌──────────┬────────┴────────┬──────────────┐
  Vixen.Ui  Vixen.Engine    Vixen.Assets   Vixen.Rendering
    │           │                │              │
    └───────────┴────────┬───────┴──────────────┘
                         │
              Vixen.Ecs, Vixen.Graphics, Vixen.Shaders
                         │
      Vixen.Core.* (Mathematics, Memory, Collections,
                    Threading, IO, Serialization, Diagnostics)
                         │
                   Vixen.Platform.*
```

Hard rules:

- `Core/*` never references `Platform/*` types directly — it consumes interfaces that
  `Vixen.Platform` defines and platform assemblies implement. `Vixen.Platform` itself is contracts
  plus pure-managed helpers, no P/Invoke.
- `Vixen.Graphics` defines the RHI. Backends (`Vixen.Graphics.Vulkan`, …) reference it; nothing
  references a backend except the bootstrapper, which selects one at runtime.
- `Vixen.Ui` has **no** dependency on `Vixen.Engine`. The UI framework must be usable to build a
  pure desktop application with no scene, no ECS world, and no game loop beyond a render tick. This
  is the single most important boundary in the codebase and the thing that makes the
  "Photoshop/Blender" claim real rather than aspirational.
- Editor assemblies may use reflection, LINQ, and allocate freely. Runtime assemblies may not.
  Editor-only code lives in `Editor/*` or behind `#if VIXEN_EDITOR` in a project that ships a
  separate editor-flavoured build.

## Coding standards (C# 14 / .NET 10)

Full ruleset lives in `.editorconfig` + `Directory.Build.props`. The intent:

- **`readonly record struct` for value-semantics data**; `sealed class` by default for reference
  types; `record` only where value equality is genuinely wanted (descriptors, keys, config).
- **`internal` by default.** `public` requires a reason and a `PublicAPI.Unshipped.txt` entry.
  `InternalsVisibleTo` only to the matching `.Tests` assembly.
- **`ref struct` + `Span<T>` + `scoped`** for all buffer traversal. `allows ref struct` generic
  anti-constraints on visitors and enumerators so they work over stack-only types.
- **`InlineArray`** for fixed-size hot structures (descriptor set slots, vertex layouts, clustered
  light lists) instead of arrays or `stackalloc`+pointer juggling.
- **Collection expressions and `params ReadOnlySpan<T>`** on all variadic APIs so call sites do not
  allocate.
- **`field` keyword** for property backing where a validating setter is needed, to avoid the
  ceremony that currently pushes people to public fields.
- **Extension members** (C# 14) for the "fluent descriptor" APIs (`RenderTargetDescription`,
  `PipelineStateDescription`), keeping the core structs minimal.
- **`static` lambdas everywhere in hot paths**; no closure allocation. An analyzer enforces
  `VSTHRD`-style rules plus a custom "no implicit closure in `[HotPath]` method" rule.
- **`[MethodImpl(AggressiveInlining)]`** only with a benchmark in the PR proving it.
- **No `async`/`await` in the frame loop.** Async is for asset loading, editor I/O, and tooling.
  Frame work uses the job system ([03](03-core-foundation.md)).
- **`Unsafe`/pointers are allowed and expected** in `Core.Memory`, `Core.Collections`, RHI backends,
  and the layout engine. They are banned elsewhere (`AllowUnsafeBlocks` is opt-in per project).

## The quality bar, concretely

The plan is "bulletproof" only if these are true at 1.0:

- A blank Vixen app boots to a cleared swapchain in **< 250 ms** on desktop, **< 800 ms** on mobile.
- A 10 000-entity scene with 2 000 draw calls runs at **> 240 fps** on a mid-range 2024 GPU with the
  forward+ pipeline, with **zero** Gen0 collections over 10 000 frames.
- The editor with 5 docked panels, a 3D viewport, and a 500-node shader graph holds **60 fps** and
  **< 2 ms** CPU per UI frame.
- Editing a `.vxml` file updates the running editor in **< 200 ms** without losing panel state.
- Editing a `.rvn` shader recompiles and swaps the effect in **< 500 ms** without a device reset.
- A full clean content build of the 1.0 sample project completes in **< 60 s** on 8 cores; an
  incremental build after touching one texture completes in **< 1 s**.
- `dotnet new vixen-app && dotnet run` works on all six targets from a clean machine with only the
  .NET 10 SDK plus documented platform SDKs.
