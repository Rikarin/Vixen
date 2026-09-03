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
| `SilkGlApi` | …one implementation of it, over `Silk.NET.OpenGL` — desktop |
| `SilkGlesApi` | …and the other, over `Silk.NET.OpenGLES` — GLES and WebGL2 |
| `IEglApi` · `NativeEglApi` | The EGL entry points, and the hand-loaded `libEGL` behind them |
| `EglContext` | A GLES context on a native window or a pbuffer, as a Silk `IGLContext` |
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

So `SilkGlApi`, `SilkGlesApi` and `NativeEglApi` are the files the suite does not touch, and there is
nothing in them but transcription, which a compiler checks. What they need instead is a driver, which
CI provides on the Mesa leg ([`docs/plan/05`](../../docs/plan/05-graphics-rhi.md) § Cross-backend
equivalence) and an Android device provides for the rest.

The same seam is repeated one layer out for EGL. What can be wrong about bringing a context up is the
*sequence* — which attribute list, in what order, what happens when a driver refuses GLES 3.2,
whether a half-built context is torn down in the reverse of the order it was built — and all of it is
decidable from the call stream. So `EglContext` talks to `IEglApi` and the tests drive
`RecordingEglApi`, which is how a machine with no EGL on it checks the part that matters.

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
- **`DescriptorBinding.SampleType` is read past.** A sampler object carries its own comparison mode
  and a sampler uniform its own type, so there is nothing here for the declaration to change and
  nothing it could catch that a GLSL compile would not. It exists for WebGPU, where a bind group
  layout has to state it before there is a resource to ask.

## Storage images

⚠ **The backend used to say yes twice and then throw.** `GlProfiles.Features` reports `HasCompute`
from GLES 3.2 up, `GlDevice.CreateTexture` accepts `TextureUsage.Storage` on the same profiles, and
`GlBindingPlan` has counted image units apart from texture units since it was written — and then
replay refused a `DescriptorKind.StorageTexture` write outright. A renderer asking the capability
question got a yes, created its texture, built its descriptor set, and failed at the draw. That is a
bug rather than a missing feature: an absent capability is answered by `HasCompute` being false, and
below GLES 3.2 it is.

`glBindImageTexture` is routed now, with the three things GL asks for and Vulkan does not:

- **`GL_READ_WRITE`, always.** A storage image carries no access direction in the RHI, so binding it
  narrower would hand a shader that also reads undefined values — with no error anywhere.
- **The sized internal format**, which the image unit reinterprets the storage through. Taken from
  the *view*, even though a format-reinterpreting view is refused here on every profile: the day that
  refusal lifts, this reads the right one already.
- **Layered when the view spans more than one layer**, or when the texture is 3D — whose slices are
  not array layers, so `ArrayLayers` is 1 for one and the first rule alone would bind a single
  z-slice of a volume the shader means to fill.

The state cache keys an image binding on all of unit, texture, level, layer and format, unlike the
sampled-texture cache beside it, which keys on the name. A compute chain writing a pyramid binds one
texture at successive levels; a cache that missed the level would elide every bind after the first
and write level 0 each time — a mip chain whose every level is the base, which is blurry rather than
absent.

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

## Running the GLES profiles

`GlProfile` has modelled GLES 3.0, 3.2 and WebGL2 since this backend was written, and the translation
layer has differed by profile throughout — that is what most of the test suite is about. What was
missing was a binding and a context, and they are here now: `SilkGlesApi` over `Silk.NET.OpenGLES`,
and `EglContext` over a hand-loaded `libEGL`. Nothing above `IGlApi` changed to accommodate either,
which is the claim the seam was built to make good on.

```csharp
if (!NativeEglApi.TryLoad(out var egl, out var reason)) {
    // No EGL on this machine. That is an ordinary answer on a desktop without ANGLE.
    return;
}

using var context = new EglContext(egl, new EglContextOptions(nativeWindow));
using var api = new SilkGlesApi(context, context.Profile);
using var device = new GlDevice(new GlDeviceOptions(api, context.SwapBuffers));
```

`context.Profile` is the ladder's answer rather than a request: a driver is asked for GLES 3.2 and
then, if it refuses, for 3.0, because `GL_VERSION` can only be read through a context that already
exists. Everything the renderer gates on — compute, storage buffers, indirect draws — follows from
which rung answered.

### Reaching them from a head, which is a second ladder one rung up

`SilkGlesApi.FromProcAddress` is the overload a windowing layer can call. `Vixen.Platform`'s
`IGlContext` deliberately names no Silk type — it is a `Core/` assembly — so the only thing a
platform that has made a context current can hand over is `GetProcAddress`, and until this existed
`SilkGlApi.FromProcAddress` was the only such overload in the assembly. ⚠ **That is why
`Tools/Vixen.App` loaded every context through the desktop binding, including the ones its own
`ProfileOf` had just called GLES**: `libGL` and `libGLESv2` are two libraries, and an embedded
context resolved through the first is asking a library Android does not ship for entry points its
own driver owns.

`GraphicsHost` now asks its window for a 4.5 core context and then, if that is refused, for GLES
3.0 — core first for the same reason `EglContext`'s ladder puts 3.2 first, because the profile with
`glClipControl` is the one worth having and a machine that silently settled for less would take the
fallback path forever. The binding follows the profile that came back.

**There is no `Silk.NET.EGL` for Silk.NET 2**; the package stops at 1.9.0, and Silk.NET 2's GLES
windowing reaches EGL through GLFW or SDL rather than binding it. So `NativeEglApi` loads nineteen
entry points out of the platform's `libEGL` itself, through `Vixen.Platform.Native` — the same
search the Vulkan loader uses, for the same reasons.

Three things this file is the place to say about GLES, each of which the translation layer already
knew and none of which needed changing when a real context arrived:

- **`GL_FRAMEBUFFER_SRGB` is desktop-only**, and enabling it on GLES is `GL_INVALID_ENUM` rather than
  a no-op. It is now gated on `GlProfiles.HasFramebufferSrgbControl`. Its absence costs nothing: GLES
  encodes for an attachment whose format is sRGB and does not for any other, which is precisely what
  the RHI means by a format. The switch is the odd one out.
- **A readback is a map and a copy.** GLES has no `glGetBufferSubData` at any version, which
  `IGlApi` already said, and `SilkGlesApi` is where it becomes `glMapBufferRange`.
- **A multi-draw is a loop.** GLES 3.1 has `glDrawElementsIndirect` for exactly one draw and no
  multi-draw at any version, which is why `HasMultiDrawIndirect` is false on every GLES profile: the
  draws happen, one call each.

WebGL2 uses the same binding over a browser context rather than an EGL one — the arrangement
[the spike](../../docs/plan/spikes/web-webgl2/RESULT.md) verified, and the reason `SilkGlesApi`
refuses only `GlProfile.Core45`.
