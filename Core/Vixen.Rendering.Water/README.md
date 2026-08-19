# Vixen.Rendering.Water

Water's device half: the pass that integrates absorption and scattering over the depth of water
between the surface and whatever is behind it, and composites it once.

Specified in [`docs/plan/35-water.md`](../../docs/plan/35-water.md) § D8, and it is what
[`docs/overview.md`](../../docs/overview.md) § 1.9 recorded **transmission / refraction** as waiting
for: "needs the scene colour or an environment sample — a pass concern, not a lobe".

```yaml
resources:
  - name: SceneColour
    format: Rgba16Float
    usage: ColourTarget, Sampled, CopySource
  - name: SceneColourCopy
    format: Rgba16Float
    usage: Sampled, CopyDestination
  - name: WaterSurface
    format: Rgba16Float
    usage: ColourTarget, Sampled
  - name: WaterNormal
    format: Rgba16Float
    usage: ColourTarget, Sampled

game: !Sequence
  children:
    # … the lit pass …
    - !WaterSurface { surface: WaterSurface, normal: WaterNormal, sceneDepth: SceneDepth, view: Camera }
    - !Copy         { source: SceneColour, destination: SceneColourCopy }
    - !Water        { behind: SceneColourCopy, output: SceneColour, view: Camera }
```

⚠ **All three, in that order, or there is no wet pixel.** `!WaterSurface` is what draws the geometry;
without it `!Water` reads a cleared mask, finds no coverage anywhere and passes the frame through
unchanged — a water stack that is wired, tested and invisible. And the copy has to be taken *after*
everything the water will be composited over.

## The surface is a mesh, and it is the terrain's quadtree

§ D4. `!WaterSurface` draws every zone's patches — the same instanced 33² grid, morphed the same way,
sharing the terrain's index buffer because the lattice is the same lattice — and writes two planes
rather than a lit pixel: a coverage mask carrying the surface's device depth, and the surface's world
normal carrying its foam. Splitting them that way is what lets the volume integration be one
full-screen pass over the pixels that *have* water rather than a per-fragment loop over the ones that
draw it.

⚠ **Depth is tested and never written, and that is what makes the pass above possible at all.** The
composite unprojects the *scene* depth to find what is behind the water; a surface that wrote depth
would put itself there, and the water would be integrated against itself — clear everywhere, at every
depth, with nothing in a capture to say why.

⚠ **The far skirt is drawn before the window.** With depth writes off nothing arbitrates between two
fragments at one pixel except which came last, and the near mesh is the one with a field under it.

## The copy is the blocker, not the pass

§ B1. The compositor could always express "run after deferred lighting, read the scene colour, write
the scene colour". What it could not express is the part that makes that legal: **sampling a target a
pass is also writing is undefined** — not slow, not approximate. So the read comes from `!Copy`'s
destination, and naming the output in `behind:` is refused by name at build time rather than rendering
on one driver and not another.

## Integrated, not blended

Alpha blending gives a surface whose opacity is a number somebody typed. Absorption over a path length
gives one whose colour *and* opacity are both consequences of how deep it is: a shallow edge is clear
because the path is short, and it goes green-blue and then black over metres because the long
wavelengths are absorbed first — from one coefficient triple rather than a gradient somebody painted.

⚠ **Absorption and scattering are separate coefficients, not one "extinction".** Absorption takes
light out and never gives it back, which is what makes deep water dark; scattering takes it out of one
direction and puts it into another, which is what makes shallow water *bright*. Folded into one number,
water can be murky or clear but not both.

⚠ **The path is measured along the view ray, not vertically.** The two differ by the grazing angle —
by a factor of ten at the angle most water is seen from — and using the vertical depth makes a lake
read as clear near the far shore, where the path is longest, which is precisely backwards.

⚠ **The in-scatter saturates, and that is physics rather than a clamp.** Past a few extinction lengths
more depth adds no more scattered light, because what is scattered in at the far end is absorbed again
on the way out. A model that multiplied by depth looks like fog.

⚠ **A phase function integrates to one over the sphere, so punctual lights alone integrate to black.**
Measured at ten depths, every channel went to zero: a single directional light contributes almost
nothing outside its forward peak, and a sea with the sun behind the viewer had no in-scatter at all.
The arithmetic was right about the term it had and silent about the one it did not have — water is
blue because of what arrives from the *whole sky*, which is an **isotropic** in-scatter with no phase
function. `WaterVolume.AmbientInScatter` and `skyColour` are that term, and with it the depth sweep
saturates where it should: red at 0.03, green at 0.24, blue at 0.51, with forty metres the same
picture as sixty.

