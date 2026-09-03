// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The one distribution rule three things share: a ring, a band, and a decoration bar.</summary>
/// <remarks>
///     ⚠ <b>Every assertion here is a closed form rather than a picture.</b> The marks are stretched
///     to fit, so a run's ink is exactly <c>count × mark</c> and its last mark ends exactly at its
///     end — two identities that hold for every length and every thickness, which is what lets this
///     be a property test rather than a table of numbers somebody read off a screenshot.
/// </remarks>
public class DashesTests {
    const float Tolerance = 0.0005f;

    // ⚠ A `bool` rather than the enum, because `StrokeStyle` is internal and a `[Theory]`'s
    // parameters are part of a public method's signature. The alternative is making the enum public
    // for a test, which is a `CheckApi` entry bought with nothing.
    static List<DashMark> Marks(float length, float thickness, bool dotted) {
        var marks = new List<DashMark>();
        Dashes.Along(length, thickness, dotted ? StrokeStyle.Dotted : StrokeStyle.Dashed, marks);
        return marks;
    }

    [Theory]
    [InlineData(100f, 1f, false)]
    [InlineData(100f, 2f, false)]
    [InlineData(37.5f, 1.5f, false)]
    [InlineData(100f, 1f, true)]
    [InlineData(9f, 2f, true)]
    [InlineData(255f, 3f, true)]
    public void A_run_begins_and_ends_with_a_mark(float length, float thickness, bool dotted) {
        var marks = Marks(length, thickness, dotted);

        Assert.NotEmpty(marks);
        Assert.Equal(0f, marks[0].Start, Tolerance);

        // ⚠ The property the obvious implementation does not have. Walking in fixed periods until
        // the run is used up leaves a stub of whatever length the arithmetic happened to produce, so
        // one corner of a dashed box carries a full mark and the other a sliver — and the sliver
        // changes length when the box is resized by a pixel.
        Assert.Equal(length, marks[^1].Start + marks[^1].Length, Tolerance);
    }

    [Theory]
    [InlineData(100f, 1f, false)]
    [InlineData(100f, 2f, false)]
    [InlineData(37.5f, 1.5f, false)]
    [InlineData(100f, 1f, true)]
    [InlineData(255f, 3f, true)]
    public void The_ink_is_the_mark_length_times_the_count_and_never_more_than_the_run(
        float length,
        float thickness,
        bool dotted
    ) {
        var marks = Marks(length, thickness, dotted);
        var mark = Dashes.MarkOf(dotted ? StrokeStyle.Dotted : StrokeStyle.Dashed, thickness);
        var ink = marks.Sum(m => m.Length);

        Assert.Equal(marks.Count * mark, ink, Tolerance);
        Assert.True(ink < length, $"a broken line covers less than the run: {ink} of {length}");
    }

    [Theory]
    [InlineData(100f, 1f, false)]
    [InlineData(100f, 1f, true)]
    [InlineData(63f, 2f, false)]
    public void No_two_marks_overlap_and_every_gap_is_the_same(float length, float thickness, bool dotted) {
        var marks = Marks(length, thickness, dotted);

        Assert.True(marks.Count > 1);

        var gap = marks[1].Start - (marks[0].Start + marks[0].Length);

        Assert.True(gap > 0f, $"marks must not touch, and the gap here is {gap}");

        for (var i = 1; i < marks.Count; i++) {
            Assert.Equal(gap, marks[i].Start - (marks[i - 1].Start + marks[i - 1].Length), Tolerance);
        }
    }

    [Theory]
    [InlineData(2f, 1f, false)]
    [InlineData(3f, 1f, false)]
    [InlineData(1.5f, 2f, true)]
    public void A_run_with_no_room_for_two_marks_is_one_mark_spanning_it(float length, float thickness, bool dotted) {
        // ⚠ Solid, not a stub and not nothing. A short edge that vanished because its dash pattern
        // did not fit is a hole in a box, and a browser draws the line.
        var mark = Assert.Single(Marks(length, thickness, dotted));

        Assert.Equal(0f, mark.Start, Tolerance);
        Assert.Equal(length, mark.Length, Tolerance);
    }

    [Fact]
    public void A_dotted_run_has_more_marks_than_a_dashed_one_of_the_same_length() {
        // The two differ only in the mark and gap lengths, and a dot is a third of a dash.
        Assert.True(Marks(200f, 2f, dotted: true).Count > Marks(200f, 2f, dotted: false).Count);
    }

    [Fact]
    public void An_empty_run_produces_nothing() {
        Assert.Empty(Marks(0f, 1f, dotted: false));
        Assert.Empty(Marks(-4f, 1f, dotted: true));
    }

    [Fact]
    public void A_dot_is_square_and_a_dash_is_three_of_them() {
        Assert.Equal(2f, Dashes.MarkOf(StrokeStyle.Dotted, 2f), Tolerance);
        Assert.Equal(6f, Dashes.MarkOf(StrokeStyle.Dashed, 2f), Tolerance);
    }
}
