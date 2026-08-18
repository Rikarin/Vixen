# 13 — Third-Person Shooter

**A project, not a sample.** Every other sample here opens a device and issues draws by hand; this
one is what `vixen new` emits — a `.vxproj` the editor opens, an `Assets/` the content build imports,
and one line of `Program.cs`:

```csharp
return VixenApp.Run<ThirdPersonShooterGame>(args);
```

It exists to prove the join works end to end, which is a different claim from any one subsystem
working.

## Status

**It builds, it starts, it simulates — and it draws.** `dotnet build` performs the whole chain from
a clean checkout, and `--vixen-frames N` loads the level, builds its collision, spawns a possessed
player, walks him and renders: the last 40-frame run drew 62 objects through 41 shader variants
with zero misses, 18 material sets bound and a complete set 0, with GPU culling answering on the
device, the GI chain filling per frame, and — since the ambient split — eighteen thousand screen
probes a frame gathered into the combine and the traced reflections running beside them, over one
nearest chain rebuilt twice.

```
Compositor Assets/Frame.vxcompositor loaded.
Content mounted from /app/Content: 80 addresses.
Loaded scene 'Arena' with 66 entities.
Built 40 collider(s) from the level's authored boxes, over 60 registered shape(s).
GI wired: 240 surface card(s) over the level's boxes (0 dropped by the atlas), 2082 texel(s) captured …
Rebuilt 'Assets/Frame.vxcompositor' with the distance field, the probe field and the virtualized path in it.
Drew 62 object(s) from 11 loaded mesh(es) (0 unresolved) using 41 shader variant(s), with 0 miss(es) …
GI frame: 160 irradiance brick(s) filled, 39 cache bounce(s) recorded, culled on the device: True.
GI screen: 18230 screen probe(s) placed, gather trace: ran. Reflections: ran. The nearest chain is rebuilt 2 time(s) a frame.
VSM: 113 page(s) marked by the last serviced frame, 0 drawn this frame, 385 resident in 1024 slot(s) after 385 allocation(s).
```

That last line is doc 22 phase 7 answering: the marking pass turned the frame's own depth into 113
page requests, the residency service holds 385 drawn pages of sun shadow, and *zero drawn this
frame* is the caught-up state — every page some pixel asked for is already in the atlas, so the
frame redrew none of them, which is the entire point of a cached shadow map over a cascade that
redraws the level four times a frame.

⚠ **The first eight lines of a run are still true of a game that renders nothing**, which is exactly
how a black screen once survived a headless run that reported success. The player spawns at y = 0.2
and the floor's top is y = 0, so *`Walking` at zero* proves the character was stepped and found the
collision the level authored — and proves nothing whatever about the picture. `Arena.ReportFrame`
is what does: objects, meshes, shader variants, misses, **bound material sets** and now the GI
counters, any of which at zero is a black window or a frame lit by less than it claims.

| | |
|---|---|
| ✅ | `.vxproj`, `.csproj` on the real `Vixen.Sdk` build files, `Program.cs`, `Assets/Default.vxgroup` |
| ✅ | `Assets/Models/*.obj` — 11 generated meshes, imported with clusters, pages and distance fields |
| ✅ | `Assets/Audio/*.wav` — 6 synthesised 16-bit mono clips |
| ✅ | `Assets/Animation/*.vxanim` — 6 hand-keyed clips with footstep and fire events |
| ✅ | `Assets/Scenes/Arena.vxscene` — 31 roots: floor, walls, cover, a sun, 7 lamps and 4 spawns |
| ✅ | `Assets/Frame.vxcompositor` — doc 22's virtualized path, doc 19's GI, bloom and tonemap |
| ✅ | `Assets/Input/GameInput.vxinput` — move, look, jump, sprint, crouch, fire, aim, reload |
| ✅ | `Arena.cs` — scene load, collision from authored boxes, the fields the GI nodes need |
| ✅ | `PlayerRig.cs` — controller, character pawn, third-person camera, bindings, segmented visuals |
| ✅ | `Behaviors/` — four scripts: animation, weapon, respawn, lamp flicker |
| ✅ | The full `dotnet build` chain, a headless run, and physics that demonstrably ran |
| ✅ | **A picture.** Set 0 fills, materials bind, and the frame draws through the whole authored chain |
| ✅ | `ThirdPersonShooter.Frame.Tests/` — the frame document parsed and built against the Null device, so a YAML mistake fails a test rather than a launch |
| ✅ | **Its picture is a file.** `--vixen-headless --vixen-frames 512 --vixen-capture ./shots` renders on the real device with no window and writes the last frame as a PNG — reproducibly, and two runs at once |
| ⬜ | Wiring `--vixen-frames` into CI as a gate — no sample is run headlessly in CI today, so this is a pattern to establish rather than a row to fill in |

## The gates, the ground under your feet, and the lake

The perimeter has four eight-metre gates in it, one per wall, and the ground outside stops a
character. Both are new, and each closed a gap that was in the engine rather than in this level.

**A gate is a wall entity split in two.** `arena-wall.obj` is 64 × 6 × 1 with its base at y = 0, so
one entity carries a `scale` on x and the `!BoxCollision` beside it carries half-extents in metres,
and the two come out of one arithmetic — segment = (64 − gate) / 2, `scale.x` = segment / 64,
`halfX` = segment / 2. That is the doorway trick the houses already use, moved to the perimeter. The
two halves are authored independently and nothing in the content build compares them, so
`FrameDocumentTests.Every_gate_is_the_same_hole_in_the_mesh_and_in_the_collider` does: a hole you can
see and not walk through, or walk through and not see, is worse than no hole at all.

