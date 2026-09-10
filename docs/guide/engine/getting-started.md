---
title: Getting started
slug: engine/getting-started
kind: tutorial
area: Engine
summary: The first ten minutes — the one SDK the build needs, where the packages come from while nothing is published to nuget.org, the six templates and which one you want, the two files a new game is, and the one edit that proves the loop is running.
api: [T:Vixen.App.VixenApp, T:Vixen.App.Game, T:Vixen.App.AppConfig]
tags: [getting-started, install, templates, dotnet-new, first-run, sdk]
since: 0.2
status: preview
related: [engine/booting-an-application, ui/tutorial, engine/unity-migration, ui/markup-project-setup, rendering/standard-frame]
---

## What it is

The page whose reader has never run this engine. Everything else in the guide assumes a project that
already builds; this is how one comes to exist.

Three things happen here, in order: the SDK, a feed to restore `Vixen.*` from, and
`dotnet new vixen-game`. Then one edit, so that what is on screen is provably yours.

## What it is for

Getting to a window with a frame in it before deciding whether the engine is worth learning — and
knowing, while doing it, which step is a real requirement and which is a consequence of the project
not having shipped a release yet.

⚠ **The second kind matters here, because there is exactly one and it is the second step.** Nothing
`Vixen.*` is published to nuget.org today: release automation stops at the changelog and the API
fold, and signing, packaging and the GitHub release are not wired. So the templates and the packages
come out of a `Pack` you run, from a local feed. That is a fact about this moment rather than about
the engine, and it is stated plainly rather than left for a reader to discover as a restore error
naming a package nobody has ever published.

## Using it

### 1. The SDK, and nothing else

The .NET SDK version is pinned in `global.json` at the repository root — **10.0.301**, rolling
forward to the latest feature band. Nothing else is required to build the solution: no workloads for
a desktop build, no native SDK, no engine installer.

```bash
dotnet --version   # 10.0.3xx
```

⚠ Two targets need more, and neither is on this path: the Android, iOS and Web *heads* need the
workloads their platforms need, and a few backends need a native binary no package ships —
`./build.sh RestoreNativeDeps` fetches each one pinned and SHA-256-verified.

### 2. A feed, because nothing is on nuget.org yet

From a clone of the repository:

```bash
./build.sh Pack        # writes artifacts/packages/*.nupkg — about 57 of them
```

Then, next to the project you are about to create, a `nuget.config` that prefers that directory for
`Vixen.*` and nuget.org for everything else. This is the file the `CheckTemplates` gate writes for
its own scaffolding run, so it is the arrangement that is actually exercised:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="vixen-local" value="/path/to/Vixen/artifacts/packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="vixen-local">
      <package pattern="Vixen.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

⚠ **A scaffolded project restores perfectly well without any of this on a machine that has ever
packed**, because the global NuGet cache already holds the `Vixen.*` packages — which is why the
gate's own first assertion is a *negative control*: it requires a restore with the feed unwired to
**fail** before it believes the six that follow. If you skip this step and the restore succeeds, you
have proved something about your package cache and not about the feed.

### 3. `dotnet new`

The template package is `Vixen.Templates`, and it ships six templates:

| Short name | What it is |
|---|---|
| `vixen-game` | A game: a `Game` subclass, a one-line host, an `Assets/` folder, a `.vxproj` the editor opens, and a Dockerfile for the dedicated-server variant |
| `vixen-app` | An application: `Vixen.Ui`, a window, and **no engine** — the framework claim, scaffolded |
| `vixen-lib` | A library |
| `vixen-tool` | A command-line tool |
| `vixen-plugin` | An editor plugin |
| `vixen-mmo` | The gameplay-library composition `Samples/14-Mmo` is built from |

```bash
dotnet new install artifacts/packages/Vixen.Templates.<version>.nupkg
dotnet new vixen-game -n Kestrel -o Kestrel
cd Kestrel
dotnet run
```

