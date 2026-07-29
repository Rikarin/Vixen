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
| `IPlatformSupplement` | How a per-OS assembly replaces the four services one operating system does better. |
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

## Two implementation helpers live here, and it is not for convenience

`PlatformEventBuffer` and `StandardFileSystemHost` were the first; `TouchTracker` and
`MobileLifecycle` are the second pair, and they are here for a sharper reason than sharing.

`Vixen.Platform.iOS` and `Vixen.Platform.Android` **cannot be in `Vixen.slnx`** — a `net10.0-ios` or
`net10.0-android` project cannot be evaluated without its workload, so its presence breaks
`dotnet build` for anyone without one. Nothing in either is therefore seen by `Test`, `CheckFormat` or
`CheckArchitecture`.

So the half that is arithmetic rather than UIKit lives here, where the solution does see it.
`TouchTracker` turns a `UITouch` address or an Android pointer id into a small stable finger id and
derives the delta neither platform provides; `MobileLifecycle` is the state machine both drive with
different vocabulary for the same three states. Both are genuinely shared and both are tested — the
transitions worth testing being the ones nobody exercises by hand, like a repeated suspend that must
not raise twice or a memory warning at an unchanged level that must.

## One implementation may be built out of two, and `IPlatformSupplement` is how

SDL covers Windows, Linux and macOS in one implementation, and covers most of it well. What it
cannot cover is the part where the three operating systems have nothing in common: a file picker, an
image on the clipboard, a thread pinned to a core, how hot the machine is. Those live in
`Vixen.Platform.Windows`, `.Linux` and `.MacOS`, and this is the seam they arrive through — the
portable implementation builds the four services it can, hands them over as a `PlatformServices`,
and uses what comes back.

Augmenting rather than replacing, because most supplements keep most of what they are given: macOS
can answer `IPowerInfo.Thermal` and has no better answer than SDL's for `BatteryLevel`. And
`Augment` returns the capabilities too, because a supplement that supplies pickers has earned
`PlatformCapabilities.NativeDialogs` for the platform hosting it and nothing else is in a position
to decide that.

## Still to come

**The rest of the implementations.** Headless, Desktop, Windows, Linux, MacOS, Android and iOS are
built; [doc 02](../../docs/plan/02-repository-layout.md) also lists Web.

**Affinity everywhere it exists.** `IProcessorTopology` describes it and Windows and Linux now do it.
macOS reports `SupportsAffinity = false` and means it — Apple offers quality-of-service classes
instead, and `Vixen.Platform.MacOS`'s README says why that is the right answer rather than a gap.

Licensed under Apache-2.0.
