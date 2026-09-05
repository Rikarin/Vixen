---
title: Baking a material
slug: editor/material-baking
kind: guide
area: Editor
summary: How a texture graph's outputs become files the engine already understands — the nine usages and the seven files they land in, the ORM packing, PNG or KTX2, the .vxmat, the GUID dance, and the provenance block that stops a painted-over map being regenerated.
api: [T:Vixen.Editor.Assets.Materials.MaterialMapUsage, T:Vixen.Editor.Assets.Materials.MaterialMapTarget, T:Vixen.Editor.Assets.Materials.MaterialMapNaming, T:Vixen.Editor.Assets.Materials.MaterialMapImage, T:Vixen.Editor.Assets.Materials.MaterialBake, T:Vixen.Editor.Assets.Materials.MaterialBakeRecord, T:Vixen.Editor.Assets.Materials.MaterialProvenance, T:Vixen.Editor.Assets.Materials.MaterialBakeSet, T:Vixen.Editor.Assets.Materials.ProjectMaterialBaker, T:Vixen.Cli.TextureRunner]
tags: [editor, bake, materials, textures, material-authoring, texture-graph, cli]
since: 0.1
status: preview
related: [editor/mesh-map-assets, editor/texture-graph-evaluation, rendering/mesh-and-material, editor/index]
---

## What it is

A texture graph produces pictures. This is what turns them into a material: `MaterialBake` packs and
encodes them, `ProjectMaterialBaker` puts them in the project, `MaterialProvenance` records what made
them, and `MaterialMapNaming` is the vocabulary all three agree on. `vixen texture bake` is the same
code from a command line.

⚠ **The content build never evaluates a graph.** A bake happens on a person's machine, when they
press the button, and writes ordinary files into `Assets/` — from there it is an ordinary texture
asset with the existing importer, the existing `.meta`, the existing streaming and the existing block
compression. That is doc 48 § D4, and it is why determinism is a non-question: the artefact is a file
somebody can open, diff and paint on.

## The nine usages and the seven files

An `Output` node declares a **usage**. A bake writes **files**, and the two lists are not the same
length, because three usages share one file.

| Usage | File | What a material calls it |
|---|---|---|
| `baseColor` | `<name>_baseColor` | `baseColorMap` |
| `normal` | `<name>_normal` | `normalMap` |
| `occlusion` | `<name>_orm` — red | `ormMap` |
| `roughness` | `<name>_orm` — green | `ormMap` |
| `metalness` | `<name>_orm` — blue | `ormMap` |
| `emissive` | `<name>_emissive` | `emissiveMap` |
| `opacity` | `<name>_opacity` | `opacityMap` |
| `height` | `<name>_height` | — nothing samples it |
| `mask` | `<name>_mask` | — nothing samples it |

⚠ **The R, G, B order of the packed map is `TexturedOrmFeature`'s and not a preference.** That
feature reads occlusion from red, roughness from green and metalness from blue; a bake that packed
them in the order the enum happens to list them would produce a material that is shiny where it
should be occluded, and nothing anywhere would say so.

⚠ **A channel the graph did not produce is not zero.** A graph that outputs roughness alone still
needs the other two channels, and zeros there write a fully occluded conductor — a material that is
black and shades. The absent values are read off the runtime features' own defaults:
`OcclusionFeature.OcclusionMap` for occlusion, and `MetalRoughnessFeature`'s roughness and metalness
for the other two.

