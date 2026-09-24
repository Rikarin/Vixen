// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The date picker: a select-shaped field whose list is a calendar.</summary>
/// <remarks>
///     ⚠ <b>The keyboard goes into the calendar and comes back out</b>, which is the one place this
///     control's behaviour is not <see cref="Select" />'s, and so the part most of these tests
///     follow: open, walk, choose, and where the focus is afterwards.
/// </remarks>
public class DatePickerTests {
    static readonly DateOnly Today = new(2026, 9, 24);

    static (ControlFixture Fixture, DatePicker Picker) Built() {
        var fixture = new ControlFixture();
        var picker = fixture.Add<DatePicker>();

        picker.Calendar.Today = Today;
        picker.Placeholder = "Pick a day";
        fixture.Update();

        return (fixture, picker);
    }

    /// <summary>
    ///     A combo box whose value is the date as the field writes it, owning a named dialog that
    ///     is nowhere near it in the element tree.
    /// </summary>
    [Fact]
    public void A_picker_is_a_combo_box_that_owns_a_named_dialog() {
        var (fixture, picker) = Built();

        using (fixture) {
            Assert.Equal(AccessibleRole.ComboBox, picker.Role);
            Assert.Equal("Pick a day", picker.Field.Text);
            Assert.True(picker.HasClass("empty"));
            Assert.Null(picker.AccessibleValue);
            Assert.True(picker.AccessibleState.HasFlag(AccessibleStates.Expandable));
            Assert.False(picker.AccessibleState.HasFlag(AccessibleStates.Expanded));

            Assert.Same(picker.Popup, picker.AccessibleRelationTarget(AccessibleRelation.Owns));
            Assert.Equal(AccessibleRole.Dialog, picker.Popup.Role);
            Assert.Equal(ControlStrings.DatePickerDialog.Text, picker.Popup.AccessibleName);
            Assert.Same(fixture.Document.Root, picker.Popup.Parent);

            picker.Value = new DateOnly(2026, 9, 3);
            fixture.Update();

            Assert.Equal("09/03/2026", picker.Field.Text);
            Assert.Equal("09/03/2026", picker.AccessibleValue);
            Assert.False(picker.HasClass("empty"));
            Assert.Equal(new DateOnly(2026, 9, 3), picker.Calendar.Value);
        }
    }

    /// <summary>
    ///     ⚠ <b>A click opens the calendar with the keyboard on today, a click on a day chooses it,
    ///     closes the calendar and gives the keyboard back to the field.</b>
    /// </summary>
    [Fact]
    public void A_click_opens_and_a_day_s_click_chooses_and_closes() {
        var (fixture, picker) = Built();

        using (fixture) {
            var changes = new List<DateOnly?>();
            picker.ValueChanged += (_, value) => changes.Add(value);

            fixture.Click(picker);
            fixture.Update();

            Assert.True(picker.IsOpen);
            Assert.True(picker.AccessibleState.HasFlag(AccessibleStates.Expanded));
            Assert.Same(picker.Calendar.DayOf(Today), fixture.Document.Focused);

            fixture.Click(picker.Calendar.DayOf(new DateOnly(2026, 9, 15))!);
            fixture.Update();

            Assert.Equal(new DateOnly(2026, 9, 15), picker.Value);
            Assert.Equal([new DateOnly(2026, 9, 15)], changes);
            Assert.False(picker.IsOpen);
            Assert.Same(picker, fixture.Document.Focused);
            Assert.Equal("09/15/2026", picker.Field.Text);
        }
    }

