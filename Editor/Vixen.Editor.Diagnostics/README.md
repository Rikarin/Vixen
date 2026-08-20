# Vixen.Editor.Diagnostics

The module that points doc 20's E4 panels at a project, a scene and a graphics device: the profiler,
the GPU timeline, memory, statistics, the frame debugger, the remote inspector and the device
manager — and doc 16's network panel at whatever session is running.

Spec: [docs/plan/20](../../docs/plan/20-editor-parity.md) § E4,
[docs/plan/36](../../docs/plan/36-an-extensible-editor.md) § P3.

```csharp
plugins.Activate(DiagnosticsModule.ModuleId, DiagnosticsModule.ModuleName, new DiagnosticsModule());
```

## Why this is a third assembly

`Vixen.Editor.Profiler` and `Vixen.Editor.Debugger` have never heard of a project, a scene or a
graphics device. That is deliberate and it is what lets both be tested against a bare `UiDocument` —
their own remarks say so.

⚠ **So the joining code could not move into either of them.** Doc 36 § P3 asks the built-in features
to register through the plugin API rather than being wired into the application; for these two,
doing that by putting `EditorDiagnostics.cs` inside one of them would have bought the registration
and spent the testability. A module is what a feature looks like when its parts are deliberately
ignorant of each other.

What this assembly decides: that the statistics panel counts *this* world, that the GPU timeline
reads *this* device, that the frame debugger captures *this* frame, that the remote inspector talks
over loopback, and that the network panel reads *this* session's ledger.

## What it asks the host for

| | |
|---|---|
| `EditorProject` | the memory panel's asset counts |
| `IActiveScene` | ⚠ **which scene is being *shown***, which is not which scene is open — an editor inspecting a prefab must count the prefab, or Refresh reports the level behind it |
| `IDeviceDeploy` | optional. Building a player is a project, a target, a content build and a process; a host with none greys Deploy with a sentence rather than hiding the panel |

The graphics device, the resolved GPU frame and the frame-capture source arrive later still — only a
host with a window and a device can supply them, and they are settable properties for that reason.

So are `NetworkLedger`, `NetworkRegistry` and `NetworkSnapshot`, and their absence is the ordinary
case rather than a degraded one: a `BandwidthLedger` belongs to whatever built the `ReplicationServer`
and the `RpcRouter`, and an editor that has not started a session has none. The panel says so rather
than drawing zeroes — a table of zeroes reads as a game sending nothing, which is the bug somebody
would have opened it to find.

⚠ **The last snapshot's bytes are the host's too, because `ReplicationServer` does not keep them.** It
writes each connection's into a caller's buffer and forgets it; which connection is worth inspecting
is a question only a game can answer. `GameServer.LastSnapshot` in `Samples/08` is a game holding on
to one for exactly this purpose.

## What stayed in the application

The **diagnostics report**, because it is the editor reporting on itself: the project's name, the open
scene's counts, the memory arenas, the log ring and the last profile capture. Four of those five are
the application's. It asks this module for the fifth.

And **`EditorKeys`** — the profiling scopes the editor's own frame loop is measured in. Those are the
host's four steps, not this module's panels.

## Tests

Through `Vixen.Editor.App.Tests`, because what is worth asserting is that the panels open, survive
being closed and reopened, and are pointed at the right thing — all of which is a claim about a whole
editor. The two assemblies underneath have their own suites against a bare `UiDocument`.
