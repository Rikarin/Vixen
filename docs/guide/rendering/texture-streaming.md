---
title: Streaming texture mip tails
slug: rendering/texture-streaming
kind: guide
area: Rendering
summary: The small mips are always resident and the large ones are pages that come and go under a byte budget, because KTX2 stores level data smallest first.
api: [T:Vixen.Core.Imaging.Ktx2Layout, T:Vixen.Core.Imaging.Ktx2Level, T:Vixen.Engine.Renderer.ITextureStreamSource, T:Vixen.Engine.Renderer.TexturePagePool, T:Vixen.Engine.Renderer.TextureStreamer, T:Vixen.Engine.Renderer.TextureStreamingQuality, T:Vixen.Engine.Renderer.AssetTextureStreamSource]
tags: [textures, streaming, memory, budget, ktx2]
since: 0.1
status: preview
related: [rendering/mesh-and-material, rendering/render-quality, rendering/terrain-rendering]
---

## What it is

A mip-tail streamer. Every streamed texture keeps its smallest levels resident always, and its large
levels are pages that arrive when something asks for them and are evicted when something else needs
the room. Four pieces:

- `Ktx2Layout` — what a KTX2 file's header and level index say, read without touching a pixel.
  `Ktx2.ReadLayout` parses `80 + 24n` bytes off the front of the file; `Ktx2.ReadTail` and
  `ReadTailAsync` then read levels *n* and smaller, which is one contiguous run.
- `TexturePagePool` — an `IPageStore` whose pages are fixed byte-size slices of a file's level data.
- `TextureStreamer` — the manager: a `PageResidency` over that pool, a byte budget, and the
  translation from "this texture should be *n* texels across" into pages to ask for.
- `AssetTextureSource` — the device side. It creates the image a resident tail decodes to and
  replaces it with a larger one as levels arrive.

## What it is for

Drawing a scene whose textures do not fit in video memory, at a quality that degrades with the
budget rather than falling over it. The budget is a hard ceiling: a request that cannot be met
without evicting something is either met by evicting something or refused and counted in
`TextureStreamer.Rejections`. A frame with a positive number there drew something blurrier than it
asked for, which is the designed behaviour.

You do not want it for a project whose textures fit. Streaming costs a host-memory copy of every
resident tail, a residency entry per page and an image swap whenever a texture changes resolution —
all of which a `PoolMegabytes` of zero avoids completely, and zero is the default.

## Using it

A host hands the numbers its quality tier resolved to, before content is mounted:

```csharp no-compile="a fragment; the renderer and the tier come from the host"
var tier = RenderQuality.Resolve(QualityTier.High);

renderer.Textures = new() { PoolMegabytes = tier.StreamingPoolMegabytes, MipBias = tier.MipBias };
renderer.Mount(assets);
```

`AppGraphics` already does exactly that, so an application that sets `GraphicsOptions.Quality` gets
it. ⚠ **Set it before `Mount`.** The pool is sized when the texture source is built and never
afterwards — a budget that could be resized would not be a budget.

That is all a project has to do. **A texture that was sampled this frame and that nobody sized wants
to be complete**, so a pool big enough for the scene draws exactly what the whole-file path drew, and
what makes it coarser under pressure is the budget and the least-recently-used order rather than a
heuristic nobody tuned.

A project that knows better narrows it, per frame. `TextureStreamer.WantedWidth` is the same
projected-size estimate mesh LOD selection uses:

```csharp no-compile="a fragment; bounds and view come from extraction"
var width = TextureStreamer.WantedWidth(bounds.Radius, distance, viewport.Y, camera.FieldOfView);

textures.Want(material.BaseColour, width);
```

⚠ **Nothing in the engine calls `Want` yet.** Extraction has no texture-to-bounds mapping to compute
one from — a material's textures are named, and which drawables use that material at what screen size
is a join that does not exist. Until it does, the budget is the only thing narrowing residency. This
is the seam a view-driven or feedback-driven signal replaces, and it is deliberately one method wide.

## Examples

