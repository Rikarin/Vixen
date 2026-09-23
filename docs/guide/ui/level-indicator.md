---
title: Level indicator
slug: ui/level-indicator
kind: guide
area: Core
summary: LevelIndicator shows how much of a capacity is in use, and whether that is a problem. ProgressBar draws nearly the same picture and means something else — a job, which only ever goes up and whose full state is the good one.
api: [T:Vixen.Ui.Controls.LevelIndicator, T:Vixen.Ui.Controls.LevelReading]
tags: [ui, controls, readout, accessibility, vxml]
since: 0.2
status: preview
related: [ui/accessibility, ui/key-value-list, ui/markup-panels]
---

## What it is

`LevelIndicator` is a readout for a capacity: a disk, a battery, a memory pool, a budget.

```vxml
<LevelIndicator Maximum="512" Value="446" Warning="384" Critical="461" />
```

The reading is `Value` between `Minimum` and `Maximum`, as for every other
[range control](../../api/Vixen.Ui.Controls.RangeBase). What this one adds is two lines drawn across
that range — `Warning` and `Critical` — and a `Level` that says which side of them the reading is on.

## What it is for

Doc 49 § 7.1 ranks it sixth of the controls this set was missing, and the reason is that
`ProgressBar` was the only readout in it. The two draw nearly the same picture and mean opposite
things:

|                      | `ProgressBar`                | `LevelIndicator`                       |
| -------------------- | ---------------------------- | -------------------------------------- |
| what the number is   | how far through a job        | how much of a capacity is in use       |
| which way it moves   | up, and then it is finished  | both, for ever                         |
| what *full* means    | done                         | possibly the thing you are worried about |
| what a reader hears  | `progressbar`                | `meter`                                |

⚠ **The accessible role is the substance of that table, not the colour.** A screen reader told
"progress bar, eighty-seven per cent" about a disk has been told a job is nearly done. `LevelIndicator`
reports [`AccessibleRole.Meter`](../../api/Vixen.Ui.AccessibleRole) — ARIA's `meter`, which exists for
exactly this distinction — and announces **the reading** rather than a fraction, because a meter's
bounds are the capacity and "446" is the number the listener wanted, not "0.87".

## Using it

### Which direction is bad

⚠ **The order of the two thresholds is the direction, and there is no flag.** A disk gets worse as it
fills; a battery gets worse as it empties. Both are level indicators, and the control tells them apart
from the numbers alone:

```vxml
<!-- A disk. Critical is above Warning, so a bigger reading is a worse one. -->
<LevelIndicator Value="@Used" Warning="0.7" Critical="0.9" />

<!-- A battery. Critical is below Warning, so a smaller reading is a worse one. -->
<LevelIndicator Value="@Charge" Warning="0.3" Critical="0.1" />
```

A `Descending` property beside those two numbers could contradict them, and a control that is
configured-looking and silently wrong is worse than one that is awkward. Two numbers cannot disagree
with themselves.

Leave either threshold unset — they default to `float.NaN` — and nothing on that line ever fires.
A single threshold on its own reads upward, because a lone line on a capacity is a ceiling.

### What it looks like

The level reaches the stylesheet as a class, so the theme owns the colours:

```vcss
level-indicator.warning { --fill-color: var(--warning); }
level-indicator.critical { --fill-color: var(--danger); }
```

`ControlTheme.vcss` writes exactly those two rules, and `--warning` is a palette token added with this
control: warning and danger are not the same word. Danger says something has gone wrong or is about to
be destroyed; warning says a reading is heading somewhere and there is still time.

Set `Segments` above zero to draw blocks instead of a bar — signal strength, a rating, a battery with
cells:

```vxml
<LevelIndicator Value="@Signal" Segments="4" />
```

⚠ **The lit block count rounds rather than truncating.** Four blocks at seven-tenths is three, not
two. A truncating version shows its last block only at the very top of the range, so a meter resting
anywhere near a boundary reads one block low for as long as it rests there. The gap between blocks is
`--segment-gap` and is taken *out of* each block rather than added between them, so a segmented
indicator and a continuous one of the same width end in the same place.

### From C&#35;

```csharp no-compile="a fragment; `panel` is the caller's own"
var disk = panel.Add<LevelIndicator>();

disk.Maximum = 512f;
disk.Warning = 384f;
disk.Critical = 461f;
disk.Value = 446f;

if (disk.Level == LevelReading.Critical) {
    // 446 of 512 is past the critical line, so this branch is taken.
}
```

`Level` is computed from the three numbers every time it is read rather than stored, so it cannot come
to disagree with them — the failure a cached level has is that it is right until somebody moves a
threshold and never touches the value again.

## Examples

A disk-space row in a settings panel, a signal strength, and a battery — three readings, three
directions, one control:

```vxml
<KeyValueList>
    <key-value-row>
        <key-value-key>Disk</key-value-key>
        <key-value-value>
            <LevelIndicator Maximum="512" Value="@Used" Warning="384" Critical="461" />
        </key-value-value>
    </key-value-row>

    <key-value-row>
        <key-value-key>Signal</key-value-key>
        <key-value-value><LevelIndicator Value="@Signal" Segments="4" /></key-value-value>
    </key-value-row>

    <key-value-row>
        <key-value-key>Battery</key-value-key>
        <key-value-value>
            <!-- Critical below Warning, so this one reads downward. -->
            <LevelIndicator Value="@Charge" Warning="0.3" Critical="0.1" />
        </key-value-value>
    </key-value-row>
</KeyValueList>
```

`ControlTheme.vcss` already stretches a `level-indicator` inside a `key-value-value`, alongside every
other control that belongs in that half of a row.

## What it deliberately does not do

- **It is not focusable and takes no input.** A readout is not operated; `ProgressBar` makes the same
  choice for the same reason. Bind it to a value and let something else change that value.
- **It has no text.** A number beside a bar is a `LabeledContent` or a `KeyValueList` row with this in
  it, and the words belong to the application — "446 GB of 512 GB" is not a sentence this assembly can
  write without knowing what is being measured.
- **It does not warn about itself.** Passing `Critical` adds a class and changes an announcement; it
  raises no event and shows no dialog. What to do about a full disk is the application's decision.

## See also

- [Accessibility](accessibility) — the role tree these readings are announced through, and why a
  `meter` is not a `progressbar`.
- [Key-value list](key-value-list) — where a reading usually sits, beside the word for what it is.
- [Labeled content](labeled-content) — the same, for a form row rather than a table of facts.
