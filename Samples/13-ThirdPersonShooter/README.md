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

⚠ **It builds, it starts, it simulates — and it does not draw yet.** `dotnet build` performs the whole
chain from a clean checkout, and `--vixen-frames N` loads the level, builds its collision, spawns a
possessed player and walks him. What it does not do is put a picture on the screen: the draws are
issued and the driver refuses them, because the material's own descriptor set is never written.

```
Compositor Assets/Frame.vxcompositor loaded.
Content mounted from /app/Content: 70 addresses.
Loaded scene 'Arena' with 31 entities.
Built 19 collider(s) from the level's authored boxes, over 24 registered shape(s).
Rebuilt 'Assets/Frame.vxcompositor' with the distance field, the probe field and the virtualized path in it.
Loaded 6 sound(s); 0 were not published.
Player 0 spawned at (-20, 0.2, -20), possessing its pawn.
Ran 60 frame(s). The player finished at (-20, -5.96e-08, -20), Walking, having fired 0 shot(s) …
```

⚠ **Every line above is true of a game that renders nothing**, which is exactly how the black screen
survived a headless run that reported success. The player spawns at y = 0.2 and the floor's top is
y = 0, so *`Walking` at zero* proves the character was stepped and found the collision the level
authored — and proves nothing whatever about the picture. `Arena.ReportFrame` is the line that does:
objects, meshes, shader variants, misses and **bound material sets**, any of which at zero is a black
window.

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
| ⬜ | **A picture.** The per-material descriptor set is never written, so every draw is refused |
| ⬜ | Wiring `--vixen-frames` into CI as a gate — no sample is run headlessly in CI today, so this is a pattern to establish rather than a row to fill in |

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
| ⬜ | **Set 0's thirteen bindings have nothing to fill them** | Every draw is still refused |

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

That last part is the actual remaining work: **wiring a frame node's outputs into the shading pass's
set 0**. It is frame plumbing in `Vixen.Rendering`, not anything this project can do from the outside.

## The behaviour scripts

Five, chosen so that between them they show every way a behaviour is used.

| Script | Shows |
|---|---|
| [`CharacterAnimation`](Behaviors/CharacterAnimation.cs) | A per-entity script with references its maker gave it — the case an `ISystem` is the wrong shape for |
| [`WeaponFire`](Behaviors/WeaponFire.cs) | Reading the controller's aim rather than the body's rotation, and turning a held button into an edge |
| [`RespawnWhenBelow`](Behaviors/RespawnWhenBelow.cs) | Writing `LocalTransform` on a character, which is a *teleport*, and why the velocity has to be cleared by hand |
| [`LampFlicker`](Behaviors/LampFlicker.cs) | `Start` over `Awake`, and attachment by query to entities the level made and the game has never seen |
| [`EmberDrift`](Behaviors/EmberDrift.cs) | A behaviour as the frame's per-frame hook for something that is not an entity at all — a `VfxSystem` nothing in the engine steps |

`PlayerRig` attaches the first three to entities it built and holds handles to; `Arena` attaches the
last two by querying for every point light the scene placed. Those are the only two ways a behaviour
ever gets onto an entity, and the project does both.

## The embers

Every lamp drifts sparks, which is the project's answer to "a sample should have VFX in it" and is
also the first thing in the engine to draw a particle at all.

| Piece | Where |
|---|---|
| the graph — a spawn rate, five initializers, six updaters | [`ArenaEmbers`](ArenaEmbers.cs) |
| the wiring — one render object per lamp, the material, the camera the quads face | `Arena.Embers` and `Arena.Sparkle` in [`Arena.cs`](Arena.cs) |
| the step | [`EmberDrift`](Behaviors/EmberDrift.cs) |
| the stage and the pass | the `Embers` stage and `!RenderPass Sparks` in [`Frame.vxcompositor`](Assets/Frame.vxcompositor) |

⚠ **`ParticleRenderFeature` had no shader it could be drawn with.** It expands particles on the CPU
into a position, a texture coordinate and a colour; `ParticleBillboard.rvn` expands the quad itself
from a particle record, which is the shape the GPU path wants. [`ParticleSprite.rvn`](../../Raven/Library/Vfx/ParticleSprite.rvn)
is the missing half, and `ParticleSpriteDeviceTests` is the first test that has ever put a particle
on a screen. [The guide](../../docs/guide/rendering/particles.md) is the rest.

⚠ **There is no `.vxvfx` a game can load**, so the graph is written in code — which `docs/overview.md`
already records as owed. And the opcodes are world-space, with no emitter transform anywhere, so
eighteen lamps are eighteen graphs with eighteen positions baked in rather than one effect
instanced.

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
| [`ShadowProjections.Tile`](../../Core/Vixen.Rendering/ShadowProjections.cs) | Found while writing the punctual atlas. The cascade atlas folded its tile with the same translation on both axes, which inverts the tile *row* under the y negation `Transform.NdcToUv` gained four days after the fold was written — so cascade zero read cascade two's tile and got a plausible depth out of it. `ShadowCascadeTests` computed its own UV the way the fold assumed, so the two agreed with each other and neither agreed with the shader |

Two more are found and **not** fixed, both in this file's compositor and both stated there:
`SceneNormals` has no producer, because `ForwardPlus` writes one colour target — so the occlusion
passes march a zero normal; and nothing anywhere consumes an occlusion buffer, because no shader in
the library declares one. The frame that applies ambient occlusion is a frame whose shading pass
writes its ambient to a target of its own, which is a shader change and a plan entry.

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
  `!VisibilityBuffer`, named in the compositor
- [docs/plan/19](../../docs/plan/19-lighting-and-global-illumination.md) — `!GlobalDistanceField`,
  `!IrradianceField`, `!DistanceFieldAo` and `!IndirectDiffuse`, likewise. The seven lamps are what
  makes the bounced light visible in a scene that would otherwise have one sun
- Post FX — `!Bloom` and `!Tonemap`

Every one of those is a line in `Assets/Frame.vxcompositor`. None of it is assembled in C#, which is
the point: a frame that cannot be written down is a frame every host has to reimplement.

Licensed under Apache-2.0.
