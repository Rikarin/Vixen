# 12 — Build, CI and Testing

## Nuke

`build/_build.csproj` referencing `Nuke.Common` 10.1.0. Nuke is the *only* sanctioned way to build,
test, package, or release — CI calls the same targets a developer calls, so "works on my machine" and
"works in CI" cannot diverge.

### Target graph

The thirty-eight targets, by what they depend on. Only the `DependsOn` edges are drawn; a target with
no edge into it is reachable on its own, which most of the checks deliberately are.

```
Restore ──┬─► Compile ────────┬─► Test ──┬─► Pack ──► CheckTemplates
          │                   │          └─► PublishEditor
          │                   ├─► Coverage
          │                   ├─► GoldenImages
          │                   ├─► ContentBytes
          │                   ├─► RemeshBytes
          │                   └─► SampleFrame
          │
          ├─► CompileRelease ─┬─► CheckApi
          │                   ├─► Docs
          │                   ├─► CheckDocs
          │                   └─► Release            (tag-triggered)
          │
          ├─► CheckFormat        RestoreNativeDeps ──► CheckAotIos
          ├─► CheckShaders
          └─► CheckAot           CompileWeb ──► PublishWeb ──► BrowserSmoke

Depending on nothing, and run alone:  Clean · Benchmark · CheckArchitecture · CheckAttribution ·
CheckBenchmarks · CheckDocComments · CheckDocsCoverage · CheckPackages · CheckStrings ·
CheckWhitespace · CompileMobile ·
CompileWeb · AffectedProjects · AffectedTests · TestOrder · PruneWorktrees
```

