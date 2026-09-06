---
title: Media queries and user preferences
slug: ui/media-queries
kind: guide
area: Core
summary: What `@media` can ask about in Vixen — the size, resolution and gamut of the surface a panel is on rather than of the monitor, the colour-scheme preference, and `prefers-reduced-motion`, which is a query and a switch the animator honours because a toolkit whose default is to animate anyway ignores the preference in every application that forgot.
api: [T:Vixen.Ui.Styling.MediaContext, T:Vixen.Ui.Styling.ColorSchemePreference, T:Vixen.Ui.Styling.MotionPreference, T:Vixen.Platform.MacOS.MacOSAccessibility, T:Vixen.Platform.Windows.WindowsAccessibility, T:Vixen.Platform.Linux.LinuxAccessibility]
tags: [ui, styling, css, media-queries, accessibility, reduced-motion]
since: 0.2
status: preview
related: [ui/cascade-layers, ui/accessibility, ui/desktop-application]
---

## What it is

`@media` conditions, evaluated against a `MediaContext` — and the context is a **surface**, not a
screen.

| Feature | Answers from |
|---|---|
| `width`, `height` | `MediaContext.Width` / `.Height`, in logical pixels, with `min-` and `max-` |
| `resolution` | `MediaContext.Resolution`, device pixels per logical one |
| `orientation` | Whether the surface is wider than it is tall |
| `prefers-color-scheme` | `MediaContext.ColorScheme` — `light`, `dark` |
| `color-gamut` | `MediaContext.Gamut`, ascending: `p3` implies `srgb` holds too |
| `prefers-reduced-motion` | `MediaContext.ReducedMotion` — `reduce`, `no-preference`, and the bare form |

A game's UI can be a whole window, a split-screen viewport, or a panel on the side of a crate, and
all three want `@media (max-width: 600px)` to mean *this panel*. `UiSurface` carries its own context,
so a torn-off window answers for itself while the rules stay shared.

**A condition this cannot evaluate makes the whole block fail to load, with a diagnostic.** Choosing
a default would be wrong in a way nobody notices: false silently drops styles and true silently
applies phone styles on a desktop.

## What it is for

A stylesheet that answers the surface it is drawn on rather than the machine it is running on. A
game's UI can be a full window, a split-screen viewport or a world-space panel on the side of a
crate, and all three want `@media (max-width: …)` to mean *this panel* — never *the monitor*.

The accessibility features are the other half: `prefers-reduced-motion`, `prefers-contrast`,
`forced-colors` and `inverted-colors` are statements about the person, and a stylesheet is the only
place in this engine that asks.

## Reduced motion

```css
.panel { transition: transform 250ms ease-out }

@media (prefers-reduced-motion: reduce) {
    .panel { transition: none }
}
```

**The bare form means `reduce`.** Media Queries 5 makes `no-preference` this feature's false value,
so `@media (prefers-reduced-motion)` is the idiomatic spelling and is the one most sheets use.

⚠ **It is also a switch the animator honours, not only a query, and that is a deliberate departure
from the web.** `StyleEngine.SetMedia` hands `MediaContext.ReducedMotion` to `Animator.ReduceMotion`;
with it set, a transition is not started and a `@keyframes` animation is not run, so a property
arrives at its new value the moment the cascade decides it. A browser does not do this — it leaves
the decision entirely to the author — but a toolkit that ships transitions, keyframes and `spring()`
that an author gets *without asking* would otherwise ignore the preference in every application
nobody remembered to write a media block for. AppKit honours
`accessibilityDisplayShouldReduceMotion` in the framework for the same reason. An author who wants
reduced-but-present motion writes it in the media block and sets `Animator.ReduceMotion` back to
false.

⚠ **A transition already in flight finishes; a keyframe animation stops.** A transition has an end,
and cutting it short freezes a panel at whatever opacity it had got to — the one state no stylesheet
asked for. An animation may be `infinite`, so there is no end to let it reach.

## Using it

```csharp no-compile="a fragment; `engine` is a StyleEngine and `surface` a UiSurface"
engine.SetMedia(new MediaContext(
    surface.Width,
    surface.Height,
    surface.DpiScale,
    ColorSchemePreference.Dark,
    ColorGamut.DisplayP3,
    MotionPreference.Reduce
));
```

### Where the answers come from

A desktop host does not pass these in by hand. `IPlatform.Accessibility` reads them from the
operating system — `com.apple.universalaccess` on macOS, `SystemParametersInfo` on Windows,
`gsettings` on Linux — and `PlatformInput.ApplyAccessibility` writes them onto every surface of a
document, once before the first frame and again on each `SystemAccessibilityChanged`. Both desktop
hosts do it, so an application built on either honours reduced motion without asking.

```csharp no-compile="a fragment; `platform` is an IPlatform and `document` a UiDocument"
PlatformInput.ApplyAccessibility(document, platform.Accessibility);
```

Behind `IPlatform.Accessibility` are three readers, one per desktop platform, each returning the
same `SystemAccessibility`. They are worth knowing individually, because what each operating system
*can* answer differs — and an axis nobody can read is `null` rather than a default.

