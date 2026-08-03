---
title: Booting an application
slug: engine/booting-an-application
kind: guide
area: Engine
summary: The three calls behind VixenApp.Run, and the two seams that decide which platform and which device you get.
api: [T:Vixen.App.VixenApp, T:Vixen.App.AppBuilder, T:Vixen.App.Game, T:Vixen.App.AppConfig, T:Vixen.App.IPlatformFactory, T:Vixen.App.IGraphicsBackend, T:Vixen.App.PlatformHost, T:Vixen.App.GraphicsHost, T:Vixen.App.GraphicsBackend]
tags: [host, bootstrap, app, platform, backends]
since: 0.1
status: stable
related: [assets/content-in-a-game, engine/world-serialisation, rendering/lit-path]
---

## What it is

The path from `Program.cs` to a running frame. Usually one line:

```csharp no-compile="Program.cs in full; MyGame is the application's own type"
return VixenApp.Run<MyGame>(args);
```

`Run` is three public calls and nothing else:

| Call | Does |
|---|---|
| `VixenApp.Create(args)` | Parses the command line and installs the default backends |
| `AppBuilder.Build(game)` | Asks the game what it wants, then starts the platform, the file system, the workers, the window, the content, the world and the frame |
| `VixenApplication.Run()` | Owns the loop until something stops it |

## What it is for

