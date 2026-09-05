# 17 — App Heads, Build Variants and Shipping

> ⚠️ **Extended by [27](27-mmo-framework.md).** The dedicated-server variant below is one process,
> started by hand. Doc 27 adds a fifth kind of head — the *realm* — and the orchestrator that spawns,
> places, drains and upgrades fleets of them under Kubernetes, Docker, or nothing at all. Everything
> in § The dedicated server is its foundation and none of it changes.

This is Q5 elaborated. The question sounds like packaging trivia and is actually one of the more
consequential architectural decisions in the plan: it determines what a user's project looks like, how
long their builds take, whether the editor and the shipped game share code paths, and whether a bug
that appears only in a release build is diagnosable.

## The question

When someone ships a Vixen game, **what executable do they ship, and who produced it?** Two established
answers:

### Model A — the prebuilt player runtime (Unity, Godot)

The engine vendor ships a compiled player binary per platform. Building a game copies that player,
appends the content bundles and the user's compiled managed assemblies, and stamps some metadata.

- **Fast builds** — no native link step, usually seconds.
- **Consistent runtime** — the vendor controls exactly what executes.
- But the player must handle *every* configuration it might ever meet, so it is large and generic.
- And it is opaque: you cannot change the boot sequence, you cannot see why startup takes 800 ms, and
  the engine is a black box at the exact moment you most need it not to be.
- It also requires the vendor to build, host, version, and support N × M prebuilt binaries.

### Model B — the app *is* the executable (Stride, and the grain of .NET)

The game is an ordinary .NET project with an entry point. `dotnet publish` produces the executable. The
engine is NuGet packages, nothing more.

- **Transparent** — normal .NET debugging, normal stack traces, no vendor-binary boundary.
- **Trimmable per game** — a 2D game does not carry the deferred renderer.
- Native .NET tooling applies directly: `PublishTrimmed`, `PublishAot`, `PublishSingleFile`,
  `PublishReadyToRun`, SourceLink, symbol servers.
- Nothing for us to build, host, or version beyond packages.
- But builds are slower (AOT especially), and the user is exposed to publish complexity unless tooling
  hides it.

## Decision

**Model B, with a Model-A-quality experience layered on top.** Concretely:

1. **The game is a normal .NET executable.** No prebuilt player binary exists, and none is shipped.
2. **`Vixen.App` provides a one-line host** so the user's `Program.cs` is trivial by default, and fully
   open when they want control.
3. **`vixen build` wraps** content build + `dotnet publish` + platform packaging into one command, so it
   *feels* like a one-click build.
4. **The editor is one app head among several** — it is a Vixen application ([11](11-editor.md)), just an
   unusual one (JIT-hosted, reflection-permitted, plugin-loading).

Model A is rejected because its two advantages do not apply to us. Fast builds: we get most of that from
incremental content builds and JIT (non-AOT) dev configurations. Consistent runtime: with six platforms
× several graphics backends × client/server/headless variants, the prebuilt matrix would be dozens of
binaries we would have to build, sign, host, and support — for an engine of this size that cost is not
recoverable, and it buys opacity we do not want.

## What the user actually writes

```csharp
// MyGame/Program.cs — the default, and usually the whole file
return VixenApp.Run<MyGame>(args);
```

```csharp
// MyGame/MyGame.cs
public sealed class MyGame : Game
{
    protected override void OnConfigure(AppConfig config)
    {
        config.Window.Title = "My Game";
        config.Graphics.PreferredBackends = [GraphicsBackend.Vulkan, GraphicsBackend.OpenGL];
        config.StartupScene = "scenes/main-menu";
    }
}
```

✅ **Built, and the line most games will not need to write.** `AppConfig.StartupScene` is an *address*
— a player has no asset database, so `Assets/Scenes/Menu.vxscene` is not something it can act on — and
it defaults to whatever the content build shipped first: the editor's Build Settings scene list is
resolved to addresses at build time and written as a `SceneManifest` beside the catalog. A game that
sets it wins over that, an operator's `--vixen-scene` overrides the manifest without a rebuild, and
the host loads it into `AppServices.Scenes` before `OnInitialise` so the hook can find what the level
placed. Every failure is a warning and an empty world rather than a process that will not start, for
the reason a broken compositor falls back: the thing that would show the message is the thing that did
not start.