**Reading a mip tail with no device and no asset system.** The claim is about the bytes it does
*not* read:

```csharp no-compile="a fragment; the file comes from wherever it comes from"
await using var stream = File.OpenRead("bark.ktx2");

var layout = await Ktx2.ReadLayoutAsync(stream);
var tail = await Ktx2.ReadTailAsync(stream, layout, layout.TailFor(64 * 1024));

// A complete smaller texture, not a larger one with holes: its level 0 is the file's `firstLevel`.
device.CreateTexture(new(tail.Format, tail.Width, tail.Height, TextureUsage.Sampled));
```

**Watching the budget hold.** Four textures and a pool that fits only their pinned pages:

```csharp no-compile="a fragment; `files` is an ITextureStreamSource"
using var streamer = new TextureStreamer(files, 4 * 64 * 1024);

foreach (var texture in textures) {
    streamer.Register(texture, layouts[texture]);
    streamer.Want(texture, 4096);
}

streamer.Service();

// Never over, however much was asked for — and Rejections says the pool is too small for the scene
// rather than that the streamer is broken.
Debug.Assert(streamer.ResidentBytes <= streamer.Budget);
```

## Design notes

**A page is a fixed byte-size slice of the level data, not a mip level.** A page per level is the
obvious mapping onto `IPageStore` and is unusable: `PageSize` is one number, mip levels differ by
factors of thousands, and `PageResidency` charges the budget `residentPages × PageSize` — so a 1×1
level would occupy and be billed for a slot sized for a 2048×2048 one. A pool per texture gives
every texture its own budget and its own eviction order, which is the failure `PageResidency` exists
to prevent, at a thousand textures rather than three systems. Fixed slices work because KTX2 stores
level data *smallest first*: a contiguous prefix of pages is exactly a complete mip tail, so "pages
0 to *k* are resident" and "levels *L* and smaller are resident" are the same statement.

**A partially streamed texture is a complete smaller image.** Allocating at full size and hiding the
missing levels behind a view's `baseMipLevel` or a sampler's `MinLod` saves no memory — the
allocation *is* the memory — and `baseMipLevel` is honoured for sampled bindings on Vulkan and
WebGPU but not on OpenGL, which binds the whole chain and reads from level zero whatever the view
said. Vulkan sparse residency would do it properly and exists on one backend of three. So the image
is replaced, which works everywhere and whose bytes are exactly the resident bytes.

**`MipBias` is applied to what is asked for, not to what is sampled.** `SamplerDescription.LodBias`
reaches the API on Vulkan alone: OpenGL drops it on every GLES profile and WebGPU has no such field.
A bias on the wanted width is arithmetic, works on every backend, and has the effect the knob is
named for — a positive bias asks for a coarser tail and frees the budget it would have taken.

**A texture that cannot be paged loads whole.** A compressed chunk has no slice of the mapped bundle
that is the payload, so it cannot be read by byte range; so can a container this cannot parse, or a
texture whose whole chain fits in one page. None of them is a reason for a material to sample the
fallback — the whole-file path handles every one and always did. That is what makes turning
streaming on a decision about memory rather than a decision about which content ships.

⚠ **The resident bytes exist twice.** The pool holds the tail in host memory, because a swap
re-uploads the whole tail and re-reading it from disk on every resolution change would put a file
read in the frame path. The GPU image is a second copy of the same bytes. `PoolMegabytes` is the
host figure; budget for roughly twice it.

⚠ **The `streaming: true` flag doc 08 specifies is not carried by the importer.**
`TextureImportSettings` has no such field and the KTX2 writer writes no key/value data, so there is
no channel from a `.meta` file to the runtime. What decides streamability today is size: a texture
whose level data exceeds one page is streamed, and one that does not is not.

## See also

- [Meshes and materials](mesh-and-material.md) — where a material's textures come from.
- [Render quality](render-quality.md) — the tier table `PoolMegabytes` and `MipBias` come from.
- [Drawing a terrain](terrain-rendering.md) — the other `IPageStore` consumer, with its own tiles.
