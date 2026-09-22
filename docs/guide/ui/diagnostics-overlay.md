---
title: The diagnostics overlay
slug: ui/diagnostics-overlay
kind: guide
area: Core
summary: A control that draws what the diagnostics panel says in rows — the four CSS boxes of the element under the pointer, and a wash over the regions the last pass invalidated — over the document it describes.
api: [T:Vixen.Ui.Controls.DiagnosticsOverlay]
tags: [ui, diagnostics, controls, overlay, layout, troubleshooting]
since: 0.2
status: preview
related: [ui/diagnostics-panel, ui/document-diagnostics, ui/desktop-application]
---

## What it is

`DiagnosticsOverlay` is the drawn half of doc 13's UI-debug view. It reads the same
[aggregator](document-diagnostics.md) the [panel](diagnostics-panel.md) does and puts the answer
where the answer is: four nested outlines round the element under the pointer — margin, border,
padding and content, in the colours every browser's inspector uses for the same four boxes — and a
translucent wash over each region the last pass invalidated.

```csharp compile
using Vixen.Ui;
using Vixen.Ui.Controls;

public static class DebugView {
    public static void Show(UiDocument document) {
        // A root child, because its outlines are in this document's coordinates. The theme rule puts
        // it over the whole surface and makes it transparent to the pointer.
        document.Root.Add<DiagnosticsOverlay>();
    }
}
```

## What it is for

"Why is this box this size" and "what am I looking at" are questions a number answers exactly and a
picture answers first. The panel reports the content box as `12, 40 · 100 × 24`; the overlay draws
it round the thing, so a margin that is not where it was expected, a padding of zero where a theme
was meant to apply one, or a border box wider than its parent is visible rather than arithmetic.

⚠ **It goes in the document it describes, and the panel's advice is the opposite.** The panel is
*exact* when it is not in the document it counts, because writing a row moves the counters the row
reports. The overlay has no such cost — it draws and never writes — and its coordinates are the
subject's, so an overlay in another document would draw the boxes somewhere else. That is why they
are two controls: the numbers go in a tools window and the picture goes over the thing.

## Using it

### Place it over the surface

The theme rule makes it `position: absolute`, inset to every edge, and `pointer-events: none`. The
last is load-bearing rather than tidy: the overlay draws the element under the pointer, so it must
never be that element itself — a hit test that landed on it would outline its own full-surface box
for ever.

### The element it draws

With no `Probe`, the subject's own `UiDocument.Hovered` is the element — the same answer the
document's hit test gave the last pointer event, so it needs no wiring at all. Setting `Probe` names
a point instead, for a host that wants one that is not the pointer's: a pinned element, or a
coordinate typed in.

### The regions

`ShowsRegions` washes what the last pass invalidated, by place. ⚠ **A cold pass is recorded as the
root's own box and is deliberately not washed** — a wash over the whole document says nothing about
*where* and hides everything about *what*. The panel's `Dirty regions` row still counts it.

⚠ **The recording is behind `DEBUG` and `VIXEN_UI_DIAGNOSTICS`.** In a build without either there is
nothing to draw, `Regions` reads zero and the overlay is only the box model — see
[`UiDiagnostics.RecordsRegions`](document-diagnostics.md).

### The colours

Five theme tokens, so a dark palette can lift them:

| Token | What it outlines |
|---|---|
| `--diagnostics-margin` | The margin box |
| `--diagnostics-border` | The border box, which is `UiElement.Bounds` |
| `--diagnostics-padding` | Inside the borders |
| `--diagnostics-content` | Inside the padding: where the words go |
| `--diagnostics-dirty` | The invalidated-region wash |

The outlines are drawn outermost first, so where two boxes coincide — an element with no margin, no
border or no padding, which is most of them — the innermost one is on top and the reading is
"content".

## Examples

The panel and the picture together, which is how the editor and `Samples/02-HelloUi` both do it:

```csharp no-compile="A fragment: `document` is the document being debugged and `host` is whatever holds the panel."
var overlay = document.Root.Add<DiagnosticsOverlay>();   // over the document
var panel = host.Add<DiagnosticsPanel>();                // the rows, wherever they fit

// The panel's half the host has to arrange; the overlay needs nothing per frame.
application.Diagnostics = panel;
```

Reading a box model on a document that is animating, where the wash would cover what is being looked
at:

```csharp no-compile="A fragment: `overlay` is the control above."
overlay.ShowsRegions = false;
```

## See also

- [The diagnostics panel](diagnostics-panel.md) — the same facts as rows, and the refresh rule.
- [Document diagnostics](document-diagnostics.md) — the aggregator both of them read.
- [Desktop applications](desktop-application.md) — where `UiApplication.Diagnostics` is refreshed.
