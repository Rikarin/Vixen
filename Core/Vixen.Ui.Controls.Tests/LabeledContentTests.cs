// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The form row: a caption, the field it names, and the line under it.</summary>
/// <remarks>
///     ⚠ <b>Every assertion here is about a relation rather than about a layout, and that is the
///     point of the control existing at all.</b> A caption and a field side by side is two elements
///     and a stylesheet; what a stylesheet cannot do is tell a screen reader that the words on the
///     left are the name of the box on the right. Doc 49 § 7.1 ranks this among the missing controls
///     and notes that <c>Card</c> and <c>KeyValueList</c> approximate it — they approximate the
///     picture, and a suite that asserted the picture would pass against either of them.
/// </remarks>
public class LabeledContentTests {
    static (ControlFixture Fixture, LabeledContent Row, TextBox Field) Row(string label = "Project name") {
        var fixture = new ControlFixture();
        var row = fixture.Add<LabeledContent>();

        row.Label = label;

        // ⚠ `Content`, not the row. `UiElement.Add<T>` is `Document.Create` and does not go near
        // `ContentHost`, which routes a *nested tag* in markup — so this is what a C# caller writes
        // and `<LabeledContent><TextBox /></LabeledContent>` is what a markup one writes, and both
        // land in the same place.
        var field = row.Content.Add<TextBox>();

        fixture.Update();

        return (fixture, row, field);
    }

    /// <summary>A field in a row is named by the row's caption, with nothing said at the call site.</summary>
    /// <remarks>
    ///     ⚠ <b>A <see cref="TextBox" /> answers <c>null</c> to its own native accessible name on
    ///     purpose</b> — a placeholder is a hint and vanishes the moment there is a value — so the
    ///     first assertion is the one that would hold against an unrelated caption, and the second is
    ///     the one that says which caption.
    /// </remarks>
    [Fact]
    public void A_field_in_a_row_is_named_by_the_rows_caption() {
        var (fixture, row, field) = Row();

        using (fixture) {
            Assert.Equal("Project name", field.AccessibleName);
            Assert.Same(row.Caption, field.AccessibleRelationTarget(AccessibleRelation.LabelledBy));

            // The caption follows the property, and the name follows it — one string, not two.
            row.Label = "Repository";
            fixture.Update();

            Assert.Equal("Repository", field.AccessibleName);
        }
    }

    /// <summary>The message is a separate element the field points at, which is what ARIA asks for.</summary>
    /// <remarks>
    ///     ⚠ <b><c>TextField.ValidationMessage</c> is deliberately not written into the tree</b> —
    ///     its own remarks say so — because <c>aria-invalid</c> is paired with a <i>separate</i>
    ///     element holding the words, and folding the string into
    ///     <see cref="UiElement.AccessibleDescription" /> would overwrite whatever the application
    ///     had put there. This row is that element, so a form that has just been refused writes one
    ///     property and the description arrives in the tree.
    /// </remarks>
    [Fact]
    public void The_message_is_described_by_rather_than_copied_into_the_field() {
        var (fixture, row, field) = Row();

        using (fixture) {
            Assert.Same(row.Message, field.AccessibleRelationTarget(AccessibleRelation.DescribedBy));

            // ⚠ Measured rather than read off the style, because the height is the consequence that
            // matters: a message element left in the flow takes the column's `gap` and pushes every
            // row below it down by a line that says nothing.
            Assert.Equal(0f, row.Message.Height);

            row.Description = "Letters, numbers and dashes.";
            fixture.Update();

            Assert.Equal("Letters, numbers and dashes.", row.Message.Text);
            Assert.True(row.Message.Height > 0f);
            Assert.Equal("Letters, numbers and dashes.", field.AccessibleDescription);

            row.Description = null;
            fixture.Update();

            Assert.Equal(0f, row.Message.Height);
        }
    }

    /// <summary>Clicking the caption focuses the field, which is what a label is for.</summary>
    /// <remarks>
    ///     ⚠ <b>The caption and not the row.</b> A click anywhere in the row moving the focus would
    ///     take a drag that started on a slider's track and would fight a text field's own caret
    ///     placement — the affordance being copied is <c>&lt;label for&gt;</c>, which is the words
    ///     rather than the space around them. So the second half of this test is the assertion worth
    ///     having.
    /// </remarks>
    [Fact]
    public void Clicking_the_caption_focuses_the_field_and_clicking_the_message_does_not() {
        var (fixture, row, field) = Row();

        using (fixture) {
            row.Description = "Letters, numbers and dashes.";
            fixture.Update();

            Assert.NotSame(field, fixture.Document.Focused);

            // The message first, while nothing has the focus: a row that moved the focus from
            // anywhere in itself would pass the caption assertion below for the wrong reason.
            fixture.Click(row.Message);

            Assert.NotSame(field, fixture.Document.Focused);

            fixture.Click(row.Caption);

            Assert.Same(field, fixture.Document.Focused);
        }
    }

    /// <summary>A field put in the row later is joined too, and joining twice costs nothing.</summary>
    /// <remarks>
    ///     ⚠ <b><c>UiElement.OnChildAdded</c> is creation only and says so</b>, so a field
    ///     <i>reparented</i> into a row — a docking host, a virtualised list, a hot reload's rebuild
    ///     — arrives without the hook running. That is why <see cref="LabeledContent.Adopt" /> is
    ///     public, and why it has to be safe to call on a field that is already joined:
    ///     <see cref="UiElement.AddAccessibleRelation" /> refuses a duplicate, so the caller does not
    ///     have to know which route the field came by.
    /// </remarks>
    [Fact]
    public void A_field_moved_into_a_row_can_be_joined_by_hand_and_joining_twice_is_free() {
        using var fixture = new ControlFixture();

        var row = fixture.Add<LabeledContent>();
        row.Label = "Mass";

        var stray = fixture.Add<NumericInput>();
        fixture.Update();

        Assert.Null(stray.AccessibleName);

        fixture.Document.Reparent(stray, row.Content);
        fixture.Update();

        // Reparenting alone does nothing — which is the fact this method exists for rather than a
        // defect: a hook that also fired on a move would register the same child once per drag.
        Assert.Null(stray.AccessibleName);

        row.Adopt(stray);
        row.Adopt(stray);

        fixture.Update();

        Assert.Equal("Mass", stray.AccessibleName);
        Assert.Same(row.Caption, stray.AccessibleRelationTarget(AccessibleRelation.LabelledBy));
    }

    /// <summary>The row itself is neither a stop nor a node, so nothing is announced twice.</summary>
    /// <remarks>
    ///     ⚠ <b>A role here would put a third node between a screen reader and the field</b>, named
    ///     by the same words the relation already carries — which is <c>ComboBox</c>'s answer one
    ///     control over and for the same reason. And the row must not be a tab stop, or a form of
    ///     eight fields would be sixteen presses.
    /// </remarks>
    [Fact]
    public void The_row_is_not_a_node_and_not_a_tab_stop() {
        var (fixture, row, field) = Row();

        using (fixture) {
            Assert.Equal(AccessibleRole.None, row.Role);
            Assert.False(row.Focusable);
            Assert.True(field.Focusable);
        }
    }
}
