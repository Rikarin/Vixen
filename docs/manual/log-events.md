# Log event ids

Every `[LoggerMessage]` in the engine carries a stable numeric `EventId`. This file is the register.

**Why this exists.** A number in a support ticket, a crash dump or a player's log is greppable and
survives the message text being reworded. Without a register, ids get picked at random, collide, and
stop meaning anything — which is the state most projects are in, because allocating them is a
five-minute job that is easy to skip until it is too late to fix.

## Rules

- **An id is permanent.** Once shipped it never changes meaning and is never reused, even if the log
  line it named is deleted. Keep the row and say so in its *Since* cell — `0.1.0, retired in 0.3.0` —
  so an old log still decodes and nobody hands the number to something new.
- **One id, one call site.** Two `[LoggerMessage]` methods sharing a number make the register
  ambiguous the first time somebody greps for one in a support log, which is the only moment it is
  worth anything. `Vixen.DocGen.Tests` asserts this on every build, because the rules above were
  followed by all three branches that independently allocated 13026 and only their union was wrong.
- **The message text may change freely.** The id is the contract; the wording is not.
- **The level may change.** A warning that turns out to be noise can become `Debug` without a new id.
- **Add the entry in the same commit as the log line.** A register updated later is a register that
  is wrong.
- **`0` means unassigned** and is what an un-annotated call site gets. Anything logging with event 0
  in a shipping build is a bug — and now a costly one: `LogRateLimiter` treats
  (category, event id) as a message's identity, so every un-annotated call site in a category shares
  one budget with all the others.

**These are not `EventSource` event ids.** `EventSourceSink` publishes the whole log as the
`Vixen-Diagnostics-Log` provider, whose six events are one per level — so
`dotnet-trace collect --providers Vixen-Diagnostics-Log` filters by verbosity, and the id below
arrives as the first field of the payload rather than as the ETW event number.

## Ranges

Allocated per subsystem, a thousand apiece, so a subsystem never has to come back for more and an id
identifies its origin on sight.

| Range | Subsystem | Status |
|---|---|---|
| 1 000 – 1 999 | `Vixen.Core.*` — services, assets of the foundation, allocators | reserved |
| 2 000 – 2 999 | `Vixen.Graphics`, backends | **in use** |
| 3 000 – 3 999 | `Vixen.Shaders`, Raven integration | reserved |
| 4 000 – 4 999 | `Vixen.Rendering`, `Vixen.Rendering.PostFx` | **in use** |
| 5 000 – 5 999 | `Vixen.Ecs`, `Vixen.Engine` | reserved |
| 6 000 – 6 999 | `Vixen.Assets`, content pipeline | reserved |
| 7 000 – 7 999 | `Vixen.Ui.*` | **in use** |
| 8 000 – 8 999 | `Vixen.Platform.*` | reserved |
| 9 000 – 9 999 | `Vixen.Physics`, `Vixen.Audio`, `Vixen.Animation`, `Vixen.Input` | **in use** |
| 10 000 – 10 999 | `Vixen.Net.*` | reserved |
| 11 000 – 11 999 | `Vixen.Editor.*` | reserved |
| 12 000 – 12 999 | `Vixen.Raven` — the compiler's own diagnostics are `RVNxxxx`, not these | reserved |
| 13 000 – 13 999 | `Vixen.App` — the host and the app heads | **in use** |
| 14 000 – 14 999 | `Samples/*` — the samples, which use the same generated call sites the engine does | **in use** |
| 15 000 – 15 999 | `Vixen.Video` and its codecs | reserved |
| 16 000 – 16 999 | `Vixen.Xr` and its backends | **in use** |
| 27 000 – 27 999 | `Vixen.Live.*` — the online service tier, numbered after docs/plan/27 | **in use** |

The jump from 16 to 27 is deliberate and is the one range not allocated in order: the service tier
took the number of [its own design document](../plan/27-mmo-framework.md), so a line from a shard in
somebody's cluster names the document that explains what a shard is. 17 000 – 26 999 stays free for
the subsystems between.

## Allocated ids

### `Vixen.Graphics` and its backends

