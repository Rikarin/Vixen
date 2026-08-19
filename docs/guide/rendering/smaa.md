---
title: SMAA
slug: rendering/smaa
kind: guide
area: Rendering
summary: Subpixel morphological antialiasing — three passes that find the whole edge, walk it to both ends and look the coverage up, rather than guessing a direction from one neighbourhood.
api: [T:Vixen.Rendering.PostFx.SmaaAsset, T:Vixen.Rendering.PostFx.SmaaRenderer, T:Vixen.Rendering.PostFx.SmaaAreaTexture, R:PostFx/Smaa]
tags: [rendering, post-processing, antialiasing, compositor]
since: 0.1
status: preview
related: [rendering/post-processing, rendering/standard-frame, rendering/choosing-a-frame]
---

## What it is

The third antialiasing node, between `!Fxaa` and `!TemporalAntialiasing` in both cost and quality.

```yaml
!Smaa
name: Edges
source: SceneGraded
output: Display
```

Three passes over one shader, plus a lookup table the node generates and uploads once:

| Pass | Reads | Writes |
|---|---|---|
| edge detection | the frame's luminance | a two-channel mask: an edge on each pixel's left and top boundary |
| blending weights | the mask, the coverage table | how far each pixel and its neighbour reach across their shared boundary |
| neighbourhood blending | the frame, the weights | one bilinear tap per pixel, placed by the weight |

## What it is for

Edges that FXAA softens badly and TAA cannot reach.

FXAA reads one pixel's neighbourhood, estimates which way the edge runs and blends along the
estimate — so it cannot tell a silhouette from a texture, and softens both. SMAA finds the edge
first: a run of edge texels bounded by two crossing edges *is* a silhouette, its sub-pixel position
follows from the run's length and where in it the pixel sits, and the coverage that follows is a
table lookup rather than a guess. Detail beside an edge is left alone because it was never an edge.

TAA is still the default where it can run, because it adds information rather than hiding its
absence — but it needs motion vectors, a history and a jittered projection, and a frame that has
none of those (a still image, a debug view, a platform with no velocity pass) has this instead.
`!TemporalAntialiasing` and `!Smaa` also compose: `antialiasing: TaaSmaa` on a `!StandardFrame`
emits both, the resolve converging the still frame and SMAA catching what its history clipped.

## Using it

**Put it after the tonemap.** `!Fxaa`'s reason: the edges a viewer sees are the ones the curve made.
Its thresholds are *relative* rather than absolute, so a document that puts it earlier gets a working
filter rather than a silent no-op — but it is then finding edges in scene-referred light, which is
not the same set.

A `!StandardFrame` places it correctly on its own:

```yaml
!StandardFrame
antialiasing: Smaa      # or TaaSmaa, which keeps the velocity pass
output: Display
```

| Property | What it is |
|---|---|
| `source` | the image it antialiases |
| `output` | what the result is published under |
| `edgeThreshold` | the **relative** local contrast a boundary needs, 0 to 1. A tenth is the reference's default and means "a tenth of the brightest thing nearby" |
| `contrastAdaptation` | how much steeper a nearby contrast may be before this edge is discarded as the flat side of something sharper. Two, which is what keeps the filter off a soft gradient |
| `lumaFloor` | the luminance below which the frame is treated as flat, in the frame's own units |

⚠ **`edgeThreshold` is relative, and that is not a refinement.** This engine's frame is metered in
cd/m² and a tonemapped one is not; an absolute threshold means "every boundary" in the first and "no
boundary at all" in the second, and a pass that finds no edges is pixel-identical to a pass that never
ran. Every luminance in `Smaa.rvn` is divided by the brightest in its neighbourhood before anything
is compared, which is why one number is right on both sides of the curve. `lumaFloor` is the clamp
under that divisor, because a division by the brightest thing in a black neighbourhood is an
amplifier with unbounded gain.

⚠ **Diagonal pattern detection is not implemented.** The reference has a second, optional detector
for silhouettes near 45°, with a coverage table of its own; without it those edges fall through to
the orthogonal path, which treats them as the staircases they are. That is the reference's own
`SMAA_DISABLE_DIAG_DETECTION` build rather than an approximation of one, and it is the single part of
SMAA 1x this node does not do.

## Examples

The coverage table is `SmaaAreaTexture`, and it is **generated rather than shipped**. The reference
distribution carries it as a 179 KB byte array in a header — a binary blob nobody can review and
nothing can regenerate — and it is an analytic function: the area under a straight line, clipped to
one pixel column. So it is written as the arithmetic that produces it, and the tests pin the values a
pencil can check:

```csharp no-compile="illustrative — the node generates and uploads this itself"
// A run one pixel long with the silhouette turning down at its left end is a triangle half a pixel
// each way: an eighth of a pixel of coverage below the line, and none above.
var (below, above) = SmaaAreaTexture.Coverage(pattern: 1, left: 0, right: 0);
```

The table is 80×80 rather than the reference's 160×560: the right half of that texture is the
diagonal patterns and the extra height is seven sub-sample offsets for SMAA S2x and 4x, and this
engine has neither — so those texels would be zeroes with nothing to read them.

**What the filter is worth, measured.** `SmaaImageTests` renders a hard step edge running at one
pixel in four and measures how far it is from straight: the per-row brightness sums *are* the
boundary's position, and the root-mean-square residual against their own best-fit line is the
staircase. Quantising a line to whole pixels leaves about 1/√12 = 0.289 of a pixel; the fixture
measures 0.279 on the hard edge and **0.017 after SMAA**, in both orientations. A 12/255 edge — well
under an absolute threshold of 0.1 — resolves to 0.042, which is the relative threshold doing the
thing it exists for.

## See also

- [The post-processing node kinds](post-processing.md) — every node a document can name, and the
  order they have to run in.
- [The standard frame](standard-frame.md) — `antialiasing:` and the rest of the seven knobs.
- [Choosing a frame](choosing-a-frame.md) — which antialiasing a project should be asking for.