⚠ **`SingleLayerWaterShading` is specular only.** Every photon read as "the colour of the water" was
scattered by the volume above, so a diffuse lobe on the surface counts the same photons twice — which
makes the shallows too bright *and* makes water get lighter with depth. Reading the pass's own output
in `behind:` is refused by name at build time, because that is § B1's undefined case.

## The reflections are doc 19 § L5's, not SSR's

A routing decision rather than a compromise. Unreal's water pass classifies tiles specifically so it
can run an indirect SSR draw over them, because SSR is what it has. Vixen's SSR is ⬜ and its traced
reflections are ✅ — a mirror ray marches the global distance field — so a lake reflects a mountain
that is **off screen**, which is the single most common reflection failure in every screen-space
implementation and the one that makes water look like a mirror bolted to the ground. Leave
`reflections:` empty and the pass compiles the variant without it.

## Where this diverges from § D8, deliberately

**The surface plane carries coverage, not a shading-model id.** The doc says to classify from the
G-buffer's shading-model id "which already exists" — it does not: the deferred path is ⬜ and this
engine is Forward+. The surface pass writes a one-channel mask, which is exactly what an id comparison
would have produced, and when the deferred path lands that binding becomes the comparison with nothing
else changing.

**The tile list is a flag per tile and not a compacted list, so the draw is instanced rather than
indirect.** § D8 got the shape from Unreal, which classifies tiles so that an *indirect* draw runs over
the ones it found — and an indirect draw needs the count on the device. `ICommandList` has
`DrawIndexedIndirect` and no non-indexed `DrawIndirect`, so an indirect path here is either a
three-entry index buffer for a triangle that has no vertex buffer, or the count coming back to the host
— a stall a frame long, every frame, to avoid a pass over tiles that are mostly empty. That is the cost
the feature exists to remove, paid the other way round.

So the classification writes one word per tile, the draw is `Draw(6, tiles)`, and a dry tile collapses
to a degenerate rectangle in the vertex stage. At 1080p that is thirty-two thousand instances the setup
engine discards — against a full-screen pass of the most expensive fragment shader in the frame, which
is what it replaces.

## § D8's tile classification

`!Water` is a `FullScreenRenderer` with a count on it. `Tiled` puts a compute dispatch ahead of the
draw — `WaterTiles.rvn`, one workgroup per 8×8 tile and one lane per pixel — which reads the coverage
mask **exactly the way the fragment stage reads it**: point sampled, at the centre of a target pixel.
That is what makes the claim conservative by construction rather than by argument. A classifier that
walked the surface plane's own texels would agree only while the two planes were the same size, and
would silently drop water the day one of them was not.

⚠ **A tiled pass loads its target and leaves a dry tile alone**, where the untiled one writes every
pixel of the frame — the scene colour back, with a zero mask in alpha. Those are the same picture only
where the target already holds what `behind:` is a copy of, which is what § B1's `!Copy` arranges: it
filled `behind:` from this very target. That is why `WaterAsset.Tiled` is on for a document and
`WaterRenderer.Tiled` is off for a node somebody wired by hand.

⚠ **And what a dry tile no longer writes is the alpha mask.** § D9's composite does not read it —
`Underwater.rvn` reads the surface plane's own coverage, for the reason its remarks give — so the
pass's alpha remains the mask everywhere the pass ran.

`WaterPassImageTests` renders the frame both ways and compares them: identical where there is water,
and where there is none the tiled render still holds a colour the fixture primed the target with, which
is the difference between "the optimisation is harmless" and "the optimisation happened".

## The zone, on the device

`WaterZoneComponent` and `WaterBodyComponent` are what a scene carries — plain entities with
transforms, duplicated and prefabbed like anything else. `WaterZoneSystem` folds them into the
`WaterZoneState`s the kernel owns, and `WaterInfoTexture` uploads a field into the four channels
§ D3 names: surface height, flow in two, and the ground beneath.

⚠ **Depth is not a channel.** How deep the water is at a texel is the surface minus the ground,
computed where it is used — storing it would be a third number that can disagree with the two it came
from.

⚠ **The components live here and not in the kernel**, which is `Vixen.Rendering.Terrain`'s arrangement
and its reason: a kernel that referenced the ECS would be a kernel a dedicated server could not link
without also linking a world.

