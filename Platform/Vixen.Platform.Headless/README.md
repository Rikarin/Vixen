# Vixen.Platform.Headless

The platform with nothing attached: no window server, no GPU, no audio device, no user.

```csharp
using var platform = new HeadlessPlatform();
using var window = platform.CreateWindow(new() { Size = new(1920, 1080) });

// The dedicated server's frame loop is the desktop's frame loop.
foreach (var item in platform.PumpEvents()) { … }
```

## Why it is built in Phase 1

It has two jobs and they are the same job. It is the head a dedicated server and a batch tool run on
([doc 17](../../docs/plan/17-app-heads-and-shipping.md)), and it is the platform every test that
needs a platform uses. Because the second happens on every build, the first cannot quietly rot — which
is the entire argument for building it now rather than discovering in Phase 9 that four subsystems
assume a window exists.

## What a headless window is

Everything a window is except a picture: an id, a size, a framebuffer size, a scale factor, focus, a
lifecycle and an event stream. Its surface reports `SurfaceKind.None`.

⚠ **That used to be described here as "what tells a graphics backend to render offscreen instead of
building a swapchain", and it was not true.** What it told a backend was to *decline*: `GraphicsHost`
makes Vulkan and WebGPU refuse a surface they cannot present to — deliberately, so that a dedicated
server does not quietly acquire a real GPU device — and the chain fell through to `Vixen.Graphics.Null`,
which draws nothing. A whole game booting headless therefore produced a black frame with every counter
reporting success.

It is true now for Vulkan, and only when something asks for it in as many words: `--vixen-capture`
(a real device and a PNG) or `--vixen-offscreen` (a real device and no PNG) opens the device without a
surface, and `VulkanOffscreenSwapChain` gives the frame an ordinary texture to write. Without one of
those, headless still means the device that draws nothing, which is the right default for the head
this platform was built for. ⚠ With one of them, the fall-through to that device is a boot failure
rather than a downgrade — a request for a device that renders cannot be answered by one that does not.
See [capturing a frame](../../docs/guide/rendering/capturing-a-frame.md).

Its state is also *writable* in a way a real window's is not, deliberately. Setting `ClientSize` here
really does resize and really does raise `WindowResized`; `SetDpiScale` is the only way to exercise a
1× → 2× transition without two monitors, and that transition is the case that breaks swapchain sizing.
A real window treats the same assignment as a request its window manager may refuse, which is why code
that must know listens for the event on both.

## What it refuses to fake

`HeadlessClipboard` returns `false` from everything. An in-process buffer pretending to be a clipboard
was the obvious alternative and is the wrong one: it would make copy-and-paste appear to work in a
headless test and then not work in the product, because a clipboard's whole purpose is to be shared
with applications that do not exist here. Code that wants a controllable clipboard for a test wants a
test double, which is a different object with a different name.

Dialogs return *nothing chosen* rather than throwing — the same answer a user pressing Cancel gives,
so the caller's existing cancellation path covers headless and no special case is needed anywhere.

Affinity is refused rather than emulated: a server shares its machine, and a process that pins itself
to core 3 there is fighting a scheduler that knows more than it does.

## Driving it

The concrete types expose what a real OS would do to you, so a test can do it instead:

| | |
|---|---|
| `HeadlessPlatform.Post` | Inject any event. A recorded input trace replayed through here drives the engine exactly as a keyboard would, deterministically. |
| `HeadlessLifecycle.Suspend` / `Resume` | The suspend/resume fault-injection loop [doc 10](../../docs/plan/10-platforms.md) asks for. On a phone it needs a phone; here a hundred cycles cost milliseconds. |
| `HeadlessLifecycle.ReportMemoryPressure` | iOS's memory warning and Android's `onTrimMemory`. |
| `HeadlessWindow.SetFocused` / `SetMinimised` / `SetDpiScale` / `RequestClose` | The window-manager half of the conversation. |
| `HeadlessInputSource.SetKey` / `ReleaseAll` | Held keys, and the release no platform performs for you on focus loss. |

None of this is on the interfaces. `IPlatform` describes what a platform does; these describe what is
done *to* it, and a shipping build has no business calling them.

## Files

Standard OS locations by default, via `StandardFileSystemHost`. Pass `HeadlessPlatformOptions.FileSystem`
to point them somewhere else — which is how a test avoids writing into the developer's home directory
and how a container image points `/data` at a mounted volume.

Licensed under Apache-2.0.