And when they want control, `VixenApp.Run` is not magic — it is a documented sequence they can inline
and edit:

```csharp
using var app = VixenApp.Create(args)
    .WithPlatform(PlatformHost.Detect())
    .WithGraphics(GraphicsBackend.Vulkan)
    .WithContent(ContentSource.Bundles("content"))
    .WithSubsystems(s => s.Add<PhysicsSystem>().Add<AudioSystem>())
    .Build();
return app.Run();
```

The rule: **nothing in the boot path is inaccessible.** Anything `VixenApp.Run` does, a user can do by
hand. This is the property Model A cannot offer and it is worth protecting deliberately.

## Build variants

Five, and they are orthogonal to platform:

| Variant | Content | Assertions / validation | Diagnostics | Trim / AOT | Purpose |
|---|---|---|---|---|---|
| **Editor** | loose files, live import | all on | everything | JIT only | the editor itself |
| **Debug** | loose files or bundles | all on | everything, hot reload | JIT | daily development |
| **Development** | bundles | on | profiler, console, remote inspector, debug overlays | JIT / ReadyToRun | QA, playtests, on-device profiling |
| **Release** | bundles | off | log ring + crash reporter only | trimmed, AOT where enabled | shipping |
| **Server** | bundles, no textures/audio/shaders | on (configurable) | full logging, metrics endpoint | trimmed | dedicated server |

The **Development** variant is the one teams discover they need and engines often omit: a bundle-based,
optimised build that still has the profiler and remote inspector. Without it, "it only reproduces in
release" is undiagnosable. Unity calls this a Development Build; it earns its place.

## The dedicated server — a new requirement from Q7

Networking ([16](16-networking.md)) makes a **headless server build a first-class variant**, which the
plan did not previously account for. Consequences worth stating:

- **`Vixen.Graphics.Null` becomes a shipping backend, not merely a test backend.** It was specified for
  CI ([05](05-graphics-rhi.md)); it is now also what a dedicated server runs on. This is a pleasant
  accident of the existing design — the backend already exists and is already the most-tested one because
  every RHI unit test uses it.
- A server build must run with **no GPU, no window, no audio device, and no display server** — so
  `Vixen.Platform` needs a `HeadlessPlatformHost` alongside the desktop/mobile/web hosts, and every
  subsystem must tolerate its absence rather than assuming a window exists.
- The content build gains a **server content profile**: skip texture compression, audio encoding, and
  shader permutations entirely. A server bundle should be a small fraction of the client's, and this is
  where the addressable group model ([08](08-asset-pipeline-and-addressables.md)) pays off — group
  membership plus a build profile is all the mechanism needed.
- Server builds want a Linux container image. `vixen build --variant server --target linux-x64
  --container` producing a minimal image is the natural deliverable.
- Metrics/health endpoints for orchestration, feeding from the existing
  `System.Diagnostics.Metrics` counters ([13](13-diagnostics.md)).

## Editor-only code: separate assemblies, not two package flavours

The hard part of Q5. Editor builds need reflection, plugin loading, asset importers, undo/redo, an
image decoder, and Assimp. Shipped games must have **none** of those — for size and for AOT.

