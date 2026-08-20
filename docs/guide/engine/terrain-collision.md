---
title: Terrain collision
slug: engine/terrain-collision
kind: guide
area: Engine
summary: One Jolt height-field shape per tile, kept in step with the ground, in its own assembly so neither the kernel nor the renderer links the other's dependency.
api: [T:Vixen.Terrain.Physics.TerrainColliderSystem, T:Vixen.Terrain.ITerrainPlacements, T:Vixen.Terrain.TerrainPlacement, T:Vixen.Physics.Shapes.HeightFieldPlacement]
tags: [terrain, collision, physics, heightfield, jolt]
since: 0.2
status: preview
related: [engine/terrain-heightfield, engine/terrain-sculpting, engine/buoyancy, engine/character-movement]
---

## What it is

`TerrainColliderSystem` turns the terrains in a world into collision: one Jolt height-field shape per
tile, one static body carrying it, rebuilt for the tiles that moved and nothing else.

| Piece | What it is |
|---|---|
| `TerrainColliderSystem` | The pass that builds and maintains the bodies |
| `ITerrainPlacements` | Where the terrains are — a kernel interface, implemented by the renderer |
| `TerrainPlacement` | One terrain and the world position of its low corner |
| `HeightFieldPlacement` | What a built shape remembers: samples a side, corner, and metres per step |

It is its own assembly, `Vixen.Terrain.Physics`. `docs/plan/31` § D1 draws two lines and this
sits between them: **`Vixen.Terrain` is device-free and physics-free** — it is what a dedicated server
runs and what a runtime crater is sculpted with — and **`Vixen.Rendering.Terrain` references no
physics**, because a reference there would drag Jolt into every project that draws a hill.

## What it is for

Standing on the ground. Without it a terrain is scenery: a character walks onto it, falls through, and
nothing anywhere reports a problem — which is what every project in this repository did until this
assembly existed, with the shape, the sample fill and the editor's seam all built and tested and no
call joining them.

It is also what a projectile hits, what a raycast finds, and what a character controller's ground
check answers against. Trees are a separate question — `docs/plan/31` § D10 collides foliage within an
activation radius, not as ten thousand static bodies — and grass never collides.

## Using it

```csharp no-compile="a fragment; `physics`, `graphics` and `loop` are the host's"
var colliders = new TerrainColliderSystem(physics, graphics.Renderer.TerrainScene);
loop.Add(colliders);
```

`WorldRenderer.TerrainScene` is a `TerrainSceneSource`, which the extraction pass refills every frame
and which implements `ITerrainPlacements`. On a headless build with no renderer, implement the
interface from whatever placed the ground.

`TileCount` is the number that says it worked. **Zero with a terrain in the scene is the failure this
assembly exists to end**, and it has no other symptom.

### ⚠ A game references this package itself, and nothing will tell it to

`Vixen.App.Hosting` links `Vixen.Rendering.Terrain`, so a `!Terrain` node and a `TerrainComponent`
reach every game that draws ground. It deliberately does not link this one, for the reason above.

```xml
<ProjectReference Include="…/Vixen.Terrain.Physics/Vixen.Terrain.Physics.csproj" />
```

### ⚠ The terrain arrives late, and a one-shot call gets nothing

A `.vxterrain` is tens of megabytes. `ITerrainAssetSource` starts a load and answers `null` until the
bytes land — several frames after the level is up — so building colliders once at load time builds
nothing, **silently**. That is why this is a system: it asks every frame until it has a map, builds,
and then costs one integer compare per tile.

### ⚠ Sculpting moves the collider without anybody saying so

`Terrain.RevisionOf` counts recomposites, and the system compares it per tile. A stroke that ran
through `Terrain.Resolve` has its collision on the next frame. `Rebuild(terrain, rect)` is the
synchronous form, for a tool that cannot wait one. The editor-side adapter over it is
[`TerrainColliders`](../editor/terrain-sculpt-collision.md).