⚠ **The apron rose from −1.2 m to −0.2 m for the gates.** It only ever had to be "below the floor
slab and out of sight", because the perimeter was solid and nobody could reach it. A 1.2 m drop is a
one-way trip past a 1.1 m jump, so the arena would have let people out and not back in.

**The terrain has a collider, and until this sample noticed, nothing in the engine ever built one.**
Every piece existed: `PhysicsShapes.HeightField` registers the Jolt shape and
`Vixen.Physics.Tests/HeightFieldShapeTests` exercises it thoroughly;
`TerrainSamples.FillCollisionSamples` produces exactly the span that shape consumes, in metres, with
holes as the caller's sentinel; and `Editor/Vixen.Editor.Terrain/ITerrainColliders` is the seam the
sculpt tools call after every stroke. Nothing joined them. The only implementation of that interface
anywhere is a test double that records tile indices, and the only callers of `FillCollisionSamples`
were two assertions. So a terrain in a *game* had no collision, in any project, and the symptom was
not an error — a character walked off the arena floor, fell through the ground, and
`RespawnWhenBelow` put them back.

**The join has since moved into the engine**, which is where it belonged: `Vixen.Terrain.Physics`'
`TerrainColliderSystem`, opted into by this project's `.csproj` on `Vixen.Water.Physics`' terms —
doc 31 § D1 forbids both of the alternatives, since the kernel may not link Jolt and the renderer
may not link physics. It reads the renderer's own frame list, so nothing about this level is in it.
Four static bodies over 252 m of ground, and the quarry's hole is a pit you fall into because the
sentinel travels. `TerrainGroundSystem` kept the half that genuinely is this level's: telling the
lake where its bed is.

**Verified by a number rather than by a picture.** `VIXEN_SPAWN=0,4,-40,0` drops the pawn four
metres above the lake's north shelf and it finishes at **y = 1.3323056**; `TerrainSeed`'s own
arithmetic for 22 m from the lake centre is `1.6 − 4.2 × (1 − smoothstep(0, 26, 22))` = **1.332355**,
and the millimetre between them is the height field's eight-bit-per-block compression. The number is
identical before and after the move into the engine, which is what makes it a move rather than a
rewrite.

### ⚠ A character with `PhysicsInterpolation` used to walk at half speed — fixed, and this is how it was found

The level asks for `WalkSpeed = 4.5` and the player covered **2.25 m/s**. The factor was exactly two
and the mechanism was two systems writing one component:

- `PhysicsInterpolationSystem` runs in `LateUpdate` and writes `LocalTransform` to
  `Lerp(Previous, Current, alpha)`. With a frame delta equal to the fixed step — which is every
  `--vixen-capture` run and every machine holding 60 fps — `alpha` is 0, so the transform was put back
  to the **previous** step's pose.
- `PhysicsScene.Adopt`, at the top of the next `StepCharacters`, saw a `LocalTransform` that differed
  from the controller's position by more than a tenth of a millimetre and **teleported the controller
  to it**, because a character's transform is how a game teleports one.

So every other frame's step was undone before it was taken, and the arithmetic gives exactly half. The
same file stated the premise that made it possible: *"a character needs no tag, because nothing else
writes its transform between two steps"* — `PhysicsInterpolationSystem` does, on the same entity,
every frame. That sentence is now a paragraph saying the opposite, because a comment asserting the
invariant being violated is how this survived.

A/B, same route, on the Null device: with the component, 1.819 m in the first second and 8.561 m in
four; with the two lines that add it removed, **3.821 m** in the first second. Nothing else changed.
After the fix the same route covers **3.746 m** and **17.171 m** — the remaining 0.075 m is one fixed
step at walk speed, because `Report` reads the transform and what is *drawn* is a step behind the
simulation by design.

It was not this sample's misuse. `PhysicsScene.WriteCharacterBack` fills a character's
`PhysicsInterpolation` itself, which is what puts a character in that pass's query at all.

**The repair, and why it is not the obvious one.** `PhysicsInterpolationSystem` now records the exact
pose it wrote in `PhysicsInterpolation.DrawnPosition`, and `Adopt` ignores a transform still sitting
on it. The obvious repair — do not adopt a transform lying on the segment between the two
interpolated poses — is worse rather than cheaper: `Adopt` is on the path a rollback replays, and a
client that mispredicts *along its own path* is corrected along exactly that segment, so the guess
would swallow the commonest correction there is. Excluding characters from the smoothing was the other
candidate and trades this defect for the camera judder the comment in `PlayerRig.CreatePawn`
describes. `PhysicsScene.CharacterAdoptionCount` is the number that would have made this visible from
the start: a walking character is adopted zero times, and this one was adopted sixty times a second.

**And this sample is what could see it at all.** A still capture cannot see a speed;
`CharacterSceneTests.SmoothingACharacterDoesNotChangeHowFarItWalks` is the regression test the
measurement bought, and it runs a whole `EngineLoop` because the four physics passes called by hand —
what every other test in that file does — never run `LateUpdate` and so could not see it either.

**And there is a lake, ten metres past the north gate.** It is the tree's only `!WaterSurface` node:
before this, `WaterMeshRenderer` was exercised by the golden suite alone. Three more absences had to
be filled to get a body as far as the fold, and all three are the same shape as the collider —