| Id | Level | Message | Since |
|---|---|---|---|
| 2001 | Warning | The Vulkan validation layers were asked for and are not installed | 0.1.0 |
| 2002 | Warning | The validation layer was found but would not load; the instance was created without it | 0.1.0 |
| 2003 | Information | `Vulkan device created on '{Adapter}' ({Kind}, Vulkan {ApiVersion}) using {RenderPath}; validation {ValidationEnabled}.` | 0.1.0 |
| 2100 | Information | `WebGPU device created on '{Adapter}' ({Kind}, {Driver}), {Mode}.` — on the web three of those four are "unknown", and knowing they are unknown rather than unlogged is the useful part | 0.1.0 |
| 2101 | Warning | `WebGPU reported an error the backend could not attribute to a call: {Message}` — WebGPU has no return codes, so this callback is the only place a failure appears | 0.1.0 |
| 2102 | Debug | `WaitIdle did nothing: this WebGPU surface cannot block on the queue.` — a browser tab has one thread and blocking on it is a deadlock | 0.1.0 |
| 2103 | Warning | `wgpu-native or Dawn could not be loaded ({Reason})` — no desktop OS ships one, so this is ordinary and selection moves on | 0.1.0 |
| 2104 | Warning | `WebGPU device lost ({Reason}). Everything has to be recreated.` | 0.1.0 |

### `Vixen.Rendering` — the render system

**Nothing here is logged per frame.** Every line below describes a frame that drew and quietly drew
less than it was asked for — the class of wrongness no exception ever reaches, and therefore the
class that has to be a log line rather than a counter nobody reads. What keeps it affordable is that
`PageResidency.Service` compares two longs when the frame is healthy and formats nothing, and reports
at most one line per five seconds when it is not.

⚠ **The range is a prefix, not one assembly.** `Vixen.Rendering.Terrain` and `Vixen.Rendering.PostFx`
draw from these thousand ids too, which is why 4003 is a terrain line and is not in the 13 000 range
the host owns. The table above is the authority; the assembly a line comes from is not.

**A renderer that quietly draws something else is worse than one that refuses.** 4003 and 4004 are
both that shape: a required input was absent, a designed-in fallback took over, the frame drew, and
every counter stayed healthy — so the picture looked like a different bug entirely. Both are said
*once per degrade* rather than per frame, because what a reader wants is a cause and a cause does not
change sixty times a second.

| Id | Level | Message | Since |
|---|---|---|---|
| 4001 | Warning | `The page pool refused {Refusals} request(s): {Resident} of {Capacity} page(s) are resident and {Pinned} of those are pinned, so there was nothing left to evict.` — the frame drew something coarser than it asked for, which is designed behaviour and still worth seeing | 0.1.0 |
| 4002 | Error | `{Refusals} pinned page(s) could not be given a slot: {Pinned} of {Capacity} page(s) are pinned already.` — error rather than warning because it is permanent: a refused request is a coarser frame and the next frame asks again, and the only thing that pins is a registration that has already happened | 0.1.0 |
| 4003 | Warning | `'{Node}' is drawing the ground with the preview shaders because {Missing}.` — `Vixen.Rendering.Terrain`, said once per degrade. The preview fragment returns a reflectance in [0, 1] rather than a luminance in cd/m², so under a physically metered sky the ground is roughly one nit in a frame exposed for thousands: **black ground under a correct sky**, at every viewpoint and every hour, with `TerrainsDrawn` reporting it drawn. `{Missing}` names which of the three inputs was absent — the lighting camera, the published cascades, or the shadow atlas resource | 0.1.0 |
| 4004 | Warning | `SceneLighting.Camera is null, so nothing wrote the froxel grid's half-tangents or planes for pass '{Pass}'.` — said once per degrade, by the first shading pass that notices. ⚠ The consequence is *not* zeros: `ClusteredShading.rvn` declares `tanHalfFov = float2(1, 0.5625)`, `nearPlane = 0.1` and `farPlane = 1000`, which is a 16:9 camera at ninety degrees horizontal — a plausible grid for a camera nobody has | 0.1.0 |

### `Vixen.Ui.Reactive` — the signal graph

| Id | Level | Message | Since |
|---|---|---|---|
| 7001 | Error | `The effect declared at {Origin} re-triggered itself {Runs} times in one flush and has been suspended.` | 0.1.0 |
| 7002 | Error | `The effect declared at {Origin} threw and has been suspended.` | 0.1.0 |
| 7003 | Warning | `An effect flush hit its budget of {Budget} runs with work still queued.` | 0.1.0 |

### `Vixen.Ui` — the cascade's refusals

`UiDocument` drains `StyleSheetLoader.Diagnostics` and `SelectorCompiler.Diagnostics` onto these two
ids after every load and every reload. Both lists were public and, outside the repository's own
tests, unread — so any CSS Vixen did not understand used to vanish without a word. The category is
whatever the host names when it builds the document; `Vixen.Editor.App` files them under
`Vixen.Ui.Styling`, deliberately apart from the editor's own `Vixen.Editor`, so that "the styling is
wrong" is one filter in the Console panel.

