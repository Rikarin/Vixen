// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>One day in a <see cref="Calendar" />.</summary>
/// <remarks>
///     ⚠ <b>Shows a number and is called by a date.</b> The cell says "24" because a month grid has
///     room for two digits; a screen reader landing on it has to hear which 24th, in which month and
///     on which weekday, or arrowing through a calendar is a string of bare numbers. So the
///     accessible name is the long date in the calendar's culture and the label is the day of the
///     month — the same split <c>TimelineHeader</c>'s mute toggle makes between what fits and what is
///     said.
/// </remarks>
public sealed partial class CalendarDay : ButtonBase {
    string? spoken;

    /// <inheritdoc />
    protected override string TagName => "calendar-day";

    /// <inheritdoc />
    /// <remarks>ARIA's date-picker pattern: the days are the cells of a <c>grid</c>, and each is one.</remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.GridCell;

    /// <inheritdoc />
    protected override string? NativeAccessibleName => spoken ?? Label;

    /// <inheritdoc />
    /// <remarks><see cref="Option" />'s answer, for its reason: a chosen cell is a selected one.</remarks>
    protected override AccessibleStates NativeAccessibleState =>
        IsSelected ? AccessibleStates.Selected : AccessibleStates.None;

    /// <summary>The date this cell stands for.</summary>
    public DateOnly Date { get; private set; }

    /// <summary>Whether it is the calendar's chosen date.</summary>
    public bool IsSelected => (State & ElementState.Checked) != 0;

    /// <summary>Points the cell at a date, saying the long form and showing the day of the month.</summary>
    internal void Show(DateOnly date, CultureInfo culture) {
        Date = date;
        Label = date.Day.ToString(culture);

        var said = date.ToString("D", culture);

        if (!string.Equals(said, spoken, StringComparison.Ordinal)) {
            spoken = said;
            InvalidateAccessibility();
        }
    }
}

/// <summary>A month of days to choose one from.</summary>
/// <remarks>
///     <para>
///         <b>The first half of doc 49 § 7.1's rank 5</b>, and the half a date picker is made of.
///         Always six weeks of seven days, the days either side of the month drawn and marked
///         <c>.outside</c> — so the grid is the same height in February as in August, on
///         <see cref="Pagination" />'s argument that a control whose size moves as you page through
///         it moves the thing you were about to click.
///     </para>
///     <para>
///         ⚠ <b>The week starts where the culture says it does, and the names are the culture's.</b>
///         <see cref="Culture" /> decides the first day of the week, the weekday headings, the month
///         title and every cell's spoken date. <c>null</c> is the invariant culture — Sunday first,
///         English names — on <see cref="NumericInput.Culture" />'s terms: a control that quietly
///         followed the thread's culture would change what every calendar prints on a machine setting
///         nobody in the application chose.
///     </para>
///     <para>
///         ⚠ <b>ARIA's date-picker grid, keyboard included.</b> One day is the tab stop — the chosen
///         one, or today's — and the arrows move it: a day either way, a week up and down, Home and
///         End to the ends of the week, Page Up and Page Down a month, and with Shift a year. Moving
///         onto a day of another month shows that month, so the focus never leaves the grid. Enter
///         and Space choose, as a click does.
///     </para>
///     <para>
///         <b>Two nodes in the tree</b>: a <c>group</c> round the whole calendar and a <c>grid</c>
///         round the days, both named by the month title through <c>LabelledBy</c>, so a reader
///         entering the grid hears "September 2026" and then the day. The weekday headings are
///         <c>columnheader</c>s whose names are the full day names; the cells show the shortest.
///     </para>
/// </remarks>
public sealed partial class Calendar : Control {
    const int Weeks = 6;
    const int DaysInWeek = 7;

    readonly CalendarDay[] days = new CalendarDay[Weeks * DaysInWeek];
    readonly UiElement[] weekdays = new UiElement[DaysInWeek];

    DateOnly month;
    DateOnly active;
    DateOnly today;
    CultureInfo? culture;
    bool created;

    /// <inheritdoc />
    protected override string TagName => "calendar";

    /// <inheritdoc />
    /// <remarks>The days and the two arrows are the stops. The calendar is what holds them.</remarks>
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.Group;

    /// <summary>The row across the top: the two arrows and the month between them.</summary>
    public UiElement Header { get; private set; } = null!;

    /// <summary>The month and year being shown, which names the calendar and its grid.</summary>
    public UiElement Title { get; private set; } = null!;

