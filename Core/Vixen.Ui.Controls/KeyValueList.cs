// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls;

/// <summary>One key and what it is worth, as a row of a <see cref="KeyValueList" />.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The value half is an element, not a string, and that is the whole reason this is a
///         control rather than two <c>div</c>s.</b> A settings panel puts a <see cref="TextBox" />, a
///         <see cref="Switch" /> or a <see cref="Slider" /> where a debugger puts a number, and a row
///         that only took text would be rewritten by hand the first time somebody needed to edit
///         one. <see cref="ValuePart" /> is where either goes, and
///         <see cref="UiElement.ContentHost" /> points at it so that <c>&lt;KeyValueRow
///         Key="Volume"&gt;&lt;Slider /&gt;&lt;/KeyValueRow&gt;</c> in a <c>.vxml</c> lands the
///         slider in the right half without the markup naming a part.
///     </para>
///     <para>
///         ⚠ <b><see cref="Value" /> and a child element are exclusive, and the framework rather
///         than this control is what says so.</b> <c>UiElement.OnTextChanged</c> attaches a measure
///         function to an element with text, and the layout algorithm treats a node that measures
///         itself as a leaf — it does not lay out its children at all. So a slot holding both would
///         draw the string and silently place the editor nowhere. Setting <see cref="Value" />
///         therefore empties the slot first, and <see cref="Content{T}" /> clears the text; both
///         directions are safe, which is what a pooled row switching between the two needs.
///     </para>
///     <para>
///         <b>No third column.</b> Every hand-rolled version of this in the editor that has one uses
///         it for a different thing — a byte count's unit, a budget bar, a diagnostic's stage — and a
///         generic third slot is a slot with no meaning that every caller has to decide about. Two
///         halves is the shape that is the same everywhere; anything else belongs in the value half
///         as an element of its own.
///     </para>
/// </remarks>
public sealed partial class KeyValueRow : Control {
    /// <inheritdoc />
    protected override string TagName => "key-value-row";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The name on the left.</summary>
    [UiProperty(Changed = nameof(OnKeyChanged))]
    public partial string? Key { get; set; }

    /// <summary>The text on the right, for the rows whose value is one.</summary>
    /// <remarks>
    ///     Setting this removes whatever was in <see cref="ValuePart" />, for the reason the type's
    ///     remarks give: an element with text does not lay out its children.
    /// </remarks>
    [UiProperty(Changed = nameof(OnValueChanged))]
    public partial string? Value { get; set; }

    /// <summary>Whether the row names a group of the rows below it rather than a value of its own.</summary>
    /// <remarks>
    ///     ⚠ <b>A property rather than a separate control, because a heading has to be a sibling of
    ///     the rows it heads.</b> The stripe is <c>:nth-child</c>, which counts every element child —
    ///     so a heading built out of something else would still take a position in the alternation
    ///     while looking like it had not. Written through to a class, the way every other appearance
    ///     on a control is.
    /// </remarks>
    [UiProperty(Changed = nameof(OnIsHeadingChanged))]
    public partial bool IsHeading { get; set; }

    /// <summary>The left half's element.</summary>
    public UiElement KeyPart { get; private set; } = null!;

    /// <summary>The right half's element: where a value's text goes, and where an editor goes.</summary>
    public UiElement ValuePart { get; private set; } = null!;

    /// <inheritdoc />
    protected override UiElement ContentHost => ValuePart;

    /// <summary>Puts a control in the value half, in place of whatever was there.</summary>
    /// <typeparam name="T">What to put there.</typeparam>
    /// <returns>The control.</returns>
    /// <remarks>
    ///     Clears <see cref="Value" /> as well as the slot's children — see the type's remarks. A
    ///     caller wanting two things side by side adds them to <see cref="ValuePart" /> itself; this
    ///     is the one-editor case, which is nearly all of them.
    /// </remarks>
    public T Content<T>() where T : UiElement, new() {
        // ⚠ The property, not just the part's text. Leaving `Value` holding the string the slot no
        // longer shows makes the next assignment of that same string a no-op — the change callback
        // never runs, the editor is never taken out, and the row shows an editor while claiming to
        // show a value. It cost a test to find and it is the only order that is right.
        Value = null;
        Empty();

        return ValuePart.Add<T>();
    }

    /// <summary>Puts the row back to a blank key and a blank value, for a pool that is reusing it.</summary>
    public void Reset() {
        Value = null;
        Empty();

        Key = null;
        IsHeading = false;
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        KeyPart = Part("key-value-key");
        ValuePart = Part("key-value-value");
    }

    void Empty() {
        ValuePart.Text = null;

        while (ValuePart.Children.Count > 0) {
            ValuePart.Children[^1].Remove();
        }
    }

    void OnKeyChanged(string? previous, string? current) => KeyPart.Text = current;

    void OnValueChanged(string? previous, string? current) {
        if (current is not null) {
            while (ValuePart.Children.Count > 0) {
                ValuePart.Children[^1].Remove();
            }
        }

        ValuePart.Text = current;
    }

    void OnIsHeadingChanged(bool previous, bool current) {
        if (current) {
            AddClass("heading");
        } else {
            RemoveClass("heading");
        }
    }
}

