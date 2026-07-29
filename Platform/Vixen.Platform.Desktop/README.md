# Vixen.Platform.Desktop

Windows, Linux and macOS, through one SDL implementation.

```csharp
using var platform = new DesktopPlatform(new() { Application = "MyGame" });
using var window = platform.CreateWindow(new() { Title = "MyGame", Size = new(1280, 720) });
window.Show();

while (!platform.Lifecycle.IsQuitRequested) {
    foreach (var item in platform.PumpEvents()) { … }
}
```

## It is SDL 2, not SDL 3

[Doc 01](../../docs/plan/01-technology-decisions.md)'s dependency register said `Silk.NET.SDL`
2.23.0 was SDL 3. It is not: Silk.NET 2.x binds SDL 2 — verified by the six-argument
`CreateWindow(title, x, y, w, h, flags)`, which is SDL 2's signature — and SDL 3 bindings only exist
in the Silk.NET 3.0 previews. The register has been corrected.

Nothing in Phase 1 needs SDL 3. What it costs is `SDL_ShowOpenFileDialog` (SDL 3 only), better HiDPI
reporting, and the cleaner event API; what moving would cost is a prerelease dependency for every
Silk.NET binding in the repository, including the Vulkan one the renderer stands on. Revisit when
Silk.NET 3.0 ships.

## SDL is not in the package

`Silk.NET.SDL` ships bindings and nothing else — there is no `Silk.NET.SDL.Native` companion on
nuget.org. `libSDL2` comes from the system, and until `Vixen.Platform.Native`
([doc 10](../../docs/plan/10-platforms.md) § Cross-platform discipline) exists to acquire it,
`SdlLibrary` replaces the default `DllNotFoundException` with the install command for the platform
you are on, and `SdlLibrary.IsAvailable` lets a test skip rather than fail on a machine that was
never going to run it.

## What SDL covers, and what is visibly missing

| | |
|---|---|
| ✅ | Windows, sizes, modes, cursors, icons, drag-and-drop |
| ✅ | Keyboard, pointer, touch, gamepads with rumble and trigger rumble |
| ✅ | Clipboard **text** |
| ✅ | IME composition and the candidate-window position |
| ✅ | Display enumeration, modes, work areas |
| ✅ | Battery and charging state |
| ✅ | Message boxes — the OS's own, which is what a fatal-error path needs before there is a renderer |
| ❌ | **File pickers.** SDL 2 has none. Left missing rather than drawn: a picker carries the user's places, tags and cloud providers, and inside a sandbox it is what grants permission to read what was picked. |
| ❌ | **Clipboard images and custom formats.** Needs Win32 registered formats, `NSPasteboard` UTIs, X11 atoms — three namespaces with nothing in common. |
| ❌ | **Thread affinity.** SDL has no affinity API at all. |
| ❌ | **Thermal and power-mode state.** SDL cannot answer either. |

## The four gaps are filled from outside, by whichever OS this is

Each of those belongs to `Vixen.Platform.Windows`, `.Linux` or `.MacOS`, and each of those exists.
They arrive through `IPlatformSupplement`: this implementation builds the four services it can,
`DesktopSupplements.ForCurrentOperatingSystem()` picks the assembly for the machine, and it replaces
what it can do better and hands the rest back.

```csharp
// On by default. Off leaves SDL's four in place, which is what a test that wants the same
// behaviour on three machines wants.
new DesktopPlatform(new() { UseNativeSupplement = false });
```

Nothing above this assembly has to know which one it got — the services arrive through the same
interfaces either way. What does change is `Capabilities`: `PlatformCapabilities.NativeDialogs`
covers pickers and message boxes together, so SDL cannot report it on its own and the supplement is
what adds it. On Windows and macOS that is unconditional; on Linux it depends on whether the session
has `zenity` or `kdialog`, which is a runtime question with a runtime answer. Message boxes work
regardless — `ShowMessageAsync` is always safe to call, and all three supplements keep SDL's, because
SDL's *are* the OS's own.

**The dependency points from here to them**, not the other way round: .NET loads an assembly when
something first calls into it, so a per-OS assembly that registered itself in a module initialiser
would only do so once something else had already touched it. A RID-specific publish keeps one of the
three; a portable one carries all three, which is a few tens of kilobytes of IL.

## The decisions

**Keyboard translation is a cast.** SDL's scancodes *are* USB HID usage codes for the whole main
range, and `Key` was defined on the same table, so `ToKey` is a range check plus one special case
for Android's back button. That was the payoff for choosing HID codes over inventing a numbering,
and a test asserts the identity so a renumbering upstream is a failure rather than a keyboard that
is subtly wrong.

**`DpiScale` is the ratio of the two sizes, not `SDL_GetDisplayDPI`.** The DPI call reports the
panel's advertised dots per inch, which is a different number from the scale the OS applies — and on
Linux it is frequently whatever the EDID claimed, which is regularly wrong. The framebuffer size
divided by the client size is the scale that actually applies to this window on the display it is
on.

**Timestamps are anchored, not resampled.** SDL's millisecond clock is converted into `Stopwatch`
ticks against an anchor taken at startup, so an event keeps the time the OS says it happened rather
than the time the loop got round to it. That difference *is* input latency; measuring it needs the
original number.

**`WindowResized` comes from `SDL_WINDOWEVENT_SIZE_CHANGED`, not `RESIZED`.** SDL sends
`SIZE_CHANGED` for every size change and `RESIZED` only for ones the user made — and both for those.
Handling the first alone catches a programmatic resize and does not report a user's twice.

**A flipped wheel event has its sign undone here.** SDL reports the platform's natural-scrolling
setting rather than the raw device; ignoring it scrolls the wrong way on a Mac in our windows and
the right way in everybody else's.

**Gamepads are keyed on the joystick instance id, not the open index.** The index is a position in
the current device list and changes when anything else is unplugged. Keying on it is how a second
controller ends up driving the first player.

**Capabilities are detected, not assumed.** `WindowPositioning` is decided by asking SDL which video
driver it chose, because Wayland deliberately refuses to tell a client where it is and the same
Linux binary under X11 can.

## Testing

`SdlTranslation` is tested exhaustively without SDL running — a wrong mapping is wrong on a machine
with no display too.

The live tests use the `dummy` video driver where they must, and that is worth being precise about.
**macOS is forced to it**: AppKit aborts the process with `SIGABRT` if a window is created from
anywhere but the process's main thread, and a test runner is never on it. Linux without a display
server gets it for the obvious reason. Windows and a Linux desktop session run the real driver.

So the Cocoa, X11 and Wayland surface paths are **not** covered by automated tests; the Win32 one is,
on the Windows CI leg. The rest is proved by `Samples/01-HelloTriangle`, which has a genuine main
thread — which is part of why [doc 14](../../docs/plan/14-roadmap.md) makes it a Phase 1 exit
criterion on all three desktops rather than one.

Licensed under Apache-2.0.