    /// <summary>The arrow that shows the month before.</summary>
    public IconButton Previous { get; private set; } = null!;

    /// <summary>The arrow that shows the month after.</summary>
    public IconButton Next { get; private set; } = null!;

    /// <summary>The weekday headings and the six weeks under them.</summary>
    public UiElement Grid { get; private set; } = null!;

    /// <summary>The forty-two days on show, a week at a time from the top left.</summary>
    public IReadOnlyList<CalendarDay> Days => days;

    /// <summary>The chosen date, or <c>null</c> for none.</summary>
    [UiProperty(Changed = nameof(OnValueChanged))]
    public partial DateOnly? Value { get; set; }

    /// <summary>The earliest date that can be chosen, or <c>null</c> for no limit.</summary>
    [UiProperty(Changed = nameof(OnLimitChanged))]
    public partial DateOnly? Minimum { get; set; }

    /// <summary>The latest date that can be chosen, or <c>null</c> for no limit.</summary>
    [UiProperty(Changed = nameof(OnLimitChanged))]
    public partial DateOnly? Maximum { get; set; }

    /// <summary>Raised when the chosen date changes.</summary>
    public event Action<Calendar, DateOnly?>? ValueChanged;

    /// <summary>The month on show, as its first day.</summary>
    /// <remarks>Any day of a month shows that month.</remarks>
    public DateOnly Month {
        get => month;
        set {
            var first = FirstOf(value);

            if (first == month) {
                return;
            }

            month = first;
            active = Clamp(new DateOnly(first.Year, first.Month, Math.Min(active.Day, DateTime.DaysInMonth(first.Year, first.Month))));
            Rebuild();
        }
    }

    /// <summary>Which day is marked as today.</summary>
    /// <remarks>
    ///     ⚠ <b>A property and not a clock read on every frame</b>, so a test can say what today is
    ///     and an application whose idea of "today" is not the machine's — a game calendar, a
    ///     scheduler working in another time zone — can say so too. The machine's date when the
    ///     calendar is created, until somebody says otherwise.
    ///     <para>
    ///         While nothing is chosen, today is also where the calendar opens and where the keyboard
    ///         lands, so saying what today is moves the month on show to it. A calendar with a
    ///         <see cref="Value" /> stays on the value's month.
    ///     </para>
    /// </remarks>
    public DateOnly Today {
        get => today;
        set {
            today = value;

            if (Value is null) {
                active = Clamp(value);
                month = FirstOf(active);
            }

            Rebuild();
        }
    }

    /// <summary>Which locale's week, names and date formats the calendar uses. <c>null</c> is invariant.</summary>
    public CultureInfo? Culture {
        get => culture;
        set {
            culture = value;
            Rebuild();
        }
    }

    CultureInfo Locale => culture ?? CultureInfo.InvariantCulture;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Header = Part("calendar-header");

        Previous = Header.Add<IconButton>();
        Previous.AddClass("calendar-previous");
        Previous.LeadingIcon.Geometry = ControlIcons.ChevronLeft;
        Previous.Variant = ControlVariant.Subtle;
        Previous.Label = ControlStrings.CalendarPreviousMonth.Text;

        Title = Header.Add("calendar-title");

        Next = Header.Add<IconButton>();
        Next.AddClass("calendar-next");
        Next.LeadingIcon.Geometry = ControlIcons.ChevronRight;
        Next.Variant = ControlVariant.Subtle;
        Next.Label = ControlStrings.CalendarNextMonth.Text;

        Grid = Part("calendar-grid");
        Grid.Role = AccessibleRole.Grid;

        var headings = Grid.Add("calendar-weekdays");
        headings.Role = AccessibleRole.Row;

        for (var i = 0; i < DaysInWeek; i++) {
            var heading = headings.Add("calendar-weekday");
            heading.Role = AccessibleRole.ColumnHeader;
            weekdays[i] = heading;

            // The shortest name drawn on a child so the heading can centre it over its column; the
            // heading itself carries the full name, which is what is said.
            heading.Add("calendar-weekday-name");
        }

        for (var week = 0; week < Weeks; week++) {
            var row = Grid.Add("calendar-week");
            row.Role = AccessibleRole.Row;

            for (var i = 0; i < DaysInWeek; i++) {
                days[(week * DaysInWeek) + i] = row.Add<CalendarDay>();
            }
        }

