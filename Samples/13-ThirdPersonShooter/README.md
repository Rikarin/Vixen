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
| `MaxLightsPerObject` and `MaxLights` | Eight slots for nineteen lights. `ForwardLightingRenderFeature.Score` measures from the sphere's near point, so on a 64 m floor **every** lamp is inside the bounds, the distance clamps to zero and the ranking collapses to intensity alone — and `LampFlicker` then swings fifteen floodlights of 110–150 klm by ±12% out of phase, so which eight win changes every frame. A lamp's whole contribution to the floor blinks on and off while the wall beside it stands still. `A_list_that_overflows_churns_when_a_lamp_flickers` measures it in both directions. ⚠ A scene-sized answer: clustered lighting is the real one, and this project cannot reach it yet |
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
  end: `ForwardPlus.SplitOutputs` writing direct light, albedo and normals to three targets,
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
with its resolve split onto the same three planes the forward pass writes (`!VisibilityBuffer
albedo:/normals:`), the distance field, the irradiance field — filled by `TracedIrradianceFiller`
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
