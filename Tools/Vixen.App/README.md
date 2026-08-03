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

## Two assemblies, one namespace

Three files live here — `VixenApp`, `GraphicsHost` and `PlatformHost` — and the rest of the host is
[`Core/Vixen.App.Hosting`](../../Core/Vixen.App.Hosting/README.md). Both declare the namespace
`Vixen.App`, this package references that one, and nothing a consumer writes changes.

What changed is the build profile. `Tools/**` is compiled as tooling — *"reflection and LINQ
permitted; these are compilers and editors, **not frame code**"* — and the boot sequence and frame
loop are frame code. Under `Core/` they are AOT- and trim-analyzed, rooted in `Tools/Vixen.AotProbe`,
API-baselined by `nuke CheckApi` and documentation-gated by `nuke CheckDocs`. None of that applied
before.

The three that stayed are the three that name an implementation, and `CheckArchitecture` forbids a
`Core/` project from referencing `Platform/`, where all four implementations are. So the choice
arrives in `Core` as `IPlatformFactory` and `IGraphicsBackend`, and `VixenApp.Create` is the only
place the defaults are installed. ⚠ An `AppBuilder` constructed directly has neither and **refuses to
build, by name** — it does not fall back to headless, because that would turn a head that forgot to
install its backends into a game that boots, runs and shows nothing.

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
PumpEvents  →  MainThread.Drain  →  Advance  →  engine.Frame  →  OnUpdate
                                 →  draw the world  →  OnRender  →  present  →  pace
