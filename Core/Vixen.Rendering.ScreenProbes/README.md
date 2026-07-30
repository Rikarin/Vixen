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

## The device half, and where it lives

The shader side is `Raven/Library/ScreenProbes`: `ScreenProbeAtlas.rvn` for the addressing —
folding through `Math.DecodeOctahedral`, because one fold in the library means the G-buffer normals
and the probe maps cannot disagree about which corner is the south pole — and `ScreenProbeTrace.rvn`
for the kernel, one workgroup per probe and one invocation per texel, composing the same
`distanceField` slot `IrradianceFill` does. `AtlasConventionTests` here walks the shader's
arithmetic in C# and holds it against `OctahedralMap`, texel by texel, with text guards on the lines
that would drift silently.

The host side is `Vixen.Rendering`: `ScreenProbeTexture` mirrors the atlas into one 2D texture —
radiance in the colour, validity in the alpha, so a readback can tell "nothing gathered" from
"gathered nothing but darkness" — and `ScreenProbeTraceFill` stages one job per valid probe and
dispatches. `ScreenProbeTraceDeviceTests` compares the dispatch against `TracedScreenProbeGather`
texel by texel, under a *linear* sky as well as a uniform one, because under a uniform sky every
texel is the same number and a mirrored decode is invisible.

The surface bias is applied while staging, so the shader receives an origin rather than a surface
and a rule — the same single place the reference applies it, which is what keeps the two comparable.
⚠ A probe with no surface is not dispatched, and on a device-owned atlas its patch is therefore
*undefined*; consumers read validity from alpha, and clearing or skipping unwritten patches belongs
to the consuming pass.

## Long rays terminate in the irradiance field

Doc 19 § L3's trace order ends by amortising distant lighting in § L2's field, and both tracers do
it: a ray that runs out of budget samples the field at its end point and blends toward the field's
answer by the probe's validity — the sky is the fallback, not an addend, for the double-counting
reason `ForwardPlus` records. The Raven `IIrradianceSource` protocol grew a second member for it,
`Radiance(world, direction)`: the raw basis with no cosine lobe, because a termination point is not
a surface — nothing stands there to bias away from, and the ray wants what the light *is*, not what
a wall would receive from it. `SphericalHarmonicsL1.Radiance` is the C# half, and a linear sky
survives the round trip exactly, because a function of the first band's shape is what an L1
projection keeps whole. The kernel composes the same `irradiance` slot the shading passes compose —
`NoIrradiance`'s zero coverage blends every termination back to the sky, so a project without a
field traces exactly the rays it traced before.

## The resolve is a dispatch, and its weights are the same table

`ScreenProbeResolve.rvn` projects each probe's map into L1 — one workgroup per probe, walking the
map in the exact order `ScreenProbeAtlas.Resolve` walks it, because a parallel reduction reorders a
float sum and the first version of anything here is the one with nothing between it and the
reference (making it wide is owed, with a baseline to hold it to). The solid angles arrive in a
buffer filled from `OctahedralMap.SolidAngles` — the same exact table, not a second derivation. The
output is four grid-sized planes in the irradiance pool's own colour-major packing, validity in the
constant plane's alpha, so whatever upsamples these probes interpolates coefficients exactly as the
field's sampler does.

## Not yet, and named so the absence is a decision

- **Adaptive probes.** Doc 19's "adaptive placement at disocclusions" — extra probes where the
  coarse grid straddles a depth edge. An addition to the lattice, not a replacement, and the lattice
  had to be right first.
- **Importance sampling.** The shipping gather aims rays where the BRDF and last frame's lighting
  say they matter. It changes which texel a ray serves, not what a texel means, so it belongs to the
  version that has a BRDF to sample against.
- **Screen traces.** The trace order's first stage — rays against the HZB before any distance field
  — needs a frame to trace, and belongs to the renderer integration. The other end of the order,
  termination in the irradiance field, is in.
- **The denoiser.** Spatial filtering, temporal reprojection through the motion vectors, and the
  bilateral upsample against depth and normal edges. Doc 19 § L3's own warning is that this is the
  project — un-denoised, the gather looks worse than § L2 alone.
- **The frame.** The upsample as a pass, probe placement reconstructed from the real depth buffer,
  and the compositor node that owns the lot. The resolve's four planes are shaped for exactly that
  consumer, and it does not exist yet.

**Nothing here creates or calls a graphics device.** The assembly references
`Vixen.Rendering.DistanceFields` for the reference's marching and `Vixen.Rendering.IrradianceFields`
for `IRadianceSource` — the same question a field probe asks, asked by a screen probe — so the same
qualified line the irradiance field's README draws applies here too.
