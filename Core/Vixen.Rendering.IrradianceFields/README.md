# Vixen.Rendering.IrradianceFields

Where the light already is, stored so that reading it is two fetches.

This is step two of [docs/plan/19](../../docs/plan/19-lighting-and-global-illumination.md) — the
storage and sampling half of dynamic global illumination. The distance fields next door are what a
filler *traces*; this is where the answer goes.

## The defining property: it does not know what filled it

A brick is a brick whether a compute shader traced rays into it this frame or an offline cube
capture wrote it at build time. That is not a nicety — it is what lets doc 19 § 7 promise a phone and
a desktop the same lighting model at different update rates. Nothing above this line branches on
which filler ran, so there is one shader and one set of artefacts to reason about rather than two.

## Four probes cubed, in a footprint of five cubed

Sixty-four probes belong to a brick; a hundred and twenty-five texels hold them. The extra plane on
each of the three positive faces holds the **neighbouring** brick's first probe — the same world
position, the same value, stored twice.

That duplication is the entire reason one hardware trilinear fetch can cross a brick boundary without
knowing there was one. Doc 19 § 3 calls it "the one everybody rediscovers the hard way", and it is
Epic's volumetric-lightmap detail rather than an invention here.

**Probes sit on the lattice, not in the middle of cells.** Brick *c*'s probe 4 and brick *c+1*'s
probe 0 are the *same world position*, which is what makes a border a copy rather than an estimate.
Any other convention leaves the border an approximation of the neighbour and the seam comes back at a
smaller amplitude — which is worse than a visible one, because it looks like something else.

`IrradianceFieldTests` says so by filling a field from a linear function of world position and
asserting the sample is exact everywhere, boundaries included. Trilinear interpolation reproduces a
linear function exactly, so any error left is a probe read from the wrong place. The companion test
runs the same field *without* syncing its borders and asserts the answer is badly wrong — because a
layout detail that looks like padding needs a test that fails when you remove it.

### Two cell conventions, and they are both right

`MeshDistanceField` puts its samples **on** the grid points, so its cell size divides by
`Resolution − 1`. `IrradianceIndirection`'s cells are **boxes**, so its cell size divides by
`Resolution`. Mixing them up is half a cell of error everywhere, and the two live one directory
apart — so: an indirection cell is a volume, and the probe lattice *inside* it is the one with the
grid-point convention.

## Divide, floor, fetch

The whole lookup. A world position becomes a cell, the cell becomes a pool slot, the fractional part
becomes a coordinate inside the brick. Integer arithmetic and two fetches — on a GPU, a point-sampled
index texture followed by a linearly filtered pool fetch.

**This is the shape doc 06's tetrahedral light probes failed at, chosen because it cannot fail the
same way.** A Delaunay tetrahedralisation needs robust predicates, degenerates on co-planar probes,
and answers "which cell am I in" with a walk. A grid has none of those ways to be wrong. Doc 19 § 3
is explicit: no Delaunay, no predicates, no repeat.

## The payload is six numbers per channel and two scalars

L1 spherical harmonics — four coefficients per channel — plus a validity scalar and a directional
shadowing scalar.

**L1 and not L2** because a Lambertian surface cannot see detail sharper than the cosine lobe that
blurs it; the second band costs more than twice the whole payload for a little directional contrast.
Both Unity's adaptive probe volumes and Epic's volumetric lightmap ship L1 as their default.

**Validity is carried, not derived**, because only the filler can know it. A probe that traced its
rays and found itself surrounded by backfaces is inside geometry, and nothing at the sampling end can
tell that from a probe in a dark room. Doc 19 carries leaks as risk G3 — the defect users actually
report — and every part of the remedy starts from this number.

**An unfilled probe is invalid, not valid-and-black.** It reads as "no answer here" so dilation can
replace it. The other default spreads darkness through a scene and looks exactly like a correct field
that happens to be dark.

## Borders are maintained, not written

A filler writes the sixty-four probes a brick owns; `SyncBorders` copies each neighbour's first probe
into the plane standing in for it. `SetProbe` refuses to address a border at all, because a border is
not data — it is a second copy of somebody else's data, and a filler that wrote one would produce a
seam exactly where two bricks disagree, which is exactly where a seam is visible.

At the edge of the field, and beside a cell with no brick, a border repeats the brick's own last
probe. There is nothing beyond to copy, and a constant extrapolation means the lighting stops
changing rather than falling to black — which is what the alternative looks like: a dark rind one
probe thick around everything.

## The pool has a fixed capacity, and that is a decision

A pool that grows reallocates the texture it is a mirror of, mid-frame, at the exact moment a scene
got complicated. Doc 19 § 7 lists sparse residency as *optional* precisely because a fixed pool
works: running out means the furthest bricks are not resident, which is a quality reduction and not a
failure. `TryAllocate` returns false rather than throwing, for the same reason.

Slots are cleared **on release** rather than on allocation, so a slot never holds a previous brick's
lighting while it waits. Handing out a dirty slot shows as one frame of somewhere else's colour and
gets blamed on the temporal filter.

## Not yet, and named so the absence is a decision

- **Refinement.** Every cell is one brick at one size. Doc 19 § 3 wants bricks subdividing near
  geometry from renderer bounds, up to three levels; when it lands it lands as a brick size stored
  beside the slot, the way Epic's does, and the sampling formula gains a divide.
- **The fillers.** Neither the runtime ray tracer (filler A, where `HasCompute`) nor the offline cube
  capture (filler B) exists. This is the half they both write into.
- **The rest of the leak mitigation.** Validity is carried but nothing dilates into invalid probes,
  and there is no normal or view bias. All three are doc 19's risk G3, and all three land in this
  phase rather than as polish.
- **The GPU mirror.** One volume texture per coefficient plus the index texture, staged and copied,
  the way `GlobalDistanceFieldTexture` does it for the clipmap. `TextureCoordinate` is the convention
  it will have to agree with, and it is tested from this side already.

**Nothing here creates or calls a graphics device**, which is what lets the sampling be checked
against arithmetic instead of against a picture. The assembly does reference `Vixen.Core.Imaging` for
the spherical-harmonic payload, and that in turn references the RHI — so this is a weaker line than
`Vixen.Rendering.DistanceFields` draws, and worth stating precisely rather than claiming the stronger
one.
