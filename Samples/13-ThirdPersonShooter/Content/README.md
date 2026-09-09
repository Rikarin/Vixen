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
| `concrete` | albedo, normal, orm, height | Walls, pillars, floor | all four — but **height by `wall.vxmat` alone** |
| `metal-panel` | albedo, normal, orm | Ramps — the one metallic surface | all three |
| `crate` | albedo, normal, orm | Cover crates | all three |
| `terrain-grass`, `terrain-rock`, `terrain-dirt` | albedo, orm | The terrain's painted layers | albedo; orm bound, unread |
| `grass-blade` | albedo (alpha), normal | `Outskirts.vxgrass` | albedo, **including its alpha** |
| `leaves` | albedo (alpha), normal | `Outskirts.vxfoliage` — the bushes | albedo, **including its alpha** |
| `bark` | albedo, normal, orm | — | nothing |
| `plaza` | splat | `plaza.vxmat`'s three painted layers | R, G and B — **and not the alpha** |

⚠ **`plaza-splat.png` is the odd one out and every sentence below about tiling stops applying to
it.** It is not a detail map: its content is *where on this surface layer 2 is*, so it is read once
across `arena-plaza.obj`'s 0..1 unwrap and it is the only map here that may not repeat. That is why
the plaza is a mesh of its own — `Content/boxuv.py` gives every other arena mesh metres of world over
a 2 m tile, so this map on `arena-floor.obj` would repeat thirty-two times per axis and place nothing,
and `SurfaceVertex` carries exactly one `TexCoord`, so it cannot ride a second uv set either.
`plaza.vxmat` blends three layers by it, which is doc 48's M11 remainder
([#1073](https://github.com/Rikarin/Vixen/issues/1073)) and the first material anywhere in the tree to
carry a `TexturedMaterialLayersFeature`.

⚠ **Three of its properties are load-bearing and each is asserted against the committed bytes by
`PaintedLayersTests`, because a map without them draws rather than fails.** Its alpha is 255 in every
texel — which is what a three-channel texture's alpha samples as anyway, so the material's
`paintedChannels: 3` is a true statement about it rather than a lucky one, and a fourth layer read off
that alpha would weigh 1 *everywhere* and become the whole surface. Its R, G and B sum to exactly 255,
so the shader's `1 / max(total, epsilon)` is a no-op and a texel painted zero in all three — which
would draw *black* rather than error — cannot occur. And each channel is pure somewhere, at three
48-texel squares the golden crops to compare a painted zone against a single-layer material.

⚠ **It is imported `content: Linear` and `compression: None`, and neither is tidiness.** A splat
weight is a number and not a colour: an sRGB view over the same bytes hands the shader 0 and 1
unchanged and every value between them transferred, which is a blend curve nobody authored. And BC7
endpoints would move a pure channel off 255, which is exactly the property the closed-form check
rests on.

⚠ **A material samples three textures now, and every arena material reads all three.**
`TexturedNormalMapFeature` and `TexturedOrmFeature` joined `TexturedMetalRoughnessFeature` in
`Core/Vixen.Rendering/Materials/MaterialFeatures.cs`, with a surface each beside
`TexturedMetalRoughnessSurface` in `Raven/Library/Material/MaterialSurface.rvn`. What used to be
"kept as the content a normal-map feature would need" is now the content that feature reads.

⚠ **`concrete-height.png` is the fourth map and the only one a single material reads.** It is not a
new field: it is the array `normal_map` already derives `concrete-normal.png` from, written out as
grey. `wall.vxmat` marches it with a `!ParallaxOcclusion` feature and the pillars and floor, which
sample the same albedo, normal and ORM, do not — so the arena carries its own A/B for the feature,
one surface apart, with the map held identical between them. Regenerating the whole set after this
change rewrote no existing byte, which is what the determinism claim above is for.

⚠ **The height map is what `MaterialMapTarget.Height` bakes, and its parameter name is not
`heightMap`.** `TexturedMaterialLayersFeature.HeightMap` is a *four-channel per-layer* bundle and
this is one material's single channel; the two features spell their material-side names differently
(`heightMap` and `parallaxHeightMap`) so that one texture cannot fill both bindless indices, which
would shade a surface and never fail. `wall.vxmat` says the same thing at the feature.

⚠ **`TexturedOrm` overrides the `roughness` and `metalness` a `.vxmat` sets rather than modulating
them** — the map's green and blue are the values, and the scalars beside them are multipliers on
those. And it reads the base albedo back out of the surface, so **the base feature must be authored
at metalness 0**: `ramp.vxmat` moved from `metalness: 0.9` to `0` and takes its 1.0 from
`metal-panel-orm`'s blue, which is the one number in the arena that adding these maps changed rather
than added to. At any other base metalness the albedo has already been split between the diffuse and
specular channels by a factor the ORM feature cannot see, and the surface draws darker with nothing
to say why.

⚠ **The terrain layers ship no normal map at all**, which is the difference between "unread" and
"unbindable". `TerrainRenderer.ResolveLayerTextures` resolves `Albedo` and `Surface` and stops;
`Terrain.rvn` declares `layerMaps` and `surfaceMaps` and no third array. There is nowhere for a layer
normal to arrive, so `make_textures.py` passes `normal=False` for those three rather than writing
three quarters of a megabyte into a bundle. Their `-orm` maps *are* bound, into `surfaceMaps`, and
the shader reads one channel of them — `.a`, and only under a height blend — so they are resident
and unread while `Outskirts.vxterrain`'s layers blend by weight.

⚠ **The alpha channels are read now, and that is what makes these cutouts.** `Grass.rvn` and
`Foliage.rvn` alpha-test against `alphaCutoff` (0.5) through one shared predicate — `GrassBlade.Cutout`
and `FoliageBase.Cutout` — which every fragment stage in each file calls, the velocity stages
included. That coupling is load-bearing rather than tidy: the velocity pass depth-tests against the
frame's finished depth, so a fragment the colour pass discarded shows the ground behind it, and a
velocity fragment surviving there writes the blade's motion over the ground's. The colour targets
also write **one** where they used to write `sampled.a`, which was a coverage mask sitting in an
opaque target's alpha beside a terrain that writes one.

⚠ **The built-in grass mesh is two meshes now, and the albedo picks between them.**
`GrassDrawPass.BuildBlade` is the tapered strip and `BuildCard` is a crossed quad pair carrying the
whole atlas; `GrassDrawPass.Blade` returns the card when an albedo is bound and the strip when none
is. Each is wrong exactly where the other is right — the strip's silhouette is its geometry, which
is what draws as a blade through the pass's white 1×1, and the card's silhouette is its alpha, which
needs a cutout and is a green slab without one. The strip's texcoord was also inverted for as long
as it existed: V runs downward from the first row of the image, so the root standing on the ground
is v = 1, and nothing noticed because nothing but a white 1×1 was ever bound to it.

⚠ **`leaves` sits in the bushes; `bark` still has nowhere to sit, and the reason is a binding.**
`Outskirts.vxfoliage`/`.vxfol` place 64 crossed-card bushes on the slope outside the walls —
`TerrainSeed.BuildFoliage` — so the leaves atlas is finally sampled. A stand binds **one** albedo
for its whole palette: `FoliageDrawPass.Albedo` is a single `Texture2D` and
`TerrainSceneRenderer.AlbedoOf` takes the first type in the palette that names one, so a second type
beside the bushes would draw through *their* map. A trunk therefore waits on a texture array indexed
by the palette slot the cull already writes into `FoliageCullParameters` — the shape is recorded on
`FoliageDrawPass.Albedo`, and nothing new has to travel to the draw for it.

⚠ **The grass has not been read as blades on a device, and the sample cannot currently show it.**
The field rasterises — an A/B at a temporarily inflated card width turns the ridge silhouette from a
clean line into a thick vegetated mass and back — but at the authored 0.6 m the only viewpoints this
sample allows put the field either in the arena's shade or out past its 55 m cull.

⚠ **The half of that paragraph about falling through the terrain is fixed and the rest is not.** It
used to read "the terrain carries no collider, so a `VIXEN_SPAWN` into the field falls through it and
`RespawnWhenBelow` cycles the camera below the surface", and that was true of the whole engine rather
than of this sample: `PhysicsShapes.HeightField`, `TerrainSamples.FillCollisionSamples` and the
editor's `ITerrainColliders` were all built and tested and nothing anywhere called them. See
`TerrainGroundSystem`, which is the missing call. A `VIXEN_SPAWN` outside the walls now lands on the
ground and stays on it — `VIXEN_SPAWN=0,4,-40,0` finishes at y = 1.3314, which is `TerrainSeed`'s own
arithmetic for that spot to four decimals.

What has *not* changed is the light: the ground outside the walls draws essentially black at every
viewpoint tried, which is the same absence the grass has and is being looked at separately. So the
blade silhouette still cannot be judged from here, for a different reason than before.

⚠ **The third map is ORM, not roughness alone**: occlusion in R, roughness in G, metalness in B.
One texture rather than three is the usual packing, and it matters here beyond tidiness — a
streaming pool is bounded in *pages*, so three maps per material would spend three times the
residency on the same information.

⚠ **Sized so streaming actually engages.** The 512² maps carry roughly 1 MiB of level data, which is
sixteen 64 KiB pages — enough for a mip tail to come and go. A 256² cutout is four pages and mostly
stays whole, which is the right answer for something whose silhouette is the point.
