# Vixen.App

The host. Usually the whole of `Program.cs`:

```csharp
return VixenApp.Run<MyGame>(args);
```

```csharp
public sealed class MyGame : Game {
    protected override void OnConfigure(AppConfig config) {
        config.Name = "My Game";
        config.Window = new() { Size = new(1920, 1080) };
    }

    protected override void OnUpdate(GameTime time) { }
    protected override void OnRender(GameTime time) { }
}
```

## Nothing here is a black box

`Run` is three public calls, and an application that wants control writes them out:

```csharp
using var app = VixenApp.Create(args)
    .WithPlatform(PlatformHost.Create(config))
    .WithServices(services => services.Registry.Add(new MySubsystem()))
    .Build(new MyGame());

app.Initialise();
while (!app.IsStopping) { app.RunFrame(); }
app.Shutdown();
```

[Doc 17](../../docs/plan/17-app-heads-and-shipping.md) chose "the app *is* the executable" over a
prebuilt player specifically so that the boot path is inspectable, and a convenience method that hid
steps would trade that away for one line. So `Initialise`, `RunFrame`, `Stop` and `Shutdown` are all
public — which is also what lets an editor's play mode drive a game from inside its own frame, and a
test drive it a fixed number of times, without a second implementation of the order things happen in.

## The frame

```
PumpEvents  →  MainThread.Drain  →  Advance  →  OnUpdate  →  OnRender  →  pace
```

Main-thread work drains *after* events so that anything an event handler posted runs in the same
frame rather than the next. Elapsed time is clamped to 250 ms, so a breakpoint or a closed laptop lid
does not hand the simulation a second of movement at once. `OnUpdate` and `OnRender` are separate
from the start, before there is a renderer to make it matter, because the split is what lets a later
loop run several simulation steps per rendered frame and retrofitting it means revisiting every
application written against a single hook.

The fixed-step accumulator itself is `Vixen.Engine`'s in Phase 2
([doc 03](../../docs/plan/03-core-foundation.md)); this loop is variable-step and calls the same
hooks.

## Input

`Services.Input` is an `InputService` — the devices, and every `.vxinput` asset being read from them.
The host clears the frame's motion deltas at the top of `PumpEvents`, offers each platform event to
`Services.Input.Devices` as it drains, and lets `SystemPhase.Input` read the actions. Without an
engine there is no such phase, so the host reads them itself before `OnUpdate`; either way it happens
once a frame and before anything that reacts to it.

**`PlatformInput` is the whole seam**, and it lives here rather than in `Vixen.Input` because
`Vixen.Input` is a `Core/` assembly that must not reference `Vixen.Platform` — see
[its README](../../Core/Vixen.Input/README.md). Events reach the device set only after
`Game.OnEvent` has declined them, so an application intercepting an event also keeps the action
system from seeing it, which is what a modal dialog needs "return true" to mean.

Gamepads already plugged in when the process started are added at boot: they produced no
`GamepadConnected` for anyone to hear, and an input layer built only from the event stream would see
a controller that does nothing until it is unplugged and plugged back in.

## Build variants

Building a game and a dedicated server from one project is written up in
[docs/manual/building-a-game-and-a-server.md](../../docs/manual/building-a-game-and-a-server.md).

Five, orthogonal to platform ([doc 17](../../docs/plan/17-app-heads-and-shipping.md) § Build
variants): `Editor`, `Debug`, `Development`, `Release`, `Server`. Resolved once from three sources in
order — `--vixen-variant`, a `[BuildVariant]` attribute on the entry assembly, and finally the
compilation's `DEBUG` flag. The last is a poor answer (it says nothing about whether content is
bundled) which is exactly why the attribute exists; it is there so a bare `dotnet run` still starts.

`Server` is the only one that is headless, and saying so is the whole of the difference at boot: the
host picks the headless platform and every subsystem takes the path it already had to have.

A crash behaves differently by variant, deliberately. Where validation is on it is logged and
**rethrown**, so an attached debugger stops on it. In a `Release` build it is logged and becomes exit
code 1, because on a player's machine there is no debugger and the log ring is what a crash reporter
uploads. Shutdown runs either way.

## Arguments

`--vixen-*` is reserved for the engine; everything else comes back in `AppConfig.Arguments`
untouched and in order.

| | |
|---|---|
| `--vixen-headless` | Run with no display server. |
| `--vixen-variant <name>` | Override the build variant. |
| `--vixen-video-driver <name>` | Insist on an SDL video driver: `x11`, `wayland`, `dummy`. |
| `--vixen-workers <n>` | Job-system workers. `0` is supported and tested. |
| `--vixen-frame-limit <n>` | Frames per second, `0` for uncapped. |
| `--vixen-log-level <level>` | The lowest level the log ring keeps. |
| `--vixen-loose-content <path>` | [Q5b](../../docs/plan/17-app-heads-and-shipping.md): read loose files instead of bundles, even in a release build. Warns loudly; the content system honours it in Phase 3. |

An unrecognised `--vixen-*` argument is **warned about**, not ignored — a typo in a launch script
that silently does nothing is how a QA build runs for a week without the profiler somebody thought
they had switched on.

## Falling back to headless

A configuration that does not ask for headless tries the desktop platform and falls back if SDL is
missing or there is no display server, recording why in `AppConfig.HeadlessFallbackReason` and warning
at startup. Not silent, and not the other way round: a head that wanted a window and did not get one
has to say so, or an operator who mistyped `--vixen-headless` spends an afternoon wondering where the
window went.