⚠ **`LayoutStyleBuilder.Diagnostics` is the third list of the same shape and is still unread** — it
is produced inside the per-element pass rather than at load, so it needs a drain point in
`UiDocument.Update`. Tracked as #56; it will use 7004 rather than an id of its own, because it is
the same event with a different source.

| Id | Level | Message | Since |
|---|---|---|---|
| 7004 | Warning | `{Source} refused '{Text}': {Reason}.` — an at-rule, a selector or a declaration the cascade dropped. The rule stays in the sheet and does nothing, which is why silence was expensive | 0.1.0 |
| 7005 | Warning | `An @apply could not be expanded: {Reason}.` — a utility name that is not one, or one carrying a variant. The declarations it stood for are simply absent from the block | 0.1.0 |

### `Vixen.Audio` and its backends

The 9 000 range is shared by four gameplay subsystems, so it is subdivided a hundred at a time:
physics 9 000, audio 9 100, animation 9 200, input 9 300. A hundred ids is more than any of them will
ever need and the block boundary makes the owner obvious from the number.

**Nothing here is logged from the audio thread.** A log call takes locks, formats strings and may
write to a file; a callback that did any of those would drop out. The render path counts and
`AudioEngine.Update` reports once a frame, which is also why several of these say "since the engine
started" rather than "just now".

| Id | Level | Message | Since |
|---|---|---|---|
| 9100 | Information | `Audio on {Backend}: {Device}, {SampleRate} Hz, {Channels} ch, {BufferFrames}-frame blocks.` | 0.1.0 |
| 9101 | Warning | `No audio device on {Backend} ({Reason})` — the mixer runs against nothing, so voices still start and finish | 0.1.0 |
| 9102 | Warning | `{Dropped} play requests were dropped: all {Capacity} voices were busy.` | 0.1.0 |
| 9103 | Warning | `Audio streaming fell behind {Underruns} times` — a track played silence while its decoder caught up | 0.1.0 |
| 9104 | Warning | `The audio device reported {Underruns} underruns.` | 0.1.0 |
| 9105 | Error | `The audio render threw and the block was silenced.` — an exception onto a driver's callback thread would take the process with it | 0.1.0 |
| 9110 | Warning | `OpenAL could not be loaded ({Reason}).` — the backend reports itself unavailable and selection moves on | 0.1.0 |
| 9111 | Error | `The OpenAL pump thread threw and has stopped; the process keeps running and is silent.` | 0.1.0 |

### `Vixen.App` — the host

