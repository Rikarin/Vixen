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

⚠ **The black picture is the *loud* half of that failure, and it is not the half that costs you a
day.** A headless run without `--vixen-capture` runs no compute, so every counter that reads a result
back *off the device* — virtual shadow pages marked and drawn, screen probes placed, whatever a trace
file collects a row a frame of — comes back **zero**, for as many frames as you asked for, on every
branch alike, and exits zero.

⚠ **And the counters that do *not* read back off the device keep reporting a healthy frame**, which is
what makes it survive a glance. Objects drawn, shader variants, misses, material sets bound, draws
recorded: all of them count what the CPU submitted, and the CPU submits the same work to a device that
throws it away. So the run looks right in exactly the places an agent checks first. A branch that fixed
the thing and a branch that broke it measure identically. The flag is therefore not only for runs that
want a picture — it is what a *measurement* run asks for too, and the PNG is a receipt rather than the
point. Sample 13's toroidal shadow work was reported as unmeasurable-headless on exactly this evidence,
and the recipe had been right all along: the run had dropped the flag.

⚠ **`--vixen-gpu-profile` is the exception, and it is the dangerous one: it does not read zero.**
`NullDevice` reports `HasTimestampQueries = true` and `TryResolveQueries` answers with the pool's own
counter times a synthetic tick — deliberately, because resolving to zero would make every duration
zero and no test could tell that from a bug. The device's own remark says it: *"It is a shape, not a
measurement, and nothing should read a number out of this device and call it a frame time."* On a run
that meant to profile a real GPU, that is a pass timeline of **plausible, monotonic, entirely
invented milliseconds** — which will not look wrong, will not read as zero, and will survive being
pasted into a table. Check the device line before the timings, never the other way round.

**A capture-free way to ask for it is `--vixen-offscreen`.** It is the second statement of intent this
paragraph used to say was owed: a real device, no window, no picture, and nothing else changed. A
measurement run no longer pays for a PNG it does not read. ⚠ `--vixen-backend vulkan` under
`--vixen-headless` still does *not* lift the refusal and still throws — the refusal is what keeps a
dedicated server off a driver, so naming an API is not the same sentence as asking for a surfaceless
device, and it was deliberately not made one.

⚠ **And either flag now makes the fall-through to `null` a boot failure**, whatever the preference
list says. That closes the same hole one backend further down: before this, `--vixen-capture` on a
machine whose Vulkan would not load fell through the default order to the device that draws nothing
and wrote a black PNG under a startup log that read as success — the exact defect the flag exists to
prevent. There is no escape hatch on purpose: to say "a GPU if there is one, the device that draws
nothing if there is not", leave both flags off and write `--vixen-backend vulkan,null`.

### How many frames — many more than you would guess

**A capture reproduces at any frame count. It has not *converged* at most of them, and those are two
different things.**

Reproducibility is settled *for the simulation*, and the flag above is what settled it. A capture run
is handed a constant frame delta, so frame *N* is the same instant of simulated time on every run:
sample 13's player position and the whole 4×4 view-projection now match to the last printed digit
across two runs, where before they were about four pixels apart at frame 511 — a difference that
saturated any per-pixel diff taken across the pair.

⚠ **The picture is not settled everywhere, and where it is not is worth knowing before you measure
anything.** Six independent runs, same build, 256 frames, 1600×900, on the grass field
(`VIXEN_SPAWN=45,3,0,0`) — the worst viewpoint this repository has found:

| Statistic | Over six runs of one build |
|---|---|
| Flipped pixels, worst pair | 238 012 of 1 440 000 (16.5 %) |
| Flipped pixels, best pair | 4 932 of 1 440 000 (0.34 %) |
| Mean \|delta\|, worst pair | **0.071/255** |
| Whole-frame mean channel | spread **0.047 %** about the mean |
| Grass band — green above both red and blue by four | spread **0.25 %** about the mean |

The lake view (`VIXEN_SPAWN=0,0.2,-48,0`) came back **byte-identical** over the pair measured, and on
the viewpoint swept frame by frame every capture at 1, 2, 3 and 4 frames was byte-identical too.

⚠ **The cause is asynchronous asset loading, not the renderer's scheduling**, and that matters
because it says which frames are affected and which statistics survive. The material textures finish
loading and the streamer's mip swaps land on a *wall-clock* schedule, so at a fixed frame index two
runs of one binary hold different content. Sample 13 at frame 4 has 15 of 17 textures loaded and 15
swaps in **both** runs and the two PNGs are byte-identical; at frame 5 one run has 16 loaded and 16
swaps and the other has 17 and 17, and the foliage cards that are still waiting for their map draw as
untextured boxes, which is a structural difference in the picture and not a rounding one. Streaming
settles by about frame 64 (32 swaps, 0
in flight, identical in every run), but the difference injected between frames 5 and about 20 is then
carried indefinitely by the temporal chain: TAA history, the screen-probe and surface-cache
accumulators, and the exposure meter. Frame 8 is the proof of both halves — the streaming counters
already agree there and the pictures still differ by 21.9 % of pixels.

