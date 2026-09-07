---
title: Mesh maps as project assets
slug: editor/mesh-map-assets
kind: guide
area: Editor
summary: The bake panel a mesh-map bake is set up in, what the nine files are called, how a set is keyed on the model it came from, and how a generator finds one by usage rather than by path.
api: [T:Vixen.Editor.Assets.MeshMaps.MeshMapUsage, T:Vixen.Editor.Assets.MeshMaps.MeshMapNaming, T:Vixen.Editor.Assets.MeshMaps.MeshMapBake, T:Vixen.Editor.Assets.MeshMaps.MeshMapImage, T:Vixen.Editor.Assets.MeshMaps.MeshMapSet, T:Vixen.Editor.Assets.MeshMaps.IMeshMapBaker, T:Vixen.Editor.Assets.MeshMaps.MeshMapLibrary, T:Vixen.Editor.Assets.MeshMaps.MeshMapAsset, T:Vixen.Editor.App.ProjectMeshMapBaker, T:Vixen.Editor.App.MeshMapBakeSettings, T:Vixen.Cli.MeshMapRunner]
tags: [editor, bake, mesh-maps, assets, material-authoring, texture-graph]
since: 0.1
status: preview
related: [engine/map-baking, editor/retopology-and-uv-surfaces, editor/texture-graph-evaluation, editor/index]
---

## What it is

`MapBaker.Bake` measures nine things at every texel of a mesh's atlas and hands back arrays. This is
what turns those arrays into files: `MeshMapBake` encodes them, `IMeshMapBaker` puts them in the
project, `ProjectMeshMapBaker` is the editor's implementation of that, and `MeshMapNaming` is the
vocabulary all three and every future reader agree on.

The verb an artist reaches is **Assets ▸ Bake Mesh Maps…** (`assets.bake-mesh-maps`), also on the
content browser's context menu. It opens the **bake panel**, whose Bake button bakes the selected
model's first mesh that carries texture coordinates and writes up to nine PNGs into
`Assets/MeshMaps/`.

## What it is for

⚠ **The bake had no caller.** Doc 48 § D12's seven measurements landed on `BakedMaps`, were tested
against closed-form oracles on a sphere and a plane, and were reachable from nothing in the
repository — no importer, no content build, no editor. Everything here exists to be that caller.

⚠ **And the files are ordinary project assets, not a cache.** § D12 puts it in one sentence: an
artist wants to look at the curvature map when a generator misbehaves. A cache in `Library/` is
invisible in the browser, cannot be opened, cannot be referenced by a material, and is gone on the
next clean. So a bake writes into `Assets/`, each map gets a `.meta` sidecar, and each one has a GUID
the rest of the project can point at.

That is also why the verb is a verb rather than a setting on the model importer. An importer writes
artefacts into `Library/` under a cache key; a file it dropped into `Assets/` would be a file the next
scan imports, that the import it came out of never declared it read, and that no cache key can see —
a hidden cache with a re-entrancy bug on top.

## Using it

### The bake panel

§ D12 asks for a bake somebody chose. The panel is where the five things a bake is are set — the
resolution, the gutter, the search radius, the ray count, and which of the seven optional maps to
measure — and where what the last bake produced is read afterwards, file by file, with its warnings.

⚠ **One settings object, two views.** The menu line and the panel's button are the same bake with the
same numbers; the menu opens the panel rather than baking with constants of its own, because a verb
and a panel with separate settings is doc 20's A4 complaint — two answers to "what does Bake do", and
the one you get is whichever you used last.

⚠ **The normal and the displacement have no checkbox**, and it is not an omission: they are not in
`MeshMaps` at all. They fall out of the one ray the bake already casts, so a set is always at least
those two. The panel's own count says nine, not seven.

⚠ **Cancel stops the casting, and the bar is a fraction of it.** `MapBaker.Bake`'s five-argument
overload takes a cancellation token, checked once per texel row, and a callback told what fraction of
the rows have been cast; `ContentTasks.BakeMeshMaps` passes both. The row is the granularity because
per texel is a branch inside the hemisphere loop and per chart triangle is not a bound at all — one
quad can cover the whole atlas. ⚠ **The three-argument overload still cannot be stopped**, so anything
with a Cancel button wants the other one.

⚠ **The settings are the project's and are written as you change them.** `MeshMapBakeSettings` is a
`[DataContract("MeshMapBake")]` under `ProjectSettings/`, so a resolution somebody raised is still
raised next session and is the same on a teammate's checkout. ⚠ **The machine-preference argument
loses on purpose**: a ray count is a cost paid on this workstation, but these numbers decide the bytes
of nine PNGs that land in `Assets/` and get committed, so one project has to agree about them the way
it agrees about an import setting. ⚠ **The measurements are stored as usage names, never as the flags
integer** — a bitset written as a number comes back meaning something else the day a member is
inserted into the enum, and it looks like the editor forgetting rather than like a defect.

