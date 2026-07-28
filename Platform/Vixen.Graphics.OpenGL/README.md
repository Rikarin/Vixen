# Vixen.Graphics.OpenGL

The OpenGL backend — GL 4.5 core, GLES 3.0/3.2 and WebGL2 behind one translation layer.

Per [ADR-001](../../docs/plan/01-technology-decisions.md#adr-001--vulkan-is-the-reference-backend-other-apis-are-conformance-targets),
this is the RHI's **abstraction validator** now that D3D12 is postponed past 1.0. It is a harder test
than D3D12 would have been, and deliberately so: GL is further from Vulkan in every direction that
matters — no pipeline objects, no descriptor sets, no explicit barriers, no multithreaded recording,
and a clip space the other way up. An RHI that survives this maps to D3D12 comfortably.

The findings are collected in [`docs/rhi-backend-mapping.md`](../../docs/rhi-backend-mapping.md),
which is the ADR's fourth measure and the place to look before changing the RHI's surface.

## What is where

| File | What it answers |
|---|---|
| `IGlApi` | Every GL entry point the backend calls, behind an interface |
| `SilkGlApi` | …and the one implementation of it, over `Silk.NET.OpenGL` |
| `GlProfile` | Which dialect, and what each one has |
| `GlslTranslator` | Vulkan-style bindings → GL's flat namespaces; Vulkan clip space → GL's |
| `GlBindingPlan` | Four descriptor sets → one binding index sequence per resource class |
| `GlProgramCache` | Pipelines → programs, shared across state-only permutations |
| `GlStateCache` | Pipelines → loose state, with a shadow so redundant calls cost nothing |
| `GlFramebufferCache` | Render passes → framebuffer objects, kept rather than rebuilt |
| `GlCommandList` | Recording on any thread, on an API that has no such thing |
| `GlDevice.Replay` | …and replaying it on the GL thread |

## The seam, and why the tests have no GL context

Every GL call goes through `IGlApi`. That is not tidiness: the part of this backend that can be
*wrong* is the translation, and none of it needs a driver to be checked. A test drives
`RecordingGlApi` and asserts the call stream — the same shape as `Vixen.Graphics.Null`'s recorder,
one level down. That one asserts what the engine asked the RHI for; this asserts what the RHI asked
GL for.

So `SilkGlApi` is the one file the suite does not touch, and there is nothing in it but
transcription, which a compiler checks. What it needs instead is a driver, which CI provides on the
Mesa leg ([`docs/plan/05`](../../docs/plan/05-graphics-rhi.md) § Cross-backend equivalence).

## Concessions, stated up front

`docs/plan/05` lists these and they are all real.

- **No multithreaded recording.** A command list records into managed memory and replays on the GL
  thread at submit. The RHI's threading contract stays true at a modest CPU cost, and
  `Vixen.Rendering` has no GL-shaped branch anywhere in it.
- **No bindless, no async compute, no timeline semaphores, no sparse resources.** All reported absent
  through `GraphicsDeviceFeatures`, so a renderer that gates on them takes the fallback path here.
- **No compute and no storage buffers below GLES 3.2**, which cascades into the post-FX chain needing
  a non-compute variant of every effect — `docs/plan/06` already requires that.
- **A base instance is refused rather than emulated.** GLES has no
  `glDrawElementsInstancedBaseVertexBaseInstance` at any version, and silently drawing instance zero
  would be a mesh in the wrong place. Use a dynamic uniform offset, which every profile has.
- **A format-reinterpreting texture view is refused on every profile**, including the one that could
  do it. Offering it only on desktop would mean content that works there and fails on Android.

## Two things worth knowing before reading the code

**Textures are stored bottom-up.** GL's viewport transform puts what the RHI calls the top of an
image at the *highest* row of the framebuffer, whichever way `glClipControl` is set — clip control
changes how clip space maps into the viewport rectangle, not where a framebuffer's first row is. So
anything rendered here is stored upside down relative to Vulkan. Uploads are flipped to match, which
keeps every texture on this backend consistent with every other and confines the difference to the
RHI's copy boundary, where the "row 0 is the top row" contract is stated. The cost is one
`glTexSubImage2D` per row; a content build targeting GL should write its KTX2 bottom-up and skip it.

**The front face is inverted.** The `y` flip that makes GL's clip space Vulkan's also reverses
triangle winding, so counter-clockwise in the RHI reaches the rasteriser clockwise. `GlEnums.Winding`
does it and `GlslTranslator` does the flip; they are one change and are wrong separately.

## Adding the GLES profiles

`GlProfile` already models GLES 3.0, 3.2 and WebGL2, and the translation layer already differs by
profile — that is what most of the test suite is about. What is missing is `Silk.NET.OpenGLES` and an
EGL context, neither of which any app head in this repository creates yet. Adding them is one class
implementing `IGlApi` and no change above it.
