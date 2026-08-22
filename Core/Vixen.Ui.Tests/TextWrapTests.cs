// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Layout;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Text that does not fit on one line, and what the element does about it.</summary>
/// <remarks>
///     <para>
///         <c>Vixen.Ui.Text</c> has had the UAX#14 breaker and the greedy filler for three phases and
///         nothing called them: an element drew one line however long its string was. What was
///         missing is the part that can only live here — the widths, which are in pixels and are
///         measured across whatever faces the fallback chain chose, so a paragraph in two fonts has
///         no single design-unit scale for <c>LineWrapper</c>'s other overload to work in.
///     </para>
///     <para>
///         Verified by sabotage, eight of eight landing: wrapping to the measure's width rather than
///         the element's fails 11, reporting the wrapped-to width rather than the widest line fails
///         2, ignoring <c>white-space: nowrap</c> fails 1, wrapping to an undefined available width
///         fails 1, stacking the lines at the first one's height fails 1, honouring a hard break only
///         when the text overflows fails 1, counting the trailing space in a line's width fails 1,
///         and aligning the block rather than each line fails 1.
///     </para>
///     <para>
///         ⚠ <b>Four of those needed something changed before they could land, and each change was a
///         real gap.</b> Three were missing tests — no label without an explicit width, no paragraph
///         whose lines are in different faces and therefore different heights, no assertion about
///         where a centred line starts. The fourth was <i>duplicated logic</i>: <c>white-space</c> was
///         tested in two places, so sabotaging either one alone changed nothing, which is exactly how
///         a condition that has stopped meaning anything hides.
///     </para>
/// </remarks>
public class TextWrapTests {
    const float Tolerance = 0.01f;
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    /// <summary>A document whose labels are a fixed width, so the wrapping width is decided.</summary>
    static UiDocument Documented(string label) {
        var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Test", Font);
        document.Load($"root {{ width: 400px; height: 300px; align-items: flex-start; }} label {{ {label} }}");

        return document;
    }

    static UiElement Labelled(UiDocument document, string text) {
        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        return element;
    }

    [Fact]
    public void A_string_that_fits_is_one_line() {
        using var document = Documented("width: 300px;");
        var block = Labelled(document, "a b").Block()!;

        Assert.Single(block.Lines);
        Assert.Equal(0, block.Lines[0].Start);
        Assert.Equal(3, block.Lines[0].Length);
    }

    [Fact]
    public void A_string_that_does_not_fit_is_broken_at_a_space() {
        using var document = Documented("width: 300px;");

        // Wide enough for a word or two and nowhere near the whole string.
        var narrow = Documented("width: 40px;");
        var block = Labelled(narrow, "aa bb cc dd ee ff").Block()!;

        Assert.True(block.Lines.Length > 1, $"expected several lines, got {block.Lines.Length}");

        // ⚠ The lines tile the text with no gap and no overlap, which is the property the caret
        // depends on: an index has to fall on exactly one line, and a break that dropped the space it
        // broke at would lose a character out of the middle of the paragraph.
        var covered = 0;
        foreach (var line in block.Lines) {
            Assert.Equal(covered, line.Start);
            covered += line.Length;
        }

        Assert.Equal("aa bb cc dd ee ff".Length, covered);
        _ = document;
    }

    [Fact]
    public void Every_line_fits_the_width_it_was_given() {
        using var document = Documented("width: 60px;");
        var block = Labelled(document, "aa bb cc dd ee ff gg hh").Block()!;

        Assert.True(block.Lines.Length > 2, $"expected several lines, got {block.Lines.Length}");

        foreach (var line in block.Lines) {
            // Trailing whitespace does not count towards a line's width, which is the wrapper's rule
            // and is why this can be a strict comparison rather than a fudged one.
            Assert.True(line.Width <= 60f + Tolerance, $"a line is {line.Width} wide in a 60px box");
        }
    }

    [Fact]
    public void The_block_measures_its_widest_line_rather_than_the_width_it_wrapped_to() {
        using var document = Documented("width: 200px;");
        var block = Labelled(document, "aa bb cc dd ee ff gg hh ii jj kk ll").Block()!;

        // ⚠ Not 200. A paragraph wrapped to 200 whose longest line is 180 measures 180 — reporting
        // the wrapping width would make every centred paragraph sit off-centre by half its own slack
        // and would stop a shrink-to-fit box ever shrinking.
        var widest = 0f;
        foreach (var line in block.Lines) {
            widest = MathF.Max(widest, line.Width);
        }

        Assert.Equal(widest, block.Width, Tolerance);
        Assert.True(block.Width <= 200f + Tolerance);
    }

