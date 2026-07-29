# Vixen.Platform.Linux

What Linux can do that SDL cannot.

```csharp
// Nothing to wire up: DesktopPlatform picks this up on Linux.
using var platform = new DesktopPlatform(new() { Application = "MyGame" });

// False on a session with neither zenity nor kdialog installed, which is a real answer.
if (platform.Has(PlatformCapabilities.NativeDialogs)) {
    var path = await platform.Dialogs.OpenFileAsync(new() { Title = "Open project" });
}
```

## What is here, and why each one

| | |
|---|---|
| **File pickers** | `zenity` or `kdialog`, whichever the session has. SDL 2 has none. |
| **Clipboard images** | `image/png` through `wl-copy`/`wl-paste` or `xclip`, with a PNG codec for the bytes. |
| **Clipboard formats** | MIME types, passed through — which is what both display servers already use. |
| **Processor classes** | ARM `cpu_capacity` and Intel's `cpu_core`/`cpu_atom` PMU lists. |
| **Thread affinity** | `sched_setaffinity`, which closes `Vixen.Core.Threading`'s deferred pinning work here. |
| **Thermal state** | `/sys/class/thermal`, graded against the kernel's own trip points. |
| **Low power mode** | The ACPI platform profile, which is what `power-profiles-daemon` writes. |

## The decisions

**The pickers and the clipboard are other programs, and that is not a shortcut.** Both are served on
Linux by D-Bus — the XDG desktop portal for one, the toolkit's own selection owner for the other —
and there is no D-Bus client in the base class library. Adding one means a native dependency and a
message-serialisation layer, against a stated policy of not taking dependencies we do not need
([doc 01](../../docs/plan/01-technology-decisions.md)). `zenity`, `kdialog`, `wl-copy` and `xclip`
are the programs the desktop already ships to do exactly this, they are what the user's session is
configured through, and **inside a Flatpak they are themselves portal clients** — so going through
them gets the portal's behaviour rather than bypassing it.

**KDE's picker under KDE, GNOME's otherwise.** A picker carries the user's places, recent files and
remote mounts, and those belong to their desktop rather than to their distribution. A KDE user given
zenity's picker is shown somebody else's bookmarks. `XDG_CURRENT_DESKTOP` is what the session sets
to say which it is. `qarma` and `matedialog` re-implement zenity's command line and are tried after
it.

**A missing helper is a capability that is absent, not an exception.** With neither picker
installed, `PlatformCapabilities.NativeDialogs` is not reported and the portable dialogs stay. That
is the capability model being cashed in on the one desktop where the answer genuinely varies between
two machines running the same binary.

**Reading and writing are different, because the processes are.** `wl-copy` and `xclip -i` keep
running after they have read their input: on X11 and Wayland the clipboard has no store, the
application that copied *is* the clipboard, and something has to stay alive to answer the paste.
So a write waits briefly for a failure and treats "still running" as success; only a read waits for
an exit.

**There is a second hand-written PNG codec in this repository and this is not it.**
`Vixen.Ui.Testing.Visual.PngCodec` is the golden-image suites', and `Platform/` cannot reference the
UI layer ([doc 00](../../docs/plan/00-vision-and-principles.md) § layer discipline) — a testing
library is the wrong direction for a dependency regardless. Consolidating them means moving that
codec and its `Bitmap` down into `Vixen.Core.Imaging`, which is a change to two shipped public
surfaces and is its own piece of work. What is here decodes what a clipboard produces: eight bits a
channel, non-interlaced, greyscale or truecolour, with or without alpha. Not 16-bit, not palettised,
not Adam7 — refusing one of those is a paste that does nothing rather than a paste that is wrong.

**Thermal state is graded against trip points, not degrees.** 82 °C means nothing without knowing
what this chassis considers hot. The kernel publishes the passive, hot and critical points it will
itself act on, and "past the passive point" is exactly what `ThermalState.Fair` is defined as. The
hottest zone wins, because which zone matters is a per-machine question.

**Everything sysfs is cached for a second.** These are properties, they look free, and a
quality-scaling policy that consults them per frame would do a dozen virtual-file-system round trips
per frame to watch a number that moves on the scale of tens of seconds.

## Testing

Everything that can be wrong without a Linux machine saying so is a pure function and is tested
everywhere: the two tools' command lines, including the separators that are legal characters in a
file name; the PNG codec against all five filter types, decoded from files this suite builds rather
than from its own encoder; and sysfs's inclusive processor-range syntax.

The affinity calls run for real when the suite is on Linux and skip elsewhere. What no test covers is
a live picker or a live clipboard, which need a display server with a person in front of it.

Licensed under Apache-2.0.
