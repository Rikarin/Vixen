# 02 — Repository Layout

You asked for `Core`, `Editor`, `Platform` as the three main folders, each containing one subfolder
per `.csproj` library. That is the spine below, with four additions the build genuinely needs
(`Raven`, `Tools`, `Samples`, `build`) and one convention decision (tests as siblings — ADR-014).

## Top level

```
Vixen/
├── .config/
│   └── dotnet-tools.json          # nuke.globaltool, dotnet-coverage, dotnet-counters, dotnet-trace
├── .github/
│   ├── workflows/                 # ci.yml, release.yml, nightly-platforms.yml, docs.yml
│   └── ISSUE_TEMPLATE/
├── build/                         # Nuke — the single entry point for every build action
│   ├── _build.csproj
│   ├── Build.cs                   # partial: target graph
│   ├── Build.Compile.cs
│   ├── Build.Test.cs
│   ├── Build.Pack.cs
│   ├── Build.Native.cs            # native deps acquisition/verification
│   ├── Build.Shaders.cs           # Raven core-library compilation
│   ├── Build.Platforms.cs         # android/ios/web app-head builds
│   ├── Build.ArchitectureRules.cs # layer-violation gate
│   └── Build.Release.cs
├── Core/                          # ── the engine and framework runtime ──
├── Platform/                      # ── per-OS/backend implementations ──
├── Editor/                        # ── the editor, built on Core ──
├── Raven/                         # ── the shader compiler (existing project, absorbed) ──
├── Tools/                         # ── CLI, workers, SDK, templates ──
├── Samples/
├── Benchmarks/
├── Testing/                       # test-only source shared across test assemblies — linked, not a project
│   ├── Vixen.Testing.props        # the Compile items, and why this is linked rather than referenced
│   └── Measured.cs                # allocation measurement with the collector kept out of it
├── references/                    # git submodules / vendored read-only reference code — NOT built
│   ├── stride/                    # symlink or submodule → /Users/jiu/Projects/stride
│   ├── arch/                      # github.com/genaray/Arch          (ADR-004)
│   ├── flexbox/                   # github.com/ru-ace/Flexbox        (ADR-006)
│   ├── yoga/                      # github.com/facebook/yoga — conformance fixtures
│   ├── signals-dotnet/            # github.com/fedeAlterio/SignalsDotnet (ADR-007)
│   └── purrnet/                   # github.com/PurrNet/PurrNet — networking reference (MIT), see 16
├── docs/
│   ├── plan/                      # this directory
│   ├── adr/                       # ADRs promoted out of 01 as they accumulate
│   └── manual/                    # user-facing docs (DocFX)
├── .editorconfig
├── .gitattributes                 # binary/lfs rules, .meta text merge
├── .gitignore
├── Directory.Build.props          # shared properties, analyzers, versioning
├── Directory.Build.targets        # shared targets, PublicAPI wiring
├── Directory.Packages.props       # Central Package Management — every version pinned
├── global.json                    # SDK pin
├── nuget.config
├── Vixen.slnx                     # full solution
├── Vixen.Core.slnf                # filter: Core + Platform + tests  (fast IDE load)
├── Vixen.Editor.slnf              # filter: Editor + deps
├── Vixen.Raven.slnf               # filter: Raven + tests
├── LICENSE.md
└── README.md
```

`references/` is excluded from the solution and from every glob. It exists so that "how did Stride
solve this" is a `grep` away rather than a browser tab away. CI does not restore or build it.

## `Core/`

Every folder here is one `net10.0` class library plus its sibling test project. `Vixen.` prefix on
every assembly; folder name == assembly name == root namespace.

