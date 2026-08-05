---
title: Streaming texture mip tails
slug: rendering/texture-streaming
kind: guide
area: Rendering
summary: The small mips are always resident and the large ones are pages that come and go under a byte budget, because KTX2 stores level data smallest first.
api: [T:Vixen.Core.Imaging.Ktx2Layout, T:Vixen.Core.Imaging.Ktx2Level, T:Vixen.Engine.Renderer.ITextureStreamSource, T:Vixen.Engine.Renderer.TexturePagePool, T:Vixen.Engine.Renderer.TextureStreamer, T:Vixen.Engine.Renderer.TextureStreamingQuality, T:Vixen.Engine.Renderer.TextureDemand, T:Vixen.Engine.Renderer.AssetTextureStreamSource]
tags: [textures, streaming, memory, budget, ktx2]
since: 0.1
status: preview
related: [rendering/mesh-and-material, rendering/render-quality, rendering/terrain-rendering]
---

## What it is

A mip-tail streamer. Every streamed texture keeps its smallest levels resident always, and its large
levels are pages that arrive when something asks for them and are evicted when something else needs
the room. Five pieces:

- `Ktx2Layout` — what a KTX2 file's header and level index say, read without touching a pixel.
  `Ktx2.ReadLayout` parses `80 + 24n` bytes off the front of the file; `Ktx2.ReadTail` and
  `ReadTailAsync` then read levels *n* and smaller, which is one contiguous run.
- `TexturePagePool` — an `IPageStore` whose pages are fixed byte-size slices of a file's level data.
- `TextureStreamer` — the manager: a `PageResidency` over that pool, a byte budget, and the
  translation from "this texture should be *n* texels across" into pages to ask for.
- `TextureDemand` — what says *n*. It surveys the frame's visible drawables once, takes the maximum
  over every user of a texture, and quantises the result onto the ladder of mip widths with a dead
  band.
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

That is all a project has to do. `WorldRenderer.Mount` builds a `TextureDemand` alongside the texture
source, and `WorldRenderer.Draw` runs it — so **what drives residency is what the camera can see**.

## What decides a texture's size

Once a frame, at the top of `WorldRenderer.Draw`, before the wants are turned into page requests:

1. **Per visible drawable, per screen-size view.** For every live object of the mesh feature that
   culling kept, `TextureStreamer.WantedWidth(bounds, view, screenHeight)` — the same projected-size
   estimate mesh LOD selection uses, `radius × (1 / tan(fov / 2)) × viewportHeight / distance` — is
   `max`ed into a slot for the object's material. Views whose `ScreenHeightScale` is zero are
   skipped: a shadow cascade and a probe face have no viewport height to measure a texel against, and
   `LodRenderFeature` skips them for the same reason.
2. **Per material, out to its textures.** `AssetMaterialSource.TexturesOf` says which files a
   compiled material samples, and the material's width is `max`ed into each of them.
3. **Per texture, onto a rung.** The raw width is quantised to a power of two and handed to
   `AssetTextureSource.Want`.

The screen height comes from `SceneRenderHost.FrameSize`, so a window that resizes needs no wiring.

⚠ **`max`, not the last one seen.** A texture is shared by everything painted with it, so the want is
the largest any of its users asked for. Taking whichever drawable the object list ended with would
make a near wall blurry because a distant rock shares its albedo — and would change answer whenever an
unrelated entity was created, because that is what reorders the list.

⚠ **A texture with no visible user is not asked for at all**, rather than asked for at zero. Not
asking is what makes it degrade: an untouched page ages in the least-recently-used order and is the
next thing evicted when something in front of the camera needs the room. Re-asking at the old width is
exactly how a texture that has left the view never gives its pages back.

⚠ **One frame stale.** The survey reads `RenderSystem.Visibility`, which the compositor fills during
`SceneRenderHost.Draw` — so it sees the previous frame's cull. A page takes many frames to arrive, so
a signal one frame behind the camera is exact enough, and the alternative is running the survey after
the frame is recorded, where the wants could not reach that frame's uploads.

