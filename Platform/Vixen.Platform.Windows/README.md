# Vixen.Platform.Windows

What Windows can do that SDL cannot.

```csharp
// Nothing to wire up: DesktopPlatform picks this up on Windows.
using var platform = new DesktopPlatform(new() { Application = "MyGame" });

if (platform.Has(PlatformCapabilities.NativeDialogs)) {
    var path = await platform.Dialogs.OpenFileAsync(
        new() { Title = "Open project", Filters = [new("Vixen project", "vxproj")] }
    );
}
```

## What is here, and why each one

| | |
|---|---|
| **File pickers** | `IFileDialog` — the shell's own, with the user's places, cloud providers and search. SDL 2 has none. |
| **Clipboard images** | `CF_DIBV5` in and out, and every shape of `CF_DIB` a real application produces on the way in. |
| **Clipboard formats** | `RegisterClipboardFormat`, so `"PNG"`, `"HTML Format"` and an application's own name all work. |
| **Processor classes** | `GetLogicalProcessorInformationEx` — which cores are the fast ones on a hybrid part. |
| **Thread affinity** | `SetThreadGroupAffinity`, which closes `Vixen.Core.Threading`'s deferred pinning work on this platform. |
| **Battery saver** | `SystemStatusFlag`, the one byte of `GetSystemPowerStatus` SDL does not report. |

Everything else — windows, input, gamepads, clipboard text, IME, message boxes — stays with
`Vixen.Platform.Desktop`, which already does it well.

## The decisions

**`net10.0`, not `net10.0-windows`.** [Doc 10](../../docs/plan/10-platforms.md) named WinRT's
`FileOpenPicker` and the Windows-versioned target framework it needs. That framework would spread
from here to every consumer — the app head, the editor, every sample — and turn a portable build
graph into a multi-targeted one; it would also take this assembly out of `nuke CheckApi`, which
covers `net10.0`. WinRT's picker in a desktop application is a wrapper over `IFileDialog`, so the
cost buys nothing a user can see. Everything here is Win32 and COM behind `[LibraryImport]` and
`[SupportedOSPlatform("windows")]`, which is why this project builds and its tests run on a Mac.

**COM by vtable, not by interface declaration.** Five interfaces and nine methods of them. A
`[GeneratedComInterface]` set or a `ComWrappers` generator would be more code and would put a
marshalling layer between us and an ABI four function-pointer calls deep. Each call site names the
slot it is calling — `// Slot 20, IFileDialog::GetResult` — which is the form in which a mistake is
visible.

**Each dialog gets its own STA thread.** A modal shell dialog runs its own message loop for as long
as the user takes. On the frame thread that stops the frame loop and Windows draws the ghosted "not
responding" chrome over a window that is fine. The dialog is still modal to the application — it is
given the owner `HWND`, which Windows disables — and one is shown at a time.

**Cancellation is honoured before the dialog opens and not after.** Closing an open `IFileDialog`
means calling `Close` on the apartment that owns it from outside its message loop, which is a
deadlock waiting for a slow network place to enumerate. `INativeDialogs` says a token dismisses a
dialog "where the platform allows it"; this is a place it does not.

**An all-zero alpha plane is read as opaque.** The fourth byte of a 32-bit `BI_RGB` pixel is
undefined by the format, and in practice it is either a real alpha channel or zeroes — the same
bytes with opposite meanings. Reading it literally turns every screenshot from the applications that
leave it alone into a fully transparent image. An image nobody can see is not something anybody
copies, so all-zero means opaque and one transparent pixel among opaque ones is kept.

**There is no thermal state, and that is Windows'.** There is no user-mode counterpart to
`NSProcessInfo`'s `thermalState` — what exists is a WMI thermal zone most laptops do not populate
and a power-setting notification for "about to shut down", which is a different question asked too
late. `IPowerInfo.Thermal` is whatever the portable implementation says, and a Windows title that
scales quality reads frame time.

## Testing

The parts that can be wrong without Windows saying so are pure functions, and they are tested
everywhere: `DibImage` against bottom-up, top-down, 16-, 24- and 32-bit bitmaps, truncated headers
and implausible sizes; and the interop structures against the offsets in `winnt.h`, because a
structure one padding byte out of place reports the wrong core count only on the machines nobody
tests on.

What is not covered by an automated test is the shell dialogs and the live clipboard, which need a
Windows session with a person in front of it. The Windows CI leg builds and runs everything above;
the pickers are proved by the editor.

Licensed under Apache-2.0.