```
Core/
├── Vixen.Core/                          # annotations, service registry, IDs, time, disposables, pooling
├── Vixen.Core.Tests/
├── Vixen.Core.Mathematics/              # ADR-003
├── Vixen.Core.Mathematics.Tests/
├── Vixen.Core.Memory/                  # allocators, arenas, NativeArray<T>, MemoryOwner, ring buffers
├── Vixen.Core.Memory.Tests/
├── Vixen.Core.Collections/             # SparseSet, ChunkedList, SmallList, PooledDictionary, BitSet
├── Vixen.Core.Collections.Tests/
├── Vixen.Core.Threading/               # job system, JobHandle, ParallelFor, MainThread affinity
├── Vixen.Core.Threading.Tests/
├── Vixen.Core.IO/                      # VFS, virtual paths, async streams, mmap, file watcher
├── Vixen.Core.IO.Tests/
├── Vixen.Core.Serialization/           # binary serializer runtime, chunks, content refs, LZ4/Zstd
├── Vixen.Core.Serialization.Generators/ # ── source generator ──
├── Vixen.Core.Serialization.Tests/
├── Vixen.Core.Reflection/              # generated type registry, attribute discovery, no runtime scan
├── Vixen.Core.Reflection.Generators/    # ── source generator ──
├── Vixen.Core.Reflection.Tests/
├── Vixen.Core.Syntax/                  # GreenNode/red-tree infra shared by Raven, VXML, VCSS
├── Vixen.Core.Syntax.Tests/
├── Vixen.Core.Yaml/                    # .meta / .vxasset read-write, tagged-type polymorphic emitter
├── Vixen.Core.Yaml.Tests/
├── Vixen.Core.Diagnostics/             # ILogger sink, profiler, counters, trace export
├── Vixen.Core.Diagnostics.Tests/
├── Vixen.Core.Imaging/                 # engine texture formats, BCn/ASTC/ETC2 encode-decode, mip gen
├── Vixen.Core.Imaging.Tests/
│
├── Vixen.Ecs/                          # ADR-004 — archetype ECS
├── Vixen.Ecs.Generators/               # ── source generator: queries, systems ──
├── Vixen.Ecs.Tests/
│
├── Vixen.Graphics/                     # ✅ RHI abstraction — ADR-001
├── Vixen.Graphics.Tests/
├── Vixen.Shaders/                      # ✅ param keys + std140 writers; effect system still open
├── Vixen.Shaders.Generators/           # ✅ source generator: Raven reflection → C# keys ──
├── Vixen.Shaders.Tests/
├── Vixen.Rendering/                    # ✅ objects, features, views, stages, culling, sorting
├── Vixen.Rendering.Tests/
├── Vixen.Rendering.PostFx/             # post-processing chain (own project: heavy, optional)
├── Vixen.Rendering.PostFx.Tests/
│
├── Vixen.Assets/                       # runtime: ContentManager, catalog, addressables, streaming
├── Vixen.Assets.Tests/
│
├── Vixen.Engine/                       # game loop, scenes, entities-as-facade, prefabs, Behavior
├── Vixen.Engine.Tests/
├── Vixen.Input/                        # ✅ devices, actions, .vxinput, rebinding — doc 11 § Input
├── Vixen.Input.Generators/             # ── source generator: .vxinput → typed accessors ──
├── Vixen.Input.Tests/
├── Vixen.Audio/                        # ✅ software mixer, buses, effects, 3D, streaming, ECS
├── Vixen.Audio.Tests/
├── Vixen.Audio.Codecs/                 # ✅ Ogg Vorbis + Opus behind IAudioStreamDecoder, both managed
├── Vixen.Audio.Codecs.Tests/
├── Vixen.Physics/                      # ✅ Jolt integration — bodies, shapes, constraints,
│                                       #   characters, queries, triggers, layers, CCD, ECS bridge
├── Vixen.Physics.Tests/
├── Vixen.Animation/                    # ✅ skeletal, blend trees, layers, IK, state machine
├── Vixen.Animation.Tests/
├── Vixen.Vfx/                          # 🟡 particles: SoA storage, compiled graph, CPU sim —
│                                       #   renderers and the GPU path not yet
├── Vixen.Vfx.Tests/
├── Vixen.Navigation/                   # ✅ navmesh: bake, query, crowd — managed, no native dep
├── Vixen.Navigation.Tests/
├── Vixen.Net/                          # session, tick, channels, replication, rules — see 16
├── Vixen.Net.Generators/               # ── source generator: RPC senders, serializers, delta ──
├── Vixen.Net.Tests/
├── Vixen.Net.Transport.Udp/            # + .Tests
├── Vixen.Net.Transport.WebSocket/      # + .Tests
├── Vixen.Net.Transport.Local/          # in-process: host mode, offline, and every test
├── Vixen.Net.Transport.Relay/          # + .Tests
├── Vixen.Video/
├── Vixen.Video.Tests/
│
├── Vixen.Ui/                           # element tree, properties, events, input routing, rendering
├── Vixen.Ui.Tests/
├── Vixen.Ui.Reactive/                  # signals — ADR-007
├── Vixen.Ui.Reactive.Tests/
├── Vixen.Ui.Layout/                    # flexbox + grid + block — ADR-006
├── Vixen.Ui.Layout.Tests/              #   ← hosts the ported Yoga conformance suite
├── Vixen.Ui.Styling/                   # VCSS parse (ExCSS), cascade, selector matcher, transitions
├── Vixen.Ui.Styling.Tests/
├── Vixen.Ui.Styling.Utilities/         # the Tailwind-like preprocessor + design tokens
├── Vixen.Ui.Styling.Utilities.Tests/
├── Vixen.Ui.Markup/                    # VXML syntax tree, parser, binder, diagnostics
├── Vixen.Ui.Markup.Generators/         # ── source generator: .vxml/.vcss → C# ──
├── Vixen.Ui.Markup.Tests/
├── Vixen.Ui.Text/                      # HarfBuzz shaping, MSDF atlas, line breaking, bidi
├── Vixen.Ui.Text.Tests/
├── Vixen.Ui.Controls/                  # the standard widget library
├── Vixen.Ui.Controls.Tests/
├── Vixen.Ui.Controls.Advanced/         # DataGrid, TreeView, Docking, PropertyGrid, Timeline, Canvas
├── Vixen.Ui.Controls.Advanced.Tests/
└── Vixen.Ui.HotReload/                 # dev-only: watcher, reparse, state preservation
    └── Vixen.Ui.HotReload.Tests/
```

