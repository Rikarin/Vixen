# 12 — Build, CI and Testing

## Nuke

`build/_build.csproj` referencing `Nuke.Common` 10.1.0. Nuke is the *only* sanctioned way to build,
test, package, or release — CI calls the same targets a developer calls, so "works on my machine" and
"works in CI" cannot diverge.

### Target graph

```
Clean ──► Restore ──► Compile ──┬─► Test ─────────────┬─► Pack ──► Publish
                                │                     │
        RestoreNativeDeps ──────┤                     ├─► PublishEditor ──► Sign ──► Notarize
                                │                     │
        CompileShaderLibrary ───┤                     ├─► PublishAndroid / PublishIos / PublishWeb
                                │                     │
        GenerateApiBaseline ────┤                     ├─► Benchmark
                                │                     │
        CheckArchitecture ──────┤                     ├─► GoldenImages
        CheckApi ───────────────┤                     ├─► AotSmoke
        CheckFormat ────────────┘                     └─► Coverage
                                                          │
                                                     Docs ─┴─► Release (tag-triggered)
```

### Targets in detail

| Target | Does |
|---|---|
| `Clean` | wipes `artifacts/`, `**/bin`, `**/obj`, `Library/` in samples |
| `RestoreNativeDeps` | downloads pinned native binaries (MoltenVK, Jolt, HarfBuzz, SPIRV-Cross, shaderc, astcenc, Recast) from checksummed URLs into `artifacts/native/<rid>/`; verifies SHA-256; emits a third-party licence manifest. Fails on checksum mismatch. |
| `Restore` | `dotnet restore Vixen.slnx` with locked mode in CI (`--locked-mode`) so a transitive version drift breaks the build instead of silently shipping |
| `CompileShaderLibrary` | runs Raven over `Raven/Library/**/*.rvn` → `.rvnlib`; `spirv-val` on every module; fails on any diagnostic |
| `Compile` | `dotnet build` with `-warnaserror`; the generator projects build first |
| `CheckArchitecture` | walks the project reference graph and asserts the layer rules from [00](00-vision-and-principles.md) — most importantly that `Vixen.Ui` does not reference `Vixen.Engine`, and that no `Core/*` project references a `Platform/*` implementation. Also enforces **ADR-002**: fails if `Mono.Cecil`, `dnlib`, `ILRepack`, `Fody`, or any IL-rewriting `AfterCompile`/`AfterBuild` target appears in the restore graph or the evaluated MSBuild target graph of any project. And **ADR-015**: fails if `SixLabors.ImageSharp` reaches any runtime (non-editor, non-tooling) assembly, and if any `Silk.NET.Vulkan` type appears in `Vixen.Graphics`' public surface (ADR-001, keeping D3D12 mappable). |
| `CheckApi` | `Tools/Vixen.ApiCheck` diffs the public surface against `PublicAPI.Shipped.txt`; unapproved additions fail |
| `CheckFormat` | `dotnet format --verify-no-changes` |
| `Test` | `dotnet test` with xunit v3, collecting coverage; enforces per-project coverage floors and the allocation gates |
| `GoldenImages` | runs the rendering fixture suite on lavapipe (Linux) or the local backend; writes diffs into `artifacts/golden-diff/` and uploads them as CI artefacts on failure |
| `AotSmoke` | `PublishAot` + `PublishTrimmed` of `Samples/01` and `Samples/02` per RID; **any IL2xxx/IL3xxx warning fails** |
| `Benchmark` | BenchmarkDotNet over `Benchmarks/*`; compares against a committed baseline JSON; fails on > 10 % regression or any allocation-count increase |
| `Pack` | produces every NuGet package; validates package contents against an expected-files manifest (a package that silently stops shipping its native asset is a real failure mode) |
| `PublishEditor` | per-RID single-file publish of `Vixen.Editor.App`; `.app` bundle + `.dmg` on macOS, AppImage on Linux, MSI/zip on Windows |
| `Sign` / `Notarize` | codesign + notarytool on macOS, Authenticode on Windows; secrets from CI, skipped locally |
| `Docs` | DocFX over XML doc comments + `docs/manual`; publishes to GitHub Pages |
| `Release` | on tag: everything above, plus GitHub Release creation with changelog from conventional commits, artefact upload, and `dotnet nuget push` |

Nuke parameters: `--configuration`, `--platform`, `--rid`, `--skip-native`, `--update-golden`,
`--update-api`, `--filter <test-trait>`.

## NuGet package layout

One version for all packages (rationale in [02](02-repository-layout.md)).

