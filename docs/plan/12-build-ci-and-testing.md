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
| `RestoreNativeDeps` | downloads pinned native binaries (MoltenVK, Jolt, HarfBuzz, SPIRV-Cross, shaderc, astcenc) from checksummed URLs into `artifacts/native/<rid>/`; verifies SHA-256; emits a third-party licence manifest. Fails on checksum mismatch. *Recast is no longer on this list: `Vixen.Navigation` is managed code and has no native half.* |
| `Restore` | `dotnet restore Vixen.slnx` with locked mode in CI (`--locked-mode`) so a transitive version drift breaks the build instead of silently shipping |
| `CompileShaderLibrary` | runs Raven over `Raven/Library/**/*.rvn` → `.rvnlib`; `spirv-val` on every module; fails on any diagnostic |
| `Compile` | `dotnet build` with `-warnaserror`; the generator projects build first |
| `CheckArchitecture` | walks the project reference graph and asserts the layer rules from [00](00-vision-and-principles.md) — most importantly that `Vixen.Ui` does not reference `Vixen.Engine`, and that no `Core/*` project references a `Platform/*` implementation. Also enforces **ADR-002**: fails if `Mono.Cecil`, `dnlib`, `ILRepack`, `Fody`, or any IL-rewriting `AfterCompile`/`AfterBuild` target appears in the restore graph or the evaluated MSBuild target graph of any project. And **ADR-015**: fails if an authoring-format importer reaches a runtime (non-editor, non-tooling) assembly, and if any `Silk.NET.Vulkan` type appears in `Vixen.Graphics`' public surface (ADR-001, keeping D3D12 mappable). ⚠️ **This row used to name `SixLabors.ImageSharp` as the package the rule guards.** It no longer does — nothing in the repository references ImageSharp, it has no `PackageVersion` to reference, and a rule naming it read as though it were still a dependency. `Silk.NET.Assimp` is what the rule now carries. |
| `CheckApi` | ✅ `Tools/Vixen.ApiCheck` reads the public surface of every packable assembly out of the built binary and diffs it against `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt` beside the project. Unapproved additions fail, and so do removals — a deleted `public` method compiles perfectly and breaks every consumer, and nothing else in the build would notice. `--update-api` rewrites the unshipped half; shipped API is only ever withdrawn through a `*REMOVED*` line, so a break is a line somebody wrote rather than an absence nobody looked for. Coverage is the RUNTIME profile: `Core/**` and `Platform/**`, non-test, non-generator, packable, `net10.0`. The subject is always the **Release** build, whatever `--configuration` says — a surface is a promise about a shipped package, and the two configurations disagree wherever a `public const` is `#if DEBUG`. See [Tools/Vixen.ApiCheck](../../Tools/Vixen.ApiCheck/README.md) |
| `CheckFormat` | `dotnet format style` and `dotnet format analyzers`, both `--verify-no-changes`. **Not `whitespace`**: the repository indents a lambda body passed as an argument one level further than `dotnet format` does — uniformly, in every file — and no `.editorconfig` key expresses that, so the whitespace pass reports ~900 violations against code that is entirely self-consistent. The brace and spacing rules the config *can* express are written down in `.editorconfig § Layout`, which took that number down from roughly forty thousand. The narrowing is real and reversible: the alternative is to reformat twenty-eight files against the tool that actually formats them. |
| `Test` | `dotnet test` with xunit v3, collecting coverage; enforces per-project coverage floors and the allocation gates. Passes `.runsettings`, which exists solely to set environment that has to be in place *before* the process starts (see below) |
| `GoldenImages` | ✅ renders the fixture suite on the local backend — lavapipe on the Linux leg, MoltenVK on macOS — and compares it with the committed references; writes the rendering, the reference and a diff into `artifacts/golden-diff/` on failure, which CI uploads. `--update-golden` rewrites the references. The fixtures also run under `Test`, so a wrong picture fails an ordinary build; the separate target exists for the diffs and the switch. |
| `AotSmoke` | `PublishAot` + `PublishTrimmed` of `Samples/01` and `Samples/02` per RID; **any IL2xxx/IL3xxx warning fails** |
| `Benchmark` | BenchmarkDotNet over `Benchmarks/*`; compares against a committed baseline JSON; fails on > 10 % regression or any allocation-count increase |
| `Pack` | produces every NuGet package; validates package contents against an expected-files manifest (a package that silently stops shipping its native asset is a real failure mode) |
| `PublishEditor` | per-RID single-file publish of `Vixen.Editor.App`; `.app` bundle + `.dmg` on macOS, AppImage on Linux, MSI/zip on Windows |
| `Sign` / `Notarize` | codesign + notarytool on macOS, Authenticode on Windows; secrets from CI, skipped locally |
| `Docs` | ⚠️ **Superseded by [25](25-documentation-generator-and-site.md)**: `Vixen.DocGen` over Roslyn source symbols + `docs/guide`, built into the Angular site in `www/` and shipped as an nginx image of static assets. `CheckDocs` is its gate — coverage, links and compiled examples — and sits beside `CheckApi` |
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
| `Vixen.Input`, `Vixen.Audio`, `Vixen.Physics`, `Vixen.Animation`, `Vixen.Vfx`, `Vixen.Navigation`, `Vixen.Video`, `Vixen.Xr` | one each — these are genuinely optional |
| `Vixen.Video.Codecs` | Opus for a video's audio track, so `Vixen.Video` links no codec of its own |
| `Vixen.Video.Rendering` | drawing one, so `Vixen.Video` needs no renderer and `Vixen.Rendering` no demuxer |
| `Vixen.Xr.OpenXR` | the XR backend, with no native payload: the OpenXR loader belongs to the runtime that owns the headset |
| `Vixen.Net` + `Vixen.Net.Transport.{Udp,WebSocket,Local,Relay}` | networking ([16](16-networking.md)); optional, and a project that never references it pays nothing |
| `Vixen.Ui` | the whole `Vixen.Ui.*` set except `HotReload` and `Testing` |
| `Vixen.Ui.HotReload` | dev-only; `DevelopmentDependency=true` |
| `Vixen.Ui.Testing` | ✅ the interface test harness — a chainable, frame-retrying command API over a real `UiDocument`, plus visual regression through a software rasteriser. Its own package rather than part of `Vixen.Ui`: a shipped game must not carry it, and a project that references it wants it in the test assembly alone. |
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
└── docs.yml             # Docs + CheckDocs → GHCR image on master and on tags; pr-<n> tag per PR (25)
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
- **Environment a test needs before its own process starts belongs in `.runsettings`, never in a
  shell profile.** Today that is exactly one variable — `DYLD_LIBRARY_PATH`, which macOS's dynamic
  linker reads once at launch and which is what makes the Vulkan validation layer load at all
  ([10](10-platforms.md) § macOS). Putting it in a developer's `~/.zshenv` would make "are the
  validation layers on?" depend on which terminal the suite happened to be launched from, and answer
  *no* in CI and in the IDE without saying so. The corresponding test asserts the layer is *on*
  wherever it is installed, so a machine that quietly loses validation fails rather than passes.