**Why `Vixen.Ui.*` is this granular.** It is the largest new subsystem and the one with the most
independent testability. Splitting layout, styling, markup, and text apart means the Yoga conformance
suite, the CSS cascade tests, the parser golden tests, and the shaping tests are four independent
gates that can go green at different times. It also lets an application consumer take
`Vixen.Ui + Vixen.Ui.Controls` without pulling `Vixen.Engine`.

**Why `Vixen.Ui` does not depend on `Vixen.Engine`.** See [00](00-vision-and-principles.md). `Vixen.Ui`
depends on `Vixen.Graphics`, `Vixen.Assets`, `Vixen.Input`, `Vixen.Core.*` — and nothing else. The
`Vixen.Engine` integration (a `UiComponent` that renders a UI tree into a 3D scene) lives in
`Vixen.Engine`, pointing the other way.

## `Platform/`

```
Platform/
├── Vixen.Platform/                     # ✅ contracts only: IPlatform, IWindow, ISurface, PlatformEvent,
│                                       #   IDisplayInfo, IFileSystemHost, IClipboard, INativeDialogs,
│                                       #   ILifecycle, IInputSource, ITextInput, IPowerInfo,
│                                       #   IProcessorTopology
├── Vixen.Platform.Tests/
├── Vixen.Platform.Desktop/             # SDL3 via Silk.NET.SDL — shared by Win/Linux/macOS
├── Vixen.Platform.Desktop.Tests/
├── Vixen.Platform.Headless/            # ✅ no window/GPU/audio: dedicated server + batch tooling (17)
├── Vixen.Platform.Headless.Tests/
├── Vixen.Platform.Windows/             # net10.0-windows: DXGI enumeration, WinRT file dialogs, jump lists
├── Vixen.Platform.Linux/               # Wayland/X11 quirks, XDG paths, portal dialogs
├── Vixen.Platform.MacOS/               # net10.0 + ObjC interop: NSWindow chrome, sandbox paths, MoltenVK load
├── Vixen.Platform.Android/             # net10.0-android: Activity lifecycle, JNI, AAssetManager, IME
│                                       #   NOT in Vixen.slnx — needs the android workload to evaluate
├── Vixen.Platform.iOS/                 # net10.0-ios: UIViewController, CAMetalLayer for MoltenVK, IME
│                                       #   NOT in Vixen.slnx — needs macOS, Xcode and the ios workload
├── Vixen.Platform.Web/                 # net10.0 + Sdk.WebAssembly: JSImport/JSExport, canvas, WebGL2 surface
│
├── Vixen.Graphics.Vulkan/              # primary
├── Vixen.Graphics.Vulkan.Tests/
├── Vixen.Graphics.Direct3D12/
├── Vixen.Graphics.Direct3D12.Tests/
├── Vixen.Graphics.OpenGL/              # GL 4.5 core (desktop) + GLES 3.0/3.2 (mobile) + WebGL2 (browser)
├── Vixen.Graphics.OpenGL.Tests/
├── Vixen.Graphics.WebGPU/
├── Vixen.Graphics.WebGPU.Tests/
├── Vixen.Graphics.Null/                # ✅ headless: CI graphics tests AND the shipping dedicated-server backend (17)
├── Vixen.Graphics.Null.Tests/
│
├── Vixen.Audio.Backend.OpenAL/         # ✅ desktop + mobile: a sink for the software mixer
├── Vixen.Audio.Backend.OpenAL.Tests/
├── Vixen.Audio.Backend.WebAudio/       # ✅ net10.0-browser — scheduled AudioBufferSourceNode queue
│                                       #   NOT in Vixen.slnx — needs the wasm-tools workload
└── Vixen.Platform.Native/              # ✅ RID mapping, runtimes/ layout, DllImportResolver (acquisition owed)
```

