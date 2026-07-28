# Vixen.Rendering.PostFx

The post-process effect set: what happens to a frame after the scene is drawn.

Every entry is a node over a shader in [`Raven/Library/PostFx`](../../Raven/Library/PostFx), and almost
every one is a single `FullScreenRenderer` — three vertices, one draw, a descriptor set of source
textures and a uniform block. `PostEffectRenderer` holds that plumbing; an effect answers four
questions: which shader, which permutations, which textures on which bindings, and its own parameters.

## Why a project of its own

The compositor can already express a post chain, and did: `FullScreenRenderer` lives in
`Vixen.Rendering`, which is where the machinery belongs. What did not exist was the *set* —
doc 06 lists fifteen effects and the library had shaders for eight of them that nothing in the engine
called. A shader with no pass compiles, validates and shades nothing, which is the same failure the
material system had with its BSDF layers.

The split is by what changes: the render system and the compositor are the engine's spine, and an
effect is content-shaped — added, removed and reordered per project. A game that ships no outline
should not link one.

## What is here

| Effect | Reads | Notes |
|---|---|---|
| `FxaaRenderer` | colour | The fallback antialiasing: needs no history, no motion vectors, no depth. Softens texture detail along with geometry, which is the trade |
| `TemporalAntialiasingRenderer` | colour, history, motion, depth | The default where it can run. Owns its history and alternates it, because a pass cannot read the target it writes |
| `SharpenRenderer` | colour | Contrast-adaptive, to put back what antialiasing and upscaling took out |
| `AmbientOcclusionRenderer` | depth, normals | Half resolution by default, which is the standard trade for an effect that is low frequency by nature |
| `FogRenderer` | colour, depth | A post-process because fog depends on distance, which the depth buffer already holds for every pixel |
| `OutlineRenderer` | colour, depth, normals, mask | Depth and normal discontinuities. The editor's selection highlight and the stylised look that goes with cel shading |
| `VignetteRenderer` | colour | Vignette, chromatic aberration and grain: three permutations in one pass, because they are one look and each is one or two taps |

| `BloomRenderer` | colour | The dual-filter pyramid: nine textures and nine passes out of one line, all transient, so the whole chain vanishes when nothing reads the result |
| `TonemapRenderer` | colour, grading table | What every frame ends with. The 3D LUT the shader has always taken finally has something that binds one |

## A document can name any of them, including yours

`CompositorBuilder` turns an authored document into a running compositor by switching on the asset's
type — which it can only do for the kinds it defines. This project is downstream of it, so a case for
`!Bloom` in that switch would be a cycle, and a document could only ever name node kinds the engine
itself shipped.

`ISceneRendererFactory` is the seam. Whoever defines a node kind supplies the factory that builds it:
the asset carries the values, the factory carries what to make of them, and the builder carries the
device, the module cache and the allocators that neither of the other two can. `PostEffectFactory` is
this project's, and a host registers it once:

```csharp
builder.Factories.Add(new PostEffectFactory());
```

A game's own effect is a node kind on exactly the same terms — a `[DataContract]` record for the YAML
tag and a factory — which is what makes "the frame is data" true past the boundary of this
repository.

## The bindings are generated, not written down

Every effect names `FxaaKeys.SourceBinding` rather than `1`. A binding index is assigned by Raven from
declaration order within a set, so adding a texture above another in the `.rvn` renumbers everything
below it — and a node holding the old number gets a validation error at best and the wrong texture at
worst. The keys come from the reflection checked in beside each shader, which
`Vixen.Raven.Tests` regenerates and compares, so they cannot drift from the shaders without a test
saying so.

The same applies to permutations: each effect passes the generated `UsedPermutationKeys`, which is the
list Raven reported the shader actually branched on. A key the shader ignores must not reach the effect
key, or the cache splits into variants that compile to the same bytes.

## What a pass must do together

`Read` adds the binding *and* records the graph read, and the two cannot be separated: the binding is
what the shader samples it through, and the read is what orders this pass after whatever wrote the
texture and keeps that producer from being culled. One without the other is either a validation error
or a race, and neither shows up as anything but an intermittently wrong frame.

## What is not here yet

SMAA, MSAA resolve, GTAO, screen-space reflections, depth of field and colour grading as an asset.
Each needs a shader that does not exist yet rather than a pass over one that does — which is the
difference between this list and the one above it.

`AutoExposure.rvn` is also still unwired. It is not a full-screen effect: it is two compute passes over
a histogram and a buffer that survives the frame, so it needs the compute node rather than this one.
