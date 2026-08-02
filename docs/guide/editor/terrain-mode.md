---
title: Sculpt and paint mode
slug: editor/terrain-mode
kind: guide
area: Editor
summary: The viewport mode the terrain tools live in — two categories sharing the digits, one drag as one undo entry, and the panel as settings objects rather than dialog code.
api: [T:Vixen.Editor.Terrain.TerrainMode, T:Vixen.Editor.Terrain.TerrainTool, T:Vixen.Editor.Terrain.TerrainEdit, T:Vixen.Editor.Terrain.TerrainCreateSettings, T:Vixen.Editor.Terrain.TerrainFacts, T:Vixen.Editor.Terrain.TerrainBrushSettings, T:Vixen.Editor.Terrain.TerrainToolSettings, T:Vixen.Editor.Terrain.TerrainStrokeCommand, T:Vixen.Editor.Terrain.TerrainHoleCommand, T:Vixen.Editor.Terrain.TerrainHoleStroke, T:Vixen.Editor.Terrain.TerrainLayerCommands, T:Vixen.Editor.Terrain.ITerrainColliders, T:Vixen.Editor.Terrain.TerrainCategory, T:Vixen.Editor.Terrain.TerrainPaintTool, T:Vixen.Editor.Terrain.TerrainPaintCommand, T:Vixen.Editor.Terrain.TerrainLayerSettings, T:Vixen.Editor.Terrain.TerrainTargetRow]
tags: [editor, terrain, sculpt, mode, undo]
since: 0.1
status: preview
related: [editor/modes, editor/foliage-mode, engine/terrain-sculpting, engine/terrain-painting, engine/terrain-heightfield, engine/terrain-brushes]
---

## What it is

`TerrainMode` is the third viewport mode, after Select and Blockout. It claims the `terrain` command
context, puts eight tools on `1`–`8` and the brush controls on `[`, `]`, `-` and `=`, and hands every
pointer gesture in the pane to `TerrainEdit` — which owns the terrain, the layer being written and the
drag in flight.

The panel is three `[DataContract]` settings objects with `[Inspector]` members:
`TerrainCreateSettings` (create and manage, with its derived readout), `TerrainBrushSettings` (the
brush section) and `TerrainToolSettings` (the parameters under it). This assembly draws nothing.

## What it is for

Building a level's ground without leaving the viewport, and getting every stroke back with one undo.
You want it whenever the terrain is being authored; a terrain being merely displayed needs none of it.

## Using it

```csharp no-compile="a fragment; the document and the collider rebuilder are the application's"
shell.Modes.Add(new SelectMode());
shell.Modes.Add(new TerrainMode { Document = scene, Editing = { Colliders = physics } });
```

That is the whole of the registration. The mode bar, the palette entries, the radio state and the
keymap follow from it — see [editor modes](modes.md).

⚠ **The digits are doc 24's B2 a second time, and that is the point.** `1`–`8` are view-bookmark
recall everywhere else; these commands declare a context and the bookmarks declare none, so `KeyMap`
files the two separately. A second consumer of the mode seam cost a `Context` string and nothing else,
which is what a seam with one implementation could only assert.

⚠ **A mode with no terrain shows the create panel rather than an empty toolbar.** The tool commands
are registered and disabled rather than hidden; entering a mode that does nothing and says nothing is
the state every one of these toolsets puts a new user in.

## The create form

`TerrainFacts` is what the form costs, computed as it is filled in: the extent, the sample count, the
height and weightmap storage, the number of collision shapes, and the vertical precision. Every row is
labelled `(derived)`.

⚠ **This is the dialog where a person accidentally asks for eight gigabytes.** Four numbers that each
look reasonable multiply into a terrain nothing can load, and the multiplication is not one anybody
does in their head.

⚠ **The tile size is offered in quads and stored in samples.** An artist reads 63 / 127 / 255; Jolt
requires a power-of-two *sample* count and refuses anything else by returning nothing at all. The two
differ by one and the form is where the translation happens, once.

⚠ **`Consequence` says what applying the form to an existing terrain would cost**, before rather than
after: cropping discards what is outside, and a height-range change rescales every height to keep its
metres and spends the precision.

## One drag, one entry

```csharp no-compile="a fragment; the ground point comes from TerrainPick"
edit.Begin(ground, invert: shiftHeld);
edit.Extend(next);

var command = edit.Commit();
```

⚠ **Merging is off.** Two strokes are two undos, which is what an artist means by "undo that" and what
every paint application does. What merges is *inside* the stroke: a drag is one record being extended
rather than four hundred commands.

⚠ **The brush is snapshotted at `Begin` and never read again.** The panel can move while a drag is in
flight — a pen's barrel wheel, a key, another window — and a stroke whose radius changed halfway has
an undo record sized to a footprint that no longer matches what it wrote.

⚠ **The composite is resolved per stamp; the colliders are rebuilt once.** The first is what makes the
ground move under the brush instead of jumping at pointer-up. The second is because a stroke marks the
same one or two tiles over and over, and building a Jolt height field per stamp is the version that
stutters.

⚠ **The command is a *redo* the first time it runs.** It is built at pointer-up from a stroke that has
already been applied, so `CommandStack.Execute` reapplies exactly what is already there — the price of
one vocabulary for undo, and the same shape every other editor command has.

⚠ **Holes get their own stroke type, because a hole is not a delta.** The seven sculpt tools write
signed offsets into an edit layer; a hole is one bit on the terrain, with no alpha and no stack.
`TerrainHoleStroke` is the parallel record, and recording a bit in a delta record would restore the
wrong container's contents.