**It is discrete, not noise.** Four runs at frame 8 produced exactly *two* distinct pictures: three
were byte-identical to each other and the fourth differed from all three by the same 315 509 pixels.
The output takes one of a few values, decided by which frame a load happened to land on.

**Giving each frame more wall time narrows it, which is the test the diagnosis has to pass.** Three
runs at 64 frames throttled to 4 fps — a quarter-second a frame, so every pending load has time to
land — put two of the three **5 pixels apart out of 1 440 000**, against a best pair of 13 350 for the
same three runs at 60 fps. The third throttled run still landed in the other cluster, which is what
"discrete" means: throttling changes the odds, not the mechanism. ⚠ Five is not zero, so a second and
far smaller source is still unaccounted for — but it is four orders of magnitude below the one named
here, and nothing measured so far separates it from rounding.

The screen-probe gather is **not** implicated, despite being the obvious suspect: probes placed is
3 801 in all six runs above, and its readback is structurally safe — `Device.BeginFrame` waits on the
fence of frame *N* − `FramesInFlight` before the compositor builds, which is exactly the ring slot
`ScreenProbeGatherRenderer` reads. Virtual-shadow residency moves by at most one page (98–99 marked,
164–165 resident), downstream of the same content difference. `--vixen-workers 0` does not halve it,
and neither does running the job scheduler with a single worker.

**So: over ground with grass and screen-probe GI on it, quote the whole-frame mean channel or a band
count and give them a floor of about 0.05 % and 0.25 % respectively. A per-pixel diff or a per-pixel
maximum there is measuring the asset loader.** Over walls and sky, and on any frame before the
fifth, a per-pixel diff is sound.

⚠ **A thresholded count is noisier than a mean, and it is the statistic people reach for.** Of the
six runs above, the grass band spread 0.25 % against 0.047 % for the mean channel of the same six
frames — a factor of five, so a band count needs an effect five times larger to say the same thing.
Both are small enough that an ordinary content change is far above them: what is *not* measurable
this way is an effect under a quarter of a percent. If a hypothesis is about something smaller, it
needs a region mean, a still camera, and the frame count held fixed.

⚠ **Earlier editions of this page quoted 640 000 – 880 000 flipped pixels, a mean \|delta\| near
1/255, and a ±5 % band spread here.** Those were taken before samples 03/12/13 stopped defeating
`--vixen-headless` with `WithPlatform(new DesktopPlatform(…))`, so they were measured through a
windowed process contending for the display. The numbers above replace them and are between 3× and
20× tighter. **Re-measure the floor on the view you intend to use, on the build you intend to use it
on** — that is the standing instruction, and it is why this paragraph exists rather than a silent
edit.

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

### ⚠ A capture is of a still frame unless something drives the camera

Everything above is about a camera that does not move, and **the renderer's temporal half is not
exercised by one**. Reprojection is antialiasing's entire job; motion vectors are a target of zeroes;
motion blur is a copy; the fog's history has nothing to reproject; and the virtual shadow map refits
its finest level every 0.31 m of walking and never refits at all when nobody walks. Two
investigations into a reported shadow blink ended at "the mechanism most likely to produce this lives
under motion, and I could not measure it"; a third walked the same routes with a counter instead of a
camera and found it in the first five seconds — see below, and the sample's README.

Nothing in the host drives a camera, and that is deliberate — a scripted camera in `Vixen.App` is the
first half of a cutscene or a replay system. What the engine gives instead is
`Vixen.Engine.Players.IPlayerInputSource`, whose own documentation names a planner, a replay and a
test as the things that implement it. A scripted walk is one of those, in the game, in about two
hundred lines: `Samples/13-ThirdPersonShooter/ScriptedWalk.cs` is the reference implementation and
`VIXEN_WALK` is how a run asks for it.

⚠ **Whatever drives it must ride the fixed step.** A source that reads a `Stopwatch` makes the walk a
function of how fast the machine rendered, which is exactly the wall-clock non-reproducibility
`--vixen-fixed-step` was added to remove — and a second clock of exactly that kind was found driving
`TerrainSceneSource`'s grass wind, where it made the sway a function of process age.
`IPlayerInputSource.Sample` is handed the frame's delta; that is the only time it may use.

⚠ **Measure the walking floor before concluding anything from a walking diff.** It is not the still
floor, and neither is a constant — see the table above and the sample's README for the numbers this
repository has measured. On this repository's arena the walking floor is the **tighter** of the two,
which is the opposite of what was expected: the floor turns out to be a property of what is on screen
rather than of whether the camera moved, and the walking route ends in grass while the still pose looks
at built surfaces.

