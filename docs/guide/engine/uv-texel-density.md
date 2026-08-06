---
title: UV texel density
slug: engine/uv-texel-density
kind: guide
area: Engine
summary: Uniform texels per world unit, with a per-material override and a per-chart multiplier — and a measurement that says what the atlas actually got.
api: [T:Vixen.Geometry.Uv.UvDensity, T:Vixen.Geometry.Uv.PackSettings, T:Vixen.Geometry.Uv.UvTexelDensity, T:Vixen.Geometry.Uv.UvIsland]
tags: [geometry, uv, atlas, packing, texture, texel-density]
since: 0.1
status: preview
related: [engine/uv-packing, engine/uv-stacking, engine/uv-charting, engine/uv-flattening]
---

## What it is

`UvDensity` is where "every chart gets the same number of texels per metre, except these ones" is
said. It has four members: `Reference` picks a density from the islands themselves, `Weight` applies a
per-chart multiplier, `Override` applies a per-material density, and `Measure` says what each island
actually got once the packer had finished.

Everything it does is a change to `UvIsland.Scale`. The islands' coordinates are never rewritten.

## What it is for

⚠ **Non-uniform texel density is invisible in the atlas and glaring in the game.** The classic symptom
is a character's face at half the resolution of their boots — nothing about the packed sheet looks
wrong, and nobody notices until the asset is in a scene next to something correct.

So uniform is the answer you want, and the two exceptions are real: a material that deserves more
(a face, a decal sheet, a hero prop) and a single chart that deserves more (the front of a sign, the
readable part of a label).

## Using it

Set `PackSettings.TexelDensity` and every island is brought to that many texels per world unit.

```csharp no-compile="a fragment; `islands` came from UvUnwrap.Flatten, an artist or a file"
var settings = new PackSettings { Resolution = 2048, Margin = 4, TexelDensity = 512f };
var placements = UvUnwrap.Pack(islands, settings, out var report);

report.TexelDensity;    // min, mean, max and variance of what was achieved
```

⚠ **`TexelDensity` defaults to zero and zero is not a density — it is the absence of one.** It means
*keep each island at whatever scale it arrived with*, and a flattener's scales differ between charts
by exactly the area distortion of their maps. Measured on this corpus: leaving it alone spreads the
achieved density by **22.9 %** on a hemisphere and **12.9 %** on a saddle, against the 2 % the design
asks for. If you have no number in mind, `UvDensity.Reference(islands)` is one.

```csharp no-compile="a fragment; continues from above"
var uniform = new PackSettings { Resolution = 2048, Margin = 4, TexelDensity = UvDensity.Reference(islands) };
```

## A multiplier, not an absolute

⚠ **A per-chart density is expressed as a ratio because the packer is allowed to rescale everything.**
When the islands do not fit, `PackOverflow.Scale` brings the whole atlas down by one factor and says
so — a chart asked for at twice its neighbours' density is still at twice theirs afterwards, where a
chart pinned to an absolute figure would quietly stop being at it.

```csharp no-compile="a fragment; one chart at twice the rest"
var multipliers = new float[islands.Count];

Array.Fill(multipliers, 1f);
multipliers[hero] = 2f;

var weighted = UvDensity.Weight(islands, multipliers);
var placements = UvUnwrap.Pack(weighted, settings, out var report);
```

`Override` is the same thing said in texels rather than in ratios: give it a material per island, a
density per material and the reference the rest of the atlas is packed at, and it does the division.
A material whose density is zero takes the reference.

⚠ **Parallel arrays and no dictionary, deliberately.** A material-to-density map read through a
`Dictionary` is a hash order, and a hash order that reaches a greedy pass is an atlas that differs
between runtimes.

## Measuring what you got

`UvReport.TexelDensity` is assembled from the factors the packer applied, so in uniform mode it is
exactly uniform *by construction*. That makes it a statement about the arithmetic rather than a
measurement — until something independent agrees with it.

`UvDensity.Measure` is that something. It goes back through `UvPlacement.Apply`, the island's own
parameter area and the world area behind it, and works out how many texels land on a square unit of
surface. `Spread` turns the result into the number the design's exit criterion names: the full range
over the mean, because "within 2 % across every chart" is violated by one chart at 10 % among four
hundred at zero and a variance over that set is small enough to pass.

⚠ **Hand `Measure` the islands the flattener produced and never the ones `Weight` returned.** The
multiplier is an island claiming to be larger in the world than it is, and this measurement divides by
that same scale to recover the world area — given the weighted list it would believe the claim and
report every island at one density.

⚠ **Density is measured before the margin, not after.** A margin is empty space *between* islands: it
costs atlas area, which is what `UvReport.EffectiveEfficiency` is for, and it does not change how many
texels land on a square metre of surface. A density charged for its margin band would make a 12-texel
margin look like a resolution drop.

## Examples

**Pack uniformly and check the criterion.**

```csharp no-compile="a fragment; `islands` came from UvUnwrap.Flatten"
var settings = new PackSettings { Resolution = 2048, Margin = 4, TexelDensity = 512f };
var placements = UvUnwrap.Pack(islands, settings, out var report);

var achieved = UvDensity.Measure(islands, placements, settings.Resolution);

Report(UvDensity.Spread(achieved));    // 0.0000 on this corpus; the criterion asks for 0.02
```

**Give one material four times the resolution of the rest.**

```csharp no-compile="a fragment; `materialOfIsland` came from the charter's group boundaries"
var reference = UvDensity.Reference(islands);
var overridden = UvDensity.Override(islands, materialOfIsland, [0f, 4f * reference], reference);

var placements = UvUnwrap.Pack(overridden, new() { Resolution = 2048, Margin = 4, TexelDensity = reference });
```

**Find out whether the packer rescaled anything**, which is the question the report field exists for.

```csharp no-compile="a fragment; continues from above"
UvUnwrap.Pack(islands, settings, out var report);

foreach (var warning in report.Warnings) {
    Report(warning);    // "…scaled to 0.327× of it" when the density did not fit
}

Report(report.TexelDensity.Mean);    // what was achieved, rather than what was asked for
```

## See also

- [UV packing](uv-packing.md) — the packer these settings are for.
- [UV stacking](uv-stacking.md) — the other way to buy atlas back.
- [docs/plan/42](https://github.com/rikarin/Vixen/blob/master/docs/plan/42-uv-unwrapping.md) — § D9,
  and the exit criterion the numbers above are measured against.
