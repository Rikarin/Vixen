---
title: Foliage instances
slug: engine/foliage
kind: guide
area: Engine
summary: Trees and rocks stored in a cell grid, the six rules that refuse a placement, and the activation radius that decides which of them are worth a physics body.
api: [T:Vixen.Foliage.FoliageType, T:Vixen.Foliage.FoliageStorage, T:Vixen.Foliage.FoliageInstance, T:Vixen.Foliage.FoliageCellGrid, T:Vixen.Foliage.FoliageCellKey, T:Vixen.Foliage.FoliageChunk, T:Vixen.Foliage.FoliageVolume, T:Vixen.Foliage.FoliageAddress, T:Vixen.Foliage.FoliageScatter, T:Vixen.Foliage.FoliageScatter.Refusal, T:Vixen.Foliage.FoliageScatter.Result, T:Vixen.Foliage.FoliageSurface, T:Vixen.Foliage.IFoliageSurface, T:Vixen.Foliage.FoliageStore, T:Vixen.Foliage.FoliageCollision]
tags: [foliage, vegetation, instancing, scatter, collision]
since: 0.1
status: preview
related: [engine/terrain-heightfield, engine/terrain-brushes, engine/grass, engine/foliage-growth, rendering/foliage-rendering, editor/foliage-mode]
---

## What it is

`FoliageVolume` is every instance in a scene, kept in a grid of fixed-size cells with the palette they
are instances of. `FoliageType` is what a `.vxfoliage` holds — the mesh, the density, the spacing, the
ranges its randomness draws from, the filters that refuse a placement. `FoliageScatter` turns a brush
stamp into instances; `FoliageStore` writes them beside the scene; `FoliageCollision` decides which of
them are near enough to something to be worth a physics body.

**No device, no document, no editor — and no terrain.** One project reference, to
`Vixen.Core.Mathematics`.

## What it is for

Fifty thousand trees an artist placed and can move. You want it whenever the things being scattered
have *identity*: a designer moves one, deletes one, and expects to find the result tomorrow; they
carry collision; a quest marker can be attached to one.

You do not want it for grass. The dividing line is **density × identity** — something you would place
ten thousand of and never name individually is derived from a rule and never persisted, which is what
`FoliageStorage.Derived` marks and what the grass phase scatters on the GPU.

⚠ **And it deliberately does not depend on the terrain.** A foliage type paints onto anything with a
surface — a blockout mesh, an imported cliff, a rooftop — so folding it into the terrain assembly
would make a roof of moss depend on a heightfield.

## Using it

```csharp no-compile="a fragment; the surface is whatever the host can probe"
var volume = new FoliageVolume(new FoliageCellGrid(32f));
var pine = volume.AddType(FoliageType.Of("Pine") with { Mesh = "Meshes/pine", Radius = 3f });

FoliageScatter.Stamp(volume, pine, surface, centre: new(50f, 50f), radius: 10f);
```

⚠ **The cell is the batch.** It holds every instance of one type within it, its bounds are tight, and
it is what the instancing feature sees as one object. Thirty-two metres by default, and it is a
compromise with two failure modes: larger cells cull worse — the far half of a big cell is behind a
hill and is drawn anyway — and smaller ones cost a draw each.

⚠ **`CellOf` floors rather than truncating.** Truncation folds −0.5 and +0.5 into the same cell, so
the four cells around the origin become two — a seam through the middle of every level built around
zero.

⚠ **A chunk's bounds reach past the trunks.** A box built from positions alone ends at them, so every
tree at the edge of a cell pops out of existence while half of it is still on screen.

⚠ **An address is not a reference.** `FoliageAddress` is valid until its chunk changes: removing an
instance shifts the ones after it. `Remove` sorts descending within each chunk so a caller handing
over ascending addresses cannot delete the wrong ones — and the trap that remains is a *loop* that
removes as it goes.

## Placement