Backend projects live under `Platform/` rather than `Core/` because they are *platform
implementations* of a `Core/` contract, and because it makes the "one folder per deployment concern"
story clean: to add a platform you add folders in exactly one place.

**Runtime backend selection.** `Vixen.Graphics.Null` is the only backend referenced by tests.
Applications reference `Vixen.App` (a meta-package in `Tools/`) which brings in the backends valid for
its RID via conditional `PackageReference`. Selection at boot is
`GraphicsBackendSelector.Select(preferences, platform)` — no reflection-based plugin scanning;
the app head's generated `VixenBackendRegistry` (source generator over referenced assemblies)
lists what is linked in, which keeps it trimming-safe.

## `Editor/`

```
Editor/
├── Vixen.Editor.Core/            # project model, asset database, GUID index, undo/redo, selection,
│   │                             #   property system, import orchestration, build orchestration
│   └── Vixen.Editor.Core.Tests/
├── Vixen.Editor.Assets/          # importers (Assimp, ImageSharp, fonts, audio) + asset compilers
│   └── Vixen.Editor.Assets.Tests/
├── Vixen.Editor.Ui/              # editor shell: docking, command palette, menus, dialogs, theming
│   └── Vixen.Editor.Ui.Tests/
├── Vixen.Editor.Inspector/       # property drawers, attribute-driven editors, multi-object editing
│   └── Vixen.Editor.Inspector.Tests/
├── Vixen.Editor.SceneView/       # viewport, gizmos, picking, camera nav, grid, selection outline
│   └── Vixen.Editor.SceneView.Tests/
├── Vixen.Editor.NodeGraph/       # reusable node-graph framework: model, layout, wiring, undo, groups
│   └── Vixen.Editor.NodeGraph.Tests/
├── Vixen.Editor.ShaderGraph/     # nodes → Raven source generation
│   └── Vixen.Editor.ShaderGraph.Tests/
├── Vixen.Editor.VfxGraph/        # nodes → VFX runtime graph
│   └── Vixen.Editor.VfxGraph.Tests/
├── Vixen.Editor.AnimationGraph/  # blend trees / state machines
│   └── Vixen.Editor.AnimationGraph.Tests/
├── Vixen.Editor.Profiler/        # in-editor profiler, frame debugger, memory view
│   └── Vixen.Editor.Profiler.Tests/
├── Vixen.Editor.Debugger/        # remote inspector client, live entity/property editing
│   └── Vixen.Editor.Debugger.Tests/
├── Vixen.Editor.Plugin/          # public extensibility API for third-party editor plugins
│   └── Vixen.Editor.Plugin.Tests/
└── Vixen.Editor.App/             # the standalone executable; PublishSingleFile per RID
```

