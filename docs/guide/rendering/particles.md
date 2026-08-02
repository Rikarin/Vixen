---
title: Drawing particles
slug: rendering/particles
kind: guide
area: Rendering
summary: Turning a VfxSystem into pixels — the feature that expands it, the vertex format it expands into, and the four wires nothing else connects.
api: [T:Vixen.Rendering.Features.ParticleRenderFeature, T:Vixen.Rendering.ParticleVertices, T:Vixen.Vfx.VfxSystem, T:Vixen.Vfx.VfxCompiledGraph, T:Vixen.Vfx.VfxSpawner, T:Vixen.Vfx.VfxOperation, T:Vixen.Vfx.VfxRenderer, T:Vixen.Vfx.ParticleVertex]
tags: [rendering, vfx, particles, compositor]
since: 0.1
status: stable
related: [rendering/lit-path, rendering/shadows]
---

## What it is

A `VfxSystem` simulates particles and knows nothing about graphics; `ParticleRenderFeature` turns
what it holds into geometry once a frame and draws it. Four pieces, and none of them finds the
others by itself:

| Piece | Says |
|---|---|
| `VfxCompiledGraph` | what spawns, what a particle starts as, what happens to it |
| `ParticleRenderFeature` | expands each live particle into a camera-facing quad and draws the run |
| `ParticleVertices.Schema` | how those quads reach a pipeline — position, texcoord, colour, by name |
| `ParticleSprite.rvn` | the shader that takes those three and returns a soft disc |

⚠ **There is no particle component and no runtime loader for `.vxvfx`.** A `.vxvfx` is a node graph
the editor compiles *in the editor process*, so a game cannot load one by address — see
`docs/overview.md`. A graph is written in code, a render object is added by hand, and something has
to call `VfxSystem.Step` every frame. That is the state of this path, and everything below is
written around it rather than pretending otherwise.

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

**Three.** The feature. `WorldRenderer` builds one, registers `ParticleVertices.Schema` at layout
index 1 and gives it a material feature of its own. Point it at the camera:

```csharp no-compile="a fragment against a built compositor, whose views a document made"
renderer.Particles.View = renderer.Host.Builder.Views["Camera"];
```

⚠ `View` is not optional in a frame with shadows in it. Left unset the feature takes `Views[0]`,
which is whichever view the `!ShadowMap` node registered first.

**Four.** A graph, a render object and a material, per effect:

```csharp no-compile="a fragment; the graph, the bound and the material are the caller's"
var effect = new VfxSystem(graph, seed);

var id = renderer.Host.System.Objects.Add(
    new() { Bounds = bound, Stages = stage.Mask, FeatureIndex = renderer.Particles.Index }
);

renderer.Particles.SetSystem(id, effect);
renderer.ParticleMaterials.Assign(renderer.Host.System, id, material);
```

⚠ **`ParticleMaterials`, not `Materials`.** A sub-feature has exactly one owner, so the particle
feature holds a material feature of its own — and `ParticleRenderFeature.Draw` asks *its* material
sub-feature which variant each object resolves to, skipping any object with no answer. A material
assigned through the mesh path leaves an effect that expands its quads every frame and draws none of
them.

⚠ **The bound is written once and nothing recomputes it.** It has to cover where the particles can
drift to, because the frustum culls the whole effect rather than a particle.

**Five.** Step it. Nothing in the engine does:

```csharp no-compile="a fragment inside whatever steps the effect"
effect.Step(Time.DeltaSeconds);
```

## Examples

The material. `ParticleSprite` declares no compose slots, so there is nothing for
`MaterialCompiler.Compile` to compose — but a compilation is the whole library and refuses any slot
left unbound, so it still names the defaults:

```csharp no-compile="a fragment; the parameter keys are interned from the shader's own names"
var material = new Material("ParticleSprite") { Composition = MaterialCompiler.PassComposition() };

material.Parameters.Set(ParameterKeys.New<float>("ParticleSprite.emissive"), 40000f);
material.Parameters.Set(ParameterKeys.New<Vector4>("ParticleSprite.tint"), Vector4.One);
material.Parameters.Set(ParameterKeys.New<float>("ParticleSprite.edgeSharpness"), 2.2f);
```

`emissive` is in the scene's own photometric units, not a 0..1 opacity: a spark that is to bloom has
to be brighter than the bloom's threshold, which in a physically lit frame is in the thousands.

An ember off a hot lamp — a trickle, born in a small sphere, rising and cooling:

```csharp no-compile="a graph, shown out of the method that returns one"
VfxCompiledGraph.Compile(
    [VfxSpawner.AtRate(6f)],
    [
        new(VfxOpcode.PositionInSphere, new Vector4(lamp.X, lamp.Y, lamp.Z, 0.16f)),
        new(VfxOpcode.VelocityInCone, new Vector4(0f, 1f, 0f, 0.7f)) { B = new(0.12f, 0.35f, 0f, 0f) },
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

⚠ **The opcodes are world-space.** There is no emitter transform: a particle's position is whatever
the initializer's vector said. So an effect that follows something is a graph per instance with the
position baked in, which is why the lamp's coordinates are in that first line.

⚠ **`Integrate` last.** The updaters above it write velocity and this turns velocity into position;
first, every particle moves on the previous step's forces.

⚠ **Seed from something stable**, never from a clock, if the frames have to be reproducible.
`VfxRandom` hashes the particle's identifier, the system's seed and the operation's salt, so the
seed is the only thing that can make two identical runs differ.

The working example is `Samples/13-ThirdPersonShooter`: `ArenaEmbers` holds the graph,
`Arena.Embers` does the wiring, `EmberDrift` steps it, and `Frame.vxcompositor` has the stage and
the pass.

## See also

- [The lit path](lit-path.md) — where set 0, set 1 and set 2 come from, which is the same division
  the particle pipeline reads them under.
- [Making everything cast a shadow](shadows.md) — the other reason a schema matches attributes by
  name rather than by location.
- `Core/Vixen.Vfx/README.md` — the simulation itself: the opcodes, the attributes each one needs, and
  the GPU path that will one day replace the CPU expansion.
