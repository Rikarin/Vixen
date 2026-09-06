---
title: The diagnostics panel
slug: ui/diagnostics-panel
kind: guide
area: Core
summary: A control that shows what a running document is doing — the per-frame work counters, whether the last pass was cold, the draw-list rebuild gap, and the element under a probe point with its four boxes — and why it is a control rather than one of the engine's diagnostic overlays.
api: [T:Vixen.Ui.Controls.DiagnosticsPanel]
tags: [ui, diagnostics, controls, overlay, performance, troubleshooting]
since: 0.2
status: preview
related: [ui/document-diagnostics, ui/key-value-list, ui/desktop-application]
---

## What it is

`DiagnosticsPanel` is a control that reads a `UiDocument`'s
[diagnostics](document-diagnostics.md) and shows them as rows. It is the reader half: the aggregator
answers what a document did, and this puts the answer on screen without the caller writing a dozen
`AddRow` calls and a pooling loop.

```csharp compile
using Vixen.Ui;
using Vixen.Ui.Controls;

public sealed class DebugWindow {
    readonly DiagnosticsPanel panel;

    public DebugWindow(UiDocument tools, UiDocument subject) {
        panel = tools.Root.Add<DiagnosticsPanel>();

        // The document being described, which does not have to be the one the panel is in.
        panel.Subject = subject;
    }

    // ⚠ At the TOP of the frame, before the document restyles. See below.
    public void BeginFrame(float pointerX, float pointerY) {
        panel.Probe = new(pointerX, pointerY);
        panel.Refresh();
    }
}
```

## What it is for

Answering "why is this interface slow" and "what am I looking at" without a debugger. `docs/plan/13`
calls a UI-debug view "the single most valuable tool for anyone building a UI in this framework"; the
rows here are its readable half — the per-frame work counters, whether the last pass was cold, the
gap between draw lists built and draw lists changed, and the element under a point with its four CSS
boxes.

⚠ **A control, not an `IDiagnosticOverlay`, and the seam is the reason.** `GpuOverlay` and
`StreamingOverlay` report a frame the interface knows nothing about, so they live where the frame
lives. Every number here is *about* a `UiDocument` — and the one host whose whole job is drawing one,
`UiApplication`, deliberately cannot see `Vixen.Engine`, while the tree's only production holder of a
`DiagnosticOverlays` cannot see `Vixen.Ui`. An overlay written for this would have been registered
nowhere. A control works in `UiApplication`, in the editor, and in any game that draws a document.

## Using it

### Refresh at the top of the frame

Nothing here is pushed and there is no timer. The panel reads when it is told to, because a surface
that subscribed to the document would be touching the single-threaded reactive graph to ask a
question about it.

⚠ **The numbers are a frame old, and that is the honest arrangement rather than a bug.** A panel
drawn into the document it describes *is* part of that document: writing a row adds elements, which
moves `StylesApplied`, `LayoutNodes` and the settling counters the row is reporting. Refreshing at
the top of the frame — before the document restyles — reports what the previous pass finished with,
which is self-consistent. Refreshing in the middle of a pass reports a document half way through
changing, including the panel's own churn.

### Point it at another document for an exact reading

`Subject` defaults to the panel's own document, which is what somebody debugging reaches for first
and is consistent to within the panel's own cost. A second `UiDocument` on its own surface removes
even that: the panel is not in the tree it measures, so the counters are exact.

```csharp no-compile="A fragment: `panel` and `game` are objects the caller already has."
// Exact rather than merely consistent — the panel is not part of what it is counting.
panel.Subject = game;
```

### The probe

`Probe` is a point in the subject's coordinates, not an element, because the question is "what is
under the pointer". Set it and the rows gain the element's tag and its margin, border, padding and
content boxes; clear it and they go away rather than going stale — a panel that kept describing the
last element the pointer crossed would be lying about a document whose layout has since moved.

### Reading the rows

| Row | What it says |
|---|---|
| `Layout nodes` | How big the layout tree is |
| `Styles resolved`, `Styles applied` | The restyle's work for the pass |
| `Container scopes`, `Style compactions` | Container queries entered, and cascade compaction |
| `Settling passes`, `Settled` | How many passes the frame needed, and whether it reached a fixed point |
| `Last pass` | `cold` or `incremental` |
| `Draw lists built`, `Draw lists changed` | Rebuilds, and the ones whose drawing differed |
| `Dirty regions`, `Regions recorded` | What invalidated the pass, when this build records them |

⚠ **`Last pass` is the row to read first.** One element moved and the whole document re-cascaded is a
defect rather than a cost, and it is invisible in any total: a cold pass and a busy incremental one
are both a large `Styles resolved`.

⚠ **`Dirty regions` says "not recorded in this build" rather than showing a zero**, because "nothing
was invalidated" and "nobody was recording" are the same empty span. The recording is behind
`DEBUG` and `VIXEN_UI_DIAGNOSTICS`; a panel that printed `0` for both would report success on the day
it did not run.

## Examples

A tools window describing the application's own document, refreshed once per frame:

```csharp no-compile="A fragment: `tools` and `app` are documents, and `frame` is the host's per-frame hook."
var panel = tools.Root.Add<DiagnosticsPanel>();
panel.Subject = app;

frame.Begin += () => {
    panel.Probe = app.Pointer;   // or null, when the pointer is outside
    panel.Refresh();
};
```

In markup, as an ordinary tag, with the host doing the refresh:

```vxml no-compile="a fragment; the host is what calls Refresh"
<Panel class="debug">
    <DiagnosticsPanel ref="diagnostics" />
</Panel>
```

## See also

- [Document diagnostics](document-diagnostics.md) — the aggregator this reads, and the three rules
  that decide its shape.
- [Key-value list](key-value-list.md) — the pooled rows the panel is built out of.
- [Stylesheet diagnostics](stylesheet-diagnostics.md) — the other half of "why does this look wrong":
  declarations the cascade refused.