| Id | Level | Message | Since |
|---|---|---|---|
| 13001 | Information | `Vixen {Variant} on {Platform}, {Workers} workers.` | 0.1.0 |
| 13002 | Warning | `No window: {Reason}` — the desktop platform was wanted and headless was used | 0.1.0 |
| 13003 | Warning | `LOOSE CONTENT — reading from {Path} instead of bundles.` (docs/plan/17 Q5b) | 0.1.0 |
| 13004 | Warning | `Unrecognised engine argument {Argument} — it was ignored.` | 0.1.0 |
| 13005 | Information | `Stopping after {Frames} frames.` | 0.1.0 |
| 13006 | Critical | `The frame loop threw and the application is stopping.` | 0.1.0 |
| 13007 | Information | `Content mounted from {Root}: {Addresses} addresses.` | 0.1.0 |
| 13008 | Information | `No content: {Reason}` — ordinary for a sample or a tool, and the line that makes "my asset was not found" a five-second diagnosis | 0.1.0 |
| 13009 | Warning | `LOOSE CONTENT — still reading from {Path} instead of bundles.` — repeated every 60 s, per docs/plan/17 Q5b | 0.1.0 |
| 13010 | Information | `Graphics on {Adapter} ({Kind}), {Width}×{Height}.` | 0.1.0 |
| 13011 | Warning | `Nothing will be presented: {Reason}` — a warning even though it is exactly what a dedicated server wants, because a head that asked for a window and is drawing into nothing has to say so. ⚠ It used to end "The frame runs against the Null backend", which stopped being true when a capture gained the right to open an offscreen Vulkan device that draws the whole frame | 0.1.0 |
| 13012 | Information | `Shaders: {Variants} baked variants.` | 0.1.0 |
| 13013 | Information | `No baked shaders: {Reason}` — ordinary for a project that has not captured a manifest yet, and the line that turns "every material draws as a miss" into a build step somebody has not run | 0.1.0 |
| 13014 | Error | `The graphics device was lost. Nothing more will be drawn this run.` | 0.1.0 |
| 13015 | Information | `Compositor {Address} loaded.` | 0.1.0 |
| 13016 | Warning | `Compositor {Address} was not loaded ({Reason}) — the built-in frame is being used.` | 0.1.0 |
| 13017 | Warning | `The compositor declares no stage called {Stage}, so nothing in the world will be drawn.` — the failure that otherwise draws an empty window and reports nothing | 0.1.0 |
| 13018 | Information | `Startup scene {Address} loaded: {Entities} entities.` | 0.1.0 |
| 13019 | Warning | `The startup scene {Address} was not loaded ({Reason}) — the world is empty.` — something asked for a level, so an empty window has a reason nothing else in the log would give | 0.1.0 |
| 13020 | Information | `Remote content: {Bundles} downloadable bundle(s), cached under {Cache}.` — the line that turns a first-run stall into an explanation, and says where the space went | 0.1.0 |
| 13021 | Information | `Unpacked content: chunks read from the artefact store at {Root}, with nothing bundled.` — doc 17's Editor variant; a run whose content came from somebody's `Library/` has to be identifiable in a log attached to a bug report | 0.1.0 |
| 13022 | Warning | `{Finding}` — one of the render graph's lint findings, said once per distinct finding; every one describes a frame that draws and quietly wastes or discards work | 0.1.0 |
| 13023 | Information | `Look profile {Source} applied.` — which layer supplied it, the document's inline one or the host's | 0.1.0 |
| 13024 | Warning | `Look profile {Address} was not loaded ({Reason}) — the frame keeps its neutral values.` | 0.1.0 |
| 13025 | Information | `GPU pass timing requested: {Attached} on '{Adapter}'.` — worth a line in both directions, because a profiled frame is not the frame that ships and an unsupported one is an empty timeline with no reason for it | 0.1.0 |
| 13026 | Information | `The window asked for {PointWidth}×{PointHeight} points and the display scale is ×{Scale}, so the frame is {PixelWidth}×{PixelHeight} — {Factor}× the pixels.` — 13010 reports the result; this one reports that the result is not what was asked for, which on a retina display is four times the pixels and rather more than four times the screen-space cost | 0.1.0 |
| 13027 | Information | `Diagnostic overlays on: {Panels} panel(s), {Commands} console command(s).` — a build with the switch on and zero commands is a console that answers `help` and nothing else, worth knowing before somebody types a subsystem's verb and concludes the subsystem is broken | 0.1.0 |
| 13028 | Information | `Captured the frame to {Path}.` — `--vixen-capture`; a picture nobody was told about is one its operator cannot tell from a capture that failed | 0.1.0 |
| 13029 | Warning | `--vixen-capture was given without --vixen-frames, so there is no last frame to capture and nothing will be written to {Path}.` — the one way to ask for a picture and correctly get none, said at startup while somebody is still watching | 0.1.0 |
| 13030 | Information | `The clock is fixed at {Milliseconds} ms a frame, so frame N is the same instant on every run and no wall time reaches the simulation.` — `--vixen-fixed-step`, or a capture implying it; a frame handed a constant delta is measuring nothing about this machine, and a reader who does not know that will quote a frame time from a run that had none | 0.1.0 |

Every other range is still reserved rather than allocated: the subsystems that will log have not been
written, and the ranges exist so that when they are, nobody has to invent a numbering scheme under
deadline.

<!--
    Format, once entries start arriving:

    | Id | Level | Message | Since |
    |---|---|---|---|
    | 2001 | Warning | `Effect {EffectName} permutation {Key} fell back after {Ms} ms` | 0.1.0 |
-->

### `Vixen.Xr` and its backends

**Nothing here is logged per frame.** A session's whole life produces a handful of lines — it started,
it changed state, the device went away — because the frame loop is paced by a compositor at ninety
hertz and a log call in it is a dropped frame. The two warnings that can repeat (`16003`, `16005`) are
runtime events that a healthy session does not produce at all.

| Id | Level | Message | Since |
|---|---|---|---|
| 16001 | Information | `OpenXR on {Runtime}: {System}, {Views} view(s) at {Width}×{Height}, {Samples} sample(s).` | 0.1.0 |
| 16002 | Information | `No OpenXR: {Reason}` — no loader, no device, or a runtime for another graphics API | 0.1.0 |
| 16003 | Warning | `The OpenXR runtime dropped {Events} event(s)` — a frame took long enough for the runtime to give up on the application hearing about a state change | 0.1.0 |
| 16004 | Information | `The OpenXR session moved to {State}.` | 0.1.0 |
| 16005 | Warning | `The active interaction profile changed` — the user plugged in a different controller, or rebound something | 0.1.0 |
| 16006 | Error | `The runtime reports the device is being lost.` — everything must be recreated | 0.1.0 |
| 16007 | Warning | `The runtime offers no swapchain format this engine knows; {Format} was requested and {Chosen} was taken instead.` | 0.1.0 |

