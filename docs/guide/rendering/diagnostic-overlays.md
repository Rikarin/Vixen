---
title: Diagnostic overlays and the console
slug: rendering/diagnostic-overlays
kind: guide
area: Rendering
summary: One flag puts the frame-stats panel, the console, the log tail and every subsystem's debug lines on the screen of a running game.
api: [T:Vixen.Engine.Renderer.DebugOverlayRenderer, T:Vixen.Engine.Diagnostics.DebugDraw, T:Vixen.Engine.Diagnostics.Overlays.DiagnosticOverlays, T:Vixen.Engine.Diagnostics.Overlays.IDiagnosticOverlay, T:Vixen.Engine.Diagnostics.Overlays.ConsoleCommands, T:Vixen.Engine.Diagnostics.Overlays.ConsoleOverlay, T:Vixen.Engine.Diagnostics.Overlays.FrameStatsOverlay, T:Vixen.App.GraphicsOptions, T:Vixen.Rendering.LineShaders]
tags: [diagnostics, overlay, console, debug-draw, profiling]
since: 0.2
status: stable
related: [rendering/timing-the-frame, engine/booting-an-application, rendering/reading-the-frame]
---

## What it is

The panels [docs/plan/13](https://github.com/Rikarin/Vixen/blob/master/docs/plan/13-diagnostics.md) § Diagnostic overlays asks for — frame stats with a
frame-time graph, a mini flame chart off the profiler's rings, the tail of the log ring, and a
console — plus everything any subsystem has drawn into `DebugDraw`, all on the screen of a running
game. One switch turns the lot on:

```bash
./MyGame --vixen-overlays
```

or, for a development build that always wants them:

```csharp no-compile="a fragment of Game.OnConfigure"
config.Graphics.Overlays = true;
```

Press the **backtick** key for the console. Type `overlays` to list the panels and
`overlay <name>` to switch one on.

## What it is for

Everything in the list above was written, tested and reachable from nowhere. `DiagnosticOverlaySystem`
was constructed by its own tests and by nothing else in the tree; no compositor node drew a frame's
`DebugDraw`; and the only line shader a build could reach lived under `Editor/`, so a game could not
construct the renderer that drains the accumulator even if it wanted to. Physics, navigation,
animation, the AI stack and water's six `water.show*` verbs had all been writing into a list nothing
read.

That is this engine's commonest defect — a finished consumer that nothing feeds — and what closes it
is three joins, none of them a new abstraction:

| Join | Where |
|---|---|
| The instrument is where a game can reference it | `Vixen.Engine`, already |
| The framework emits, not the caller | `SceneRenderHost.Load` appends the node to every document it builds |
| The host owns the switch, and it is off by default | `GraphicsOptions.Overlays`, `--vixen-overlays` |

The same three that fixed the GPU profiler; see [timing the frame](timing-the-frame.md).

## Using it

`AppGraphics` builds exactly one of each and exposes them, because the way this feature fails when
two exist is an empty screen with every counter reading as though it had worked:

| Property | Is |
|---|---|
| `Graphics.Debug` | the `DebugDraw` every subsystem writes into |
| `Graphics.Overlays` | the panel registry |
| `Graphics.Console` | the command registry |

A game draws its own geometry straight into the accumulator, from anywhere, with nothing to create
and nothing to dispose:

```csharp no-compile="a fragment; `services` is the game's AppServices"
services.Graphics?.Debug?.Arrow(muzzle, muzzle + aim * 5f, new(1f, 0.3f, 0.2f, 1f), seconds: 1f);
```

Adding a panel of your own is one call, and the panel belongs to whoever has the numbers — which is
what `IDiagnosticOverlay` is for:

```csharp no-compile="a fragment of Game.OnInitialise"
services.Graphics?.Overlays?.Add(new AudioOverlay(audio));
```

⚠ **A subsystem's console verbs should arrive on their own.** `[ConsoleCommand]` alone has never made
a verb typable — the only thing that could find an attributed method is
`ConsoleCommands.RegisterFrom(Assembly)`, which is `RequiresUnreferencedCode` and had no callers, so
for a long time water's six were the only console verbs in the whole engine. The trim-safe seam is
a module initialiser beside the verbs:

```csharp no-compile="the shape Vixen.Rendering.Water uses; copy it"
[ModuleInitializer]
internal static void Register() => ConsoleCommands.Contribute(commands => MyDebug.Register(commands));
```

A contribution reaches consoles built before it as well as after, so it does not matter when the
assembly is first touched.

⚠ **The console reads no keyboard of its own.** `Type`, `Backspace`, `Submit` and the history moves
are pushed in by the host, because which device produces a character — and whether an IME is
involved — is a platform's question. `VixenApplication` answers it: characters come from
`PlatformEventKind.TextInput` and never from key codes, so the console types the right letters on a
non-US layout, and every key is swallowed while the panel is open so that typing `reload` does not
also make the player reload.

⚠ **Both `Key.Grave` and `Key.NonUsBackslash` open it, and the second is not a courtesy.** A scancode
is a position on a board, not a character: the key that types a backtick is below <kbd>Esc</kbd> on an
ANSI keyboard and beside left shift on an ISO one. Checking `Grave` alone opens the console for
nobody in Europe — which is exactly how the first run of this on a real machine reported "the key
does nothing" with every count reading correct.

⚠ **Text input is started only where the platform says it is off.** `ITextInput` is documented as
off by default; on SDL desktop it is already running and the characters arrive with nothing asked
for. The host therefore checks `IsActive` first, and stops only what it started — a browser canvas
or a phone, which is what the interface was drawn for, still gets its `Activate`.

⚠ **Ageing happens after the frame is recorded, and it is not a system.** `DebugDrawSystem` ages the
accumulator in `SystemPhase.PostRender`, which is after the drain only if the drain is itself a
system. `VixenApplication` runs every phase of `EngineLoop.Frame` and records the GPU frame
*afterwards*, so that system in that loop would delete each frame's lines one call before anything
drew them. `AppGraphics.AdvanceDebug` is the host's call instead, and it is already wired.

## Examples

**Where the geometry is drawn.** `DebugOverlayRenderer` declares a pass of its own over the frame's
last colour target, loading rather than clearing, so it lands after tone mapping and the screen
chain. Drawing it as a child of the final `!RenderPass` would put the panels *through* FXAA and the
grade. Nothing has to be added to a `.vxcompositor` for this — `SceneRenderHost.Load` appends the
node to whatever document it just built, including on a reload, so a project that authors its own
frame gets the overlays without declaring a node it never asked for.

**Untested against depth, deliberately.** By the frame's last colour target the scene's depth may
have been aliased away by the graph, and an overlay that could be occluded is one that disappears
exactly when something has gone wrong in front of it. World lines are therefore drawn over
everything. A viewport that wants them hidden by geometry draws them from inside the scene pass, the
way the editor's does.

**A line shader a game can reach.** `LineShaders.Default(device)` creates the two pre-compiled
modules embedded in `Vixen.Rendering` — the same bytes the golden `debug-world` and `debug-overlay`
images are rendered with. A project with its own Raven line stage builds `LineShaders` itself and
never calls it.

```csharp no-compile="a fragment; the host does this for you when Overlays is on"
var shaders = LineShaders.Default(device);
var node = new DebugOverlayRenderer(device, shaders, draw, view) { Target = "SceneColour" };

renderer.Host.Debug = node;
```

**What the panel's numbers mean.** Frame time is graphed and not only printed, because a mean of
16.7 ms and a mean of 16.7 ms with a 60 ms spike every second are the same number and completely
different games. The GPU figure is several frames old and says by how much; a dash means nobody
measured, which needs `--vixen-gpu-profile`.

## See also

* [Timing the frame](timing-the-frame.md) — the GPU profiler behind the panel's `gpu` row, and the
  same three joins that made it reachable.
* [Reading the frame](reading-the-frame.md) — what the counters on the panel are counting.
* [Booting an application](../engine/booting-an-application.md) — where `--vixen-*` flags are parsed
  and why a game's `OnConfigure` out-votes them.