- `WaterZoneSystem.Splines` and `.Waves` are null until a game sets them, and no host sets them.
  `AssetWaterSource` is the implementation, and `Arena.SupplyWater` is the only place in the tree
  outside its own tests that constructs one.
- `WaterZoneSystem.Ground` defaults to `FlatWaterGround(0)` and only the editor's presenter has ever
  replaced it, so in a game the depth of every lake was its own surface height above zero rather than
  surface minus terrain. Doc 35 § D3 is explicit that the terrain is a first-class producer of that
  channel; `TerrainGroundSystem` is a game being it.
- `CharacterState.Immersion` is written by nothing, so `CharacterMoveMode.Swimming` could not be
  entered from any scene. `WaterImmersionSystem` writes it, and a character dropped into the middle
  of the lake settles at y = −0.334 against the −0.33 its own `SwimRestImmersion` predicts.
  ⚠ **That system was written here and no longer lives here.** It is
  `Vixen.Water.Physics.WaterImmersionSystem` now — a game-relevant system sitting in a sample is a
  feature no game can use without copying source, and its two dependencies were always that
  assembly's exactly. This sample is now a *consumer* of it, which is the only thing in the tree
  that exercises it end to end.

⚠ **A fourth was a defect rather than a gap, and this sample used to work around it.**
`WaterZoneSystem.GatherBodies` keyed a body's cache on its component and its placement and stored the
*failure* with it, so a body whose `SplineFor` answered null was recorded as unresolved and never
asked again — and `AssetWaterSource` answers null for the first frames by construction, which made a
lake named in a scene one that could never appear. The zone's sea state was the evidence:
`GatherZones` re-resolves every fold with no cache, so the `.vxwaves` arrived late and worked while
the `.vxspline` beside it arrived late and did not. The fold caches the success and not the answer
now, `Arena.Warm`'s blocking loop is gone, and
`WaterZoneSystemTests.A_spline_that_arrives_late_is_asked_again_and_the_lake_appears` counts the asks
rather than only the bodies — because a source that answers on the first ask, which is every other
one in that file, cannot tell the two behaviours apart. A 60-frame headless run of this level is the
A/B: with the fold as it was and no warm loop it ends `1 zone(s), 0 bod(ies), 1 unresolved spline(s)`,
and with the fold as it is, `1 zone(s), 1 bod(ies), 0 unresolved spline(s)`.

⚠ **The water drew nothing for a while, and none of the counters could see why.** The scene folded —
one zone, one body, nothing unresolved — the field rasterised, `!WaterSurface` submitted its patches,
and the frame was identical whether the pass ran or not. `!Water` composites absorption and
scattering against the sun and sky colours, which in this level are a radiance of thousands, and the
document was handing it an authored tint around one: the integration was exactly right, four decades
under the exposure. `WaterRenderer.LightFrom` takes all three from the frame's own `SceneLighting`
now.
## Looking at it

```
dotnet build -c Release
dotnet bin/Release/net10.0/ThirdPersonShooter.dll \
    --vixen-headless --vixen-frames 512 --vixen-variant Development --vixen-capture ./shots
```

⚠ **512, and not fewer.** The frame's exposure, surface cache and probe history all climb for a long
time: the whole-frame mean channel goes 9.2 → 23.6 → 35.8 → 37.4 at 4, 64, 256 and 512 frames, so a
short run is a picture of a renderer that has not started yet. Two runs at the same count agree to a
mean absolute channel of 0.002/255, which is what makes an A/B against another commit mean anything.

⚠ **The spawn corner faces away from the sun, so a correct frame is almost all shade.** That has been
mistaken for a broken one. [The guide](../../docs/guide/rendering/capturing-a-frame.md) says what else
a headless picture is and is not.

### Walking, because a still frame cannot show a temporal defect

`VIXEN_SPAWN` places the camera and nothing moves it afterwards — the pawn falls the twenty
centimetres from its spawn height to the floor and that is the whole of the motion a headless run has
ever had, over about a fifth of a second. So every picture above is of a **still** frame, and
reprojection, motion vectors, motion blur, the fog's temporal history and the virtual shadow map's
refit are all things that only happen when the camera moves. `VIXEN_WALK` is the other half: a script
the player is driven by instead of a device.

```
VIXEN_SPAWN=-3,0.2,24,180 VIXEN_WALK=20 \
dotnet bin/Release/net10.0/ThirdPersonShooter.dll \
    --vixen-headless --vixen-frames 512 --vixen-variant Development --vixen-capture ./shots
```

A script is legs separated by `;`, each `seconds:forward:strafe:yawRate:pitchRate` with everything
after the duration optional — so `20` is "walk straight ahead for twenty seconds", `2:0:0:90` is
"stand still and pan ninety degrees a second", and the two joined by a `;` do one then the other.
`ScriptedWalk` is the whole of it, and it is an `IPlayerInputSource`, which is the seam the engine
already documents as the one a planner, a replay or a test takes — so nothing in the engine changed
to make this work.

⚠ **It rides the fixed step and has no clock of its own.** `--vixen-capture` implies
`--vixen-fixed-step` at a sixtieth, so a leg's duration is an exact number of frames and two runs walk
identically. The tests beside it were checked against a sabotaged copy holding a `Stopwatch` — the
second clock that was found driving the grass wind — and the result is written down there, because the
obvious guard is the one that does not catch it.