- **Each test project writes its own `.trx`,** named after the project (`VSTestLogger` in
  `Directory.Build.props`). Nuke passes a results *directory* and no filename: a fixed `LogFileName`
  points all eighteen projects at one path, they run concurrently, and the artefact CI publishes is
  whichever finished last. The build still fails on a red test — the exit code does not go through
  the file — but the report a human opens to find out *which* test is the entire point of producing
  one.

### Coverage of the pyramid

| Layer | What | Volume |
|---|---|---|
| **Unit** | pure logic: math, collections, serializers, parsers, cascade, layout, signals, ECS, catalogs | the bulk; fast, no I/O, no GPU |
| **Property-based** (CsCheck) | algebraic laws (matrix/quaternion), layout invariants, selector matching vs. brute force, serializer round-trip, ECS archetype transitions, undo/redo | ~30 high-value properties |
| **Snapshot / golden** | generated C# from every generator, syntax trees, SPIR-V disassembly, shader-graph → Raven output, draw lists, `.meta` round-trips, catalog output | ~200 files; `--update-golden` regenerates, and diffs are reviewed like code |
| **Conformance** | **Yoga flexbox suite** ([09](09-ui-framework.md)), UAX#14/#29 text data, CSS Grid WPT subset, `spirv-val` | several hundred cases, all externally sourced — the highest-confidence tests in the project |
| **Integration** | import→compile→bundle→load round-trips, world save/load, hot-reload scenarios, editor scenario tests | ~50 scenarios |
| **Golden image** | ✅ five fixtures — clear, triangle, indexed quad with push constants, reversed-Z depth, alpha blending — rendered headless through the render graph and compared perceptually with a per-fixture tolerance. Generated on MoltenVK and **verified against lavapipe**, so the tolerances are what cross-driver agreement actually needs rather than what one machine happens to produce. Grows towards ~40 with the rendering pipeline in Phase 4; editor layouts follow the editor. | GPU-dependent, runs everywhere a driver exists |
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
- **`Vixen.Ui.Testing`** — ✅ the interface half of `TestApp`, built ahead of it because it needs
  nothing from the engine: a real `UiDocument`, a clock the test owns, a synthetic pointer and
  keyboard, and a frame pump. Commands retry **in frames rather than in seconds**, which is what
  makes waiting deterministic and keeps the conventions above (no `Thread.Sleep`, no ambient clock)
  intact. Selectors compile through the cascade's own `SelectorCompiler`/`SelectorMatcher`, so a
  selector in a test means what it means in a stylesheet. `Screenshot(name)` is visual regression
  through a **software rasteriser over `UiGeometry`** — no device, so it runs on every CI leg, and
  the comparison is exact rather than perceptual because no driver is involved. It does not replace
  the golden-image suite: it cannot see below `UiGeometry`, which is where descriptor bindings and
  vertex layouts live. `Ticked` is the per-frame seam a real `TestApp` would drive it through.
- **`FixtureProject`** — a synthetic Vixen project generator (N textures, M models, K scenes) for asset
  pipeline scale tests.
