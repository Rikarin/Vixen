// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     A field dropped into a form row takes the row's width, and what it holds does not decide how wide
///     it is (#1404).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The defect was an empty field eighteen pixels wide</b> — two 8 px paddings and two 1 px
///         borders. <c>field-content</c> is a row, so the field sits on its main axis at its content
///         width, and an empty field has none: <c>field-text</c> holds no text and the placeholder is
///         absolutely positioned, which is exactly so that it does not size the box. So the prompt had
///         no room to be drawn, and a filled field grew as the user typed.
///     </para>
///     <para>
///         ⚠ <b>Each fill has a control that must not fill</b>, because the rule is a list and not
///         <c>&gt; *</c> for <c>key-value-value</c>'s reason: a check box stretched across a form row
///         puts the tick a panel's width from its caption. A rule written as <c>&gt; *</c> would pass
///         every fill assertion here and fail the control.
///     </para>
/// </remarks>
public class FieldFillTests {
    /// <summary>The chrome of a field — its padding and its borders — which is all an empty one had.</summary>
    const float Chrome = 2 * 8f + 2 * 1f;

    /// <summary>A form row in a 600 px column, holding one control of the given kind.</summary>
    /// <remarks>
    ///     The column is the application's, as in <c>02-HelloUi</c>'s gallery card: a stretched column
    ///     is what makes the row wide, and the root alone does not stretch what is put in it.
    /// </remarks>
    static (ControlFixture Fixture, LabeledContent Row, T Field) Row<T>() where T : UiElement, new() {
        var fixture = new ControlFixture(css: "column { width: 600px; flex-direction: column; align-items: stretch; }");
        var row = fixture.Document.Root.Add("column").Add<LabeledContent>();
        row.Label = "Password";

        var field = row.Content.Add<T>();
        fixture.Update();

        return (fixture, row, field);
    }

    [Fact]
    public void An_empty_secure_field_fills_its_row() {
        var (fixture, row, field) = Row<SecureTextBox>();

        using (fixture) {
            field.Placeholder = "Password";
            fixture.Update();

            // The premise: the row is wide, so a field at its chrome is a choice and not a squeeze.
            Assert.True(row.Content.Width > 400f, $"the row is {row.Content.Width} px wide");

            Assert.True(field.Width > Chrome + 1f, $"an empty field is {field.Width} px wide — its chrome and nothing else");
            Assert.Equal(row.Content.Width, field.Width, 0.5f);
        }
    }

    [Fact]
    public void A_fields_width_does_not_follow_what_is_typed_into_it() {
        var (fixture, _, field) = Row<TextBox>();

        using (fixture) {
            var empty = field.Width;

            field.Value = "a much longer user name";
            fixture.Update();

            Assert.Equal(empty, field.Width, 0.5f);
        }
    }

    [Fact]
    public void A_check_box_in_a_row_keeps_its_own_width() {
        var (fixture, row, box) = Row<CheckBox>();

        using (fixture) {
            box.Label = "Remember me";
            fixture.Update();

            Assert.True(box.Width < row.Content.Width / 2, $"a check box is {box.Width} px of a {row.Content.Width} px row");
        }
    }
}
