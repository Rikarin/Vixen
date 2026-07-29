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

## Bricks come in sizes, and every one holds sixty-four probes

A brick of size eight covers five hundred and twelve times the volume of a brick of size one for the
same memory, and its probes are eight times further apart. `Allocate` covers a region cheaply at some
size; `Refine` splits what overlaps another region until it is fine enough. The usual shape is one
coarse call over the world and one refine pass driven by renderer bounds.

**A brick is aligned to its own size**, which is what makes the sampling arithmetic work — dividing a
cell coordinate by the size only gives a position inside the brick if the brick started at a multiple
of it. That is why refinement halves rather than subdividing by arbitrary factors, and why `Assign`
refuses a misaligned brick rather than producing a field that samples slightly wrong everywhere.

**A split discards the parent's probes.** Interpolating them down would give eight children that agree
with each other and with a coarser answer than any of them should hold — and a filler would then be
converging toward the truth from something that already looks converged. Empty is honest: the children
are invalid until something fills them, and dilation treats them as the holes they are.

**There is no field-wide probe lattice, and there cannot be one.** Two bricks of different sizes have
probes at different spacings, so "the probe next door" is a question about world positions rather than
about indices. Everything that walks probes — dilation, a filler — walks bricks and asks the field
where a position lands.

### Border sync has an order, and it is coarsest first

A fine brick borrowing from a coarse neighbour interpolates that neighbour's field, and the position
it wants can fall in the coarse brick's *own* border plane — so the coarse brick has to be finished
first. The reverse never happens: a coarse brick's border lands on the near face of whichever fine
brick covers it, which needs only probes that brick owns.

Within a size the order does not matter, because two bricks of the same size only ever copy each
other's owned probes — and every value in a size is computed before any of it is written, so it
cannot come to matter.

This was found by running the seam test on a refined field, and it is worth naming because the
obvious way to make a pass order-independent — compute everything, then write everything — is
precisely what breaks it.

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

Both failures have tests that assert they *do* leak, so that the day a scene is refined enough to fix
them, the tests say which one it fixed. The fix for both is the same and it is `Refine`: halve the
probe spacing near geometry until the wall is more probes thick. **That is why refinement is a leak
fix before it is a memory optimisation**, and why dilation cannot substitute for it.

### The normal bias

A surface stands exactly where the ambiguity is: its own position is the boundary between the probes
that saw the room and the probes inside the wall it is part of. `NormalBias` pushes the lookup a
quarter of a probe spacing along the normal — Unity's and Epic's number too — onto the side the
surface faces.

It lives on the field rather than on a caller because it is a constant the shader has to match. It
does nothing for a wall thinner than a probe spacing: it moves along the *surface's* normal, and a
floor's normal is not the direction a thin wall is thin in.

## The filler here is the reference, and the shipping one is checked against it

`TracedIrradianceFiller` is doc 19 § L2's filler A written where it can be checked: sixty-four
Fibonacci directions per probe, marched through an `IDistanceField`, cosine-projected into L1. The
shipping version is a compute shader doing N bricks a frame. This is the same arithmetic with nothing
between it and a closed form — the arrangement `DistanceFieldTracer` already has with its Raven port,
and it exists for the same reason: a shader can only be compared against *something*.

It is now compared. `IrradianceFillDeviceTests` dispatches the shader over a whole field, reads the
pool back, and asserts that every probe of every brick is the probe this writes for the same position.
The comparison is against this rather than against the closed form on purpose: a uniform sky pins down
only the constant coefficient, so a transposition or a sign error among the three linear ones would
slip past it. Sixty-four Fibonacci directions do not sum to exactly zero, so the linear coefficients
here are small nonzero numbers a wrong shader has no way of reproducing.

**Which bricks, and in what order, is `IrradianceBrickCursor`'s** — one walk shared by both, because
comparing them means visiting the same bricks, and two implementations of one ordering is the pair that
drifts.

`Dilate` and `SyncBorders` have a device half too, and it is checked the same way: seed a pool from this
tracer against an analytic sphere, dispatch `IrradianceRepair`, and compare all one hundred and
twenty-five texels of every brick. Two things in this file changed because of it. **`Nearest` spells its
tie-break out** as `floor(x + ½)` rather than calling `MathF.Round`, which breaks ties to even where GLSL
does not — and on a refined field every lookup across a size boundary is a tie, because a fine brick's
probes sit exactly halfway between a coarse neighbour's. And the **deferred write list has no device
equivalent**, so the shader writes a repair with its validity negated instead: a value this code already
rejects, since `validity <= 0` was always the test for "do not borrow from this".

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