## `Raven/`

The existing project, moved in with history (see migration below) and renamed to the monorepo
convention. Its current `RootNamespace` is already `Vixen.Raven`.

```
Raven/                            ✅ renamed — this layout is live
├── Directory.Build.props         # tracked analyzer debt, scoped per project
├── Vixen.Raven/                  # was Compiler/  — syntax, semantic, IR, GLSL + SPIR-V emit
├── Vixen.Raven.Tests/            # was Tests/     — sibling, per ADR-014
├── Vixen.Raven.Cli/              # was Cli/       — AssemblyName stays `raven`
└── Library/                      # was Feed/ — the shipped .rvn standard library (PBR, math, etc.)
```

Raven carries **no roadmap of its own**. Its `docs/IMPLEMENTATION_PLAN.md` was retired once every
phase in it was complete; [07](07-raven-shader-pipeline.md) is the plan of record, and what was still
open in that file is
[§ I](07-raven-shader-pipeline.md#i-gaps-carried-over-from-ravens-retired-implementation-plan) there.
Two roadmaps for one compiler is how they come to disagree.

`Tools/SyntaxGenerator/` is not in this tree: the `Syntax.xml` generator is not
Raven-specific and now lives at `Core/Vixen.Core.Syntax.Generator/`, alongside the tree
it generates against. Raven references it as an analyzer and supplies its own
`Syntax.xml`.

Projects the plan anticipates but that do not exist yet — add them when the code needs
splitting out of `Vixen.Raven`, not before:
`Vixen.Raven.Transpile` (SPIRV-Cross wrapper → ESSL/HLSL/MSL/WGSL) and
`Vixen.Raven.Reflection` (binding/layout metadata for `Vixen.Shaders.Generators`).
`Vixen.Raven.Spirv` is **not** listed: GLSL and SPIR-V emission land together in the same
phase ([07](07-raven-shader-pipeline.md)), so both emitters live in `Vixen.Raven` unless
there is a reason to separate them.

`Vixen.Core.Syntax` extraction: Raven's `SyntaxNode`/`GreenNode`/`SyntaxToken`/`SyntaxTrivia`/
`SeparatedSyntaxList`/`SyntaxList<T>` and the `SyntaxGenerator` (`Syntax.xml` → node classes) are
generic infrastructure. Phase 0 lifts them into `Core/Vixen.Core.Syntax` and
`Core/Vixen.Core.Syntax.Generator`, with Raven, VXML, and VCSS all declaring their own `Syntax.xml`.
This is the single highest-leverage refactor available: it turns three parser front ends into one
piece of tested infrastructure plus three grammars.

## `Tools/`

```
Tools/
├── Vixen.Cli/                    # `dotnet vixen` global tool: new, build, run, import, pack, serve, doctor
│   └── Vixen.Cli.Tests/
├── Vixen.AssetCompiler/          # ✅ out-of-process import worker, crash-isolated; parallel owed
│   └── Vixen.AssetCompiler.Tests/
├── Vixen.ContentServer/          # local CDN emulator for addressable remote-catalog testing
│   └── Vixen.ContentServer.Tests/
├── Vixen.ShaderCompilerService/  # remote shader compile service for mobile/console iteration
│   └── Vixen.ShaderCompilerService.Tests/
├── Vixen.Sdk/                    # MSBuild SDK: props/targets that wire .meta import + content build
│   └── Vixen.Sdk.Tests/          #   into `dotnet build` for consumer projects
├── Vixen.App/                    # meta-package: sensible default reference set for an app head
├── Vixen.Templates/              # dotnet new templates: vixen-game, vixen-app, vixen-lib, vixen-plugin
│   └── Vixen.Templates.Tests/
├── Vixen.ApiCheck/               # public API surface diffing, run in CI
├── Vixen.AotProbe/               # the subject of `nuke CheckAot`: every runtime assembly, rooted
└── Vixen.AotProbe.iOS/           # the same for `ios-arm64` — outside the solution, needs the ios workload
```

**What `Vixen.Cli` has so far, and one correction to the verb list above.** `import`,
`content build`, `content serve` and `doctor` are built ([README](../../Tools/Vixen.Cli/README.md));
`new`, `run` and `build` need the SDK package layout and the platform packaging and are absent rather
than stubbed. `serve` is grouped under `content` rather than sitting at the top level, because
[08](08-asset-pipeline-and-addressables.md) already writes `vixen content build` and the two commands
are about the same directory — one noun, its verbs beneath it.

## `Samples/` and `Benchmarks/`

```
Samples/
├── 01-HelloTriangle/             # RHI only, all six platforms — the platform smoke test
├── 02-HelloUi/                   # Vixen.Ui only, no engine — proves the UI/Engine boundary
├── 03-PbrShowcase/               # materials, IBL, shadows, post FX
├── 04-EcsStressTest/             # 100k entities
├── 05-PlatformerGame/            # physics, input, animation, audio, VFX end to end
├── 06-CanvasStress/              # P2: huge scrollable 2D canvas, layers, tool overlays, floating palettes
└── 07-AddressablesRemote/        # remote catalog + delta update on mobile

Benchmarks/
├── Vixen.Benchmarks.Ecs/         # ported from Arch's suite (ADR-004)
├── Vixen.Benchmarks.Layout/      # flexbox throughput, 10⁴/10⁵ nodes
├── Vixen.Benchmarks.Reactive/    # signal propagation, alloc == 0
├── Vixen.Benchmarks.Math/
├── Vixen.Benchmarks.Jobs/        # scheduling overhead, ParallelFor vs Parallel.For
├── Vixen.Benchmarks.Serialization/
└── Vixen.Benchmarks.Rendering/   # CPU-side: culling, sorting, command recording
```

**On `Samples/06-CanvasStress` (was `06-ImageEditor`).** Per the decided audience order
([00](00-vision-and-principles.md)), **the editor is the large-scale application-platform proof**, so this
sample no longer carries that burden and is demoted from phase gate to P2. What it still uniquely
exercises is the one thing the editor does not: a multi-megapixel scrollable paint canvas with layer
compositing, tool overlays, and marching-ants selection. Those specific stress requirements are now
listed against the editor's own gates in [11](11-editor.md) where they overlap, and this sample covers
the remainder if and when it is built.

## `Testing/`

Test-only source that more than one test assembly needs. It is **linked into** the projects that use
it — `Testing/Vixen.Testing.props` carries the `Compile` items and each consumer imports it — rather
than being a project they reference.

```
Testing/
├── Vixen.Testing.props           # the Compile items, imported by each consuming .Tests project
└── Measured.cs                   # allocation measurement with the collector kept out of it
```

Linked rather than referenced because the alternative is worse in three ways. A library under
`Core/` inherits the runtime profile below — packable, AOT-compatible, trimmable, documentation
file — and all four are wrong for test-only code, so it would need a fourth profile or a name that
lies about what it is. A referenced assembly would also have to make its types `public` and would
flow xunit into every consumer transitively, where a linked file stays `internal` and adds nothing
to any test output. And the repository already links source across project boundaries where an
assembly boundary is the wrong shape — `Vixen.Input.Generators` and `Vixen.Ui.Markup.Generators`
both do it.

Top level rather than under `Core/` because every folder there is one library plus its sibling test
project, and this is neither. It is where the shared test infrastructure of
[12](12-build-ci-and-testing.md) § "Test infrastructure worth building early" — `TestApp`,
`RecordingBackend`, `GoldenFile`, `FixtureProject` — belongs when it is written.

## Shared MSBuild

**`Directory.Build.props`** (root) sets for every project:

```xml
<PropertyGroup>
  <LangVersion>latest</LangVersion>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <AnalysisLevel>latest-all</AnalysisLevel>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <EnableNETAnalyzers>true</EnableNETAnalyzers>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <Deterministic>true</Deterministic>
  <ContinuousIntegrationBuild Condition="'$(CI)'=='true'">true</ContinuousIntegrationBuild>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)generated</CompilerGeneratedFilesOutputPath>
  <InvariantGlobalization>true</InvariantGlobalization>
  <PublishRepositoryUrl>true</PublishRepositoryUrl>
  <EmbedUntrackedSources>true</EmbedUntrackedSources>
  <IncludeSymbols>true</IncludeSymbols>
  <SymbolPackageFormat>snupkg</SymbolPackageFormat>
</PropertyGroup>
```

Then, conditioned on the folder, three profiles:

| Profile | Applies to | Adds |
|---|---|---|
| **Runtime** | `Core/**`, `Platform/**` (non-test) | `IsAotCompatible=true`, `IsTrimmable=true`, `EnableTrimAnalyzer`, `EnableAotAnalyzer`, `EnableSingleFileAnalyzer`, `IsPackable=true`, PublicAPI analyzer |
| **Tooling** | `Editor/**`, `Tools/**`, `Raven/**` | reflection/LINQ allowed, `IsAotCompatible` off except `Vixen.Editor.App` |
| **Test** | `**/*.Tests` | xunit v3 + NSubstitute + Shouldly refs auto-added, `IsPackable=false`, `InternalsVisibleTo` back-reference generated |

Auto-wiring test references from `Directory.Build.targets` (rather than repeating them in 60 csproj
files) is worth the small amount of MSBuild magic; it also guarantees nobody quietly uses MSTest.

**Versioning.** Single `VersionPrefix` in `Directory.Build.props`, with build metadata from Nuke via
GitVersion-style computation. All packages version in lockstep — the engine is one product, and
independently versioned packages for a monorepo this coupled produce a support matrix nobody can
reason about.

## Monorepo migration (Phase 0, day 1)

Raven has its own git history worth keeping.

```bash
cd /Users/jiu/Projects/Vixen
git init
git commit --allow-empty -m "chore: initialise Vixen monorepo"

# bring Raven in with history under a Raven/ prefix
git remote add raven-origin ./Raven
git fetch raven-origin
git merge -s ours --no-commit --allow-unrelated-histories raven-origin/main
git read-tree --prefix=Raven/ -u raven-origin/main
git commit -m "chore: absorb Raven shader compiler into the monorepo (history preserved)"
git remote remove raven-origin
```

Then, as separate reviewable commits: delete `Raven/.git`, rename projects to the
`Vixen.Raven.*` convention, lift `Vixen.Core.Syntax` out, and add `references/` as submodules. Do
**not** squash — the Raven parser history is the most valuable existing artefact in this repo.

`.gitattributes` essentials:

```
*.meta        text eol=lf                 # typed schema ⇒ a conflict here is real; do not auto-union
*.vxasset     text eol=lf
*.vxml        text eol=lf
*.vcss        text eol=lf
*.rvn         text eol=lf
*.png  filter=lfs diff=lfs merge=lfs -text
*.ktx2 filter=lfs diff=lfs merge=lfs -text
*.fbx  filter=lfs diff=lfs merge=lfs -text
```
