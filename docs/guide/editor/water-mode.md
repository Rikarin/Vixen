---
title: Water mode
slug: editor/water-mode
kind: guide
area: Editor
summary: One mode and three verbs — draw a body's curve on the ground, drag its profile handles, and preview what its carve did to the terrain — plus the zone panel that turns a resolution into metres.
api: [T:Vixen.Editor.Water.WaterMode, T:Vixen.Editor.Water.WaterEdit, T:Vixen.Editor.Water.WaterTool, T:Vixen.Editor.Water.WaterHandle, T:Vixen.Editor.Water.WaterZoneSettings, T:Vixen.Editor.Water.WaterBodySettings, T:Vixen.Editor.Water.WaterModule, T:Vixen.Editor.Water.WaterProfileCommand, T:Vixen.Editor.Water.WaterCarveCommand, T:Vixen.Editor.SceneView.IWaterScene, T:Vixen.Rendering.Water.WaterDebug, T:Vixen.Rendering.Water.WaterOverlay, T:Vixen.Rendering.Water.WaterMeshOverlay, T:Vixen.Rendering.Water.WaterStatistics, T:Vixen.Rendering.Water.WaterMeshStatistics, T:Vixen.Editor.Assets.Water.WaterWavesImporter, T:Vixen.Editor.Assets.Water.WaterWavesImportSettings]
tags: [editor, water, mode, river, lake, undo]
since: 0.1
status: preview
related: [editor/modes, editor/terrain-mode, engine/water-surface, engine/splines]
---

## What it is

The viewport mode water is authored in, and the smallest of the editor's toolsets by a long way.

Doc 31 needed a sculpt mode *and* a foliage mode because each owns the viewport and has an
incompatible idea of what a click means. Water needs one, and the reason it needs even that is short:
**placing a lake is placing an entity, editing its shape is editing a spline**, and the editor already
does both. What is left is the three things that are neither.

| Verb | What it is | Key |
|---|---|---|
| **Draw** | Click points on the ground to lay a curve at the ground's own height — closed for a lake or an ocean, open for a river | `1` |
| **Profile** | Drag the width handles either side of the curve, and the depth handle down | `2` |
| **Preview** | Toggle the reserved layer's contribution, to see what the water did to the ground | `3` |

## What it is for

Authoring a lake, a river running into it and an ocean beyond, without text editing — which is
`docs/plan/35`'s W9 exit criterion, tested by a session that saves the scene, reopens it and finds the
tools bound.

The mode is also where the zone gets placed, and a zone is not optional: **a body with no zone is a
body nothing has rasterised.** That is Unreal's rule kept, for the reason § D3 gives — the field is
the interchange every consumer reads. What is *not* kept is discovering it from a blank frame:
`stat water` draws `zoneless` in red, and `Create zone` is on the panel and is reachable from any mode.

## Using it

### Drawing a body

```csharp no-compile="what the gesture does, not a compiling editor"
mode.Editing.Kind = WaterBodyKind.River;   // ⚠ decides whether the curve closes

mode.Editing.Add(new(0f, 8f, 0f));         // the ground's own height, not a flat plane
mode.Editing.Add(new(40f, 6f, 10f));

mode.Finish();                             // raises Drawn(spline, kind)
```

⚠ **The points carry their height, and that is the whole difference between a river and a bent
lake.** A river's surface *is* its curve; one laid at a single height is a canal drawn in a curve.

⚠ **Clicking the first point again closes a lake**, because the UI layer has no double click:
`PointerAction` is moves, presses and releases, and a click count is a fact about time the event does
not carry. Enter finishes as well, and is the only way to finish a river — an open curve has no first
point to come back to.

⚠ **Three points for a closed body, two for a river.** Finish is greyed until there are enough,
because a lake built from two points is a curve `WaterBody`'s constructor refuses — and an author
would meet that as an exception dialog rather than as a disabled button.

⚠ **Two clicks in the same place are one point.** A spline segment of no length has no tangent, so a
body built from one has a boundary walk that divides by zero. The spacing is measured on the ground
plane and not in three dimensions: a river down a cliff has two points a metre apart horizontally and
twenty vertically, and a three-dimensional test would accept them.

### The profile handles

Both width handles edit the **same** number, and the sign is the difference — a river's channel is
symmetric about its centreline, so two handles is two grips on one value. Two independent half-widths
would be a second number to author and a river whose centreline is not its centre.