⚠ **That sentence used to name ImageSharp, and rested its strongest leg on ImageSharp's licence**
(#353). There is no ImageSharp: 4.0.0 fails the build without a purchased licence key — an error out of
its own targets file — so the editor decodes with `StbImageSharp`, which is public domain and covers
more of doc 08's importer table than ImageSharp reached. `Directory.Packages.props` § Imaging records
the swap, and `CheckArchitecture`'s editor-only rule now carries `Silk.NET.Assimp` instead
([12](12-build-ci-and-testing.md)). The licensing argument left with the package; size and AOT are the
two that remain, and ADR-015's runtime half is unaffected — a shipped game reads KTX2 with Vixen's own
code and never decodes an authoring format at all.

Three ways to achieve that, and the choice matters:

| Approach | Verdict |
|---|---|
| `#if VIXEN_EDITOR` inside runtime assemblies | **Rejected.** It requires publishing two flavours of every package, which doubles the version matrix, breaks NuGet caching semantics, and guarantees someone eventually ships the editor flavour. |
| Separate editor assemblies only, no conditional code | **Primary mechanism.** Editor functionality lives in `Editor/*`; runtime assemblies expose extension points it hooks into. Enforced by `CheckArchitecture` ([12](12-build-ci-and-testing.md)). |
| **Trimmer feature switches** for the residual cases | **Held in reserve — the residue never arose.** The mechanism is the right one if a runtime type ever genuinely needs an edit-time-only member: an `AppContext` switch declared with `[FeatureSwitchDefinition]` and an ILLink substitution, which the trimmer proves false in a published game and removes along with everything it reachable-references. ⚠ Nothing in the tree uses it, and that is the good outcome rather than a gap — see below. |

So: **one set of packages, one version, and there is no editor-only path left in a runtime assembly for
the trimmer to remove.** The separation carried the whole load.

⚠ **This section used to say the switches were "adopted for the remainder", and there is no remainder**
(#351). The case it named is *an asset that re-serialises at author time*, and every `ToYaml` on an asset
is in `Editor/*` — `Vixen.Assets` and `Vixen.Engine` do not reference `Vixen.Core.Yaml` at all, so the
edit-time re-serialiser is not merely unreachable in a shipped game, it is not linked into one. The one
runtime assembly that does reference the YAML stack is `Vixen.Ui.Controls.Advanced`, whose `DockLayout`
is reached from the public `DockingHost.Load`: a desktop application saving its own dock layout is a
runtime feature of the framework, which is the whole claim [00](00-vision-and-principles.md) makes about
it, and not editor residue.

⚠ **Read as a reference-graph audit and not as a measurement.** What was proposed to settle this was a
trimmed publish under the IL trimmer's `--dump-dependencies`, and that would answer a stronger question:
which of the assemblies a game *does* link survive trimming. The claim above is the weaker and cheaper
one — that the edit-time serialiser is in a different assembly from the ones a game references — which
is enough to retire the adoption but not enough to close the measurement. `CheckArchitecture` is what
keeps the boundary honest between audits.

## Play mode: in-process, with out-of-process as a real option

The editor's play mode was specified as in-process with a world snapshot ([11](11-editor.md)).
Networking forces the second mode into scope:

| Mode | Behaviour | When |
|---|---|---|
| **In-process** (default) | Game runs inside the editor viewport; world snapshot on entry, restored on exit | Normal iteration. Fast, inspectable, live-editable. |
| **Out-of-process** | Editor launches N standalone player processes against the same content, attaches the remote inspector to each | **Multiplayer testing** (N clients + a server), verifying release-config behaviour, and isolating a game that hangs or crashes |

Out-of-process is not a nice-to-have once networking exists — testing a server-authoritative game
requires several instances, and doing that by hand is the kind of friction that stops people testing.
The remote inspector ([13](13-diagnostics.md)) is already specified and is exactly the mechanism needed,
so the incremental cost is process launch, content sharing, and a session-management panel.

**In-process play mode's known hazard**, stated because it bites every engine that does this: static
state and unmanaged resources leaking between sessions. Mitigations — the world snapshot restores ECS
state; subsystem teardown is asserted (the `DisposeBag` and leak tracker from
[03](03-core-foundation.md) fail a play-stop that leaks); and an editor setting runs play mode
out-of-process for anyone who prefers certainty over speed.

## Trimming and AOT policy per target

| Target | 1.0 default | Notes |
|---|---|---|
| Windows / Linux / macOS | `PublishTrimmed` + `PublishReadyToRun`, single-file | AOT opt-in: real startup win, and losing plugin loading is fine for a *game* (unlike the editor) |
| iOS | **NativeAOT, mandatory** | Apple forbids JIT. Gated in CI from Phase 3 ([14](14-roadmap.md)) |
| Android | trimmed, JIT | AOT opt-in later |
| Web | trimmed, `InvariantGlobalization` | Verified: 0.93 MB Brotli floor ([spikes/web-webgl2](spikes/web-webgl2/RESULT.md)) |
| Server | trimmed | AOT attractive for container size and cold start; opt-in |
| **Editor** | trimmed **off**, JIT | Plugin loading via `AssemblyLoadContext` requires it ([11](11-editor.md)) |

## Packaging

`vixen build --target <t> --variant <v>` produces the platform-native artefact, all scripted in Nuke
([12](12-build-ci-and-testing.md)) so CI and a developer's machine run identical steps:

| Target | Artefact |
|---|---|
| Windows | folder, zip, or MSI; single-file exe |
| Linux | tarball, AppImage, or container image |
| macOS | `.app` bundle → signed, notarised `.dmg` |
| Android | AAB with per-ABI splits; Play Asset Delivery for remote addressable groups |
| iOS | `.ipa` with provisioning + entitlements |
| Web | `wwwroot` with Brotli-precompressed assets and a service worker |
| Server | container image or tarball |

## Project templates

```
dotnet new vixen-game      # ✅ game: Program.cs + Game subclass + Assets/ + Dockerfile (Q5c)
dotnet new vixen-app       # ✅ non-game application: Vixen.Ui only, no scene, no game loop
dotnet new vixen-lib       # ✅ a library consumable by either
dotnet new vixen-mmo       # ✅ a dedicated-server game — five projects, doc 27's reference graph
dotnet new vixen-plugin    # ✅ editor plugin — a plugin.yaml, one IEditorPlugin, registered through PluginContext
dotnet new vixen-tool      # ✅ headless batch tooling head (Q5d)
```

They live in [`Tools/Vixen.Templates`](../../Tools/Vixen.Templates/README.md) as **one tree of
files**, packed for `dotnet new` and embedded in `Vixen.Cli` for `vixen new`, because two copies of
every template is two copies waiting to disagree. `vixen-plugin` was written down as blocked — it would pin a
`PackageReference` on an assembly nobody publishes, and a template producing a project that will not
restore fails at the one moment a person has no context to debug it — and `Vixen.Editor.Plugin`
landed in the same wave, which is what unblocked it.

`vixen-game` produces one project rather than the per-platform heads named above; `vixen build
--target Android` publishes it. ⚠ **The sibling head projects are blocked rather than owed, on this
package's own rules**: a head is a `net10.0-android` or `net10.0-ios` project — which is why
`Samples/01`'s are out of the solution — the templates have no conditionals to make one opt-in, and
`Vixen.Templates.Tests` compiles a multi-project template as one compilation against assemblies a
machine without the workloads cannot supply. The reasoning, and the three ways out, are in
[`Tools/Vixen.Templates/README.md`](../../Tools/Vixen.Templates/README.md) § Still to come.

`vixen-app` matters for Q3's ordering: game developers are primary, but the application-framework claim
needs a template that produces a UI application with **no engine dependency** — and its existence is
also the practical test that the `Vixen.Ui` ⇸ `Vixen.Engine` boundary holds. It is written, and the
test is real rather than rhetorical: it references neither `Vixen.Engine` nor `Vixen.App` (which
would reach the engine the easy way), and `Vixen.Templates.Tests` asserts both absences.

## Sub-decisions — all resolved

| # | Question | Recommendation |
|---|---|---|
| Q5a | Client + server from one project, or separate projects? | ✅ **Decided: one project, two heads, selected by variant.** Shared gameplay code is the point; separation invites drift. |
| Q5b | May a Release build load loose files behind a flag? | ✅ **Decided: yes.** `--vixen-loose-content <path>`, off by default, refuses silently-quiet operation. Weakens the "release reads only bundles" invariant in exchange for diagnosability — the trade is deliberate and visible, and the visibility is the half the trade was made for. **Two of the three surfaces exist**: `VixenApplication.WarnAboutLooseContent` repeats `HostLog.LooseContentStill` every 60 s (once at startup is not visible — a build left running overnight scrolled that line away hours ago), and `FrameStatsOverlay` draws a `content LOOSE` row in the alarm colour, which is the surface a screenshot in a bug report carries. ⚠️ The crash-report stamp is blocked on crash reports existing at all ([#331](https://github.com/Rikarin/Vixen/issues/331)) and belongs in whatever lands there. |
| Q5c | Ship container images, or document it? | ✅ **Decided: ship a `Dockerfile` template** in `vixen-game` (multi-stage, distroless-ish base, non-root). No container toolchain, no registry integration. |
| Q5d | A console/tools app head? | ✅ **Decided: yes** — `dotnet new vixen-tool`. The headless host minus networking: batch asset conversion, CI screenshot generation, content validation, custom pipeline steps. Nearly free once `Vixen.Platform.Headless` exists. |
