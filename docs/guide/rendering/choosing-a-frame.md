---
title: Choosing a frame
slug: rendering/choosing-a-frame
kind: guide
area: Rendering
summary: The two-audience story — start with the Standard Frame's knobs, explode the document when you need surgery, and hand-author only when the frame itself is the subject.
api: [T:Vixen.Rendering.PostFx.StandardFrameAsset, T:Vixen.Rendering.PostFx.PostEffectFactory, T:Vixen.Rendering.Compositor.QualityTier, L:13022]
tags: [rendering, compositor, presets, getting-started]
since: 0.1
status: preview
related: [rendering/standard-frame, rendering/render-quality, rendering/post-processing, rendering/timing-the-frame]
---

## What it is

Vixen has exactly one way to describe a frame — the compositor document — and two ways to write
one. The first is seven lines: a [`!StandardFrame`](standard-frame.md) node whose knobs say what
the game wants (`shadows: Cascades`, `gi: Probes`) and whose build-time expansion emits the same
node graph a hand-authored document would contain. The second is the expansion's output itself,
every resource and pass and seat line spelled out, which is what sample 13 ships and what
`vixen frame explode` writes. This page is the decision between them, and it is deliberately not a
hard one: the knobs are the answer until the day they are not, and that day has a command rather
than a rewrite.

There is no third renderer hiding behind either form. The document reader produces an object
model, the builder consumes the object model, and the preset node is just one more way of making
one — which is the lesson both incumbent engines paid for differently, and the reason a knobs
project and an authored project never diverge in what they can express.

## What it is for

The compositor document is the most honest frame format there is: nothing renders that the file
does not say. That honesty is exactly what makes it unusable as a *default* — sample 13's document
is eleven hundred lines, and the audit that preceded doc 39 catalogued the ways those lines were
silently wrong even for their own authors. So the default path became engine code under test, and
authoring became the opt-in. Start with the knobs because the invariants the audit paid for — atlas
extents derived from the nodes' own arithmetic, load actions that respect the sky, the
TAA-before-fog ordering — are encoded once in the expansion instead of transcribed by hand into
your project.

What each knob costs is worth knowing before turning it up, because a knob is a set of passes:

- **`shadows:`** — `Cascades` re-draws every caster once per cascade into the sun's atlas and adds
  the lamps' tile atlas; `Virtual` adds the page-backed map on top, with the cascades as its
  fallback. The scene pays in draws, scaled by cascade count and resolution — which are
  [tier numbers](render-quality.md), not document edits.
- **`gi:`** — `Ambient` buys the occlusion pair (distance-field and screen-space) over the split
  targets and the ambient combine; `Probes` adds the clipmap, the irradiance field, the surface
  cache and the screen-probe gather. The biggest single step in frame cost the node offers, and
  the budget lives in the tier's GI group.
- **`reflections: Screen`** — a screen-space march per pixel, bounded by the tier's step count.
- **`antialiasing:`** — `Fxaa` is one cheap post pass; `Taa` emits the velocity pass, so every
  moving mesh is drawn again into `Motion`, and the host must extract it there.
- **`exposure: Automatic`** — a histogram reduce and a metering buffer the tonemap reads; `Fixed`
  trusts the camera.
- **`quality:`** — no passes of its own: it picks which column of the
  [quality waterfall](render-quality.md) feeds every number above, and a document that omits it
  hands the choice to `GraphicsOptions.Quality`, which is what a settings screen switches.

## Using it

Start every project the way `dotnet new vixen-game` does: the seven-line document, the
`PostEffectFactory` registration in `OnConfigure` that makes it bind, the caster stages the knobs
need, and an empty `RenderQuality.vxpreset` for the day a tier needs overriding. Tuning happens in
this order, and each step is deliberately smaller than the next:

1. **Turn the knobs.** Feature on, feature off, tier up, tier down. The document stays seven
   lines.
2. **Override a tier.** When High is right except for one number, the number goes in
   `RenderQuality.vxpreset` — cascade resolution, probe budget, march steps — and the document
   still says `quality: High`.
3. **Splice, don't fork.** One custom full-screen pass belongs in the node's `extensions:` lists
   (`afterOpaque`, `beforePost`, `beforeUi`), which exist so that one pass never becomes a
   hand-maintained copy of the whole frame.
4. **Explode.** When the surgery is structural — reordering passes, changing what a target holds,
   removing a link the expansion always emits — run `vixen frame explode Assets/Frame.vxcompositor`.
   It replaces the node with the fully expanded document, comments included, and the header says
   what the trade was: one-way, every line yours, nothing regenerates it.

### The frame tells you when it is wrong