[docs/plan/17](https://github.com/Rikarin/Vixen/blob/master/docs/plan/17-app-heads-and-shipping.md) chose "the app *is* the executable" over a prebuilt
player so that nothing in the boot path is a black box. Every step above is public, so an application
that wants control writes the steps out and edits the middle one:

```csharp no-compile="a fragment; `args` and `MyGame` are the application's"
using var app = VixenApp.Create(args)
    .WithServices(services => services.Registry.Add(new MySubsystem()))
    .Build(new MyGame());

app.Initialise();
while (!app.IsStopping) { app.RunFrame(); }
app.Shutdown();
```

That is also what lets an editor's play mode drive a game from inside its own frame, and a test drive
it a fixed number of frames, without a second implementation of the order things happen in.

## Using it

Subclass `Game`, and hand it to `VixenApp.Run`. `AppConfig` is what the application is asked for
before anything exists:

```csharp no-compile="the two hooks a game must have; OnConfigure runs before the platform does"
public sealed class MyGame : Game {
    protected override void OnConfigure(AppConfig config) {
        config.Name = "My Game";
        config.Window = new() { Size = new(1920, 1080) };
    }

    protected override void OnUpdate(GameTime time) { }
    protected override void OnRender(GameTime time) { }
}
```

⚠ **`OnConfigure` runs before the platform is started**, which is why it can decide what the platform
will be — `config.Headless`, `config.Window`, `config.Graphics.Enabled`, `config.UseEngine`. Anything
that needs a live world or a device belongs in `OnInitialise`, which runs after all of it.

The next three things you will reach for are all on the builder. `WithServices` registers a subsystem
once everything else exists; `WithPlatform` and `WithGraphics` hand over a platform or a device
somebody else owns — an editor's play mode, an XR runtime — and `WithContent` replaces plain HTTP for
remote bundles. A device handed to `WithGraphics` is **not** disposed with the application, because it
belongs to whoever handed it over.

## Which platform, which device

Two decisions in the boot sequence name an implementation rather than a contract: which platform to
open a window with, and which backend to open a device with. Both arrive as an interface.

| Interface | Answers | Ships as |
|---|---|---|
| `IPlatformFactory` | Desktop, or headless? | `PlatformHost` |
| `IGraphicsBackend` | Which graphics API? | `GraphicsHost` |

`VixenApp.Create` installs both, so the one-line form needs to know none of this.

### Choosing a graphics API

`GraphicsOptions.Backends` is an ordered preference list. `GraphicsHost` tries each in turn and
takes the first that opens:

```csharp no-compile="a fragment of OnConfigure"
config.Graphics.Backends.Clear();
config.Graphics.Backends.Add(GraphicsBackend.WebGpu);
config.Graphics.Backends.Add(GraphicsBackend.Vulkan);
config.Graphics.Backends.Add(GraphicsBackend.Null);
```

or from the command line, which **replaces** the list rather than adding to it:

```bash
./MyGame --vixen-backend vulkan,null
```

Leave the list empty and you get `GraphicsHost.Default`, which is Vulkan then Null — exactly what
this package did before the setting existed.

⚠ **Arguments are applied *before* `OnConfigure`**, which is true of every `--vixen-*` flag and not
special to this one. So a game that assigns `config.Graphics.Backends` in `OnConfigure` overrides
the command line, the same way one that assigns `config.WorkerCount` overrides `--vixen-workers`. If
you want an operator to be able to steer it, read the list rather than replacing it.

| Backend | Opens when | |
|---|---|---|
| `Vulkan` | there is a presentable surface | the reference backend (ADR-001) |
| `WebGpu` | Dawn or wgpu-native is installed | opt-in; not in the default order |
| `Null` | always | a shipping backend — doc 17's dedicated server runs on it |
| `OpenGl` | **never, yet** | see below |

⚠ **`OpenGl` cannot currently be opened by an app head, and asking for it says so rather than being
skipped.** A GL device needs entry points over a context already current on the calling thread, and
no `Vixen.Platform` implementation creates a GL context — there is no `SDL_GL_CreateContext` path in
`Vixen.Platform.Desktop` and nothing in `WindowOptions` to ask for one. Per ADR-001 the backend
exists as the RHI's abstraction validator, exercised by tests against a supplied `IGlApi`. It is in
the enum and in the selector deliberately: a chain that named it and silently skipped it would be
indistinguishable from one that tried and failed.

⚠ **Every rejection is reported, including on success.** A chain that fell through to Null says what
each earlier candidate refused with, so one log line explains the whole decision — "no Vulkan" alone
never told you why WebGPU was not used either.

⚠ **A list with nothing openable in it is a boot failure, not a silent downgrade.** There is no
implicit fall-through to `Null`: an operator who ran `--vixen-backend vulkan` to find out whether
Vulkan works is owed the answer, and quietly handing back a device that draws nothing is the exact
shape of the bug that question was asked to find. Put `Null` last to ask for the fall-through.

All four backends open the same way — `TryCreate(options, out device, out reason)` — so adding one
to the selector is a case in a `switch`, not a new shape to learn.

⚠ **The interfaces exist because of where the code lives, and that is worth knowing before you go
looking for a plugin model.** The host is `Vixen.App.Hosting`, under `Core/`, and
`nuke CheckArchitecture` fails the build when a `Core/` project references `Platform/` — where all
four implementations are. So the choice is asked for rather than made, and `Tools/Vixen.App` is the
package that answers it. There is no registry and no discovery: there are two answers to each
question, and a plugin model for two answers would be machinery for its own sake.

A head that wants something else supplies it. Either the finished object:

```csharp no-compile="a fragment; `platform` and `device` are the caller's, and `device` is not disposed with the app"
VixenApp.Create(args)
    .WithPlatform(platform)
    .WithGraphics(device)
```

or a factory, which is what an app head for a platform this package does not ship — Android, iOS,
Web — installs instead of `PlatformHost`:

```csharp no-compile="a fragment; MyBackend is the caller's IGraphicsBackend"
new AppBuilder(AppArguments.Parse(args))
    .WithPlatformFactory(new MyPlatformFactory())
    .WithGraphicsBackend(new MyBackend())
```

⚠ **A builder constructed directly and given neither refuses to build, by name.** It does not fall
back to headless. Falling back would turn "this head forgot to install its backends" into a game that
boots, runs and shows nothing — which is the hardest failure in this path to attribute, and is
indistinguishable from the fallback to headless that `PlatformHost` performs for real reasons.

⚠ **A device that draws nothing is not a failure either.** `IGraphicsBackend.Create` never returns
null and never throws for "there is no GPU": it reports why through `reason` and returns the Null
device. [docs/plan/17](https://github.com/Rikarin/Vixen/blob/master/docs/plan/17-app-heads-and-shipping.md) makes that a shipping backend — it is what
the dedicated server runs on, and running the whole frame against it is what keeps a server and a
client one program rather than two paths that drift.

### What is not behind a seam

Creating the swapchain looks like a backend decision and is not. Every backend implements
`IGraphicsDevice.CreateSwapChain`, so `AppGraphics.SwapChainFor` is plain code — a surface handle, a
size, and two format choices off `GraphicsOptions`. Routing it through the interface would have been
an indirection with one possible implementation.

⚠ It is sized from the window's **framebuffer**, not its client size. The two differ by the display's
scale factor, and a swapchain built from the client size on a 2× display is a quarter of the window —
which looks like a game rendered into the top-left corner. `AppGraphics.FramebufferOf` is where that
is decided, and it also clamps to one pixel, because a minimised window reports zero and every
backend refuses a zero-sized swapchain.

## Examples

**A dedicated server out of the same project.** Nothing is swapped: the variant says headless, the
platform factory returns the headless platform, the graphics backend returns the Null device, and the
frame runs end to end against a device that draws nothing.

```csharp no-compile="a fragment of OnConfigure; the variant usually comes from --vixen-variant or [BuildVariant]"
config.Headless = true;
config.FrameRateLimit = 30;
```

**A test that runs a fixed number of frames.** `Initialise`, `RunFrame` and `Shutdown` are public for
exactly this, so a test drives the real boot order rather than a second copy of it.

```csharp no-compile="a fragment; `platform` is a HeadlessPlatform the test owns"
using var app = VixenApp.Create([]).WithPlatform(platform).Build(new MyGame());

app.Initialise();
for (var frame = 0; frame < 10; frame++) { app.RunFrame(); }
app.Shutdown();
```

**An app head for a platform this package does not ship.** Android, iOS and Web have their own entry
points and their own platform and backend; they build the builder directly and install their own,
which is the case the two interfaces were drawn for.

```csharp no-compile="a fragment; both types are the head's own"
new AppBuilder(AppArguments.Parse(args))
    .WithPlatformFactory(new AndroidPlatformFactory(activity))
    .WithGraphicsBackend(new VulkanBackend())
    .Build(new MyGame())
    .Run();
```

## See also

* [Getting content into a running game](../assets/content-in-a-game.md) — what `AppConfig.StartupScene`
  defaults to, and where it comes from.
* `Tools/Vixen.App/README.md` — the frame, argument reference, logging,
  build variants and content mounting in full.
* `Core/Vixen.App.Hosting/README.md` — why the host is two
  packages.
