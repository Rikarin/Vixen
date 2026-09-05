---
title: Document diagnostics
slug: ui/document-diagnostics
kind: guide
area: Core
summary: What a debug overlay may read about a running interface — the per-frame work counters, an element's four boxes under the pointer, and the regions that invalidated the last pass — with the three rules that decide the shape: it reads rather than samples, it allocates nothing, and the region recording is compiled out of a build that did not ask for it.
api: [T:Vixen.Ui.UiDiagnostics, T:Vixen.Ui.UiBoxModel, T:Vixen.Ui.UiDirtyRegion, T:Vixen.Ui.UiInvalidationKind]
tags: [ui, diagnostics, overlay, performance, troubleshooting, layout]
since: 0.2
status: preview
related: [ui/stylesheet-diagnostics, ui/desktop-application]
---

## What it is

`UiDocument.Diagnostics` is one read-only view over what a document's passes already publish: how
much work the last frame did, what the layout tree holds, which element is under a point and what
its four boxes are, and what invalidated the pass that has just run.

```csharp no-compile="A fragment: `document` is a UiDocument the caller already has, and `x`/`y` are the pointer's."
var diagnostics = document.Diagnostics;

// The frame's work. `LastPassWasCold` is the row worth a colour: one element moved and the
// whole document re-cascaded is a defect no other number here shows.
var resolved = diagnostics.StylesResolved;
var cold = diagnostics.LastPassWasCold;

// The element under the pointer, and the four boxes an overlay draws as nested outlines.
if (diagnostics.TryDescribe(x, y, out var element, out var boxes)) {
    Outline(boxes.Margin);
    Outline(boxes.Border);
    Outline(boxes.Padding);
    Outline(boxes.Content);
}

// And what made the last pass necessary. Empty is a real answer; see below for the other one.
foreach (var region in diagnostics.DirtyRegions) {
    Highlight(region.Bounds, region.Kind);
}
```

## What it is for

The UI-debug overlay `docs/plan/13-diagnostics.md` calls "the single most valuable tool for
anyone building a UI in this framework" — element bounds, layout boxes, style origin for a hovered
element, a dirty-region highlight. It is equally what a test or a benchmark asserts against: every
counter here is a deterministic count of work, which is what this repository prefers to a wall-clock
budget.

⚠ **It is an aggregator, not an instrument.** Nearly everything it exposes was already published by
the pass that computes it — `StylesResolved` by the restyle, `NodeCount` by the layout tree,
`HitTest` by the document — one property at a time across the partials that produce them. What did
not exist before is the dirty-region recording: the invalidation paths recorded *that* something
changed and never what or where.

## Using it

### The three rules

**It reads; it never samples.** `Vixen.Ui`'s reactive graph is single-threaded by contract, so a
diagnostics surface able to touch a signal to answer a question could perturb the document it is
describing. Every member is a field read or a walk of results that already exist, and none of them
marks anything dirty.

**Nothing on the read path allocates.** An overlay is on for minutes at a time in the frame it is
diagnosing, so a surface that allocated per read would be measuring itself. `UiDiagnostics` is a
struct over the document, the regions come back as a `ReadOnlySpan<UiDirtyRegion>`, and there is no
list or string anywhere in it — `DiagnosticsTests.Reading_the_diagnostics_allocates_nothing` states
that as zero rather than as a ceiling.

**The regions are compiled out of a build that did not ask for them.** Recording a box per
invalidation is work on a frame with a debugger attached and waste on every other one, so the
recording sits behind `[Conditional("DEBUG")]` and `[Conditional("VIXEN_UI_DIAGNOSTICS")]` — the
shape `Vixen.Ecs` uses for its structural events. In a build without either symbol the call site is
gone entirely.

### The two empty answers, and why one constant exists

`DirtyRegions` is empty when nothing was invalidated **and** when nothing was recording, and those
are different facts:

```csharp no-compile="A fragment: `diagnostics` is a UiDiagnostics the caller already read."
if (!UiDiagnostics.RecordsRegions) {
    // Not "the interface is idle" — "this build does not record".
    Say("dirty regions: not recorded in this build");
}
```