Six rules, in one pass over each candidate: is there ground, is the slope in range, is the altitude in
range, is the filtered layer painted enough, is something already within the spacing, and did the
brush reach. `FoliageScatter.Consider` returns *which* one refused.

⚠ **Because "the brush places nothing and does not say why" is the most reported problem with every
foliage tool ever shipped.** A panel that can say "forty candidates, thirty-one too steep" turns that
into a setting somebody changes.

⚠ **Deterministic from the stamp and the candidate index, never from an iteration order.** A hash of
the seed and the index means an undone-and-redone stroke produces the same forest — and it is what
makes the CPU reference and the GPU scatter comparable when the grass phase lands.

⚠ **The spacing check includes the stamp's own earlier candidates.** Checking only the volume would
let one stamp drop forty trees on one spot, because none of them was there when the others were
tested.

⚠ **Alignment is a fraction, not a flag.** A tree leaning ten per cent into a hill reads as growth; a
tree lying flat on it reads as felled.

⚠ **The layer filter is a *name*, not an index.** Removing the second of six terrain layers shifts
the rest down, and a type holding the index would silently start spawning on different ground — a
forest that migrates when somebody tidies the layer list.

## Collision

⚠ **A projectile fired at a tree four hundred metres away passes through it.** Ten thousand static
bodies is not a scene, it is a broadphase problem, so an instance gets a body only within its type's
activation radius of something physics-relevant. This is stated rather than hidden, and the mitigation
available to a project that needs otherwise is to raise the radius for that type.

⚠ **`Update` hands back a *difference*, not a set.** The caller pools bodies, and a set would make it
diff two collections of ten thousand addresses every frame to find the four that changed.

⚠ **An erased instance's address has to be `Forget`ten.** It now belongs to whichever instance shifted
down into it, so the next update would find that one already active and never give it a body — a tree
with a hole where its collision should be, for as long as the level runs.

Grass never collides: a derived type is never asked.

## Examples

A type that only grows on a painted layer, on gentle ground, below the tree line:

```csharp no-compile="a fragment"
var pine = FoliageType.Of("Pine") with {
    Mesh = "Meshes/pine",
    Density = 0.05f,
    Radius = 3f,
    MaxSlope = MathF.PI / 6f,
    MaxAltitude = 900f,
    LayerFilter = "Grass",
    LayerThreshold = 0.4f
};
```

Reading a stroke's refusals, which is what a panel reports:

```csharp no-compile="a fragment; `pending` is what this stamp has already placed"
var why = FoliageScatter.Consider(volume, type, settings, surface, at, pending, hash, out var instance);

if (why == FoliageScatter.Refusal.Slope) {
    // "thirty-one of forty candidates were too steep"
}
```

Saving beside the scene:

```csharp no-compile="a fragment; the palette goes in the text file that declares the volume"
var bytes = new byte[FoliageStore.ByteCount(volume)];

FoliageStore.Write(volume, bytes);
```

⚠ **Binary, and it is the one place in this subsystem where that is not a shortcut.** Fifty thousand
instances is 1.4 MB of packed floats and about 12 MB of YAML. What is *not* binary is the palette,
which is names and numbers an author edits and a diff should show.

⚠ **Reading re-cells, because positions are the truth and cells are an index over them.** A file
written with 32 m cells and read into a volume using 64 m ones is a reasonable thing to happen —
somebody changed a setting — and the alternative is a forest whose cells no longer match its grid.

## See also

- [Drawing foliage](../rendering/foliage-rendering.md) — cells culled as objects, instances culled within them.
- [Grass](grass.md) — the derived half, and the density-times-identity line between them.
- [Growing a forest](foliage-growth.md) — the offline ecology that fills a volume of its own.
- [Foliage mode](../editor/foliage-mode.md) — the six tools, the palette and the gizmo.
- [Terrain brushes](terrain-brushes.md) — the brush a foliage stroke shares with the sculpt and paint tools.
- [docs/plan/31 § D8](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  grass is derived, trees are stored, and the distinction is the density.