## Frame pacing

`FrameRateLimit` defaults to 60 rather than uncapped, and that matters more today than it will: with
no swapchain there is no vsync, so an uncapped loop spins a core at 100 % to draw nothing. Once a
graphics backend is present, presentation paces a windowed frame and this becomes the cap for when
vsync is off or there is no window — a server's tick rate, or a tool's.

`UnfocusedFrameRateLimit` defaults to 10. A game alt-tabbed away is a game whose fans should stop.

## Logging

The always-on ring buffer from `Vixen.Core.Diagnostics`, behind a twenty-line `ILoggerFactory` —
ADR-008 takes `Microsoft.Extensions.Logging.Abstractions` and no more, so the concrete `LoggerFactory`
(and the configuration and options stack behind it) is deliberately not available.

**And a console, for every variant except `Release`.** That is doc 17's table read literally:
Development lists a console among the things it carries and Server lists full logging, while Release
gets the ring and the crash reporter and nothing else — a shipped game has no terminal to write to
and would pay for every string it formatted. `config.LogToConsole` overrides it either way.

Until that existed the host added no providers at all, so a scaffolded game printed nothing and
`Samples/01` carried its own thirty-line copy — which is the usual sign that they belonged one layer
down. A dedicated server logging into a ring nobody reads is the same bug with worse consequences.

The host's own lines are generated `[LoggerMessage]` call sites with ids registered in
[`docs/manual/log-events.md`](../../docs/manual/log-events.md) — the first entries in that register,
which existed empty until something logged.

## Content

`Services.Assets` is an `AssetManager` over the content build the application shipped with, or
**null** when it shipped with none.

The host looks for `catalog.bin` under `/app/Content` — the folder `Vixen.Sdk` copies a build into,
spelled once in `VixenContentFolderName` and once in `ContentMount.FolderName` because two spellings
of one name is how a build that produced content and an application that found none end up in the
same release.

**It reads through the virtual file system, not through a path.** The obvious version takes
`IFileSystemHost.ApplicationDirectory` and appends `Content`, and it is wrong on the two platforms
Phase 3 exists for: that property is documented as empty where content is not a directory at all,
which is an APK's assets and an iOS bundle. Going through `/app` means Android's `AAssetManager`
answers the same call a desktop directory does.

**No content is not an error.** A sample that draws a triangle, a batch tool and a test each have
nothing to load, and a host that refused to start without a catalog would make the smallest possible
program the hardest one to write. The host logs one line saying why, which turns "my asset was not
found" from an afternoon into five seconds.

**A catalog it cannot read is reported, not thrown.** Truncated by a failed download, corrupted on a
phone's flash, written by a newer build — each happens in the field, and an application that refused
to start over one could not even show the message saying why.

### `--vixen-loose-content`

Points a build at a content directory it did not ship with, which is
[doc 17](../../docs/plan/17-app-heads-and-shipping.md) Q5b: a bug that only reproduces in a shipping
configuration has to be pokeable. The directory is mounted at `/content` and read instead of `/app`.

The trade is that "release reads only bundles" stops being an invariant, so **it is not allowed to be
quiet**: the host warns at startup and then **every sixty seconds** for as long as the build runs.
Once is not visible — a build left overnight in a QA lab scrolled that line away hours ago. The
diagnostic-overlay and crash-report stamps doc 17 also asks for arrive with the things that have them.

Today "loose" means a content *build* directory outside the package — what `vixen content build
--output` writes and what `vixen content serve` serves. Reading unbundled loose files, which is what
the Editor variant will want, needs a provider that does not exist yet.

## The world

`Services.Engine` is an `EngineLoop` — a world, its systems, its behaviours, its coroutines and its
fixed-step accumulator — and the host runs one frame of it per frame of its own.

**On by default**, because `VixenApp.Run<TGame>()` takes a `Game` and a game with a world is what
this host is for. `config.UseEngine = false` is one line, and it is the right line for the three
heads that do not want one: [doc 17](../../docs/plan/17-app-heads-and-shipping.md)'s batch tool, a
server driving its own simulation, and a UI-only application. Leaving it on for a head that ignores
it costs a world with no entities and eight system phases iterating nothing.

That this reference exists is **not** a licence for `Vixen.Ui` to reference `Vixen.Engine`. That
boundary is about `Vixen.Ui`, it is the thing that makes the application-framework claim real, and
`CheckArchitecture` still enforces it.

**The engine frame runs before `OnUpdate`.** That is the useful order: `OnUpdate` is where an
application reads the world it is about to render, and reading it before it has been stepped renders
last frame's positions — which looks like input lag and gets blamed on everything else.

**The engine is handed the unscaled delta and `TimeScale` separately**, because that is what
`EngineLoop.Frame` takes. Passing the already-scaled value along with the scale squares it, and half
speed silently becomes a quarter. `VixenApplication.TimeScale` is the one place to set it and it
reaches both clocks, so a paused game owes no simulation steps rather than accumulating a debt it
pays all at once when the menu closes.

## Still to come

**Graphics.** `OnRender` runs with nothing to render to. The shape is deliberate — the hooks and the
ordering are what a later phase fills in, not what it replaces.

**The meta-package.** [Doc 02](../../docs/plan/02-repository-layout.md) also describes `Vixen.App` as
the package that pulls in the graphics backends valid for a RID. That half arrives with the backends.

Licensed under Apache-2.0.