A document that has been exploded or hand-authored can express things nothing checks at load: a
resource written twice with nothing reading the first write, a pass whose output nobody wants. The
render graph finds those while it builds and the host reports them as log event **13022**, once per
distinct finding rather than once per frame — because a warning repeated sixty times a second is a
warning its reader has muted.

```
warn  VX2101: 'Blur.Horizontal' writes 'Blur.Scratch' and 'Blur.Vertical' overwrites it before
      anything reads it — the first write is discarded every frame.
```

⚠ These are frames that draw. Nothing throws, nothing is missing from the picture, and the work is
simply spent and dropped — which is why it has to be a line in a log rather than an exception.

⚠ **And read it as a claim about the declaration, not only about the passes.** The graph knows what
a node said it does, which is not always what it does: sample 13's meter reported this pair for
months because its histogram declared a write of the image its `target` binding has to name and
never stores a texel into. Nothing was being discarded. Before rearranging passes, check that each
one declares what it actually touches — `Reads`, `Writes`, and `Bound` for the storage image a
variant is obliged to bind and produces nothing in.

Hand-author from scratch only when the frame itself is the subject — a renderer experiment, a
non-standard pipeline, a golden test. That is sample 13's territory: its `Frame.vxcompositor` is
kept authored *because* it is the showcase and the test bed, and its eleven hundred lines are the
honest price of that position. If you are not trying to hold that position, the explode output is
the same document with the guardrails already applied.

### A frame you replace has to be released

Loading a document builds a tree of nodes, and **some of those nodes own device memory**. A cached
shadow atlas is the clearest case: a cache has to survive the frame, every `!Resource` in a document
is transient by definition, and the graph's pool exists to recycle precisely the memory a cache must
keep — so the node holds its own texture instead. Sample 13's two shadow caches are 94 MiB together.

So a `GraphicsCompositor` owns what was built for it, and disposing it is what gives that memory
back. If you use `SceneRenderHost`, this is already done for you: `Load` releases the tree it
replaces and `Dispose` releases the one still installed. If you build a compositor yourself —
`CompositorBuilder.Build` directly, which is what the editor's viewport does — then it is yours:

```csharp no-compile="a fragment of a host's reload, against a builder and a document it already has"
var built = builder.Build(document);   // build first: a document that fails to bind throws here

previous?.Dispose();                   // and only then release the frame it replaces
previous = built;
```

⚠ **In that order.** A document that does not bind throws out of `Build`, and the correct response
is to go on drawing the frame you already had — which is only possible if nothing was freed before
the new tree existed. (A build that throws releases its own half-finished tree, so nothing is
stranded either way.)

⚠ **No idle is needed, and none should be added.** Every `IGraphicsDevice.Destroy` is deferred by
the backend until the frames that could still reference the object have retired, which is the same
path a node uses to release a texture. What is not safe is disposing a compositor *from inside* a
frame it is building — but a reload happens between frames anyway.

⚠ **A node you appended yourself stays yours.** `SceneRenderHost.Debug` is put into every tree the
host builds and is meant to outlive each of them, so ownership follows *who built the node*, not
what the tree can reach. Build a node in your own code and you dispose it in your own code.

## Examples

The whole of a new project's frame authoring, before and after the day the knobs stopped being
enough:

```yaml
# Day one — Assets/Frame.vxcompositor, in full: what `vixen new` writes.
version: 2
game: !StandardFrame
  quality: High
  shadows: Cascades
  gi: Off                # Ambient and Probes also need their host halves — see the sample below
  reflections: Off
  antialiasing: Taa
  exposure: Automatic
  output: SceneColour
```

`Samples/03-PbrShowcase` is the template's document plus the knobs that ask something of the host —
`gi: Ambient`, the caster stages, the shading permutations — each paid where it is marked.

```bash
# Much later — the frame needs surgery the knobs cannot express.
vixen frame explode Assets/Frame.vxcompositor --in-place
```

A knob whose cost was wrong for one shipping tier, fixed without touching the document:

```yaml
# RenderQuality.vxpreset — Low keeps its shadows but pays less for them.
low: !QualityTierOverrides
  shadows: !ShadowQuality { cascadeResolution: 1024, shadowDistance: 40 }
```

## See also

- [The Standard Frame](standard-frame.md) — the node, its knobs, its extension seams and the
  explode contract in detail.
- [Render quality presets](render-quality.md) — the waterfall behind `quality:`, and which group
  each cost above is budgeted in.
- [The post-processing node kinds](post-processing.md) — what the expansion emits, named one node
  at a time, for reading an exploded document.
- `Samples/13-ThirdPersonShooter/Assets/Frame.vxcompositor` — the worked example of the
  hand-authored end, kept authored because it is the showcase and the test bed.
- `docs/plan/39-standard-frame-and-render-presets.md` — the design, and the two incumbents whose
  lessons it encodes.