    /// <summary>
    ///     ⚠ <b>The whole round trip on the keyboard</b>: Enter opens on the chosen day, the arrows
    ///     walk, Enter chooses and closes; Escape closes without choosing. Both give the focus back.
    /// </summary>
    [Fact]
    public void The_keyboard_opens_walks_chooses_and_cancels() {
        var (fixture, picker) = Built();

        using (fixture) {
            picker.Value = new DateOnly(2026, 9, 3);
            fixture.Update();
            fixture.Document.Focus(picker);

            fixture.Type(InputKey.Enter);
            fixture.Update();

            Assert.True(picker.IsOpen);
            Assert.Same(picker.Calendar.DayOf(new DateOnly(2026, 9, 3)), fixture.Document.Focused);

            fixture.Type(InputKey.Down);
            fixture.Type(InputKey.Enter);
            fixture.Update();

            Assert.Equal(new DateOnly(2026, 9, 10), picker.Value);
            Assert.False(picker.IsOpen);
            Assert.Same(picker, fixture.Document.Focused);

            // Escape leaves the value where it was.
            fixture.Type(InputKey.Down);
            fixture.Update();
            Assert.True(picker.IsOpen);

            fixture.Type(InputKey.Right);
            fixture.Type(InputKey.Escape);
            fixture.Update();

            Assert.False(picker.IsOpen);
            Assert.Equal(new DateOnly(2026, 9, 10), picker.Value);
            Assert.Same(picker, fixture.Document.Focused);
        }
    }

    /// <summary>
    ///     ⚠ <b>Choosing the date that is already chosen still closes the calendar.</b> Nothing
    ///     changed, so nothing that listens for a change hears it — which is why the picker listens
    ///     for the day being chosen instead.
    /// </summary>
    [Fact]
    public void Choosing_the_chosen_date_again_closes_the_calendar() {
        var (fixture, picker) = Built();

        using (fixture) {
            picker.Value = Today;
            fixture.Update();

            picker.Open();
            fixture.Update();
            Assert.True(picker.IsOpen);

            fixture.Type(InputKey.Enter);
            fixture.Update();

            Assert.False(picker.IsOpen);
            Assert.Equal(Today, picker.Value);
        }
    }

    /// <summary>
    ///     ⚠ <b>A form asks it</b>: a required picker with no date refuses the submission and takes
    ///     the keyboard, and one holding a date outside its limits refuses too.
    /// </summary>
    [Fact]
    public void A_form_refuses_a_required_picker_with_no_date_or_one_out_of_range() {
        using var fixture = new ControlFixture();

        var form = fixture.Add<Form>();
        var picker = form.Add<DatePicker>();
        picker.Required = true;
        fixture.Update();

        Assert.Equal([picker], form.Fields);
        Assert.False(picker.IsValid);
        Assert.True(picker.AccessibleState.HasFlag(AccessibleStates.Required));
        Assert.True(picker.AccessibleState.HasFlag(AccessibleStates.Invalid));

        Assert.False(form.Submit());
        Assert.Same(picker, fixture.Document.Focused);

        picker.Value = Today;
        Assert.True(form.Submit());

        picker.Maximum = Today.AddDays(-1);
        Assert.False(picker.IsValid);
        Assert.False(form.Submit());
    }

    /// <summary>The popup lives on the root, so removing the picker has to take it too.</summary>
    [Fact]
    public void Removing_the_picker_removes_its_calendar() {
        var (fixture, picker) = Built();

        using (fixture) {
            var popup = picker.Popup;
            Assert.Same(fixture.Document.Root, popup.Parent);

            picker.Remove();
            fixture.Update();

            Assert.True(popup.IsRemoved);
        }
    }

    /// <summary>In a labelled row, nothing a screen reader reaches is unnamed — open or shut.</summary>
    [Fact]
    public void A_labelled_picker_leaves_nothing_unnamed_open_or_shut() {
        using var fixture = new ControlFixture();

        var row = fixture.Add<LabeledContent>();
        row.Label = "Due";
        var picker = row.Content.Add<DatePicker>();
        picker.Calendar.Today = Today;
        fixture.Update();

        Assert.Equal("Due", picker.AccessibleName);
        Assert.Empty(AccessibilitySnapshot.Unnamed(fixture.Document.Root));

        picker.Open();
        fixture.Update();

        Assert.Empty(AccessibilitySnapshot.Unnamed(fixture.Document.Root));
    }
}