| Reader | Reduced motion | Forced colours / contrast | Text scale |
|---|---|---|---|
| `MacOSAccessibility` | `reduceMotion` in `com.apple.universalaccess` | `increaseContrast` in the same domain | **nothing to read** — `null` |
| `WindowsAccessibility` | `SPI_GETCLIENTAREAANIMATION` | `SPI_GETHIGHCONTRAST` | `HKCU\Software\Microsoft\Accessibility\TextScaleFactor` |
| `LinuxAccessibility` | `org.gnome.desktop.interface enable-animations` | `org.gnome.desktop.a11y.interface high-contrast` | `org.gnome.desktop.interface text-scaling-factor` |

⚠ **Two of the three motion settings are the *inverse* of the preference.** Windows'
`SPI_GETCLIENTAREAANIMATION` and GNOME's `enable-animations` both answer *may I animate*, so `true`
is the user having said nothing. Reading either straight through ships more animation to precisely
the people who asked for less, which is the failure this whole path exists to prevent.

⚠ **macOS is read out of `NSUserDefaults` and not `NSWorkspace`.** The two agree, and only the
Foundation one is reachable from a process that never created an `NSApplication` — which is every
SDL application, this engine's included. In that domain an *absent* key genuinely means off: the
defaults are written when the setting is first turned on. What is unknown is failing to reach the
Objective-C runtime at all.

⚠ **macOS answers no text scale, and that is a difference between platforms rather than a hole.**
Dynamic Type is a UIKit API; the Mac's equivalents are per-application font sizes or a resolution
change, neither of which is a multiplier a process can read. Windows and GNOME both answer, so
`TextScale` is `null` on one desktop and a number on the other two — never `1.0` standing in for
"did not ask", because a `1.0` cannot be told apart from a user who chose it.

⚠ **`increaseContrast`, not `reduceTransparency`.** They sit beside each other on the same macOS
settings pane and only the first is what `forced-colors` describes: Reduce Transparency changes
materials rather than the palette, and reporting it here would make a stylesheet throw its own
colours away because somebody switched off a blur.

⚠ **Linux costs two subprocesses, so it is read once and never polled.** Following a change there
needs a portal subscription, which these do not have; a missing schema or no `gsettings` at all is
`SystemAccessibility.Unknown` rather than a set of `false`s.

⚠ **Two of the six preference axes have an operating system behind them and four do not.** Reduced
motion and the forced-colours pair are read; `inverted-colors` and the two pointer axes have no
reader on any platform, so an application that wants those still passes them in itself. Android, iOS
and the browser report `SystemAccessibility.Unknown` — each has a source and none of them is wired.

⚠ **An axis the platform could not read is `no-preference` and never the "on" value.** A headless run
and a Linux desktop with no settings daemon both answer `null`, and reading that as "the user asked
for reduced motion" would take the animation off machines that never asked.

⚠ **High contrast answers two questions.** Windows' high-contrast mode and macOS's Increase Contrast
are one switch, and the host sets both `forced-colors` and `prefers-contrast: more` from it — a sheet
written with only the second in it would otherwise do nothing on the platforms where the setting is
most used. The converse is deliberately not wired: asking for more contrast is not asking for the
palette to be replaced.

⚠ **`SetMedia` does not reload the stylesheets.** It re-evaluates the `@media` verdicts for one
scope; the rule set, the keyframes table and the animator stay exactly where they are. A resize
therefore costs a re-evaluation and not a reparse. (`Replace` and `Reload` are the calls that rebuild,
and the reduced-motion switch is carried across one — a hot edit of a `.vcss` is a change of mind
about the stylesheet and never about the user.)

## Examples

**A panel that lays out by its own width.** The context is the surface, so the same sheet works for a
window and for a world-space panel:

```vcss
inspector-row { flex-direction: row; }

@media (max-width: 320px) {
  inspector-row { flex-direction: column; }
}
```

**Honouring a request for less movement.** ⚠ `no-preference` is the feature's false value, so the
bare boolean form is the idiomatic spelling and means `reduce`:

```vcss
@media (prefers-reduced-motion) {
  * { transition-duration: 0ms; }
}
```

**A high-contrast palette.** ⚠ `(prefers-contrast)` is true of *any* stated preference, `custom`
included — it is not a synonym for `more`, and a rule that treats it as one applies to a palette the
user chose for themselves:

```vcss
@media (prefers-contrast: more) { :root { --border: #000; } }
```

## What is not here

A system accent colour, dynamic semantic colours and an OS text-size scale are all absent.

⚠ **`forced-colors` is half here, and the missing half is the renderer's.** The query evaluates, the
platform feeds it and a sheet's `@media (forced-colors: active)` block applies — what does not exist
is a forced-colours *mode*: nothing substitutes a system palette for the colours a sheet asked for.
So the two places that record the gap — `UtilityFamilies`' `outline-hidden` registration and
`DrawListBuilder`'s `outline-style` remarks — are still true about the mode and no longer true about
the query, and both now say which half they mean.


## See also

- [Cascade layers](../ui/cascade-layers.md) — where a media block sits in the order rules are resolved.
- [Accessibility](../ui/accessibility.md) — the four preference features, and what an application owes
  a person who has stated one.
- [Desktop applications](../ui/desktop-application.md) — who fills the context in, and why a surface
  that is never fed one answers every query with its defaults.
