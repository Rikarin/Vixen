// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A field that holds what was typed and shows something else.</summary>
/// <remarks>
///     ⚠ <b>Every assertion here is about the <i>drawn</i> text and not about the value.</b> A test
///     that only checked <c>Value</c> would pass against a field that masked nothing at all, which is
///     the state this control set was in: any login screen asked for a password with a
///     <see cref="TextBox" />.
/// </remarks>
public class SecureTextBoxTests {
    const string Secret = "hunter2";

    [Fact]
    public void What_is_drawn_is_bullets_and_what_is_held_is_the_password() {
        using var fixture = new ControlFixture();
        var field = Typed(fixture, Secret);

        Assert.Equal(Secret, field.Value);
        Assert.Equal(new string(SecureTextBox.Bullet, Secret.Length), Drawn(field));
    }

    /// <summary>
    ///     ⚠ One unit out for one unit in, which is what the caret, the selection and the hit test
    ///     are all measured against. A mask that collapsed or grew the string would put the caret in
    ///     front of a different character than the one it is in front of.
    /// </summary>
    [Fact]
    public void The_mask_is_the_same_length_as_the_value() {
        using var fixture = new ControlFixture();

        foreach (var value in new[] { "", "a", Secret, "  spaces  ", "ünïcödé" }) {
            var field = Typed(fixture, value);
            Assert.Equal(value.Length, Drawn(field).Length);
        }
    }

    /// <summary>
    ///     ⚠ The accessibility tree gets the mask rather than the value <i>and</i> rather than
    ///     nothing. Reporting nothing would make an empty field and a full one sound the same, which
    ///     is how somebody typing blind loses track of whether their keystrokes are arriving.
    /// </summary>
    [Fact]
    public void The_accessibility_tree_is_told_the_mask_and_never_the_value() {
        using var fixture = new ControlFixture();
        var field = Typed(fixture, Secret);

        Assert.Equal(new string(SecureTextBox.Bullet, Secret.Length), field.AccessibleValue);
        Assert.DoesNotContain(Secret, field.AccessibleValue ?? "", StringComparison.Ordinal);
    }

    /// <summary>An ordinary field is unaffected, which is what the seam being a no-op by default means.</summary>
    [Fact]
    public void A_plain_text_box_still_draws_what_it_holds() {
        using var fixture = new ControlFixture();

        var box = fixture.Add<TextBox>();
        box.Value = Secret;
        fixture.Update();

        Assert.Equal(Secret, Drawn(box));
    }

    static SecureTextBox Typed(ControlFixture fixture, string value) {
        var field = fixture.Add<SecureTextBox>();

        field.Value = value;
        fixture.Update();

        return field;
    }

    /// <summary>What the field's text part actually holds, which is what gets shaped and drawn.</summary>
    static string Drawn(TextField field) =>
        field.Children.First(child => string.Equals(child.Tag, "field-text", StringComparison.Ordinal)).Text
        ?? string.Empty;
}