    [Fact]
    public void The_lines_stack_and_the_block_is_as_tall_as_all_of_them() {
        using var document = Documented("width: 40px;");
        var block = Labelled(document, "aa bb cc dd").Block()!;

        Assert.True(block.Lines.Length > 1);

        var y = 0f;
        for (var i = 0; i < block.Lines.Length; i++) {
            Assert.Equal(y, block.TopOf(i), Tolerance);
            y += block.Lines[i].Height;
        }

        // ⚠ The sum, not one line's height times the count. They are the same number today because
        // every line is in one font at one size — and they stop being the same the moment a fallback
        // face with taller metrics appears on one line only, which is exactly when a paragraph would
        // start overlapping itself.
        Assert.Equal(y, block.Height, Tolerance);
        Assert.True(block.Height > block.Lines[0].Height);
    }

    [Fact]
    public void Nowrap_keeps_it_on_one_line_however_narrow_the_box() {
        using var document = Documented("width: 30px; white-space: nowrap;");
        var block = Labelled(document, "aa bb cc dd ee ff").Block()!;

        // ⚠ And it overflows, which is what `nowrap` means. The single-line text field depends on
        // exactly this: a long value scrolls sideways rather than growing the field downwards.
        Assert.Single(block.Lines);
        Assert.True(block.Width > 30f);
    }

    [Fact]
    public void An_unbreakable_word_overflows_unless_the_style_says_otherwise() {
        using var document = Documented("width: 30px;");
        var overflowing = Labelled(document, "aaaaaaaaaaaaaaaa").Block()!;

        // CSS's default. A word with no break opportunity in it is not broken, and the line is as
        // wide as the word — which is what prose wants, because the alternative is hyphen-less
        // fragments.
        Assert.Single(overflowing.Lines);
        Assert.True(overflowing.Width > 30f);

        using var permitted = Documented("width: 30px; overflow-wrap: anywhere;");
        var broken = Labelled(permitted, "aaaaaaaaaaaaaaaa").Block()!;

        Assert.True(broken.Lines.Length > 1, "overflow-wrap: anywhere should break inside the word");
    }

    [Fact]
    public void A_newline_starts_a_line_wherever_it_falls() {
        using var document = Documented("width: 300px;");
        var block = Labelled(document, "aa\nbb").Block()!;

        // The whole string fits, so nothing would break it — a mandatory break is not about width.
        Assert.Equal(2, block.Lines.Length);
        Assert.Equal(0, block.Lines[0].Start);
        Assert.Equal(3, block.Lines[1].Start);
    }

    [Fact]
    public void The_element_grows_downwards_rather_than_sideways() {
        using var document = Documented("width: 60px;");
        var label = Labelled(document, "aa bb cc dd ee ff gg hh");

        var block = label.Block()!;

        // ⚠ The measured height reaches the *layout*, which is the half that makes wrapping visible
        // rather than a property nobody reads. Before this, the measure function ignored the width
        // it was offered entirely and every paragraph was one line tall.
        Assert.True(block.Lines.Length > 2);
        Assert.Equal(block.Height, label.Height, 1f);
        Assert.True(label.Height > block.Lines[0].Height * 2f);
    }

    [Fact]
    public void A_label_with_no_width_of_its_own_wraps_at_its_container() {
        using var document = Documented("max-width: 80px;");
        var label = Labelled(document, "aa bb cc dd ee ff gg hh");
        var block = label.Block()!;

        // ⚠ **No `width` on the label**, which is the case an available width that is *offered*
        // rather than fixed exercises. Flexbox asks a leaf twice — once with the width undefined, for
        // the intrinsic size, and once against a real constraint — and a measure function that
        // wrapped to the undefined one reports a column one character wide, which flexbox then
        // believes.
        Assert.True(block.Lines.Length > 1, $"expected wrapping, got {block.Lines.Length} line(s)");
        Assert.True(label.Width <= 80f + 1f, $"the label grew to {label.Width}");
        Assert.Equal(block.Height, label.Height, 1f);
    }

