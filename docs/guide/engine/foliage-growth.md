---
title: Growing a forest
slug: engine/foliage-growth
kind: guide
area: Engine
summary: The offline ecology — seeds sown, aged, spread, shaded out and displaced — regenerated wholesale from four sliders and a stated seed.
api: [T:Vixen.Foliage.FoliageGrowth, T:Vixen.Foliage.FoliageEcology, T:Vixen.Foliage.FoliageBlocker, T:Vixen.Foliage.FoliageGrowthSettings, T:Vixen.Foliage.FoliageGrowthResult, T:Vixen.Editor.Terrain.TerrainGrowthSettings]
tags: [foliage, vegetation, procedural, simulation, ecology]
since: 0.1
status: preview
related: [engine/foliage, engine/grass, editor/foliage-mode, rendering/foliage-rendering]
---

## What it is

`FoliageGrowth.Simulate` sows a region, ages it for a fixed number of steps, and lets what survives
stand. `FoliageEcology` is what each species brings to that: how densely it starts, how far it
spreads, how far its canopy shades, how much shade it survives, and who wins when two of them want
the same ground. `FoliageBlocker` is a box nothing grows inside.

## What it is for

A forest that reads as a forest, from four sliders — clumped where a parent stood, thinned under
shade, cleared where a volume says. It is Unreal's procedural foliage tool, and the reason to have it
is the one every project discovers late: hand-placing ten thousand trees is a week, and the result
still looks hand-placed.

You do not want it for a hedge, an avenue, or anything an author has a shape in mind for. Those are
[strokes](../editor/foliage-mode.md).

## Using it

```csharp no-compile="a fragment; the surface is whatever the host can probe"
var pine = FoliageType.Of("Pine") with {
    Radius = 2f,
    Ecology = FoliageEcology.Tree with { SeedDensity = 0.004f, SpreadDistance = 12f }
};

var settings = FoliageGrowthSettings.Over(new(0f, 0f), new(500f, 500f)) with { Steps = 8 };
var result = FoliageGrowth.Simulate(grown, surface, settings, blockers);
```

⚠ **The output is a volume of its own, and that is [§ D4](terrain-heightfield.md)'s reserved layer in
this kernel's vocabulary.** A simulation is re-runnable, which means it regenerates its instances
*wholesale* — so it cannot share a container with the ones an artist placed by hand, or re-rolling
the seed would delete an afternoon's work. The destination is cleared and refilled; the scene's own
volume is never touched.

⚠ **The seed is a setting, because it is the one number an author re-rolls.** "Grow me a different
forest with the same rules" is a different operation from changing the rules, and hiding the seed
inside the simulation makes the first one impossible.

⚠ **A fixed step count, not a convergence test.** A simulation that ran until it settled would take a
different number of steps on a different seed, so "the same rules, a different forest" would produce
two forests of different maturity — and it would make the cost unpredictable in the one place an
author is waiting for it.

## Determinism

⚠ **A plant's identity is hashed at birth**, from its parent's identity and the seed index, so it
does not move when the plants around it change.

⚠ **Each step's candidates are resolved in hash order, not in the order they were generated.** Which
of two overlapping seeds wins must not depend on which parent was walked first — otherwise the same
seed grows a different forest whenever anything upstream reorders the working set, which is a bug
report nobody can act on.

## The mechanisms

**Spread** is what makes a forest read as one: a seed lands within `SpreadDistance` of the plant that
dropped it, at a square-rooted distance so it spreads evenly over the disc rather than packing at the
trunk.

**Shade** is cast by the canopy, and ⚠ **the canopy grows with the plant**. A seed under a sapling
survives and the same seed under the grown tree does not. Shading from the mature radius instead
produces one tree per shade radius, evenly spaced, everywhere — the pattern that makes a procedural
forest read as procedural.

**Priority displaces rather than ties.** A higher-priority seed landing on a lower-priority plant
*removes* it, which is how an oak comes to stand in a clearing of the scrub that got there first.
Equal priorities fall back to age — the established plant keeps its ground — and equal ages to the
seed's identity.

