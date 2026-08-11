---
title: The look profile
slug: rendering/look-profiles
kind: guide
area: Rendering
summary: One .vxlook asset carrying the project's artistic base — exposure target, meter clamps, grade, fog, lens — folded under every scene's volumes, never baked into the frame.
api: [T:Vixen.Rendering.LookAsset, T:Vixen.Rendering.BlendedGrading, T:Vixen.Rendering.BlendedToggle, L:13023, L:13024]
tags: [rendering, post-processing, compositor, look, presets]
since: 0.1
status: preview
related: [rendering/post-process-volumes, rendering/standard-frame, rendering/render-quality, rendering/post-processing]
---

## What it is

A `.vxlook` is one asset holding the artistic values a whole project shares: the exposure it is
pinned to, the range its meter may wander, its grade, its fog, its vignette. It is the bottom of the
post-process volume stack — Unity's Volume Profile by way of Vixen's own overlay model — and its
payload **is** `PostProcessSettings`, the same per-parameter opinion struct a volume authors:

```yaml
# Dusk.vxlook
!Look
settings:
  ev100: 12.5              # the fixed exposure, pinned in stops
  meterMinimumEv: 4        # the meter may not expose for darker than this…
  meterMaximumEv: 13.5     # …or brighter than this
  bloomThreshold: 1.2
  bloomKnee: 0.4
  fogColour: 1400 1200 1680   # ⚠ a radiance in cd/m², not a tint — see below
  fogDensity: 0.015
  vignetteIntensity: 0.35
  grading:
    shadows: { saturation: 0.9, gain: [0.92, 0.95, 1.1] }
    highlightsMin: 0.55
```

Every field is optional — doc 32's "says nothing / has an opinion" distinction — so a look that only
pins the exposure says nothing about anything else, and `null` is never `0`.

> ⚠ **`fogColour` is a radiance in cd/m², and it is the one field here a colour picker will get
> wrong.** Everything else on this list is a ratio or a stop; this one is a luminance the frame's own
> scene colour is lerped toward, and a lit frame's sky averages thousands of it. A value near one is
> not a subtler fog — it is a fade to black that is too faint to see, which is exactly how it went
> unnoticed in three separate passes. Prefer saying nothing: a `!Fog` node with no opinion laid over
> it takes the sky's mean radiance and the sun's illuminance from the scene, which is the number this
> field is trying to guess. See `FogRenderer.Colour`.

The precedence is fixed, four layers, never ambiguous:

| Layer | Who authors it |
|---|---|
| 1. Engine neutral defaults | the node values the document (or the Standard Frame) emits |
| 2. **Project look profile** | this asset |
| 3. Scene unbound volume | the level's base grade, per level |
| 4. Local volumes and overlays | rooms, doorways, gameplay |

The look loses to anything any volume says, per parameter, whatever the volume's priority — the
precedence is by *kind* of layer, not by number.

## What it is for

The Standard Frame deliberately emits *neutral* artistic values, because the audit behind doc 39
showed what art living inside pipeline structure costs: sample 13's dusk works only because
`ev100`, the meter's clamps and the fog colour all agree with the sky, and those agreements were
scattered across eleven hundred lines. They belong together, in one named artifact the editor can
edit live — and because the look reaches the frame through the volume fold at run time rather than
through the expansion, editing it relights the same document with nothing rebuilt.

The boundary with [`RenderQuality.vxpreset`](render-quality.md) is one rule: **look changes the
intent, quality changes only the fidelity and cost of the same intent.** Bloom threshold and knee
are look; bloom pyramid levels are quality. DoF aperture and focus distance are the *camera's* and
live on neither — the look's only word on defocus is the volume vocabulary's `maximumDefocus`
ceiling.

⚠ **Exposure is the one absolute in the stack.** A volume says `exposureCompensation` — an offset
that composes — but the look may pin `ev100` itself, because "this game's dusk is EV 12.5" is the
base those offsets compose over. The pin blends in stops, not linear exposure, and only reaches
frames whose exposure is *fixed*: a metered frame reads the meter's buffer, and the look constrains
that through `meterMinimumEv`/`meterMaximumEv` instead. The clamps are authored as EVs and
converted where consumed — the meter's own knobs are reciprocal linear multipliers, and that
inversion stays out of every document.

## Using it

Two ways in, and the document's wins:

```yaml
# Inline on the frame, the way `preset:` flows — no content IO in the pure expansion.
game: !StandardFrame
  quality: High
  look: !Look
    settings:
      ev100: 12.5
```

```csharp no-compile="config is the host's AppConfig"
// Or host-supplied: the loaded asset's address, resolved by AppGraphics at start-up.
config.Graphics.Look = "Looks/Dusk";
```

The seam underneath is small and stated: the `!StandardFrame` transformer deposits the document's
inline look on `CompositorBuilder.Look` (an output of the build — the look is never baked into the
emitted nodes, and the expansion with and without one is snapshot-identical); after `Load`, the
host hands whichever look won to `PostProcessVolumeSystem.Look`, where the fold lays it down first
at full weight. A game holding the asset already may assign `graphics.Volumes.Look` directly.

⚠ **A look opinion reaches nodes whose own knob is off in the variant sense.** The Standard Frame's
tonemap has no grade compiled in; a look's `grading:` turns the permutation on and blends from
`ColorGrading.Neutral` — never from a zeroed struct, whose gain of zero is a black frame.

## Examples

**Asking why it looks like this** — the resolved stack, per camera, bottom layer first:

```csharp no-compile="graphics is the host's AppGraphics"
var stack = new List<(string Layer, string Parameter)>();
graphics.Volumes.Contributions(stack);

// ("look", "ev100"), ("look", "fogDensity"), ("scene", "saturation"),
// ("volume(priority 3)", "fogDensity") — the last claimant of a parameter is on screen.
```

**A scene overriding one thing.** The look pins the whole project's fog; one level wants it
thicker. An unbound volume with `fogDensity: 0.05` says exactly that and nothing else — every other
parameter stays the look's, which is the entire point of per-parameter opinions.

**Switches flip at half weight.** `fogHeightFalloff` and `fogSunScattering` are booleans, and a
weighted fold cannot crossfade one — so a `BlendedToggle` takes the heavier side, flipping at the
blend's midpoint. The look always contributes at weight 1, so this only shows where a bounded
volume claims a switch and fades.

## See also

- [Making a room look different](post-process-volumes.md) — the volume system the look is a layer
  of, and the opinion model both share.
- [The Standard Frame](standard-frame.md) — the neutral frame the look dresses, and the `look:`
  knob.
- [Scaling the frame with render quality](render-quality.md) — the other named asset, and the
  intent/fidelity boundary between the two.
- `docs/plan/39-standard-frame-and-render-presets.md` § The look profile — the design and the
  four-layer precedence.
