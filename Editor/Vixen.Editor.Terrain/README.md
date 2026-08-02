# Vixen.Editor.Terrain

The editor half of [docs/plan/31 § T3](../../docs/plan/31-terrain-grass-and-trees.md): the viewport
mode the sculpt tools live in.

**The mode, the settings the terrain panel is made of, the stroke and layer commands, and the editing
state that turns a drag into one undo entry.** The arithmetic under all of it is
[`Core/Vixen.Terrain`](../../Core/Vixen.Terrain/README.md) — this assembly is what turns a pointer
into a stamp, a stamp into a kernel call, and a drag into one entry in the undo history.

```csharp
shell.Modes.Add(new SelectMode());
shell.Modes.Add(new TerrainMode { Document = scene, Editing = { Colliders = physics } });
```

That is the whole of the registration. The mode bar, the palette entries, the radio state and the
keymap all follow from it — see the [editor modes guide](../../docs/guide/editor/modes.md).

## What it owns

| | |
|---|---|
| `1`…`8` | sculpt, smooth, flatten, ramp, erosion, hydro, noise, holes — in the `terrain` context |
| `[` `]` | the brush radius, **multiplied** by an eighth rather than stepped by a metre |
| `-` `=` | the brush strength, stepped by 0.05 |
| `Shift`+drag | inverts: sculpt lowers, noise subtracts, holes fill in |
| `Escape` | abandons a stroke in flight, and nothing else |
| The mode bar's second strip | the eight tools as one segmented control |
| The terrain panel | create / manage, the edit-layer stack, the brush section, the tool parameters |

⚠ **The keys are doc 24's B2 a second time, and that is the point.** `1`…`8` are view-bookmark recall
everywhere else; these commands declare `Context = TerrainMode.TerrainContext` and the bookmarks
declare none, so `KeyMap` files the two under different contexts. A second consumer of the mode seam
took no new machinery, which is the claim the seam was built to make.

## The panel is settings objects, not dialog code

[Doc 31 § Part 2](../../docs/plan/31-terrain-grass-and-trees.md)'s bargain, which is
[doc 20 § B6](../../docs/plan/20-editor-parity.md)'s for world settings: every row is an
`[Inspector]` member of a `[DataContract]` type. `TerrainCreateSettings`, `TerrainBrushSettings` and
`TerrainToolSettings` are the whole of it, and all three are testable with no window.

⚠ **The create form shows what it costs while it is being filled in.** `TerrainFacts` is the extent,
the sample count, the height and weightmap storage, the number of collision shapes and the vertical
precision — every one labelled `(derived)`. This is the dialog where a person accidentally asks for
eight gigabytes: four numbers that each look reasonable multiply into a terrain nothing can load.

⚠ **The vertical precision is on the form because the height range is authored.** Unreal fixes the
range and nobody has to think about it; [§ D2](../../docs/plan/31-terrain-grass-and-trees.md) lets the
author set it, which buys a 40 m landscape 0.6 mm of precision instead of 8 mm — and makes it possible
to ask for a 20 km range and wonder later why a flatten will not settle.

⚠ **The tile size is offered in quads and stored in samples.** An artist reads 63 / 127 / 255; Jolt
requires a power-of-two *sample* count and refuses anything else by returning nothing at all. The two
differ by one and the form is where the translation happens, once.

## One drag, one entry

[§ D11](../../docs/plan/31-terrain-grass-and-trees.md). `TerrainEdit.Begin` starts a
`TerrainStroke`, every `Extend` records the ground before it writes and applies the kernel
immediately, and `Commit` hands back one `IEditorCommand`. **Merging is off** — two strokes are two
undos, which is what an artist means and what every paint application does. What merges is *inside*
the stroke: a drag is one record being extended rather than four hundred commands.

⚠ **The brush is snapshotted at `Begin` and never read again.** The panel can move while a drag is in
flight — a pen's barrel wheel, a key, another window — and a stroke whose radius changed halfway has
an undo record sized to a footprint that no longer matches what it wrote.

⚠ **The composite is resolved per stamp, and the colliders are rebuilt once.** The first is what makes
the ground move under the brush instead of jumping at pointer-up; the second is because a stroke marks
the same one or two tiles over and over, and building a Jolt height field per stamp is the version of
this that stutters.

⚠ **Holes get their own stroke type, because a hole is not a delta.** The seven sculpt tools write
signed offsets into an edit layer; holes are one bit on `TerrainHoles`, which lives on the terrain,
has no alpha and no stack. Recording one in a `TerrainStroke` would be recording the wrong container.

⚠ **The ramp previews by undoing itself.** A ramp is one shape between two points rather than stamps
that accumulate, so each move of the second point undoes the stroke and redraws it — which works
because the record captures its before-image lazily and an undo puts the layer back to exactly what
that image holds.

## The collider seam

`ITerrainColliders`, for the reason `IMeshBaker` is an interface: **nothing in the editor references
`Vixen.Physics`, and this does not either.** The editor says which tiles moved; whatever holds a
physics world turns that into shapes, from `TerrainSamples.FillCollisionSamples` and
`PhysicsShapes.HeightField`, both of which already exist.

⚠ **Through `TerrainDescription.TilesOf`, which answers with *both* tiles for a sample on a boundary.**
A stroke along a tile edge that rebuilt only one side leaves a strip of collision disagreeing with the
ground beside it — a lip the player trips on, on a seam nothing draws.

## Tests

A real `EditorShell` headless with both modes, asserting the arbitration rather than the picture: that
the tool commands are registered and disabled before the mode is entered, that entering it moves `3`
from the bookmark to the flatten tool, and that unregistering takes the commands with it.

Beside that, a real terrain: every tool run and asserted, the eight-strokes-undone-and-redone property
over every sample, the tile-seam collider rebuild, and doc 31's own exit criterion as one test — a
terrain created, a valley sculpted, a ridge eroded, a layer added, a pad flattened on it, the layer
hidden and shown, eight strokes undone and redone, and a ray dropped on the pad to prove it is where
the artist put it.

⚠ **Untouched ground does not read back as zero, and the tests say so once.** A height is one of
65 536 steps over the authored range, so "flat at zero" is the nearest step to it — a millimetre and a
half out over ±100 m. `Ground.Rest` is what every "this was not changed" assertion compares against;
asserting `0f` to three places is asserting a precision the storage does not have.

## What is owed

The panel's chrome — this assembly holds the model and `Vixen.Editor.App` draws it, as it does for
world settings. Heightmap import and export are wired to the raw `r16` path in the kernel; 16-bit PNG
belongs with the importer, which already depends on `Vixen.Core.Imaging`. The target-layer section and
the four paint tools are [§ T4](../../docs/plan/31-terrain-grass-and-trees.md), and the `.vxterrain`
asset itself is what `TerrainMode.Created` hands out rather than something this writes.
