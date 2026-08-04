# 03 — PBR Showcase

Twenty-five spheres: metallic along one axis of the grid, roughness along the other, on a floor,
under one sun — rendered through the standard frame. The frame document is seven knobs:

```yaml
version: 2
game: !StandardFrame
  quality: High
  shadows: Cascades
  gi: Ambient
  reflections: Off
  antialiasing: Taa
  exposure: Automatic
  output: SceneColour
```

```bash
dotnet run --project Samples/03-PbrShowcase/PbrShowcase.csproj
```

`--vixen-frames N` renders N frames and exits, which is how CI proves the whole frame — cascades,
occlusion, the temporal resolve, the meter — builds, runs and stops without a validation error or a
hang.

## What it is for

The arrangement is the point. Along one axis a dielectric becomes a metal; along the other a mirror
becomes a diffuse surface. Any mistake in the microfacet model shows up as one row or one column
behaving unlike its neighbours — which is a far better test of a BRDF than a single object under a
single light, where almost anything looks plausible.

| Axis | What changes | What to look for |
|---|---|---|
| One way | metallic, 0 → 1 | The diffuse lobe disappears and the reflection takes the base colour |
| The other | roughness, 0 → 1 | One tight highlight spreads into the whole lit hemisphere |

The sample is also the smallest complete example of a project on the
[standard frame](../../docs/guide/rendering/standard-frame.md): what the seven knobs buy, and what
each one still asks of the host.

## How it is built

This sample used to open a `VulkanDevice` and record two passes by hand, and what it owed for that
honesty is recorded in its own old text: no shadows, no image-based lighting, one post effect,
because each meant authoring a frame. The conversion pays all three debts at once — the document
above expands into the cascade atlas, the occlusion pair over the ambient split, the velocity pass
and temporal resolve, the histogram meter and the tonemap — and the code keeps only what a document
cannot say:

- **What exists** — `PbrShowcaseGame.Spawn`: 25 `!Sphere` primitives whose two material numbers are
  their grid coordinates, a floor for the shadows to land on, a sun, a camera on a slow orbit.
- **The knobs' host halves**, each marked ⚠ where it is paid:
  - `shadows:`/`antialiasing: Taa` → the `Shadow` and `Motion` caster stages in `OnConfigure`,
    because a document cannot decide what an object is extracted as.
  - `gi: Ambient` → `SplitOutputs` on the materials (`GridMaterials`) and a `GlobalDistanceField`
    handed to the builder (`SupplyFrame`), filled with exact analytic sphere distances so the
    occlusion under each sphere is a real march.
  - the per-frame set → `ShowcaseFrame`, sample 13's `ArenaFrame` carried over: a baked Preetham
    sky (which is also the image-based ambient the old sample lacked), the probe fallback, and the
    stand-ins for bindings the shader declares whatever the permutations say.
- **The materials** — `GridMaterials`, an `IMaterialSource` the project implements, because the
  grid is arithmetic and twenty-five `.vxmat` files would be that arithmetic transcribed by hand.

`reflections:` stays `Off` deliberately: the screen-trace node resolves through builder slots
(`Effects`, `TracePyramid`) that only sample 13's illumination wiring fills today, and
`StandardFrameAsset`'s remarks record driving them from the expansion as owed. The day that lands,
one line of the document is the whole upgrade.

## What the conversion found

Worth recording, because both produced refusals — loud ones, which is the improvement over the
class of bug the old README ends on:

- **A `!StandardFrame` document could not be imported at all.** The content build's binary
  serializer had no entry for the node's nullable `quality:` — every non-nullable enum member is
  serialised inline and never touches the registry, and this was the first nullable one. Samples 12
  and 13 never hit it because their documents are hand-authored. `QualityTierSerializer` in
  `Vixen.Rendering.PostFx` is the fix and carries the account.
- **`shadows: Cascades` crashed any scene with no point or spot lights.** The punctual-shadow node
  skipped its pass entirely with nothing to draw, while the Main pass the expansion emits declares
  a read of the atlas by name — a read with no producer, refused at graph compile on frame one.
  The node now clears its atlas when it has no lamps, which is also the true answer: a cleared
  depth reads as "unshadowed".

## Where to go from here

`Samples/01` is the device layer this sample used to demonstrate, kept raw on purpose. Sample 13 is
the other end: the same frame hand-authored at eleven hundred lines, because it is the showcase and
the test bed. [Choosing a frame](../../docs/guide/rendering/choosing-a-frame.md) is the decision
between them, and `vixen frame explode` on this sample's document is the demonstration — the
knobs above, written out in full, comments included.

**Look at the output.** The old README closed with two bugs that five clean frames never caught and
one rendered PNG caught in a minute, and the lesson survives the conversion unchanged: the golden
suite holds the engine's pictures, but a sample's picture is held by whoever looks.
