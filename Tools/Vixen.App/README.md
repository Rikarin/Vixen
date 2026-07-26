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

## Build variants

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

The host's own lines are generated `[LoggerMessage]` call sites with ids registered in
[`docs/manual/log-events.md`](../../docs/manual/log-events.md) — the first entries in that register,
which existed empty until something logged.

## Still to come

**Content**, **graphics** and **the engine loop** are the three things this host will build and does
not yet: `--vixen-loose-content` is parsed and not honoured, `OnRender` runs with nothing to render
to, and the fixed-step accumulator arrives with `Vixen.Engine`. The shape is deliberate — the hooks
and the ordering are what later phases fill in, not what they replace.

**The meta-package.** [Doc 02](../../docs/plan/02-repository-layout.md) also describes `Vixen.App` as
the package that pulls in the graphics backends valid for a RID. That half arrives with the backends.

Licensed under Apache-2.0.