⚠ **The ramp previews by undoing itself.** A ramp is one shape between two points rather than stamps
that accumulate, so each move of the second point undoes the stroke and redraws it — which works only
because the record captures its before-image lazily.

## Two categories, one set of digits

`TerrainCategory` is Sculpt or Paint. Both need a terrain and both act on its texels, so they are one
mode; what differs is whether a stamp writes a height or a weight. Unreal spells the same split as
tabs within Landscape mode.

⚠ **The digits are bound to *slots*, not to named tools.** Binding "Sculpt" and "Paint Layer" both to
`1` in the `terrain` context puts two commands on one chord, and the keymap resolves that to whichever
registered last — silently, and differently depending on registration order. `terrain.tool-N` means
what the design sentence means, "the third tool", and the named commands keep the words an artist
searches the palette for.

⚠ **A digit past the current category's tool count does nothing rather than wrapping.** Wrapping round
to the first tool is the version that silently paints with the wrong one.

⚠ **Changing category mid-drag abandons the stroke**, for the same reason changing tool does: a stroke
is a record of what one tool did, and finishing it under the other category would commit an entry
whose name and whose record belong to two different operations.

⚠ **A paint stroke rebuilds no colliders.** No height moved, so the shape is the shape it was; what
changed is which *material* each quad is, and that is read from the weights when it is asked. The
first version rebuilt anyway and a test caught it.

## The target-layer panel

`TerrainLayerSettings` is the `.vxlayer` form and `TerrainTargetRow` is a row of the list above the
strip. Selecting a row makes it the paint target.

⚠ **The layer being painted changes far more often than the tool**, so the list is above the strip and
not in it. That is Unreal's layout and it is correct: an artist paints grass, then rock, then grass
again with the same tool, and making the layer a mode would make that three mode switches.

⚠ **The coverage is one number, not a histogram.** A per-layer histogram over four million samples is
a bar nobody reads; what the section is for is "this layer is at zero and I do not know why" — the
state you get into by painting over your base layer — and a percentage answers it.

⚠ **Removing a target layer records every channel, not just the one removed.** Its weight goes to the
others in proportion, which is not invertible from the layer alone: putting the channel back would
leave the rest holding what they were given.

## The layer stack

`TerrainLayerCommands` is the panel's verbs, each one entry: add, remove, duplicate, move, clear,
collapse, rename, show/hide, lock, and the two alphas.

⚠ **A removed layer is held by its command, not rebuilt from a name.** An undo built on `AddLayer`
puts back a layer with the right name and none of its deltas — which passes any test that counts
layers and loses an hour of sculpting. `Terrain.InsertLayer` exists for this and nothing else.

⚠ **The alphas merge and the toggles do not.** A slider drag is three hundred changes and one edit; a
visibility toggle is one of each. The merged command undoes to the value before the *drag* started,
which is the half of `TryMergeWith`'s contract that is easy to get backwards.

⚠ **A clear swaps contents rather than replacing the object.** The panel's selection, the mode and a
stroke in flight all hold the layer by reference.

## Collision

`ITerrainColliders` is an interface for the reason `IMeshBaker` is one: **nothing in the editor
references `Vixen.Physics`, and this does not either.** The editor says which tiles moved; whatever
holds a physics world turns that into shapes.

⚠ **Through `TerrainDescription.TilesOf`, which answers with *both* tiles for a sample on a boundary.**
A stroke along a tile edge that rebuilt only one side leaves a strip of collision disagreeing with the
ground beside it — a lip the player trips on, on a seam nothing draws.

⚠ **Null is a terrain with no collision, not an error.** A scene being sculpted before anybody has
pressed play has no physics world, and a mode that refused to work without one could not be used until
the game ran.

## Examples

A stroke driven from world points, which is what a test does and what the pane does with a ray:

```csharp no-compile="a fragment; the ground points come from TerrainPick"
edit.Tools.Tool = TerrainTool.Sculpt;
edit.Tools.Metres = 4f;
edit.Brush.Radius = 12f;

edit.Begin(start);
edit.Extend(next);

if (edit.Commit() is { } command) {
    document.Stack.Execute(command);
}
```

A refusal, which is a sentence rather than an exception:

```csharp no-compile="a fragment"
if (!edit.Begin(where)) {
    // "The layer 'Splines' is managed by the Splines generator, so a hand edit would be discarded…"
    Notify(edit.Refusal);
}
```

⚠ **A brush that silently does nothing is the version of this that gets reported as the tool being
broken.** Locked layers and generated layers are ordinary things to aim at.

The create form's readout, which is what stops a terrain nothing can load:

```csharp no-compile="a fragment; the rows go straight into the panel"
foreach (var (label, value) in mode.Create.Facts.Rows()) {
    // "Height storage", "128.1 MB (derived)"
    Row(label, value);
}
```

## See also

- [Editor modes](modes.md) — the seam this is the third consumer of.
- [Foliage mode](foliage-mode.md) — the fourth, and why foliage is not a category of this one.
- [Sculpting a heightfield](../engine/terrain-sculpting.md) — the kernels behind the sculpt tools.
- [Painting a terrain](../engine/terrain-painting.md) — the kernels behind the paint tools.
- [Terrain brushes](../engine/terrain-brushes.md) — what `TerrainBrushSettings` produces.
- [docs/plan/31 § T3](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  the phase this is, and its exit criterion.
