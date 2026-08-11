#!/usr/bin/env python3
"""Generates the sample texture set. Deterministic: same seeds, same bytes."""

import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from gentex import fbm, normal_map, normalise, rgba, tint, value_noise, write_png

OUT = sys.argv[1] if len(sys.argv) > 1 else "."
SIZE = 512
CUTOUT = 256


def grid_lines(size, cells, width=0.03):
    """Panel seams: 1 away from a seam, 0 on it."""
    t = (np.arange(size, dtype=np.float32) + 0.5) / size * cells
    d = np.minimum(t % 1.0, 1.0 - (t % 1.0))
    x, y = np.meshgrid(d, d, indexing="xy")

    return np.clip(np.minimum(x, y) / width, 0.0, 1.0)


def streaks(size, cells, seed, vertical=True):
    """Anisotropic noise — grain, brushing, fibre."""
    fine = value_noise(size, cells, seed)
    coarse = value_noise(size, max(2, cells // 8), seed + "/c")
    band = fine * 0.45 + coarse * 0.55

    return np.repeat(band[:, :1], size, axis=1) if not vertical else np.repeat(band[:1, :], size, axis=0)


def emit(name, colour, height, rough, metal=False, alpha=None, strength=2.0, normal=True):
    files = []
    files.append((f"{name}-albedo.png", rgba(colour, alpha)))

    # ⚠ `normal=False` for the three terrain layers, and it is not a saving of taste. A terrain
    # layer's normal map has nowhere in the engine to arrive: `TerrainLayerDescription.Normal` is
    # written and read back by `TerrainStore` and never resolved —
    # `TerrainRenderer.ResolveLayerTextures` asks for `Albedo` and `Surface` and stops — and
    # `Terrain.rvn` declares `layerMaps` and `surfaceMaps` and no third array. Generating one would
    # be three quarters of a megabyte the content build imports, the bundle carries and no shader can
    # sample. The mesh materials keep theirs; see Content/README.md for what reads what.
    if normal:
        files.append((f"{name}-normal.png", rgba(normal_map(height, strength))))

    # Occlusion in R, roughness in G, metalness in B — the ORM convention, in that order, which is
    # what `TexturedOrmSurface.Compute` reads and what Content/README.md states. This line used to
    # say "roughness in R … green left for occlusion", describing neither the stack below it nor any
    # reader: the first channel is a flat 1 because these materials carry no baked occlusion, and a
    # comment that names it roughness makes the ramp's authored 0.34 look like the one being
    # overridden when it is the green that overrides it.
    orm = np.stack([np.ones_like(rough), rough, np.full_like(rough, 1.0 if metal else 0.0)], axis=-1)
    files.append((f"{name}-orm.png", rgba(orm)))

    total = 0
    for filename, data in files:
        total += write_png(os.path.join(OUT, filename), data)

    print(f"{name:16} {total / 1024:7.1f} KiB  ({len(files)} maps)")


# ── arena ───────────────────────────────────────────────────────────────────

def concrete():
    speckle = fbm(SIZE, 32, 4, "concrete/speckle")
    blotch = fbm(SIZE, 3, 3, "concrete/blotch")
    height = normalise(speckle * 0.7 + blotch * 0.3)

    # Stains pull the value down without changing hue much: concrete weathers grey.
    colour = tint(normalise(blotch * 0.6 + speckle * 0.4), (0.26, 0.26, 0.27), (0.62, 0.62, 0.61))
    rough = 0.72 + 0.18 * normalise(speckle)

    emit("concrete", colour, height, np.clip(rough, 0, 1), strength=1.4)


def metal_panel():
    seams = grid_lines(SIZE, 4, 0.02)
    brushed = streaks(SIZE, 256, "metal/brush", vertical=True)
    grime = fbm(SIZE, 6, 3, "metal/grime")

    height = normalise(seams * 0.8 + brushed * 0.2)
    base = 0.38 + 0.10 * brushed - 0.10 * grime
    colour = tint(np.clip(base, 0, 1), (0.18, 0.19, 0.21), (0.55, 0.57, 0.60))
    colour *= (0.45 + 0.55 * seams)[..., None]

    rough = np.clip(0.28 + 0.34 * grime + 0.10 * (1.0 - seams), 0, 1)

    emit("metal-panel", colour, height, rough, metal=True, strength=3.0)


def crate():
    planks = grid_lines(SIZE, 1, 0.5)
    bands = np.floor((np.arange(SIZE, dtype=np.float32) / SIZE) * 5.0)
    bands = np.repeat(bands[:, None], SIZE, axis=1)
    seam = np.clip(np.abs(((np.arange(SIZE) / SIZE) * 5.0) % 1.0 - 0.5) / 0.06, 0, 1)
    seam = np.repeat(seam[:, None], SIZE, axis=1)

    grain = streaks(SIZE, 192, "crate/grain", vertical=True)
    knots = fbm(SIZE, 8, 3, "crate/knots")
    perplank = (bands * 37.0 % 7.0) / 7.0

    height = normalise(grain * 0.5 + seam * 0.5)
    warm = np.clip(0.35 + 0.35 * grain + 0.22 * perplank - 0.20 * knots, 0, 1)
    colour = tint(warm, (0.20, 0.12, 0.06), (0.62, 0.44, 0.24))
    colour *= (0.55 + 0.45 * seam)[..., None]

    emit("crate", colour, height, np.clip(0.62 + 0.25 * knots, 0, 1), strength=2.2)


# ── terrain layers ──────────────────────────────────────────────────────────

def terrain_grass():
    clumps = fbm(SIZE, 12, 4, "tgrass/clump")
    blades = value_noise(SIZE, 128, "tgrass/blade")
    height = normalise(clumps * 0.6 + blades * 0.4)

    mask = np.clip(clumps * 0.65 + blades * 0.35, 0, 1)
    colour = tint(mask, (0.07, 0.13, 0.04), (0.30, 0.44, 0.14))

    emit("terrain-grass", colour, height, np.full((SIZE, SIZE), 0.86, np.float32), strength=1.6, normal=False)


def terrain_rock():
    plates = fbm(SIZE, 7, 5, "trock/plate", gain=0.62)
    chips = value_noise(SIZE, 96, "trock/chip")
    height = normalise(plates * 0.75 + chips * 0.25)

    colour = tint(np.clip(plates * 0.7 + chips * 0.3, 0, 1), (0.22, 0.21, 0.20), (0.58, 0.57, 0.55))

    emit("terrain-rock", colour, height, np.clip(0.60 + 0.25 * plates, 0, 1), strength=3.2, normal=False)


def terrain_dirt():
    grit = fbm(SIZE, 40, 4, "tdirt/grit")
    clods = fbm(SIZE, 9, 3, "tdirt/clod")
    height = normalise(grit * 0.55 + clods * 0.45)

    colour = tint(np.clip(clods * 0.6 + grit * 0.4, 0, 1), (0.16, 0.11, 0.07), (0.46, 0.35, 0.23))

    emit("terrain-dirt", colour, height, np.full((SIZE, SIZE), 0.90, np.float32), strength=1.8, normal=False)


# ── vegetation ──────────────────────────────────────────────────────────────

def grass_blade():
    """Vertical blades on transparent, the shape a cutout card samples."""
    size = CUTOUT
    x = (np.arange(size, dtype=np.float32) + 0.5) / size
    y = (np.arange(size, dtype=np.float32) + 0.5) / size
    xx, yy = np.meshgrid(x, y, indexing="xy")

    alpha = np.zeros((size, size), dtype=np.float32)
    shade = np.zeros((size, size), dtype=np.float32)

    rng = np.random.default_rng(7)
    for blade in range(7):
        root = 0.08 + 0.84 * (blade + 0.5) / 7.0
        lean = float(rng.uniform(-0.16, 0.16))
        wide = float(rng.uniform(0.016, 0.030))
        tall = float(rng.uniform(0.62, 0.98))

        # yy is 0 at the top of the image; the blade grows from the bottom.
        up = np.clip((1.0 - yy) / tall, 0.0, 1.0)
        centre = root + lean * up * up
        half = wide * (1.0 - up) ** 0.7

        inside = (np.abs(xx - centre) < half) & (up < 1.0)
        alpha = np.maximum(alpha, inside.astype(np.float32))
        shade = np.where(inside, 0.25 + 0.75 * up, shade)

    colour = tint(np.clip(shade, 0, 1), (0.06, 0.14, 0.03), (0.40, 0.62, 0.18))
    height = alpha * 0.5

    files = [
        ("grass-blade-albedo.png", rgba(colour, alpha)),
        ("grass-blade-normal.png", rgba(normal_map(height, 1.2))),
    ]
    total = sum(write_png(os.path.join(OUT, f), d) for f, d in files)
    print(f"{'grass-blade':16} {total / 1024:7.1f} KiB  ({len(files)} maps)")


def bark():
    # Vertical, because that is the way a trunk grows and the way its UVs run.
    fibre = streaks(SIZE, 220, "bark/fibre", vertical=True)
    ridges = fbm(SIZE, 5, 4, "bark/ridge")
    cracks = np.clip(1.0 - np.abs(normalise(fbm(SIZE, 3, 4, "bark/crack")) - 0.5) / 0.06, 0, 1)
    height = normalise(fibre * 0.5 + ridges * 0.35 - cracks * 0.4)

    lit = np.clip(fibre * 0.5 + ridges * 0.5 - cracks * 0.55, 0, 1)
    colour = tint(lit, (0.08, 0.06, 0.04), (0.50, 0.38, 0.25))

    emit("bark", colour, height, np.clip(0.72 + 0.20 * ridges, 0, 1), strength=3.6)


def leaves():
    """A leaf cluster on transparent."""
    size = CUTOUT
    x = (np.arange(size, dtype=np.float32) + 0.5) / size
    xx, yy = np.meshgrid(x, x, indexing="xy")

    alpha = np.zeros((size, size), dtype=np.float32)
    shade = np.zeros((size, size), dtype=np.float32)

    rng = np.random.default_rng(11)
    for leaf in range(14):
        cx, cy = rng.uniform(0.15, 0.85), rng.uniform(0.15, 0.85)
        rx, ry = rng.uniform(0.07, 0.14), rng.uniform(0.04, 0.08)
        angle = rng.uniform(0.0, np.pi)

        dx, dy = xx - cx, yy - cy
        ux = dx * np.cos(angle) + dy * np.sin(angle)
        uy = -dx * np.sin(angle) + dy * np.cos(angle)

        inside = (ux / rx) ** 2 + (uy / ry) ** 2 < 1.0
        alpha = np.maximum(alpha, inside.astype(np.float32))
        shade = np.where(inside, 0.30 + 0.60 * (1.0 - np.abs(uy) / ry), shade)

    colour = tint(np.clip(shade, 0, 1), (0.05, 0.12, 0.03), (0.34, 0.55, 0.16))

    files = [
        ("leaves-albedo.png", rgba(colour, alpha)),
        ("leaves-normal.png", rgba(normal_map(alpha * 0.6, 1.4))),
    ]
    total = sum(write_png(os.path.join(OUT, f), d) for f, d in files)
    print(f"{'leaves':16} {total / 1024:7.1f} KiB  ({len(files)} maps)")


for step in (concrete, metal_panel, crate, terrain_grass, terrain_rock, terrain_dirt, grass_blade, bark, leaves):
    step()