| Package | Contains |
|---|---|
| `Vixen.Core` | the whole `Vixen.Core.*` set (they are never used apart, and 14 packages for one layer is user-hostile) |
| `Vixen.Ecs` | + generators |
| `Vixen.Graphics` | RHI |
| `Vixen.Graphics.Vulkan` / `.OpenGL` / `.Null` | one each, with `runtimes/<rid>/native/` payloads |
| `Vixen.Graphics.Direct3D12` / `.WebGPU` | **package identity reserved, stub implementations at 1.0** (Q4 / cut list). Published so the ID and RID mapping are settled and the real backends land additively. |
| `Vixen.Shaders` | effect system + generators + the compiled `Raven/Library` `.rvnlib` artefacts |
| `Vixen.Rendering` | + `Vixen.Rendering.PostFx` |
| `Vixen.Assets` | runtime content |
| `Vixen.Engine` | scenes, behaviours, game loop |
| `Vixen.Input`, `Vixen.Audio`, `Vixen.Physics`, `Vixen.Animation`, `Vixen.Vfx`, `Vixen.Navigation`, `Vixen.Video` | one each — these are genuinely optional |
| `Vixen.Net` + `Vixen.Net.Transport.{Udp,WebSocket,Local,Relay}` | networking ([16](16-networking.md)); optional, and a project that never references it pays nothing |
| `Vixen.Ui` | the whole `Vixen.Ui.*` set except `HotReload` |
| `Vixen.Ui.HotReload` | dev-only; `DevelopmentDependency=true` |
| `Vixen.Platform.Desktop` / `.Android` / `.iOS` / `.Web` | platform heads |
| `Vixen.Raven` | the compiler as a library (useful standalone) |
| `Vixen.Raven.Cli` | `dotnet tool` |
| `Vixen.Sdk` | MSBuild SDK — the props/targets that make `dotnet build` do content builds |
| `Vixen.App` | meta-package: the sensible default reference set |
| `Vixen.Cli` | `dotnet tool` |
| `Vixen.Templates` | `dotnet new` templates |
| `Vixen.Editor.Plugin` | plugin authoring API |

Every package: SourceLink, symbols (`.snupkg`), deterministic, README, icon, and `PackageValidation`
against the previous release for baseline compatibility. Plus, per **ADR-015**:
`PackageLicenseExpression=Apache-2.0`, the `NOTICE` file, and the generated third-party attribution
manifest. `Pack` fails if any of the three is missing from a package — Apache-2.0 §4 obligations are not
something to satisfy by memory.

## GitHub Actions

```
.github/workflows/
├── ci.yml               # PR + push to main: the matrix below
├── nightly.yml          # physical-device suites, long benchmarks, fuzzing, full golden sweep
├── release.yml          # tag-triggered: Release target, signed artefacts, NuGet push
└── docs.yml             # DocFX → Pages on main
```