⚠ **A map you have painted on is not overwritten.** Each sidecar records `meshMap.digest` over the
bytes the bake wrote, and a re-bake that finds a file disagreeing with its digest refuses and names
the maps — because the usual reason for the mismatch is that somebody opened the curvature map and
fixed a seam by hand. **Overwrite painted maps**, beside the Bake button, is how you say you meant it;
the loss is then carried in the set's warnings rather than nowhere. A set baked before the key existed
records no digest and is overwritten, which is deliberate: a guard that fires on every project is a
guard people turn off. ⚠ **That tick is the one setting that is deliberately not persisted** — ticked
once for a good reason and remembered across a restart, it is the guard silently off for every later
bake.

### The naming, which is the part M8 depends on

A set is named after the mesh, and each map is that name, an underscore, and the usage's suffix:

| Usage | Suffix | File |
|---|---|---|
| `Normal` | `normal` | `Barrel_normal.png` |
| `Displacement` | `height` | `Barrel_height.png` |
| `AmbientOcclusion` | `ao` | `Barrel_ao.png` |
| `BentNormal` | `bent` | `Barrel_bent.png` |
| `Curvature` | `curvature` | `Barrel_curvature.png` |
| `Thickness` | `thickness` | `Barrel_thickness.png` |
| `Position` | `position` | `Barrel_position.png` |
| `WorldNormal` | `world` | `Barrel_world.png` |
| `Id` | `id` | `Barrel_id.png` |

⚠ **The sidecar is authoritative and the file name is the artist's convenience.** § 4.8's Mesh Map
Input binds *by usage* — that is what makes one generator compound work on every mesh — so every
sidecar carries an extensions block:

```yaml
guid: 3b1f…
importer: !TextureImporter
  content: Linear
  compression: None
  generateMips: false
extensions:
  meshMap.usage: curvature
  meshMap.mesh: Barrel
  meshMap.model: 7c41a0d29e5b4f1783ac6d0e2b9f5541
  meshMap.scale: 0.42
  meshMap.digest: sha256:9f2c…
```

A rename changes the file name and does not change what the map measures, which is doc 08's whole
argument about paths applied to a bake. `MeshMapNaming.TryParseFileName` is the other direction, for a
reader that has only a name; ⚠ it splits at the **last** underscore, so `Old_Barrel_ao.png` is a map
of `Old_Barrel` and not one of `Old` with an unknown usage.

⚠ **The mesh's name is what a caller suggests and the writer is what decides.** Two things happen to
it before it reaches a file:

1. **It is made safe.** A mesh is named by a person and Assimp hands it back verbatim, so `Wall/2` is
   a perfectly good object name and a directory that does not exist. `Write` sanitises — not `Bake`,
   which is the overload nothing in the editor calls.
2. **It is made unique to the model.** `meshMap.model` is the set's identity: a re-bake of the *same*
   model's *same* mesh overwrites and keeps every GUID, and another model's mesh with the same name
   lands beside it as `Cube_2` with a warning saying so. ⚠ `Cube` is Blender's default object name and
   every exporter's fallback, so before the key existed the second bake overwrote the first's pixels,
   inherited its GUIDs, and silently rebound every material reading them.

A set written before `meshMap.model` existed records no model, and the next keyed bake of that name
adopts it rather than landing beside it.

⚠ **The set a model owns is looked for before a free name is taken.** Suffix 1 is the first *free*
candidate and a free candidate is what a deleted or renamed neighbour leaves behind — so a bake that
took it would walk `Cube_2`'s owner back to `Cube`, mint nine fresh GUIDs, and orphan the set every
generator was bound to. Owning a stem is asked about every candidate before freedom is asked about
any, and the collision warning is only reported when a *new* set is displaced, not on every re-bake of
an already-displaced one.

### Reading one back — the resolver

`MeshMapLibrary` is the query the naming above exists to serve. `MeshMapLibrary.Index` walks the asset
database, opens the sidecar beside every indexed `.png`, and keeps the ones that carry
`meshMap.usage`; `TryResolve` then answers **(set, usage)** or **(model, usage)** with a
`MeshMapAsset` — the reference, the set, the model, the scale and the path.

⚠ **This is what a Mesh Map Input node calls**, and until it existed nothing in the repository read
`meshMap.usage` at all: the bake wrote nine files with a full sidecar each and the whole read side was
empty. `vixen mesh-maps list` is a second caller of the same index, so the answer an artist gets from
a terminal is the answer the node gets:

```console
$ vixen mesh-maps list --project . --set Barrel
Barrel  normal      3b1f…  Assets/MeshMaps/Barrel_normal.png
Barrel  height      9a02…  Assets/MeshMaps/Barrel_height.png  scale=0.42
Barrel  ao          c714…  Assets/MeshMaps/Barrel_ao.png
```

⚠ **A model with more than one set does not resolve by model alone.** Every mesh of a model has its
own nine maps, so "the normal map of this model" has as many answers as the model has meshes —
`TryResolve(model, usage, …)` returns false rather than the first, and `SetsOf` is what a caller names
a mesh from. Returning one silently is how a graph binds the barrel's lid on the machine the bake ran
on and the barrel's body on the next.

