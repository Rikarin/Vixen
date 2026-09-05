---
title: Media queries and user preferences
slug: ui/media-queries
kind: guide
area: Core
summary: What `@media` can ask about in Vixen — the size, resolution and gamut of the surface a panel is on rather than of the monitor, the colour-scheme preference, and `prefers-reduced-motion`, which is a query and a switch the animator honours because a toolkit whose default is to animate anyway ignores the preference in every application that forgot.
api: [T:Vixen.Ui.Styling.MediaContext, T:Vixen.Ui.Styling.ColorSchemePreference, T:Vixen.Ui.Styling.MotionPreference]
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

## Feeding it

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

⚠ **No platform assembly reads any of these preferences from the operating system yet.** Width,
height, resolution and gamut are fed from the surface; `prefers-color-scheme` and
`prefers-reduced-motion` are fed by whatever the application passes and by nothing else, so an
application that wants them from the system asks the system itself and passes the answer in. The
values a bridge would read are `NSWorkspace.accessibilityDisplayShouldReduceMotion` on macOS,
`SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)` on Windows, `gtk-enable-animations` on Linux and
`matchMedia` in a browser.

⚠ **`SetMedia` does not reload the stylesheets.** It re-evaluates the `@media` verdicts for one
scope; the rule set, the keyframes table and the animator stay exactly where they are. A resize
therefore costs a re-evaluation and not a reparse. (`Replace` and `Reload` are the calls that rebuild,
and the reduced-motion switch is carried across one — a hot edit of a `.vcss` is a change of mind
about the stylesheet and never about the user.)

## What is not here

`forced-colors`, a system accent colour, dynamic semantic colours and an OS text-size scale are all
absent. `forced-colors` in particular is deliberately not added as a query alone: without a mode the
renderer honours, `@media (forced-colors: active)` would evaluate and change nothing, and the two
places that record its absence — `UtilityFamilies`' `outline-hidden` registration and
`DrawListBuilder`'s `outline-style` remarks — would become half-true rather than true.
