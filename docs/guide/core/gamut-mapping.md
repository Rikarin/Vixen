---
title: Gamut mapping
slug: core/gamut-mapping
kind: guide
area: Core
summary: Bringing a colour into what a display can actually show, by reducing chroma at constant lightness and hue rather than clipping channels.
api: [T:Vixen.Core.Mathematics.GamutMap, T:Vixen.Core.Mathematics.ColorGamut]
tags: [colour, oklab, oklch, css, wide-gamut, display-p3]
since: 0.1
status: preview
related: [ui/utility-composition, rendering/look-profiles]
---

## What it is

`GamutMap` takes a colour that a display cannot show and returns the closest one it can, using the
algorithm CSS Color 4 specifies: hold lightness and hue fixed, binary-search the chroma downward, and
stop as soon as a per-channel clip of the candidate is within one just-noticeable difference of it.

`ColorGamut` names the three destinations it knows — sRGB, Display P3 and Rec. 2020. They share the
D65 white point and differ only in where their primaries sit, so converting between them is a single
3×3 matrix on linear values.

Everything here works in **linear sRGB primaries**, including the results. A colour outside sRGB is
carried as linear values outside `[0, 1]`; `Vixen.Core.Mathematics.Oklab` produces exactly that, and
nothing in the styling pipeline clamps it.

## What it is for

Tailwind v4's palette — the engine's default — is authored in `oklch()`, and **two of its three
sampled colours are outside sRGB**: `blue-500`'s linear blue is `+1.053`, `emerald-500`'s linear red
is `−0.039`. On a P3 display both are showable and must be left alone. On an sRGB display both need
repairing, and how they are repaired is visible.

⚠ **Per-channel clipping is not a cheaper version of this, it is a different answer.** Clamping each
channel independently moves the hue, because a vivid red runs out of red first and keeps whatever
green and blue it had — it clips *towards orange*. Measured on this implementation at `L = 0.65`,
`C = 0.37`, clipping shifts the hue by up to **42.5°**; chroma reduction holds it to **5.5°**, and
that residual is the deliberate final clip described below.

⚠ **The destination is the display's gamut, not always sRGB.** Mapping a P3-showable colour to sRGB
anyway throws away precisely the chroma the panel was bought for. This is why the repair belongs at
presentation, where the swapchain's colour space is known, and why nothing earlier in the pipeline
clamps.

## Using it

```csharp compile
using Vixen.Core.Mathematics;

public static class Showing {
    public static Vector3 OnThisDisplay(ColorGamut display) {
        // A vivid Tailwind blue, as the parser produces it: linear, and past white on blue.
        var blue = new Vector3(0.078f, 0.435f, 1.053f);

        // On an sRGB display this reduces chroma; on a P3 one it returns `blue` untouched, because
        // the intent is relative colorimetric and the colour is already showable.
        return GamutMap.Map(blue, display);
    }
}
```

`FromLinearSrgb` and `ToLinearSrgb` rebase a colour between gamuts without repairing it. A
presentation pass that has chosen a Display P3 swapchain needs `FromLinearSrgb`: the same numbers
sent to a P3 surface unconverted are a *more saturated* picture, not a wider one.

The swapchain reports what it actually got. Ask for a gamut through `SwapChainDescription.Gamut` and
read the backend's gamut back — a surface that offered no wide colour space with enough precision
behind it stays in sRGB, and mapping to P3 regardless would over-saturate an ordinary display.

## Examples

**Choosing what to map against.** The gamut comes from the swapchain, never from a constant:

```csharp no-compile="a fragment; the swapchain and the colour come from the caller"
var target = swapChain.Gamut;                       // what the surface actually granted
var shown = GamutMap.Map(colour, target);
```

**Why the algorithm ends in a clip.** The search reduces chroma until the colour is within one JND of
the gamut boundary, then returns the *clipped* version rather than the reduced one. That is the
"local MINDE" step, and it is what recovers chroma near a concave patch of the gamut surface where a
pure reduction would give away more than it had to. `GamutMap.Clip` is exposed for that reason, and
using it alone is the mistake this page opens with.

**The constants are the specification's, not tuning.** `JustNoticeableDifference` is `0.02` and
`SearchEpsilon` is `0.0001`. CSS Color 4 fixes the first by analogy: in CIE Lab, where lightness runs
0–100, one JND under ΔE2000 is 2; Oklab's lightness runs 0–1, so the same threshold is a hundred
times smaller.

⚠ **The specification now offers three algorithms**, not one — this binary search, EdgeSeeker, and a
ray-trace variant — and lets an implementation choose among them. This is the one whose constants the
prose pins down and which has reference implementations to check against.

## See also

- [CSS Color 4 §14.2](https://www.w3.org/TR/css-color-4/#binsearch) — the algorithm and its pseudocode.
- `docs/plan/43-web-styling-parity.md` § D4 — why the palette forces this decision.
- `Vixen.Core.Mathematics.Oklab` — the space the search walks, and the one the distance is measured in.
- `Vixen.Core.Mathematics.ColorSpace` — the sRGB transfer function, which is a different thing from a gamut.