⚠ **The side is the curve's own frame and not world X.** A river that bends would otherwise have its
handles cross its own bank halfway round, and dragging one would widen the channel in the wrong
direction — which is what makes a viewport handle worse than a number field.

⚠ **A width cannot be dragged below zero.** A negative half-width inverts the containment test, so
the body covers everywhere *except* itself: the whole zone floods, and it reads as a renderer bug.

### The zone panel

The derived half is the point of it. A resolution is meaningless and a metre per texel is not.

| Readout | Why it is there |
|---|---|
| Sea state | Which spectrum this zone actually draws — the named `.vxwaves` or the panel's own |
| Metres per texel | Whether a shoreline can be resolved at all — a two-metre falloff at two metres a texel is neither a ramp nor a cut |
| Info texture | What a resolution costs, in megabytes, before somebody types one |
| Vertices, full window | What the surface mesh draws at its finest, which is one vertex per texel by construction |
| Height quantum | What half precision does to a horizon, stated in centimetres rather than left to be discovered as a stepped sea |
| Maximum amplitude | It decides the node error metric, the far-mesh cut *and* the collision bounds |

⚠ **Naming a `.vxwaves` does not grey out the four sea-state fields below it.** The inline spectrum
is what the zone falls back to while the asset loads and what it keeps if the name is wrong, so a
panel that hid it would be hiding the sea that is actually on screen. `stat water`'s `no waves` row —
in *warning* rather than in red, because that water is drawing — is what says which one won.

⚠ **Create ▸ Sea state writes the panel's current spectrum, not the default one.** An author who has
spent a minute finding a sea they like and then presses it means *that* sea; a template that threw it
away would make the shared asset the slower route to the thing they already had.

⚠ **The arithmetic is the kernel's.** `WaterZone.MetresPerTexel`, `Bytes` and `HeightQuantum` are the
same properties the renderer sizes its texture from, and `Validate` is the same rule it refuses by —
so the panel cannot be right about a configuration the renderer rejects.

### ⚠ The curve is written to disk, and an unsaved scene refuses the draw

A body names its curve by **name**, not by handle — a handle names a slot in a world that issued it,
and a scene file is read by a world that has not run yet. So the module writes a `.vxspline` beside
the scene and the entity names it.

Which means a scene that has never been saved has nowhere to put one, and the draw is **refused**
rather than half-done. An entity naming a spline nothing can supply loads, resolves to nothing, counts
into `WaterZoneSystem.UnresolvedBodies` and draws no water — the author would be looking at a lake in
the outliner and dry ground in the viewport.

### What the viewport shows

The pane draws the water the mode paints, and it does it by running the game's own fold.
`WaterModule` contributes an `IWaterScene` — three questions the fold cannot answer for itself, which
are what a *name* means — and the presenter hands them to a `WaterZoneSystem` it folds over the scene
document's world.

| Question | What the module answers with |
|---|---|
| `SplineFor` | The `.vxspline` beside the scene, re-read when its timestamp moves |
| `SpectrumFor` | The `.vxwaves` the zone names, or null to fall back to the inline spectrum |
| `GroundAt` | A flat plane at zero — the module may not reference the terrain one, and either may be absent |

⚠ **The fold is `WaterZoneSystem.Fold(World)` and not a second implementation of it.** § D2 is a rule
about hosts and the editor is one: a second fold would be a second opinion about where the shoreline
is, and it would agree with the game's until the frame it stopped. The surface itself is evaluated on
the CPU by the same `WaterQuery` a game's vertex stage samples, so a grid over the window is the
picture the game will draw rather than an impression of it — which is the opposite call from the
grass, where a CPU preview of a hundred thousand blades would have been a lie.

⚠ **The preview is translucent and it writes depth, so it is recorded last of the solid geometry.**
The ground under a lake has to be in the target before the water blends over it; and something under
the surface has to lose the depth test to it, which is what makes a submerged object read as
submerged.

⚠ **A zone whose spectrum cannot be summed draws a still sea rather than failing.** A zone authored in
a file that never wrote a `waves:` block holds a *zeroed* spectrum, whose minimum wavelength is zero
and whose dispersion relation therefore divides by nothing. That is the ordinary state of a lake
somebody has just placed, and the runtime's own answer for it — `WaterMeshRenderer` sums zero waves —
is flat.

### Debugging

Doc 35 copies Unreal's debug surface on purpose: `stat water` and `stat watermesh` are better than most
first-party debug tooling in any engine, and there is no credit in inventing worse ones.

