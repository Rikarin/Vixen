---
title: Turning on dynamic global illumination
slug: rendering/lit-path
kind: guide
area: Rendering
summary: The four compositor nodes that make a frame's indirect light a file rather than a program.
api: [T:Vixen.Rendering.Compositor.GlobalDistanceFieldAsset, T:Vixen.Rendering.Compositor.IrradianceFieldAsset, T:Vixen.Rendering.PostFx.DistanceFieldAoAsset, T:Vixen.Rendering.PostFx.IndirectDiffuseAsset]
tags: [rendering, compositor, lighting, global-illumination]
since: 0.1
status: stable
related: [engine/players-and-possession, rendering/mesh-and-material]
---

## What it is

Four node kinds a `.vxcompositor` can name, which together are the lit path
`docs/plan/19-lighting-and-global-illumination.md` describes:

| Node | Does |
|---|---|
| `!GlobalDistanceField` | Composites the camera-following signed-distance clipmap every trace marches |
| `!IrradianceField` | Fills the probe field that carries the scene's bounced light |
| `!DistanceFieldAo` | Marches the clipmap for ambient occlusion and a sun shadow |
| `!IndirectDiffuse` | Reads the probe field into the ambient term |

## What it is for

Indirect light that follows a scene which changes — a lamp that is switched on, a wall that falls
down, a door that opens. Nothing here is baked, which is doc 19's whole argument.

You do not want it for a scene that never changes and ships on a budget: a baked solution is cheaper
and, for a static scene, better. And you do not want the first two nodes at all if your project
supplies no field — they build and do nothing, which is deliberate, but a document that names them
without a host that fills them is a document promising something no frame delivers.

## Using it

The nodes go in the frame, before whatever shades with them:

```yaml
version: 2
resources:
  - name: SceneDepth
    format: Depth32Float
    usage: DepthStencilTarget, Sampled
  - name: SceneNormals
    format: Rgba16Float
    usage: ColourTarget, Sampled
game: !Sequence
  name: Frame
  children:
    - !GlobalDistanceField
      name: Clipmap
    - !IrradianceField
      name: Probes
      budget: 16
    - !DistanceFieldAo
      name: Occlusion
      depth: SceneDepth
      normals: SceneNormals
      source: DistanceFieldAo.GlobalDistanceField
    - !IndirectDiffuse
      name: Indirect
      depth: SceneDepth
      normals: SceneNormals
```

⚠ **`source` is a contract, not a decoration.** It is the compose-slot prefix that
`!GlobalDistanceField` writes its bindings under and `!DistanceFieldAo` reads them from, so the two
have to be the same string. They are separate settings because a frame may march a field a different
node composited — or none.

⚠ **Leave `source` out and you get the honest default, not a broken frame.** `!DistanceFieldAo`
answers "nothing is near" — fully open, fully lit. `!IndirectDiffuse` answers no indirect light *and*
an unshadowed sun, which are two different right answers rather than one convenient zero: answering
zero for the second would put every surface in the world into shadow.

**The fields themselves are the host's**, on exactly the terms the virtualized path's traversal is:

```csharp compile
using Vixen.Core.Mathematics;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;

public static class Lighting {
    public static void Supply(CompositorBuilder builder) {
        builder.DistanceField = new GlobalDistanceField();

        builder.IrradianceField = new IrradianceField(
            new BoundingBox(new(-64f, -8f, -64f), new(64f, 24f, 64f)),
            new Int3(16, 4, 16)
        );
    }
}
```

A `GlobalDistanceField` owns volume textures and a residency the camera drives; an `IrradianceField`
owns a brick pool and a probe budget. Neither is something a document can create, and a node built
without one does nothing rather than throwing — which is what a shared compositor document says to a
project that has no field in it.

## Examples

**Choosing how fast the field settles.** `budget` is the whole quality-against-cost decision:

```yaml
- !IrradianceField
  name: Probes
  budget: 32          # converges in fewer frames, costs more in each
  dilationPasses: 2   # spread a filled probe's answer into its unfilled neighbours
```

A field settles over several frames, so a higher budget reaches the right answer sooner and costs
more per frame. Which of those a project wants is not something the engine can know.

**Turning the sun shadow off** where a shadow map already covers it:

```yaml
- !DistanceFieldAo
  name: Occlusion
  depth: SceneDepth
  normals: SceneNormals
  sunShadow: false
```

That is a permutation rather than a branch, so the march is not compiled at all and the pass carries
none of the sun's uniforms either.

**Registering the factory**, which is what lets a document name the two screen passes:

```csharp compile
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;

public static class Frames {
    public static void Register(CompositorBuilder builder) => builder.Factories.Add(new PostEffectFactory());
}
```

`CompositorBuilder` cannot switch on those types — `Vixen.Rendering.PostFx` is downstream of it, and a
case there would be a cycle — so the knowledge travels the only direction it can.

## See also

- [Players and possession](engine/players-and-possession) — what puts a camera in the scene these
  nodes light.

The design record is `docs/plan/19-lighting-and-global-illumination.md`. The nodes' placement in a
frame follows `docs/plan/06-rendering-pipeline.md` § Compositor, which is why they are an asset at
all: a path that cannot be written down is a path every host has to reimplement.
