---
title: Capturing a frame to a file
slug: rendering/capturing-a-frame
kind: guide
area: Rendering
summary: A headless run renders on the real GPU with no window and writes its last frame as a PNG, so a sample's picture is a file two people can produce at once rather than a screenshot of whoever's display was in front.
api: [T:Vixen.Core.Imaging.Bitmap, T:Vixen.Core.Imaging.PngCodec, L:13011, L:13026, L:13028, L:13029, L:13030]
tags: [rendering, headless, capture, screenshot, diagnostics, testing]
since: 0.1
status: preview
related: [rendering/reading-the-frame, rendering/timing-the-frame, engine/booting-an-application]
---

## What it is

Two flags and a file.

```
dotnet run -- --vixen-headless --vixen-frames 512 --vixen-capture ./shots
```

The run opens no window, renders five hundred and twelve frames on the real graphics device, and
writes the last one as `shots/frame.png`. Nothing is presented; the frame's last colour target — the
resource named by `GraphicsOptions.Output`, `SceneColour` by default — is copied into a host-visible
buffer once the queue has retired the frame, and encoded by `PngCodec`.

| Piece | What it is |
|---|---|
| `--vixen-capture <dir>` | Where the picture goes. Also what waives the no-surface refusal, below. |
| `--vixen-frames N` | Which frame. The captured one is the last, and without this there is no last. |
| `--vixen-fixed-step <s>` | How long each frame is *told* it took. Implied by `--vixen-capture` at 1/60. `0` puts the wall clock back. |
| `GraphicsOptions.CapturePath` | The same setting from `OnConfigure`, for a head that always captures. |
| `AppGraphics.RequestCapture(name)` | Asking for one frame by hand, if the loop is yours. |
| `Vixen.Core.Imaging.PngCodec` | Baseline 8-bit RGBA PNG, in about two hundred lines and no dependency. |
| `Vixen.Core.Imaging.Bitmap` | Width, height, and RGBA bytes. What the codec and the comparers speak. |

## What it is for

Three things a screenshot cannot do.

**It reproduces.** "Settle about fourteen seconds and grab the display" fights TAA convergence,
exposure adaptation and camera settling all at once, so two runs of the same commit disagree. An
agent working in this repository got the *sign* of a lighting change wrong that way, and blamed a sun
that does not move. A capture at frame *N* is the same frame every time.

**It can be done twice at once.** `screencapture` grabs a display, and there is one display. Two
headless runs share nothing, so a comparison between two commits is two commands rather than a
careful ritual with a stopwatch.

**It makes a sample's look testable.** Sample 13's picture is currently held only by whoever last
looked at it, and sample 03's README says as much about its own. A file is something a golden test can
compare.

### ⚠ It is not what a player sees

A headless frame is **not byte-identical to a windowed one**, and the guide would be lying if it
implied otherwise:

- There is no present, and therefore no compositor colour management at the end of it.
- The format is what `GraphicsOptions.Format` asked for rather than what a surface offered.
- The size is `GraphicsOptions.WindowlessSize`, or the headless window's — no display backing scale
  is involved, so the retina question that tasks #110 and #113 are about does not arise here.

For *is the grass black*, *did the shadow appear*, *is it washed out*, *did this commit change the
lighting* — it is strictly better than a screenshot, because it reproduces. For *what does a user
actually see*, the window remains ground truth.

A windowed capture is separately impossible today, for a reason worth knowing: `VulkanSwapChain`
creates its images with `ColorAttachmentBit | TransferDstBit` and no `TransferSrcBit`, so nothing can
copy out of a presented image at all. Headless sidesteps it rather than solving it — with no
swapchain, the frame's final colour target is an ordinary readable texture.

## Using it

### Headless means the Null device unless you ask for a picture

`--vixen-headless` on its own gives a window whose surface is `SurfaceKind.None`, and `GraphicsHost`
makes Vulkan **decline** a surface it cannot present to. That refusal is deliberate and load-bearing:
Vulkan opens perfectly happily with no surface, so a chain that simply tried it would hand a dedicated
server a real GPU device where it used to get the one that draws nothing — doc 17's server quietly
changing backend, noticed only as a machine that suddenly needs a driver.

`--vixen-capture` is the one statement specific enough to overrule it. Asking for a picture is asking
for a device that can draw one, and a capture written by the Null device would be a black PNG and a
passing run. So the flag does two things at once, and the log line says which device answered:

```
warn  Nothing will be presented: vulkan is drawing offscreen, because a capture was asked for
      and there is nothing to present to.
info  Graphics on Apple M1 Max (Integrated), 1600×900.
info  Captured the frame to ./shots/frame.png.
```

If that middle line names a software device instead, the picture is black and the run will still
exit zero.

### How many frames — many more than you would guess

**A capture reproduces at any frame count. It has not *converged* at most of them, and those are two
different things.**

Reproducibility is settled *for the simulation*, and the flag above is what settled it. A capture run
is handed a constant frame delta, so frame *N* is the same instant of simulated time on every run:
sample 13's player position and the whole 4×4 view-projection now match to the last printed digit
across two runs, where before they were about four pixels apart at frame 511 — a difference that
saturated any per-pixel diff taken across the pair.

⚠ **The picture is not settled everywhere, and where it is not is worth knowing before you measure
anything.** Two independent runs, same build, 256 frames, 1600×900:

