---
title: Drawing particles
slug: rendering/particles
kind: guide
area: Rendering
summary: Dropping a .vxvfx onto an entity — the component, the importer that compiles the graph, the bridge that runs it, and the feature that draws it.
api: [R:Vfx/ParticleSprite, T:Vixen.Shaders.Generated.ParticleSpriteKeys, T:Vixen.Shaders.Generated.ParticleSpritePerMaterialConstants, T:Vixen.Shaders.Generated.ParticleSpritePerViewConstants, T:Vixen.Rendering.Vfx.ParticleSpriteMaterial, T:Vixen.Rendering.Ecs.VfxEmitter, T:Vixen.Rendering.Ecs.VfxEmitters, T:Vixen.Rendering.Ecs.VfxHandle, T:Vixen.Rendering.Ecs.VfxExtractionSystem, T:Vixen.Rendering.Ecs.IVfxEffectSource, T:Vixen.Engine.Renderer.AssetVfxEffectSource, T:Vixen.Editor.Assets.Vfx.VfxImporter, T:Vixen.Editor.Assets.Vfx.VfxImportSettings, T:Vixen.Rendering.Vfx.VfxEffectContent, T:Vixen.Rendering.Vfx.VfxSpawnerRow, T:Vixen.Rendering.Vfx.VfxOperationRow, T:Vixen.Rendering.Vfx.VfxRendererRow, T:Vixen.Rendering.Vfx.VfxCustomAttributeRow, T:Vixen.Rendering.Features.ParticleRenderFeature, T:Vixen.Rendering.ParticleVertices, T:Vixen.Vfx.VfxSystem, T:Vixen.Vfx.VfxCompiledGraph, T:Vixen.Vfx.VfxSpawner, T:Vixen.Vfx.VfxOperation, T:Vixen.Vfx.VfxRenderer, T:Vixen.Vfx.ParticleVertex]
tags: [rendering, vfx, particles, compositor]
since: 0.1
status: stable
related: [rendering/lit-path, rendering/shadows]
---

## What it is

A particle effect is an asset an author drops onto an entity, exactly as a mesh is. Six pieces, each
owned by whoever knows the fact it carries:

| Piece | Says |
|---|---|
| `.vxvfx` | the node graph an author edits — spawners, initializers, updaters, an output |
| `VfxImporter` | compiles it at build time into `VfxEffectContent`, the flat instruction list |
| `VfxEmitter` | which effect an entity emits, whether it is running, and how far it reaches |
| `VfxExtractionSystem` | resolves, creates, places, **steps** and retires — the bridge |
| `ParticleRenderFeature` | expands each live particle into a camera-facing quad and draws the run |
| `ParticleSprite.rvn` | the shader that takes a position, a texcoord and a colour and returns a disc |

⚠ **The extraction is the only thing that steps a simulation.** `ParticleRenderFeature` draws what it
has been handed and never advances it, deliberately — a renderer that simulated would simulate once
per view. So an effect created by hand, outside the component path, is one somebody has to step.

⚠ **The opcodes are world-space and there is no emitter transform.** `PositionInSphere` carries a
centre, not an offset. What makes one `.vxvfx` serve twenty entities is `VfxSystem.Origin`, which the
extraction writes from the entity's `WorldTransform` — and which is read **at spawn**, so moving an
emitter moves where the next particles appear and leaves the live ones where they are. That is what a
torch carried across a room does to its smoke.

## What it is for

Anything whose geometry is different every frame and is not a mesh: embers, sparks, smoke, dust,
tracers, a trail behind a projectile.

You do not want it for a shadow stage. A camera-facing quad faces *a* camera, and the expansion
happens once for the whole frame against `ParticleRenderFeature.View` — so a cascade drawing the
same quads draws them edge-on to its own light. You do not want it in a velocity stage either: a
particle has no previous world matrix, so there is nothing for a motion-vector pass to difference.

## Using it

**One.** A stage for them, in the `.vxcompositor`. Additive or premultiplied, depth-tested but not
depth-writing, and nothing culled:

```yaml
stages:
  - name: Embers
    blend: Additive
    depth: TestOnly
    cull: None
    sortMode: BackToFront
```

**Two.** A pass that draws it. A pass of its own rather than a second child of the opaque one, if
that pass writes more than one colour target: `ParticleSprite` returns a single value, and a
pipeline built for two attachments leaves the second undefined.

```yaml
    - !RenderPass
      name: Sparks
      colourTargets: [SceneHdr]
      depthTarget: SceneDepth
      depthLoad: Load
      readOnlyDepth: true
      loaded:
        - SceneHdr
      children:
        - !SingleStage
          name: Embers
          view: Camera
          stage: Embers
```

Drawing into the HDR target rather than the swapchain is what puts the particles through the
tonemap and the bloom — which is what makes an emissive value in cd/m² mean anything.

**Three.** Name that stage where the host can find it, and point the feature at the camera:

```csharp no-compile="in Game.OnConfigure, and in whatever runs after the document is loaded"
config.Graphics.ParticleStage = "Embers";

renderer.Particles.View = renderer.Host.Builder.Views["Camera"];
```

⚠ `ParticleStage` is **not** a `CasterStages` entry. Those are stages a mesh is extracted into *as
well as* the opaque one; this is where emitters are drawn and no mesh ever is. Putting it in the
caster list would draw the camera's billboards into every shadow cascade, edge-on to the sun.

⚠ `View` is not optional in a frame with shadows in it. Left unset the feature takes `Views[0]`,
which is whichever view the `!ShadowMap` node registered first.

**Four.** Put the component on an entity — in the editor, or in the scene file:

