# Vixen.Terrain.Physics

Gives a terrain a collider. [docs/plan/31][31] § D10's runtime half: one Jolt height-field shape per
tile, rebuilt for the tiles a stroke moved and nothing else, with holes carried through as the
shape's own no-collision sentinel so a quarry is a pit you fall into.

## The gap this closes, because it is worth naming

Every piece of the terrain-collision path was already built and tested, and **nothing anywhere joined
them**:

- `PhysicsShapes.HeightField` registers the Jolt shape, validated hard, with sixteen tests of its own.
- `TerrainSamples.FillCollisionSamples` produces *exactly* the span that shape consumes — metres,
  row-major, holes as the caller's sentinel. Its only callers were two assertions in a kernel test.
- `Editor/Vixen.Editor.Terrain/ITerrainColliders` is the seam the sculpt tools call after every
  stroke. The only implementation of it in the tree is a test double that records tile indices.

So a terrain in a *game* had no collision at all, in any project, and **the symptom was not an
error**: a character walked onto the ground and fell through it. `docs/plan/31` § B1 was ticked ✅ and
said "a terrain tile is a collider on the day the shape exists", which was true of the ECS bridge and
false of the tree — a ✅ that stops anyone looking.

## Why it is its own assembly

[§ D1][31] draws two lines and this sits between them.

- **`Vixen.Terrain` is device-free and stays that way.** It is what a dedicated server runs and what a
  runtime crater or a moddable map is sculpted with; a kernel that linked Jolt to describe a height
  sample would be backwards, and would put a physics engine in every process that reads a heightmap.
- **`Vixen.Rendering.Terrain` references no physics and must not.** Nothing in the rendering stack
  does. A reference there would drag Jolt into every project that draws a hill.

`Vixen.Water.Physics`' arrangement exactly, and `Vixen.Audio.Physics`' before it: the kernel says what
the ground is and knows nothing about a body, the physics world registers a shape and knows nothing
about an edit layer, and this is the twenty lines between them.

⚠ **Note what it does not reference: `Vixen.Rendering.Terrain`.** `TerrainComponent`,
`ITerrainAssetSource` and the extraction pass all live there, because turning an asset name into a
loaded heightfield is render-stack work. A collider that reached for them would put a graphics device
in a headless build — § D1 undone in one line. It finds the ground through `ITerrainPlacements`
instead, which is a **kernel** interface: `TerrainSceneSource` implements it on a client, and a
headless build implements it from whatever placed the ground there.

## Using it

```csharp
var colliders = new TerrainColliderSystem(physics, graphics.Renderer.TerrainScene);
loop.Add(colliders);
```

That is the whole of it. `TileCount` is the number that says it worked; zero with a terrain in the
scene is the failure this assembly exists to end.

## Three things that are silent when they are wrong

⚠ **The terrain arrives late, so this asks every frame.** A `.vxterrain` is tens of megabytes and
`ITerrainAssetSource` answers `null` until the bytes land — several frames after the level is up. A
one-shot call at load time gets nothing and builds nothing, with no error: exactly the
late-resolution trap the water fold shipped, where a cache stored a failure and never asked again.
Once a terrain has bodies the per-frame cost is one integer compare per tile.

⚠ **The tile's corner goes in the shape's offset, not in the entity's transform.** Both would place
one tile and only one places four: a shape is interned by its description, so four flat tiles at four
transforms over one description are *one* shape drawn four times in the same place. The offset is part
of the description.

⚠ **Tiles share their boundary samples, so the shapes overlap by one quad.** That is the terrain's own
arithmetic — `TileQuads` is `TileSamples − 1` — and it is right. A gap of one quad between two height
fields is a seam a character falls through; an overlap is two surfaces at identical heights, which
Jolt resolves as one.

## What it does not do yet

- **It does not implement `ITerrainColliders`.** That interface lives in `Editor/Vixen.Editor.Terrain`
  and this assembly may not reference the editor, so an adapter has to live on the editor's side of
  the line. `Rebuild(Terrain, TerrainRect)` here has that interface's signature so the adapter is
  three lines; nobody has written it, and the editor's sculpt tools therefore still rebuild nothing.
- **Nothing releases a shape.** `PhysicsShapes` has no release — its table is a level's shapes and a
  level ends with the world — so a rebuilt tile registers a new shape and the old one stays interned.
  `Rebuilds` is the counter that makes a terrain being recomposited every frame visible.
- **Hole edits are watched by count.** `TerrainHoles` carries no per-tile version and punching one
  moves no height, so the poll rebuilds a terrain's tiles when `HoleCount` moves and misses a punch
  and a fill that net to zero within a frame. A tool that does that should call `Rebuild` directly.

[31]: ../../docs/plan/31-terrain-grass-and-trees.md