A window opens, titled `Kestrel`, at 1280 × 720, drawing the frame the project's own
`Assets/Frame.vxcompositor` describes.

### 4. What the project is

Two files, and it is worth reading both before changing anything, because between them they are the
whole of the boot path:

`Program.cs` is one line. ⚠ **Everything `VixenApp.Run` does is a public call you can inline and
edit** — nothing in the boot path is inaccessible, which is the point of it being one line rather
than a framework.

```csharp no-compile="the template's Program.cs in full; KestrelGame is the project's own type"
return VixenApp.Run<KestrelGame>(args);
```

`KestrelGame.cs` is a `Game` with three overrides. `OnConfigure` names the window and — the line to
notice — hands the graphics configuration a **compositor asset** rather than a list of render passes:

```csharp no-compile="the template's OnConfigure, abridged; config is the AppConfig it is handed"
config.Window = new() { Title = "Kestrel", Size = new(1280, 720), IsVisible = true };
config.Graphics.Compositor = "Assets/Frame.vxcompositor";
```

That file is seven semantic knobs which expand at build time into the whole graph — shadows, GI, the
post chain. **The frame is data**: turning shadows on is an edit to an asset, not a code change and
not a call site.

⚠ **`config.Graphics.CasterStages` in the same method is extraction's half of those knobs, and a
frame cannot decide it.** `shadows:` above `Off` needs every mesh drawn into the `Shadow` stage too,
and `antialiasing: Taa` needs the velocity pass fed through `Motion`. A frame configured for
shadows with nothing extracted into the stage produces a picture that is not obviously wrong, which
is why the template ships both lines rather than letting you find out.

### 5. Change one thing

Open `Assets/Frame.vxcompositor` and change one knob — the exposure, or `shadows:`. Run again. The
edit is in a file, the frame it produced is different, and nothing was recompiled to make that true.

That is the shortest demonstration of the sentence the rest of this guide is written against.

## Examples

**An application rather than a game.** `vixen-app` is the other starting point, and it is a
different program: `UiApplication.Run` with a `.vxml` for the interface, a `.vcss` for the tokens,
and no `Vixen.Engine` anywhere.

```csharp no-compile="the vixen-app template's Program.cs, abridged; AppShell is the .vxml's generated class"
return UiApplication.Run(
    new UiApplicationOptions {
        Title = "Kestrel",
        Size = new Int2(1280, 800),
        Styles = { VixenUtilityStyles.Css },
        Content = () => new AppShell()
    },
    arguments
);
```

⚠ **Add the engine the day the application has a scene in it, and not before.** The game host owns a
frame loop built around an ECS world and a fixed-step accumulator; an interface's loop redraws a
document. They are two programs, and the templates are two templates for that reason.

**Running it where there is no screen.** Both hosts read `--frames N` — run exactly N frames and
exit. That is what a CI job asserts a build starts, presents and stops with, on a machine that may
have no GPU at all.

```bash
dotnet run -- --frames 60
```

⚠ **A headless run without `--vixen-capture` or `--vixen-offscreen` falls back to the Null device on
every platform**, exits 0, and prints healthy-looking counters. That is fine for "does it start" and
worthless for "is the picture right" — see the rendering guide before drawing a conclusion from a
headless number.

## See also

- [Booting an application](booting-an-application.md) — the three calls behind `VixenApp.Run`, and
  the two seams that decide which platform and which device you get.
- [The UI tutorial](../ui/tutorial.md) — the same first hour for `Vixen.Ui`: a panel with a list, a
  form and a command, built in `.vxml` and `.vcss`.
- [Coming from Unity](unity-migration.md) — the vocabulary map, and the four places the two models
  genuinely differ.
- [Making a project compile markup](../ui/markup-project-setup.md) — what turns a `.vxml` on disk
  into a class, and the three build errors that say which half is missing.
