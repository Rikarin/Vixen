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

✅ **It builds and it runs.** `dotnet build` performs the whole chain — `vixen import`, the C#
compile, `vixen content build`, the copy beside the binary — from a clean checkout, and
`--vixen-frames N` runs headless with no warnings and no validation errors:

```
Compositor Assets/Frame.vxcompositor loaded.
Content mounted from /app/Content: 70 addresses.
Loaded scene 'Arena' with 31 entities.
Built 19 collider(s) from the level's authored boxes, over 24 registered shape(s).
Rebuilt 'Assets/Frame.vxcompositor' with the distance field, the probe field and the virtualized path in it.
Loaded 6 sound(s); 0 were not published.
Player 0 spawned at (-20, 0.2, -20), possessing its pawn.
Ran 60 frame(s). The player finished at (-20, -5.96e-08, -20), Walking, having fired 0 shot(s) and respawned 0 time(s).
```

⚠ **That last line is the assertion, and a frame count is not.** "It started and stopped" is true of a
game that loaded no level, spawned nobody and simulated nothing — which is exactly what every failure
in this project's history looked like. The player spawns at y = 0.2 and the floor's top is y = 0, so
finishing *`Walking` at zero* means the character was stepped, found the collision the level authored,
and settled on it.

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
| ✅ | The full `dotnet build` chain and a headless `--vixen-frames N` run |
| ⬜ | Wiring `--vixen-frames` into CI as a gate — no sample is run headlessly in CI today, so this is a pattern to establish rather than a row to fill in |

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