⚠ **This paragraph used to say the adapter was "three lines" because `Rebuild` carries
`ITerrainColliders`' signature. It does not, quite** — both overloads here return `bool` where the
interface returns `void`, and `false` means *this system has never heard of this terrain*. A wrapper
that forwarded and discarded that value would report success for every stroke while rebuilding
nothing, in no log.

### ⚠ A tile index that is not a tile is refused, and it used to alias a neighbour

`Rebuild(terrain, tileX, tileZ)` validates the index and throws `ArgumentOutOfRangeException`. Before
2026-08-20 it did not, and everything below it indexes flat: `tileZ * TilesX + tileX` for the entity
slot, the same arithmetic inside `Terrain.RevisionOf`, and `TerrainSamples`' indexer **clamps** rather
than throwing — deliberately, because every sculpt kernel reads its neighbours. So on a 2 × 2 terrain
`Rebuild(terrain, 2, 0)` *was* `Rebuild(terrain, 0, 1)`: a height field built from the edge row
repeated, placed at a corner 28 m away, written into tile `(0, 1)`'s slot, returning `true`. The tile
it landed on became a hole a ray falls through.

What made it permanent is the last line of the build: the slot was stamped with the revision of the
*aliased* tile, which is the number the per-frame poll compares — so the poll skipped that tile for
the life of the world and no recomposite ever repaired it.

The `rect` overload cannot reach this, because `TerrainDescription.TilesOf` clamps. A caller naming a
tile by hand can, and `ITerrainColliders` hands that overload to any tool. A throw rather than a
clamp, because a rect of samples is data and a tile index is not: clamping it would rebuild a tile the
caller did not ask for.

⚠ **Hole edits are watched by `TerrainHoles.HoleCount`, which is coarse.** The mask carries no
per-tile version and punching a hole moves no height, so a change to the count rebuilds that terrain's
tiles and a punch-and-fill that nets to zero within one frame is missed. A tool that does that should
call `Rebuild`.

## Examples

### The friction is not the `Collider` default, and the difference is felt

```csharp no-compile="a fragment; `colliders` is the system above"
colliders.Friction = 0.8f;
```

`Collider.Of` gives 0.2, which is right for a prop and nearly ice for ground a character walks up. The
default here is 0.8; a slope a player slides down is the symptom of the other one.

### What the shapes look like

A terrain of 2 × 2 tiles of 64 samples is four static bodies over 252 m. The tile is the unit because
`docs/plan/31` § D2 makes it the unit of everything — storage, culling, streaming, undo — and because a
terrain whose collision was one shape would pause for the whole terrain every time somebody smoothed a
ridge.

⚠ **Tiles share their boundary samples, so the shapes overlap by one quad.** That is the terrain's own
arithmetic: `TileQuads` is `TileSamples − 1`, so two tiles of 64 samples span 127 samples and not 128.
A gap of one quad between two height fields is a seam a character falls through; an overlap is two
surfaces at identical heights, which Jolt resolves as one.

⚠ **The tile's corner rides in the shape's offset, not in the entity's transform.** A shape is interned
by its description, so four flat tiles at four transforms over one description would be one shape drawn
four times in the same place.

⚠ **Nothing releases a shape.** `PhysicsShapes` has no release — its table is a level's shapes and a
level ends with the world — so a rebuilt tile registers a new one and the old stays interned.
`Rebuilds` is the counter that makes a terrain being recomposited every frame visible before it is a
memory problem.

## See also

- [The terrain heightfield](terrain-heightfield.md) — what `FillCollisionSamples` fills, and the seam
  it is one half of.
- [Sculpting terrain](terrain-sculpting.md) — what moves the ground the colliders follow.
- [Floating things on water](buoyancy.md) — the same assembly split, one subsystem over.
- `docs/plan/31` § B1 and § D10 — the design, and the ✅ that was not true.