        // ⚠ Both named by the title and neither by a copy of it: the title changes every time the
        // month does, and a relation is read when it is asked for.
        AddAccessibleRelation(AccessibleRelation.LabelledBy, Title);
        Grid.AddAccessibleRelation(AccessibleRelation.LabelledBy, Title);

        AddHandler<ClickEvent>(static (element, args) => ((Calendar)element).Chosen(args));
        AddHandler<KeyEvent>(static (element, args) => ((Calendar)element).Keyed(args));

        today = DateOnly.FromDateTime(DateTime.Today);
        active = Value ?? today;
        month = FirstOf(active);
        created = true;

        Rebuild();
    }

    /// <summary>The cell showing a date, if it is on show.</summary>
    /// <param name="date">The date.</param>
    /// <returns>The cell, or <c>null</c> when the date is not in the six weeks on show.</returns>
    public CalendarDay? DayOf(DateOnly date) {
        foreach (var day in days) {
            if (day.Date == date) {
                return day;
            }
        }

        return null;
    }

    /// <summary>Puts the keyboard on the day that is the calendar's tab stop.</summary>
    internal void FocusActive() {
        if (DayOf(active) is { } day) {
            Document.Focus(day);
        }
    }

    void Chosen(ClickEvent args) {
        switch (args.Source) {
            case CalendarDay day when Owns(day):
                // ⚠ Read once, here. A day of the next month moves the month, and the rebuild that
                // does it re-points every cell — this one included — so `day.Date` afterwards is
                // whatever date the clicked cell shows now, three weeks from the one clicked.
                var date = day.Date;

                active = date;
                month = FirstOf(date);
                Value = date;
                Rebuild();

                if (DayOf(date) is { } chosen) {
                    Document.Focus(chosen);
                }

                break;

            case IconButton button when ReferenceEquals(button, Previous):
                Month = month.AddMonths(-1);
                break;

            case IconButton button when ReferenceEquals(button, Next):
                Month = month.AddMonths(1);
                break;
        }
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || args.Source is not CalendarDay day || !Owns(day)) {
            return;
        }

        var shift = args.Has(ModifierKeys.Shift);

        if (!shift && !args.Has(ModifierKeys.None)) {
            return;
        }

        var date = day.Date;
        var first = (int)Locale.DateTimeFormat.FirstDayOfWeek;
        var intoWeek = ((int)date.DayOfWeek - first + DaysInWeek) % DaysInWeek;

        DateOnly? target = (args.Key, shift) switch {
            (InputKey.Left, false) => Step(date, -1),
            (InputKey.Right, false) => Step(date, 1),
            (InputKey.Up, false) => Step(date, -DaysInWeek),
            (InputKey.Down, false) => Step(date, DaysInWeek),
            (InputKey.Home, false) => Step(date, -intoWeek),
            (InputKey.End, false) => Step(date, DaysInWeek - 1 - intoWeek),
            (InputKey.PageUp, false) => Months(date, -1),
            (InputKey.PageDown, false) => Months(date, 1),
            (InputKey.PageUp, true) => Months(date, -12),
            (InputKey.PageDown, true) => Months(date, 12),
            _ => null
        };

        if (target is not { } moved) {
            return;
        }

        MoveTo(moved);
        args.Handled = true;
    }

    /// <summary>Makes a date the tab stop, showing its month, and puts the keyboard on it.</summary>
    void MoveTo(DateOnly date) {
        active = Clamp(date);
        month = FirstOf(active);
        Rebuild();

        if (DayOf(active) is { } day) {
            Document.Focus(day);
        }
    }

    void OnValueChanged(DateOnly? previous, DateOnly? current) {
        if (current is { } chosen) {
            active = chosen;
            month = FirstOf(chosen);
        }

        Rebuild();

        Raise(new ValueChangedEvent<DateOnly?> { Previous = previous, Value = current });
        ValueChanged?.Invoke(this, current);
    }

    void OnLimitChanged(DateOnly? previous, DateOnly? current) {
        active = Clamp(active);
        Rebuild();
    }

    /// <summary>Restates every cell, the headings and the title from the month on show.</summary>
    /// <remarks>
    ///     The forty-two cells are made once and re-pointed rather than rebuilt, so a cell that has
    ///     the keyboard keeps it while the month moves under it — the focus is a reference to an
    ///     element, and replacing the element would drop it on the floor between two frames.
    /// </remarks>
    void Rebuild() {
        if (!created) {
            return;
        }

        var locale = Locale;
        var format = locale.DateTimeFormat;
        var first = (int)format.FirstDayOfWeek;

        for (var i = 0; i < DaysInWeek; i++) {
            var weekday = (first + i) % DaysInWeek;
            weekdays[i].Children[0].Text = format.ShortestDayNames[weekday];
            weekdays[i].AccessibleName = format.DayNames[weekday];
        }

        var offset = ((int)month.DayOfWeek - first + DaysInWeek) % DaysInWeek;

        // ⚠ Clamped at both ends of the calendar .NET can represent: the grid for January of the
        // year 1 would otherwise start in December of the year 0, which throws.
        var start = Math.Clamp(month.DayNumber - offset, DateOnly.MinValue.DayNumber, DateOnly.MaxValue.DayNumber - days.Length + 1);

        for (var i = 0; i < days.Length; i++) {
            var date = DateOnly.FromDayNumber(start + i);
            var day = days[i];

            day.Show(date, locale);
            day.Disabled = !Allows(date);
            day.TabIndex = date == active ? 0 : -1;
            day.State = date == Value ? day.State | ElementState.Checked : day.State & ~ElementState.Checked;

            Mark(day, "outside", date.Year != month.Year || date.Month != month.Month);
            Mark(day, "today", date == today);
        }

        Title.Text = month.ToString("Y", locale);

        Previous.Disabled = Minimum is { } minimum && month.DayNumber - 1 < minimum.DayNumber;
        Next.Disabled = Maximum is { } maximum && FirstOf(month.AddMonths(1)) > maximum;
    }

    bool Owns(CalendarDay day) => Array.IndexOf(days, day) >= 0;

    bool Allows(DateOnly date) =>
        (Minimum is not { } minimum || date >= minimum) && (Maximum is not { } maximum || date <= maximum);

    DateOnly Clamp(DateOnly date) {
        if (Minimum is { } minimum && date < minimum) {
            return minimum;
        }

        return Maximum is { } maximum && date > maximum ? maximum : date;
    }

    static DateOnly FirstOf(DateOnly date) => new(date.Year, date.Month, 1);

    static DateOnly? Step(DateOnly date, int by) {
        var number = (long)date.DayNumber + by;

        return number < DateOnly.MinValue.DayNumber || number > DateOnly.MaxValue.DayNumber
            ? null
            : DateOnly.FromDayNumber((int)number);
    }

    /// <remarks><see cref="DateOnly.AddMonths" /> keeps the day where it can and clamps it where it cannot: 31 January and a month is 28 or 29 February.</remarks>
    static DateOnly? Months(DateOnly date, int by) {
        var index = (date.Year * 12L) + date.Month - 1 + by;

        return index < 12 || index >= 10000 * 12 ? null : date.AddMonths(by);
    }

    static void Mark(UiElement element, string name, bool on) {
        if (on) {
            element.AddClass(name);
        } else {
            element.RemoveClass(name);
        }
    }
}