- **Fuzzers** — ✅ **all five of the parsers this line asked for are fuzzed**: VXML, VCSS (as
  `stylevalue` and `layerrule`), Raven, the `.meta` reader and the bundle reader, among twenty targets
  in [`Core/Vixen.Fuzz`](../../Core/Vixen.Fuzz), replayed nightly over a committed corpus by
  [`nightly.yml`](../../.github/workflows/nightly.yml). **By an in-house harness rather than
  `SharpFuzz`**, and that is a decision rather than a shortfall — the reasons are worth recording
  because this line reads like an unmet commitment and is not one.

  **The oracles are the point, and they are managed-language-specific.** A native fuzzer's oracle is a
  crash. In C# you rarely get one: you get an `OutOfMemoryException` twenty minutes later, or a server
  that dies on its second day. Five things are asserted around every case — that nothing **escaped**,
  that nothing **amplified** (allocation against an allowance proportional to the input, summed over a
  window, because a list that doubles pays for a thousand appends in one), that nothing **hung**, that
  nothing was **retained** past a bound the target declares, and that nothing **ran away** while the
  case was still in flight. Amplification and retention have no AFL or libFuzzer equivalent, and they
  are what caught the attacker-declared-length allocations — a crash-finder reports every one of those
  inputs clean, because none of them crashes anything.

  **It runs on every build, not only nightly.** In-process under xunit, bounded by case count rather
  than by the clock, with no instrumentation pass and nothing orchestrated out of process: twenty
  targets and about 12.1 M cases in roughly eighteen seconds. A fuzzer that runs nightly and nowhere
  else finds a regression the morning after somebody has already built on it.

  **And for a grammar, structure-aware mutation beats coverage-guided byte mutation.** Coverage
  guidance reaches deep code by *search*; tree mutation reaches it by *construction*. `IFuzzDomain`
  parses a corpus entry and mutates a span chosen from the tree — replacing a subtree with another of
  its kind, duplicating one, deleting an optional one, grafting one in from a second entry — so what
  comes out lexes and mostly parses, and therefore reaches the binder and the backend, which is where a
  compiler's defects live. **One case in eight stays byte havoc**, so an unterminated string, a stray
  byte and a nesting depth that exhausts the parser's stack are still reached; structured generation
  that *replaces* havoc rather than joining it stops finding those the day it lands.

  `SharpFuzz` is still worth having **later, for `raven` only**, and conditional on the tree-mutation
  target plateauing — an if-the-data-says-so decision, not a now decision. Each target is already
  `(ReadOnlySpan<byte>) -> outcome`, so the wrapper is short. Its out-of-process execution would also
  buy the one runaway nothing in-process can report: a stack overflow ends the CLR where it happens,
  with no thread left to name the input.

  **Three findings are open and filed rather than fixed**, recorded here so the ✅ above is not read as
  "and nothing is owed":

  - the YAML binder writes `null` into a member declared non-nullable — nullability is decided from the
    CLR type, so the C# annotation contradicting it is not in the descriptor to read. Refusing it is a
    decision about every `[DataContract]` type in the engine and belongs to `Vixen.Core.Yaml`;
  - three inputs make Raven's incremental reparse build a **structurally different tree** — the printed
    text still agrees, so only the shape comparison sees it.

  **The third is fixed.** A binder recursion on `func F(): float[F()]` overflowed the stack — not a
  property of `shader` as first recorded, but of any type with members: a `struct` did it too, and so
  did a parameter type, two signatures sizing arrays by each other, and a `val` parameter sizing its
  own type. Three of the four source symbols that resolve a type already carried the cycle guard;
  `SourceMethodSymbol` had it around the inferred branch only, and the two parameter symbols had none.
  Guarding the whole resolution in each, keyed by the symbol, reports `RVN2005` and closes the family.

  The input is `Corpus/raven/70ae34e20b4880ee.bin` now — it had been kept out deliberately, because one
  that overflows the stack takes the test host down on every build, and the rule in that harness is
  that promotion follows the fix. With it fixed, `raven` is back in the nightly and `VIXEN_FUZZ_SKIP`
  is empty; the cap moved from 240 to 255 for the twentieth target.

### Optional external tools

Some checks are worth more than they are worth *blocking* on, so they run when the tool is present
and report their absence through the test output rather than failing or silently passing:

| Tool | Install | What it unlocks |
|---|---|---|
| `spirv-val`, `spirv-dis` | `brew install spirv-tools` | validation of every emitted SPIR-V module, and the disassembly the differential oracle reads |
| `glslc` (shaderc) | `brew install shaderc` | compiles Raven's GLSL back to SPIR-V for the differential oracle ([07 § C](07-raven-shader-pipeline.md)) |

The command-line tools rather than their NuGet bindings, deliberately: an oracle is a test-time
thing, and a native package would put shaderc's binaries in the restore graph of projects that must
never ship them.

**`ci.yml` must install both**, so these are optional locally and mandatory on a PR — a green local
run with the tools missing is a weaker signal than a green CI run, and the test output says which one
you got. That is a requirement on the workflow when it is written; nothing enforces it today.

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
