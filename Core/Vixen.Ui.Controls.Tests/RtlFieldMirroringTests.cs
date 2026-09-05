// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What the field draws over the text, in a block the alignment has moved — issue #326.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A <c>TextLayout</c> places every line from zero, and the alignment is applied by
///         whoever draws it.</b> The block does not know how wide the box around it is, so
///         <c>CaretOffset</c>, <c>VisualRanges</c> and <c>CaretPositionAt</c> are all line-local
///         while <c>DrawListBuilder</c> puts the glyphs at <c>left + TextAlignShift(…)</c>. Anything
///         drawn <i>over</i> the text has to add the same number back, and the caret, the selection
///         band and the hit test did not — so a wrapped RTL area drew its caret against the left
///         edge of the block while the short line it belonged to sat flush against the right, fifty
///         pixels away, and a click on the text put the caret somewhere else again.
///     </para>
///     <para>
///         ⚠ <b>#326 is where this belongs and is also why it was not found.</b> That issue's three
///         claims about the text layer were refuted — the glyphs mirror, the runs are cut on level,
///         the per-line alignment resolves <c>start</c> against <c>direction</c> — and every one of
///         those is about <i>glyphs</i>. The caret is not a glyph. `BidiMirroringTests` in
///         <c>Vixen.Ui.Tests</c> is the sibling of this and asserts the line; this asserts what a
///         control paints on top of it.
///     </para>
///     <para>
///         ⚠ <b>The oracle is containment, not a coordinate.</b> "The caret is at x = 281.5" is a
///         number that has to be recomputed by hand every time the font or the fixture moves. "The
///         caret is inside the horizontal span of the glyphs on its own line" is the actual
///         requirement, is true for every correct rendering, and was false by the whole slack before
///         the fix.
///     </para>
/// </remarks>
public class RtlFieldMirroringTests {
    /// <summary>Three wrapped lines, the last of them much shorter than the box.</summary>
    /// <remarks>
    ///     ⚠ <b>The short last line is the whole fixture.</b> A line that fills its box has no slack,
    ///     so the alignment shift is zero and every one of these assertions passes against the bug.
    ///     The value is chosen so the final row holds two characters in a box wide enough for nine.
    /// </remarks>
    const string Wrapped = "aaaa bbbb cccc dddd ee";

    const string Css = """
        root     { direction: rtl; font-family: Test; font-size: 16px; }
        textarea { width: 120px; height: 100px; }
        """;

    static (UiTest Ui, TextArea Field) Opened() {
        var ui = ControlHarness.Open(300f, 160f, Css);

        var field = ui.Add<TextArea>("notes");
        field.Value = Wrapped;

        ui.Frame();
        ui.Document.Focus(field);
        ui.Frame();

        return (ui, field);
    }

    /// <summary>The horizontal span of every glyph run drawn on one row of the block.</summary>
    /// <remarks>
    ///     Selected by the row's top rather than by order, because a line is several commands — one
    ///     per run per line, since a command names one font and lies on one baseline.
    /// </remarks>
    static (float Left, float Right) GlyphSpan(UiTest ui, float top, float height) {
        var left = float.MaxValue;
        var right = float.MinValue;

        foreach (var command in ui.Document.Drawing.Commands) {
            if (command.Kind != DrawCommandKind.Text || command.Y < top || command.Y >= top + height) {
                continue;
            }

            left = MathF.Min(left, command.X);
            right = MathF.Max(right, command.X + command.Width);
        }

        Assert.True(left < right, $"no glyphs were drawn on the row at {top}");

        return (left, right);
    }

    /// <summary>The one-pixel bar, which is the only command of its width in the frame.</summary>
    static (float X, float Y, float Height) Caret(UiTest ui) {
        var bars = ui.Document.Drawing.Commands
            .Where(static command => command.Kind == DrawCommandKind.Rectangle && MathF.Abs(command.Width - 1f) < 0.01f)
            .ToArray();

        var bar = Assert.Single(bars);

        return (bar.X, bar.Y, bar.Height);
    }

    [Fact]
    public void The_caret_on_a_short_rtl_line_is_drawn_inside_that_line_s_glyphs() {
        var (ui, field) = Opened();

        using (ui) {
            // The last row, whose two characters leave most of the box empty. Under `direction: rtl`
            // the glyphs are pushed to the right-hand end of it and the caret used to stay at the
            // left, which is a caret in the middle of blank space.
            field.MoveCaret(Wrapped.Length - 1);
            ui.Frame();

            var caret = Caret(ui);
            var (left, right) = GlyphSpan(ui, caret.Y, caret.Height);

            Assert.True(
                caret.X >= left - 1f && caret.X <= right + 1f,
                $"the caret is at {caret.X:0.0} and its line's glyphs run from {left:0.0} to {right:0.0}"
            );
        }
    }

    /// <summary>And a press on the text puts the caret where the press was.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion is on where the caret is <i>drawn</i>, never on the index.</b> #326's
    ///     own note records why: entering the wrong run clamps to its trailing edge, which for a
    ///     short row can be the same number the right answer gives. A round trip through the pixels
    ///     is the only form of this that cannot be satisfied by a coincidence.
    /// </remarks>
    [Fact]
    public void A_press_inside_a_short_rtl_line_puts_the_caret_where_it_was_pressed() {
        var (ui, field) = Opened();

        using (ui) {
            field.MoveCaret(Wrapped.Length - 1);
            ui.Frame();

            var before = Caret(ui);
            var (left, right) = GlyphSpan(ui, before.Y, before.Height);

            // A quarter of the way into the row's glyphs, which is inside its only word and is
            // nowhere near either end of the block.
            var target = left + ((right - left) * 0.25f);

            ui.MovePointer(target, before.Y + (before.Height * 0.5f));
            ui.PressPointer();
            ui.ReleasePointer();
            ui.Frame();

            var after = Caret(ui);

            Assert.Equal(before.Y, after.Y, 1);

            Assert.True(
                MathF.Abs(after.X - target) <= (right - left) * 0.5f,
                $"a press at {target:0.0} on a row running {left:0.0}–{right:0.0} moved the caret to {after.X:0.0}"
            );
        }
    }

    /// <summary>The selection band lands on the glyphs too, and it is a separate call site.</summary>
    [Fact]
    public void The_selection_band_covers_the_glyphs_it_selects() {
        var (ui, field) = Opened();

        using (ui) {
            field.MoveCaret(Wrapped.Length - 2);
            field.MoveCaret(Wrapped.Length, extend: true);
            ui.Frame();

            var caret = Caret(ui);
            var (left, right) = GlyphSpan(ui, caret.Y, caret.Height);

            // The widest filled rectangle sitting on this row that is neither the field's own
            // background nor the caret: the selection is the only other thing painted there.
            var band = ui.Document.Drawing.Commands
                .Where(command =>
                    command.Kind == DrawCommandKind.Rectangle
                    && command.Width > 1.01f
                    && command.Y >= caret.Y
                    && command.Y < caret.Y + caret.Height
                    && command.Height <= caret.Height + 0.01f
                )
                .ToArray();

            var selection = Assert.Single(band);

            Assert.True(
                selection.X >= left - 1f && selection.X + selection.Width <= right + 1f,
                $"the band runs {selection.X:0.0}–{selection.X + selection.Width:0.0} and the glyphs {left:0.0}–{right:0.0}"
            );
        }
    }
}
