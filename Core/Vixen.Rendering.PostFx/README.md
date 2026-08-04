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

## `!StandardFrame` — the frame most projects never have to write

`StandardFrameAsset` is doc 39's preset node: seven semantic knobs that expand, at build time, into
the same graph sample 13 hand-authors — resources with extents from the shadow nodes' own
arithmetic, the caster and particle stages, every load list and seat line. It lives here rather
than beside the compositor schema because the expansion emits this project's node kinds, and it
reaches the builder the way everything here does: `PostEffectFactory` implements
`ICompositorAssetTransformer`, so the registration above is also the installation. The expansion is
deterministic and the tests snapshot its structure; the artistic numbers stay at node defaults,
because look belongs to the `.vxlook` profile of a later increment.

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

## Auto-exposure is the one effect here that is not a full-screen pass

Everything else is a fullscreen triangle writing one target, because that is what a fragment stage can
do. `AutoExposureRenderer` cannot be written that way, and the reason is precise: **its output is not
an image.** It reduces the frame to a single number and leaves it in a buffer, and a fragment stage
cannot write a buffer at all. So it is a chain of `ComputeRenderer` dispatches — K2's node, spent —
halving a 512-wide luminance image down to 1×1 and then easing the stored exposure toward what that
last texel says.

⚠ **The exposure buffer is imported into each frame, not declared in it.** A declared buffer lives for
one frame; this one holds the value the eye has adapted to. Adaptation eases *toward* a target from
where it already is, so a buffer the graph re-declared each frame would ease from zero every time — an
exposure that never converges. Importing also tells the graph the memory belongs to somebody else, so
it does not alias next frame's exposure over this one's.

⚠ **Into the frame, not onto the compositor**, which is what `TemporalAntialiasingRenderer` does with
its history. `GraphicsCompositor.BufferImports` is folded into the frame *before* any node builds, so
a node adding to it during its own build is a frame too late: the first frame refers to a buffer
nothing bound and every frame after it works, which is the worst shape a bug can have. It cost one
test run to find.

**The tonemapper reads it through a permutation.** `TonemapRenderer.ExposureBuffer` names the
resource; naming one selects the variant that declares the binding and reads it, and leaving it empty
selects the variant every consumer compiled to before auto-exposure existed — no binding declared and
none bound. That is what makes the change additive, and both directions are asserted, because a
regression that always declared the binding is an incomplete descriptor set in every frame that does
not measure.

**The first reduction takes the log and the rest do not**, as a permutation rather than a branch.
Averaging luminance directly lets one specular highlight drag the whole frame's exposure down; the
geometric mean is what "the middle of this scene's brightness" means. Every later step then averages
values that are already logarithmic.

⚠ **The chain starts at 512 rather than at the frame's size.** Reducing 4K to 1×1 is eleven dispatches
and measures nothing a 512-wide version does not — exposure is a property of the whole image, and
every step after the first is averaging an average.
