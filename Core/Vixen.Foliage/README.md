<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.Foliage

The foliage kernel — [docs/plan/31 § T5](../../docs/plan/31-terrain-grass-and-trees.md). Instance
storage in a cell grid, the placement rules a scatter obeys, and the collision residency that decides
which trees are worth a physics body.

**No device, no document, no editor — and no terrain.** One project reference, to
`Vixen.Core.Mathematics`. Foliage paints onto anything with a surface: a blockout mesh, an imported
cliff, a rooftop. Making this depend on a heightfield would make a roof of moss depend on one, which
is [§ D1](../../docs/plan/31-terrain-grass-and-trees.md)'s reason for keeping the two kernels apart.

## What is here

| Type | What it is |
|---|---|
| `FoliageType` | What a `.vxfoliage` holds: the mesh, the density, the spacing, the ranges, the filters, the cull distances, the collision |
| `FoliageCellGrid` | A fixed-size square of world, and the unit of batching, streaming and collision |
| `FoliageChunk` | Every instance of one type inside one cell, with the bounds a cull tests |
| `FoliageVolume` | The palette and every chunk — what a scene names |
| `FoliageScatter` | A stamp into instances, and the six rules that refuse a candidate |
| `IFoliageSurface` | Where a scatter asks what the ground is. An interface, so this needs no scene |
| `FoliageStore` | Instances as bytes, beside the scene rather than in it |
| `FoliageCollision` | Which instances are near enough to something to have a body, as a *difference* |

## Six things worth knowing

**The cell is the batch.** A foliage cell holds every instance of one type within it, its bounds are
tight, and it is what the instancing feature sees as one object — which is what makes [§ B2]'s
contract satisfiable. Thirty-two metres by default, and it is a compromise with two failure modes:
larger cells cull worse, smaller ones cost a draw each.

**Placement is deterministic from the stamp and the candidate index, never from an iteration order.**
A hash of the seed and the index means an undone-and-redone stroke produces the same forest, and it is
what will make the CPU reference and the GPU scatter comparable at zero drift when the grass phase
lands. A counter-based identity makes both impossible.

**The spacing check includes the stamp's own earlier candidates.** Checking only the volume would let
one stamp drop forty trees on one spot, because none of them was there when the others were tested.

**Alignment is a fraction, not a flag.** A tree leaning ten per cent into a hill reads as growth; a
tree lying flat on it reads as felled. Slerping from upright towards the normal is what makes the
setting continuous, and rocks want one end of it while trees want the other.

**An address is not a reference.** `FoliageAddress` is valid until its chunk changes: removing an
instance shifts the ones after it. `Remove` sorts descending within each chunk so a caller cannot get
it wrong, and a *loop* that removes as it goes is the trap — one was written and a test caught it.

**Collision hands back a difference, not a set.** The caller pools bodies, and a set would make it
diff two collections of ten thousand addresses every frame to find the four that changed.

## The behavioural difference this ships

⚠ **A projectile fired at a tree four hundred metres away passes through it.** Ten thousand static
bodies is not a scene, it is a broadphase problem, so an instance gets a body only within its type's
`ActivationRadius` of something physics-relevant. This is stated rather than hidden —
[§ D10](../../docs/plan/31-terrain-grass-and-trees.md) — and the mitigation available to a project
that needs otherwise is to raise the radius for that type.

Grass never collides: a derived type is never asked, because its instances do not exist between one
frame and the next.

## The seam to a surface

This assembly cannot name a terrain, a mesh or a physics world. What it asks is one question:

```csharp
FoliageSurface SampleAt(Vector2 position, string layer);
```

Which is `ISurfaceProbe`'s question with the painted weight added, and it is why the surface filters
work without foliage-specific code — a probe that answers for blockout meshes makes painting onto a
wall work on the day they are probeable.