    [Fact]
    public void Each_line_is_aligned_on_its_own() {
        using var document = Documented("width: 90px; text-align: center;");
        var label = Labelled(document, "aa bb cc ddddddddd");

        document.Draw();

        var block = label.Block()!;
        Assert.True(block.Lines.Length > 1, $"expected wrapping, got {block.Lines.Length} line(s)");

        var widths = block.Lines.Select(line => line.Width).Distinct().Count();
        Assert.True(widths > 1, "the lines need different widths for centring to be visible");

        var xs = document.Drawing.Commands
            .Where(static command => command.Kind == DrawCommandKind.Text)
            .Select(static command => command.X)
            .Distinct()
            .ToList();

        // ⚠ Centring the *block* and laying the lines out inside it left-aligns every line but the
        // widest, which looks almost right and is the mistake worth a test. Two lines of different
        // widths, centred, start at two different x.
        Assert.True(xs.Count > 1, $"every line started at the same x: {string.Join(", ", xs)}");
    }

    [Fact]
    public void An_undefined_available_width_means_do_not_wrap_at_all() {
        using var document = Documented("width: 60px;");
        var label = Labelled(document, "aa bb cc dd ee ff");
        var unwrapped = label.Block(float.PositiveInfinity)!;

        // ⚠ **Both halves of that condition are real and neither is reached by a document.** Flexbox
        // asks a leaf for its max-content size with the mode undefined and the width whatever it
        // happened to have, and for its min-content size with the width `NaN` — and a measure that
        // wrapped to either reports a column one word wide, which flexbox then believes and lays the
        // element out at. Nothing in this suite arrives that way, so the request is built by hand:
        // insurance that is exercised rather than insurance that is asserted.
        foreach (var width in new[] { float.NaN, 60f, 0f }) {
            var request = new MeasureRequest(
                document.Layout,
                label.LayoutNode,
                label,
                width,
                MeasureMode.Undefined,
                float.NaN,
                MeasureMode.Undefined
            );

            var size = TextLayout.Measure(request);

            // ⚠ <b>The block's own size rounded up, because a measurement is reported in whole
            // device pixels and a block is not.</b> The measure ceils so that the same paragraph
            // comes out the same height whether an element holds it in its own `Text` or in a
            // `text` child — see <c>TextLayout.Measure</c>. This document is at 1×, so the ceiling
            // is the plain one. What is being asserted here is still that nothing <i>wrapped</i>:
            // a measure that had wrapped would be shorter across and several lines taller, neither
            // of which a pixel of rounding could account for.
            Assert.Equal(MathF.Ceiling(unwrapped.Width), size.Width, Tolerance);
            Assert.Equal(MathF.Ceiling(unwrapped.Height), size.Height, Tolerance);
        }

        // And a width that *is* offered is used, or the condition above would be a way of never
        // wrapping at all.
        var offered = new MeasureRequest(
            document.Layout,
            label.LayoutNode,
            label,
            60f,
            MeasureMode.AtMost,
            float.NaN,
            MeasureMode.Undefined
        );

        Assert.True(TextLayout.Measure(offered).Height > unwrapped.Height);
    }

    [Fact]
    public void A_caret_index_finds_the_line_it_is_on() {
        using var document = Documented("width: 40px;");
        var block = Labelled(document, "aa bb cc dd").Block()!;

        Assert.True(block.Lines.Length > 1);

        var second = block.Lines[1];

        Assert.Equal(0, block.LineOf(0));
        Assert.Equal(1, block.LineOf(second.Start + 1));

        // And the caret's y is the top of that line rather than of the block, which is the whole
        // reason `CaretAt` returns a pair.
        var (_, y) = block.CaretAt(second.Start + 1);
        Assert.Equal(block.TopOf(1), y, Tolerance);
    }

    [Fact]
    public void Hit_testing_picks_the_line_the_point_is_over() {
        using var document = Documented("width: 40px;");
        var block = Labelled(document, "aa bb cc dd").Block()!;

        Assert.True(block.Lines.Length > 1);

        var onFirst = block.CaretIndexAt(0f, 0f);
        var onSecond = block.CaretIndexAt(0f, block.TopOf(1) + 1f);

        Assert.Equal(0, onFirst);
        Assert.Equal(block.Lines[1].Start, onSecond);

        // Below everything lands on the last line rather than off the end, which is what dragging a
        // selection past the bottom of a paragraph has to do.
        Assert.True(block.CaretIndexAt(0f, block.Height + 100f) >= block.Lines[^1].Start);
    }

