# Vixen.Rendering.SurfaceCache

The surface cache and radiosity of [docs/plan/19](../../docs/plan/19-lighting-and-global-illumination.md)
§ L4 — its geometry and arithmetic, written first and device-free. Cards fitted to a mesh's six
sides, an atlas that holds their captures, direct lighting evaluated on the cache, and the bounce
over the cards that turns one bounce into the infinite-bounce look. Every tracer below this returned
*nothing* from a hit since the day it was written; this is the package that gives a hit an answer.

## A card is a box and an axis

`SurfaceCard` captures the surfaces inside its box whose normals lean along its direction, looking
straight down that direction orthographically. Its in-plane frame is the other two world axes in
cyclic order whatever the sign — one rule with no branches, so a card and the shader that will
someday sample it cannot disagree about which way U runs. `CardGenerator` fits up to six of them to
a triangle mesh: triangles vote by dominant normal axis, ties at exactly forty-five degrees go to
the smaller index (a tie broken by float noise is a card that flickers between shapes across a
rebake), and a perfectly flat face gets a millimetre of depth because a card is a box.

## The atlas is a budget

`SurfaceCacheAtlas` is doc 19 § 6's "texture atlas allocation and residency", CPU half: shelf
allocation with exact-size reuse of released rectangles — cards recur at the sizes their meshes
generate, so the exact match is the common case and a general packer is an optimisation with this
as its baseline. Running out is a refusal, not an error: an uncached card's surfaces answer black
through the tracers, a quality reduction in exactly the brick pool's sense.

## The capture is a march, and the cache does not know what filled it

`TracedCardCapture` walks one orthographic ray per texel through an `IDistanceField`, storing depth,
the gradient as the normal, and an `ISurfaceMaterial`'s albedo and emissive — deterministic, so the
rasterising runtime capture (the device half, owed) has a reference to be compared against texel by
texel. `SurfaceCacheStore` holds it all and shares the pools' property: a texel captured by this
reference and one a rasteriser writes are the same texel.

A texel's outgoing radiance is `emissive + albedo · (direct + gathered)`, both terms incident
irradiance over π — the package convention, so albedo turns them into diffuse radiance. Sampling
picks the best-facing card containing the point at the stored depth: containment finds the box,
depth agreement proves the card captured *this* surface and not one in front of it, facing proves
it saw the surface's own side.

## Radiosity is a gather that reads last pass

`CardRadiosity.Light` puts the sun on every texel — cosine, a shadow ray through the field, over π.
`Gather` shoots deterministic cosine-weighted rays from every texel: a cached hit brings back the
surface's outgoing radiance, an uncached one black (the tracers' own honest answer), an escape the
sky — which is how skylight reaches cards at all, with no ambient term to double-count. Each pass
reads the previous pass's gather (the store double-buffers), so pass *n* carries light bounced *n*
times, and iterating to a fixed point is the infinite-bounce look; the series converges because
albedo is below one, and the Cornell box measures the limit instead of trusting the argument.

## The Cornell box is the exit criterion, and its numbers were measured first

Doc 19 § L4's exit: *a Cornell-box fixture converges to a reference within a stated error; the
second bounce is visible and measurable rather than asserted.* `CornellBoxTests` builds the five
walls and the ceiling panel as cards, converges, and holds a cosine gather over the cache against a
five-bounce path tracer — Halton with two fresh prime bases per bounce, because the first version
shifted one sequence per bounce, correlated the bounces into a one-dimensional lattice threading a
four-dimensional domain, and biased the two-bounce estimate by a fifth. **The stated error is five
per cent** (measured at 1.2 before being stated). The second bounce is the red wall's colour
arriving on the floor: emissive-only the floor's red-to-green ratio is the white panel's — one —
and converged it rises past 1.1, colour that took two bounces to get there. Two convergences agree
to the bit, which is the property every dispatch comparison will lean on.

## The seam every tracer left open is closed, on both processors

`SurfaceCacheRadiance` wraps any `IRadianceSource`: a hit inside a resident card answers with the
card's outgoing radiance — direct, emissive, and every bounce — and everything else falls through
to the wrapped source, so composing it over an existing scene changes exactly the hits the cache
covers. A screen probe on the Cornell floor holds the panel in its up-texel and red toward the red
wall, while the same probe over the black world stays dark: the L2 fillers and the L3 gather
inherit multi-bounce light without changing a line.

The shader half is the same idea as a compose slot: `ISurfaceCacheSource` in the Raven
`SurfaceCache` package, with `NoSurfaceCache` answering the black every tracer always answered and
`SurfaceCacheSource` answering with the card atlas — `TryRadiance`'s walk, test for test.
`ScreenProbeTrace` composes it at its hit branch, and the bounce kernel reads the cache through the
same implementation it feeds.

## Sampling is one grid cell, and the linear scan referees it

`SurfaceCardIndex` is a uniform grid of card lists: a card registers into every cell its box
overlaps, so the cell a point falls in holds a superset of the cards that could contain it, in
arrival order — which keeps the equal-facing tie-break on the earlier card without the index
knowing there is one. The containment, depth and facing tests run unchanged on what it returns; a
test holds the indexed answer against the linear scan rewritten in full, on random cards, half the
queries aimed at stored surfaces so the comparison is not a comparison of misses.

## The device halves, each against the reference that was built first

- **The dispatches.** `SurfaceCacheLight` and `SurfaceCacheGather` (`Vixen.Rendering.Lighting`) run
  `Light` and `Gather` as compute over `SurfaceCacheTexture`'s atlas planes — the gather casting
  this package's own Hammersley rays, reading the front of the double buffer while writing the
  back. The open-sky device tests compare under `NoDistanceField`, pure arithmetic; the seam test
  hands **one** `GlobalDistanceField` object to both sides, so the CPU marches the very grids the
  clipmap uploads — drift measured at exactly zero on the measuring machine, stated at 1e-4.
- **The runtime capture.** `SurfaceCardCapture` rasterises a card in `IrradianceCubeCapture`'s
  mould: the projection is derived from the card so framebuffer texel (x, y) *is* card texel
  (x, y) — pinned by a closed form over all six axes — and three passes over one attachment stand
  in for MRT, because a scene's pipelines already target one colour format. Compared against
  `TracedCardCapture` on one scene captured both ways: validity texel for texel, materials to float
  precision, depth to the march's own arrival hair.

## And the cache lives in the frame

`SurfaceCacheRenderer` (`Vixen.Rendering.Compositor`) sequences a frame's keeping: decode last
frame's capture, record the next card round-robin, light, upload, bounce, swap, publish — an order
a caller cannot invert, with one author per plane and both refused rather than resolved. The device
sampler's scan is one grid cell of `SurfaceCardIndex`'s device form — a dense grid over the cards'
padded union, ascending candidates per cell — with the zero-drift seam test refereeing the change.

## Not yet, and named so the absence is a decision

- **The one-pass MRT capture.** The three-pass capture is its baseline and its referee, and a
  scene's pipelines already target its one attachment.

**Nothing in this package creates or calls a graphics device** — the dispatches, the mirror, the
capture and the node live in `Vixen.Rendering`, where the devices are.