⚠ **A panel that cannot tell the two apart reports success on the day it does not run**, which is
the failure mode this codebase keeps meeting. `RecordsRegions` is a `const`, so it costs nothing to
test and the public surface is identical in every configuration — `CheckApi` baselines Release, and
what the flag removes is the recording rather than the ability to ask.

⚠ **And a frame that found nothing to do empties the regions.** A settled document was invalidated
by nothing, so `DirtyRegions` is empty rather than still showing the last real pass's boxes — the
same honesty `Update`'s own counters had to learn, having once reported a few hundred resolved
styles for ever on a document nobody was touching. A dirty-region highlight is therefore drawn in
the frame that did the work, which is the frame it is about.

### What the kinds mean

`UiInvalidationKind` is the distinction the restyle itself draws, because it is the one that decides
the cost:

| Kind | What it reaches |
|---|---|
| `Class` | whatever a selector says — a class can reach a sibling, so the pass collects a batch |
| `State` | the same, for hover, focus and disabled |
| `Inline` | the element's own subtree and nothing else: no selector in this engine can see an inline declaration |
| `Document` | everything. The next pass is a cold one over the whole tree |

A `Document` region on a frame where one thing moved is the shape of a real defect — it is what a
virtualised list scrolling used to cost before an inline write became narrowable.

### What it does not answer

**Which rules matched an element.** The cascade carries provenance (`StyleOrigin`,
`CascadePrecedence`, `StyleRuleSet.Origin`), so doc 13's "style origin for a hovered element" is
close to free — but reading the winning rule *per property* is a query the styling engine does not
offer yet, and inventing one here would mean re-matching selectors on the read path, which the first
rule above forbids.

**Anything about drawing.** These are the document's numbers, not the renderer's: what a frame cost
on the GPU is `GpuOverlay`'s subject.

## Examples

### The box model

`UiBoxModel` is four rectangles rather than twelve edges, because four nested outlines is what an
overlay draws and the arithmetic that turns edges into them is the part that is easy to get wrong
once per overlay.

⚠ **`Border` is `UiElement.Bounds`, and the declared `width` is the *content* box.** This engine
follows CSS's initial `box-sizing: content-box`, which is one of the four places CSS's initial values
differ from the layout library's own — so a `box { width: 40px; padding: 3px; border-width: 2px }`
has a 40-pixel content box and a 50-pixel border box.

```csharp no-compile="A fragment: `document` is a UiDocument the caller already has, and `Row` is the caller's own way of putting a line on screen."
// A panel of the frame's work, which is the overlay doc 13 asks for at its plainest. Every number
// is a count rather than a duration, so it reads the same on an idle laptop and a loaded runner.
var diagnostics = document.Diagnostics;

Row("elements cascaded", diagnostics.StylesResolved);
Row("styles applied", diagnostics.StylesApplied);
Row("layout nodes", diagnostics.LayoutNodes);
Row("settling passes", diagnostics.SettlingPasses);
Row("cold pass", diagnostics.LastPassWasCold ? "yes" : "no");

Row(
    "dirty regions",
    UiDiagnostics.RecordsRegions ? diagnostics.RegionsRecorded.ToString() : "not recorded"
);
```

⚠ **`RegionsRecorded` and `DirtyRegions.Length` are different numbers on the frame worth looking
at.** The ring holds a bounded number of boxes on purpose — a document restyled two thousand times
in one pass is exactly what somebody opens this overlay to see, and a list that grew to hold all of
it would allocate in the frame being diagnosed. The count is of what was recorded; the span is of
what fitted.

## See also

- [Stylesheet diagnostics](stylesheet-diagnostics.md) — the other half of "why does this element
  look like that": the declarations the cascade refused, and where each refusal is reported.
- [Docking panels](docking-panels.md) — the panel a debug overlay is usually docked into, and its
  own scrolling contract.
- `Core/Vixen.Ui/README.md` § Diagnostics — the argument behind this shape, and the table of what
  each pass already published before it existed.
- `docs/plan/13-diagnostics.md` § Diagnostic overlays — the panel this exists to unblock, and the
  rule that an overlay is registered only where the host holds the object its numbers come from.