⚠ **Both components are *managed* ones** because each names an asset by string — a body its spline, a
zone its `.vxwaves` — so the fold reaches them one entity at a time rather than as a span. The
transforms beside a body are unmanaged and are read as a span, which is why only one of the two loops
looks unusual.

⚠ **A named sea state becomes a value in `GatherZones` and nowhere else.** The component published in
`Zones` carries the resolved spectrum, so the vertex stage and the underwater shape read one field
and neither has to know the asset exists. Resolving in two places would be two answers to "what sea is
this", and the frame they disagree on is a boat riding a different swell from the one drawn under it.

⚠ **Bodies are cached by identity, and that is what makes the whole amortisation real.** A fold that
built a fresh `WaterBody` every frame hands the zone a different list every frame, marks the field
dirty every frame, and re-rasterises every frame — the cost § D3's threshold exists to avoid, paid in
full and invisible in a picture. `RebuiltBodies` and `UploadCount` are the readings that say it is
working; both should track the *change* count and not the frame count.

⚠ **And the cache must store the success, never the failure.** The first version recorded a body whose
spline had not loaded as unresolved against a component and a placement that then never changed — so
it was never asked again for the life of the world. Every asset source a game has answers null for its
first frames *by construction*, so a lake named in a scene could not appear in a running game at all,
and the failure was permanent rather than transient. `GatherZones` beside it re-resolved every fold
with no cache, which is why a late `.vxwaves` worked and the `.vxspline` next to it did not. The test
counts the *asks* rather than the bodies: a source that answers on the first ask — which is every test
double in the suite — cannot tell a retry from a cache hit, and that is how a thoroughly covered fold
shipped this.

Three diagnostics rather than one: `ZonelessBodies` is a body no zone's window reached,
`UnresolvedBodies` is one whose spline has not loaded, and `UnresolvedWaves` is a *zone* whose sea
state could not be used. The fixes are different — a zone's extent, an asset name, an asset name
again — so one number for all three would send an author to the wrong place.

⚠ **The third is not like the other two, and `stat water` draws it differently on purpose.** A
zoneless or unresolved body is water that is not on screen; a zone whose sea state did not load has
water that looks entirely convincing and is the wrong sea, which on a client is a boat that rides
differently from the one on the server. It is warned, not flagged red, and it is the only evidence
there is.

## The alpha is the waterline mask

⚠ Not an opacity. § D9 separates the underwater *volume* from the *waterline* explicitly, because a
camera straddling the surface needs two treatments in one frame divided by a curve that is the
intersection of the wave surface with the near plane — and a post-process volume's fold produces one
weight for the whole frame. This pass already knows, per pixel, whether the surface is in front of the
camera, so it says so. Designing the volume path first and discovering the waterline second is how the
transition ends up a hard cut whose fix is architectural.


## The waterline is a curve, and a fold cannot produce one

[§ D9] divides underwater into two features that look like one, and warns twice that getting the
order wrong is architectural: **"designing the volume path first and discovering the waterline second
is how you get a system where the transition is a hard cut and the fix is architectural."**

`UnderwaterShape` is the volume half and grades the whole frame. `!Underwater` is the other half. A
fold produces **one weight**; a camera straddling the surface needs two treatments divided by the
intersection of the wave surface with the near plane, which is a per-pixel question no scalar answers.

⚠ **The curve is solved against the local surface *plane*, not the wave sum, and the approximation is
stated.** The exact answer needs the info texture and the Gerstner sum bound into a post-process node
— a second place for § D2's seam test to have to hold. Over the few centimetres a near plane spans, a
wave is its own tangent plane to well under a millimetre. What it costs is a crest smaller than the
near plane passing the camera, which is spray rather than a waterline.

⚠ **The plane comes from the same `WaterQuery` the volume fold and the buoyancy solver read, at the
same water time.** A waterline drawn against the rest height sits at mean sea level while the drawn
surface moves around it, which reads as the camera being wrong rather than the line being wrong.

⚠ **The mask does one job here, and it is not the waterline.** `WaterSurface`'s coverage says the
surface is between the eye and the scene — which, underwater, means *the ray leaves the water there*.
So it bounds the fog path. A diver looking up sees the sky through a metre of water; taking the
distance to the sky instead makes looking up exactly as dark as looking down at the bed, which is the
failure that reads as "underwater is just a blue filter".

