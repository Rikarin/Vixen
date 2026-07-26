# Vixen.Platform

The contracts every platform implements, and the reason nothing above this assembly has a
`#if ANDROID`.

```csharp
using var window = platform.CreateWindow(new() { Title = "Vixen", Size = new(1280, 720) });
window.Show();

while (!quit) {
    foreach (var item in platform.PumpEvents()) {
        switch (item.Kind) {
            case PlatformEventKind.WindowCloseRequested: quit = true; break;
            case PlatformEventKind.WindowResized:        swapchain.Resize(item.PixelSize); break;
        }
    }
}
```

That loop is the same on Windows, Linux, macOS, Android, iOS, in a browser, and on a dedicated
server with no display at all.

## What is here

| | |
|---|---|
| `IPlatform` | The root: windows, displays, files, clipboard, dialogs, lifecycle, input, text, power, processors. |
| `IWindow` / `ISurface` | A window, and the native handles a graphics backend presents to. |
| `PlatformEvent` | Everything that happens, in one type, in one stream, in order. |
| `PlatformEventBuffer` | The double-buffered queue between whoever produces events and the frame that drains them. |
| `IDisplayInfo` | Monitors, modes, scale factors, work areas. |
| `IFileSystemHost` | Where this platform keeps things — the only native paths engine code sees. |
| `IClipboard`, `INativeDialogs`, `ITextInput` | The three that must be the OS's own, not ours. |
| `ILifecycle`, `IPowerInfo` | Suspend, resume, memory pressure, battery, thermal. |
| `IInputSource`, `IGamepad`, `IHaptics` | Raw devices. The action system is `Vixen.Input` in Phase 8. |
| `IProcessorTopology` | Core counts and affinity — the contract half of the deferred thread-pinning work. |
| `StandardFileSystemHost` | The desktop path conventions, shared by every head that runs on one. |

## The decisions

**Capabilities are a runtime question.** `platform.Has(PlatformCapabilities.Clipboard)` rather than
`#if`. Every capability in the enum is absent on at least one target, and
[doc 10](../../docs/plan/10-platforms.md) makes feature detection with a fallback the rule — this is
where that rule is cashed in.

**One event type, one stream.** Events arrive interleaved from the OS, so splitting them into several
typed streams means buffering and re-ordering them, and losing the ordering between a key press and
the resize that happened between it and the next one. `PlatformEvent`'s payload slots are shared
between kinds; reading the wrong one trips an assert in debug and the factory methods are the only
supported way to build one.

**`Key` is a physical position, and there is no second layout-dependent enum.** WASD must be the same
shape under the player's left hand on AZERTY, and every engine that shipped a layout-dependent
binding system rewrote it. Typed characters are not keys — they may need a dead key, an IME, or
several keystrokes — so they arrive as `TextInput` carrying a string.

**Logical points and physical pixels are different types of number.** A window is sized in points and
its swapchain in pixels, `WindowResized` carries both, and confusing them renders a quarter of the
window or four times too much of it.

**Windows exist without a display.** A headless window has a size, an id, focus and an event stream,
and its surface reports `SurfaceKind.None`. That is what lets the dedicated server run the desktop's
frame loop rather than a second one written for it.

**The clipboard is synchronous.** Reading a browser's clipboard is asynchronous *and* gated on a user
gesture, so an async API would look like it worked there and still return nothing. The web
implementation serves what the last paste delivered, which is the only thing a browser will give it.

**Dialogs are asynchronous, and native.** Modal dialogs run the platform's own event loop, so a
synchronous call would either block the frame loop or reenter it. Native because a drawn file picker
has none of the user's places, tags, cloud providers or accessibility settings — and inside a sandbox
the picker's result is what grants the read permission at all.

**Nothing here names a graphics API.** `SurfaceHandle` is a discriminant and two `nint`s, which covers
every windowing system we target. A Vulkan-shaped type would put a Silk.NET type in this assembly's
public surface and break ADR-001 §3, which is what keeps the RHI mappable to a second backend.

## Threading

A platform belongs to the thread that created it. That is the operating systems' restriction, not a
simplification of ours — Win32 delivers messages to the thread that created the window and AppKit
refuses to touch one from anywhere else — so hiding it behind a lock would turn an exception into a
deadlock. `PlatformEventBuffer.Post` is the exception and is safe from anywhere, because Android's
lifecycle callbacks arrive on the UI thread and a browser's on the JS thread.

## Still to come

**The implementations.** `Vixen.Platform.Headless` is built;
[doc 02](../../docs/plan/02-repository-layout.md) lists Desktop, Windows, Linux, MacOS, Android, iOS
and Web, each of which adds what SDL does not cover well.

**Affinity.** `IProcessorTopology` describes it; pinning a thread is per-OS work that lands with the
per-OS assemblies, and `SupportsAffinity` reports `false` until then rather than pretending.

Licensed under Apache-2.0.
