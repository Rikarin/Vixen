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
| `SmaaRenderer` | colour | The same inputs and a better answer, for three passes instead of one: it walks the whole edge and looks the coverage up rather than guessing a direction, so it leaves the texture beside an edge alone. Owns a generated coverage table, and is the only node here that uploads one |
| `TemporalAntialiasingRenderer` | colour, history, motion, depth | The default where it can run. Owns its history and alternates it, because a pass cannot read the target it writes. Its sub-pixel offset is applied by `CameraExtractionSystem`, not here: what it offsets is the projection |
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
because look belongs to the `.vxlook` profile — `LookAsset`, whose payload is the volume system's
own `PostProcessSettings`, folded under every scene's volumes at run time. The node's `look:` never
enters the emission (the expansion with and without one is snapshot-identical); the transformer
deposits it on `CompositorBuilder.Look` and the host hands it to `PostProcessVolumeSystem.Look`,
which is what makes editing the look relight the same document with nothing rebuilt.

The numeric sub-knobs a `quality:` tier folds come from the `RenderQuality` waterfall: the
engine's complete tier table (`RenderQuality.EngineDefaults`, expressed as the same
`RenderQualityAsset` a project's `RenderQuality.vxpreset` authors), a project preset handed to
`PostEffectFactory.Preset`, and an inline `preset:` on the node — folded per parameter by
`RenderQuality.Resolve` into the flat `ResolvedQuality` the emission reads. A frame that names no
tier takes `CompositorBuilder.Quality`, which the host sets from `GraphicsOptions.Quality`; the
fold is pure and takes assets rather than addresses, so loading the `.vxpreset` stays the host's.

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

MSAA resolve. It needs a shader that does not exist yet rather than a pass over one that does —
which is the difference between this list and the one above it.

⚠ **SMAA was on that list and is now on the one above it, with one part of it still owed.** Diagonal
pattern detection — the reference's second, optional detector for silhouettes near 45°, with a
coverage table of its own — is not implemented, so those edges fall through to the orthogonal path.
That is `SMAA_DISABLE_DIAG_DETECTION`, a build the reference itself ships, rather than an
approximation of the detector; and the edge search is a per-texel loop rather than the reference's
`SearchTex`-accelerated two-texel walk, which is the same answer at twice the iterations and one
fewer generated table.

⚠ **This list used to be four items longer, and the four went different ways.** GTAO, screen-space
reflections and depth of field all shipped: `Ssao.rvn` *is* GTAO rather than the classic hemisphere
walk, `ReflectionRenderer` is the reflections node, `DepthOfFieldRenderer` the defocus one, and all
three are arms of `PostEffectAssets`' asset-to-node switch that `StandardFrame` emits on the tier
flags — so a project gets them by asking for a quality tier rather than by authoring a document.

⚠ **Colour grading as an asset is the interesting one, because exactly one half of it is reachable.**
The consuming half is finished and reached: `TonemapAsset.Lut` names a 3D table, `TonemapRenderer`
binds it and flips `Tonemap.rvn`'s `UseLut` permutation, and `AssetTextureSource` makes a `Texture3D`
for it. The authoring half is not. `CubeLutImporter` exists and parses `.cube`, and `[Importer]` is a
declaration nothing scans for — so until it is added to `BuiltInImporters` by hand, a `.cube` dropped
into a project falls through to `RawImporter` and the finished consumer has nothing to load. That
registration is task #167; the sentence above it is the one to change when it lands.

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

⚠ **The histogram meter declares one image and produces nothing in it.** Its clear, build and resolve
all bind `target` and `average` because a descriptor set is written whole or not at all, and what the
build takes from that image is `GetDimensions` — the grid it meters the frame on. Declaring it as a
write is what made sample 13 report VX2101 twice at every launch: three producers with no reader
between them is the shape of a frame's work thrown away, and nothing was being thrown away. It is
owned by the node and declared through `ComputeRenderer.Bound`; see the compositor's README.

⚠ **The frame that creates the exposure buffer is told the scene has been there for hours, and that
is the launch.** A fresh device-local allocation holds no exposure, and the claim that one was seeded
to `1` lived in this class's remarks and in none of its code — so the adaptation eased from zero
toward its target and took about five time constants to arrive. At sample 13's `darkenRate` of 0.6
that is eight and a half seconds of a black screen slowly lighting, which reads as a broken renderer
rather than as an eye adjusting. The blend is `1 - exp(-dt·rate)` and saturates, so the fix is the
elapsed time and not a second path through the adaptation: the first frame lands on what it metered
and every frame after it eases at the authored rate, with the rates, the clamps and the value it
converges on all untouched. **Measured on sample 13:** frame 1's mean channel went from 8.3 to 45.7,
frame 8 from 10.8 to 37.8 against a settled 38.4, and frame 1024 stayed at 38.4.