⚠ **How much tighter depends on the statistic, so a single ratio is not a thing to carry around.**
Ten headless runs of one build from one start pose — four walking, six still — put the walking view at
0.117 % spread on the whole-frame mean channel against the still view's 0.487 %, and at 0.077 % on a
band count against 4.95 %. That is a factor of four and a factor of sixty out of the same ten runs.
An earlier edition of this paragraph said "about six times", from one pair of runs each, through the
windowed process, and while a character carrying `PhysicsInterpolation` walked at half the speed its
`CharacterMovement` asked for — an engine defect the same harness found and which is now fixed. **Take
the numbers from `Samples/13-ThirdPersonShooter/README.md`, and re-measure on the view you intend to
use.**

⚠ **The still floor on built surfaces is well outside the floors this page quotes above.** Those were
measured on the grass field; the arena's still view came back ten times looser on the mean channel and
twenty times looser on the band, over six runs whose every reported counter — probes placed, pages
marked and resident, textures loaded, mip swaps — was identical. The counters agreeing is not evidence
the pictures do.

### Two frames of one run, rather than two runs

`--vixen-capture` writes the last frame, so frame *N* and frame *N* + 1 are two whole runs — and two
runs differ by the renderer's own scheduling as well as by the frame step. For a *temporal* question
that is the wrong instrument: over grass and screen-probe GI the cross-run residue reaches a mean
absolute channel near 1/255, which is half of what one frame of walking produces.

`AppGraphics.RequestCapture(name)` is the public way to ask for one frame by hand, and a loop over it
is a strip: many frames of **one** run, sharing a schedule, a streaming state and a probe history, so
their difference is the frame step and nothing else. `VIXEN_STRIP=first-last[/stride]` in sample 13 is
thirty lines of exactly that, and the stride is what reaches a multi-second timescale — a strip of
thirty consecutive frames is half a second, and a defect somebody describes in seconds will not be in
it.

### ⚠ When the picture is the wrong instrument entirely

**A renderer that degrades gracefully cannot be measured by looking at it.** The virtual shadow map is
the sharpest case in this repository: a page the table does not answer falls through to
`ClusteredShading`'s cascades, which render, and render plausibly. So a frame whose shadow map
answered *nothing* is a frame that looks fine, every counter the host reports — pages marked, pages
drawn, pages resident — reports success while it happens, and a whole-frame delta between two walking
frames is dominated by the camera step. Two investigations into a reported shadow blink measured
pictures and found health; the defect was there the whole time.

What settles it is a counter of what the pass *could not answer*, taken at the moment the data it
reads stops changing — `VirtualShadowAtlas.AnsweredPages` and `AbsentPages`, counted at the page
table's upload, and `VIXEN_VSMTRACE=<file>` in sample 13 to write them a row a frame. The blink came
straight out of that as ninety frames in the first five seconds of walking against a dozen after
fifteen, and the cause out of the invalidation counter beside it. See the sample's README.

The generalisation is worth stating: **before capturing a picture of a subsystem, ask what its
failure looks like.** If the answer is "like a slightly different correct picture", no number of
captures will separate the two, and the thing to build is the counter.

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

### ⚠ Two ways a head takes `--vixen-headless` away, and only one of them shows

The ordering above cuts both ways, and both mistakes were in this repository's own samples until
2026-08-19.

The visible one is `AppBuilder.WithPlatform`. A platform handed to the builder is used *ahead* of the
`IPlatformFactory`, deliberately — an Android activity, a UIKit view controller and the editor's play
mode all own a platform the host cannot make — so a head that supplies one never reaches
`PlatformHost.Create`, and `AppConfig.Headless` is parsed, stored and never read. Four desktop
samples did this to request a Vulkan surface at window creation, which
`DesktopPlatformOptions.RequestGpuSurface` has defaulted to for as long as the option has existed. A
`--vixen-headless` run logged `on Desktop (macOS)` and opened a window.

The quiet one is `config.Window = new() { …, IsVisible = true }`. It reads like the same bug and is
not: by the time those options are used the platform has already been chosen from the same flag, and
a `HeadlessWindow` has no picture whatever `IsVisible` says — the only effect is a synthetic
`WindowShown` nothing consumes. It is still worth writing `IsVisible = !config.Headless`, because a
line that silently overrides an operator is one the next reader has to disprove.

`Tools/Vixen.App.Tests/HeadlessFlagTests` gates both, by scanning `Samples/` for either shape.

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
- `build/Build.SampleFrame.cs` — the gate that runs this recipe on every push (`nuke SampleFrame`,
  the `sample-frame` leg in `ci.yml`). It is also where the assertions that can tell a real device
  from the Null one are written down, and why "not a software device" is the wrong one to make.