⚠ **Two of the nine bind to nothing, and are written anyway.** There is no textured height feature —
[#615](https://github.com/Rikarin/Vixen/issues/615) is that decision — so a height map is a file an
artist and a future feature can read and a material cannot. A mask is § 4.10's input to another graph
or to a layer stack, and a material never samples one.

⚠ **A material may not rename its maps.** `WorldRenderer.Paired` pairs one shader parameter with one
material-side name and keys that on the feature's *default*, so a renamed map resolves nothing, takes
slot zero, and is shaded by the bindless table's fallback checker — a lit surface whose shading is
merely wrong, on every device, with nothing reported. `MaterialMapNaming.Parameter` therefore reads
the names off the feature records rather than holding literals.

## PNG, or KTX2 above 2K

| Largest edge | File | Mips and compression |
|---|---|---|
| up to 2048 | `.png` | the sidecar names them; `TextureImporter` applies them at build time |
| over 2048 | `.ktx2` | already in the file; the importer copies a compressed source straight through |

⚠ **2048 is a PNG and 2049 is a container.** "Over 2K" is an exclusive ceiling, because 2K is the
size most texture sets are authored at and a ceiling that caught it would put the ordinary case on
the exceptional path.

⚠ **One table drives both paths.** `MaterialMapNaming.CompressionOf` names BC7 for the colour and
packed maps, BC5 for the normal map and BC4 for the one-channel maps, and that value is both what the
sidecar says below the limit and what the bake encodes above it. Leaving it at `Automatic` would give
an opacity mask four channels of BC7 below the limit and one channel of BC4 above it — a resolution
slider that changes the compression.

## The write

1. The name is sanitised, and the set's name is resolved against what is already in the folder.
2. Any output whose bytes are not what the last bake wrote stops the run — see below.
3. The maps are written, and the database is scanned.
4. Each map's GUID is read back and its sidecar is finished with the settings that say what it is.
5. The `.vxmat` is written, naming those GUIDs, and the database is scanned again.
6. The material's GUID is read back and the `texturing:` block is written into its sidecar.

⚠ **A scan rather than an import, and the difference is what mints the identity.** A file in
`Assets/` has no `AssetId` until the database has seen it and written a `.meta` beside it. That is
`ProjectMeshBaker`'s dance and § D11 names it as the sequence a material bake must use too.

⚠ **Two scans, and the second is not tidiness.** The `.vxmat` names its maps by `AssetId`, and those
ids do not exist until the maps have been scanned.

⚠ **Re-baking overwrites and keeps every GUID**, so every entity already pointing at the material
picks up the new maps.

⚠ **Which is why a set is keyed on its source and not on its name.** "The same graph again" and
"another graph called the same thing" produce identical file names, and keying on those turns the
correct behaviour above into a silent swap of one material's maps for another's, GUIDs and all — the
defect [#681](https://github.com/Rikarin/Vixen/issues/681) records against the mesh-map baker. A
second source baking under a taken name gets `<name>_2` and a warning.

⚠ **The bake owns the features and the textures; the shading model and the pass are the author's.** A
re-bake replaces what the graph says and keeps what it cannot say, so a material somebody switched to
`SubsurfaceShading` stays that way and a feature whose map the graph stopped producing goes.

## Provenance, and the painted-over check

The material's sidecar carries what doc 48 § D4 asks for:

```yaml
extensions:
  texturing.source: Assets/Materials/ship-hull.vxtexgraph
  texturing.sourceAsset: 6f1e…
  texturing.outputs: baseColor, normal, orm
  texturing.resolution: 2048
  texturing.parameter.rust: 0.6
  texturing.adapter: AMD Radeon RX 7900 XT
  texturing.digest.baseColor: sha256:…
  texturing.writtenDigest: sha256:…
  texturing.at: 2026-09-05T…
```

⚠ **Flat dotted keys rather than § D4's nested mapping.** A sidecar's extensions are a
`Dictionary<string, string>`, which is what `meshMap.usage` and every other extension in the tree
writes into. What the sketch is about is which facts are recorded, and all of them are.

⚠ **The adapter is recorded and never compared.** A re-bake on the same machine is byte-identical and
that *is* asserted; a re-bake on a different card is not, and pretending otherwise would make the
first artist with a different GPU a bug report.

⚠ **A file somebody painted over is refused, not regenerated.** Before writing anything, the bake
compares each output's bytes with the digest it recorded. A mismatch stops the run and names the
maps, because the most common reason for one is that a person painted on the file — and a bake that
overwrote it would destroy that work in the moment it looked like success. `--force`, or the panel's
equivalent, is how somebody says they meant it.

⚠ **The digest covers the maps and not the `.vxmat`.** A material an artist edited in the inspector
is not a painted-over map, and including it would make raising an emissive intensity look identical
to painting on a normal map.

⚠ **What "byte-identical" covers is the outputs and the material.** The sidecar carries the time the
bake ran, so two runs differ there by construction.

## From the command line

```bash
vixen texture bake --project . --from authored/ --name ShipHull
vixen texture bake --project . --from authored/ --name ShipHull --force
```

The inputs are named `<anything>_<usage>.png` — `hull_roughness.png`, `hull_baseColor.png` — and
everything else in the folder is ignored. Two files claiming one usage is refused rather than
resolved by enumeration order.

⚠ **The verb reads a folder of maps and does not evaluate a graph.** A `.vxtexgraph` is M4's document
and does not exist yet, so `--graph` would be the flag that parses and then apologises — the same
reason `remesh` has no `--bake`. Everything below the argument parsing is the code a panel will call,
so the graph arrives as a second way of filling the same dictionary rather than as a second baker.

⚠ **The input vocabulary is usages and the output vocabulary is files**, so re-reading a bake's own
output folder does not round-trip: `hull_roughness.png` is an input and `hull_orm.png` is an output,
because packing three inputs into one output is the work the verb exists to do.

## What is not here yet

- **A bake panel.** M5 is the write; the panel that drives it arrives with M4's document.
- **Evaluating a graph.** The seam is a dictionary of bitmaps by usage, and the evaluator fills it.
- **A height feature.** [#615](https://github.com/Rikarin/Vixen/issues/615).