| Viewpoint | Flipped pixels | Mean channel gap | Mean \|delta\| |
|---|---|---|---|
| The spawn corner (no `VIXEN_SPAWN`) | 36 of 1 440 000 | 0.0000 | 0.000009/255 |
| The grass field (`VIXEN_SPAWN=45,3,0,0`) | 640 000 – 880 000 | 0.13 – 0.42 | 0.97 – 1.18/255 |

The first is reproducible enough for a per-pixel diff. The second is not reproducible at all, and the
residue is not spread evenly — it sits on the GI-lit hillside, where a 200 × 150 block reaches a mean
absolute channel of **15/255** while the sky beside it stays at 0.3. `--vixen-workers 0` roughly
halves it; `--vixen-frame-limit 25` does not touch it, so it is not the host simply running ahead of
the device. The counters move with it: screen probes placed varies over 3788–3795 and virtual-shadow
resident pages over 247–254 between runs whose camera matrices are bit-identical.

**So: over ground with grass and screen-probe GI on it, quote a band statistic and give it a floor of
about half a percent of the mean channel. A per-pixel diff there is measuring the renderer's own
scheduling.** Over walls and sky, a per-pixel diff is sound.

⚠ **A thresholded count is far noisier than a mean, and it is the statistic people reach for.** Six
runs of one build at the same frame count and viewpoint, counting pixels that read as grass — green
above both red and blue by four — spread over 620 633 to 654 954: **±5 % about the mean**, against
±0.5 % for the mean channel of the same six frames. Anything smaller than that measured by counting
pixels in a band is not a measurement. If a hypothesis is about a one-percent effect, it needs a
region mean, a still camera, and the frame count held fixed.

Convergence is the slow one. Sample 13's frame carries a GPU cull, a surface cache, a screen-probe
gather and an exposure meter, and several of them keep device state across frames, so the picture
brightens for a long time. Measured on this repository's arena, from the spawn corner, as mean channel
over the whole frame:

| Frames | Mean channel | Moved since the row above | What it looks like |
|---|---|---|---|
| 4 | 9.2 | — | Silhouettes only. A shape against a slightly lighter sky. |
| 16 | 13.4 | 3.2 | The character is a smudge. Nothing is readable. |
| 64 | 23.6 | 7.6 | Walls, ground and character all legible; still much darker than the end. |
| 128 | 30.6 | 5.3 | Close. Material colours start to be distinguishable. |
| 256 | 35.8 | 3.9 | Recognisable as the game. |
| 512 | 37.4 | 1.3 | Where it is worth stopping. |

It roughly halves per doubling, which is an exponential approach with no cliff in it — so there is no
number at which the picture is finally "done", only one past which the next doubling buys less than
the golden suite's own whole-frame mean tolerance of 0.35/255. **Use 512 for a look, 64 for "did this
pass break", and never fewer than 16 for anything.**

⚠ Whatever you pick, keep it. An A/B between two commits at different frame counts measures the frame
count.

⚠ A picture from the spawn corner of a *correct* sample 13 frame looks almost entirely in shade even
at 512, because the spawn faces away from the sun. That is not a bug and has been mistaken for one.

### Where the picture comes from

The frame's last colour target, after tonemapping, antialiasing and every post effect. In sample 13
that is the `!Vignette` node named `Glass`, which writes `SceneColour` — a resource the document both
declares *and* has imported over it at run time, the import winning, which is what lets one document
build in a test against a scratch texture and in a game against the real target.

⚠ The copy is recorded on the frame's own command list before it is finished, and the read happens
after the queue has gone idle. They cannot be the same call: `IGraphicsDevice.Read` is immediate while
a recorded copy executes at submit, and a readback that races its own frame does not throw — it writes
a black or half-drawn PNG and reports success.

## Examples

### Two commits, side by side

```
git checkout HEAD~1 && dotnet build -c Release
dotnet run -c Release -- --vixen-headless --vixen-frames 512 --vixen-capture ./shots/before
git checkout - && dotnet build -c Release
dotnet run -c Release -- --vixen-headless --vixen-frames 512 --vixen-capture ./shots/after
```

Both may run at the same time, in two checkouts, on the same machine.

### A head that always captures

```csharp no-compile="a fragment; the override belongs to the project's Game subclass"
protected override void OnConfigure(AppConfig config) {
    config.Graphics.CapturePath = "artifacts/shots";
    config.MaxFrames = 512;
}
```

`AppConfig.Apply` runs before `OnConfigure`, so an operator's `--vixen-capture` is what a game sees
here and a game that hard-codes a directory wins — the order every other setting takes.

### Encoding a picture directly

```csharp no-compile="PngCodec.Save writes to the host filesystem, which a doc example should not do"
var image = new Bitmap(width, height, pixels);
var bytes = PngCodec.Encode(image);
```

`Encode` and `Decode` are the whole of the codec; `Load` and `Save` are the same two with a file
around them, for a caller whose path came from a command line rather than from a mount.

## See also

- [Reading the frame](reading-the-frame.md) — what each of the frame's resources holds.
- [Timing the frame on the GPU](timing-the-frame.md) — the same shape of flag, for cost rather than
  colour.
- [Booting an application](../engine/booting-an-application.md) — the `--vixen-*` arguments in full.
- `Platform/Vixen.Graphics.Golden.Tests` — the suite that has rendered offscreen and written PNGs
  since before this flag existed, and the reference implementation of half of it.