```

Main-thread work drains *after* events so that anything an event handler posted runs in the same
frame rather than the next. Elapsed time is clamped to 250 ms, so a breakpoint or a closed laptop lid
does not hand the simulation a second of movement at once. `OnUpdate` and `OnRender` are separate,
because the split is what lets the loop run several simulation steps per rendered frame — and now
also because the world's own frame is opened between them. See [The frame's
picture](#the-frames-picture).

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
| `--vixen-backend <list>` | Which graphics APIs to try, most preferred first: `vulkan,null`. Replaces the list rather than adding to it. One unreadable name rejects the whole argument rather than half-applying it. See [Which device](#which-device). |
| `--vixen-variant <name>` | Override the build variant. |
| `--vixen-video-driver <name>` | Insist on an SDL video driver: `x11`, `wayland`, `dummy`. |
| `--vixen-workers <n>` | Job-system workers. `0` is supported and tested. |
| `--vixen-frame-limit <n>` | Frames per second, `0` for uncapped. |
| `--vixen-log-level <level>` | The lowest level the log ring keeps. |
| `--vixen-log-file <dir>` | Also write rolling JSON-line files there, through `ZLoggerFileSink`. A directory rather than a file name, because the sink rolls by day and by size and therefore owns the names. |
| `--vixen-loose-content <path>` | [Q5b](../../docs/plan/17-app-heads-and-shipping.md): read content from there instead of from the package, even in a release build. Either another build's bundles or a project's `Library/` — see [Downloaded content](#downloaded-content) and the section below it. Warns loudly, on a timer. |
| `--vixen-scene <address>` | Open this scene instead of the one the content build listed first. What makes "it only reproduces on the third map" something a tester can hand over. |

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

`FrameRateLimit` defaults to 60 rather than uncapped. A windowed head is paced by presentation —
`config.Graphics.PresentMode` is `Fifo`, which is vsync — so the two cap at whichever is lower, which
is what a player expects from both settings. The limit is what does the work where there is no vsync
to do it: a server's tick rate, a tool's, and any head whose swapchain is the Null backend's.

`UnfocusedFrameRateLimit` defaults to 10. A game alt-tabbed away is a game whose fans should stop.

## Logging

The always-on ring buffer from `Vixen.Core.Diagnostics`, behind a twenty-line `ILoggerFactory` — the
concrete `LoggerFactory` lives in the non-abstractions package with a configuration and options stack
an engine has no use for, and composing three providers is not worth it.

**And a console, for every variant except `Release`.** That is doc 17's table read literally:
Development lists a console among the things it carries and Server lists full logging, while Release
gets the ring and the crash reporter and nothing else — a shipped game has no terminal to write to
and would pay for every string it formatted. `config.LogToConsole` overrides it either way.

**And a file, when asked.** `--vixen-log-file <dir>` or `config.LogFileDirectory` adds
`ZLoggerFileSink`, which is what a player attaches to a bug report and what a dedicated server keeps
between restarts. The factory is disposed last of everything the application owns, because the file
sink's dispose is what flushes its background buffer — a log missing its final seconds is missing the
part that explains them.

All of them share one `LogFilter`, so `--vixen-log-level` and any per-category rule mean the same
thing in every sink. A head that wants otherwise — a verbose file behind a quiet console — gives the
sink a filter of its own.

Until this existed the host added no providers at all, so a scaffolded game printed nothing and
`Samples/01` carried its own thirty-line copy — which is the usual sign that they belonged one layer
down. A dedicated server logging into a ring nobody reads is the same bug with worse consequences.
The mobile heads add `PlatformSink` instead, which is the one that reaches `logcat` and the Apple
unified log; a phone has no terminal, so a console sink there writes to nothing.

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

### Downloaded content

**Whether a build can download anything is read out of its catalog, not configured.** A bundle
carries a URL when its group declared `loadPath: Remote`; seeing one is what makes the host build a
`BundleCache`, a `RemoteBundleSource` and the `RoutedBundleSource` that routes by exactly that —
empty URL to the files beside the binary, non-empty to the cache. A game that ships everything in its
package gets none of it and pays for none of it: no cache directory, no socket, **no `HttpClient`**.
`Services.Content.RemoteReason` says so when there is nothing to fetch.

This was the other half of the gap `ContentMount` was written to close. Every piece worked — ranges,
resume, CRC, eviction, the "this pack is 240 MB, continue?" arithmetic — and the boot path built a
bare `LocalBundleSource`, so a remote group threw `BundleUnavailableException` on the first address
in it.

The cache lives under `/cache`, not `/data`: every byte in it is re-fetchable from the URL the
catalog names, so an operating system reclaiming it under storage pressure has taken nothing but
time. The same policy applied to `/data` would delete a save game. `Services.Content.Cache` is the
handle a "storage used" row and the button beside it want; `AssetManager.DownloadSize`,
`DownloadAsync` and `ClearCache` are the per-address forms, and they are what a "get this DLC now,
play it later" button is made of.

`VixenApp.Create(args).WithContent(transport)` replaces plain HTTP when reaching the URLs takes more
than `HttpClient`'s defaults — an authorisation header, a certificate pin, a retry policy. A
transport handed in is **not** disposed with the application, which is `WithGraphics`' rule and for
the same reason: it is somebody else's object and may outlive the mount.

### `--vixen-loose-content`

Points a build at a content directory it did not ship with, which is
[doc 17](../../docs/plan/17-app-heads-and-shipping.md) Q5b: a bug that only reproduces in a shipping
configuration has to be pokeable. The directory is mounted at `/content` and read instead of `/app`.

The trade is that "release reads only bundles" stops being an invariant, so **it is not allowed to be
quiet**: the host warns at startup and then **every sixty seconds** for as long as the build runs.
Once is not visible — a build left overnight in a QA lab scrolled that line away hours ago. The
diagnostic-overlay and crash-report stamps doc 17 also asks for arrive with the things that have them.

"Loose" now means either of two things, and the directory says which. A content **build** outside the
package — what `vixen content build --output` writes and `vixen content serve` serves — or a
project's own `Library/`, written by `vixen content loose`: a catalog whose entries name **no bundle
at all**, beside the `ArtifactDb/` the import wrote its chunks into. Finding that folder is what
makes the host mount the artefact store and read chunks straight out of it, and
`Services.Content.IsUnpacked` says it did.

That second form is doc 17's **Editor variant**, and it is the one that changes the iteration loop:
the same `BuildPlanner` decides the same addresses from the same sidecars, so a player resolves
`Assets/Textures/Crate.png` to the same asset it would in a shipped build — but the cost of making a
change visible is the re-import of the one asset that changed, with nothing packed. The runtime hooks
for it were already there and had nothing to serve: `AssetManager.MountFor` returns without mounting
anything for an entry that names no bundle, and `ObjectDatabase.Mount` adds bundles *last* so a
bundle never shadows what an editor is rebuilding into.

### The scene a build opens with

`scenes.bin` sits beside `catalog.bin` and is the third file a content build writes: the addresses of
the scenes the project's Build Settings listed, in that order. `Services.Content.Scenes` is what it
was read into.

**It exists because the two ends speak different languages.** What is committed under
`ProjectSettings/` is project-relative paths — a person edits that list, reviews it in a diff and
merges it when two branches each add a level — and a player has no asset database to resolve a path
with. `ContentPipeline` is the one place both forms exist, so the translation happens there, once, at
build time; an entry that names nothing or names a scene with no address **refuses the build**, because
either one produces a player that starts to an empty world.

`AppConfig.StartupScene` defaults to the first entry, and `VixenApplication.Initialise` loads it into
`Services.Scenes` **before `OnInitialise`** — which is the order that makes the hook able to find the
level's camera or parent something to its player. A game that sets `config.StartupScene` itself is
never overridden by the manifest: "the level this executable is for" is a stronger statement than "the
first one somebody listed". `--vixen-scene` sits between the two, so an operator can move a build to
another level without a rebuild.

**A scene that will not load is a warning and an empty world, not a failure to start** — the trade the
catalog, the shader bundle and the compositor all make, for the same reason: the thing that would show
a player the message is the thing that did not start. `VixenApplication.StartupScene` is the handle,
so a game returning to its main menu can unload the level it booted into.

## The world

`Services.Engine` is an `EngineLoop` — a world, its systems, its behaviours, its coroutines and its
fixed-step accumulator — and the host runs one frame of it per frame of its own.

**On by default**, because `VixenApp.Run<TGame>()` takes a `Game` and a game with a world is what
this host is for. `config.UseEngine = false` is one line, and it is the right line for the three
heads that do not want one: [doc 17](../../docs/plan/17-app-heads-and-shipping.md)'s batch tool, a
server driving its own simulation, and a UI-only application. Leaving it on for a head that ignores
it costs a world with no entities and eight system phases iterating nothing.

`Services.Scenes` is the one `SceneManager` over that world, non-null wherever there is an engine.
One manager rather than one per scene, because additive loading is the point: a level, its lighting,
the UI and a streamed chunk of terrain share a world so that a query can see across them, and
membership is a component so unloading stays a query and a destroy.

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

## The frame's picture

`Services.Graphics` is an `AppGraphics` — a device, a swapchain, an `EffectSystem` and the
`WorldRenderer` that joins the world to a drawn frame. The host opens it at boot and drives it once
a frame, so a game that places a camera and some drawables sees them without writing a line of
rendering code.

```
                        ┌ Begin: acquire → lend the image → WorldRenderer.Draw
