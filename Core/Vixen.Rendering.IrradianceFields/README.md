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

## Leaks: what is fixed here, and what is not

A leak is light where a wall should have stopped it, and it is the defect users actually report —
[doc 19](../../docs/plan/19-lighting-and-global-illumination.md)'s risk G3. Two things here work on
it, and neither is a general solution.

### Dilation, which is really about the opposite problem

A probe inside a wall holds nothing, and **nothing is a colour**. Trilinear interpolation does not
know it should skip that probe, so every surface within a probe spacing of a wall reads part of a
hole and comes out dark — a rind one probe thick around everything, which is what people describe as
"the GI looks dirty". `Dilate` fills invalid probes from their valid face neighbours and removes it.

It is a fill rather than a weighting at sample time for a stated reason: doc 19 § 3 commits to **one**
trilinear fetch, and a validity-weighted skip needs eight taps and cannot be one.

**The pass count is not the leak knob**, though everyone assumes it is. A repair never overwrites a
valid probe, so each face of a wall fills inward from its own side and the two meet without mixing —
once the face touching a room has taken the room's light, no number of further passes carries the
outside's past it. `AClosedBoxStaysDark` runs at one, two and eight passes to say so.

### The knob is how thick a wall is in probes

| Wall | What happens |
|---|---|
| Three probes thick | Works. No interior stencil reaches an exterior probe; each face repairs from its own side |
| **Exactly one probe thick** | Leaks, at full strength, in one pass. A single invalid plane touches the room on one side and the outside on the other, and its repair is the average of both |
| **Thinner than the probe spacing** | Worse: no probe is inside it, so every probe is valid, dilation has nothing to repair, and a stencil spans straight through |

Both failures have tests that assert they *do* leak, so that the day refinement fixes them, the tests
say which one it fixed. The fix for both is the same — finer bricks near geometry, so the same wall
is more probes thick. That is the refinement doc 19 § 3 asks for and this does not have yet.

### The normal bias

A surface stands exactly where the ambiguity is: its own position is the boundary between the probes
that saw the room and the probes inside the wall it is part of. `NormalBias` pushes the lookup a
quarter of a probe spacing along the normal — Unity's and Epic's number too — onto the side the
surface faces.

It lives on the field rather than on a caller because it is a constant the shader has to match. It
does nothing for a wall thinner than a probe spacing: it moves along the *surface's* normal, and a
floor's normal is not the direction a thin wall is thin in.

## One filler exists, and it is the reference rather than the shipping one

`TracedIrradianceFiller` is doc 19 § L2's filler A written where it can be checked: sixty-four
Fibonacci directions per probe, marched through an `IDistanceField`, cosine-projected into L1. The
shipping version is a compute shader doing N bricks a frame. This is the same arithmetic with nothing
between it and a closed form — the arrangement `DistanceFieldTracer` already has with its Raven port,
and it exists for the same reason: a shader can only be compared against *something*.

**A distance field says where geometry is and nothing about its colour**, so `IRadianceSource` is who
the filler asks. Separating it out keeps the tracing honest — the filler owns which directions matter
and how much of the sphere each stands for, and nothing else. On a GPU that answer eventually comes
from Lumen's surface cache (§ L4); until then it comes from whatever the caller can work out, which
for a bootstrap is the previous iteration's own irradiance times an albedo.

**Validity is the field's sign, with the backface vote behind it.** Doc 19 names the vote as *the*
mechanism, and against an exact field it cannot fire at all: sphere tracing stops where the field
crosses zero on the way down, and the gradient there always opposes the ray. The vote earns its place
against a *sampled* field, where an over-reported step lands past a thin wall and the surface it then
finds is seen from behind — a case the probe's own position says nothing about. Both are implemented;
which one answers depends on how good the field is, and that is worth knowing rather than assuming.

**Hysteresis defaults to zero because zero is what can be tested.** A single fill of a uniform
environment of radiance *L* then gives a probe that lights everything with exactly *L* — the same
closed form the projection itself is checked against, now reached through traced rays. A filler
running per frame wants something near nine-tenths, which averages away the noise in a sixty-four-ray
estimate at the cost of lagging a light that moved.

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
- **Filler A on a GPU**, and filler B at all. The CPU tracer above is filler A's reference; the
  compute shader that does N bricks a frame, and the offline cube capture for targets without
  compute, are both still owed.
- **View bias.** Dilation and the normal bias are here; the offset along the view ray, which is what
  helps at grazing angles, is not.
- **The GPU mirror.** One volume texture per coefficient plus the index texture, staged and copied,
  the way `GlobalDistanceFieldTexture` does it for the clipmap. `TextureCoordinate` is the convention
  it will have to agree with, and it is tested from this side already.

**Nothing here creates or calls a graphics device**, which is what lets the sampling be checked
against arithmetic instead of against a picture. The assembly does reference `Vixen.Core.Imaging` for
the spherical-harmonic payload, and that in turn references the RHI — so this is a weaker line than
`Vixen.Rendering.DistanceFields` draws, and worth stating precisely rather than claiming the stronger
one.
