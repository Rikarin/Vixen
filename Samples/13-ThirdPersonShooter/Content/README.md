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
| `concrete` | albedo, normal, orm | Walls, pillars, floor | albedo |
| `metal-panel` | albedo, normal, orm | Ramps — the one metallic surface | albedo |
| `crate` | albedo, normal, orm | Cover crates | albedo |
| `terrain-grass`, `terrain-rock`, `terrain-dirt` | albedo, orm | The terrain's painted layers | albedo; orm bound, unread |
| `grass-blade` | albedo (alpha), normal | — | nothing |
| `bark`, `leaves` | albedo (leaves alpha), normal | — | nothing |

⚠ **A material samples exactly one texture, and it is the base colour.**
`TexturedMetalRoughnessFeature.BaseColorMap` is the only texture parameter in
`Core/Vixen.Rendering/Materials/MaterialFeatures.cs`, and `TexturedMetalRoughnessSurface` the only
surface in `Raven/Library/Material/MaterialSurface.rvn` that reads one — `NormalMapFeature` carries a
constant `normalTS` and `OcclusionFeature.OcclusionMap` is a `float`. So the arena's `-normal` and
`-orm` maps are kept as the content a normal-map feature would need and are sampled by nothing today.

⚠ **The terrain layers ship no normal map at all**, which is the difference between "unread" and
"unbindable". `TerrainRenderer.ResolveLayerTextures` resolves `Albedo` and `Surface` and stops;
`Terrain.rvn` declares `layerMaps` and `surfaceMaps` and no third array. There is nowhere for a layer
normal to arrive, so `make_textures.py` passes `normal=False` for those three rather than writing
three quarters of a megabyte into a bundle. Their `-orm` maps *are* bound, into `surfaceMaps`, and
the shader reads one channel of them — `.a`, and only under a height blend — so they are resident
and unread while `Outskirts.vxterrain`'s layers blend by weight.

⚠ **`grass-blade` and `leaves`/`bark` have no seat.** `GrassType` carries a mesh reference and no
albedo, and nothing in `Vixen.Rendering.Terrain` ever assigns `GrassDrawPass.Albedo` or
`FoliageDrawPass.Albedo` — the `albedoMap` binding `Grass.rvn` and `Foliage.rvn` declare is left at
the pass's default view. They are kept because the gap is in the engine rather than in the content.

⚠ **The third map is ORM, not roughness alone**: occlusion in R, roughness in G, metalness in B.
One texture rather than three is the usual packing, and it matters here beyond tidiness — a
streaming pool is bounded in *pages*, so three maps per material would spend three times the
residency on the same information.

⚠ **Sized so streaming actually engages.** The 512² maps carry roughly 1 MiB of level data, which is
sixteen 64 KiB pages — enough for a mip tail to come and go. A 256² cutout is four pages and mostly
stays whole, which is the right answer for something whose silhouette is the point.
