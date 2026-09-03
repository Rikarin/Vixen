---
title: Procedural and UV nodes in a shader graph
slug: editor/shader-graph-procedural
kind: guide
area: Editor
summary: Noise, a checker and the two UV transforms — each one a call into the shader library rather than a second copy of it, which is also why they have no preview.
api: [T:Vixen.Editor.ShaderGraph.Nodes.NoiseNode, T:Vixen.Editor.ShaderGraph.Nodes.FractalNoiseNode, T:Vixen.Editor.ShaderGraph.Nodes.CheckerNode, T:Vixen.Editor.ShaderGraph.Nodes.RotateUvNode, T:Vixen.Editor.ShaderGraph.Nodes.FlipbookNode]
tags: [editor, shader-graph, raven, materials, node-graph]
since: 0.1
status: preview
related: [editor/shader-graph-materials, editor/shader-graph-previews, editor/graph-diagnostics]
---

## What it is

Five nodes that produce a pattern or move a coordinate, without a texture.

| Node | Produces | Notes |
|---|---|---|
| `Procedural/Noise` | a `float` in 0..1 | smoothed value noise on a grid |
| `Procedural/Fractal Noise` | a `float` in 0..1 | octaves of the above, at a lacunarity and a gain |
| `Procedural/Checker` | 0 or 1 | the fastest way to find out whether a UV is what you think |
| `Vector/Rotate UV` | a `float2` | turns a coordinate about a pivot, in radians |
| `Vector/Flipbook` | a `float2` | one cell of a sprite sheet, rows counted from the top |

## What it is for

**Each one is a call, not an implementation.** The functions live in
`Raven/Library/Material/ComputeColor.rvn`, whose header describes itself as "the shader-graph node
vocabulary: the primitives a visual material graph compiles down to". So a node adds no shader code,
`CheckShaders` already compiles what it calls, and the CPU has no second opinion about what noise is.

## Using it

**Leave the UV port unwired and the node uses the mesh's own coordinate.** An unconnected port carries
the literal its default made, which is not a coordinate — so every node here asks the emitter for the
stage's UV instead. Wire `Vector/Tiling and Offset` or `Vector/Rotate UV` in front when you want
something else.

**`Scale` is cells across, not a multiplier on the output.** `Procedural/Noise` at a scale of 1 is one
noise cell over the whole UV square, which reads as a very slow gradient rather than as noise.

**Fractal noise's `Octaves` is rounded down and clamped to at least one.** It is a port like the
others because a wire carries a float; the cast to `int` happens in the emitted call rather than in
the library, so a library function never has to round somewhere an author cannot see.

⚠ **`Vector/Flipbook` counts rows from the top**, matching the engine's top-left UV origin. That flip
is the part that is easy to get wrong and invisible until an animation plays backwards vertically.

## Two things they cannot do yet

⚠ **They have no preview thumbnail, and it is the preview's limit rather than theirs.**
`ShaderGraphPreviewRenderer` compiles the emitted preview through `RavenEffectCompiler.FromSources`
with exactly one source, so nothing in the shipped shader library is in scope and a call into
`ComputeColor` does not bind. The same graph compiles as a material perfectly well, because
`EditorEffects` and the shader build both hand Raven the library's import closure. See
[Shader-graph preview thumbnails](shader-graph-previews.md).

⚠ **There is no Perlin, simplex or voronoi node.** Each of those is a function `ComputeColor` does not
have, so adding one is a change to a published `.rvn` — a regeneration and a `CheckShaders` run —
rather than a node. Value noise is what the library chose, and it says why: a hash and a smoothstep,
fed into a ramp, is where the difference in gradient quality stops surviving. A gradient between two
values is `Math/Lerp`.

## How the import gets there

A graph emits one of two shapes, and they disagreed about imports.

A **surface** graph writes the four `Vixen.Shaders.*` packages unconditionally — it is composed into a
pass that has them. A **standalone** graph wrote none at all, which is right for the thing that
compiles one: the node preview binds a single uniform block and refuses any variant whose reflection
asks for more.

So a node *asks*, through `RavenEmitter.Import`, and the compiler writes only what was asked for. A
graph with no procedural node in it emits no import line, and the surface shape drops a request that
duplicates one of its four, because Raven refuses a repeated import.

## Examples

A slow drift of colour across a surface, with no texture bound at all:

```csharp no-compile="a description of a graph, which is authored rather than written"
// Procedural/Fractal Noise  →  Math/Lerp (T)
//   Scale    3      cells across the UV square, not a multiplier
//   Octaves  4      rounded down and clamped to at least one
//   A, B            the two colours to move between
```

Turning a coordinate before it is sampled, which is what a rotating pattern actually is:

```csharp no-compile="a description of a graph, which is authored rather than written"
// Vector/Rotate UV  →  Procedural/Checker
//   Angle    Time * 0.2      radians, so a full turn is a little over 31 seconds
//   Pivot    (0.5, 0.5)      the middle of the square rather than its corner
```

⚠ Both leave the UV port unwired, so both take the mesh's own coordinate. Wiring
`Vector/Tiling and Offset` in front is how you change that — a literal on the port is not a
coordinate.

## See also

- [A material that draws with a graph](shader-graph-materials.md) — where the emitted surface goes,
  and why only one of the two graph shapes can be drawn.
- [Shader-graph preview thumbnails](shader-graph-previews.md) — why these five have no preview, which
  is the preview's limit and not theirs.
