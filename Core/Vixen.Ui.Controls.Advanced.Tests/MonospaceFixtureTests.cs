// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>
///     The advanced theme's <c>font-family: monospace</c> resolves to a fixed-pitch face in this
///     suite, and the text pipeline — not the control — is what says so.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Verify the instrument first.</b> Every <c>CodeEditor</c> geometry test computes the
///         x it expects as <c>column × CharacterWidth</c>, which is exactly how the control computes
///         it, so the suite is green whatever face the family resolves to — including the
///         proportional <c>TestShapeLana</c> it resolved to until #1259, where an <c>i</c> measured
///         3.625 against a 10.9375 cell and a caret placed after it sat two thirds of a cell to the
///         right of the glyph. Nothing here multiplies: a span's width is read back from the run
///         the shaper produced and compared with the grid's arithmetic.
///     </para>
///     <para>
///         ⚠ <b>The family is named, not the face.</b> The theme writes <c>monospace</c> and
///         <c>FontRegistry.Chain</c> substitutes <c>Default</c> for a name nothing registered, so a
///         fixture that registered its fixed-pitch face under any other name would pass the width
///         test below by accident on a machine where the default happened to be fixed-pitch and
///         fail it everywhere else. The first test pins the name.
///     </para>
/// </remarks>
public class MonospaceFixtureTests {
    /// <summary>The generic family the theme asks for is registered, and to the fixed-pitch face.</summary>
    [Fact]
    public void The_theme_family_resolves_to_the_fixed_pitch_face_and_not_to_the_default() {
        using var fixture = new AdvancedFixture();
        var fonts = fixture.Document.Fonts;

        Assert.Same(AdvancedFixture.Monospace, fonts.Resolve("monospace"));
        Assert.NotSame(fonts.Default, fonts.Resolve("monospace"));
    }

    /// <summary>The editor's probe measures the face the theme names, not the default.</summary>
    [Fact]
    public void The_editor_measures_its_cell_in_the_monospace_face() {
        using var fixture = new AdvancedFixture();
        var editor = fixture.Add<CodeEditor>();
        editor.Source = "0";
        fixture.Update();

        // 0.6 em in the synthetic face, at whatever size the cascade gave the editor.
        var expected = editor.FontSize * 1229f / 2048f;
        Assert.Equal(expected, editor.CharacterWidth, 0.01f);
    }

    /// <summary>Every printable ASCII column is one cell wide when the text pipeline shapes it.</summary>
    /// <remarks>
    ///     The line holds the narrowest and the widest Latin letters, a digit, spaces and
    ///     punctuation, and the assertion is on the shaped run's width against
    ///     <c>length × CharacterWidth</c>. Under the proportional face the same line measures
    ///     roughly two thirds of the grid's answer.
    /// </remarks>
    [Theory]
    [InlineData("iiiiiiii")]
    [InlineData("WWWWWWWW")]
    [InlineData("if (x) { return y; }")]
    [InlineData("0123456789 .,;:!?")]
    public void A_shaped_line_is_as_wide_as_the_grid_says(string text) {
        using var fixture = new AdvancedFixture();
        var editor = fixture.Add<CodeEditor>();
        editor.Source = text;
        fixture.Update();
        editor.Refresh();
        fixture.Update();

        var row = editor.Pool.First(line => line.Index == 0);
        var shaped = row.Spans
            .Where(span => !span.HasClass("parked"))
            .Sum(span => span.Block()?.Lines[0].Width ?? 0f);

        Assert.Equal(text.Length * editor.CharacterWidth, shaped, 0.05f);
    }
}
