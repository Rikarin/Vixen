// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using System.Text;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     Each entry in <c>UiElement.Block</c>'s reuse key that nothing else proved, toggled on a settled
///     element: the block has to come back rebuilt under the declaration that replaced it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A stale entry in that key is the hardest defect class in the file</b>, and a test is
///         the only thing that can see it: a paragraph built under one declaration and reused under
///         another draws the new style's spacing with the old style's line breaks, and it stays wrong
///         only until something else happens to invalidate it. <c>white-space</c>'s two entries are
///         proved by <c>WhiteSpacePreLineTests</c> and <c>WhiteSpaceBreakSpacesTests</c>; the six here
///         had no test at all. <c>Rikarin/Vixen#1344</c>.
///     </para>
///     <para>
///         ⚠ <b><c>flex-direction: column</c> is load-bearing, and every test here is green under
///         sabotage without it.</b> The width is an entry in the same key, and layout asks a
///         paragraph for its block more than once per pass at <i>different</i> widths — a row item
///         at 0 and then its used width, an <c>align-items: flex-start</c> item at infinity and then
///         0. A second <c>Update</c> after a class toggle therefore rebuilds the block on the width
///         alone, before any other entry is consulted, and deleting the entry under test changes
///         nothing. A column item with a definite width is measured at that one width in both
///         passes, which is the only arrangement in which these entries decide anything. Measured,
///         not inferred: with <c>row</c> here and all six entries deleted from the key at once,
///         all six tests pass.
///     </para>
///     <para>
///         ⚠ <b>And there is an <c>Update</c> between the toggle and the read.</b> <c>Style</c> is
///         assigned by the style pass, so a toggle read without one still carries the old computed
///         style, every key entry agrees with the standing block, and the assertion passes whether
///         or not the entry exists.
///     </para>
///     <para>
///         Every test is a pair with a control. The same text is laid out fresh under each of the two
///         declarations, the two layouts are asserted to <i>differ</i> — so a declaration this engine
///         had stopped honouring cannot satisfy the test by making both sides agree — and then the
///         settled element must report the first before the toggle and the second after it. The
///         comparison is <see cref="Shape" />, every line's extent, width, offset and run levels,
///         because the six entries each change a different one of those.
///     </para>
/// </remarks>
public class BlockCacheKeyTests {
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    /// <summary>Everything about a block the six entries could change, as one comparable string.</summary>
    /// <param name="block">The block.</param>
    /// <returns>One segment per line: start, length, width, offset and each run's bidi level.</returns>
    static string Shape(TextLayout block) {
        var shape = new StringBuilder();

        foreach (var line in block.Lines) {
            shape.Append(CultureInfo.InvariantCulture, $"[{line.Start}+{line.Length} w{line.Width:F2} o{line.Offset:F2}");

            foreach (var run in line.Runs) {
                shape.Append(CultureInfo.InvariantCulture, $" L{run.Level}");
            }

            shape.Append(']');
        }

        return shape.ToString();
    }

    static UiDocument Document(string before, string after) {
        var document = new UiDocument(900f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
            root          { width: 800px; height: 300px; flex-direction: column; }
            label         { font-family: Test; font-size: 16px; {{before}} }
            label.toggled { {{after}} }
            """
        );

        return document;
    }

    /// <summary>The block a fresh element settles on under one declaration.</summary>
    static string Fresh(string text, string common, string declaration) {
        var document = Document(common + " " + declaration, string.Empty);
        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        return Shape(element.Block()!);
    }

    /// <summary>Settles an element under <paramref name="before" />, toggles to <paramref name="after" />.</summary>
    static void AssertToggleRebuilds(string text, string common, string before, string after) {
        var expectedBefore = Fresh(text, common, before);
        var expectedAfter = Fresh(text, common, after);

        // The control: the two declarations lay this text out differently, so agreeing with the
        // second one below cannot be an accident of a declaration that does nothing.
        Assert.NotEqual(expectedBefore, expectedAfter);

        var document = Document(common + " " + before, after);
        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        Assert.Equal(expectedBefore, Shape(element.Block()!));

        element.AddClass("toggled");
        document.Update();

        Assert.Equal(expectedAfter, Shape(element.Block()!));
    }

    /// <summary><c>line-break: anywhere</c> breaks a word with no other opportunity in it.</summary>
    [Fact]
    public void Toggling_line_break_on_a_settled_element_rebuilds_the_block() =>
        AssertToggleRebuilds("abcdefghijklmnopqrstuvwxyz", "width: 60px;", "line-break: auto;", "line-break: anywhere;");

    /// <summary><c>text-wrap: balance</c> takes different breaks with the same text, font and width.</summary>
    [Fact]
    public void Toggling_text_wrap_style_on_a_settled_element_rebuilds_the_block() =>
        AssertToggleRebuilds(
            "one two three four five six seven eight nine ten eleven",
            "width: 200px;",
            "text-wrap: wrap;",
            "text-wrap: balance;"
        );

    /// <summary>A clamp changes how many lines there are, which is the block's height.</summary>
    [Fact]
    public void Toggling_the_line_clamp_on_a_settled_element_rebuilds_the_block() =>
        AssertToggleRebuilds(
            "one two three four five six seven eight",
            "width: 80px;",
            "-webkit-line-clamp: none;",
            "-webkit-line-clamp: 2;"
        );

    /// <summary>Moving the tab stops moves everything after the first tab.</summary>
    [Fact]
    public void Toggling_tab_size_on_a_settled_element_rebuilds_the_block() =>
        AssertToggleRebuilds("a\tb", string.Empty, "tab-size: 2;", "tab-size: 12;");

    /// <summary>An indent narrows the first line, so the first line takes fewer words.</summary>
    [Fact]
    public void Toggling_text_indent_on_a_settled_element_rebuilds_the_block() =>
        AssertToggleRebuilds(
            "one two three four five six seven eight",
            "width: 120px;",
            "text-indent: 0px;",
            "text-indent: 60px;"
        );

    /// <summary>
    ///     A trailing neutral takes the paragraph's direction, so <c>rtl</c> splits it into a run of its
    ///     own at an odd level.
    /// </summary>
    [Fact]
    public void Toggling_direction_on_a_settled_element_rebuilds_the_block() =>
        AssertToggleRebuilds("ab cd !", string.Empty, "direction: ltr;", "direction: rtl;");
}