engine.Frame → OnUpdate ┤  OnRender  (Services.Graphics.Commands is open, scene already recorded)
                        └ End: submit → present
```

**`OnRender` is inside the frame, not before it.** The scene is already in
`Services.Graphics.Commands` by the time the hook runs, so an overlay, a UI pass or a debug draw
records on top of it. `Commands` is null outside a frame and during one whose image could not be
acquired — a window mid-resize, a device that has gone — so an application checks rather than assumes.

**The camera comes from the world.** `CameraExtractionSystem` fills the frame's `RenderView` from the
lowest-`Order` entity carrying a `Camera` — in `SystemPhase.PreRender`, after the transforms are
written, so a camera moved this frame renders from where it now is. A world with no camera leaves the
view alone and says so through `Camera.Found`, because a zeroed matrix is a black screen that reads
as a broken renderer.

**The frame's shape is a document, not code.** `config.Graphics.Compositor` names a compositor asset
to load; without one the host uses `AppGraphics.DefaultFrame`, which is one lit pass into the window
— the smallest frame in which a scene is visible, and deliberately too small to be mistaken for what
a project should ship.

⚠ **The window's image is lent to the document under a name** (`config.Graphics.Output`, default
`SceneColour`) **and the frame's last colour target has to be that name.** A render graph culls a
pass whose output nobody outside it reads, so a document whose final pass writes a resource it
declared itself draws a correct frame into memory that is then discarded — a black window, no error
anywhere.

### Which device

`GraphicsHost` is `PlatformHost`'s counterpart. It walks `config.Graphics.Backends` — an ordered
preference list, also settable with `--vixen-backend vulkan,null` — and returns the first API that
opens. An empty list means `GraphicsHost.Default`: Vulkan, then Null.

`Vixen.Graphics.Null` is not a failure mode.
[Doc 17](../../docs/plan/17-app-heads-and-shipping.md) makes it a shipping backend: it is what the
dedicated server runs on, and running the whole frame against it is what keeps a server and a client
one program instead of two paths that drift. It is also what makes `--vixen-frames 10` a smoke test of
the entire renderer on a machine with no GPU, which is the only kind of machine CI has.

⚠ **The fallback to it is opt-in.** A list with nothing openable in it fails the boot and says what
each candidate refused with; there is no implicit downgrade, because an operator who asked for one
API is asking a question that "here is a device that draws nothing" answers with silence.

⚠ **`GraphicsBackend.OpenGl` has to be first in the list.** A GL device draws into the window's own
default framebuffer, so the window must have been created for OpenGL — and SDL fixes a window's
graphics API when it is made, with the OpenGL and Vulkan flags mutually exclusive. `PlatformHost`
reads the list to choose the flag before any backend is opened, so `[OpenGl, Null]` works and
`[Vulkan, OpenGl, Null]` does not fall back to GL.

It also needs 4.5 core or GLES 3.0 — `glClipControl` is what makes GL's clip space Vulkan's — and it
does not run on macOS, where Apple caps GL at 4.1 and SDL builds Metal-backed windows.

`WebGpu` opens where Dawn or wgpu-native is installed and is deliberately not in the default order:
promoting it would silently move existing heads onto a different API.

A head that wants another backend — OpenGL, WebGPU, the device an editor's play mode is already
drawing with — passes one to `AppBuilder.WithGraphics`. A device handed in that way is **not**
disposed with the application, because it belongs to whoever handed it over.

`config.Graphics.Enabled = false` is the line for a head that wants no device at all: a batch tool, or
a sample that builds the whole stack by hand to show what it looks like.

### Shaders

`vixen build` writes `shaders.effects` beside `catalog.bin`, and the host opens it into an
`EffectStore` behind `Services.Graphics.Effects`. That is a shipping build's **only** effect source —
the code that could compile a variant lives in `Tools/Vixen.ShaderCompiler` and is never linked into
a game — so a project with no bundle resolves every material to a miss and gets one line at startup
saying which build step has not been run. A development head adds a compiling provider on top, which
is the tiering `IEffectSource` was drawn for.

## Still to come

**Recovering from a lost device.** `AppGraphics.IsLost` latches and nothing more is drawn. Rebuilding
every device resource a game holds after a driver reset is a feature; a half-measure that left
handles dangling would be worse than the honest stop.

**The meta-package.** [Doc 02](../../docs/plan/02-repository-layout.md) also describes `Vixen.App` as
the package that pulls in the graphics backends valid for a RID. It now references Vulkan and Null
directly; the per-RID conditioning arrives with the packaging.

Licensed under Apache-2.0.