⚠ **The route matters, and the level is full of things to walk into.** `Crate4` sits at (0, 0, 12)
and is 1.6 m tall, so the obvious script — spawn at the origin, walk south — stops dead after eleven
metres with every counter reporting a successful run. `x = −3` misses it and threads the south gate,
whose hole is `x ∈ [−4, 4]`.

⚠ **The character used to walk at half the speed the level asks for**, and it was not this harness's
doing — see *A character with `PhysicsInterpolation` used to walk at half speed* above, which this
harness is what found. A script's durations are now against the level's own 4.5 m/s, so **every route
written before that fix goes twice as far as its author measured**. The twenty-second script above
now covers 87.9 m and finishes at (−3.2, 3.7, 111.9), up on the terrain rather than just past the
gate; it still walks the whole way with no respawns, but it is a different picture than it was.

`VIXEN_STRIP=first-last[/stride]` is the other half, and it is what makes a temporal question
answerable at all: it writes a picture of **every** frame in a range beside the ordinary `frame.png`,
so frame *N* and frame *N* + 1 come out of one run and their difference is the frame step rather than
two runs' scheduling. `0-1200/30` samples twenty seconds of walking at half-second intervals, which is
the timescale a person reporting "it blinks for ten or twenty seconds" is describing.

### What a walking capture measures, and what it does not

Measured on this level at 1600 × 900, 512 frames, from `-3,0.2,24,180` walking out of the south gate:

| | Mean channel gap | Mean \|delta\| | Flipped pixels |
|---|---|---|---|
| Two identical **walking** runs | 0.0040 | 0.0085/255 | 32 157 of 1 440 000 |
| Two identical **still** runs, same start pose | 0.0197 | 0.0492/255 | 170 194 of 1 440 000 |

⚠ **The walking floor is about six times *tighter* than the still floor at the same viewpoint**, which
is the opposite of what was expected. Two walking runs land on bit-identical player positions —
`(-3.0024705, 0.69134784, 42.6403)` both times — so nothing in the walk itself contributes, and what
is left is the renderer's own scheduling residue over whatever is on screen. The still view here looks
at the arena floor and the walls, and the walking one ends up in deep grass; screen-probe GI over
built surfaces is apparently noisier run-to-run than grass is. **The floor is a property of the
viewpoint, not of whether the camera moved** — measure it where you intend to measure, every time.

A *within-run* frame-to-frame delta is a different and much better instrument, because the two frames
share one schedule. Walking, mid-arena, facing down-sun, thirty-one consecutive frames:

| | Whole-frame mean \|delta\| | Worst 32 × 32 tile | Tiles over 4/255 |
|---|---|---|---|
| Still | 0.17 – 0.25 | 1.9 – 2.9 | 0 |
| Walking | 0.57 – 0.71 | 5.5 – 11.2 | 8 – 22 |

Both series are **smooth**: the walking one's frame-to-frame delta never departs from its own median
by more than 12 %, and the single largest regional event in thirty frames — a tile at 11.2 — is a
lamp's ember particles against the sky, not a shadow.

### ⚠ Walking does not reproduce the reported shadow blink