⚠️ **And four targets existed that this ledger did not name, which is #340's defect the other way
round** — `Coverage`, `CheckDocsCoverage`, `TestOrder` and `PruneWorktrees`, the last two added on
2026-09-05. A target no document names is one nobody types, and `PruneWorktrees` is the one that
matters for that: it is the only thing in the repository that reclaims the disk agent worktrees take
([#561](https://github.com/Rikarin/Vixen/issues/561)), and it deletes checkouts, so it will never be
put on the graph or in CI. It has to be findable instead.

⚠️ **`CheckApi`, `Docs`, `CheckDocs` and `Release` hang off `CompileRelease` and not off `Compile`.**
A public surface and a generated doc site are promises about a *shipped* package, and the two
configurations disagree wherever a `public const` is `#if DEBUG` — so those four build Release whatever
`--configuration` says. A conclusion drawn from a manual `-c Release` run of `Test`, which compiles
Debug, is not a conclusion about them.

⚠️ **Eight names this graph and the table below used to carry do not exist**
([#340](https://github.com/Rikarin/Vixen/issues/340)). Four were renamed and four were never built:

| Named | Actually |
|---|---|
| `CompileShaderLibrary` | `CheckShaders` — recompiles the shaders whose `.spv` is committed, from their import closure, and reports drift |
| `GenerateApiBaseline` | `--update-api` on `CheckApi` |
| `AotSmoke` | `CheckAot` and `CheckAotIos` |
| `Coverage` | ✅ reports line coverage of each test project's own subject assembly and gates on nothing but its own instrument; not in CI. ⚠️ This row used to add "not on the graph", and it is: `Coverage` `DependsOn(Compile)` (`build/Build.Coverage.cs:67`). § Coverage below |
| `Sign`, `Notarize` | — |
| `PublishAndroid`, `PublishIos` | — `CompileMobile` builds the assemblies; nothing publishes |

And fourteen existed that this document mentioned nowhere, which now have rows below:
`AffectedProjects`, `AffectedTests`,
`BrowserSmoke`, `CheckAttribution`, `CheckBenchmarks`, `CheckDocComments`, `CheckPackages`,
`CheckStrings`,
`CheckWhitespace`, `CompileMobile`, `CompileRelease`, `CompileWeb`, `ContentBytes`, `RemeshBytes`,
`SampleFrame`. ⚠️ Several have real teeth — `CheckStrings` is what caught the untranslatable Undo menu
item, and `CheckAttribution` is ADR-015's enforcement.

### Targets in detail

| Target | Does |
|---|---|
| `Clean` | wipes `artifacts/`, `**/bin`, `**/obj`, `Library/` in samples |
| `RestoreNativeDeps` | downloads pinned native binaries (MoltenVK, Jolt, HarfBuzz, SPIRV-Cross, shaderc, astcenc) from checksummed URLs into `artifacts/native/<rid>/`; verifies SHA-256; emits a third-party licence manifest. Fails on checksum mismatch. *Recast is no longer on this list: `Vixen.Navigation` is managed code and has no native half.* |
| `Restore` | `dotnet restore Vixen.slnx` with locked mode in CI (`--locked-mode`) so a transitive version drift breaks the build instead of silently shipping |
| `CheckShaders` | recompiles the nine modules whose `.spv` is **committed** — five permutations of the three `Raven/Library/Terrain` shaders, each from its own import closure, plus the four `.rvn` beside `Vixen.Editor.Host` and `Vixen.Ui.Desktop` — and fails if any committed binary drifted. ⚠️ **Not the whole library, and this row used to be called `CompileShaderLibrary` and to claim it was**: the library has over a hundred shaders and `LibraryReflectionTests` is what binds it whole. ⚠️ It also sweeps the other way, over every committed `.spv` that *nothing* in those two lists produces — because everything else here walks the lists, so a module dropped from one was never opened and the target reported green over a binary the editor still loads. `--update-shaders` rewrites the committed bytecode |
| `Compile` / `CompileRelease` | `dotnet build` with `-warnaserror`; the generator projects build first. `CompileRelease` is the same build pinned to Release whatever `--configuration` says, and is what `CheckApi`, `Docs`, `CheckDocs` and `Release` depend on |
| `CompileMobile` / `CompileWeb` / `PublishWeb` / `BrowserSmoke` | the three heads no solution build reaches. `CompileMobile` builds the Android and iOS assemblies (nothing publishes them — [#327](https://github.com/Rikarin/Vixen/issues/327)); `CompileWeb` then `PublishWeb` produce a loadable browser head, and `BrowserSmoke` drives it over CDP — 37 checks, no Playwright and no npm |
| `ContentBytes` / `RemeshBytes` | build one fixture and emit a manifest of `ObjectId`s. ⚠️ **Neither is the determinism gate on its own** — a single machine agreeing with itself proves nothing. The gate is the CI job that downloads all three legs' manifests and compares them, and it counts the manifests first, because a comparison of nothing agrees with itself |
| `SampleFrame` | renders `--frame-sample` for `--frame-count` frames on the local backend and asserts the counters. The one target whose cost is a shader-variant compile rather than the frames |
| `AffectedProjects` / `AffectedTests` | `--since <ref>` narrows to the projects owning the diff, and to the test projects reachable from them. ⚠️ **Inner-loop conveniences and never the gate**: a `ProjectReference` closure cannot see a golden image, a content bundle, an `.rvn` import closure, or a test that walks the repository |
| `CheckArchitecture` | walks the project reference graph and asserts the layer rules from [00](00-vision-and-principles.md) — most importantly that `Vixen.Ui` does not reference `Vixen.Engine`, and that no `Core/*` project references a `Platform/*` implementation. Also enforces **ADR-002**: fails if `Mono.Cecil`, `dnlib`, `ILRepack`, `Fody`, or any IL-rewriting `AfterCompile`/`AfterBuild` target appears in the restore graph or the evaluated MSBuild target graph of any project. And **ADR-015**: fails if an authoring-format importer reaches a runtime (non-editor, non-tooling) assembly, and if any `Silk.NET.Vulkan` type appears in `Vixen.Graphics`' public surface (ADR-001, keeping D3D12 mappable). ⚠️ **This row used to name `SixLabors.ImageSharp` as the package the rule guards.** It no longer does — nothing in the repository references ImageSharp, it has no `PackageVersion` to reference, and a rule naming it read as though it were still a dependency. `Silk.NET.Assimp` is what the rule now carries. |
| `CheckApi` | ✅ `Tools/Vixen.ApiCheck` reads the public surface of every packable assembly out of the built binary and diffs it against `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt` beside the project. Unapproved additions fail, and so do removals — a deleted `public` method compiles perfectly and breaks every consumer, and nothing else in the build would notice. `--update-api` rewrites the unshipped half; shipped API is only ever withdrawn through a `*REMOVED*` line, so a break is a line somebody wrote rather than an absence nobody looked for. Coverage is the RUNTIME profile: `Core/**` and `Platform/**`, non-test, non-generator, packable, `net10.0`. The subject is always the **Release** build, whatever `--configuration` says — a surface is a promise about a shipped package, and the two configurations disagree wherever a `public const` is `#if DEBUG`. See [Tools/Vixen.ApiCheck](../../Tools/Vixen.ApiCheck/README.md) |
| `CheckFormat` | five passes, cheapest first: the SPDX licence header on every file, `CheckAttributionManifest`, `CheckWhitespaceFormatting`, `CheckDocCommentPlacement`, and then `dotnet format style` and `dotnet format analyzers` with `--verify-no-changes`. ⚠️ **This row used to say the whitespace pass was refused outright.** It is not: the lambda-indentation argument that refused it covers 551 files out of 4 842, and using it to skip the other 4 291 left mis-indentation ungated everywhere. `docs/WhitespaceExempt.txt` carries the exceptions and may only shrink, so a file that becomes clean fails until its line is removed |
| `CheckWhitespace` / `CheckAttribution` / `CheckStrings` / `CheckDocComments` | the four of those passes that are also targets of their own, so each can be run — and watched failing — without the two minute-long `dotnet format` passes. `CheckStrings` fails on a declared string id used nowhere and on a call site that rebuilds an id a declaration class already declares; it is what caught the untranslatable Undo menu item. `CheckAttribution` is ADR-015's enforcement over `docs/manual/third-party.md`. `--update-exemptions` rewrites the whitespace list, and the diff is worth reading: a commit that grows it added mis-indented code |
| `Test` | xunit v3 over every test project, and the allocation gates run inside it as ordinary tests. ⚠️ **This row used to say it passes `.runsettings`**; it does not, because the assemblies run through Microsoft.Testing.Platform, which does not read one ([#560](https://github.com/Rikarin/Vixen/issues/560)). The one variable that file carried is exported onto the build process instead (see below). ⚠️ **Collects no coverage and enforces no floor, and this row used to say it did both** — the collector lives in the separate `Coverage` target, which reports and does not gate; § Coverage below says why the floor is refused rather than owed |
| `TestOrder` | prints the order `Test` starts the test assemblies in, which is longest first out of `build/test-cost.txt`; `--update-test-cost` rewrites that list from the last run's TRX `Times`. ⚠️ **What used to decide the order was `Vixen.slnx`**, and a solution build of a custom target walks each project's dependencies first — so the assembly referencing the most of the tree was dispatched last, and that assembly is for the same reason the slowest one. `Test` hands MSBuild a generated flat traversal project instead. Measured on the 2026-09-05 runs either side of the change: elapsed 873.3 s → **677.5 s**, with `Vixen.Editor.App.Tests` moving from a 218.4 s start to 0.2 s ([#592](https://github.com/Rikarin/Vixen/issues/592), then [#557](https://github.com/Rikarin/Vixen/issues/557)). ⚠️ **This row used to end "the run is now its longest single assembly and cannot be shortened by scheduling at all", and that expired when #557 halved that assembly** — 655.0 s → **329.5 s** against a 498.3 s run in the 2026-09-05 23:04 TRX, so 169 s of the run happens after it finishes and greedy LPT bottoms out at six workers rather than at four. The cost list itself was the last thing holding the old conclusion up: it still said 655.0, and a schedule input wrong by 2× is wrong in the direction that makes the run look unfixable. ⚠️ **A stale number now fails `Test` rather than merely packing the run worse** ([#863](https://github.com/Rikarin/Vixen/issues/863)): the same pass that counts the TRX afterwards compares each measured wall with the committed cost and fails on a gap over **both** 60 s and 1.5× — both, because the small assemblies are host start-up and noisy in ratio while irrelevant in seconds, and 1.5 rather than 2 because the drift it was written for was 1.988×. It fails only when the two are the same measurement: `--update-test-cost` stamps the configuration into the file's header, so CI's Release run on other hardware reports the gap and declines to fail on it |
| `Coverage` | reports line coverage of each test project against its own subject assembly and gates on nothing but its own instrument; not in CI. `--coverage-project <substring>` narrows it. § Coverage below says why the floor is refused rather than owed |
| `CheckDocsCoverage` | the half of `CheckDocs` that builds nothing: fails when a type in a `PublicAPI` baseline has no guide page and no `docs/DocsExempt.txt` line. Reachable alone precisely because `CheckDocs` costs a Release build of the solution first |
| `PruneWorktrees` | reports the agent worktrees under `.claude/worktrees` that are **merged into master, clean, unlocked and unwritten for `--idle-minutes`** (30), and with `--remove-merged` removes those and only those. ⚠️ **The fourth condition is the only one about the worker rather than the work** ([#770](https://github.com/Rikarin/Vixen/issues/770)): the lock is what "somebody is still using this" is read off, and two of thirteen live agent worktrees carried none on 2026-09-05 — while merged-and-clean is precisely the state a live agent is in between the orchestrator merging its branch and its own process ending. The signal is the newest write anywhere under the worktree, free because the size report already walks every file; the pid in the lock reason is deliberately not consulted, since a recycled pid is a worse oracle than a missing lock and there is no lock to read one out of in the case this is about. ⚠️ The ordering hazard is that merge-then-prune now reports those worktrees as `keep` for up to the window — which costs a sweep and not the disk, since #561's worktrees had been held for days — and `--idle-minutes 0` restores the three-condition behaviour exactly. ⚠️ **Nothing else in the repository reclaims that disk**: on 2026-09-04 it was 105 GB of a 132 GB tree, ~25 GB per worktree, three of them merged and clean for days. ⚠️ It enumerates *directory entries* and not `git worktree list`, because one 3.8 GB directory in there was not a registered worktree at all — and git run from inside such a directory answers about the parent repository, so it reported itself clean and on master while being neither. Those are warned about and never removed. Removal is `git worktree remove`, so git's own dirtiness refusal stays behind the filter rather than being replaced by it. Not on the graph and never in CI: it deletes checkouts, so it is a target somebody types on purpose ([#561](https://github.com/Rikarin/Vixen/issues/561)). ⚠️ **The fourth condition had no instrument of its own until 2026-09-06**: the walk answers a null newest write both for a directory it read and found no files in *and* for one whose walk threw, and the idle check read both as "nothing has touched this" — so on the day that safety condition could not run, its verdict was *removable*, which is the shape this repository keeps meeting. The walk now reports whether it completed and an unreadable worktree is kept. ⚠️ **The removable verdict is unreachable on a real machine** — a removable worktree is one nobody has — so four audits could only see that branch by sabotaging a predicate. The four conditions are now a pure function in `build/WorktreeSafety.cs`, linked into `Vixen.ApiCheck.Tests`, and the positive case, each refusal on its own, the reversed-recency direction and the empty-HEAD short circuit are ordinary tests |
| `GoldenImages` | ✅ renders the fixture suite on the local backend — lavapipe on the Linux leg, MoltenVK on macOS — and compares it with the committed references; writes the rendering, the reference and a diff into `artifacts/golden-diff/` on failure, which CI uploads. `--update-golden` rewrites the references. The fixtures also run under `Test`, so a wrong picture fails an ordinary build; the separate target exists for the diffs and the switch. |
| `CheckAot` / `CheckAotIos` | `PublishAot` + `PublishTrimmed` of a probe that roots the runtime assemblies; **any IL2xxx/IL3xxx warning fails**. ⚠️ **Two targets and not one `AotSmoke`**, because iOS is the NativeAOT-only platform and `CheckAotIos` `.Requires` macOS — a single target would have been silently half a gate on every other runner. ⚠️ The probe roots 29 of 95 runtime assemblies rather than all of them ([#506](https://github.com/Rikarin/Vixen/issues/506)) |
| `Benchmark` | BenchmarkDotNet over `Benchmarks/*`, then judged against `Benchmarks/baseline.json`: **any** allocation growth fails, a mean more than 10 % above the baseline fails only under `--gate-timing`, and a benchmark that is in the baseline and did not run fails too. ⚠️ **There is no committed baseline yet, and the target therefore fails rather than passing** — a comparison with nothing to compare against is the shape of a gate that did not run, and this row described one for as long as it had described anything. `--update-baseline` writes the file, stamped with the machine, the runtime, the BenchmarkDotNet version and the commit out of BenchmarkDotNet's own `HostEnvironmentInfo`; `--report-only` asks for numbers without a verdict. The comparison alone, over whatever is already in `artifacts/benchmarks`, is `CheckBenchmarks` |
| `Pack` | produces every NuGet package, then opens every one of them. Four checks, each written from a failure that had already happened or from an obligation that cannot be satisfied by memory: `CheckApacheObligations` asserts the Apache-2.0 licence expression in each manifest and a non-empty `NOTICE` at its root (ADR-015); `CheckPackedToolsAreComplete` is the **expected-files manifest** this row asks for — ⚠️ **the manifest is the tool's own `.deps.json`**, so every assembly in the closure and every `runtimes/<rid>/native/` payload it names has to be in the package, and nothing is hand-maintained (`build/PackageContents.cs`); `CheckStyleGenIsShippable` names the five files `Vixen.Ui.Styling.Utilities` must carry for its `tools/` to start; `CheckCliIsShippable` extracts `Vixen.Sdk` and **runs** the CLI out of it. All four are reachable alone as `CheckPackages`, over whatever is already in `artifacts/packages` — an instrument nobody can run alone is one nobody has watched fail. Still owed: `PackageValidation` ([#337](https://github.com/Rikarin/Vixen/issues/337)) |
| `CheckTemplates` | ✅ scaffolds every `dotnet new` template from the feed `Pack` just wrote, into a directory outside the repository, with an **empty package cache**, and builds each one. On the `pack` leg, in the same invocation as `Pack` — a second invocation would clean the feed it consumes. ⚠️ **The assertion that carries this target is the negative control, not the six builds**: a scaffolded project restores perfectly well outside the repository on any machine that has ever run `Pack`, because ~57 `Vixen.*` packages are sitting in the global NuGet cache, so "it restored" is a statement about the cache. The target therefore first requires a restore to **fail** with the feed unwired, and refuses to continue if it succeeds. Source mapping pins `Vixen.*` to the local feed so a package this build failed to produce cannot be supplied by a published one. Still owed on [#114](https://github.com/Rikarin/Vixen/issues/114): the Android, iOS and Web *platform* heads, which need workloads no desktop leg has |
| `PublishEditor` | per-RID single-file publish of `Vixen.Editor.App`; `.app` bundle + `.dmg` on macOS, AppImage on Linux, MSI/zip on Windows |
| ~~`Sign` / `Notarize`~~ | ⚠️ **Not built.** codesign + notarytool on macOS and Authenticode on Windows are still what a signed editor build needs; nothing in `build/` does either, and `PublishEditor` produces an unsigned bundle |
| `Docs` | ⚠️ **Superseded by [25](25-documentation-generator-and-site.md)**: `Vixen.DocGen` over Roslyn source symbols + `docs/guide`, built into the Angular site in `www/` and shipped as an nginx image of static assets. `CheckDocs` is its gate — coverage, links and compiled examples — and sits beside `CheckApi` |
| `Release` | on tag: everything above, plus GitHub Release creation with changelog from conventional commits, artefact upload, and `dotnet nuget push` |

Nuke parameters, all of them: `--configuration`, `--workers <n>`, `--since <ref>`; the four that
rewrite something a gate then checks — `--update-api`, `--update-golden`, `--update-shaders`,
`--update-baseline`, `--update-exemptions`, `--update-test-cost`; and the per-target ones —
`--coverage-project`, `--remove-merged`, `--benchmark-filter`, `--short`,
`--gate-timing`, `--report-only`, `--verify-docs`, `--all-native-deps`, `--stage-vulkan`,
`--publish-smoke`, `--frame-sample`, `--frame-count`, `--browser-smoke-checks`,
`--browser-smoke-timeout`, `--release-version`, `--release-date`.

⚠️ **`--platform`, `--rid`, `--skip-native` and `--filter <test-trait>` are not among them and this
line used to name all four** ([#340](https://github.com/Rikarin/Vixen/issues/340)). There is no
per-RID switch because the targets that need one derive it, and no test-trait filter because a
narrowed run is `AffectedTests --since` or a direct `dotnet test --filter`.

### The benchmark baseline

Two numbers, judged differently, because only one of them is a property of the code.

**An allocation count is machine-independent**, so it fails a pull request on a shared runner and it
fails on *any* increase rather than on a percentage. That is the same reasoning as the allocation
gates below asserting through `Measured` **in a test rather than in a report** — it fails the build
instead of producing a document nobody opens.

**A mean is not.** This repository has attributed a ±14 % swing to machine state alone, and wall-clock
budgets calibrated on an idle machine are its single largest flake source. So a timing regression is
*reported* everywhere and *fatal* only under `--gate-timing`, which is the nightly run — and ⚠️ **it is
refused outright when the baseline's `host.processor` is not the machine doing the judging**, because
comparing an M1 Max mean with a shared Linux runner's is not a weak signal but no signal, and a verdict
drawn from one would be read as evidence.

⚠️ **The baseline carries its provenance or it stops being evidence.** `--update-baseline` stamps the
processor, core count, operating system, architecture, runtime and BenchmarkDotNet version out of
BenchmarkDotNet's own `HostEnvironmentInfo`, plus the commit and the moment — an unattributed number is
indistinguishable from a guess within a month, and this file is asked to be a gate. The comparison
reads the processor back out, which is what makes the paragraph above enforceable rather than advisory.

⚠️ **And the route to that baseline was broken, in a way only running the target could show.**
`Benchmark` built BenchmarkDotNet's command line as one interpolated string and handed it to
`SetApplicationArguments`. Nuke quotes *each element* of that list — every other `DotNetRun` in
`build/` passes a `List<string>`, and `Build.ContentBytes.cs` says so in a comment — so the whole line
arrived as a single argv entry, `-- "--filter * --artifacts … --exporters json"`. Measured against
BenchmarkDotNet 0.15.8's own `ConfigParser`: that argv parses **false** and the element-per-argument
form parses **true**. So the target would have run no benchmark at all, and
`--update-baseline` would have failed with *"no reports to write a baseline from"* — a message naming
the symptom and not the cause. Five rounds of this issue recorded the baseline as blocked on hardware;
it was also blocked on this.

⚠️ **Nothing prevented the next one, so `BuildArgumentQuotingTests` does.** `build/_build.csproj` is
outside the solution and has no test project, which is why the only call site in `build/` that broke
the rule was in the only target nobody had run. The check is over the *syntax* of `build/**/*.cs` and
holds both spellings of the same trap: `SetApplicationArguments` may not be handed a string with a
space in the text the author typed (interpolation holes are exactly what Nuke quotes correctly, so
they are ignored), and `DotNet(…)` — whose `ArgumentStringHandler` quotes the holes and leaves the
literal text alone — may only be handed a string written at the call site, never one built into a
variable first. ⚠️ Both sabotages are the historical defect itself: restoring `Benchmark`'s single
interpolated string, and hoisting `CheckWhitespace`'s command line into a local, each go red naming
the file and line. It lives in `Tools/Vixen.ApiCheck.Tests` beside `AotProbeProjectFileTests` and
`CoverageReportTests` — the two other build files that acquired a fixture the same way.

**Still owed** ([#339](https://github.com/Rikarin/Vixen/issues/339)): the baseline itself, taken on
hardware that will run the suite again — and now, first, one real `Benchmark` run to confirm the
arguments reach BenchmarkDotNet end to end, which needs `build.sh`; and the `benchmark` CI job, which
has to follow it — a job added first would fail every pull request for want of the file it compares
against.

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

Every package: SourceLink, symbols (`.snupkg`), deterministic, README, and — per **ADR-015** —
`PackageLicenseExpression=Apache-2.0` and the `NOTICE` file. Both of the last two are set once, for
every package at a time, in `Directory.Build.props`, and **`Pack` opens each produced `.nupkg` and
fails if either is absent**: they are obligations that travel with the distribution, so the only place
the answer is real is inside the archive.

⚠️ **Three things this paragraph used to claim, that are not true and are owed rather than done**
([#337](https://github.com/Rikarin/Vixen/issues/337)):

- **No package declares an icon.** There is no `PackageIcon` anywhere in the tree.
- **`PackageValidation` against the previous release is not switched on.** `EnablePackageValidation`
  appears nowhere. It is the natural companion to `CheckApi` — which catches a source-level break in
  the tree, where this catches a binary one against what was shipped — and it needs a published
  baseline version to compare against, which is why it is waiting on the first release rather than on
  a decision. (`gh release list` is empty and `VersionPrefix` is `0.1.0`.)

  ⚠️ **And the half that does not need a baseline is empty here, which is worth knowing before
  somebody lands it as "a one-line change available today".** `EnablePackageValidation=true` with no
  baseline runs two validators, and this repository gives both of them nothing to compare: the
  *compatible framework* validator compares one target framework's assets against another's, and no
  project in the tree declares `TargetFrameworks` at all — all 282 are single `net10.0`; the
  *compatible runtime* validator compares RID-specific `lib` assets against the RID-less ones, and the
  only RID-shaped path any package writes is `tools/runtimes/` inside `Vixen.Sdk`, which is a tool's
  own payload rather than a `runtimes/<rid>/lib` the validator inspects. So the property would gate
  nothing today, and its absence costs nothing. ⚠️ It is also **not** the risk it was believed to be:
  `Vixen.Core.Mathematics` packs clean with it set, so the fear that switching it on across 167
  packable projects under warnings-as-errors turns `Pack` red is at least not universal. The day it
  starts mattering is the day a package multi-targets or ships a RID-specific assembly, which is
  exactly when nobody will remember this paragraph.

  ⚠️ **So both halves of the refusal now expire loudly instead of being re-derived by hand.**
  [#337](https://github.com/Rikarin/Vixen/issues/337) recorded the same two facts through six separate
  rounds — no release to be a baseline, no multi-target to be an input — and each round established
  them by grepping and left nothing behind, which is the shape of a refusal that quietly becomes an
  oversight. `Tools/Vixen.ApiCheck.Tests/PackageValidationTests` asserts the two premises rather than
  the property: it fails naming the first project to declare `TargetFrameworks`, and it fails when
  `docs/api-history/index.json` archives any release other than the `VersionPrefix` this tree builds
  — the ritual's own committed record being the one statement about "has this repository released"
  that is readable without a network. ⚠️ Its third test is the guard the other two need, and it
  earned its place immediately: the first version of the walk excluded any path containing `.claude`,
  which excludes *the entire tree* when the tree is an agent worktree under `.claude/worktrees`, so
  both premises were being asserted over nothing and both were green.
- **The third-party attribution manifest is in no package.** `docs/manual/third-party.md` is packed by
  nothing, so "fails if any of the three is missing" could never have held for it. Whether it belongs
  inside every package, or whether the `NOTICE` discharges §4(d) on its own, is a licence question and
  is tracked with [#129](https://github.com/Rikarin/Vixen/issues/129) rather than decided here.
  `CheckAttribution` does a different job: it holds that page against the files that pin what it
  attributes, and says nothing about any `.nupkg`.

## GitHub Actions

```
.github/workflows/
├── ci.yml               # PR + push to master: the eight jobs below
├── nightly.yml          # fuzzing (a job per target), property suites, and the three that need a real service
└── docs.yml             # Docs → GHCR image on master and on tags; pr-<n> tag per PR (25).
                         # ⚠ CheckDocs is not here — it runs in ci.yml's `checks`, on every PR
```

⚠️ **`release.yml` does not exist, and this list named four files for as long as it named any.** The
`Release` target does exist, so the tag-triggered path is a target nothing triggers
([#13](https://github.com/Rikarin/Vixen/issues/13)).

`ci.yml`, eight jobs:

| Job | Runner | Runs |
|---|---|---|
| `test` | `ubuntu-latest`, `windows-latest`, `macos-14` | `RestoreNativeDeps`, then `Test --configuration Release`. ⚠️ The three legs are not "does it build everywhere": `Vixen.Net.Tests/Wire` asserts the wire format against committed bytes, so running it on three operating systems and two architectures **is** the assertion that two peers encode a value identically. It also builds and uploads the content and remesh manifests the two jobs below compare |
| `checks` | `ubuntu-latest` | `CheckArchitecture CheckApi CheckFormat CheckDocs CheckStrings`, then `CheckShaders` |
| `web` | `ubuntu-latest` | `CompileWeb PublishWeb`, then `BrowserSmoke` — 37 checks over CDP, no Playwright |
| `pack` | `ubuntu-latest` | `Restore Compile Pack --skip Test`. ⚠️ `Pack` depends on `Test` in the target graph, which is right for a developer typing `nuke Pack` and would be a second full test run here |
| `aot` | `ubuntu-latest`, `windows-latest`, `macos-14` | `CheckAot` |
| `sample-frame` | `ubuntu-latest` | `SampleFrame` |
| `content-bytes` | `ubuntu-latest`, needs `test` | compares the three legs' `ObjectId` manifests — the determinism gate. ⚠️ It counts them first: a comparison of nothing agrees with itself |
| `remesh-bytes` | `ubuntu-latest`, needs `test` | the same for the remesh manifest |

⚠️ **Eleven rows used to be named here and none of them corresponded**
([#340](https://github.com/Rikarin/Vixen/issues/340)). The three `build-test-<os>` rows are one `test`
job with a matrix; `graphics` is folded into it, because the golden fixtures run under `Test`;
`content` is `content-bytes`; and `pack`, `sample-frame`, `remesh-bytes` and `web`'s `BrowserSmoke`
appeared in no row at all. The four legs that are genuinely missing — `android`, `ios`, `benchmark`,
and `CheckAotIos` — are [#327](https://github.com/Rikarin/Vixen/issues/327) and
[#339](https://github.com/Rikarin/Vixen/issues/339); the `benchmark` row's reasoning (allocation gates
on a pull request, timing gates nightly) is right and is now implementable, but it has to follow the
committed baseline rather than precede it.

`nightly.yml`: `targets` (reads the fuzz target list and its budgets), `fuzz` (a job per target, over a
committed corpus), `properties` (a job per suite), and `postgres`, `docker` and `kubernetes` — the
three that need a real service rather than a double.

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
- ⚠️ **Proposed, and implemented by nothing** — traits for filtering:
  `[Trait("Category","Unit|Integration|Golden|Perf|Platform")]`. `Trait(` appears in **zero** of the
  5 291 tracked `.cs` files (searched with `git grep -a`, so a NUL byte in a literal cannot be hiding
  one); no test in this repository has ever carried a trait of any kind. It is listed among the
  conventions above, which read as *in force*, and it is not one — which is why it is marked here
  rather than left to be discovered by somebody writing `--filter Category=Unit` and getting an empty
  run. Whether it arrives at all is [#558](https://github.com/Rikarin/Vixen/issues/558)'s open
  question: a speed lane wants a way to name the slow tests, and a tag every author has to remember
  is a gate nothing enforces unless something enforces it per assembly.
  ⚠️ **What the threshold would be is arithmetic rather than taste, which nobody had checked.** Model
  a `Speed!=Slow` lane by scaling each assembly's wall by the fraction of its test CPU that survives,
  over the 178 TRX of the 2026-09-05 23:04 run: at a **10 s** per-method threshold the lane is
  **316 s** against the full run's 498.3 s — 62 methods tagged, 1 542 s of the 2 982 s of test CPU
  removed — and at 5 s it is still 316 s. Below 10 s the lane buys nothing further because
  `Vixen.Editor.App.Tests` bounds it: that assembly holds **13.3 s** of slow methods, 4.1% of its own
  CPU, so it survives the lane almost whole at 316 s while the next assembly finishes at 72 s. ⚠️ So
  the old floor argument is wrong about the full run and exactly right about the lane, and the answer
  it gives is "yes, and 10 s". Raising the threshold is what costs: 20 s gives 342 s and 30 s gives
  411 s. ⚠️ Also worth knowing before anyone budgets on it: 'summed wall removed converts to elapsed
  at about a quarter' over-promises here by about 2× (1 542 / 4 = 385 s against a measured 182 s),
  because the makespan stops falling once it reaches the longest surviving assembly.
  ⚠️ It does **not** contradict the refusal 160 lines above. What that paragraph refuses is the *Nuke
  switch* `--filter <test-trait>` ([#340](https://github.com/Rikarin/Vixen/issues/340)), and the
  replacement it names — a direct `dotnet test --filter` — is exactly what would consume a trait.
  Were these categories ever applied, `dotnet test <project> --filter "Category=Unit"` is the command,
  and `nuke Test --filter` remains a switch that does not exist.
  ⚠️ **But that command is now the *wrong* spelling of the lane, and the reason is
  [#560](https://github.com/Rikarin/Vixen/issues/560).** `dotnet test --filter` builds
  `VSTestTestCaseFilter`, and a VSTest-only switch takes the whole invocation back to VSTest — so a
  `Speed!=Slow` lane written that way would hand back the per-assembly second the platform runner
  just bought, on every assembly it ran. xunit's own runner takes the same question directly:
  `--filter-not-trait Speed=Slow` (also `--filter-trait`, `--filter-class`, `--filter-method`,
  `--filter-query`, each with a `not` form — read off `--help` on a built test assembly on
  2026-09-06). It reaches the run through `TestingPlatformCommandLineArguments`, which is where
  `Directory.Build.props` already spells the TRX name, and it stays on the fast path. Nothing about
  the threshold or the tagging is settled by this; it only means the command in the bullet above is
  not the one to write down.
- Deterministic: no `DateTime.Now`, no unseeded random, no `Thread.Sleep`, no real network, no ambient
  filesystem (an in-memory `IFileProvider` is the default).
- Every test project runs green with `VIXEN_JOB_WORKERS=0` (single-threaded) as a separate CI leg.
- **Environment a test needs before its own process starts belongs on the process that starts it,
  never in a shell profile.** Today that is exactly one variable — `DYLD_LIBRARY_PATH`, which macOS's
  dynamic linker reads once at launch and which is what makes the Vulkan validation layer load at all
  ([10](10-platforms.md) § macOS). Putting it in a developer's `~/.zshenv` would make "are the
  validation layers on?" depend on which terminal the suite happened to be launched from, and answer
  *no* in CI and in the IDE without saying so. The corresponding test asserts the layer is *on*
  wherever it is installed, so a machine that quietly loses validation fails rather than passes.
  ⚠️ **This used to say `.runsettings` and it had to move**, because the platform runner does not read
  that file: `Build.ExportLayerLibraryPath` sets the variable on the Nuke process, and `Test`,
  `GoldenImages` and `AffectedTests` call it before invoking anything. Measured on 2026-09-06 rather
  than assumed — a probe test asserting the variable's value passes through
  `dotnet msbuild -t:VSTest` when the invoking process exports it and fails when it does not, so the
  inheritance chain through `dotnet` and MSBuild to the test executable is real. `.runsettings` still
  exists and is still passed by `Coverage`, which is the one target still on VSTest.
- **Each test project writes its own `.trx`,** named after the project — `--report-trx-filename` on
  the platform path, `VSTestLogger` on `Coverage`'s, both in `Directory.Build.props`. Nuke passes a
  results *directory* and no filename: a fixed name points all eighteen projects at one path, they
  run concurrently, and the artefact CI publishes is whichever finished last. The build still fails
  on a red test — the exit code does not go through the file — but the report a human opens to find
  out *which* test is the entire point of producing one.

**[#560](https://github.com/Rikarin/Vixen/issues/560) landed on 2026-09-06: the test assemblies run
through Microsoft.Testing.Platform, not VSTest.** A xunit.v3 assembly is an executable that hosts its
own runner, and VSTest started two more processes per assembly to talk to it — `vstest.console.dll`
and `testhost.dll` beside the executable, ~1 s of protocol each across 180 assemblies. Five audits
costed this migration and each declined it; what they got wrong is worth recording:

- ⚠️ **It needed no package and no csproj edit, and four audits costed it at 180 `PackageReference`
  additions plus 180 removals.** `Microsoft.Testing.Platform.MSBuild` 1.9.1 is *already* in every test
  project's graph: `xunit.v3` 3.2.2 resolves to `xunit.v3.mtp-v1` → `xunit.v3.core.mtp-v1`, whose
  nuspec depends on it and on `Microsoft.Testing.Platform` itself. Its targets override MSBuild's
  `VSTest` target when `TestingPlatformDotnetTestSupport` is true, so the switch is one property.
- ⚠️ **The removals were never the point and are deliberately not made.** `xunit.runner.visualstudio`
  and `Microsoft.NET.Test.Sdk` cost restore, not run time, and keeping them is what makes the whole
  change revertible on one line — `-p:TestingPlatformDotnetTestSupport=false` puts any single
  invocation back on VSTest. `Coverage` is that invocation: `--collect "Code Coverage;Format=cobertura"`
  is a VSTest data collector the platform does not have, and a coverage run left on the platform path
  would have reported nothing and said so in green.
- The TRX writer *is* genuinely new — `Microsoft.Testing.Extensions.TrxReport`, pinned to **1.9.1**.
  ⚠️ 2.0.0 is what NuGet resolves by default and it dies before running a test with
  `TypeLoadException: Could not load type 'Microsoft.Testing.Platform.Extensions.TestHost.IDataConsumer'
  from assembly 'Microsoft.Testing.Platform, Version=2.0.0.0'`, because xunit.v3 binds the 1.x
  platform. One `ItemGroup` in `Directory.Build.props` reaches all 180 projects.
- The instrument survives, checked rather than reasoned about: a deliberately failing test through
  `dotnet msbuild -t:VSTest` exits **1** and writes `<ResultSummary outcome="Failed">` with
  `<Counters>` and `<Times>` in VSTest's shape, into a TRX named after its project in the directory
  `-p:VSTestResultsDirectory` named. So `TestOrder --update-test-cost`, `AffectedTests`, #863's cost
  guard and the read-the-outcome-not-the-counters rule all keep working.
- ⚠️ **A run given a VSTest-only switch falls back to VSTest for that invocation**, and finding out
  why is the sharpest illustration of the hazard below. `dotnet test <project>` with a test-case
  filter — the command this repository's own CLAUDE.md hands every reader — sets
  `VSTestTestCaseFilter`, which the platform ignores: the first filtered run after the switch landed
  ran **95 tests having been asked for 8**, exited green, and said so only in an `MTP0001` line. So
  `VSTestTestCaseFilter`, `VSTestSetting` and `VSTestCollect` each turn `TestingPlatformDotnetTestSupport`
  back off for that one invocation. It costs a second on a single project, which is the run where a
  second does not matter, and it is why `Coverage` keeps working even without asking.
- ⚠️ **`MTP0001` is a warning, not an error**, and it is what the platform says when a VSTest property
  is set and ignored — `VSTestSetting`, `VSTestLogger`, `VSTestResultsDirectory`. That is the failure
  mode this change is most exposed to: one warning line inside a five-hundred-second log. Two of the
  three are therefore set only on the path that reads them, and `VSTestResultsDirectory` is re-routed
  into `--results-directory` rather than left to be ignored. `Test`'s existing count-the-TRX
  assertion is the backstop.
- **Not measured here.** The saving is quoted from earlier audits (~1 s per assembly, ~43 s of a
  498 s run) and this session could not run `Test`. What was measured is that the run works, and
  `Vixen.Ecs.Tests` went 2.03 s through `dotnet test` before to 1.65 s through
  `dotnet msbuild -t:VSTest` after, on a machine with fifteen worktrees on it.

### Coverage, reported and not gated

⚠️ **The floor is refused; the report is a separate target and off the graph.** This document used to
put `Coverage` on the graph after `Test` and to say the `Test` row *"enforces per-project coverage
floors"*. Neither was ever built. What follows is the decision that replaces those two sentences, so
that the refusal is a choice somebody can argue with rather than a hole nobody noticed — and then the
half of it that is not refused.

A percentage gate over ~180 projects fails all three of this repository's tests for an instrument:

- **Ask what it prints on the day it does not run.** A collector that fails to attach reports 0 % or
  100 % depending on which one it is — a number that fails the build for the wrong reason, or passes
  it for the wrong reason, and in neither case says the instrument is dead. That is the Null-device
  failure and the never-skipped-golden failure again, in a third costume.
- **A floor set at today's number is a ratchet, and a ratchet is what people route around.** A test
  written to raise a percentage rather than to catch a defect passes the gate and helps nobody, and
  it is indistinguishable at review from one that does both.
- **A per-project table goes stale the day a project is added**, which is the drift
  `FuzzGateTests.TheNightlyMatrixIsTheRegistry` exists one document over to stop.

So coverage is not a gate here. The gates that carry the same weight are the ones that are *executable
claims* rather than metrics — the allocation gates above, the conformance suites, `CheckApi`,
`CheckStrings`, the golden images — each of which fails on a described defect rather than on a number
drifting downwards.

**The report is not the gate, and it is built.** ✅ `Coverage` (`build/Build.Coverage.cs`) runs the
collector `Microsoft.NET.Test.Sdk` already carries — `--collect "Code Coverage;Format=cobertura"`, no
new package and no restore — one assembly at a time, and writes `artifacts/coverage/coverage.md`.
`--coverage-project` aims it, because instrumentation multiplies a run's cost and nothing in CI runs
this.

⚠️ **It gates on nothing about the number and on exactly one thing about itself.** A run that produced
no cobertura document did not measure zero — it did not measure — so a missing document fails by name,
a `--coverage-project` matching nothing fails, and a suite whose report does not name its own subject
assembly fails. That last one is a finding about the suite rather than a number about the assembly.

⚠️ **The reader counted every line twice, and running it once is how that was found.** The target has
never been executed through `build.sh` by any session that wrote it, so its invocation was run by hand
instead — `dotnet test Core/Vixen.Core.Mathematics.Tests --settings .runsettings --results-directory
artifacts/coverage/<project> --collect "Code Coverage;Format=cobertura"`, which is what the fluent
`DotNetTest` settings build — plus `--property:TestingPlatformDotnetTestSupport=false`, which is
what keeps this one target on VSTest and its collector ([#560](https://github.com/Rikarin/Vixen/issues/560)) —
and the attachment landed exactly where `Measure` globs for it. Over that
real document the reader said `Vixen.Core.Mathematics` was **8 444 of 11 318** lines. It is **4 221 of
5 658**: a cobertura `<class>` lists its lines once inside each `<method>` and once more in its own
`<lines>`, so `Descendants("line")` counts both. ⚠️ **The rate was right to three decimal places
while both counts were doubled**, which is the kind of wrong a table of percentages cannot show you —
and the fix carries its own oracle, because a document's header states the totals over its packages:
`Measure` now sums every package and fails when that disagrees with `lines-covered`/`lines-valid`, so
a reader and a collector that disagree about what the file says stop the run before a plausible number
is printed. Restoring the descendants walk turns the sum into 14 092 of 21 139 against a header of
7 045 of 10 566.

⚠️ **The number reported is the subject assembly's, not the run's, and that is most of what makes it
worth reading.** Measured on this machine: `Vixen.Graphics.Null.Tests` covers **80.8 %** of
`Vixen.Graphics.Null` (740 of 916 lines — this paragraph said *"2 960 of 3 664"*, which is that same
measurement read by the double-counting walk above, twice over) and **32.6 %** of everything the run
loaded (3 689 of 11 322), because the same document carries `Vixen.Core` at 0.1 % — a figure that
describes neither project and moves
whenever an unrelated dependency grows. A "per-project coverage" table built from a document's own
`line-rate` would be that second number.

⚠️ **And running it a second time found the second reader defect: cobertura names a package by
*assembly* name, and this tree renames ten of them.** `Measure` derived the subject by stripping
`.Tests` off the project name, which is right for `Core/Vixen.Ecs.Tests` and wrong for every tool —
`Tools/Vixen.ApiCheck` builds `vixen-api-check.dll`, so a real document from `Vixen.ApiCheck.Tests`
carries the packages `vixen-api-check` and `Vixen.ApiCheck.Tests` and nothing called
`Vixen.ApiCheck` at all. The target's own "does not name its subject, so the suite never loaded the
assembly it is named after" then fires — a finding about the reader wearing a finding about the
suite, which is the exact failure the paragraph above warns about, arriving one round later in a new
place. `CoverageReport.Subject` now asks the sibling project file for its `<AssemblyName>` (as XML,
the way `AotProbeProjectFile` reads the probe) and falls back to the convention.

⚠️ **Both of those were found by linking the reader into a throwaway harness, and both proofs left
nothing behind — so the harness is committed now.** `Tools/Vixen.ApiCheck.Tests/CoverageReportTests`
links `build/CoverageReport.cs` the way that project already links `build/AotProbeProjectFile.cs`,
over a fixture cut from a real attachment that keeps the duplicate listing. Restoring the descendants
walk turns three of its six tests red on the counts while leaving the *rate* untouched, and dropping
the assembly-name lookup turns exactly one red. That is the only part of `Coverage` a session
forbidden `build.sh` can prove; the fluent `DotNetTest` settings and the `artifacts/coverage` layout
were checked by running the equivalent `dotnet test` invocation by hand (the attachment landed where
`Measure` globs for it, and the class lists summed to the header's 978 of 1 193 where the descendants
walk gave 1 957 of 2 387), and **Nuke's own traversal and the `coverage.md` it writes are still
unproved**.

**And where "is this line reached" is a real question, the answer is a test.** ✅ Two of the three
places ([#338](https://github.com/Rikarin/Vixen/issues/338)) are done, and the first is the worked
example of the shape: the generated ECS query surface, driven rather than counted, by
`Vixen.Ecs.Tests/QueryAritySurfaceTests` and `Vixen.Ecs.Tests/QueryAritySweepTests`.

⚠️ **What made it worth writing is what the grep found, from both ends.** `QueryArityGenerator`
emits sixteen arities of four description builders and four iteration families — "roughly two
thousand lines of code whose only variable is a number", as its own remarks put it — which is
**128 callable methods** (sixty-four builders, sixty-four iteration methods) beside thirty-two
delegates and thirty-two visitor interfaces. Across `Core`, `Samples`, `Editor`, `Platform` and
`Tools` the tree called **ten** of them: `Query` at arities 1, 2 and 4, `QueryWithEntity` and
`ForEach` at arity 1, `WithAll` at 1, 2 and 4, `WithAny` at 2, `WithNone` at 1 — and
`ForEachWithEntity`, all sixteen arities of it, by nothing at all. Narrow the grep to the suite that
is supposed to be testing this and the number is **one**: `ForEach<SumHealth, Health>`, in
`QueryTests`. A transposed index at arity nine would have been found by a game, months later. A
coverage percentage would have reported all of that as one number against `Vixen.Ecs` and left the
reader to guess which lines it was about.

The claim is a **drive and a census together**, because either alone is green on the day it stops
measuring. The drive runs all sixty-four iteration methods and all sixty-four builders over three
entities — three and not one, because with a single row every offset into the chunk is zero and a
loop that walked the same row *n* times would leave the arithmetic exactly right — and asserts a
closed form: slot *i* is a column of every arity above *i*, in each of four families, so it ends
worth `seed + i + 4 × (16 − i)`. The census comes in two forms, and they are complementary rather
than duplicate: one asserts by reflection that the generator emits **no arity beyond** the ones the
drive covers, so raising `MaxArity` without extending the drive fails rather than silently leaving
the new arities untouched; the other reads the test assembly's own IL back and fails **naming any
generated member nothing calls**, so the claim survives a fifth family arriving as well.

⚠️ **Three sabotages, and the first one is the lesson.** Transposing a column into the wrong
parameter *does not compile* — the generated `Values<T{i}>()` is type-checked, so the failure mode
everyone fears is the one the compiler already owns, and an attempt to prove these tests that way
proves nothing. ⚠ A build error is not a red test. The ones that compile are arithmetic: a row
offset made wrong only above arity 4 fails the drive and **nothing else in the suite** — 127 other
tests at the time it was measured, and that silence is the measurement of the gap; pinning the column offset to zero (every entity handed the
first entity's components) and pinning the entity reference (the handle stops advancing beside the
columns) each go red naming the entity they were given — which is why every component in the sweep
knows which entity it belongs to. Raising `MaxArity` to 17 fails the census.

✅ **The serializers are the second, and the same two halves carry it:**
`Vixen.Core.Serialization.Tests/BuiltInSerializerSweepTests` runs every serializer
`BuiltInSerializers` declares against the edges of its own type, and reads the nested types back off
the assembly so the file rather than the table is the enumeration. ⚠️ **Twenty of the twenty-five
built-ins had never been written by that suite**, for a reason no percentage could have shown: every
contract in its `Contracts.cs` is made of `int`, `float`, `double`, `string`, an enum and collections
of those, so `sbyte`, `ushort`, `char`, `Half`, `decimal`, `Guid`, `DateTime`, `DateTimeOffset`,
`TimeSpan`, `AssetId`, `SubAssetId`, `AssetReference`, `ObjectId`, `Entity` and `ComponentTypeId` did
not appear in the test project at all.

⚠️ **And the first assertion written for it was itself the defect, which is the part worth keeping.**
Stamping every `DateTime` `Utc` in the writer left a plain `Assert.Equal(written, read)` **green**:
`DateTime.Equals` compares ticks and ignores `Kind`, and `DateTimeOffset.Equals` compares the instant
and ignores the offset. The kind is asserted on its own and the offset through `EqualsExact` because
of that, and neither would have been without the sabotage. Both code sabotages — the stamped kind,
and an `Entity` read that drops its world id — fail this suite and **nothing else in the project**:
84 other tests green in each case, which is the measurement of the gap rather than a claim about it.
Adding a twenty-sixth serializer without a sweep entry fails the census.

✅ **The cascade is the third, and it needed the census far more than it needed the drive.**
`Vixen.Ui.Styling.Tests/SelectorSurfaceSweepTests` carries one row per member of the four enums the
selector language is made of — `Combinator`, `SimpleSelectorKind`, `AttributeOperator`, `PositionTest`,
thirty-four in all — and reads the enums back so a member with no row fails by name.
⚠️ **That direction is the whole value here, because this is the one subsystem where the gap is
silent by construction**: a new member with no arm in `SelectorMatcher` *compiles*, and a selector
using it then matches nothing at all — no exception, no diagnostic, a rule that never fires. It is
`TypeSelectorReachTests`' drift one layer down, in the language rather than in the sheets.

⚠️ **The rows had to be made unable to lie, and that is not the usual table's problem.**
`SelectorMatchingTests` already says what each construct *means*, by hand and better than a table
can; what it cannot say is that its list is complete. So each row's selector is compiled and walked
— compounds, simples, and the selectors nested inside `:not()`, `:is()` and `:has()` — and the member
it claims must actually appear, or `":first-child"` filed under `NthLastOfType` would satisfy the
census while testing something else. ⚠️ `SimpleSelector.Operator` and `.Position` are read **only**
where the kind says they mean something: both are non-nullable with a real default, so every simple
selector in the tree otherwise "contains" `AttributeOperator.Present` and `PositionTest.First`, and
those two rows would pass against anything. Each row also asserts an element it does *not* match;
`Universal` is the single row that cannot, and a row without a negative has to say why.

Three sabotages, each red and each on its own: `SelectorMatcher`'s `Lang` arm returning `false` — the
missing-arm failure itself — fails that row and nothing else; a row mis-filed under another member
fails on what compiling it actually produced; deleting a row fails the census naming the member.
1860/1860 in `Vixen.Ui.Styling.Tests`, 0 skipped.

⚠️ **None of the three ended up as a percentage, and that is now evidence rather than an argument.**
Each of the query surface, the serializers and the cascade turned into an executable claim that a
named surface is exercised whole, and in each the enumeration — not the assertion — is what caught
something: twenty unswept serializers, six hundred ungated arities, and a selector language nothing
counted.

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
| **Performance** | BenchmarkDotNet, judged by `Benchmark` against `Benchmarks/baseline.json` — ⚠️ **which is not committed**, so the target fails rather than judging, and this row said "committed baselines" for as long as it said anything ([#339](https://github.com/Rikarin/Vixen/issues/339)). The allocation gates beside it are real and run inside `Test` | ~40 benchmarks |

### The gates that enforce [00](00-vision-and-principles.md)

These are ordinary tests, which is the point — the non-negotiables are executable.

⚠️ **This block used to be written as `TestApp.Create(GraphicsBackend.Null)` / `app.RunFrames(10_000)`
— against a type that has never existed** (see § Test infrastructure worth building early). The gates
were written regardless, in twenty-three suites, each driving the subsystem it measures rather than a
whole frame: `Measured.NothingAllocated` is the shared half, and the arrangement is the test's own.
That turned out to be the better shape and not a workaround. A whole-engine frame that allocates
forty-eight bytes names none of the forty systems that could have done it, and every one of these
suites would still have had to construct its own subject inside the harness to say anything sharper.

```csharp
const int WarmUpFrames = 200;
const int MeasuredFrames = 2_000;

static readonly TimeSpan Sixtieth = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

[Fact]
public void ALoopingCoroutineAllocatesNothingPerFrame() {
    var scheduler = new CoroutineScheduler();
    var time = GameTime.Zero;

    scheduler.BeginFrame(time = time.Advance(Sixtieth));
    scheduler.Run(Body());

    Measured.NothingAllocated(Frame, WarmUpFrames, MeasuredFrames);
    return;

    void Frame() {
        scheduler.BeginFrame(time = time.Advance(Sixtieth));
        scheduler.Drain(ResumePoint.Update);
    }
}
```

— [`Core/Vixen.Engine.Tests/CoroutineAllocationTests.cs`](../../Core/Vixen.Engine.Tests/CoroutineAllocationTests.cs),
abridged. `Measured` itself is linked test-only source rather than a package; the reasons are in
[`Testing/Vixen.Testing.props`](../../Testing/Vixen.Testing.props).

Equivalents exist for: a 10 k-entity scene, a steady-state UI frame, a signal-update storm, a layout
pass over an unchanged tree, and an asset-load/release cycle. When one of these fails,
`Measured.NothingAllocated` names the **types** the allocation came from, in a second explanatory pass
run with the runtime's allocation sampler armed — otherwise "something allocated 48 bytes" is an
unactionable red build.

⚠ **It names the type, not the call site, and that is the mechanism's ceiling rather than a shortcut
taken.** This section previously specified "the exact allocation via a `GCHeapAllocationEventSource`
listener". There is no such event source; the runtime's provider is `Microsoft-Windows-DotNETRuntime`,
and measured on .NET 10 against an in-process `EventListener`:

- `GCAllocationTick`, the event that sentence meant, **is never delivered at all** — not at keyword
  `GC` (0x1) at Informational, not with all sixty-four keyword bits set at Verbose, not over a 96 MB
  window in which nine hundred other runtime events arrived.
- What *is* delivered is `AllocationSampled`, and only at keyword `0x800_0000_0000` (bit 43). Its
  payload is `TypeName` and `ObjectSize` with **no stack**, and the callback runs on the EventPipe
  dispatcher thread rather than the allocating one — so there is no call site to be had, by any
  arrangement of the listener.
- It **samples one allocation in ~100 KB** (941 samples over 96,000,040 B), and the budget is not
  tunable through the provider's filter arguments.

The last point is why the naming has to be a *second* pass: the counted window these gates fail on is
of the order of 9,640 B or 48,040 B, and at both of those the sampler produces nothing at all. The
explanatory run scales the pass count until enough of the same allocation has gone by, and is armed
only around itself — never around the counted window, and never around a re-measurement `Measured`
discarded. A genuine call site needs a profiler-grade capture (`dotnet-trace` with a stack-carrying
provider, or a heap diff), which is an opt-in investigation tool and not something a per-PR gate can
afford.

### Test infrastructure worth building early

⚠️ **All five are written now, and the sequencing this section used to state is refuted by the tree.**
`TestApp` was specified as a Phase 1 item that *"every later phase depends on"*. Every later phase
shipped without it: 180 test projects, twenty-three suites of allocation gates and the golden suite
exist, and nothing anywhere named the type until it landed. So the dependency was never real — what
each of the five bought when it arrived was arrangement code deleted and a refusal added, not tests
made possible. (This paragraph said *"two of the five are still unwritten"* while `GoldenFile` was
already ✅ two bullets below it, and then one, which is the arithmetic a count written in prose
always loses.) Tracked as [#336](https://github.com/Rikarin/Vixen/issues/336).

- **`TestApp`** — ✅ an in-process engine host with the Null backend, an in-memory VFS, a fake clock and
  a synthetic input source, in [`Testing/TestApp.cs`](../../Testing/TestApp.cs), linked into a test
  project the way `Measured` is. ⚠️ **All four parts already existed and none of them had ever been
  assembled**: `HeadlessPlatform` is the host, `MemoryFileProvider` is the VFS,
  `AppConfig.FixedFrameTime` is the clock, and `HeadlessInputSource` with `HeadlessPlatform.Post` is
  the input. So what landed is arrangement plus **three refusals**, which is the part of the
  specification that could not be got from the parts:

  ⚠️ **Each refusal replaces a form that is green when it should be red**, which is this section's own
  standard — *"whatever lands has to answer the question the Null device already taught this repository
  once"*. (a) A game whose `OnConfigure` sets `Graphics.Enabled = false` builds, initialises and runs
  frames perfectly happily, and every command-log assertion made against it is an assertion over a
  device that was never opened — so `Create` refuses to hand one back. (b) `VixenApplication.RunFrame`
  **returns normally on a stopping application**: it pumps events, drains posted work and returns
  before `Advance`, so `for (…) app.RunFrame();` over an app that stopped on frame one is a hundred
  successful calls, an unmoved clock and an empty log. `RunFrames` counts frames off `GameTime.FrameCount`,
  which only a frame that simulated advances, and names why it stopped. (c) `HeadlessInputSource.SetKey`
  is the obvious way to press a key and it posts **no event**, while `Services.Input` is fed from the
  event stream in `PumpEvents` — so a key "pressed" that way reaches nothing, and a test asserting the
  action did *not* fire passes for the wrong reason for ever. `PressKey` does both halves, and
  `TestAppTests.SettingTheKeyWithoutTheEventReachesNothing` pins the difference.

  Adopted in `HostedDeclaredSystemTests`, which lost its throwaway temp directory and its own builder
  in the same change; the other ten fixtures in `Vixen.App.Tests` are the remaining adoption, and
  ⚠️ one of them cannot move — `--vixen-loose-content` takes a **physical** directory (`Directory.Exists`
  in `ContentMount.Open`), which an in-memory VFS by definition cannot provide.
- **`RecordingBackend`** — ✅ the Null backend's structured command log with a fluent assertion API,
  in [`Testing/RecordingBackend.cs`](../../Testing/RecordingBackend.cs), linked into a test project
  the way `Measured` is. ⚠️ The recording half was never missing: `Vixen.Graphics.Null` has
  `CommandRecorder` and `RecordedCommand` and seventy-odd test files read them, so what landed is the
  vocabulary — `log.ShouldContainDrawIndexed(36).AfterBinding(pipeline)` — and the name is the
  document's rather than the thing's.

  **Two of its rules exist because the hand-rolled form gets them wrong, and both are proved red in
  `RecordingBackendTests`.** ⚠️ `ShouldNotContain` **fails on an empty log** rather than passing: a
  device built without `Record = true`, or a frame that threw before it drew, satisfies every
  `Assert.Empty(log.OfKind(…))` in a suite, which is a green report on a frame that never ran — the
  Null-device trap one layer up. And the ordering assertions hang off a **cursor that exists only
  because a match was found**, because the tree's usual form is
  `stream.FindIndex(c => c.Kind == BindDescriptorSet) < stream.FindIndex(c => c.Kind == Draw)`, and
  `FindIndex` returns −1 for a call that never happened — so "it bound before it drew" is green when
  nothing bound anything. `AfterBinding` asks which pipeline was *in force*, not whether the one named
  appears somewhere earlier.
- **`GoldenFile`** — ✅ the snapshot helper, in [`Testing/GoldenFile.cs`](../../Testing/GoldenFile.cs),
  linked the way `Measured` is and adopted by the four `Golden*Tests` in `Vixen.Raven.Tests`, which
  are what it was taken out of: each had written the same fifteen lines by hand.

  **Two of the three specified parts landed and the third was refused.** The unified diff is here and
  is the half `Assert.Equal` cannot do — over a few kilobytes of syntax tree or SPIR-V listing its
  message is a window of characters around an offset, which tells a reviewer that something moved and
  not what. ⚠️ **`__golden__/` was not imposed**: the corpora predate the helper and live where the
  input they were rendered from lives (`Fixtures/lambert.ir` beside `Fixtures/lambert.rvn`, reading
  the pair together being the whole review), so the caller names the path.

  ⚠️ **And the switch was not a decision after all.** The tree was read here as having three
  conventions for one behaviour; it has two, split by *what is being rewritten*. `UPDATE_GOLDEN`
  rewrites a **snapshot of output** and is what all six text suites already document and type;
  `VIXEN_REGENERATE` rewrites a committed **artefact that is not a fixture** — generated binding code,
  `reflect.json`, the parity census — which is a different thing that happens to be spelled with a
  file. `GoldenFile` honours `UPDATE_GOLDEN`, and also `VIXEN_UPDATE_GOLDEN` because `build/Build.cs`
  exports that from this document's own `--update-golden`.

  **What it buys is three refusals, and all three replace a form that is green when it should be
  red.** A golden that had to be **created** fails rather than passes, because a snapshot nobody has
  read is not evidence. An **empty rendering** is refused even against an empty committed golden — a
  printer that returned nothing, a generator that emitted no stages, an enumeration that found no
  fixtures, and an empty file committed once that agrees with all of them for ever. And
  ⚠️ `GoldenFile.Batch` refuses a set that **compared nothing**: `GoldenSpirvTests` and
  `GoldenGlslTests` both looped `foreach (var unit in Compile(name))` and reported a **pass** on a
  backend that generated no stages at all, which is the one failure a code-generation golden exists
  to catch. Each rule is pinned in `GoldenFileTests` and each was proved by sabotage, including that
  last one against the real suite.
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
- **`FixtureProject`** — ✅ the synthetic project generator, in
  [`Testing/FixtureProject.cs`](../../Testing/FixtureProject.cs), adopted by
  `Vixen.Editor.Assets.Tests/ImportBudgetTests`, which is the scale test that had been written
  without it.

  **What the generalisation turned out to buy is the kinds, and that was not the argument for it.**
  The prediction here was that this one was *"a generalisation of a working thing rather than a
  hole"*, because `ImportBudgetTests` already built its own fixture and scaled it from
  `VIXEN_IMPORT_SCALE`. True as far as it went: what that fixture wrote was ten thousand `.bin`
  files, which reach exactly one importer — the `RawImporter` fallback. ⚠️ **A fixture of the wrong
  kinds is green.** `RawImporter` takes whatever nothing else claimed, succeeds and is counted, so a
  "texture" no importer claims imports, passes and is indistinguishable from a texture unless
  something asks *which importer claimed it* — the shape of the `.vxwaves` that became a byte blob
  no runtime reader resolves, and of the five attributed importers found missing from
  `BuiltInImporters` after it. So the textures are real PNGs through `PngCodec`, the models are
  Wavefront OBJ and the scenes are `.vxscene` YAML, and `FixtureProjectTests` asserts the importer
  name per extension rather than the count.

  **Its three refusals are the same standard the other four are held to.** A fixture asked for
  nothing writes an empty project, over which *"everything imported"* and *"nothing failed"* are
  both true — refused. A second fixture written over the first returns counts short by whatever was
  already there, and short in the direction that still passes — refused. And what it wrote is
  counted **off the disk** and compared against what it computed, so a kind whose loop never ran is
  a smaller project that satisfies every assertion derived from it — refused. ⚠️ The counts are the
  product rather than the files: `ImportBudgetTests` used to compute `Files + (Files / 100) + 1`
  beside the loop that made the folders it was counting, which is two derivations of one number in
  one file.
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

  **Three findings were recorded here as open and filed rather than fixed. ⚠ All three are fixed, and
  none of them was ever filed** (#341) — the paragraph outlived the defects it was protecting the ✅
  above from being read over. They are kept rather than deleted, because each names a shape worth
  recognising a second time:

  - the YAML binder wrote `null` into a member declared non-nullable — nullability was decided from the
    CLR type, to which every reference type is nullable, so `subAssets: null` bound straight into an
    `AssetMeta` that broke its own declaration and the crash landed in whichever consumer dereferenced
    it first. ⚠ **Fixed in `0a905f21`**, and by carrying the *annotation* rather than by the cheaper
    "a collection member may not be null": the reflection generator reads it while the member is still
    a symbol and puts it on `MemberDescriptor.IsNullable`, which only ever narrows the CLR answer — the
    narrow rule would have refused `AssetMaterialSource.Slots`, `MoveQuery.Preferred` and
    `ResponseCurve.Keys`, which are nullable on purpose. `MetaTests.ANullAgainstANonNullableMemberIsRefused`
    pins the three refusals and `ANullAgainstANullableMemberIsStillBound` pins the half a type-shaped
    rule would have got wrong. Oblivious counts as nullable, so nothing that used to bind stopped;
  - three inputs made Raven's incremental reparse build a **structurally different tree** — the printed
    text still agreed, so only the shape comparison saw it. ⚠ **Fixed in `ad962730`**: a reuse
    candidate carries the parse loop that produced it (`ReuseCandidate.Context`) and a reuse site names
    the one it is standing in, because a node belongs to the grammar that read it and not to the
    characters underneath it. The smallest input is forty bytes — an enum whose name is replaced by the
    keyword `shader`, which leaves its members lexing identically at what is now a member boundary —
    and it is a row in `IncrementalParseTests` with the inputs in the corpus.

  **And the third.** A binder recursion on `func F(): float[F()]` overflowed the stack — not a
  property of `shader` as first recorded, but of any type with members: a `struct` did it too, and so
  did a parameter type, two signatures sizing arrays by each other, and a `val` parameter sizing its
  own type. Three of the four source symbols that resolve a type already carried the cycle guard;
  `SourceMethodSymbol` had it around the inferred branch only, and the two parameter symbols had none.
  Guarding the whole resolution in each, keyed by the symbol, reports `RVN2005` and closes the family.

  The input is `Corpus/raven/70ae34e20b4880ee.bin` now — it had been kept out deliberately, because one
  that overflows the stack takes the test host down on every build, and the rule in that harness is
  that promotion follows the fix. With it fixed, `raven` is back in the nightly and `VIXEN_FUZZ_SKIP`
  is empty.

  **A second overflow of the same family, found by reading rather than by fuzzing, is `RVN2008`.**
  `struct T { var f: T }` was neither a crash nor a diagnostic: resolution terminates — `var f: T`
  resolves to `T` in one step — so the `RVN2005` guard, which is about resolution that does not
  terminate, correctly never fired. What does not terminate is the *size*, and nothing asked for one
  until the SPIR-V backend walked the field types of an `OpTypeStruct` that held itself and ended the
  process at the guard page, exactly as `float[F()]` had. So the check is at layout time rather than
  at resolution time: the storage a value of the type holds — its bases' fields then its own, through
  array elements and tuple elements, on the generic *definition* so `Node<Node<T>>` is caught with
  `Node<T>` — and it reports the route (`A.b: P.B → B.a: P.A`) rather than the type, because `A`
  containing `B` containing `A` is the shape nobody sees by reading and naming only `A` sends the
  author to the file where nothing is wrong. There is no legal self-reference to admit alongside it:
  Raven has no pointer and no reference, and the one indirection it does have, `Buffer<T>`, is a
  descriptor that may only be a shader field (`RVN2053`) and so can never be a struct member.

  **And the nightly is a job per target rather than a job**, which is what makes the skip a hand
  override rather than the mechanism. One `strategy: matrix` leg over `FuzzTargets.Names`,
  `fail-fast: false`, each job with its own budget, its own `timeout-minutes` derived from that budget
  and its own `fuzz-findings-<target>` artifact. Three things go away with the single job: a cap that
  was target-count arithmetic done by hand and went stale twice; a target that ends its own process
  costing the other nineteen their results, which is precisely what `raven` did and why it was skipped
  at all; and one wall clock shared by twenty targets, so the deepest one could have no more time than
  the shallowest. The budgets are `Core/Vixen.Fuzz/nightly-budgets.json` — five minutes for a grammar
  that saturates in seconds, two hours for `raven` at 465 cases/s — and
  `FuzzGateTests.TheNightlyMatrixIsTheRegistry` fails on every build if that file stops being the
  target list, because a list of names in a YAML file is the drift this replaced wearing a different
  hat.

### Optional external tools

Some checks are worth more than they are worth *blocking* on, so they run when the tool is present
and report their absence through the test output rather than failing or silently passing:

| Tool | Install | What it unlocks |
|---|---|---|
| `spirv-val`, `spirv-dis` | `brew install spirv-tools` | validation of every emitted SPIR-V module, and the disassembly the differential oracle reads |
| `glslc` (shaderc) | `brew install shaderc`, `apt-get install glslc` | compiles Raven's GLSL back to SPIR-V for the differential oracle ([07 § C](07-raven-shader-pipeline.md)) |

The command-line tools rather than their NuGet bindings, deliberately: an oracle is a test-time
thing, and a native package would put shaderc's binaries in the restore graph of projects that must
never ship them.

**`ci.yml` installs both**, so these are optional locally and mandatory on a PR — a green local
run with the tools missing is a weaker signal than a green CI run, and the test output says which one
you got.

⚠ **It did not, for `glslc`, for as long as the differential oracle existed.** All three legs
installed `spirv-tools`; none installed shaderc, and the oracle ran on Windows only, by accident,
because LunarG's Vulkan SDK happens to carry `glslc.exe`. On the other two `SpirvDifferentialTests`
returned early and reported thirteen *passes*, which is the failure mode this section exists to
prevent — an optional check whose absence looks like a green one. Two things close it: the tests
now `Assert.Skip` rather than return, so a missing tool reads as a skip, and
`The_oracle_is_installed_so_this_file_means_something` **fails** when either tool is absent, exactly
as `SpirvBackendTests.The_validator_is_installed_so_these_tests_mean_something` already did for
`spirv-val`. A guard is what "must install" means; a sentence in a plan document is not.

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
nuke Test                                              # or dotnet test <one project> --filter <name>
nuke GoldenImages --update-golden                      # then review the diff
nuke Benchmark --benchmark-filter '*Layout*' --report-only

# run things
dotnet run --project Samples/03-PbrShowcase
dotnet run --project Editor/Vixen.Editor.App

# hot-reload loop
dotnet watch --project Editor/Vixen.Editor.App
```

Pre-commit hook (opt-in, installed by `nuke SetupDev`): `CheckFormat` + affected unit tests. Keep it
under 10 seconds or developers will disable it.
