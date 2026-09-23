---
title: Gauge
slug: ui/gauge
kind: guide
area: Core
summary: Gauge is a level indicator drawn as a dial — the same reading, the same two thresholds and the same levels, as an arc that opens at the bottom and fills clockwise.
api: [T:Vixen.Ui.Controls.Gauge]
tags: [ui, controls, readout, accessibility, vxml]
since: 0.2
status: preview
related: [ui/level-indicator, ui/accessibility]
---

## What it is

`Gauge` shows a reading between `Minimum` and `Maximum` as an arc — a CPU load in a status panel, a
frame budget in an overlay, a tank on a HUD.

```vxml
<Gauge Maximum="16.6" Value="@FrameTime" Warning="12" Critical="15" />
```

## What it is for

Doc 49 § 7.1 ranks "Gauge / LevelIndicator" sixth of the controls this set was missing. It is the
[level indicator](level-indicator) drawn round a circle, and deliberately nothing more. A bar suits a
reading that sits in a row beside the word for what it measures; a dial suits one that sits on its
own — the corner of an overlay, the middle of a card. What the reading *means* is the same, so the
two share everything that decides it: a capacity rather than a job, announced as ARIA's `meter` with
the reading as its value.

## Using it

### Thresholds and levels

`Warning`, `Critical`, `Direction` and `Level` are the level indicator's, computed by the same code —
a bar and a dial over one number cannot disagree about whether it is a problem. In short: with two
lines their order is the direction, with one line `Direction` says which way is worse, and an unset
line (`float.NaN`) never fires. The [level indicator page](level-indicator) has the reasoning,
including why a lone line with no stated direction reads upward.

```vxml
<!-- A tank with one line, which says a smaller reading is the worse one. -->
<Gauge Value="@Fuel" Critical="0.1" Direction="Falling" />
```

The level reaches the stylesheet as the same two classes, and `ControlTheme.vcss` colours them the
same way:

```vcss
gauge.warning { --fill-color: var(--warning); }
gauge.critical { --fill-color: var(--danger); }
```

### Shape

⚠ **The dial opens at the bottom and fills clockwise from its lower left**, which is what a
speedometer, a pressure gauge and every audio meter with a needle does. `Sweep` is how much of a
whole turn it spans: three quarters by default, held between a tenth and one. A sweep of one is a
closed ring whose ends meet at the bottom.

The arc's width is the theme's, as `--arc-width` — five pixels on the default 48-pixel dial:

```vcss
gauge.large { width: 96px; height: 96px; --arc-width: 10px; }
```

The dial is drawn in the largest square that fits the element and centred in it. `Orientation`,
which every range control inherits, means nothing to a dial and is ignored.

### From C&#35;

```csharp no-compile="a fragment; `panel` is the caller's own"
var load = panel.Add<Gauge>();

load.Warning = 0.7f;
load.Critical = 0.9f;
load.Value = 0.95f;

// load.Level is LevelReading.Critical, and the element carries the `critical` class.
```

## Examples

A frame budget beside a memory pool, and a fuel tank with a single line:

```vxml
<div class="flex gap-4">
    <!-- 16.6 ms is a frame at 60 Hz; the pair rises, so a bigger reading is worse. -->
    <Gauge Maximum="16.6" Value="@FrameTime" Warning="12" Critical="15" />

    <Gauge Maximum="512" Value="@PoolUsed" Warning="384" Critical="461" />

    <!-- One line and falling: critical below a tenth, ordinary when full. -->
    <Gauge Value="@Fuel" Critical="0.1" Direction="Falling" />
</div>
```

`Samples/02-HelloUi`'s gallery puts one beside the level indicators in its Feedback card, driven past
both of its lines by the same clock.

## What it deliberately does not do

- **No needle.** The filled arc's end is the needle; a separate one is another shape to antialias at
  every angle and says nothing the arc does not.
- **No text in the middle.** "87 %" is not a sentence this assembly can write without knowing what is
  being measured — the level indicator's rule. Put a label over it.
- **Square ends.** A round cap reaches past the reading by half the arc's width, so an empty gauge
  would still show a dot.
- **Not focusable.** It is a readout, announced as ARIA's `meter` with the reading itself as its value.

## See also

- [Level indicator](level-indicator) — the same reading as a bar, and where the threshold rules are
  explained.
- [Accessibility](accessibility) — why a capacity is a `meter` and not a `progressbar`.
