---
title: Mesh maps as project assets
slug: editor/mesh-map-assets
kind: guide
area: Editor
summary: Where a mesh-map bake is invoked from in the editor, what the nine files are called, and how a generator finds one by usage rather than by path.
api: [T:Vixen.Editor.Assets.MeshMaps.MeshMapUsage, T:Vixen.Editor.Assets.MeshMaps.MeshMapNaming, T:Vixen.Editor.Assets.MeshMaps.MeshMapBake, T:Vixen.Editor.Assets.MeshMaps.MeshMapImage, T:Vixen.Editor.Assets.MeshMaps.MeshMapSet, T:Vixen.Editor.Assets.MeshMaps.IMeshMapBaker, T:Vixen.Editor.App.ProjectMeshMapBaker]
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

The verb an artist reaches is **Assets ▸ Bake Mesh Maps** (`assets.bake-mesh-maps`), also on the
content browser's context menu. It bakes the selected model's first mesh that carries texture
coordinates and writes nine PNGs into `Assets/MeshMaps/`.

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
  meshMap.scale: 0.42
```

A rename changes the file name and does not change what the map measures, which is doc 08's whole
argument about paths applied to a bake. `MeshMapNaming.TryParseFileName` is the other direction, for a
reader that has only a name; ⚠ it splits at the **last** underscore, so `Old_Barrel_ao.png` is a map
of `Old_Barrel` and not one of `Old` with an unknown usage.

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
pool, `Write` back on the frame thread.

## Examples

Baking a mesh's maps into the project and pointing a material at the curvature one:

```csharp no-compile="needs an open project and its asset database"
var set = baker.Bake("Barrel", highPoly, lowPoly, new BakeSettings {
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
static Dictionary<MeshMapUsage, AssetEntry> Bound(EditorProject project, string mesh) {
    var found = new Dictionary<MeshMapUsage, AssetEntry>();

    foreach (var entry in project.Assets.Entries) {
        var sidecar = AssetMetaFile.PathFor(project.Paths.Absolute(entry.Path));

        if (!File.Exists(sidecar)) {
            continue;
        }

        var extensions = AssetMetaFile.ReadFile(sidecar).Extensions;

        // ⚠ The sidecar and not the file name: a rename must not unbind a generator.
        if (extensions.TryGetValue(MeshMapNaming.MeshKey, out var owner)
            && owner == mesh
            && extensions.TryGetValue(MeshMapNaming.UsageKey, out var suffix)
            && MeshMapNaming.TryParseSuffix(suffix, out var usage)) {
            found[usage] = entry;
        }
    }

    return found;
}
```

## See also

- [Map baking](engine/map-baking) — what the nine measurements are and how each one is made.
- [Retopology and UV surfaces](editor/retopology-and-uv-surfaces) — where the atlas being baked into
  comes from.
- [Texture graph evaluation](editor/texture-graph-evaluation) — what reads these maps afterwards.
