# Vixen.Rendering.ScreenProbes

The screen probe gather of [docs/plan/19](../../docs/plan/19-lighting-and-global-illumination.md)
§ L3 — its geometry and arithmetic, written first and device-free. One probe per tile of screen, an
octahedral radiance map per probe, an atlas that holds them, and the resolve back to per-pixel
irradiance. The shipping gather is a set of compute passes; this is what those passes will be
compared against, texel by texel, the same arrangement `TracedIrradianceFiller` has with
`IrradianceFill` one subsystem over.

## The octahedral map is a contract, not a convenience

`OctahedralMap` folds the sphere into a square exactly the way the Raven library's
`Math.EncodeOctahedral` does — the tie at zero picks the positive hemisphere, the corners are the
south pole. A probe's map is written by one side and read by the other, so the direction a texel
stands for has to be one function evaluated twice; the convention tests pin it with hand-written
values rather than roundtrips, because a roundtrip passes with the axes swapped.

## Texel solid angles are exact, and that is the load-bearing part

A projection into spherical harmonics is an integral, so each texel carries the area of sphere it
stands for. The octahedral map is not equal-area, and a Jacobian-at-the-centre approximation is
wrong by whole percents at 8×8 — every one of them a probe that resolves that much dark, uniformly,
which is the kind of error that reads as a tuning problem forever.

Within one octant the decode is affine in the plane, so a straight texel edge maps to a great-circle
arc. The exact area is therefore: clip the texel against the eight octants, map each piece's
corners, sum the spherical triangle fans. The test that keeps it honest asserts the texels of every
resolution sum to 4π — at double precision, not within a shrug.

## Probes stand on pixels, and a probe with no surface says so

`ScreenProbeLayout` puts one probe per 16-pixel tile (a parameter, not a law), anchored at the
tile's centre and clamped into the viewport at the partial tiles. Every pixel reads the four probes
around it bilinearly; the lattice clamps at the border rather than extrapolating, and the weights
always sum to one.

A probe whose anchor shows the sky is **invalid, not black** — black would pull every neighbouring
pixel toward darkness through the filter, which is the screen-space version of the buried-probe leak
the irradiance field's validity exists for. Invalid probes drop out of the resolve and the weights
renormalise over what is left. A probe standing inside geometry is invalid by the field's sign,
before any ray is cast — the same rule as the field filler's.

The resolved probe stores what `SphericalHarmonicsL1.Irradiance` evaluates: irradiance over π, the
number a shading pass multiplies by albedo, clamped at zero on the way out — both conventions shared
with the irradiance field, and for the field's reasons.

## The reference gather is deliberately the naive one

`TracedScreenProbeGather` casts one deterministic ray per octahedral texel, at the texel's centre,
no jitter — so two gathers of one scene agree to the bit and a test asserts numbers rather than
tolerances around noise. Its closed forms are the family every layer of § L2 was held against: a
uniform sky comes back as itself through the whole chain, a linear sky resolves to
`a + ⅔·b·(n·ŷ)` within the quadrature's stated two per cent, and a probe on a lit floor still
answers the whole sky for the upward normal, because the L1 truncation of a hemisphere is exact at
the pole.

One of its tests documents the truncation's other face: beside an occluder, the away-facing answer
overshoots the sky. That is the positive mirror of the "an L1 field can return negative light"
finding in doc 19 § L2, and the test asserts a bound on it rather than pretending L1 is something
it is not.

## Not yet, and named so the absence is a decision

- **Adaptive probes.** Doc 19's "adaptive placement at disocclusions" — extra probes where the
  coarse grid straddles a depth edge. An addition to the lattice, not a replacement, and the lattice
  had to be right first.
- **Importance sampling.** The shipping gather aims rays where the BRDF and last frame's lighting
  say they matter. It changes which texel a ray serves, not what a texel means, so it belongs to the
  version that has a BRDF to sample against.
- **The trace order.** Screen traces against the HZB first, then the mesh and global SDFs, then
  termination in the irradiance field for distant light. The reference marches one `IDistanceField`
  because a closed form needs one thing to be true at a time.
- **The denoiser.** Spatial filtering, temporal reprojection through the motion vectors, and the
  bilateral upsample against depth and normal edges. Doc 19 § L3's own warning is that this is the
  project — un-denoised, the gather looks worse than § L2 alone — and none of it is here, because
  all of it is device-side work over real frames.
- **Everything device-side.** No shader, no atlas texture, no renderer. The next milestone is the
  same one L2's was at this point: the storage convention pinned by a test that walks the shader's
  addressing in C#.

**Nothing here creates or calls a graphics device.** The assembly references
`Vixen.Rendering.DistanceFields` for the reference's marching and `Vixen.Rendering.IrradianceFields`
for `IRadianceSource` — the same question a field probe asks, asked by a screen probe — so the same
qualified line the irradiance field's README draws applies here too.