/// <summary>A field showing a date, with a calendar behind it.</summary>
/// <remarks>
///     <para>
///         <b>The second half of doc 49 § 7.1's rank 5.</b> A <see cref="Select" /> whose list is a
///         <see cref="Calendar" />: the same field, the same chevron, the same popover hung off the
///         document root so no scrolling ancestor clips it, and the same <c>combobox</c> role with
///         <c>Owns</c> pointing at a popup that is nowhere near it in the element tree.
///     </para>
///     <para>
///         ⚠ <b>The keyboard goes into the calendar when it opens, which is where this parts company
///         with <see cref="Select" />.</b> A select keeps the focus on its field and points at the
///         current option with <c>aria-activedescendant</c>, because its options are a list the
///         arrows walk. A calendar is a grid a user walks in four directions and pages through, and
///         ARIA's date-picker pattern puts the focus on the day. Escape, a click outside and a
///         choice all close it and give the keyboard back to the field.
///     </para>
///     <para>
///         ⚠ <b>It validates, so a <see cref="Form" /> asks it.</b> <see cref="Required" /> and a
///         date outside <see cref="Minimum" />..<see cref="Maximum" /> — which a bound model can
///         supply, even though the calendar will not offer one — both make it refuse a submission.
///     </para>
///     <para>
///         <b>What it deliberately is not.</b> The field cannot be typed into: a date is chosen from
///         the calendar, or set by code. Typing a date is a parser per locale and a mask, and a
///         field that half-parsed a date would be worse than one that shows it.
///     </para>
/// </remarks>
public sealed partial class DatePicker : Control, IValidated {
    /// <inheritdoc />
    protected override string TagName => "date-picker";

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.ComboBox;