### 14 000 — Samples

A sample uses the same generated call sites as the engine. It would be easy to argue that a demo may
call `LogInformation` directly — and then the one place a reader looks to learn how to write against
Vixen would show them the thing the analyzer forbids everywhere else.

| Id | Level | Message | Since |
|---|---|---|---|
| 14001 | Information | `Running on {Adapter} ({Kind}), presenting {Format} at {Width}×{Height} with {Images} images.` | 0.1.0 |
| 14002 | Error | `There is no window to present to.` — `Samples/01` owns its device and present, so it needs a real display and `--vixen-capture` writes nothing for it | 0.1.0 |
| 14003 | Error | `The device was lost.` — recreation arrives in Phase 2 | 0.1.0 |
| 14004 | Information | `The swapchain was out of date and has been rebuilt at {Width}×{Height}.` | 0.1.0 |
| 14005 | Information | `Generated {Width}×{Height} at {Rate} Hz, {Duration} s, {Megabytes} MB uncompressed.` — `Samples/11` writes its own WebM rather than carrying one | 0.1.0 |
| 14006 | Information | `Bound {Planes} plane(s) of a {Width}×{Height} picture.` — once per video, not per frame | 0.1.0 |
| 14007 | Information | `Sound on {Device} at {Rate} Hz, {Codec} — the picture follows it.` | 0.1.0 |
| 14008 | Information | `No sound ({Reason}); the picture runs on the frame delta instead.` — a runner with no card, which is ordinary | 0.1.0 |
| 14009 | Information | `Reached {Position} s in {Wall} s: …` — the sync check, printed at shutdown | 0.1.0 |
| 14011 | Information | `Showing {Rows}×{Columns} materials through the standard frame, with {Instances} distance-field instance(s) behind the occlusion march.` | 0.1.0 |
| 14012 | Warning | `No Raven/Library above the binary and no baked shaders, so every material will resolve to a miss and the screen will be black. …` — `Samples/03`'s copy of 14040's situation, under its own id | 0.1.0 |
| 14013 | Error | `The grid's material would not compile, so every sphere will draw with nothing: {Diagnostics}` | 0.1.0 |
| 14014 | Information | `Rebuilt '{Address}' with the distance field in it. The first build ran before OnInitialise and the clipmap node captured a null.` | 0.1.0 |
| 14015 | Information | `Stopping after {Frames} frame(s): {Objects} object(s) extracted, {Variants} shader variant(s) compiled.` | 0.1.0 |
| 14016 | Information | `The ground: {Terrains} terrain(s) and {Fields} grass field(s) drawn in the last frame; extraction saw {Extracted} terrain(s), {Waiting} still loading, {Refused} refused grass rule(s).` — zero drawn with zero waiting is a scene problem; zero drawn with one waiting is content that never arrived | 0.1.0 |
| 14017 | Information | `The pines: {Volumes} foliage volume(s) drawn in the last frame, {Missing} mesh(es) still missing; extraction saw {Extracted} volume(s), {Refused} refused.` — drawn with meshes missing is content that never arrived; volumes refused is a broken palette type | 0.1.0 |
| 14018 | Information | `The ground's motion: {Active} under this frame's TAA, {Draws} reprojection draw(s) recorded in the last frame.` — active with no draws is a velocity pass that ran against nothing, which reads as a still image being jittered | 0.1.0 |
| 14021 | Information | `Built {Triangles} triangles into {Clusters} clusters over {Pages} page(s) on {Adapter} ({Kind}). The host never learns how many are drawn.` | 0.1.0 |
| 14022 | Error | `There is no window to present to.` — `Samples/12` needs a real display | 0.1.0 |
| 14023 | Error | `The device was lost.` — recreation arrives in Phase 2 | 0.1.0 |
| 14024 | Information | `The swapchain was out of date and has been rebuilt at {Width}×{Height}.` | 0.1.0 |
| 14025 | Information | `The last frame's traversal accepted {Visible} of {Clusters} clusters, with {Resident} page(s) resident.` — printed at shutdown; zero visible after a real run is a frame that drew nothing, and it is what caught the traversal accepting more clusters than the mesh has | 0.1.0 |
| 14026 | Information | `Running on {Adapter} ({Kind}), presenting {Format} at {Width}×{Height} with {Images} images.` — `Samples/11`; reads the same as 14001 and is a separate id, see below | 0.1.0 |
| 14027 | Error | `There is no window to present to.` — `Samples/11` needs a real display | 0.1.0 |
| 14028 | Error | `The device was lost.` — `Samples/11`; recreation arrives in Phase 2 | 0.1.0 |
| 14031 | Information | `Loaded scene '{Scene}' with {Entities} entities.` | 0.1.0 |
| 14032 | Warning | `Nothing is published at '{Address}'. The level is empty; run the content build.` | 0.1.0 |
| 14033 | Warning | `This build shipped no content, so there is no level, no sound and no input map.` | 0.1.0 |
| 14034 | Information | `Built {Colliders} collider(s) from the level's authored boxes, over {Shapes} registered shape(s).` | 0.1.0 |
| 14035 | Information | `Rebuilt '{Address}' with the distance field, the probe field and the virtualized path in it.` — the first build ran before this game existed and every field node in it captured a null | 0.1.0 |
| 14036 | Information | `Player {Slot} spawned at {Position}, possessing its pawn.` | 0.1.0 |
| 14037 | Warning | `No input map at '{Address}' ({Reason}).` — the player stands still, which is what a controller with no source does rather than a crash | 0.1.0 |
| 14038 | Information | `Loaded {Clips} sound(s); {Missing} were not published.` | 0.1.0 |
| 14039 | Information | `Ran {Frames} frame(s). The player finished at {Position}, {Mode}, having fired {Shots} shot(s) and respawned {Respawns} time(s).` — the shutdown line | 0.1.0 |
| 14040 | Warning | `No Raven/Library above the binary and no baked shaders, so every material will resolve to a miss and the screen will be black.` — a development build run from outside the repository | 0.1.0 |
| 14041 | Information | `Drew {Objects} object(s) from {Meshes} loaded mesh(es) ({FailedMeshes} unresolved) using {Variants} shader variant(s), with {Misses} miss(es) and {BoundMaterials} material set(s) bound.` — any of those at zero is a black screen | 0.1.0 |
| 14042 | Error | `The level's material would not compile, so every object will draw with nothing: {Diagnostics}` | 0.1.0 |
| 14043 | Information | `The frame's set 0 was written {Writes} time(s), and was last {Completeness}.` — zero writes is a black screen whatever the rest of the summary says | 0.1.0 |
| 14044 | Warning | `Nothing filled the frame's {Bindings}, so set 0 never bound and every draw in the shading pass was refused.` | 0.1.0 |
| 14045 | Information | `The frame drew from {Position}, through {ViewProjection}.` — a view-projection still at identity is a camera nothing extracted into | 0.1.0 |
| 14046 | Information | `The shared geometry holds {Vertices} vertex(es) and {Indices} index(es) over {Slices} slice(s), of which {Uploaded} byte(s) reached the device.` | 0.1.0 |
| 14047 | Information | `The frame holds {Count} render object(s), the first two at {A} and {B}, and recorded {Draws} draw(s) over {Indices} index(es).` | 0.1.0 |
| 14048 | Information | `Set 1 was filled with {Bytes} byte(s); the matrix at offset zero is {Sent}.` — what the vertex stage multiplied by | 0.1.0 |
| 14049 | Information | `Lighting: {Lights} punctual light(s), sun {Sun}, sky L00 {Sky} at intensity {Intensity}, {Bound} of {Slots} probe slot(s) filled.` — all of these at zero is a surface lit by nothing | 0.1.0 |
| 14050 | Information | `The level holds {Renderables} entity(s) with a MeshRenderable, of which {Placed} also have a WorldTransform, and {Lights} light(s) of which {LitPlaced} do.` | 0.1.0 |
| 14051 | Information | `Post-process volumes: {Contributing} of {Total} reaching the camera, and the fold {State}.` — placed and not contributing is this feature's commonest failure and looks exactly like not wired up at all | 0.1.0 |
| 14052 | Information | `Composited {Instances} distance field(s) from the same boxes into the clipmap.` — zero is occlusion that marches a clipmap holding nothing and answers "nothing is near" everywhere | 0.1.0 |
| 14053 | Information | `GI wired: {Cards} surface card(s) over the level's boxes ({Dropped} dropped by the atlas), {Captured} texel(s) captured, and the load-time radiosity settled at a change of {Change}.` | 0.1.0 |
| 14054 | Information | `GI frame: {Bricks} irradiance brick(s) filled, {Bounces} cache bounce(s) recorded, culled on the device: {CulledOnDevice}. Cache light: {Light}. Cache bounce: {Bounce}.` | 0.1.0 |
| 14055 | Information | `GI screen: {Probes} screen probe(s) placed, gather trace: {Gather}. Reflections: {Mirrors}. The nearest chain is rebuilt {Ring} time(s) a frame.` | 0.1.0 |
| 14056 | Information | `VSM: {Marked} page(s) marked by the last serviced frame, {Drawn} drawn this frame, {Resident} resident in {Slots} slot(s) after {Allocations} allocation(s).` | 0.1.0 |
| 14057 | Information | `Textures: {Painted} of {Requested} material texture(s) loaded, {Failed} failed. Survey: {Promotions} promotion(s), {Demotions} demotion(s). Streaming {Streamed} texture(s), {Resident} of {Budget} byte(s) resident, {Loading} in flight; {Swaps} swap(s), {Refusals} refusal(s), {Rejections} rejection(s), {Image} byte(s) of image.` | 0.1.0 |
| 14058 | Information | `Ground: {Terrains} terrain(s), {Fields} grass field(s) and {Volumes} foliage volume(s) drawn, {Extracted} extracted with {Waiting} still loading, {RefusedGrass} grass rule(s) refused.` | 0.1.0 |
| 14059 | Information | `Material maps: {Indexed} texture(s) hold a bindless slot, {Unresolved} pairing(s) resolved to slot zero.` — unresolved above zero once the level has settled draws the table's magenta checker | 0.1.0 |
| 14060 | Information | `GPU frame {Frame}: {Milliseconds} ms across {Passes} pass(es), {Attributed} ms attributed, {Ordering}.` | 0.1.0 |
| 14061 | Information | `  {Rank}. {Name} {Milliseconds} ms  {Share}%` — one row of the pass table 14060 heads | 0.1.0 |
| 14062 | Warning | `GPU frame has {Unattributed} ms ({Share}%) outside any pass — the timeline is missing work.` | 0.1.0 |
| 14063 | Information | `Built {Tiles} height-field collider(s) of {Samples}² samples at {MetresPerQuad} m a quad.` | 0.1.0 |
| 14064 | Information | `Water: {Zones} zone(s), {Bodies} bod(ies), {Zoneless} zoneless, {UnresolvedBodies} unresolved spline(s), {UnresolvedWaves} unresolved sea state(s).` | 0.1.0 |
| 14065 | Warning | `No asset manager, so the water has no spline source and no sea state source.` — every water body counts as unresolved and nothing is drawn | 0.1.0 |
| 14066 | Information | `Water mesh: {Zones} zone(s) recorded, {Patches} patch(es) drawn, {Dropped} dropped, over {Builds} build(s); the composite built {Composites} time(s); {Swimming} character(s) were swimming.` — a composite count of −1 is a document with no `!Water` node in it at all | 0.1.0 |
| 14067 | Information | `The sky says the sun is {Illuminance} lux, tinted ({Red}, {Green}, {Blue}), and the level's directional light now says the same.` | 0.1.0 |
| 14068 | Information | `{Effects} lamp(s) are drifting embers and {Waiting} are waiting for one; {Particles} particle(s) were expanded last frame, through {Sets} particle material set(s).` | 0.1.0 |
| 14069 | Information | `The player is driven by the script '{Script}', which lasts {Seconds} simulated second(s) — {Frames} frame(s) at the sixtieth a capture is fixed to.` — `VIXEN_WALK`; a capture shorter than the script is a picture of a moment part-way through the walk | 0.1.0 |
| 14070 | Information | `The script ran {Elapsed} of {Duration} simulated second(s) and the player covered {Distance} m of ground.` — zero metres with a script that has time on it is a walk nothing acted on, and it captures as a still frame with every other counter reporting success | 0.1.0 |

