---
title: Date picker and calendar
slug: ui/date-picker
kind: guide
area: Core
summary: DatePicker is a select-shaped field whose list is a Calendar — a six-week month grid laid out by the culture and walked by the keyboard in ARIA's date-picker pattern. The calendar also stands on its own.
api: [T:Vixen.Ui.Controls.DatePicker, T:Vixen.Ui.Controls.Calendar, T:Vixen.Ui.Controls.CalendarDay]
tags: [ui, controls, forms, dates, accessibility, vxml]
since: 0.2
status: preview
related: [ui/form, ui/labeled-content, ui/accessibility]
---

## What it is

`Calendar` is a month of days to choose one from. `DatePicker` is a field showing a date, with a
calendar in a popup behind it.

```vxml
<LabeledContent Label="Due">
    <DatePicker Placeholder="No date" Required="true" bind:Value="@Model.Due.Value" />
</LabeledContent>
```

The calendar is always six weeks of seven days, starting on the culture's first day of the week, with
the days of the months either side drawn and marked `.outside`. So it is the same size in February as
in August — a control that grows and shrinks as you page through it moves the thing you were about to
click. The chosen day is `:checked`, today's has the `.today` class, and a day outside `Minimum` and
`Maximum` is disabled.

## What it is for

Doc 49 § 7.1's fifth rank: there was no way to ask for a date. The two controls follow ARIA's
date-picker pattern.

- ⚠ **The calendar is a grid, and the keyboard walks it.** One day is the tab stop — the chosen one, or
  today's — and the arrows move it: a day either way, a week up and down, Home and End to the ends of
  the week, Page Up and Page Down a month, and with Shift a year. Moving onto a day of another month
  shows that month, so the keyboard never leaves the grid. Enter and Space choose, as a click does. A
  month on from 31 January is 28 February, not 3 March.
- ⚠ **A day shows a number and is called by its date.** The cell says "24"; a screen reader landing on
  it hears the culture's long date, "Thursday, 24 September 2026", or arrowing through a month would be
  a string of bare numbers.

  ```text
  group "September 2026"
    button "Previous month"
    button "Next month"
    grid "September 2026"
      row
        columnheader "Monday"
        …
      row
        gridcell "Monday, 31 August 2026"
        …
  ```

- ⚠ **The picker puts the keyboard in the calendar**, which is where it parts company with `Select`. A
  select keeps the focus on its field and points at the current option with `aria-activedescendant`; a
  calendar is walked in four directions, so the focus goes onto the day. Choosing a day — including
  the one already chosen — closes the popup, and so do Escape and a click outside. All three give the
  keyboard back to the field.
- **It validates**, so a [`Form`](form) asks it: `Required` with no date, or a date outside
  `Minimum`..`Maximum` (which a bound model can supply even though the calendar will not offer one),
  refuses the submission.

## Using it

`Value` is a `DateOnly?` on both controls, bindable. `ValueChanged` is raised when it changes, and so is
the routed `ValueChangedEvent<DateOnly?>`.

```csharp no-compile="a fragment; `form` and `SaveDue` are the caller's own"
var due = form.Add<DatePicker>();
due.Minimum = DateOnly.FromDateTime(DateTime.Today);
due.ValueChanged += (_, date) => SaveDue(date);
```

`Calendar.Month` is the month on show (any day of it), `Today` is the day marked as today — the
machine's date until something says otherwise, which a test and a game calendar both do — and
`DayOf(date)` is the cell showing a date. A picker's calendar is `picker.Calendar`.

⚠ **`Culture` decides the week, the names and the formats**, and `null` is the invariant culture:
Sunday first, English names, `MM/dd/yyyy` in the field. That is `NumericInput.Culture`'s rule — a
control that followed the thread's culture would change what every application prints on a machine
setting nobody in it chose. ⚠ And this repository builds with `InvariantGlobalization`, so a named
culture is not there to look up: a locale is a clone of the invariant culture with its
`DateTimeFormat` written out.

```csharp no-compile="a fragment; `picker` is the caller's own"
var czech = (CultureInfo) CultureInfo.InvariantCulture.Clone();
czech.DateTimeFormat.FirstDayOfWeek = DayOfWeek.Monday;
czech.DateTimeFormat.ShortDatePattern = "d. M. yyyy";
picker.Culture = czech;
```

**Limits.**

- ⚠ **The field cannot be typed into.** A date is chosen from the calendar or set by code. Typing one is
  a parser and a mask per locale, and a field that half-parsed a date would be worse than one that
  shows it.
- **No range selection, no time of day, no week numbers.**
- The month title uses the culture's `YearMonthPattern`, which for the invariant culture is
  "2026 September".

## Examples

**A standalone calendar**, for a scheduling panel that shows the month rather than hiding it behind a
field:

```vxml
<Calendar bind:Value="@Model.Day.Value" />
```

**Restyling the marks.** The cells are `calendar-day` elements carrying `.today` and `.outside`, and
the chosen one is `:checked`:

```css
calendar-day.today { border-color: var(--accent); }
calendar-day.outside { opacity: 0.4; }
```

⚠ The forty-two cells are made once and re-pointed at new dates as the month moves, so a class an
application adds to a cell stays on the *cell*, not on the date — mark dates from the classes the
calendar maintains, not by hand.

## See also

- [Form](form) — what refuses a submission while a required date is missing.
- [Labeled content](labeled-content) — the row that names the picker.
- [Accessibility](accessibility) — roles, names and the gates that hold them.
