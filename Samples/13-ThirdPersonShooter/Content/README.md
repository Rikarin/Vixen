# Generated sample textures

The PNGs in `../Assets/Textures` are produced by `make_textures.py`, not authored by hand and not
downloaded. Regenerate them with:

```
python3 make_textures.py ../Assets/Textures
```

Only `numpy` is needed — `gentex.py` writes the PNG container itself (IHDR/IDAT/IEND with zlib
scanlines) rather than depending on PIL or ImageMagick, so the sample's content can be rebuilt on
any machine that can run the engine's tests.

**Deterministic by construction.** Every noise field is seeded from a string through SHA-256, so the
same script produces the same bytes on every machine and a regeneration is a no-op in `git status`
unless the generator actually changed. That is what makes these safe to commit: a diff means
somebody edited the recipe.

## What is here, and why these

The **Read by** column is the honest one, and it is not the same as **Used by**. A map that reaches a
bundle and has no shader binding is bytes the content build imports, the bundle carries, the pool
makes resident and nothing samples.

| Material | Maps | Used by | Read by |
|---|---|---|---|
| `concrete` | albedo, normal, orm | Walls, pillars, floor | albedo, normal |
| `metal-panel` | albedo, normal, orm | Ramps — the one metallic surface | albedo, normal |
| `crate` | albedo, normal, orm | Cover crates | albedo, normal |
| `terrain-grass`, `terrain-rock`, `terrain-dirt` | albedo, orm | The terrain's painted layers | albedo; orm bound, unread |
| `grass-blade` | albedo (alpha), normal | `Outskirts.vxgrass` | albedo |
| `bark`, `leaves` | albedo (leaves alpha), normal | — | nothing |

⚠ **A material samples two textures now, and the second one is the normal.**
`TexturedNormalMapFeature` joined `TexturedMetalRoughnessFeature` in
`Core/Vixen.Rendering/Materials/MaterialFeatures.cs`, with `TexturedNormalMapSurface` beside
`TexturedMetalRoughnessSurface` in `Raven/Library/Material/MaterialSurface.rvn`. Each arena material
carries both features, so the `-normal` maps are read rather than merely resident.

⚠ **The `-orm` maps are still read by nothing.** `OcclusionFeature.OcclusionMap` is a `float`, and
no feature reads a packed occlusion/roughness/metalness texture — so occlusion, roughness and
metalness all come from the constants each `.vxmat` sets. That is the remaining half of the gap this
column was written to record.

⚠ **The terrain layers ship no normal map at all**, which is the difference between "unread" and
"unbindable". `TerrainRenderer.ResolveLayerTextures` resolves `Albedo` and `Surface` and stops;
`Terrain.rvn` declares `layerMaps` and `surfaceMaps` and no third array. There is nowhere for a layer
normal to arrive, so `make_textures.py` passes `normal=False` for those three rather than writing
three quarters of a megabyte into a bundle. Their `-orm` maps *are* bound, into `surfaceMaps`, and
the shader reads one channel of them — `.a`, and only under a height blend — so they are resident
and unread while `Outskirts.vxterrain`'s layers blend by weight.

⚠ **`grass-blade` has a seat; what it does not have is a mesh.** `GrassType.Albedo` and
`FoliageType.Albedo` name a texture, and `TerrainSceneRenderer` resolves both into
`GrassDrawPass.Albedo` and `FoliageDrawPass.Albedo` every frame — so `Outskirts.vxgrass` names
`grass-blade-albedo` and the field draws green instead of the pass's white 1×1. But the map is a
cutout *card* — seven blades on transparent — and the built-in blade is one tapered strip whose
texcoord runs 0…1 across itself, so the card's seven blades are squeezed across each strip. The
right green, the wrong framing. Closing it needs a blade-card mesh whose unwrap matches the atlas,
and an alpha-test discard: `Grass.rvn` writes `sampled.a` into an opaque target, so a transparent
margin draws as its background colour rather than as a hole.

⚠ **`leaves`/`bark` still have no volume to sit in.** The seat exists — `FoliageType.Albedo` — and
this sample places no `FoliageVolume`, so there is nothing to assign them to. Note that a stand
binds **one** albedo for its whole palette, so a volume mixing `bark` and `leaves` would need one
map or one volume each until the pass can bind a texture per type.

⚠ **The third map is ORM, not roughness alone**: occlusion in R, roughness in G, metalness in B.
One texture rather than three is the usual packing, and it matters here beyond tidiness — a
streaming pool is bounded in *pages*, so three maps per material would spend three times the
residency on the same information.

⚠ **Sized so streaming actually engages.** The 512² maps carry roughly 1 MiB of level data, which is
sixteen 64 KiB pages — enough for a mip tail to come and go. A 256² cutout is four pages and mostly
stays whole, which is the right answer for something whose silhouette is the point.
