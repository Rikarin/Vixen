<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Water

Oceans, lakes and rivers that share one surface: a spline and a profile per body, rasterised into one
top-down texture per zone that carries surface height, flow and the ground beneath it; a mesh that is
the terrain's own quadtree reading a different height source; a shading model that integrates a depth
of water rather than blending an alpha; waves from a spectrum asset; and one evaluator that the
vertex stage, the buoyancy solver and a gameplay query all call, so a boat floats at the height the
water is drawn at.

This document extends [06 § Geometry and materials](06-rendering-pipeline.md) and
[31](31-terrain-grass-and-trees.md), and it closes an owed row in
[29](29-players-and-possession.md). It is a separate file for
[31](31-terrain-grass-and-trees.md)'s reason: five subsystems own a piece of it — terrain, physics,
the compositor, post-process volumes and character movement — and the first third of it is an
argument rather than a schedule.

**Read [the rows this closes](#the-rows-this-closes) before the phases.** One of them is a decision
recorded as *deliberately not built*, and this reopens it.

---

## The rows this closes

[29 § Where this stops](29-players-and-possession.md) says:

> **No swimming.** It needs water volumes, which do not exist. A mode that could never be entered
> would be a promise in an enum, so `CharacterMoveMode` has three members and not four.

That is exactly right and it is the shape of the whole document: several subsystems have already
written down that they are waiting for water, each of them correctly declining to invent it.

| Where | What it says | What happens to it |
|---|---|---|
| [29 § Where this stops](29-players-and-possession.md) | No swimming, because there are no water volumes | [D11](#d11-swimming-is-a-fourth-move-mode-and-immersion-is-the-only-new-number). `CharacterMoveMode` gains its fourth member |
| [overview § 1.9](../overview.md) — *Transmission / refraction ⬜* | "Needs the scene colour or an environment sample — a pass concern, not a lobe" | [D8](#d8-the-surface-is-a-pass-between-lighting-and-translucency-and-its-reflections-are-l5s). Water is the pass that supplies it, and the lobe follows |
| [28 § Vixen.Gameplay.Movement](28-gameplay-framework.md) | Lists "swimming, flight, gliding, water craft" as a planned library | Unblocked. The buoyancy solver and the surface query are what a water craft is built out of |
| `Raven/Library/Geometry/Displacement.rvn` | Its own comment says "Foliage, **water** and a material's own displacement all sway" | Honoured rather than duplicated — [D12](#d12-ripples-are-a-sliding-window-height-field-and-they-are-displacement-not-geometry) |
| [06 § Geometry and materials](06-rendering-pipeline.md) | No water row at all | Added, and it is a *pass* and a *shading model*, not a material anybody writes |

### Where the line goes

| In | Out |
|---|---|
| Ocean, lake and river bodies from splines; a custom body from a mesh | A hydrology solver, a watershed, a river network generator |
| One surface mesh for every body in a zone, with transitions | A separate mesh per body that the artist lines up by hand |
| Gerstner waves from a spectrum, attenuated by depth | A full FFT ocean in the first pass — [D7](#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic) defers it with arithmetic |
| Terrain carved by the bodies over it, non-destructively | A terrain that water erodes at run time |
| A shading model that integrates absorption and scattering over a depth | A volumetric water renderer with god rays through the surface |
| Underwater as a post-process volume, with a waterline | A wet-surface system — puddles, drips, screen droplets |
| Buoyancy from pontoons, and swimming | A vehicle physics library. [28](28-gameplay-framework.md) owns the boat |
| A sliding-window ripple simulation around the view | A 3-D fluid solver, splashes, spray, breaking surf |
| Flow that pushes bodies downstream and drags the ones in it | Water that can be dammed, drained, or poured |

⚠ **The test for the left-hand column is [31](31-terrain-grass-and-trees.md)'s, reworded: does an
environment artist reach for it while blocking out a level?** Flow velocity per spline point is on
the left because a river without it reads as a bent lake. A hydrology solver is on the right because
it is a content-generation product, and the two engines that ship water both ship it as a plugin.

---

## What the references actually ship

Surveyed rather than remembered.

### Unreal Engine 5 — the Water plugin

[The Water system](https://dev.epicgames.com/documentation/en-us/unreal-engine/water-system-in-unreal-engine)
is a plugin — off by default, enabled in *Edit ▸ Plugins*, requiring a restart, shipping its own
`Water` content folder that is invisible until *Show Plugin Content* is on. That is worth noting
before anything else: **Epic's own water is not part of the renderer**, and the seams where it
reaches into one are visible in the design.

It is six pieces.

**1. The Water Zone and the water mesh.** A `AWaterZone` must exist or nothing renders; it owns a
`UWaterMeshComponent` and applies to every water body inside it. The mesh is a quadtree traversed
**every frame** to produce the visible tile set, and tiles *morph* between levels rather than
switching — "four quads collapse into one for lower LOD, or expand to 16". The knobs are `Tile Size`
(default 2400 uu), `Extent in Tiles` (64 from centre to edge), `LODScale` (where morphing begins),
`Tessellation Factor` (vertex density within a tile), and a **Far Distance** mesh — a low-vertex
skirt to the horizon with its own simplified material.

**2. The Water Info Texture, which is the keystone and is documented as a property.** `AWaterZone`
holds a `water_info_texture` (`TextureRenderTarget2D`), a `render_target_resolution`, a
`zone_extent`, a `capture_z_offset` ("offsets the height above the water zone at which the
WaterInfoTexture is rendered"), a `half_precision_texture` flag (16 vs 32 bits per channel), a
`velocity_blur_radius` for its finalise pass, and a `water_zone_index` — "unique Id for accessing
zone data in GPU buffers". Every water body renders into it through a dedicated
`water_info_material`, and — the part that is easy to miss — **so does the terrain**:
`UWaterTerrainComponent` "can be attached to any actor with primitive components to allow them to
render into a Water Info Texture as the terrain". The texture is therefore where *water surface*,
*flow velocity* and *ground height* meet, and depth is their difference.

Since 5.3 the zone can run in **local tessellation** mode, where the info texture "represents only a
sliding window around the view location", sized by `local_tessellation_extent` — both the texture and
the quadtree are then regenerated at run time, and a smaller window buys pixel density.

**3. Water bodies.** Five actors, four of which are splines:

| Actor | Shape | Notes |
|---|---|---|
| `AWaterBodyOcean` | Closed spline | Marks the shoreline; the water extends past it to the far mesh |
| `AWaterBodyLake` | Closed spline | One height for every point |
| `AWaterBodyRiver` | Open spline | Height varies along it, and it has a direction |
| `AWaterBodyCustom` | A static mesh | Still carries a spline, for querying |
| `AWaterBodyIsland` | Closed spline | Raises the terrain, so land sits above water |

Plus `AWaterBodyExclusionVolume`, which "allows players not enter surface swimming when touching a
water volume" — a cave under a lake.

A river's spline carries `UWaterSplineMetadata`: **depth, width, velocity and audio intensity per
control point**, with a `WaterSplineCurveDefaults` set of fallbacks. Right-clicking the spline in the
viewport offers *Visualize Water Velocity*, which draws a trio of arrows scaled by the value.
`UWaterBodyRiverComponent` adds two materials over the base — `lake_transition_material` and
`ocean_transition_material` — "used when a river is overlapping a lake" and the ocean respectively,
because a river mouth is the one place two flow models meet.

**4. Terrain carving.** Water bodies deform the landscape under them through a landscape brush, and
**only if the landscape has *Enable Edit Layers* checked**. The property surface is large and worth
reading as a list of the problems a shoreline has:

| Group | Properties |
|---|---|
| Curve settings | `Use Curve Channel`, `Elevation Curve Asset`, `Channel Edge Offset`, `Channel Depth`, `Curve Ramp Width` |
| Heightmap settings | `Blend Mode` (alpha / min / max / adaptive), `Invert Shape` (a lake becomes an island), `Falloff Mode` (angle or width), `Falloff Angle`, `Falloff Width`, `Edge Offset`, `ZOffset` |
| Effects | `Blur Shape` and `Radius`, `Curl Amount`/`Curl Tiling` in two octaves, `Inner`/`Outer Smooth Distance`, `Terrace Alpha`/`Spacing`/`Smoothness`, `Mask Length`, `Mask Start Offset` |
| Gate | `Affects Landscape` |

**5. Waves.** A `Water Waves` asset drives a body's surface. The shipped model is Gerstner, generated
either simply — `Num Waves` (default 16), min/max wavelength with a falloff, min/max amplitude, a
`Dominant Wind Angle`, steepness, a seed — or from a spectrum
(`UGerstnerWaterWaveGeneratorSpectrum`). Custom generators derive from
`UGerstnerWaterWaveGeneratorBase` in C++ or Blueprint and emit an array through a `Make GerstnerWave`
node. Per body there is a `Wave Attenuation Water Depth` — "the depth at which waves start to
attenuate" — and a `Max Wave Height Offset` correcting the automatically derived bounds. The wave
data is GPU-resident and indexed by `Water Body Index`.

**6. The Single Layer Water shading model.** A material with blend mode Opaque or Masked and shading
model `SingleLayerWater`, whose node takes **Scattering Coefficients**, **Absorption Coefficients**,
**PhaseG** and **Color Scale Behind Water**. It runs as a custom pass **after the base pass and
deferred lighting and before ordinary translucency**, and it is implemented as a tile classification:
a compute shader classifies screen tiles by material id in the GBuffer, an indirect draw does screen
space reflections into a separate buffer over just those tiles, and reflection captures, sky and SSR
are composited onto the surface. There is no separate translucency sort, which is where the cost
saving is. Mobile and low-end fall back to a path that drops the volume integration, and in the
simplest case to an ordinary translucent material.

**And the rest of the surface**, which matters for scoping:

- **Underwater** is per body: `UnderwaterPostProcessSettings` with `Enabled`, `Priority`,
  `Blend Radius`, `Blend Weight`, and a full `PostProcessSettings` — i.e. a post-process volume
  whose bounds are the water body.
- **Buoyancy** is `UBuoyancyComponent`: spherical **pontoons** (radius, relative location or a mesh
  socket, an FX flag), a `Buoyancy Coefficient`, first- and second-order damping (`Buoyancy Damp`,
  `Buoyancy Damp 2`), `Max Buoyant Force`, drag coefficients, and — river-specific — downstream
  current force and a shore-push that keeps a boat off the bank. `ABuoyancyManager` centralises the
  simulation; `r.Water.DebugBuoyancy 1` draws the pontoons and their sample grid.
- **Queries** are `UWaterSubsystem` and `UWaterBodyComponent::GetWaterSurfaceInfoAtLocation`,
  returning location, normal, velocity and depth into an `FWaterBodyQueryResult` selected by
  `EWaterBodyQueryFlags`. The subsystem also carries an **ocean flood height** (base + flood =
  total), a water time separate from world time, and the camera's underwater depth.
- **Interaction** is Niagara's, not the plugin's: a **Shallow Water** 2-D height-field simulation
  ("useful for simulating pools… boat wakes, or simple interactions"), rendered as a displacement,
  plus `UNiagaraDataInterfaceWater`, `UBakedShallowWaterSimulationComponent` and, since 5.6, a path
  that turns a river body directly into a Niagara 2-D fluid simulation.
- **Debugging** is unusually good and worth copying wholesale: `stat water` (the CPU cost of
  `IsUnderwater` tests, info computation, depth and wave-height queries), `stat watermesh` (vertices,
  tiles, draws, materials), and `r.Water.WaterMesh.ShowTileBounds` — **colour-coded by body type**,
  red rivers, green lakes, blue oceans, yellow and purple transitions — plus `ShowWireframe`,
  `ShowLODLevels`, `ShowTileGenerationGeometry`, `LODCountBias`, `TessFactorBias`, `LODMorphEnabled`,
  `r.Water.WaterSplineResampleMaxDistance` (default 50 cm) and
  `r.Water.VisualizeActiveUnderwaterPostProcess`.

### Unity — the HDRP Water System

[Unity's water](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@14.0/manual/WaterSystem-Overview.html)
arrived in 2022 LTS and is a different set of choices from the same requirements.

A **water surface** is one component with a type — *Ocean, Sea or Lake*, *River*, or *Pool* — and the
waves are an **FFT** simulation rather than a sum of Gerstner waves, summed over up to three
**bands**: swell, agitation and ripples, driven by a wind direction and speed. Around it:

| Piece | What it is |
|---|---|
| **Water mask** | A texture that suppresses simulation per band, per region — how a sheltered bay is authored |
| **Current map** | A texture of flow direction, which is how a river flows without a spline |
| **Deformers** | Components and custom render textures that push the surface down or up — a boat's hull, a waterfall's plunge |
| **Foam generators** | The same mechanism for the foam channel |
| **Water excluder** | A mesh that removes water from a volume — a hull's interior |
| **Underwater** | Optional, and when the camera straddles the surface it generates a **water line** and composites the submerged part of the frame |
| **CPU simulation** | A scripted height query for buoyancy, deliberately a separate evaluation from the GPU one |
| **Water decals** (Unity 6) | Deformers, foam and masks unified as decals projected onto the surface |

### The consensus

Strip the vocabulary and both engines agree on six things, and disagree on two.

1. **The surface is one mesh for many bodies**, LOD'd continuously toward the viewer, not a plane per
   lake. Both morph rather than switch, because water is the worst case for popping — a flat
   specular surface shows a vertex moving from half a kilometre away.
2. **Depth is the material's most important input.** Colour, opacity, foam, wave attenuation and the
   shoreline all come from *how deep the water is here*, which means the renderer needs the ground
   under the water, not just the water.
3. **Flow is a texture, and it is sampled twice at an offset.** Both engines flow-map, both accept
   the periodic reset, and both drive it from authored data — a spline's velocity or a current map.
4. **Translucency is integrated, not blended.** Scattering and absorption over a path length, with a
   phase function, composited against the opaque scene. Neither uses alpha blending for water.
5. **Underwater is a separate composite with its own transition problem**, and the hard case in both
   is the camera crossing the surface.
6. **Buoyancy queries the surface on the CPU**, separately from how it is drawn.

They disagree on **how a body is shaped** — Unreal splines everything, Unity textures everything —
and on **the wave model**, Gerstner versus FFT. Both disagreements are real and are settled in
[D6](#d6-a-water-body-is-a-spline-and-a-profile-and-there-is-no-new-spline) and
[D7](#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic).

⚠ **Point 6 is the one to be suspicious of.** Both engines evaluate the water surface twice — once in
a vertex shader and once in C++ or C# — and neither guarantees the two agree. Unreal's own
documentation reaches for `Max Wave Height Offset` to correct "automatically calculated wave height
bounds", which is a knob that exists because two evaluations of the same wave disagree. That is
[D2](#d2-one-evaluator-two-hosts-and-the-seam-is-a-test), and it is the single largest thing this
design does differently.

---

## Where Vixen already is

The argument for building this now is [31](31-terrain-grass-and-trees.md)'s argument: the subsystem
did not get smaller, the thing it stands on got much larger — and this time most of what it stands on
was built *by* doc 31.

| Water needs | Vixen has | Where |
|---|---|---|
| A quadtree with a vertex morph, no cracks, no skirts | `TerrainLod` + `TerrainGridPatch` — CDLOD node selection and the shared 33×33 patch | `Core/Vixen.Rendering.Terrain` |
| A spline asset, authored, serialised, editable in the viewport | `SplineAsset` / `Spline` / `ISplineSource`, with `SplineEdit` and `SplineCommand` on the gizmo | `Core/Vixen.Core.Mathematics`, `Editor/Vixen.Editor.SceneView` |
| Per-control-point channels along a spline | `TerrainSpline`'s profile — half-width and independent side falloff | `Core/Vixen.Terrain` |
| Non-destructive terrain deformation regenerated wholesale | Reserved edit layers; the Splines layer already does exactly this | `Core/Vixen.Terrain` — [31 § D4](31-terrain-grass-and-trees.md#d4-edit-layers-are-the-storage-model-not-a-feature-on-top-of-it) |
| A top-down render of the world into a texture | `ImpostorCapturePass`, and the grass scatter's terrain sampling | `Core/Vixen.Rendering.Terrain` |
| Sampling a texture in a vertex stage | `SampleLevel`, added for terrain | `Raven/Vixen.Raven` |
| A shading model that is a protocol implementation, not a branch | `ShadingModels.rvn` — eight of them behind one `protocol` | `Raven/Library/Material` |
| Reflections without SSR | `Vixen.Rendering.Reflections` — a mirror ray marches the distance-field tracer | ✅, [19 § L5](19-lighting-and-global-illumination.md) |
| A named scene-colour target a pass can read | `SceneColour` in `CompositorAsset`; `ReflectionTrace` already binds it | `Core/Vixen.Rendering` |
| Post-process settings that layer by priority with a blend radius | [32](32-post-process-volumes.md), built | `Core/Vixen.Rendering.PostFx` |
| A physics world with bodies, forces and a character controller | `PhysicsWorld` over Jolt 2.22 | `Core/Vixen.Physics` |
| A character with walking, falling and flying | `CharacterMovement`, `CharacterState`, `CharacterMotion` | `Core/Vixen.Physics` — [29 § P1](29-players-and-possession.md) |
| Compute dispatch with upload and readback | K2, built and spent | `Core/Vixen.Rendering` |
| Wind displacement shared across consumers | `Displacement.WindPhased`, whose comment already names water | `Raven/Library/Geometry` |
| A page residency manager that does not care what a page is | `PageResidency`, `PageKey.Source` | [22 § improvement 6](22-virtualized-geometry.md) |
| A CPU/GPU seam test with a stated tolerance of *exact* | The grass scatter's, and [19 § L4](19-lighting-and-global-illumination.md)'s surface cache | `Core/Vixen.Foliage.Tests` |

**Eleven of those thirteen rows did not exist a year ago**, and none of them was built for water.
That is the whole of the argument for the estimate in [Part 3](#part-3--phases) being 13 EM rather
than the 25 it would have been.

---

## What blocks it

Six, and only two of them are real work.

### B1. There is no shading model that can read the scene behind it

[overview § 1.9](../overview.md) records **Transmission / refraction ⬜** with the correct diagnosis:
"needs the scene colour or an environment sample — a pass concern, not a lobe". Water is the pass.
What is needed is a compositor node that runs after deferred lighting, reads a copy of `SceneColour`
and the depth buffer, and writes back into `SceneColour` — and the compositor can already express
that (`ReflectionTrace` binds `sceneColor` today). **The blocker is the copy, not the pass**: writing
into a target you are also sampling is undefined, and the copy has to be a real resource the render
graph aliases rather than a barrier somebody remembers.

Closed by [W2](#w2--the-water-pass-and-the-shading-model--20-em), and it closes the refraction row
with it.

### B2. Doc 32's volumes are boxes

[32](32-post-process-volumes.md) built "a box with a priority and a blend radius, and a per-frame
fold over the camera's position". Underwater is a volume whose containment test is *below this
surface and inside this body's shape* — which is not a box, is not static, and moves with the waves.

The fix is small and is a generalisation doc 32 would benefit from anyway: the fold tests
`IPostProcessShape.Contains(worldPosition, out distanceOutside)` instead of an AABB, with the box as
the default implementation and the water body as the second. **A water body must not be the only
non-box shape**, or the seam will be shaped like water; a sphere lands with it.

### B3. `CharacterMoveMode` has three members

By deliberate decision, quoted in [the rows this closes](#the-rows-this-closes). A fourth member,
an immersion depth on `CharacterState`, and the transition rules are
[D11](#d11-swimming-is-a-fourth-move-mode-and-immersion-is-the-only-new-number).

### B4. The terrain's reserved layers are a closed set

[31 § D4](31-terrain-grass-and-trees.md#d4-edit-layers-are-the-storage-model-not-a-feature-on-top-of-it)
names two — Splines and Scatter — each "regenerated wholesale". Water needs a third, on exactly the
same contract. This is a day, and it is the strongest evidence that doc 31 got the storage model
right: the feature that most obviously wants non-destructive terrain deformation was not in scope
when the mechanism was designed, and needs no change to it.

### B5. There is no ping-pong compute target helper

The ripple simulation ([D12](#d12-ripples-are-a-sliding-window-height-field-and-they-are-displacement-not-geometry))
is a height field advanced by a compute pass reading frame N and writing frame N+1. Compute exists
and is spent; what does not exist is a pooled two-target rotation with the render graph told about
the dependency. It is a small render-graph utility, and the VFX GPU path's storage buffers are the
nearest existing thing.

⚠ **`glBindImageTexture` is ⬜** in the GL backend, so on GL the simulation is a fullscreen fragment
pass into a colour target rather than a compute pass into a storage image. Every other compute path
in the engine already carries that variant, so this is a known shape rather than a new problem.

### B6. There is no world streaming

[31 § B6](31-terrain-grass-and-trees.md) unchanged, and water is affected less than terrain: a zone's
info texture is a sliding window ([D3](#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render)),
so it is bounded by the view rather than by the world. What does not stream is the set of water
*bodies*, which is a scene-level problem and belongs to the same unwritten document.

---

## Part 1 — The design

### D1. Three assemblies, and the kernel touches no device

```
Core/Vixen.Water/                body shapes, spline→profile, the wave evaluator, the surface
                                 query, the buoyancy solver, the ripple kernel's CPU reference —
                                 pure functions over arrays and structs
Core/Vixen.Rendering.Water/      WaterRenderer, the info-texture pass, the surface pass, the
                                 ripple dispatch, the underwater composite — the only one that
                                 knows what a device is
Editor/Vixen.Editor.Water/       WaterMode, the body tools, the zone panel, the commands
```

[31 § D1](31-terrain-grass-and-trees.md#d1-two-runtime-assemblies-and-one-editor-assembly-and-the-kernel-touches-no-device)'s
split, for its two reasons and a third specific to water: **the kernel is what the dedicated server
runs**. A headless build has no device and still has to answer *how deep is the water at this
position* for every swimming character and every boat it simulates, and
[D2](#d2-one-evaluator-two-hosts-and-the-seam-is-a-test) makes that answer the same one the client
draws. `Vixen.Water` therefore references `Vixen.Core.Mathematics` and nothing that opens a device,
and the physics join is a separate small assembly rather than a reference from the kernel to Jolt.

⚠ **`Vixen.Water.Physics` is opted into by a game, and that is this section's sentence rather than an
omission.** `Vixen.App.Hosting` links `Vixen.Rendering.Water`, so zones and bodies reach every game
with a `!WaterSurface` node; linking the physics join the same way would drag Jolt into every host
that draws a lake, which is precisely what the split exists to prevent. What it costs has to be said
plainly, because it is the one failure a separate assembly buys: **a `[ModuleInitializer]` cannot run
in a process that never loads the assembly**, so a game that has not added the reference and loads a
scene naming `!BuoyancyBody` fails with `SceneComponentException` — which is loud, is the right
failure, and does not name the package. The guide page does.

⚠ **The editor links it unconditionally, and for a reason that is not physics.** `BuoyancyBody` is a
scene component: without the assembly loaded it is absent from Add ▸ and a scene naming it will not
open, so a boat could not be *authored* before it could be opted into. That is `EditorApplication`'s
`BuiltInSubsystems` list, which exists exactly to touch an assembly nothing else in a running editor
calls into — and it has to run before the scene file is read, which it did not.

### D2. One evaluator, two hosts, and the seam is a test

This is the design decision the rest hangs off.

**The water surface height at a position is defined once**, as arithmetic over: the body's base
height, the sum of the wave spectrum's Gerstner terms at that position and time, attenuated by the
local depth, plus the ripple field's displacement if a simulation covers the position. That
arithmetic exists in exactly two places — a Raven function in `Raven/Library/Water/Surface.rvn` and a
C# method in `Vixen.Water` — and **the two are held together by a seam test with a stated tolerance
of exact-to-the-float**, the same instrument [19 § L4](19-lighting-and-global-illumination.md) used
for the surface cache and [31 § D8](31-terrain-grass-and-trees.md#d8-grass-is-derived-trees-are-stored-and-the-distinction-is-the-density)
used for the grass scatter.

⚠ **Why this is worth a test rather than a convention.** Both references evaluate the surface twice
and neither pins them together, and the symptoms are the reason people believe water is hard: a boat
that hovers a hand's width above the crests in a swell, a character whose swimming state flickers at
the shoreline, a buoy that sinks when the frame rate drops. Unreal's `Max Wave Height Offset` is a
manual correction for precisely this drift, exposed as a per-body property — a knob whose existence
is a bug report.

Three consequences follow, and each is load-bearing:

- **Time is a water time, not a world time.** The evaluator takes an explicit `waterTime`, and the
  fixed-step simulation and the render both pass a value derived from the same clock — the render
  interpolating within the step. A buoyancy solver reading `GameTime.Total` and a shader reading a
  smoothed frame time is the drift, and it is invisible until the frame rate changes.
- **The evaluator is jobbable and allocation-free**, because a hundred pontoons and forty swimming
  characters query it per fixed step.
- **The wave sum is bounded at compile time** — [D7](#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic)
  quantises the wave count so the loop is the same shape on both sides. A dynamic loop on the CPU
  and an unrolled one on the GPU is how two implementations of one function start to differ in the
  last bits.

### D3. The water info texture is the interchange, and it is a *zone* render

One `R32G32B32A32` (or half-precision, per zone) render target per water zone, rendered top-down,
carrying **water surface height, flow velocity in two channels, and the ground height beneath**.
Depth is `surface − ground`, computed where it is used rather than stored, because storing it is a
third number that can disagree with the two it came from.

This is Unreal's structure, taken deliberately and with one correction. Taken, because it is right:
every consumer wants a *field* over the ground plane, not a set of bodies — the material wants depth
for its colour and its shoreline, the vertex stage wants attenuation, the foam wants the gradient,
the ripple simulation wants a boundary, and none of them wants to iterate bodies per pixel. The zone
is the render unit because transitions between bodies must be resolved *once*, by rasterising them
in priority order into one texture, rather than being blended per pixel by a material that has to
know which two bodies it is between.

The corrections:

- **The terrain writes into it as a first-class producer, not through a component somebody remembers
  to attach.** Unreal's `UWaterTerrainComponent` is opt-in per actor, which is why "my water has no
  depth" is a common question with a non-obvious answer. Here the zone's ground channel is filled by
  the terrain renderer for every terrain overlapping the zone, and a *mesh* contributes only if it
  carries an explicit `WaterOccluder` — the opposite default, because a terrain always wants to be
  the riverbed and a lamp post never does.
- **The zone is always a sliding window.** Unreal made local tessellation a mode in 5.3 because the
  fixed-extent version does not survive an open world; there is no reason to build the version that
  does not survive first. The window is centred on the view, snapped to a texel so it does not
  shimmer, and re-rendered only when it has scrolled past a threshold or a body has changed.
- **The resolution is derived and stated, not typed.** The zone panel shows metres per texel and the
  memory, in the style of [31](31-terrain-grass-and-trees.md)'s create dialog. A number an author
  types into `render_target_resolution` with no idea what it buys is how the reference gets
  configured wrongly.

⚠ **The snap must be to a texel of the *coarsest* thing that reads the texture.** Snapping to the
window's own texel grid is not enough once the ripple simulation samples it at a different
resolution; the two grids beat against each other and produce a crawl along the shoreline that
appears only while the camera moves. Same class of bug as
[31 § D3](31-terrain-grass-and-trees.md#d3-a-quadtree-with-a-morph-not-a-clipmap)'s morph, and it
gets the same treatment: a test on the arithmetic, before the renderer.

### D4. The surface is the terrain's quadtree with a different height source

Unreal has two quadtrees — Landscape's components and `FWaterQuadTree` — with two LOD schemes, two
morph implementations and two sets of debug commands. They exist separately for a historical reason
and they solve the same problem.

Vixen has one, and it is already the right one.
[31 § D3](31-terrain-grass-and-trees.md#d3-a-quadtree-with-a-morph-not-a-clipmap) selects CDLOD nodes
on the CPU, uploads one instance record each, culls them on the GPU and draws one indirect instanced
call of a shared 33×33 patch, with the vertex stage morphing each vertex toward its parent's position
across the outer part of the node's range. **Every word of that is true of water**, and the only
difference is what the vertex stage samples for height: the terrain samples its heightmap mip chain,
water samples the info texture and adds the wave sum.

So `TerrainLod`'s node selection is extracted to a `PatchSelector` parameterised by an extent, a
height range and an LOD scale, with two consumers. What water adds on top:

| Water needs | How |
|---|---|
| Tiles only where there is water | The selector takes a coverage predicate — a coarse mip of the info texture's alpha, tested per node during descent |
| A far mesh to the horizon | One more node level past the selector's range, drawn with a permutation that skips the wave sum and the flow |
| Density where the waves are, not where the ground is | The LOD scale is per zone, and the node's error metric includes the spectrum's maximum amplitude rather than only the height range |
| Transitions between bodies | Resolved in the info texture ([D3](#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render)), so a tile has one material |

⚠ **The morph must be computed from the node's own range, exactly as in terrain**, and the
no-crack test transfers unchanged. Water makes it worse, not better: a crack in a terrain shows a
sliver of skybox for a frame, and a crack in a flat specular surface shows a bright line that reads
as a rendering artefact from four hundred metres.

**One draw call for all the water in a zone**, sharing the terrain's patch vertex buffer.

### D5. Carving is a reserved edit layer, and the machinery exists

A `Water` reserved layer on the terrain, alongside Splines and Scatter, regenerated wholesale
whenever a body changes ([B4](#b4-the-terrains-reserved-layers-are-a-closed-set)). Each body writes a
height delta and, optionally, layer weights — a riverbed of gravel, a lake floor of silt — through
the same `TerrainSpline` profile machinery that already deforms ground under a road.

The profile a body carves is: a channel of a given depth, a ramp of a given width from the channel to
the shoreline, and a falloff outward into the surrounding ground. That is Unreal's `Channel Depth` /
`Curve Ramp Width` / `Falloff Width` in different words, and the elevation curve asset is the same
`CurveAsset` the animation and grading systems already use.

What is *not* taken from the reference: the terracing, the two octaves of curl and the shape blur.
They are a procedural shoreline generator living inside a water body, they are the reason that
property list has twenty entries, and every one of them is expressible as a terrain brush the author
runs on a layer above. ⚠ **The gate to check that decision against is whether a shoreline sculpted by
hand survives a body being moved** — it does, because it is in a different layer, which is
[31 § D4](31-terrain-grass-and-trees.md#d4-edit-layers-are-the-storage-model-not-a-feature-on-top-of-it)'s
whole promise. If it did not, the procedural version would be mandatory.

**Islands are the same mechanism with the sign flipped**, and are a body kind rather than a second
actor type — Unreal's `Invert Shape` property, promoted to the thing it actually is.

### D6. A water body is a spline and a profile, and there is no new spline

The two references disagree here and Unreal is right, for a reason Unity's own documentation
demonstrates: a current map is a texture somebody has to author in an external tool, and Unity's
river sample scene needs one. A spline with a velocity per control point is authored in the viewport
by dragging, it is diffable, and it is the same object a road is.

So: **a water body is an entity with a `WaterBody` component**, naming a `.vxspline`, a body kind, a
material and a wave asset, and carrying a profile whose per-control-point channels are **width,
depth, velocity and audio intensity** — Unreal's four, because they are the right four and the fourth
is the one everybody forgets until the river is silent.

⚠ **No new asset kind for the body, and this diverges from terrain deliberately.**
[31 § D2](31-terrain-grass-and-trees.md#d2-the-terrain-is-an-asset-and-the-tile-is-the-unit) earned a
`.vxterrain` because a heightfield is tens of megabytes of binary and merging it is not a thing. A
water body is a spline reference and eleven numbers; putting it in a sidecar asset would mean a lake
that cannot be moved without opening a second document, for no merge benefit at all. **The rule the
two cases share is *put it where the merge is*** — and it produces opposite answers, which is how you
can tell it is a rule rather than a preference.

One new asset kind only: `.vxwaves`
([D7](#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic)), because
a sea state is shared between every body in a region and between levels.

A **custom body** is a mesh instead of a spline, for a swimming pool, an aqueduct or a ship's hold —
it still carries a spline for querying, as Unreal's does, because the query API must not have two
shapes. An **exclusion volume** is a box or a shape that removes water from a region, so a cave under
a lake is dry.

### D7. Waves are a spectrum summed as Gerstner, and the FFT is deferred with arithmetic

A `.vxwaves` asset holds a spectrum — a wind direction, a wind speed, a directional spread, a
wavelength range with a falloff, an amplitude scale, a steepness and a seed — and *generates* a list
of Gerstner waves from it. The list is the runtime form; the spectrum is what an author edits. That
split is Unreal's simple-versus-spectrum generator pair collapsed into one thing, because the simple
generator is a spectrum with a flat distribution and shipping both is shipping two mental models.

**Attenuation by depth is in the evaluator, not the material.** A wave whose amplitude does not fall
off as the ground rises produces a crest that intersects the beach, and every project that hits it
solves it in a material and then discovers buoyancy still uses the unattenuated height. It belongs
where [D2](#d2-one-evaluator-two-hosts-and-the-seam-is-a-test) puts everything else.

**Why not FFT first.** An FFT ocean is unquestionably better at open sea, and the arithmetic that
decides the order is this: a Gerstner sum of 16 waves is 16 sin/cos pairs per vertex, evaluates
identically on the CPU and the GPU, needs no textures, no per-frame dispatch, and — the deciding
property — **is a closed-form function of position and time**, so a server can answer *where is the
surface at t* without having simulated the intervening frames. An FFT needs a per-frame dispatch
chain, three cascaded bands of textures, a CPU readback for buoyancy, and it is *not* closed-form:
the CPU path has to either read back a GPU texture or run a second FFT. That is
[D2](#d2-one-evaluator-two-hosts-and-the-seam-is-a-test)'s seam broken by construction, and it is why
the order is Gerstner first.

The wave count is the permutation axis, **quantised to 8 / 16 / 32** so a sea state gaining a wave
does not compile a shader — [31 § D6](31-terrain-grass-and-trees.md#d6-the-splat-material-is-generated-from-the-layer-list)'s
trick, unchanged.

When FFT lands it is a second `IWaveModel` behind the same evaluator interface, with the CPU host
running the same inverse transform on a job — and the seam test is what makes it safe to add.

### D8. The surface is a pass between lighting and translucency, and its reflections are L5's

The surface shading model is `SingleLayerWater`'s idea and it is the right one: **integrate
absorption and scattering over the path length from the surface to whatever is behind it**, using the
depth buffer for that length, and composite once. Inputs: scattering coefficients, absorption
coefficients, a phase `g`, and a scale on what is behind the water. It is a `ShadingModels.rvn`
implementation like the other eight, and the pass is what supplies it with the scene behind.

The pass runs after deferred lighting and before translucency, reading a copy of `SceneColour` and
the depth buffer ([B1](#b1-there-is-no-shading-model-that-can-read-the-scene-behind-it)) and writing
back into `SceneColour`. It is a `!Water` node in a `.vxcompositor`, which means a project that has
no water does not pay for the copy.

⚠ **The reflections come from [19 § L5](19-lighting-and-global-illumination.md), not from SSR, and
this is a routing decision worth stating.** Unreal's water pass classifies tiles specifically to run
an indirect SSR draw over them, because SSR is what it has. Vixen's SSR is ⬜ and its **traced
reflections are ✅** — "a mirror ray marches L1's tracer" over the global distance field. For water
that is strictly better in the case that matters: a lake reflecting a mountain that is off-screen,
which is the single most common reflection failure in every screen-space implementation, and which is
what makes water look like a mirror bolted to the ground. So water is `Vixen.Rendering.Reflections`'
second consumer, and **SSR staying unbuilt does not block it**.

Tile classification is still worth doing, for its actual reason: the water pass is expensive per
pixel and covers a small fraction of most frames. The classification is over the GBuffer's shading
model id, which already exists.

### D9. Underwater is a post-process volume, and the waterline is named as the hard part

[32](32-post-process-volumes.md) built optional-field settings, a priority, a blend radius and a
per-frame fold over the camera's position. Underwater is a volume whose shape is *the body, below its
surface* ([B2](#b2-doc-32s-volumes-are-boxes)), whose blend radius is the fade as you approach it, and
whose settings are the usual fog, grade and vignette. **That is the whole feature**, and it costs the
shape generalisation and nothing else.

Two things it does not cover, and both are stated rather than absorbed:

- **Distortion and the caustic wobble are compositor nodes**, driven by a submersion depth the fold
  already computes. They are not lens properties — [32](32-post-process-volumes.md)'s boundary holds:
  the volume describes a place, the lens belongs to the camera, and being underwater changes the
  medium rather than the aperture.
- ⚠ **The waterline — the camera straddling the surface — is a separate problem and is not solved by
  a volume.** A volume's fold produces one weight for the frame, and a half-submerged camera needs
  two treatments in one frame divided by a curve that is the intersection of the wave surface with
  the near plane. Unity generates a water line mesh for this; Unreal handles it in the underwater
  material with the surface geometry itself. The answer here is a screen-space mask written by the
  water pass — it already knows, per pixel, whether the surface is in front of or behind the camera —
  and the underwater composite reads that mask instead of a scalar. **It is called out because
  designing the volume path first and discovering the waterline second is how you get a system where
  the transition is a hard cut and the fix is architectural.**

### D10. Buoyancy is pontoons over Jolt, evaluated at the fixed step's water time

`BuoyancyBody` is a component holding a list of pontoons — a radius and an offset each — a buoyancy
coefficient, first- and second-order damping, a maximum force, and drag coefficients. Per fixed step,
per pontoon: query the evaluator for surface height, normal and flow; compute the submerged fraction
of the sphere; apply an upward force at the pontoon's world position, a drag opposing relative
velocity, and a lateral force from the flow. Unreal's model, and it is the right one — pontoons are
a volumetric approximation cheap enough to run on every crate in a river.

Three things it does that the reference does not:

- ⚠ **It runs at the fixed step and reads the *simulation's* water time**, never a frame time. A
  buoyancy force computed from an interpolated render-time surface is a force that changes when the
  frame rate does, and in a networked game that is a client and a server disagreeing about where a
  boat is. This is [16](16-networking.md)'s determinism requirement applied to a force, and it is
  why [D2](#d2-one-evaluator-two-hosts-and-the-seam-is-a-test)'s explicit `waterTime` parameter is
  not a stylistic choice.
- **It is replicated as a predicted body**, on `Vixen.Net.Physics`' existing rigid-body path — which
  works only because the surface is closed-form ([D7](#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic)):
  a rollback re-simulating six ticks needs the surface at six past times, and an FFT ocean cannot
  answer that without having kept six frames of textures.
- **A pontoon's forces are zero-allocation and jobbed per body**, because the answer to "how many
  floating crates" should be "as many as you like".

⚠ **Jolt has a buoyancy impulse of its own and it is deliberately not used.** It takes a plane, which
is exactly the approximation a wave surface is not, and using it would put a second definition of the
water surface inside the physics engine where the seam test cannot reach it.

### D11. Swimming is a fourth move mode, and immersion is the only new number

`CharacterMoveMode` gains `Swimming`, closing [29](29-players-and-possession.md)'s row.
`CharacterState` gains an immersion depth — the fraction of the capsule below the surface — which is
the only new state, and every rule is a threshold on it:

| Immersion | Behaviour |
|---|---|
| Below the wading threshold | Walking, with a speed multiplier from the depth |
| Above the swim threshold | `Swimming`: buoyancy holds the capsule near the surface, gravity is replaced by a restoring force, control is three-dimensional |
| At the surface, moving toward land | Exit if there is ground within a step height — the same probe the walk mode already does |

`MoveIntent` is unchanged and this is the point: [29](29-players-and-possession.md) made it "the one
seam between input, physics and the wire", and a swimming character produces the same intent a
walking one does. Diving is the existing vertical axis. **Nothing about the network path changes**,
which is what a good seam is for.

⚠ **The threshold must have hysteresis, and the reason is the waves.** A character standing in
chest-deep water with a 30 cm swell crosses any single threshold twice a second, and the symptom is
an animation state machine that stutters between wade and swim. Two thresholds with a gap, and the
gap is at least the local wave amplitude the evaluator already reports.

Wading, swimming and diving are three facets in [34](34-move-sets-and-pose-constraints.md)'s
catalogue, not three graphs — which is that document's claim, and water is a good test of it.

### D12. Ripples are a sliding-window height field, and they are displacement, not geometry

A single height field in a window around the view — a texture of surface displacement and its
velocity, advanced by a wave-equation step per frame, with damping toward zero at the window's edge
so nothing reflects off the boundary. Entities inject into it: a character wading writes a moving
depression, a boat writes a hull-shaped one, an impact writes an impulse. The result is added by the
evaluator ([D2](#d2-one-evaluator-two-hosts-and-the-seam-is-a-test)) — so a second boat *rides* the
first one's wake, and the buoyancy solver feels it.

The window scrolls and is snapped to a texel, with the same warning as
[D3](#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render). The simulation is
bounded: a fixed resolution, a fixed step, and an explicit injection budget per frame, because an
unbounded number of sources is how this feature becomes a frame-time cliff in a busy scene.

⚠ **The CPU side reads back one frame late, and that is a stated behaviour rather than a bug to
chase.** Buoyancy sampling a GPU texture cannot see the current frame's step without a stall. So the
simulation is *also* run on the CPU at a lower resolution over the same injections — a kernel small
enough to be a job, deterministic, and compared against the GPU version in the seam test at a
**stated tolerance that is not exact**, unlike everything else in this document. That asymmetry is
deliberate and it is why the ripple contribution is separated from the wave sum in the evaluator's
signature: the closed-form part is exact and the simulated part is not, and a caller that needs the
exact one — the network path — asks for it alone.

### D13. Residency rides `PageResidency`, and there is very little to stream

The info texture is a window, the ripple field is a window, and the wave spectrum is a few hundred
bytes. The only water data with a footprint is the surface *material's* textures, which are ordinary
material textures. So water is the cheapest possible customer of
[22 § improvement 6](22-virtualized-geometry.md)'s one-residency-manager promise, and it is included
here only to record that it does not need a second mechanism.

---

## Part 2 — The authoring surface

### One mode, not two

[31](31-terrain-grass-and-trees.md) needed a sculpt mode and a foliage mode because each owns the
viewport and has an incompatible idea of what a click means. Water needs **one**, `WaterMode`, and
most of the time it does not need that either: placing a lake is placing an entity, and editing its
shape is editing a spline.

> ⚠ **This section used to end "with the tools `SplineEdit` already provides", and it does not
> provide any.** `SplineEdit` and `SplineCommand` are instantiated by nothing outside their own tests,
> so no author can move a control point in any viewport —
> [#118](https://github.com/Rikarin/Vixen/issues/118). The scope argument stands (shape editing is not
> water's job); the capability it defers to has not been built.

The mode exists for the three things that are not entity editing:

| Verb | What it is |
|---|---|
| **Draw a body** | Click a series of points on the ground to lay a spline at the terrain's height, closing it for a lake or an ocean and leaving it open for a river. The gesture that stays this mode's even once #118 is fixed, because laying points *at the terrain's height* is a question about the terrain rather than about the curve |
| **Edit the profile** | Drag width handles on each side of a river, drag a depth handle down, and see the velocity arrows — Unreal's three viewport visualisations, and they are the reason its river authoring is good |
| **Preview the carve** | Toggle the reserved layer's contribution on and off, so an author can see what the water did to the ground |

### The zone panel

One water zone per region, and the panel is the place the derived numbers live, in the style of
[31](31-terrain-grass-and-trees.md)'s create dialog:

- The window extent in metres, and beneath it **metres per texel** and **megabytes**, updating as the
  resolution changes. A resolution is meaningless and a metre per texel is not.
- The LOD scale and tessellation factor, with the vertex count for a full window beside them.
- The wave asset, with its maximum amplitude shown — because that number decides the node error
  metric, the far-mesh cut and the collision bounds, and an author raising it from 0.5 m to 4 m
  should see what it costs before the frame time does.
- Half-precision on or off, with the height range it can represent stated in metres. ⚠ A half float
  over a 20 km zone is a *quantised* surface, and the panel says so rather than leaving somebody to
  discover a stepped horizon.

### The body inspector

Kind, spline, material, wave asset, and then the profile — width, depth, velocity, audio intensity —
each editable as a default and per control point. Then the terrain group (carve on/off, ramp width,
falloff, target layer), the underwater group (a [32](32-post-process-volumes.md) settings block,
verbatim), and the physics group (surface flow strength, whether the body is swimmable).

### What the scene sees

A `WaterZone` component and a `WaterBody` component per body, both `[DataContract]`, both plain
entities in the hierarchy with transforms. A body is duplicated, prefabbed and moved like anything
else, and a river copied into a second level takes its spline with it.

### Debugging

Unreal's debug surface is the best part of its water system and it is copied deliberately, as console
commands on the existing `[ConsoleCommand]` registry and overlays on the existing
`IDiagnosticOverlay`:

| Command | What it draws |
|---|---|
| `water.showTiles` | Patch bounds, **coloured by body kind**, with the LOD level as a label |
| `water.showLod` | The morph bands as rings, which is where a pop is diagnosed |
| `water.showInfo` | The info texture's four channels as an overlay, individually |
| `water.showFlow` | Flow vectors on the surface, and the spline velocity arrows |
| `water.showBuoyancy` | Pontoons, their submerged fraction, and the forces as arrows |
| `water.showRipples` | The simulation window's bounds and the current injection count against the budget |
| `stat water` | Query counts and costs: surface evaluations, immersion tests, ripple injections |
| `stat watermesh` | Nodes selected, nodes culled, vertices, draws |

---

## Part 3 — Phases

13 EM. The cut line is after W6.

### W0 — Unblockers · 1.0 EM

[B2](#b2-doc-32s-volumes-are-boxes) (a shape interface for post-process volumes, with a box and a
sphere implementation), [B4](#b4-the-terrains-reserved-layers-are-a-closed-set) (a third reserved
terrain layer), [B5](#b5-there-is-no-ping-pong-compute-target-helper) (a ping-pong target pair in the
render graph), and the scene-colour copy [B1](#b1-there-is-no-shading-model-that-can-read-the-scene-behind-it)
needs. Each has a consumer outside water, and each is written that way.

**Exit:** a sphere-shaped post-process volume in a sample; a reserved layer that regenerates from a
test fixture; a two-target compute rotation with a render-graph dependency asserted.

### W1 — The kernel and the evaluator · 2.0 EM

`Vixen.Water`: body kinds, the spline profile, shape rasterisation into a height/flow/coverage field,
the wave spectrum and its Gerstner generation, the evaluator, and the surface query
(`WaterQuery.Sample(position, waterTime) → height, normal, flow, depth, immersion`). No device, no
world, no renderer.

**Exit:** a river spline with per-point width, depth and velocity produces a field whose values at
sampled positions match hand-computed expectations; the evaluator is allocation-free over ten
thousand queries; the spectrum is deterministic from its seed on three operating systems.

### W2 — The water pass and the shading model · 2.0 EM

The `SingleLayerWater`-shaped shading model in `ShadingModels.rvn`, the `!Water` compositor node, the
scene-colour read, the depth-based transmittance, the tile classification, and reflections routed to
[19 § L5](19-lighting-and-global-illumination.md).

**Exit:** a flat quad of water over a textured floor, absorbing and scattering with depth, reflecting
an off-screen object correctly, in a golden image — and the **transmission / refraction** row in
[overview § 1.9](../overview.md) closes with it.

### W3 — The info texture and the zone · 1.5 EM

The zone component, the sliding window with its texel snap, the top-down render of bodies in priority
order, the terrain's ground channel, the velocity blur finalise, and the derived readout in the panel.

**Exit:** two bodies overlapping produce one continuous depth field with no seam at the boundary; the
window scrolls across 2 km with the shoreline stable to the pixel in a recorded flythrough.

### W4 — The surface mesh · 2.0 EM

`PatchSelector` extracted from `TerrainLod` with two consumers, the coverage predicate, the water
vertex stage, the far mesh, and the wave displacement.

**Exit:** an ocean to the horizon in one draw call, with the no-crack test green at every level
boundary and the morph-continuity test green across a flythrough — both transferred from
[31 § Part 4](31-terrain-grass-and-trees.md#part-4--testing), both written before the renderer.

### W5 — Carving and the shoreline · 1.0 EM

The reserved Water layer, the channel/ramp/falloff profile, layer painting along the bed, and the
preview toggle.

**Exit:** a river laid across a sculpted terrain cuts a bed and a bank, non-destructively; moving the
river restores the old ground and cuts the new; a hand-sculpted shoreline in a layer above survives
both.

### W6 — Underwater and swimming · 1.5 EM

The volume shape, the underwater composite, the waterline mask, `CharacterMoveMode.Swimming`, the
immersion depth and its hysteresis.

**Exit:** a character walks into a lake, wades, swims, dives and climbs out; the camera crossing the
surface shows a waterline rather than a cut; [29](29-players-and-possession.md)'s row closes.

**This is the cut line.** W0–W6 is water an artist builds a level with and a character swims in.

### W7 — Buoyancy · 1.5 EM

Pontoons, the solver, the flow forces, the shore push, the debug draw, and the networked predicted
body.

**Exit:** a crate floats in a swell without hovering or sinking, measured against the evaluator to a
stated tolerance; a raft in a river reaches the mouth; a client and a server simulating the same raft
report a misprediction count of zero over a thousand ticks.

### W8 — Ripples and interaction · 1.5 EM

The height-field simulation, the injection API, the CPU reference, the seam test, the wake and splash
hooks for `Vixen.Vfx`.

**Exit:** a boat leaves a wake a second boat rides; the CPU and GPU fields agree within the stated
tolerance; the injection budget is asserted and the overflow is reported rather than dropped
silently.

### W9 — The editor mode and the panels · 1.0 EM

`WaterMode`, the draw gesture, the profile handles, the velocity visualisation, the zone panel, the
body inspector, the console commands and the overlays.

**Exit:** a lake, a river flowing into it and an ocean beyond, all authored in the viewport with no
text editing — and [31](31-terrain-grass-and-trees.md)'s "built and not yet reachable" failure mode
tested for explicitly, by a session test that saves the scene, reopens it and finds the tools bound.

⚠ **Three things this phase turned out to be, which the paragraph above does not say.** The first is
that the *wiring* is most of it: a mode nothing registers and a node no host hands a zone system to
are both "built and not yet reachable", and closing that meant `AppGraphics` growing a
`WaterZoneSystem` on exactly the terrain factory's terms. The second is that **the water clock had to
become one number** — the surface node held its own and the zone system held another, which is a
vertex stage a frame ahead of a buoyancy solver and therefore a boat that hovers. The third is that
**the UI layer has no double click**, so "click the first point again" is how a closed body is
finished; `PointerAction` is moves, presses and releases, and a click count is a fact about time the
event does not carry.

⚠ **The fourth thing, which is the one this phase kept getting wrong: the viewport drew none of it.**
`ScenePresenter` had no occurrence of the word "water" in it. The gesture wrote a real `.vxspline` and
created a real `WaterBodyComponent`, and an author saw the same dry ground they had before — doc 31's
"built and not yet reachable" arriving through the one door this phase's exit criterion did not check,
because a session test can find the tools bound and say nothing about what is on screen. It is closed
the way the vegetation one was: an `IWaterScene` the module contributes, a `WaterPresenter` the host
hands it to, and **`WaterZoneSystem.Fold(World)` rather than a second fold** — § D2 is a rule about
hosts and the editor is one. The surface is CPU-evaluated through the same `WaterQuery` the vertex
stage samples, which is the opposite call from the grass and is right for the opposite reason: a
closed-form surface has an honest CPU preview and a hundred thousand blades do not.

⚠ **And the scene could not be opened at all.** `EditorApplication` touches its subsystems' assemblies
so their `[ModuleInitializer]`s run — and did it a hundred lines *after* the scene file was read, for
the Add Component menu's benefit. A `Main.vxscene` naming a `!WaterZoneComponent` therefore died on
the way up with "Nothing in this build claims the name". The touch happens before the read now, and
the list has the ground, the water and the buoyancy join in it.

⚠ **The six `water.show*` verbs were "flags with nothing behind them", and that has been out of date
in both directions.** `WaterDebugDraw` is fully built and was instantiated only by its own tests: the
dangling link moved one hop, from a flag with no drawing to a drawing with no host, and did not close.
Both ends are closed here. `WaterDebug.Register(ConsoleCommands)` registers the six by name without
the reflection `RegisterFrom(Assembly)` needs — that overload is annotated `RequiresUnreferencedCode`
and has no callers anywhere, so the attributes alone were never a route. And `WaterModule` registers
the same six as editor commands under the same names, with the pane draining `DebugDraw`'s world lines
into its depth-tested line pass, so **`water.showFlow` draws flow arrows and spline velocities on a
river.** Tiles, LOD bands and ripples stay inert in a pane, because a preview surface is a CPU grid
with no device patches and no ripple simulation; `showInfo`'s charts are screen-space and a pane has
no screen-space debug pass.

⚠ **`stat water` and `stat watermesh` are built and still have no host, and that is doc 13's rather
than water's.** Nothing in the tree constructs a `DiagnosticOverlays`, a `ConsoleCommands` or a
`DebugDraw` outside its own tests, and no compositor node draws a frame's `DebugDraw` — so
`FrameStatsOverlay`, `LogOverlay`, `ConsoleOverlay`, `FrameGraphOverlay`, `AudioOverlay` and
`PhysicsDebugDrawSystem` are all exactly as unreachable as water's two. Water's `[ConsoleCommand]`s
happen to be the only ones in the tree, so "the console verbs cannot be typed" and "there is no
console" are the same sentence.

### Cost

| Phase | EM | Cumulative |
|---|---|---|
| W0 Unblockers | 1.0 | 1.0 |
| W1 Kernel and evaluator | 2.0 | 3.0 |
| W2 Pass and shading model | 2.0 | 5.0 |
| W3 Info texture and zone | 1.5 | 6.5 |
| W4 Surface mesh | 2.0 | 8.5 |
| W5 Carving | 1.0 | 9.5 |
| W6 Underwater and swimming | 1.5 | 11.0 |
| W7 Buoyancy | 1.5 | 12.5 |
| W8 Ripples | 1.5 | 14.0 |
| W9 Editor mode | 1.0 | 15.0 |

⚠ **15.0, and the 13 above was the cut-line number.** W0–W6 is 11 EM and is a complete, usable water
system; W7–W9 are 4 EM of things a project notices the absence of on its second week. The ordering is
deliberate: W2 before W3 and W4 means the *look* is proven on a flat quad before any of the meshing
or the zone machinery exists, which is the opposite of the order the feature list suggests and is how
you avoid building a beautiful quadtree for a surface that turns out to shade wrongly.

---

## Improvements over the references

### 1. One evaluator, and the seam is a test

[D2](#d2-one-evaluator-two-hosts-and-the-seam-is-a-test). Both references evaluate the water surface
twice and neither pins the two together; Unreal ships a per-body offset property to correct the
drift. Here it is one definition, two hosts, and a test at a tolerance of exact — the instrument this
repository has already used twice for exactly this class of problem.

### 2. One quadtree, not two

[D4](#d4-the-surface-is-the-terrains-quadtree-with-a-different-height-source). Unreal has a landscape
LOD system and a water LOD system, with two morphs, two sets of bias cvars and two ways to get a
crack. One `PatchSelector` with two height sources means the no-crack test, the morph-continuity
test and the debug overlay are all written once.

### 3. Reflections that see off-screen

[D8](#d8-the-surface-is-a-pass-between-lighting-and-translucency-and-its-reflections-are-l5s). The
reference's water pass exists partly to run SSR efficiently over water tiles. Vixen's traced
reflections already march a global distance field, so a lake reflects a mountain that is behind the
camera — the failure that makes screen-space water read as wrong, avoided by routing rather than by
building anything.

### 4. Carving on machinery that already existed

[D5](#d5-carving-is-a-reserved-edit-layer-and-the-machinery-exists). Unreal's water brush requires
opting the landscape into edit layers, five years after Landscape shipped, which is why it is a
setup step people miss. Here non-destructive layers *are* the storage model, and water is the third
consumer of a contract designed without it in mind.

### 5. Underwater costs a shape, not a system

[D9](#d9-underwater-is-a-post-process-volume-and-the-waterline-is-named-as-the-hard-part).
[32](32-post-process-volumes.md) built the priority, the blend radius and the optional fields; water
needs a non-box containment test and nothing else — and the generalisation makes doc 32 better for
every other volume.

### 6. Determinism is a design input, not a retrofit

[D10](#d10-buoyancy-is-pontoons-over-jolt-evaluated-at-the-fixed-steps-water-time). Buoyancy reads
the simulation's water time, the surface is closed-form so a rollback can ask for six past states,
and the wave model was chosen partly for that property. Neither reference's water survives being
rolled back, and it is the reason both are used for scenery more often than for gameplay.

### 7. The debug surface is copied on purpose

Unreal's `stat water`, `stat watermesh` and the colour-coded tile bounds are better than most
first-party debug tooling in any engine, and there is no credit in inventing worse ones. They are in
[Part 2](#debugging) as a deliverable of W9 rather than as something added after the first bug.

### 8. The units are metres and the panel says so

[31](31-terrain-grass-and-trees.md)'s derived-readout habit, applied to the three numbers that
actually decide water's cost: metres per texel, vertices per window, and the wave amplitude that sets
the error metric.

---

## What is deliberately not built

| Not built | Why, and what it would take |
|---|---|
| **An FFT ocean** | Deferred with arithmetic in [D7](#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic), not rejected. It lands as a second `IWaveModel`, and the seam test is what makes adding it safe. The blocker is not the transform, it is a CPU path that can answer *where was the surface six ticks ago* |
| **Volumetric underwater — god rays, participating media** | It is [19](19-lighting-and-global-illumination.md)'s volumetric fog with a different phase function, and volumetric fog is ⬜. When it lands, underwater is a preset for it rather than a second implementation |
| **Caustics** | The reference ships them as a content example — a projected texture animated by the surface normal — and that is a material and a decal, both of which exist. Nothing in this design forecloses the real version, which is a photon gather and is a research project |
| **Wet surfaces** | Puddles, darkening at the tide line, droplets on the lens. A material feature and a screen effect, neither of them water's, and folding them in here is how a water system becomes a weather system |
| **Water that can be poured, drained or dammed** | A body whose *shape* is simulated rather than authored. Every cheap thing above assumes the surface is a function of two variables plus time; this breaks that, and it is a game mechanic with a bespoke budget |
| **Splashes, spray and breaking surf** | `Vixen.Vfx` emitters triggered by the impacts the buoyancy solver and the ripple injections already detect. Owed rather than cut — [W8](#w8--ripples-and-interaction--15-em) leaves the hooks |
| **Boat and swimmer physics as a library** | [28 § Vixen.Gameplay.Movement](28-gameplay-framework.md)'s, explicitly. This document ships the forces; a vehicle that steers with them is a gameplay library |
| **A river network or watershed generator** | A content-generation product, and it fails [the left-column test](#where-the-line-goes) |
| **Water at planetary scale** | Curvature, and doc 03's coordinates are single-precision. It is the same conversation as large-world terrain and belongs there |
| **Per-body flood height** | Unreal's ocean flood height, which raises the sea globally for a scripted event. One number on the zone, trivially addable, and left out until something asks — a feature that exists because one game needed it is how a plugin's property list gets to two hundred |

---

## Part 4 — Testing

The kernel is pure functions, so most of this needs no device — the bargain
[31 § Part 4](31-terrain-grass-and-trees.md#part-4--testing) and [24 § Part 4](24-blockout-tools.md)
both make.

| Level | Mechanism |
|---|---|
| **The seam** | The evaluator's C# and Raven forms, over randomised spectra, positions and times, compared bit-for-bit. **Stated tolerance: exact.** This is the highest-value test in the document and it is written in W1, before there is a renderer to see it fail in |
| **Closed form** | The evaluator at time `t` after simulating to `t` equals the evaluator at `t` evaluated cold. It is what makes rollback possible and it is one assertion |
| **Determinism across hosts** | A spectrum generated from a seed produces identical waves on three operating systems and two architectures, on the existing bit-exact CI legs |
| **No cracks** | [31](31-terrain-grass-and-trees.md)'s test, transferred: the morph evaluated at every vertex of a boundary between two levels must produce identical world positions. Pure arithmetic, no device |
| **Morph continuity** | Vertex position as a function of camera distance is continuous across a level transition, densely sampled — the pop that a screenshot will not catch |
| **Texel snap** | The window's origin quantised to the coarsest consumer's grid, asserted over a swept camera path. Catches the shoreline crawl |
| **Depth field continuity** | Two overlapping bodies rasterised in either order produce the same field, and the field has no discontinuity at the boundary greater than a stated bound |
| **Property tests** (CsCheck) | Wave amplitude attenuates monotonically as depth decreases and reaches zero at zero depth. Flow along a river is continuous in the spline parameter. Immersion is monotone in the capsule's Z. A pontoon fully submerged produces exactly the maximum force and no more |
| **Hysteresis** | A character held at the swim threshold with a randomised swell changes mode at most once per second — the stutter test, and it fails without the gap |
| **Buoyancy convergence** | A body released above the surface settles to a rest height matching the analytic displacement of its pontoons, within a stated bound, in a stated number of steps |
| **Network agreement** | A predicted raft in a swell over a thousand ticks, client and server, with a misprediction count of zero — [29](29-players-and-possession.md)'s existing instrument, pointed at a new force |
| **Ripple CPU/GPU** | Compared at a **stated non-exact tolerance**, and the asymmetry with the first row is the point — the docstring says why |
| **Round trip** | Every body and zone saves, reloads and re-saves to identical bytes |
| **Gestures** | Synthetic pointer input against the real tools, asserting the spline and the profile rather than the pixels: "dragging a width handle widens one control point and dirties one tile of the reserved layer" |
| **Budget assertions** | Nodes selected, draws, info-texture bytes and evaluator calls per frame for a 4 km² zone with three bodies. The numbers that regress silently |
| **Golden screenshots** | A lake at three camera distances, a river mouth transition, a shoreline at grazing angle (where depth quantisation shows), the underwater view, the waterline, and an off-screen reflection |

⚠ **The no-crack and seam tests are written before their renderers, for the same reason
[31](31-terrain-grass-and-trees.md) insists on it**: a crack or a drift found by eye is found at one
camera position, attributed to the wrong thing, and worked around with a fudge factor that then lives
forever. Unreal's `Max Wave Height Offset` is what that fudge factor looks like after five years.

---

## Risks

| Risk | Mitigation |
|---|---|
| **The seam test is stricter than the hardware** | Exact float agreement between a C# evaluator and a SPIR-V one is a real claim, and `sin`/`cos` precision is not guaranteed identical across devices. The mitigation is structural: the evaluator's trigonometry goes through a shared polynomial rather than the intrinsic, which is a decision to make in W1 rather than discover in W7. ⚠ **If that turns out to be too expensive, the tolerance becomes a stated ULP bound and the test still holds** — what must not happen is the test being deleted |
| **Water touches five subsystems and could stall behind any of them** | The five dependencies are all ✅ today, and W0 is the only phase that changes code outside water's own assemblies. W2 deliberately comes before the meshing so the risky rendering question is answered first |
| **The far mesh and the horizon** | A flat surface to the horizon is where depth precision, LOD error metrics and half-float quantisation all show at once, and it is the shot every water screenshot is taken from. It is a golden image from W4, not a polish item |
| **Underwater is two features wearing one name** | The volume is cheap; the waterline is not. [D9](#d9-underwater-is-a-post-process-volume-and-the-waterline-is-named-as-the-hard-part) separates them explicitly so the second is budgeted rather than absorbed into "and then the camera goes under" |
| **The ripple simulation is an unbounded feature wearing a bounded name** | Fixed resolution, fixed step, explicit injection budget, and the overflow reported. The moment somebody proposes making the window adaptive it has failed [the left-column test](#where-the-line-goes) |
| **Carving invalidates the GI chain** | [31](31-terrain-grass-and-trees.md)'s risk, inherited exactly: a body that carves the terrain dirties distance-field bricks and surface-cache cards, and the bounce light will otherwise be from the old shape. The mechanism belongs to [19](19-lighting-and-global-illumination.md) |
| **A project uses water as scenery and never touches buoyancy** | Likely, and fine. W0–W6 is coherent on its own, and nothing in the surface path pays for the physics path |
| **Water bodies do not stream** | [B6](#b6-there-is-no-world-streaming). The honest failure mode is a 16 km² world loading every river at once. Rivers are a spline and eleven numbers, so the bytes are small; what is not small is the reserved layer's regeneration cost across a world's worth of them, and that is measured in W5 rather than assumed |

---

## Documents this changes

| Document | Change |
|---|---|
| [29 § Where this stops](29-players-and-possession.md) | **No swimming** closes. `CharacterMoveMode` gains its fourth member and the row's own condition — "it needs water volumes, which do not exist" — is met by [D11](#d11-swimming-is-a-fourth-move-mode-and-immersion-is-the-only-new-number) |
| [06 § Geometry and materials](06-rendering-pipeline.md) | A **Water** row: a compositor pass and a shading model, not a material anybody writes. And **transmission / refraction** gains the pass it was waiting for ([D8](#d8-the-surface-is-a-pass-between-lighting-and-translucency-and-its-reflections-are-l5s)) |
| [31 § D3](31-terrain-grass-and-trees.md#d3-a-quadtree-with-a-morph-not-a-clipmap) | `TerrainLod`'s node selection becomes `PatchSelector` with two consumers. The no-crack and morph-continuity tests move with it and are shared |
| [31 § D4](31-terrain-grass-and-trees.md#d4-edit-layers-are-the-storage-model-not-a-feature-on-top-of-it) | A third reserved layer, on the existing contract and with no change to it |
| [32](32-post-process-volumes.md) | Volumes gain a shape interface — box, sphere, water body — replacing the AABB containment test. The optional-field, priority and blend-radius mechanics are untouched |
| [19 § L5](19-lighting-and-global-illumination.md) | Traced reflections gain their second consumer, and the case that justifies them over SSR gets a name |
| [28 § Vixen.Gameplay.Movement](28-gameplay-framework.md) | "Swimming, flight, gliding, water craft" is unblocked — the forces and the query are here, the vehicle is there |
| [16](16-networking.md) | Buoyancy is a new predicted force, on the existing rigid-body path, and the closed-form surface is what makes rollback answerable |
| [02](02-repository-layout.md) | Three assemblies with their tests: `Core/Vixen.Water`, `Core/Vixen.Rendering.Water`, `Editor/Vixen.Editor.Water` |
| [08](08-asset-pipeline-and-addressables.md) | One asset kind and its importer: `.vxwaves`. A water body is a scene component and deliberately not an asset — [D6](#d6-a-water-body-is-a-spline-and-a-profile-and-there-is-no-new-spline) |
| [20 § A1](20-editor-parity.md#a1--the-application-frame) | `IEditorMode` gains a fifth consumer |
| [13](13-diagnostics.md) | Eight console commands and two stat groups, on the existing registry and overlay seams |
| `Raven/Library` | A `Water` package: `Surface.rvn` (the shared evaluator), `WaterShading.rvn` (the shading model), `WaterInfo.rvn` (the info-texture write), `Ripples.rvn` (the simulation step) |

Licensed under Apache-2.0.
