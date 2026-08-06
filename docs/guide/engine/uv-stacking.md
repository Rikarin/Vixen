---
title: UV stacking
slug: engine/uv-stacking
kind: guide
area: Engine
summary: Two symmetric islands sharing one region of texture — detected and offered, never applied on your behalf.
api: [T:Vixen.Geometry.Uv.UvStacking, T:Vixen.Geometry.Uv.UvStackOffer, T:Vixen.Geometry.Uv.UvPlacement, T:Vixen.Geometry.Uv.UvIsland]
tags: [geometry, uv, atlas, packing, texture, symmetry, mirroring]
since: 0.1
status: preview
related: [engine/uv-packing, engine/uv-texel-density, engine/retopology, engine/quad-remeshing]
---

## What it is

`UvStacking` finds pairs of islands that one region of texture could serve, and hands you a list.
`Detect` proposes, `Fold` drops the partners so the packer only sees one of each, and `Unfold` gives
every original island a placement again — the partner's identical to its representative's.

## What it is for

A symmetric asset spends half its atlas twice. A character's two arms, a vehicle's two doors, a
building's two identical windows: stacking them halves what they cost, at 2× the resolution for the
same sheet.

⚠ **It is off by default and nothing in this library calls it.** Stacking forbids asymmetric detail —
a scar on one cheek, a logo on one sleeve, wear on one boot — and discovering that after texturing is
a retexture rather than a repack. So the detector produces a list with a number beside each entry, and
the decision is somebody else's.

## Using it

```csharp no-compile="a fragment; `islands` came from UvUnwrap.Flatten"
var offers = UvStacking.Detect(islands);                            // nothing has happened yet
var accepted = offers.Where(offer => offer.Residual < 1e-4f).ToArray();

var folded = UvStacking.Fold(islands, accepted, out var source);
var placements = UvStacking.Unfold(UvUnwrap.Pack(folded, settings), source);
```

`source[i]` says which folded island carries original island `i`; a representative and its partner
share an entry, which is what makes them share a region.

## What "the same shape" means here

`Detect` compares two islands corner for corner after putting each in its own lower corner — the same
normalization `UvPlacement.Apply` makes — under the identity and under a reflection in `u`. The worst
corner's disagreement, as a fraction of the island's extent, is the offer's `Residual`.

⚠ **Comparing raw coordinates would measure where the flattener left the gauge.** A conformal map is
unique only up to a similarity, so two islands can be the same shape in different corners of the plane
and read as entirely different.

⚠ **The corner-for-corner comparison is the honest limitation, and it is what makes symmetric
retopology worth having.** A mesh remeshed with symmetry on has vertex *k* and its mirror as exact
negations, so the two charts come out with corresponding corners at corresponding indices and the
match is an *equality* rather than a search. On a mesh that was not built that way the correspondence
is unknown, and this detector reports nothing rather than reporting a guess.

⚠ **Detection is order-stable.** Every island is visited in index order and paired with the
lowest-index island still free. An offer that moved between runtimes would be worse than no offer at
all, because a human accepts it once and the acceptance is recorded against island indices.

## What it costs

The partner receives the representative's offset, scale, rotation and tile unchanged; only
`UvPlacement.Island` differs. That is the whole of stacking — two islands with one transform is two
islands on one region of texture.

⚠ **So a bake has to write one of them and not both.** Baking both writes the same texels twice, and
whichever goes second wins. This is the cost paid at texturing time that the opt-in exists for.

An island may be stacked onto one representative or none. A chain — `a` onto `b` onto `c` — is refused
by name, because three halves of a mirror is not a thing.

## Examples

**Offer, decide, pack.**

```csharp no-compile="a fragment; `islands` came from UvUnwrap.Flatten"
var settings = new PackSettings { Resolution = 2048, Margin = 4, TexelDensity = 512f };
var offers = UvStacking.Detect(islands);

foreach (var offer in offers) {
    Report(offer.Representative, offer.Partner, offer.Mirrored, offer.Residual);
}

var folded = UvStacking.Fold(islands, offers, out var source);
var placements = UvStacking.Unfold(UvUnwrap.Pack(folded, settings), source);
```

**Take only the exact mirrors**, which is what a symmetry-preserving remesh produces.

```csharp no-compile="a fragment; continues from above"
var exact = UvStacking.Detect(islands, tolerance: 0f).Where(offer => offer.Mirrored).ToArray();
var folded = UvStacking.Fold(islands, exact, out var source);
```

**Measure what it bought**, which needs a pinned density or the packer's scale search answers instead.

```csharp no-compile="a fragment; continues from above"
UvUnwrap.Pack(islands, settings, out var flat);
UvUnwrap.Pack(folded, settings, out var stacked);

Report(stacked.PackingEfficiency / flat.PackingEfficiency);   // 0.5 when every island paired
```

## See also

- [UV packing](uv-packing.md) — where the folded islands go.
- [UV texel density](uv-texel-density.md) — the other half of what an atlas costs.
- [docs/plan/42](https://github.com/rikarin/Vixen/blob/master/docs/plan/42-uv-unwrapping.md) — § D10,
  and why the default is off.
