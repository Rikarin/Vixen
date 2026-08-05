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

game: !Sequence
  children:
    # … the lit pass, then the water surface pass writing WaterSurface and WaterNormal …
    - !Copy   { source: SceneColour, destination: SceneColourCopy }
    - !Water  { behind: SceneColourCopy, output: SceneColour, view: Camera }
```

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

## The reflections are doc 19 § L5's, not SSR's

A routing decision rather than a compromise. Unreal's water pass classifies tiles specifically so it
can run an indirect SSR draw over them, because SSR is what it has. Vixen's SSR is ⬜ and its traced
reflections are ✅ — a mirror ray marches the global distance field — so a lake reflects a mountain
that is **off screen**, which is the single most common reflection failure in every screen-space
implementation and the one that makes water look like a mirror bolted to the ground. Leave
`reflections:` empty and the pass compiles the variant without it.

## Two places this diverges from § D8, both deliberate

**The surface plane carries coverage, not a shading-model id.** The doc says to classify from the
G-buffer's shading-model id "which already exists" — it does not: the deferred path is ⬜ and this
engine is Forward+. The surface pass writes a one-channel mask, which is exactly what an id comparison
would have produced, and when the deferred path lands that binding becomes the comparison with nothing
else changing.

**Tile classification is not built yet.** § D8 keeps it "for its actual reason: the water pass is
expensive per pixel and covers a small fraction of most frames" — that reason is still true and it is
an optimisation over a correct pass, so it lands after the look is proven rather than before.

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

⚠ **`WaterBodyComponent` is a *managed* component** because it names its spline by string, so the fold
reaches it one entity at a time rather than as a span. The transforms beside it are unmanaged and are
read as a span, which is why only one of the two loops looks unusual.

⚠ **Bodies are cached by identity, and that is what makes the whole amortisation real.** A fold that
built a fresh `WaterBody` every frame hands the zone a different list every frame, marks the field
dirty every frame, and re-rasterises every frame — the cost § D3's threshold exists to avoid, paid in
full and invisible in a picture. `RebuiltBodies` and `UploadCount` are the readings that say it is
working; both should track the *change* count and not the frame count.

Two diagnostics rather than one: `ZonelessBodies` is a body no zone's window reached, and
`UnresolvedBodies` is one whose spline has not loaded. The fixes are different — a zone's extent
against an asset name — so one number for both would send an author to the wrong place.

## The alpha is the waterline mask

⚠ Not an opacity. § D9 separates the underwater *volume* from the *waterline* explicitly, because a
camera straddling the surface needs two treatments in one frame divided by a curve that is the
intersection of the wave surface with the near plane — and a post-process volume's fold produces one
weight for the whole frame. This pass already knows, per pixel, whether the surface is in front of the
camera, so it says so. Designing the volume path first and discovering the waterline second is how the
transition ends up a hard cut whose fix is architectural.