### The dead band

A swap is a fresh image, a fresh view and a whole mip tail copied up. A wanted width that flipped
either side of a mip level's width would pay that on alternate frames for ever, so `TextureDemand`
keeps the rung it is on until the width leaves a band around it — `Hysteresis`, an eighth, between
`LodRenderFeature`'s tenth and `GrassResidency`'s 0.15, and exactly representable in binary so the
band's edges are the same numbers on every machine.

The rungs are powers of two **because the levels are**: what a want decides is a mip level, and
`TextureStreamer` compares the wanted width against `Width >> level`. Quantising to a power of two
puts the band around the same number the swap decision turns on rather than near it. A jump of more
than one rung — a camera that teleported — is taken whole, because the condition is written against
the current rung and clears it by a wide margin.

`TextureDemand.Promotions` and `Demotions` are how many rung moves there have been; a scene standing
still should add nothing to either.

### Turning it off, or replacing it

A project that wants no view-driven signal leaves `WorldRenderer.Host.FrameSize` unset, or sets
`TextureDemand.ScreenHeight` to zero: the survey then sizes nothing and every texture falls back to
**a texture that was sampled this frame and that nobody sized wants to be complete**, which is what
the whole-file path drew. Zero is the safe direction on purpose — a survey that ran with no pixels
would ask for zero texels, and zero texels means the *smallest* level.

A project with a better signal calls `AssetTextureSource.Want` itself; it is idempotent, holds for
one frame, and takes the maximum of everything said in that frame. This is the seam a
feedback-buffer signal replaces, and it is deliberately one method wide.

⚠ **Textures no material of `AssetMaterialSource`'s paints are not surveyed.** Terrain layers reach
the streamer through `AssetTerrainTextures`, particle materials through a second
`MaterialRenderFeature`, and a project's own `IMaterialSource` through neither. Each of those falls
into the "sampled and not sized" branch and wants to be complete, exactly as before. The survey
narrows what it can see and silences nothing.

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

⚠ **`PoolMegabytes` is the host figure, and the bytes exist twice.** The pool holds the tail in host
memory, because a swap re-uploads the whole tail and re-reading it from disk on every resolution
change would put a file read in the frame path; the GPU image is a second copy of the same bytes. So
a project setting `textures.streamingPoolMegabytes` to 2048 should expect roughly 4 GiB of texture
memory in total, and **the number it typed is the host half of it**.

The two are reported separately rather than folded into one budget, because they are not equal: a
tail whose pages have arrived but whose swap was refused for staging (`StreamingRefusals`) is large
in the pool and small on the device, and one that arrived and was then evicted is the other way
round. `TextureStreamer.ResidentBytes` is the host number and `AssetTextureSource.StreamedImageBytes`
is the device number. Making the budget cover both was the alternative and was rejected twice over:
it would silently halve what every tier value already shipped in `RenderQuality` means, and it would
state an approximation as a promise.

**Streamability is decided by size, and there is no authored flag.** A texture whose level data
exceeds one 64 KiB page is streamed and one that does not is loaded whole. Doc 08 once sketched a
`.meta` `streaming: true`; it has been withdrawn from that sketch rather than implemented, because
there is no channel for it — `TextureImportSettings` has no such field and `Ktx2.Write` writes
`kvdByteLength = 0` — and building one is a permanent format commitment across four assemblies for a
decision size already makes correctly. `streaming: true` on a small texture is what already happens,
since page 0 covers a chain under 64 KiB whole; `streaming: false` on a large one does not give an
author what they would reach for it for, because it moves the texture out of a bounded pool into an
unbounded whole-file load, which is what the pool exists to stop. The control that claim actually
wants is a *pin* on the streamer, and nothing has asked for one.

## See also

- [Meshes and materials](mesh-and-material.md) — where a material's textures come from.
- [Render quality](render-quality.md) — the tier table `PoolMegabytes` and `MipBias` come from.
- [Drawing a terrain](terrain-rendering.md) — the other `IPageStore` consumer, with its own tiles.