    /// <inheritdoc />
    /// <remarks>The date as the field shows it, which is what a screen reader should say.</remarks>
    protected override string? NativeAccessibleValue => Value is { } date ? Shown(date) : null;

    /// <inheritdoc />
    /// <remarks><see cref="Select.NativeAccessibleState" />'s four, for its reasons.</remarks>
    protected override AccessibleStates NativeAccessibleState =>
        AccessibleStates.Expandable
        | (IsOpen ? AccessibleStates.Expanded : AccessibleStates.None)
        | (Required ? AccessibleStates.Required : AccessibleStates.None)
        | (IsValid ? AccessibleStates.None : AccessibleStates.Invalid);

    /// <summary>Where the date is written.</summary>
    public UiElement Field { get; private set; } = null!;

    /// <summary>The chevron on the right of the field.</summary>
    public Icon Chevron { get; private set; } = null!;

    /// <summary>The floating panel the calendar is in.</summary>
    public Popover Popup { get; private set; } = null!;

    /// <summary>The calendar in it.</summary>
    public Calendar Calendar { get; private set; } = null!;

    /// <summary>Whether the calendar is showing.</summary>
    public bool IsOpen => Popup.IsOpen;

    /// <summary>The chosen date, or <c>null</c> for none.</summary>
    [UiProperty(Changed = nameof(OnValueChanged))]
    public partial DateOnly? Value { get; set; }

    /// <summary>The earliest date the calendar offers, or <c>null</c> for no limit.</summary>
    [UiProperty(Changed = nameof(OnLimitChanged))]
    public partial DateOnly? Minimum { get; set; }

    /// <summary>The latest date the calendar offers, or <c>null</c> for no limit.</summary>
    [UiProperty(Changed = nameof(OnLimitChanged))]
    public partial DateOnly? Maximum { get; set; }

    /// <summary>Whether a date has to be chosen.</summary>
    [UiProperty(Changed = nameof(OnRequiredChanged))]
    public partial bool Required { get; set; }

    /// <summary>What the field says when no date is chosen.</summary>
    [UiProperty(Changed = nameof(OnPlaceholderChanged))]
    public partial string? Placeholder { get; set; }

    /// <summary>Raised when the chosen date changes.</summary>
    public event Action<DatePicker, DateOnly?>? ValueChanged;

    /// <summary>Which locale the date is written in and the calendar is laid out in. <c>null</c> is invariant.</summary>
    public CultureInfo? Culture {
        get => Calendar.Culture;
        set {
            Calendar.Culture = value;
            Restate();
        }
    }

    /// <summary>Whether the date held is acceptable: present if it is required, and inside the limits.</summary>
    public bool IsValid =>
        Value is not { } date
            ? !Required
            : (Minimum is not { } minimum || date >= minimum) && (Maximum is not { } maximum || date <= maximum);

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Field = Part("date-picker-field");
        Chevron = Part<Icon>();
        Chevron.AddClass("date-picker-chevron");
        Chevron.Geometry = ControlIcons.ChevronDown;

        // ⚠ On the root, for `SelectBase.List`'s reason: an overlay inside the field it drops out of
        // is clipped by every scrolling ancestor between the two. `OnRemoved` pays for it.
        Popup = Document.Root.Add<Popover>();
        Popup.AddClass("date-picker-popup");
        Popup.Placement = Placement.Bottom;
        Popup.Role = AccessibleRole.Dialog;
        Popup.AccessibleName = ControlStrings.DatePickerDialog.Text;

        Calendar = Popup.Content.Add<Calendar>();

        // The popup is a root child, so nothing in the element tree connects it to this field.
        AddAccessibleRelation(AccessibleRelation.Owns, Popup);

        Popup.OpenChanged += (_, open) => {
            State = open ? State | ElementState.Open : State & ~ElementState.Open;
            InvalidateAccessibility();

            // The keyboard comes back out with it. Left on a day inside a hidden popover, it would
            // be talking to something nobody can see.
            if (!open && !IsRemoved && Within(Document.Focused, Popup)) {
                Document.Focus(this);
            }
        };