Four walking routes, all captured as within-run strips so the comparison is free of cross-run noise:
out of the south gate onto the terrain, across the gate itself, the same at three times the throttle
(≈ 6 m/s, so the clipmap's finest level recentres about every three frames instead of every eight),
and mid-arena facing down-sun where the walls' shadows are actually on screen. **None shows a blink.**

The whole-frame frame-to-frame delta stays within 8–40 % of its own median in every strip; every
regional peak that was chased turned out to be embers or the third-person camera's occlusion spring
pulling in behind the pawn. The direct measurement is the shadowed floor's own brightness, over
thirty-one consecutive frames of walking mid-arena:

| Region | Walking: total range / worst single-frame step | Still: the same |
|---|---|---|
| The shadow boundary by the walls | 0.62 / 0.15 of 255 | 0.26 / 0.11 |
| Open shadowed floor | 0.40 / 0.08 | 0.11 / 0.02 |

Motion makes the shadowed floor about three times less steady, and three times a hundredth of a unit
is still a hundredth of a unit. A page/cascade disagreement flipping over a region would be units, not
hundredths.

⚠ **This is a negative result at *this* timescale and not a closure.** The complaint was ten to
twenty seconds; a strip of thirty-one consecutive frames is half a second. A twenty-second strided
strip does swing — the whole-frame mean channel runs 83.9 → 102.4 → 87.7 → 109.4 over the walk — but
at half-second sampling that is auto-exposure and the camera's own occlusion, both of which change
what is on screen, and it cannot separate a shadow from a view. Closing the question wants a
per-frame counter of pages absent at the shading pass, not another picture.

## The picture, and what is in the way

⚠ **`WorldRenderer`'s mesh path had never drawn on a device, and this project is the first thing to
try.** Every device test assembles the pieces by hand; `Samples/03` renders lit materials through a
stack of its own and never touches `WorldRenderer`. The path a *game* gets — `VixenApp.Run` →
`AppGraphics` → `WorldRenderer` — had every host-owned slot empty, and each one is invisible until the
one before it is fixed, because a draw refused for one set says nothing about the next.

| | What was unset | What it looked like |
|---|---|---|
| ✅ | `EffectPipelineDescriber.VertexLayouts`, which every `MeshDraw` indexes at zero | The pipeline declared no vertex attributes and Vulkan refused it — `ForwardPlus` reads locations 6–9 |
| ✅ | `MaterialRenderFeature.Device` and `.Descriptors` | Set 2 never bound |
| ✅ | `ForwardLightingRenderFeature.Layout` | Set 3 never bound |
| ✅ | `RenderPassRenderer.SceneConstants` — the builder set it on three node kinds and not on the one a forward frame is made of | Set 0 never reached the draw context |
| ✅ | `SingleStageRenderer.Constants` had no fallback when a document declares no `viewBlock:` | Set 1 never bound |
| ✅ | `ViewConstants` needed a `Layout` before there was a shader to take one from | Set 1 bound a frame late, and one refused frame is a GPU fault on Metal — so there was no later frame |
| ✅ | **Set 0's thirteen bindings** — filled between the document's nodes and `ArenaFrame`'s stand-ins | Every draw now lands; see the next section for who fills what |

### What set 0 wants, and why nothing fills it

`ForwardPlus` declares thirteen bindings in set 0 — and **declares all of them whatever the
permutations say**, which is the thing that made this hard to see:

```
0  ForwardPlusPerFrameUniforms (UniformBuffer)   7  IrradianceFieldProbes.irradianceL1G
1  shadowMap                                     8  IrradianceFieldProbes.irradianceL1B
2  environment                                   9  shadowSampler
3  probes                                       10  environmentSampler
4  IrradianceFieldProbes.irradianceIndirection  11  probeSampler
5  IrradianceFieldProbes.irradianceL0           12  IrradianceFieldProbes.irradiancePointSampler
6  IrradianceFieldProbes.irradianceL1R
```

Turning `UseShadows`, `UseImageBasedLighting`, `UseReflectionProbe` and `UseIrradianceField` off
changes the generated code and leaves every declaration in place. `EffectSetWriter` writes every
binding or none, so one unfilled texture is the whole frame refused.

⚠ **A generic fallback cannot close this.** The obvious fix — a 1×1 white texture bound wherever
nothing else is — does not work here, because `EffectBinding` carries no texture *dimension*:
`environment` and `probes` are cubes, the five irradiance textures are 3D, and a 2D view bound to any
of them is a different validation error. The resources have to come from the nodes that own them —
the shadow atlas, the environment, and the `!IrradianceField` node whose textures this document
already builds and does not hand to a material.

That plumbing exists now and this project uses all of it: the `!IrradianceField` node names
`ForwardPlus` among its `passes:` and hands over the five volumes and their samplers, the
`!ShadowMap` node and the Main pass's `sceneTextures:` line fill the shadow pair, and
`ArenaFrame.Apply` fills the sky, the probe array and the cluster stand-in. The set is complete —
`Arena.ReportFrame` prints it — which is what the picture row above is claiming.

## The behaviour scripts

Four, chosen so that between them they show every way a behaviour is used.

| Script | Shows |
|---|---|
| [`CharacterAnimation`](Behaviors/CharacterAnimation.cs) | A per-entity script with references its maker gave it — the case an `ISystem` is the wrong shape for |
| [`WeaponFire`](Behaviors/WeaponFire.cs) | Reading the controller's aim rather than the body's rotation, and turning a held button into an edge |
| [`RespawnWhenBelow`](Behaviors/RespawnWhenBelow.cs) | Writing `LocalTransform` on a character, which is a *teleport*, and why the velocity has to be cleared by hand |
| [`LampFlicker`](Behaviors/LampFlicker.cs) | `Start` over `Awake`, and attachment by query to entities the level made and the game has never seen |

`PlayerRig` attaches the first three to entities it built and holds handles to; `Arena` attaches the
fourth by querying for every point light the scene placed. Those are the only two ways a behaviour
ever gets onto an entity, and the project does both.

## The embers

Every lamp drifts sparks, and the whole of the game code for it is three lines in `Arena.Sparkle`.

| Piece | Where |
|---|---|
| the effect — a spawn rate, five initializers, six updaters, an output | [`Assets/Effects/Embers.vxvfx`](Assets/Effects/Embers.vxvfx) |
| which entities emit it | one `!VfxEmitter` line per lamp in [`Arena.vxscene`](Assets/Scenes/Arena.vxscene) |
| the stage and the pass | `Embers` and `!RenderPass Sparks` in [`Frame.vxcompositor`](Assets/Frame.vxcompositor) |
| the three the document cannot say | `Arena.Sparkle` — the camera the quads face, the brightness, the stage's mask |

There is no per-lamp code. `VfxExtractionSystem` resolves each emitter's reference, creates the
simulation, places it at the entity's transform, steps it, and takes it away when the component goes
— which is `MeshExtractionSystem`'s shape with one job added, because nothing else in the engine
steps a `VfxSystem`.

⚠ **`ParticleRenderFeature` had no shader it could be drawn with.** It expands particles on the CPU
into a position, a texture coordinate and a colour; `ParticleBillboard.rvn` expands the quad itself
from a particle record, which is the shape the GPU path wants. [`ParticleSprite.rvn`](../../Raven/Library/Vfx/ParticleSprite.rvn)
is the missing half, and `ParticleSpriteDeviceTests` is the first test that has ever put a particle
on a screen. [The guide](../../docs/guide/rendering/particles.md) is the rest.

⚠ **`.vxvfx` had no runtime compiler**, so an effect had to be written in C# — which
`docs/overview.md` recorded as owed. `VfxImporter` closes it: the file is a node graph the editor
edits, and the build compiles it into the flat instruction list both backends run.

⚠ **The opcodes are world-space and there was no emitter transform**, so one graph could serve
exactly one entity. `VfxSystem.Origin` is what makes fifteen lamps one asset — read at *spawn*, so a
moving emitter leaves its live particles behind rather than dragging them, which is what smoke does.

## What this found

An end-to-end project is a test. This one failed nine times before it ran, every failure in the
engine rather than in the sample, and seven of the nine produced a *working* program with a wrong
answer rather than an error.

| Fix | What was wrong |
|---|---|
| [`GameAssemblies`](../../Tools/Vixen.Cli/GameAssemblies.cs) | The build tool never loaded the game's assembly, so a level could not name a component the game declared — and never loaded the engine's either, so whether `!MeshRenderable` resolved depended on which importers happened to run that build |
| [`Vixen.Sdk.targets`](../../Tools/Vixen.Sdk/build/Vixen.Sdk.targets) | The import runs before the compiler, so the assembly it needs does not exist on a clean build. That pass is now advisory and the content build after `Build` is the authority |
| [`ModelImporter.FieldName`](../../Editor/Vixen.Editor.Assets/Models/ModelImporter.cs) | A model's distance field was named exactly what its mesh was named, so both claimed one address, so the model got none, so **every scene referencing a model failed the build** |
| `ModelImporter` type strings | A mesh artefact was written as `"Mesh"` while `MeshData`'s contract alias is `"MeshData"`, so `TypeIdOf` missed and stamped every mesh chunk in every build with `ImportedArtifact` — an editor type no game has heard of |
| [`CompositorImporter`](../../Editor/Vixen.Editor.Assets/Compositors/CompositorImporter.cs) | `.vxcompositor` matched no importer and shipped as a `Blob`, so `AppGraphics` failed to load the project's frame, logged one warning and silently drew its own built-in one instead |
| `ImportPipeline.TryChooseImporter` | A `.meta` recording `!RawImporter` pinned the file to it for ever, so the moment a real importer for a format shipped, every file imported before it stayed a byte blob — in every checkout that had the sidecar. The fallback now pins nothing |
| `ImportPipeline` settings binding | The recorded settings block belongs to the importer that wrote it; binding a `RawImportSettings` against `CompositorImportSettings` failed with "its import settings do not fit", about a file nobody had touched |
| [`ObjectDatabase.TryReadObject`](../../Core/Vixen.Core.Serialization/Storage/ObjectDatabase.cs) + [`RawPayload`](../../Core/Vixen.Assets/AssetManager.cs) | The closure preload deserialised every dependency, and a model's cluster and page blobs are deliberately raw — so a model with a distance field was unloadable by anything referencing it, which is to say by every scene |
| [`GraphicsOptions.Factories`](../../Tools/Vixen.App/GraphicsOptions.cs) | There was no moment early enough for a game to bind its own node kinds: `AppGraphics` builds the compositor in its constructor, before `OnInitialise` |

### And nine more, from somebody playing it

The second round came from a person walking around the level rather than from a counter, which is a
different instrument and found a different class of thing. Six were the same shape as the nine above
— engine faults that produced a working program — and three were the sample's own.

| Fix | What was wrong |
|---|---|
| [`ShadowProjections.CubeAxes`](../../Core/Vixen.Rendering/ShadowProjections.cs) + [`CubeMapping.Locate`](../../Core/Vixen.Rendering/Lighting/CubeMapping.cs) | The cube convention was a half turn round from the hardware's on ±X and ±Z and right on ±Y, so every CPU-baked cube had four faces disagreeing with two at every edge. The sky read as **a yellow box with a blue lid**. Both halves were wrong together, so the round-trip test passed |
| [`MotionBlur.Longest`](../../Raven/Library/PostFx/MotionBlur.rvn) | The neighbour-max taps were spaced half the maximum radius apart and the winner was taken rather than blended, so the smear was constant over 24-pixel squares. Small squares laid edge to edge, on fast pans, over bright ground |
| `PhysicsInterpolation` on the pawn | The pawn never asked for it, so it held one fixed step's position across every frame inside that step. Everything parented to it stepped too — including the camera, which is what a person notices |
| [`TonemapAsset.ExposureBuffer`](../../Core/Vixen.Rendering.PostFx/PostEffectAssets.cs) | `TonemapRenderer` has read a measured exposure since the meter was written and no `!Tonemap` could name the buffer, so a document could run the whole histogram and then tonemap with a typed number |
| Spawn rotation | `Spawn0` carries a 45° yaw. It went onto the **capsule**, which deliberately never turns, so the visuals hanging off it wrote a world-space facing as a local rotation under a permanent 45° parent — the avatar walked forward facing 45° left, for ever. It seeds `ControlRotation` now, which is the one place a facing belongs |
| `CursorMode.Relative` | Nothing asked for it. `<Mouse>/delta` stops at the edge of the desktop, so a look was clamped to whatever fraction of a turn the remaining screen was worth — which reads as a yaw limit somebody coded |
| Seeding `PhysicsInterpolation` | And then a zeroed one lerps the origin with the origin and drags the pawn to (0, 0, 0). The component was right and `default` was the trap |
| [`Raven/Library/PunctualShadows`](../../Raven/Library/PunctualShadows/PunctualShadows.rvn) | **Point and spot lights were unshadowed everywhere in the engine.** `PunctualShadowRenderer` has rendered a depth atlas since it was written and no shader in `Raven/Library` had a lookup that could read one, so the sodium floodlights outside these houses lit the inside of their far walls — which reads as a lighting setup somebody got wrong, not as a missing feature. Reported as *"outside lamp is lighting the interior wall"* |
| `MaxLightsPerObject` and `MaxLights` | Eight slots for nineteen lights. `ForwardLightingRenderFeature.Score` measures from the sphere's near point, so on a 64 m floor **every** lamp is inside the bounds, the distance clamps to zero and the ranking collapses to intensity alone — and `LampFlicker` then swings fifteen floodlights of 110–150 klm by ±12% out of phase, so which eight win changes every frame. A lamp's whole contribution to the floor blinks on and off while the wall beside it stands still. `A_list_that_overflows_churns_when_a_lamp_flickers` measures it in both directions. ⚠ A scene-sized answer: clustered lighting is the real one, and this project cannot reach it yet. ⚠ **The engine's own half landed later**: `CompositorBuilder` now publishes the feature's budget as the shader's `MaxLights` for every shading pass a document declares, so the two numbers cannot disagree by omission — and the disagreement never was the shader reading past what the host wrote, whichever side was shorter, but the shorter side silently winning |
| The sun's elevation | **Six degrees, which is most of the floor banding and is not a defect.** A shadow map looks along the sun, so one of its texels projects onto a horizontal floor stretched by 1/sin(elevation) — 9.6× at six degrees. Every shadow edge quantises into slivers that long, aligned with the light's own texel grid, so a flat floor reads as straight bands that rotate when the sun does. Four correct fixes to the shadow path left it exactly where it was, because it is the geometry of grazing incidence rather than a bug in any of them. Raised to thirty, where the stretch is 2.0× |
| [`ShadowCascades.Fit`](../../Core/Vixen.Rendering/ShadowCascades.cs) | The light's texel grid was built from the **camera's** `up`, so the sun's whole shadow map turned under stationary geometry whenever the player looked around. Two comments over the guard said the basis must not depend on the camera and that `up` was for the degenerate case — and the guard's condition was that case's negation, so for every ordinary sun the camera won and the camera-independent reference on the line above was dead. Reported as *"shadows from the sun on the ground rotate when I rotate the camera"* |
| [`AmbientCombine.Reflectance`](../../Raven/Library/PostFx/AmbientCombine.rvn) | **Every surface in the arena was a mirror, and the level had no colour in it.** The reflections plane's alpha is *validity* — `ReflectionTrace` writes a literal 1 wherever the trace answered — and the combine lerped by it, so at every shaded pixel the traced radiance replaced the whole combined colour: the sun term, the rebuilt ambient, and therefore the albedo. Concrete at roughness 0.81 came out as a flat mirror of the irradiance field, and forcing every material's base colour to red and then to black produced pixel-identical frames. The weight is `Ibl.EnvironmentDfg` against the normals plane's roughness and the view angle now — four per cent for a rough dielectric, which is what one is. ⚠ Two consumers read that plane and `Water.rvn` already weighed it by *water's* Fresnel, which is why the reflectance belongs to the consumer and not to the trace's alpha |
| [`ShadowProjections.Tile`](../../Core/Vixen.Rendering/ShadowProjections.cs) | Found while writing the punctual atlas. The cascade atlas folded its tile with the same translation on both axes, which inverts the tile *row* under the y negation `Transform.NdcToUv` gained four days after the fold was written — so cascade zero read cascade two's tile and got a plausible depth out of it. `ShadowCascadeTests` computed its own UV the way the fold assumed, so the two agreed with each other and neither agreed with the shader |

Two long-standing "found and not fixed" entries closed with the ambient split: `SceneNormals` has a
producer now (the Main pass's third attachment under `ForwardPlus.SplitOutputs`), and
`!AmbientCombine` is the consumer an occlusion buffer never had. Turning the freed nodes on found
three more engine seams, and all three are closed in the engine now: `ReflectionRenderer`
`frame.Add`s its target into the frame's namespace (it used to import into the render graph only,
so no document line could resolve the name — the regression test beside the renderer says how the
engine suites missed it), `PostEffectFactory` hands `!DistanceFieldAo` its `SceneConstants` the way
the builder's own node kinds get theirs, and the asset grew the `view:` knob its unprojection
needed. `ArenaIllumination.Feed` carried the last two as per-frame host workarounds while the seams
were open; those lines are gone, and `!Reflections` is on with its plane in the combine's
`reflections:` seat.

⚠ **`.vxanim` has no runtime path.** A clip is imported as its authored YAML and nothing compiles it
into the `AnimationClipData` that `AnimationClip.Create` bakes against a skeleton, so a game cannot
load one by address. `CharacterAnimation` computes the same swing the clips describe and says so at
its top; the clips become its source the moment that compiler exists.

## How this differs from what the CLI emits

One line, and it is called out in the `.csproj`. A `vixen new` project says
`<Project Sdk="Vixen.Sdk/0.1.0">` and gets the SDK from a package; this one imports
`Tools/Vixen.Sdk/build/Vixen.Sdk.props` and `.targets` **by path**, because nothing in this
repository can reference a package version the repository has not published. Those are the same two
files a shipped game gets — the import step, the content build, the copy beside the binary and the
publish list are all the real ones.

`VixenToolPath` points at the in-tree CLI, which is the SDK's own documented first answer for "how do
I run the tool" and how its tests drive it without installing anything.

## The content is placeholder, and deliberately so

Boxes, synthesised WAVs and hand-keyed transform curves — all generated, all committed, all real
files through the real importers.

⚠ **`.obj` carries no rig**, so nothing here is skinned. The character is **segmented**: torso, head,
arms and legs are separate meshes parented into a skeleton, which is how Quake 1 and Lego characters
work. The animation drives the joints and the meshes go along because they hang from them.

That is not a shortcut around the animation system — `Skeleton`, `AnimationClip`, the blend trees and
the state machine are all the real ones, and `.vxanim` is the format the engine has for exactly this:
its own remarks call it "a camera move, a door, a UI wobble, a hand-keyed idle". Swapping in a rigged
glTF changes the assets and none of the code.

## What it is meant to demonstrate

- [docs/plan/29](../../docs/plan/29-players-and-possession.md) — a controller that outlives its pawn,
  `MoveIntent` as the one seam, and a third-person rig steered by the player's aim
- [docs/plan/22](../../docs/plan/22-virtualized-geometry.md) — `!ClusterCulling` and
  `!VisibilityBuffer` for the virtualized path, and `!GpuCulling` with `!HiZ` for the classic one:
  the object cull dispatched on the device, occlusion-tested against last frame's depth pyramid.
  And phase 7: `!VirtualShadow` marks the frame's own depth into page requests, draws only the sun
  shadow pages some pixel asked for, and the forward pass shades through `VirtualShadowLookup` —
  falling through to the `!ShadowMap` cascades wherever the map has no drawn page, so the sun is
  shadowed exactly once at every pixel. ⚠ Two honest limits, both doc 22's own: a *virtualized*
  mesh casts through the fallback mesh phase 1 generates (the traversal cannot yet cut clusters per
  page — named as owed there), and the directional form is a clipmap centred on the camera
- [docs/plan/19](../../docs/plan/19-lighting-and-global-illumination.md) — the ambient split end to
  end: `ForwardPlus.SplitOutputs` writing direct light, albedo, normals and `f0` to four targets,
  `!ScreenProbeGather` tracing screen probes over the `!GlobalDistanceField` clipmap with the
  `!IrradianceField` as its far field and the `!SurfaceCache` behind its hits, `!DistanceFieldAo`
  and `!Ssao` producing the room's and the crease's occlusion, and `!AmbientCombine` rebuilding the
  diffuse ambient from all of it — `albedo × irradiance × occlusion` over the direct term. The
  seven lamps are what makes the bounced light visible in a scene that would otherwise have one sun
- Post FX — the whole chain, TAA through `!Tonemap` to the lens, plus `!ShadowMap` cascades and the
  `!PunctualShadows` atlas. The lamps stay on that punctual atlas: doc 22 built the virtual map as a
  clipmap plus *a map per spot*, and this level's lamps are point lights — six faces each, which is
  the cube address space phase 7 still owes — so wiring them through the map is not a knob yet.
  Spot lights would be: `VirtualShadowRenderer.SpotProjections` takes exactly the projections a
  punctual host already builds, and the lookup's spot path exists — what is missing is the host
  loop matching light indices to level records, which this level has no spot light to earn

Every one of those is a line in `Assets/Frame.vxcompositor`. None of it is assembled in C#, which is
the point: a frame that cannot be written down is a frame every host has to reimplement.

### What is on, and what stays off

Everything the engine can currently compose is on, and since the ambient split that is the whole
page: GPU object culling with Hi-Z occlusion (`!GpuCulling` + `!HiZ`), the virtualized path
with its resolve split onto the same four planes the forward pass writes (`!VisibilityBuffer
albedo:/normals:/specular:`), the distance field, the irradiance field — filled by `TracedIrradianceFiller`
and consumed as the screen-probe trace's far field — the surface cache behind the trace's hits,
`!ScreenProbeGather` over the frame's own Depth32Float depth and Rgba16Float normals,
`!DistanceFieldAo` (`sunShadow: false` — the shadow path owns the sun: the `!VirtualShadow` map
where its pages are drawn, the cascades everywhere else, and a cone march here would shadow it a
third time; `view: Camera` for the world unprojection) with `!Ssao` beside it,
`!VirtualShadow` marking the frame's depth into sun shadow pages that the forward pass reads
through its `directionalShadow` compose slot, `!Reflections` marching the same clipmap with SSR over the
frame's own lit opaques and its answer blended into the combine by the surface's own specular
reflectance, and `!AmbientCombine`
rebuilding diffuse ambient from all of them before the TAA sees a pixel. The
material's own field reads moved out with the split: `UseIrradianceField` and
`UseDistanceFieldOcclusion` are off again, each with its double-counting story told in
`Arena.Paint`, because every term now has exactly one seat — irradiance in the gather, occlusion in
the screen pair, reflections in the trace, all applied in the combine.

Off, each with its one-line reason, stated at length beside the node itself:

| Off | Why |
|---|---|
| `!IndirectDiffuse` | Redundant, not blocked: the gather already supplies the combine's screen irradiance with real traces behind it — running both is the same skylight added twice |
| `!Outline` | A look this level does not want: over a physically lit arena it reads as a rendering fault, and over a selection mask it is the editor's highlight rather than a game's |
| Ray-traced field (`RayQueryField`) | No acceleration structures on MoltenVK — `HasRayTracing` is false on this sample's target |

Licensed under Apache-2.0.