| 14081 | Information | `The village is up: {Agents} agents choosing from {Actions} registered actions, over a {Seconds} s intrusion. One AiSystem, one registry, one blackboard layout, one perception config, one sensor set and one navmesh.` — `Samples/15`; logged after the first frame, because `AiSystem.Population` is written by `Join` and a line logged at initialise time reports an empty village | 0.1.0 |
| 14082 | Information | `frame {Frame} · {Seconds}s · {Agent} ({Planner}) {From} → {To}, intruder {Distance} m` — one agent changing its mind, with where the intruder was when it did. A transition and not a state: "the guard is patrolling" is true of a guard that has never done anything else | 0.1.0 |
| 14083 | Information | `{Changes} change(s) of mind in {Seconds} s — guard {Guard}, villager {Villager}, scavenger {Scavenger} — and {Symptoms} diagnosed symptom(s).` — ⚠ zero changes after a full script is the failure to expect: the stack ran and decided nothing. One symptom is expected and is the scavenger's — `AiDiagnosis` counts switches over the ring against an absolute threshold rather than a rate | 0.1.0 |
| 14084 | Information | `Guard {Guard}, villager {Villager}, scavenger {Scavenger}, intruder {Intruder}.` — where everybody ended up | 0.1.0 |
| 14085 | Information | `The AI overlay is registered on the frame loop — doc 37 § P7's debugger, in an application rather than in a test.` | 0.1.0 |
| 14086 | Warning | `There is no DebugDraw, so the overlay is not registered.` — `Graphics.Overlays` is what builds one, and it is off by default | 0.1.0 |
| 14087 | Information | `The overlay drew {Agents} agent(s) and {Rows} row(s) on the last frame.` — ⚠ zero agents with a village that decided things means the style culled them: `Range` and `Viewpoint`, in that order | 0.1.0 |
| 14088 | Warning | `There is no engine loop, so there is no world and nothing to decide.` — what `--vixen-frames 1` on a machine with no GPU looks like, and not an error | 0.1.0 |
| 14072 | Information | `Buoyancy: {Floating} bod(ies) floating, {Pontoons} pontoon(s), {Wet} of them wet…` — the raft's own numbers, which is how a body resting at the wrong height is told from one that never floated | 0.1.0 |
| 14073 | Information | `Console: typed '{Line}'; claimed = {Claimed}.` — what `VIXEN_CONSOLE` ran, so a capture run's debug verbs are evidence rather than assumption | 0.1.0 |
| 14074 | Warning | `VIXEN_CONSOLE said '{Script}' and this run has no console…` — the verbs were asked for without `--vixen-overlays`, which is what builds the console and the node that draws it | 0.1.0 |
| 14140 | Information | `Composed {Modules} module(s) over {Definitions} definition(s)…` — the shard's libraries composed over real content, which is what says the gameplay stack is running in a process rather than a fixture | 0.1.0 |
| 14141 | Warning | `This build shipped no content mount…` — the shard started with no definitions; every gameplay module is inert rather than absent | 0.1.0 |
| 14142 | Warning | `Nothing in this build carries the '{Label}' label…` — the content build ran and produced nothing the shard asked for, which reads as an empty world | 0.1.0 |
| 14143 | Information | `Spawned {Issued} order(s) across {Camps} camp(s)…` — the world spawns actually issued, so an empty world is distinguishable from an unspawned one | 0.1.0 |

