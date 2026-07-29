// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>The stroke font: the table parses, and the box it draws in is the one it promises.</summary>
public sealed class DebugFontTests {
    /// <summary>
    ///     Every printable character draws something, except the one that is meant not to. A glyph
    ///     that silently drew nothing would be invisible in exactly the situation the font exists for
    ///     — reading a number off the screen when nothing else works.
    /// </summary>
    [Fact]
    public void EveryPrintableCharacterHasStrokes() {
        for (var character = DebugFont.FirstCharacter; character <= DebugFont.LastCharacter; character++) {
            var count = DebugFont.SegmentCount([character]);

            if (character == ' ') {
                Assert.Equal(0, count);
                continue;
            }

            Assert.True(count > 0, $"'{character}' (U+{(int) character:X4}) draws nothing");
        }
    }

    /// <summary>An unmapped character is a box rather than a gap.</summary>
    [Fact]
    public void AnUnmappedCharacterIsDrawnAsABox() {
        // Four sides. Dropping it instead would turn "café" into "caf" and hide the fact that
        // anything was there.
        Assert.Equal(4, DebugFont.SegmentCount("é"));
    }

    /// <summary>
    ///     Text sits inside the box <c>MeasureWidth</c> and <c>MeasureHeight</c> describe, which is
    ///     what every panel's layout is computed from — and what right-alignment relies on.
    /// </summary>
    [Fact]
    public void StrokesStayInsideTheMeasuredBox() {
        const string Text = "Wgy|_ 123";
        const float Size = 10f;

        var sink = new Bounds();
        DebugFont.Emit(Text, Vector2.Zero, Size, ref sink);

        Assert.True(sink.MinX >= -0.01f, $"a stroke starts at x = {sink.MinX}");
        Assert.True(sink.MaxX <= DebugFont.MeasureWidth(Text, Size) + 0.01f, $"a stroke ends at x = {sink.MaxX}");

        // The measured height is the cap box; descenders reach below it, which is why the line height
        // is larger than the size and why a panel's rows are spaced by the former.
        Assert.True(sink.MinY >= -0.01f, $"a stroke rises above the cap line to y = {sink.MinY}");
        Assert.True(sink.MaxY <= DebugFont.LineHeightFor(Size), $"a descender falls to y = {sink.MaxY}");
    }

    [Fact]
    public void ANewLineStartsAColumnAgainAndDropsARow() {
        const float Size = 10f;

        var one = new Bounds();
        DebugFont.Emit("AB", Vector2.Zero, Size, ref one);

        var two = new Bounds();
        DebugFont.Emit("A\nB", Vector2.Zero, Size, ref two);

        // Two lines are one glyph wide and two lines tall; one line is two glyphs wide.
        Assert.Equal(DebugFont.MeasureWidth("AB", Size), DebugFont.MeasureWidth("A\nB", Size) * 2f, 3);
        Assert.True(two.MaxY > one.MaxY, "the second line was not put below the first");
        Assert.True(two.MaxX < one.MaxX, "the second line was not started back at the left");
    }

    /// <summary>The advance is fixed, which is what makes right-aligning a column of numbers work.</summary>
    [Fact]
    public void EveryGlyphAdvancesTheSame() {
        const float Size = 9f;

        Assert.Equal(DebugFont.MeasureWidth("iii", Size), DebugFont.MeasureWidth("WWW", Size), 4);
        Assert.Equal(DebugFont.AdvanceFor(Size) * 3f, DebugFont.MeasureWidth("   ", Size), 4);
    }

    [Fact]
    public void MeasuringSeveralLinesTakesTheWidest() {
        const float Size = 9f;

        Assert.Equal(DebugFont.MeasureWidth("abcd", Size), DebugFont.MeasureWidth("a\nabcd\nab", Size), 4);
    }

    /// <summary>Collects the extent of whatever is emitted.</summary>
    struct Bounds : IDebugFontSink {
        public float MinX = float.MaxValue;
        public float MaxX = float.MinValue;
        public float MinY = float.MaxValue;
        public float MaxY = float.MinValue;

        public Bounds() { }

        public void Segment(Vector2 head, Vector2 tail) {
            Take(head);
            Take(tail);
        }

        void Take(Vector2 point) {
            MinX = MathF.Min(MinX, point.X);
            MaxX = MathF.Max(MaxX, point.X);
            MinY = MathF.Min(MinY, point.Y);
            MaxY = MathF.Max(MaxY, point.Y);
        }
    }
}