⚠ **The distortion and the caustics are the volume's, not the lens's** — § D9 says so outright, and the
caustics fade with `submersion` rather than with the ray's length. The two are different questions and
the wrong one is quietly wrong: caustics land on whatever the light reaches, so a diver just under the
surface sees them on a wall thirty metres away and a diver at thirty metres sees none on a wall he
could touch. Fading by the path is the second of those applied to the first, and an image fixture is
what caught it.

⚠ **A node with no zone system leaves its plane alone; a node whose zones answer nothing overwrites
it.** The difference matters: a host that has not wired the system up has made no claim about where
the water is, and a host that has and got nothing has claimed there is none. Collapsing the two makes
the node impossible to drive by hand, which is how an image fixture reaches it — and is how the
collapse was found. The plane's own default is a kilometre below the world rather than the origin,
because a plane at the origin fogs the bottom half of every frame in a project whose ground sits below
zero, and that reads as the effect working.

## The six `water.show*` draws, and two rules in them

`WaterDebugDraw` draws five of the six into `DebugDraw` — an *accumulator* rather than a renderer,
which is why this project can draw into it without knowing what a line pass is. The sixth,
`water.showBuoyancy`, is `BuoyancyDebugDraw`'s in `Vixen.Water.Physics`: the flag stays with the
console verb and the drawing goes with the data, because a renderer must not reference the assembly
that links Jolt.

⚠ **What carries the sixth across is `BuoyancyDebugSystem`, and it takes the flag as a delegate.** For
a long time nothing carried it at all and the toggle set a bool nobody read. A host that has both
assemblies writes `Show = () => WaterDebug.ShowBuoyancy` once — `Samples/13-ThirdPersonShooter`'s
`Arena` is the one that does — and the direction of that line is the whole point: the flag is pulled
from over here, never pushed from over there.

⚠ **`water.showTiles`' colour rule cannot be `WaterBody.Contains`.** That is an even-odd test on a
*closed* boundary, so it is false for every river — and "coloured by body kind" painted every open
body as the far skirt, which is exactly the case the verb exists to diagnose. It reads the body's
*contribution* instead, the same function the field was rasterised from, so the colours agree with the
water by construction.

⚠ **`water.showLod` draws two rings per level, not one.** A pop at the outer ring is a selection range
that is too near; a pop inside the band is a morph that never reaches zero. Those have different
fixes, so one ring cannot tell an author which they are looking at. The patches come from the frame's
own selection — `WaterSurfacePass.Selected`, published for this — because an overlay that descended
the quadtree a second time would agree with the frame right up until the moment it stopped, and that
moment is the bug somebody turned the overlay on to find.

## Underwater is a shape, not a system

§ D9, and it cost the shape generalisation and nothing else. `UnderwaterShape` implements doc 32's
`IPostProcessShape`, `WaterZoneSystem` is the `IPostProcessShapeSource` that supplies it, and an
underwater grade is a `PostProcessVolume` with `Shape: Custom` on the zone entity. The priority, the
blend radius and the optional fields are all doc 32's, untouched.

⚠ **Per zone rather than per body, and that diverges from the reference deliberately.** Unreal hangs
an `UnderwaterPostProcessSettings` on each water body, so a river running into a lake is two volumes
an author has to keep in agreement and a camera at the mouth is inside both. The zone's field has
already resolved every body once — that is what § D3 is for — so *am I underwater* is one question
about a field rather than N questions about bodies.

⚠ **It tests the drawn surface, waves included, which is why it is rebuilt per frame.** Against the
rest height the boundary would sit at mean sea level, and a camera in a swell would cross it half a
second before and after the water actually reached it.

⚠ **It does not answer the waterline, and that separation is § D9's whole point.** A fold produces one
weight for the frame; a camera straddling the surface needs two treatments divided by a curve. The
node that draws that curve is `UnderwaterRenderer`, and it is built: `!Underwater` is an
`UnderwaterAsset` that `WaterRendererFactory` constructs, over `Raven/Library/Water/Underwater.rvn`,
with an image golden. ⚠ **The curve is not the alpha mask, though this paragraph used to say it would
be.** `Underwater.rvn` is explicit that the mask does one job and it is not the waterline; the
waterline is solved per pixel against the local surface plane, which is what survives a camera pushed
through a wave. **What is owed is a frame that names it** — no shipped `.vxcompositor` carries
`!Underwater`, so the node is reachable only by a document somebody authors.