The 14 000 range is subdivided a sample at a time: `Samples/01` at 14001, `Samples/03` at 14011,
`Samples/12` at 14021, `Samples/13` at 14031, `Samples/15` at 14081, and `Samples/11` at 14005
**and** 14026 — two runs,
because for a while it logged its first three lines under 14001–14003, which are `Samples/01`'s.

That is what all of 14022, 14023, 14026, 14027 and 14028 are about. Five of the rows above read the
same as 14001 to 14004 and are separate ids anyway: a shared id makes this register ambiguous the
first time somebody greps for one in a support log, and the register is only useful if an id names
exactly one call site. `Samples/12` allocated its own and `Samples/11` did not, which nothing
noticed until a test was asked to look — see `Vixen.DocGen.Tests.LogEventRegisterTests`.

### `Vixen.Live` — the service tier

Numbered from its design document rather than from the sequence, and subdivided by process: the
orchestrator at 27 000, the gate at 27 100. **Nothing here is logged per tick.** A shard's whole life
is a handful of lines, because these processes run unattended and the log is the only thing anybody
reads afterwards.

| Id | Level | Message | Since |
|---|---|---|---|
| 27001 | Information | `Spawning shard {Shard} for {Map}: {Reason}` | 0.1.0 |
| 27002 | Error | `Could not start shard {Shard} for {Map}` | 0.1.0 |
| 27003 | Information | `Draining shard {Shard}: {Reason}` | 0.1.0 |
| 27004 | Warning | `Shard {Shard} cannot finish draining: {Reason}` — the players on it are not leaving, and the drain has a deadline | 0.1.0 |
| 27005 | Warning | `Shard {Shard} missed its heartbeats` | 0.1.0 |
| 27101 | Information | `Signed in {Account} through {Scheme}.` | 0.1.0 |
| 27102 | Information | `Placed {Player} on {Shard}: {Reason}` | 0.1.0 |
| 27103 | Information | `Did not place {Player}: {Status} — {Reason}` — an ordinary answer as well as a failure, which is why it is not a warning | 0.1.0 |
| 27104 | Debug | `Service-plane socket opened for {Account}; {Open} open.` | 0.1.0 |
| 27105 | Debug | `Service-plane socket closed for {Account}; {Open} open.` | 0.1.0 |