    /// <summary>
    ///     ⚠ The same paragraph in the same box is the same height whether the element holds it or a
    ///     <c>text</c> child does, at every width and at every scale.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The two forms are the imperative one and the markup one.</b> Setting
    ///         <c>UiElement.Text</c> puts the measure function on the element itself; a
    ///         <c>.vxml</c> interpolation emits <c>BuildContext.Text</c>, which is a child element
    ///         tagged <c>text</c> with the string on it. Every markup panel takes the second path, so
    ///         a disagreement between them is a panel changing height purely by being ported —
    ///         indistinguishable, in a visual diff, from a real regression.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A sweep and not a width, because at most widths the bug is invisible.</b> The
    ///         difference was a fraction of a line being ceiled on one path and rounded on the other,
    ///         so it only showed where the fraction fell below a half: at two, three, eight and nine
    ///         lines the two agreed and a single-width test would have passed throughout. Both scales
    ///         are swept for the same reason — the rounding is on the <i>device</i> grid, so 111.5
    ///         layout pixels is a fraction at 1× and a whole number at 2×.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two instrument checks, because this assertion passes on a broken engine in two
    ///         different ways.</b> Without a real font every glyph is zero-wide, nothing wraps and
    ///         both paths report nothing; without a fractional line height there is no rounding to
    ///         disagree about. So the sweep is required to have produced a genuinely fractional block
    ///         and a genuinely multi-line one before its agreement counts for anything.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void A_paragraph_measures_the_same_as_a_child_as_it_does_as_the_parents_own_text(float scale) {
        var sawFraction = false;
        var sawWrapping = false;

        for (var width = 80; width <= 400; width += 4) {
            var own = Measured(scale, width, child: false, out _, out var block);
            var viaChild = Measured(scale, width, child: true, out var inner, out _);

            sawFraction |= !Inexact(block.Height, MathF.Round(block.Height));
            sawWrapping |= block.Lines.Length > 1;

            Assert.Equal(own, viaChild, Tolerance);

            // ⚠ And the container is never shorter than the thing inside it, which is the half of
            // this that is not a matter of taste. A box a pixel shorter than its own text child
            // clips the last line's descenders, and that was the state of the child path: the
            // measured leaf ceiled to 767 while the box around it rounded to 766.
            Assert.True(
                viaChild + Tolerance >= inner,
                $"at width {width} the box is {viaChild} tall and the text child inside it is "
                + $"{inner}, so the last line is clipped"
            );
        }

        Assert.True(sawFraction, "no width produced a fractional block, so there was no rounding to disagree about");
        Assert.True(sawWrapping, "nothing wrapped, so the sweep never built the multi-line block this is about");
    }

    /// <summary>Lays the paragraph out one of the two ways and reports the outer element's height.</summary>
    /// <param name="scale">The device scale to round against.</param>
    /// <param name="width">How wide the box is.</param>
    /// <param name="child">Whether the string goes on a <c>text</c> child rather than on the box.</param>
    /// <param name="inner">The height of the element the string is actually on.</param>
    /// <param name="block">The wrapped block, for the instrument checks.</param>
    /// <returns>The outer element's height, as a consumer reads it.</returns>
    static float Measured(float scale, int width, bool child, out float inner, out TextLayout block) {
        const string Paragraph =
            "the quick brown fox jumps over the lazy dog and then keeps running through the field "
            + "until it reaches the far hedge where it stops to look back at the sleeping hound";

        using var document = new UiDocument(600f, 600f);
        document.Fonts.Register("Test", Font);
        document.Layout.PointScaleFactor = scale;

        document.Load(
            "root { width: 600px; height: 600px; align-items: flex-start; } "
            + $"para {{ width: {width}px; font-size: 22px; }} text {{ font-size: 22px; }}"
        );

        var element = document.Root.Add("para");
        var holder = child ? element.Add("text") : element;

        holder.Text = Paragraph;
        document.Update();

        block = holder.Block()!;

        // ⚠ The font has to be doing something. A missing face measures every glyph at zero, which
        // wraps to nothing and makes every assertion above trivially true.
        Assert.True(block.Width > 0f, "the paragraph measured no width, so the test font did not load");

        inner = holder.Height;
        return element.Height;
    }

    static bool Inexact(float left, float right) => MathF.Abs(left - right) < 0.0001f;
}