## The other side of the line

`Raven/Library/IrradianceFields/IrradianceField.rvn` is the shader half of the lookup, and
`Vixen.Rendering.Lighting.IrradianceFieldTexture` is what feeds it. Nothing in *this* assembly knows
either exists — the dependency runs one way, which is what lets the hard half be checked against
arithmetic rather than against a screenshot.

**Four pool volumes for a payload of fourteen numbers, not six.** The constant term takes three
channels and validity rides in its fourth; each *colour* channel's three linear coefficients take a
volume, with the sun's shadow in the red one's fourth. The packing is colour-major on purpose: one
fetch gives all three of red's coefficients, which is what the evaluation wants. Transposing it reads
as lighting whose colour rotates with the surface normal.

**The index volume is point sampled and always half-precision.** Interpolating two slot indices gives
a third that means nothing, so it never filters — which removes the only reason to want the wider
format. It holds a pool origin in texels and a brick size in cells; half represents integers exactly
to 2048, and `Upload` refuses a pool past that rather than storing an origin that rounds.

**Two indirection fetches per shaded pixel, and the first is not waste.** It learns how big the brick
under the surface is, because the normal bias is measured in *that brick's* probe spacing. Both are
point samples of a small volume.

`SamplingConventionTests` walks the shader's addressing in C# — voxel, cell, entry, origin, local,
texture coordinate, trilinear — and asserts it lands on the same texels the field's own sampler reads,
on a refined field as well as a uniform one. Refined is where it gets interesting: the divide by the
brick size and the floor of the cell by it are the two steps a uniform field would never exercise.

### And it has drawn

`IndirectDiffuseImageTests` runs the whole thing on a device: an empty world under a uniform sky of
radiance *L* comes back as a flat frame of *L*. Every step between a probe and a pixel is in that
path — the fill, the dilation, the border sync, the pack into four volumes, the copy, the index fetch,
the trilinear read, and the basis evaluation.

*L* is deliberately neither a half nor a one, because the g-buffer is cleared to halves and the alpha
the shader writes is a one — a radiance equal to either would pass for a picture that had merely
copied something through, which is the shape of most of the ways a path like this goes wrong.

**And a picture is not enough for the compute filler**, which is why `IrradianceFieldTexture` grew a
readback. Once the pool is a storage image the dispatch owns, the field in *this* assembly is no longer
what anything reads, so every closed form above has nothing left to test. Worse, the two ways a
dispatch fails — writing nothing, and writing to the wrong texels — draw the same unlit frame.
`RecordReadback` and `TryRead` copy the pool back and decode it, which is what tells those two apart
and what lets the device filler be checked probe by probe rather than pixel by pixel.

## The pool has a fixed capacity, and that is a decision

A pool that grows reallocates the texture it is a mirror of, mid-frame, at the exact moment a scene
got complicated. Doc 19 § 7 lists sparse residency as *optional* precisely because a fixed pool
works: running out means the furthest bricks are not resident, which is a quality reduction and not a
failure. `TryAllocate` returns false rather than throwing, for the same reason.

Slots are cleared **on release** rather than on allocation, so a slot never holds a previous brick's
lighting while it waits. Handing out a dirty slot shows as one frame of somewhere else's colour and
gets blamed on the temporal filter.

## Not yet, and named so the absence is a decision

- **Coarsening.** `IrradianceRefinementPolicy` decides where a field should be fine and only ever adds
  detail. Nothing merges bricks back when geometry moves away, so a streamed scene ratchets toward its
  finest everywhere the geometry has ever been — which needs the pool to take slots back and a policy
  for when, and neither exists.
- **Filler B at all.** The offline cube capture, for the targets without compute.
- **A repair narrowed to what changed.** `IrradianceFieldRepair` dilates and syncs every brick every
  frame, which is what this does too and is not an oversight in either — a brick the budget did not
  refill still has neighbours that were, and a border is a copy of a probe that may have just changed.
  Restricting it to the dirty bricks and their neighbours is real work nobody has done.
- **View bias.** Dilation and the normal bias are here; the offset along the view ray, which is what
  helps at grazing angles, is not.
- **`Deferred`.** `ForwardPlus` composes the field into its ambient term and `Deferred` has the same
  term and has not been given the slot.

**Nothing here creates or calls a graphics device**, which is what lets the sampling be checked
against arithmetic instead of against a picture. The assembly does reference `Vixen.Core.Imaging` for
the spherical-harmonic payload, and that in turn references the RHI — so this is a weaker line than
`Vixen.Rendering.DistanceFields` draws, and worth stating precisely rather than claiming the stronger
one.