        // ⚠ A day's click and not the calendar's `ValueChanged`, because choosing the day that is
        // already chosen changes nothing and still has to close the popup — a user who opens the
        // calendar and presses Enter on the highlighted date has answered the question. The calendar
        // hears the click first, being deeper, so its value is already the day by the time this runs.
        Popup.AddHandler<ClickEvent>((_, args) => {
            if (args.Source is CalendarDay day && ReferenceEquals(Calendar.DayOf(day.Date), day)) {
                Value = Calendar.Value;
                Close(CloseReason.Committed);
            }
        });

        AddHandler<PointerEvent>(static (element, args) => ((DatePicker)element).Pointed(args));
        AddHandler<KeyEvent>(static (element, args) => ((DatePicker)element).Keyed(args));

        Restate();
        Revalidate();
    }

    /// <inheritdoc />
    /// <remarks>The popup is a root child, so the subtree removal does not reach it. See its creation.</remarks>
    protected override void OnRemoved() {
        if (Popup is { IsRemoved: false }) {
            Document.Remove(Popup);
        }

        base.OnRemoved();
    }

    /// <summary>Shows the calendar and puts the keyboard on its chosen day, or on today.</summary>
    public void Open() {
        if (Disabled) {
            return;
        }

        Popup.Open(this);
        Calendar.FocusActive();
    }

    /// <summary>Hides the calendar.</summary>
    /// <param name="reason">Why.</param>
    public void Close(CloseReason reason = CloseReason.Code) {
        if (Popup is { IsRemoved: false }) {
            Popup.Close(reason);
        }
    }

    /// <summary>Republishes the verdict.</summary>
    /// <remarks>Public on <see cref="TextField.Revalidate" />'s terms.</remarks>
    public void Revalidate() {
        if (FieldValidity.Publish(this, Required, IsValid)) {
            InvalidateAccessibility();
        }
    }

    void Pointed(PointerEvent args) {
        if (args is not { Action: PointerAction.Pressed, Button: PointerButton.Primary } || Disabled) {
            return;
        }

        Document.Focus(this);

        // Toggling, for `SelectBase.Pointed`'s reason: the press the popover's light dismiss closes
        // it on would otherwise be followed by a click that opens it again.
        if (IsOpen) {
            Close();
        } else {
            Open();
        }

        args.Handled = true;
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || !ReferenceEquals(args.Source, this) || IsOpen) {
            return;
        }

        var opens = args.Key switch {
            InputKey.Space or InputKey.Enter or InputKey.KeypadEnter => args.Has(ModifierKeys.None),
            InputKey.Down => args.Has(ModifierKeys.None) || args.Has(ModifierKeys.Alt),
            _ => false
        };

        if (!opens) {
            return;
        }

        Open();
        args.Handled = true;
    }

    void OnValueChanged(DateOnly? previous, DateOnly? current) {
        Calendar.Value = current;
        Restate();

        // Before the notifications, so a handler reading `IsValid` sees the verdict on the date it
        // was just handed.
        Revalidate();
        InvalidateAccessibility();

        Raise(new ValueChangedEvent<DateOnly?> { Previous = previous, Value = current });
        ValueChanged?.Invoke(this, current);
    }

    void OnLimitChanged(DateOnly? previous, DateOnly? current) {
        Calendar.Minimum = Minimum;
        Calendar.Maximum = Maximum;
        Revalidate();
    }

    void OnRequiredChanged(bool previous, bool current) {
        Revalidate();
        InvalidateAccessibility();
    }

    void OnPlaceholderChanged(string? previous, string? current) => Restate();

    void Restate() {
        if (Field is null) {
            return;
        }

        Field.Text = Value is { } date ? Shown(date) : Placeholder;

        // On the control for `select.empty`'s sake — the one a stylesheet reaches for — and on the
        // field so the theme can grey the placeholder with a compound selector rather than a
        // descendant one.
        Mark(this, Value is null);
        Mark(Field, Value is null);

        static void Mark(UiElement element, bool empty) {
            if (empty) {
                element.AddClass("empty");
            } else {
                element.RemoveClass("empty");
            }
        }
    }

    /// <summary>The date as the field writes it: the culture's short date.</summary>
    string Shown(DateOnly date) => date.ToString("d", Calendar.Culture ?? CultureInfo.InvariantCulture);

    static bool Within(UiElement? element, UiElement ancestor) {
        for (var walk = element; walk is not null; walk = walk.Parent) {
            if (ReferenceEquals(walk, ancestor)) {
                return true;
            }
        }

        return false;
    }
}