`ci.yml` jobs (per [10](10-platforms.md)'s matrix), all required for merge:

| Job | Runner | Target |
|---|---|---|
| `build-test-windows` | `windows-latest` | `Test` |
| `build-test-linux` | `ubuntu-latest` | `Test` |
| `build-test-macos` | `macos-14` | `Test` |
| `checks` | `ubuntu-latest` | `CheckArchitecture CheckApi CheckFormat` |
| `graphics` | `ubuntu-latest` (+ Mesa/lavapipe) | `GoldenImages` |
| `aot-trim` | `ubuntu-latest`, `macos-14` | `AotSmoke` |
| `android` | `ubuntu-latest` | `PublishAndroid` + emulator smoke |
| `ios` | `macos-14` | `PublishIos` + simulator smoke |
| `web` | `ubuntu-latest` | `PublishWeb` + Playwright smoke |
| `benchmark` | `ubuntu-latest` | `Benchmark` (allocation gates only on PR; timing gates nightly, since shared runners are too noisy for timing) |
| `content` | `ubuntu-latest`, `windows-latest`, `macos-14` | full content build of `Samples/05`; asserts identical `ObjectId`s across all three — the determinism gate |

Build caching: NuGet packages, the native-deps directory (keyed on the checksum manifest), the shader
bytecode cache, and the asset artefact DB (keyed on the source tree hash). Content builds are the
slowest step and cache well.

## Testing strategy

Stack: **xunit v3 (3.2.2) + NSubstitute (6.0.0) + Shouldly (4.3.0)** everywhere, as specified. Added:
BenchmarkDotNet for perf, CsCheck for property-based tests, Verify-style snapshot assertions for golden
files.

### Conventions

- One test project per production project, sibling-located (ADR-014), auto-referenced by
  `Directory.Build.targets`.
- Naming: `MethodOrScenario_Condition_ExpectedResult`. Arrange/Act/Assert with blank-line separation.
- `Shouldly` for all assertions (`result.ShouldBe(expected)`) — its failure messages include the
  expression source, which is worth the dependency on its own.
- `NSubstitute` only for genuine boundaries (platform interfaces, file providers, network). **Never mock
  a type you own and can construct** — a mocked `Signal<T>` or mocked `LayoutStore` tests the mock.
- Traits for filtering: `[Trait("Category","Unit|Integration|Golden|Perf|Platform")]`.
- Deterministic: no `DateTime.Now`, no unseeded random, no `Thread.Sleep`, no real network, no ambient
  filesystem (an in-memory `IFileProvider` is the default).
- Every test project runs green with `VIXEN_JOB_WORKERS=0` (single-threaded) as a separate CI leg.

### Coverage of the pyramid

| Layer | What | Volume |
|---|---|---|
| **Unit** | pure logic: math, collections, serializers, parsers, cascade, layout, signals, ECS, catalogs | the bulk; fast, no I/O, no GPU |
| **Property-based** (CsCheck) | algebraic laws (matrix/quaternion), layout invariants, selector matching vs. brute force, serializer round-trip, ECS archetype transitions, undo/redo | ~30 high-value properties |
| **Snapshot / golden** | generated C# from every generator, syntax trees, SPIR-V disassembly, shader-graph → Raven output, draw lists, `.meta` round-trips, catalog output | ~200 files; `--update-golden` regenerates, and diffs are reviewed like code |
| **Conformance** | **Yoga flexbox suite** ([09](09-ui-framework.md)), UAX#14/#29 text data, CSS Grid WPT subset, `spirv-val` | several hundred cases, all externally sourced — the highest-confidence tests in the project |
| **Integration** | import→compile→bundle→load round-trips, world save/load, hot-reload scenarios, editor scenario tests | ~50 scenarios |
| **Golden image** | ~40 rendering fixtures + editor layouts, on lavapipe, perceptual diff | GPU-dependent, Linux CI |
| **Platform smoke** | boot, clear, triangle, UI, input on each of six targets | 6 × a handful |
| **Performance** | BenchmarkDotNet with committed baselines and allocation gates | ~40 benchmarks |

### The gates that enforce [00](00-vision-and-principles.md)

These are ordinary tests, which is the point — the non-negotiables are executable.

```csharp
[Fact, Trait("Category","Perf")]
public void EmptyScene_TenThousandFrames_AllocatesNothing()
{
    using var app = TestApp.Create(GraphicsBackend.Null);
    app.RunFrames(100);                                  // warm up: JIT, caches, pools
    var before = GC.CollectionCount(0);
    var bytes  = GC.GetAllocatedBytesForCurrentThread();

    app.RunFrames(10_000);

    GC.CollectionCount(0).ShouldBe(before);
    (GC.GetAllocatedBytesForCurrentThread() - bytes).ShouldBe(0);
}
```

Equivalents exist for: a 10 k-entity scene, a steady-state UI frame, a signal-update storm, a layout
pass over an unchanged tree, and an asset-load/release cycle. When one of these fails, it names the
exact allocation via a `GCHeapAllocationEventSource` listener in the failure message — otherwise
"something allocated 48 bytes" is an unactionable red build.

### Test infrastructure worth building early

- **`TestApp`** — an in-process engine host with the Null backend, an in-memory VFS, a fake clock, and a
  synthetic input source. Makes almost every "integration" test a fast unit test. Build this in Phase 1;
  every later phase depends on it.
- **`RecordingBackend`** — the Null backend's structured command log with a fluent assertion API
  (`log.ShouldContainDrawIndexed(count: 36).AfterBinding(pipeline: "Opaque"))`.
- **`GoldenFile`** — the snapshot helper: reads/writes under `__golden__/`, honours `--update-golden`,
  produces a readable unified diff on mismatch.
- **`FixtureProject`** — a synthetic Vixen project generator (N textures, M models, K scenes) for asset
  pipeline scale tests.
- **Fuzzers** — `SharpFuzz` over the VXML parser, the VCSS parser, the Raven parser, the `.meta` reader,
  and the bundle reader. Parsers and binary readers are exactly where fuzzing pays, and all five parse
  untrusted-ish input. Run nightly with a persistent corpus.

### What is explicitly *not* tested

Stated so nobody pretends otherwise: real-GPU-specific driver behaviour (covered by manual pre-release
passes on the IHV matrix), audio output correctness (buffer-level tests only), physics numerical
agreement with Jolt upstream (Jolt's own tests cover that; Vixen tests its integration), and
end-to-end store submission.

## Developer workflow

```bash
# one-time
dotnet tool restore

# everyday
nuke Compile
nuke Test --filter Category=Unit
nuke Test --filter Category=Golden --update-golden     # then review the diff
nuke Benchmark --filter *Layout*

# run things
dotnet run --project Samples/03-PbrShowcase
dotnet run --project Editor/Vixen.Editor.App

# hot-reload loop
dotnet watch --project Editor/Vixen.Editor.App
```

Pre-commit hook (opt-in, installed by `nuke SetupDev`): `CheckFormat` + affected unit tests. Keep it
under 10 seconds or developers will disable it.