```yaml
  - name: Lamp0
    position: -16 2.6 -16
    components:
      - !Light { kind: Point, unit: Lumen, intensity: 150000, temperature: 1900, range: 26 }
      - !VfxEmitter { effect: vx:611abda7a814472b9618b8eda16e61b6, playing: true, reach: 4.0, rise: 1.6 }
```

`[AssetType(typeof(VfxEffectContent))]` on `VfxEmitter.Effect` is what makes the inspector row a
picker for *effects* rather than for anything in the project, and what makes a dragged `.vxvfx` a
legal drop. Nothing else is needed: `WorldRenderer.Mount` builds the source, `Register` adds the
bridge, and the bridge does the rest.

⚠ **`reach` is the bound the frustum culls against, and nothing recomputes it.** A particle outside
it does not vanish on its own — the whole effect does, because culling is per render object. It has
to cover where the drift can get to rather than where the particles are now.

⚠ **Leave `seed` at zero unless the frames have to be reproducible.** The bridge derives one from the
entity, so two lamps of one effect are two different fires rather than one repeated twice. Writing a
seed down is what a test that renders N frames twice wants.

## Examples

The effect itself, as the editor writes it — a chain of blocks whose wire is *order*, not data:

```yaml
version: 1
name: Embers
nodes:
  - id: 1
    type: Vfx/Spawn/Rate
    values:
      Rate:
        - 6
  - id: 2
    type: Vfx/Initialize/Position in Sphere
    values:
      Centre:
        - 0
        - 0
        - 0
      Radius:
        - 0.16
edges:
  - { fromNode: 1, fromPort: Out, toNode: 2, toPort: In }
```

⚠ **`Centre` is an offset from the emitter, not a place in the level** — see the origin note above.
An effect authored with a level's coordinates in it works for exactly one entity.

⚠ **This reader will not take a comment on the line after a flow sequence**, which is why the values
above are block style. `Rate: [6]` parses; a comment on the next line does not.

The material, when a project wants something other than the default. `WorldRenderer.ParticleMaterial`
is `ParticleSpriteMaterial.Default()` — composed the way a non-surface pass has to be, and with every
parameter set — so most projects change one number rather than building one:

```csharp no-compile="a fragment; the parameter keys are interned from the shader's own names"
renderer.ParticleMaterial.Parameters.Set(ParameterKeys.New<float>("ParticleSprite.emissive"), 40000f);
renderer.ParticleMaterial.Parameters.Set(ParameterKeys.New<float>("ParticleSprite.edgeSharpness"), 2.2f);
```

`emissive` is in the scene's own photometric units, not a 0..1 opacity: a spark that is to bloom has
to be brighter than the bloom's threshold, which in a physically lit frame is in the thousands.

⚠ **Build the material through `ParticleSpriteMaterial.Default()`, not with `new Material(...)`.** A
shader's declared default reaches the GPU through the generated key's `DefaultBytes`, and the Raven
reflection records a default only for **scalars** — so a vector parameter a host does not mention is
written as zero. For this shader that is `tint = (0, 0, 0, 0)`: black, alpha zero, and additively
blended that is perfectly invisible. `Bloom.texelSize` has the same gap and has never shown it,
because `BloomRenderer` writes that parameter every frame.

An effect built in code, for a game that makes one at run time rather than authoring it. ⚠ Nothing
steps this one — that is the extraction's job, and an effect outside the component path has none:

```csharp no-compile="a graph, shown out of the method that returns one"
VfxCompiledGraph.Compile(
    [VfxSpawner.AtRate(6f)],
    [
        new(VfxOpcode.PositionInSphere, new Vector4(0f, 0f, 0f, 0.16f)),
        new(VfxOpcode.SetLifetime, new Vector4(4.5f, 9f, 0f, 0f)),
        new(VfxOpcode.SetSize, new Vector4(0.02f, 0.05f, 0f, 0f)),
        new(VfxOpcode.SetColour, new Vector4(1f, 0.58f, 0.16f, 1f))
    ],
    [
        new(VfxOpcode.Gravity, new Vector4(0f, 0.35f, 0f, 0f)),
        new(VfxOpcode.Drag, new Vector4(0.55f, 0f, 0f, 0f)),
        new(VfxOpcode.Turbulence, new Vector4(0.45f, 0.45f, 0.45f, 0.5f)) { B = new(0.2f, 2f, 0f, 0f) },
        new(VfxOpcode.ColourOverLife, new Vector4(1f, 0.58f, 0.16f, 1f)) { B = new(0.7f, 0.16f, 0.03f, 0f) },
        new(VfxOpcode.SizeOverLife, new Vector4(0.045f, 0.012f, 0f, 0f)),
        new(VfxOpcode.Integrate, Vector4.Zero)
    ],
    64,
    VfxRenderer.Billboard
);
```

⚠ **`Integrate` last.** The updaters above it write velocity and this turns velocity into position;
first, every particle moves on the previous step's forces.

The working example is `Samples/13-ThirdPersonShooter`: `Assets/Effects/Embers.vxvfx` is the graph,
every lamp in `Assets/Scenes/Arena.vxscene` carries one `!VfxEmitter` line, and `Frame.vxcompositor`
has the `Embers` stage and the `Sparks` pass. There is no per-lamp code at all.

## See also

- [The lit path](lit-path.md) — where set 0, set 1 and set 2 come from, which is the same division
  the particle pipeline reads them under.
- [Making everything cast a shadow](shadows.md) — the other reason a schema matches attributes by
  name rather than by location.
- `Core/Vixen.Vfx/README.md` — the simulation itself: the opcodes, the attributes each one needs, and
  the GPU path that will one day replace the CPU expansion.