⚠ **A missing map and a missing scale are both ordinary.** Only the normal and the height map are
always baked, so a set baked with the ray-casting maps switched off genuinely has no occlusion map —
`TryResolve` says false, and a generator has to report that rather than sample nothing. And a
measurement whose range is zero — the curvature of a flat target — writes no `meshMap.scale`, so a
`Scale` of zero means "nothing was measured" rather than "the key was lost".

⚠ **The library is a snapshot.** It reads one sidecar per candidate file, so it is built once and is
stale the moment a bake writes; a caller rebuilds it afterwards, which is the same contract
`AssetDatabase.Scan` has with everything reading its index.

### What is in the pixels

⚠ **Nothing is compressed and nothing gets a mip chain.** A mesh map is an authoring input a
generator samples at atlas resolution, not a texture a surface minifies. § D12 demands it of the id
map in particular — an id is a label, the average of two labels is a third label, and a filtered id
map grows a hairline of a material that does not exist along every chart border — and making it the
rule for all nine means it is not a special case somebody later optimises away.

⚠ **The rows are flipped on the way out.** A `BakedMaps` array is row-major from the bottom left,
because that is where a texture coordinate's origin is; a PNG's first row is the top one.

⚠ **Two maps are signed and carry their scale.** `Displacement` and `Curvature` are measurements in
the model's own units, stored as `0.5 + 0.5·v/range`, so a reader recovers `v` as
`(sample·2 − 1) · meshMap.scale`. A range of zero writes a flat half and no scale key, which decodes
to zero everywhere — which is what was measured.

⚠ **An object-space normal map is declared `Linear`, not `NormalMap`.** That content means BC5 plus a
shader reconstructing Z as `+sqrt(1 − x² − y²)`, which is true of a tangent-space map and false of an
object-space one, whose Z is signed.

### The seam

`IMeshMapBaker` is declared in `Vixen.Editor.Assets` and implemented in `Vixen.Editor.App`, which is
the arrangement `IMeshBaker`/`ProjectMeshBaker` already has for doc 24's block-out bake: the thing
that wants a bake says what it wants, and the application, which owns the asset database, answers. It
is published as a service, so a plugin resolves it the way the block-out module resolves `IMeshBaker`.

⚠ **`Bake` and `Write` are separable on purpose.** A bake casts `OcclusionSamples` rays at every texel
and belongs on a pool thread; a write means `AssetDatabase.Scan`, which rewrites the index every panel
in the editor is reading. `ContentTasks.BakeMeshMaps` is the split in practice — arithmetic on the
pool, `Write` back on the frame thread, with the one-at-a-time guard held across *both* so that an
import cannot run over the folder while the files are going down.

⚠ **A map the database did not pick up stops the bake.** A `.meta` whose GUID cannot be read is a
file `AssetDatabase` leaves out of the index entirely — it refuses to mint a replacement, because a
new id would break every reference through the old one — so the read-back after the scan misses. The
bake refuses rather than recording a null reference for that usage: `MeshMapSet.Maps` is the by-usage
index a generator binds through, and a set that reports nine maps and resolves eight is a generator
reading nothing with nothing said anywhere. It is the same refusal `ProjectMaterialBaker` makes, one
asset type over.

⚠ **An encoded image does not carry a file name.** `MeshMapBake.Encode` produces pixels, a usage and
the import settings that usage needs; `Write` is what names the file, because naming needs the folder,
the sanitising and the model key, and none of those are known at encode time.

## Examples

Baking a mesh's maps into the project and pointing a material at the curvature one:

```csharp no-compile="needs an open project and its asset database"
var set = baker.Bake(model, "Barrel", highPoly, lowPoly, new BakeSettings {
    Resolution = 2048,
    Maps = MeshMaps.All,
    OcclusionSamples = 256
});

if (set.Maps.TryGetValue(MeshMapUsage.Curvature, out var curvature)) {
    material.Set("EdgeWearMask", curvature);
}
```

Finding a mesh's baked maps again, by usage rather than by path:

```csharp no-compile="needs an open project and its asset database"
var library = MeshMapLibrary.Index(project.Assets);

// ⚠ The sidecar and not the file name: a rename must not unbind a generator.
if (library.TryResolve("Barrel", MeshMapUsage.Curvature, out var curvature)) {
    material.Set("EdgeWearMask", curvature.Map);
}

// By the model, where the model has one set. Two and it refuses — name the mesh instead.
if (!library.TryResolve(model, MeshMapUsage.AmbientOcclusion, out var occlusion)) {
    report($"{model} has {library.SetsOf(model).Count} sets; say which.");
}
```

## See also

- [Map baking](engine/map-baking) — what the nine measurements are and how each one is made.
- [Retopology and UV surfaces](editor/retopology-and-uv-surfaces) — where the atlas being baked into
  comes from.
- [Texture graph evaluation](editor/texture-graph-evaluation) — what reads these maps afterwards.