| Command | What it says |
|---|---|
| `stat water` | Zones, bodies, and — in red — `zoneless` and `unresolved`, which have different fixes |
| `stat watermesh` | Zones drawn, patches, vertices, draws, and `dropped` in red |
| `water.showTiles`, `water.showLod`, `water.showInfo`, `water.showFlow`, `water.showBuoyancy`, `water.showRipples` | Flags a renderer reads |

The six `show` verbs are registered by `WaterModule` under those exact names, so the command palette
finds them — **the command palette is the editor's console**, and a different id there would mean the
sentence above matched neither. `WaterDebug.Register(ConsoleCommands)` is the same six for a game,
without the reflection the `[ConsoleCommand]` attributes would otherwise need.

⚠ **`water.showFlow` is the one of the six a pane draws today.** Tiles and LOD bands describe the
patches a *device* selected and ripples a simulation only a game runs; the editor's preview surface is
a CPU grid with none of the three. They are registered anyway, so that the set an author sees does not
depend on which host they are in. `water.showInfo`'s channel charts are screen-space and a pane has no
screen-space debug pass to drain them into.

⚠ **`stat water` and `stat watermesh` still have no host.** `WaterOverlay` and `WaterMeshOverlay` are
`IDiagnosticOverlay`s and nothing in the tree constructs a `DiagnosticOverlays`, a `ConsoleCommands` or
a `DebugDraw` outside its own tests — and no compositor node draws a frame's `DebugDraw`. That is doc
13's host wiring rather than water's, and it is missing for `FrameStatsOverlay` and `AudioOverlay`
equally.

⚠ **Two diagnostics rather than one, because the fixes differ.** A zoneless body is a zone's extent;
an unresolved one is an asset name or an asset that has not loaded. One number for both sends an
author to the wrong place.

The six `show` verbs draw through `DebugDraw`, which turned out to be the seam water was said to be
missing: it is an *accumulator* rather than a renderer, so `Vixen.Rendering.Water` draws into it
without knowing what a line pass is.

⚠ **Five of the six are `WaterDebugDraw`'s and the sixth is `BuoyancyDebugDraw`'s.** The pontoons and
the forces belong to `Vixen.Water.Physics`, which the renderer must not reference — § D1 puts the
physics join in its own assembly precisely so that nothing linking Jolt is linked by a renderer. The
flag is with the console verb; the drawing is with the data, and a host copies one across.

⚠ **`showTiles` colours a patch by what is under it, using the contribution and not the containment
test.** `WaterBody.Contains` is an even-odd test on a closed boundary and answers *false* for every
open body — so a colour rule written on it paints every river as open sea, which is precisely the case
the verb exists for.

⚠ **`showLod` draws two rings per level and not one.** A level's range is where it takes over; its
morph band is where it has already begun degenerating onto its parent's grid. A pop at the outer ring
is a range that is too near; one inside the band is a morph that is not reaching zero, and they have
different fixes.

## Examples

**A lake, drawn.** Four clicks and a fifth on the first point.

```csharp no-compile="the gesture, not a compiling editor"
var mode = new WaterMode { Document = scene };

mode.Editing.Kind = WaterBodyKind.Lake;
mode.Editing.Add(new(0f, 4f, 0f));
mode.Editing.Add(new(20f, 4f, 0f));
mode.Editing.Add(new(20f, 4f, 20f));
mode.Editing.Add(new(0f, 4f, 20f));

if (mode.Editing.ClosesAt(new(0.4f, 4f, 0.4f))) {
    mode.Finish();          // a closed curve, and the module writes it beside the scene
}
```

**A zone, placed.** Without one nothing renders.

```csharp no-compile="the panel's button, as a call"
mode.Zone.Extent = 512f;
mode.Zone.Resolution = 257;     // ⚠ a power of two *plus one* — see the readout

foreach (var (label, value) in mode.Zone.Facts()) {
    Console.WriteLine($"{label}: {value}");
}

mode.CreateZone();
```

**A handle drag, as one undo entry.** `WaterProfileCommand` holds the value before and the value after
rather than a delta — a delta applied twice is a profile that drifts, and an undo stack is replayed in
both directions.

## See also

- [Modes](modes.md) — what a mode is, and why `Register` and `Activated` are different moments.
- [Terrain mode](terrain-mode.md) — the ground a body is drawn on and carves into.
- [Where the water surface is](../engine/water-surface.md) — the kernel this edits.
- [Splines](../engine/splines.md) — the curve a body is, and the same one a road is.
- `docs/plan/35-water.md` § Part 2 — the authoring surface this implements, and § W9's exit criteria.
