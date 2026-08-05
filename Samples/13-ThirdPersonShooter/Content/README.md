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

| Material | Maps | Used by |
|---|---|---|
| `concrete` | albedo, normal, orm | Arena walls |
| `metal-panel` | albedo, normal, orm | Floor and props — the one metallic surface |
| `crate` | albedo, normal, orm | Cover crates |
| `terrain-grass`, `terrain-rock`, `terrain-dirt` | albedo, normal, orm | The terrain's painted layers |
| `grass-blade` | albedo (alpha), normal | Scattered grass cards |
| `bark`, `leaves` | albedo (leaves alpha), normal | Foliage |

⚠ **The third map is ORM, not roughness alone**: occlusion in R, roughness in G, metalness in B.
One texture rather than three is the usual packing, and it matters here beyond tidiness — a
streaming pool is bounded in *pages*, so three maps per material would spend three times the
residency on the same information.

⚠ **Sized so streaming actually engages.** The 512² maps carry roughly 1 MiB of level data, which is
sixteen 64 KiB pages — enough for a mip tail to come and go. A 256² cutout is four pages and mostly
stays whole, which is the right answer for something whose silhouette is the point.