⚠ **Shade and spread pull in opposite directions, and at forest tolerances shade wins.** A forest
under competition is *more* evenly spaced than chance, not less: shade suppresses exactly the near
neighbours that clumping produces. A test that measures "clumped" without saying which mechanism it
is looking at will fail on correct output.

⚠ **Age scales the instance rather than choosing a mesh.** A sapling is the tree at a third of its
size — botanically wrong, right for a tool whose output an artist then edits by hand, and a second
mesh per species would double the palette for a distinction the simulation cannot make well anyway.

## Blocking volumes

⚠ **A blocker refuses rather than deletes.** A seed inside is turned away, so re-running with the
volume moved regrows what it used to cover. A blocker that removed would make its own removal
irreversible, which is the opposite of what a re-runnable simulation is for.

⚠ **A box, not a shape, because this assembly has no physics world.** A caller with one converts; a
caller without still gets the feature — `IFoliageSurface`'s reason.

## Reading the result

```csharp no-compile="a fragment; these are what a panel reports"
// result.Sown / result.Sprouted — seeds scattered, then dropped
// result.Placed                 — plants standing at the end
// result.NoSurface / Blocked / Crowded / Shaded / Capped
// result.Displaced              — plants a higher-priority seed removed
```

⚠ **A refusal per reason**, for [`FoliageScatter.Consider`](foliage.md)'s reason. "The simulation
grew nothing" is the report; "eleven thousand seeds, nine thousand of them shaded out" is a shade
tolerance somebody changes.

⚠ **The cap is announced rather than silent.** Spread is exponential until shade catches up with it,
so a region an author made ten times too large is ten thousand times the plants — and a simulation
that quietly stopped growing reads as a rule that stopped working.

## The panel

`TerrainGrowthSettings` is what the Growth panel edits, and `ToSettings` is where it meets the
kernel — a mutable object beside an immutable one, for `TerrainBrushSettings`' reason: a simulation
has to be the same settings from its first step to its last.

⚠ **The seed is a field and not a hidden number, and that is the whole feature.** "The same rules, a
different forest" is what a procedural forest is for; a generator that reseeded itself every run
would make an author who liked what they saw unable to get it back, and one that never reseeded would
make every hillside the same hillside.

⚠ **The region is centred in the panel and cornered in the kernel.** `CentreOn` is what "grow around
the cursor" is; converting between a corner and a centre in the panel rather than in the kernel is
the same division of labour the brush settings make.

⚠ **Replacing the layer is on, and the alternative is not a feature.** A generated layer that
accumulated would double its forest every time the button was pressed, which is the one behaviour an
author reads as the simulation being broken rather than as a setting.

⚠ **The plant cap is reported when it bites.** Spread is exponential until shade catches up with it,
so a region an author made ten times too large is ten thousand times the plants — and a simulation
that quietly stopped sowing reads as a rule that stopped working. The panel shows `Capped` beside the
counts.

## Examples

Two species competing, where the taller one wins the ground:

```csharp no-compile="a fragment"
var scrub = FoliageType.Of("Scrub") with {
    Radius = 3f,
    Ecology = FoliageEcology.Tree with { SeedDensity = 0.01f, Priority = 1, ShadeTolerance = 1f }
};

var oak = FoliageType.Of("Oak") with {
    Radius = 3f,
    Ecology = FoliageEcology.Tree with { SeedDensity = 0.004f, Priority = 20 }
};
```

Keeping a clearing:

```csharp no-compile="a fragment"
FoliageGrowth.Simulate(grown, surface, settings, [FoliageBlocker.Around(new(100f, 100f), 30f)]);
```

A type that takes no part at all, which is every palette entry by default:

```csharp no-compile="a fragment"
var hedge = FoliageType.Of("Hedge") with { Ecology = FoliageEcology.None };
```

⚠ **A zeroed ecology never sows, and that is the right way round.** A zero that meant "the default
density" would make every palette entry appear in a simulation somebody ran to grow one species.

## See also

- [Foliage instances](foliage.md) — the volume this fills and the rules it obeys.
- [Foliage mode](../editor/foliage-mode.md) — the hand-placed half, in its own volume.
- [Grass](grass.md) — the other derived path, and why it is a rule rather than a simulation.
- [docs/plan/31 § T9](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  the phase this is, and its exit criterion.