/// <summary>A column of key/value rows: equal halves, alternating shades, one row per fact.</summary>
/// <remarks>
///     <para>
///         <b>The editor grew five of these by hand</b> — a frame debugger's state pane, a memory
///         panel, a statistics panel, a build analysis and a compositor's settings — and every one of
///         them reimplemented the same pooling loop, the same two-element row and a slightly
///         different set of column widths. This is that shape, once, with the parts named so a
///         stylesheet can reach them and the rows addressable from a <c>.vxml</c>.
///     </para>
///     <para>
///         ⚠ <b>Not <c>PropertyGrid</c> generalised, and the reason is what a
///         <c>PropertyRow</c> <i>is</i>.</b> That row carries a <c>MemberDescriptor</c>, the path
///         through the boxes that reaches it, a nesting depth and a reset button — it is a view of a
///         member of an object, and its whole value is that the grid builds it by asking a type about
///         itself. Nothing here has a type to ask. Stripping a property row down until it fitted a
///         memory panel's byte counts would leave two columns and a class, which is this, and would
///         leave the inspector's row in an assembly that cannot depend on <c>Vixen.Core.Reflection</c>.
///         The two share a stylesheet's worth of ideas and no code.
///     </para>
///     <para>
///         ⚠ <b>Rows are direct children, which is a constraint rather than a convenience.</b> The
///         stripe is <c>key-value-row:nth-child(even)</c>, so anything else parented here — a
///         heading built out of a <c>div</c>, an empty-state, a separator — takes a position in the
///         alternation and shifts every row after it. The cascade is what re-resolves the stripe when
///         a row is inserted or removed in the middle, and it does that for free; a class applied
///         when the row was built would be stale from the first mutation, which is the bug this
///         choice exists to avoid. A caller with something else to show puts it beside the list, not
///         in it.
///     </para>
///     <para>
///         <b>The halves are equal by <c>flex-basis: 0</c> and <c>min-width: 0</c> together.</b>
///         Either alone is not enough: a flex item's automatic minimum size is its content, so a long
///         unbreakable key with only the basis set measures its whole string and overhangs the value
///         column — 388 pixels into a 200-pixel row, in the test that says so.
///     </para>
/// </remarks>
public sealed partial class KeyValueList : Control {
    readonly List<KeyValueRow> rows = [];

    /// <inheritdoc />
    protected override string TagName => "key-value-list";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The rows, in order, including any this list is holding parked.</summary>
    public IReadOnlyList<KeyValueRow> Rows => rows;

    /// <summary>How many rows are showing.</summary>
    /// <remarks>
    ///     The live ones, not <see cref="Rows" />'s count: a list that has been shortened is still
    ///     holding the rows it may need next time, and a caller asking how much is on screen means
    ///     the ones that are.
    /// </remarks>
    public int Count { get; private set; }

    /// <summary>Adds a row at the end.</summary>
    /// <param name="key">The name on the left.</param>
    /// <param name="value">The text on the right, if it is text.</param>
    /// <returns>The row.</returns>
    /// <remarks>
    ///     Called <c>AddRow</c> rather than <c>Add</c> because <see cref="UiElement.Add(string, string, ReadOnlySpan{string})" />
    ///     already takes two strings and means something else entirely — a tag and an id. Two
    ///     overloads a call could not choose between is worse than a longer name.
    /// </remarks>
    public KeyValueRow AddRow(string? key = null, string? value = null) {
        var row = Row(Count);

        row.Key = key;
        row.Value = value;

        return row;
    }

    /// <summary>The row at an index, making it and everything before it if they do not exist yet.</summary>
    /// <param name="index">Which row.</param>
    /// <returns>The row, unparked and counted.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is negative.</exception>
    /// <remarks>
    ///     ⚠ <b>The pooling loop every one of the five hand-rolled versions wrote.</b> A panel
    ///     refreshed sixty times a second must not build and discard a few hundred elements to say
    ///     the same thing it said last frame — so a row is kept, reused, and hidden when the data got
    ///     shorter. <see cref="Trim" /> is the other half; the pair replaces a <c>while (pool.Count
    ///     &lt;= slot)</c> and a trailing park loop at each call site.
    /// </remarks>
    public KeyValueRow Row(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        while (rows.Count <= index) {
            rows.Add(Add<KeyValueRow>());
        }

        var row = rows[index];
        row.RemoveClass("parked");

        if (index >= Count) {
            Count = index + 1;
        }

        return row;
    }

    /// <summary>Hides every row from an index on, keeping them for the next time.</summary>
    /// <param name="count">How many rows are live.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is negative.</exception>
    /// <remarks>
    ///     ⚠ <b>Parked rather than removed, and parked at the <i>end</i>, which is what keeps the
    ///     stripe honest.</b> A hidden element still counts for <c>:nth-child</c> — the element tree
    ///     has no anchor nodes precisely so that nothing invisible can shift a striped list — so
    ///     surplus rows are only harmless where they are, after the last live one.
    /// </remarks>
    public void Trim(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        for (var index = count; index < rows.Count; index++) {
            rows[index].AddClass("parked");
        }

        Count = Math.Min(count, rows.Count);
    }

    /// <summary>Takes every row out, pool and all.</summary>
    /// <remarks>
    ///     For a list whose content changed shape rather than length — a different object being
    ///     shown. <see cref="Trim" /> is what a refresh wants.
    /// </remarks>
    public void Clear() {
        foreach (var row in rows) {
            row.Remove();
        }

        rows.Clear();
        Count = 0;
    }
}
