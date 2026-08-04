---
title: The Standard Frame
slug: rendering/standard-frame
kind: guide
area: Rendering
summary: One engine-owned node that expands into the full frame graph — shadows, GI, reflections, the whole post chain — configured by seven semantic knobs.
api: [T:Vixen.Rendering.PostFx.StandardFrameAsset, T:Vixen.Rendering.PostFx.StandardFrameExtensions, T:Vixen.Rendering.Compositor.QualityTier, T:Vixen.Rendering.PostFx.ShadowMode, T:Vixen.Rendering.PostFx.GiMode, T:Vixen.Rendering.PostFx.ReflectionsMode, T:Vixen.Rendering.PostFx.AntialiasingMode, T:Vixen.Rendering.PostFx.ExposureMode, T:Vixen.Rendering.Compositor.ICompositorAssetTransformer, T:Vixen.Editor.Assets.Compositors.CompositorWriter]
tags: [rendering, compositor, presets, post-processing]
since: 0.1
status: experimental
related: [rendering/post-processing, rendering/shadows, rendering/lit-path, rendering/render-quality]
---

## What it is

`!StandardFrame` is a compositor node that stands for a whole frame. At build time it expands into
the same node graph a hand-authored document would contain — the resources with their extents, the
stages with their caster settings, the passes in their one workable order, and every seat/publisher
pair spelled out — so a project's entire frame document can be:

```yaml
version: 2
game: !StandardFrame
  quality: High          # Low | Medium | High | Epic
  shadows: Cascades      # Off | Cascades | Virtual
  gi: Probes             # Off | Ambient | Probes
  reflections: Screen    # Off | Probe | Screen
  antialiasing: Taa      # Off | Fxaa | Taa | TaaFxaa
  exposure: Automatic    # Fixed | Automatic
  output: SceneColour
```

The knobs are semantic on purpose: they say what the game wants, never how the frame is wired. At
full knobs the expansion is sample 13's `Frame.vxcompositor`; at none it is a sky, an opaque pass
and a tonemap. `quality:` selects a tier of the numeric sub-knobs (cascade resolutions, probe
budgets, march steps, tap counts) through the [render-quality waterfall](render-quality.md):
engine defaults, then the project's `RenderQuality.vxpreset` on `PostEffectFactory.Preset`, then
an inline `preset:` on the node itself, folded per parameter. A document that writes no `quality:`
takes the platform's pick — `GraphicsOptions.Quality`, handed through
`CompositorBuilder.Quality` — which is what a settings screen switches without editing the
document.

## What it is for

The compositor document is the most honest frame format there is — nothing renders that the file
does not say — and for exactly that reason it is unusable as a *default*: sample 13's document is
eleven hundred lines, and the audit that preceded doc 39 catalogued what those lines cost even
their own authors. The Standard Frame is the default path: the invariants the audit paid for
(atlas extents from the nodes' own arithmetic, load actions that respect the sky, the
TAA-before-fog ordering, seat lines that match compose slots) are encoded once, in engine code,
under test. Authoring stays for those who opt in, and the expansion produces the same object model
authoring produces — one builder, one node registry, no second pipeline.

## Using it

Register the effect-set factory, which a project using any post effect already does — the factory
implements the builder's document-transform seam, so registering it is the whole installation:

```csharp no-compile="the builder is the host's; see SceneRenderHost"
builder.Factories.Add(new PostEffectFactory());
```

Two facts stay the host's, exactly as they do for a hand-authored frame:

1. **Caster stages are extraction's.** With `shadows:` on, add `"Shadow"` to
   `GraphicsOptions.CasterStages`; with `antialiasing: Taa` or `TaaFxaa`, add `"Motion"` too. A
   frame document cannot decide what an object is extracted as.
2. **The ambient split is the material's.** `gi:` above `Off` emits the split targets and the
   ambient combine, which pay off when the shading pass runs with `ForwardPlus.SplitOutputs` on.

The `extensions:` lists are the three seams a project's own nodes splice into without forking the
frame: `afterOpaque` (after the Main pass, sharing its depth), `beforePost` (lighting is whole,
nothing post has run), `beforeUi` (after the output resource is written). The expansion's resource
names are sample 13's canonical ones — `SceneHdr`, `SceneDepth`, `ShadowAtlas` — and a document may
declare its own resources beside the node; redeclaring a canonical name *differently* is refused by
name at build time.

### Ejecting: `vixen frame explode`

The escape hatch, when the knobs stop being enough:

```bash
vixen frame explode Assets/Frame.vxcompositor            # writes Frame.exploded.vxcompositor beside it
vixen frame explode Assets/Frame.vxcompositor --in-place # replaces the document
```

It replaces the `!StandardFrame` node with the fully expanded document — every resource with its
extent, every stage with its caster state, every seat line — and the text carries a comment per
declaration saying why it exists and what its neighbours rely on, generated from the same prose the
expansion encodes. **One-way, deliberately**: the file says so at the top, and from then on it is a
hand-authored document like sample 13's. The exploded text is sparse — a member equal to its
record's default is not written — and it round-trips: reading it back binds a structurally
identical asset, so what the ejected file builds is exactly what the knobs built.

The pieces are reusable on their own: `PostEffectFactory.Transform(document, out var notes)` is the
expansion plus the comments, and `CompositorWriter.Write(asset, notes, header)` is the text — the
same pair the editor's explode button will drive.

## Examples

A frame for a stylised game with no GI and no meter, plus one custom full-screen pass:

```yaml
version: 2
game: !StandardFrame
  quality: Medium
  shadows: Cascades
  gi: Off
  antialiasing: Fxaa
  exposure: Fixed
  extensions:
    beforePost:
      - !FullScreen { name: Posterise, shader: Posterise, colourTargets: [Posterised], reads: [SceneHdr] }
```

Expanding in code — what the builder does internally, and what a test asserts against:

```csharp no-compile="the expansion is internal; documents reach it through PostEffectFactory"
var expanded = new PostEffectFactory().Transform(document, builder);
```

## See also

- [The post-processing node kinds](post-processing.md) — every node the expansion emits, and the
  ordering rules it encodes.
- [Shadow maps for the sun and the lamps](shadows.md) — what `shadows: Cascades` unfolds into.
- [Turning on dynamic global illumination](lit-path.md) — what `gi: Probes` unfolds into, and the
  host slots it needs to do more than nothing.
- `docs/plan/39-standard-frame-and-render-presets.md` — the design, and the incumbents it answers.
