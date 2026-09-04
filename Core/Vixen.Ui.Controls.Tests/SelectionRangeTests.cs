// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What a field actually emits for a selection that is not one rectangle.</summary>
/// <remarks>
///     <para>
///         <b>The band is resolved by <c>TextLine.VisualRanges</c> and this is the test that it is
///         resolved by anything at all.</b> <c>Vixen.Ui.Tests.CaretAffinityTests</c> judges the
///         geometry; the failure mode this file exists for is the one this repository meets most
///         often — a correct helper nothing calls, with the field still painting its own single span
///         beside it.
///     </para>
///     <para>
///         ⚠ <b>The oracle is the emitted draw list, and it has to be: a selection drawn as one
///         rectangle and one drawn as two have the same bounding box.</b> Anything reading extent —
///         a screenshot's coloured columns included — passes against the defect, because the defect
///         is that the middle of that box is filled in when it should not be.
///     </para>
/// </remarks>
public class SelectionRangeTests {
    const string Latin = "AB";
    const string Arabic = "ات";

    /// <summary>Pure magenta, which nothing in the theme paints.</summary>
    /// <remarks>
    ///     ⚠ Channels of exactly 0 and 1 so that the linear conversion the draw list holds its
    ///     colours in is the identity on them, and the match can be exact rather than a tolerance
    ///     wide enough to catch something else.
    /// </remarks>
    const string SelectionColour = "textbox { --selection-color: #ff00ff; width: 400px; }";

    static (ControlFixture Fixture, TextBox Field) Bidi() {
        var fixture = new ControlFixture(css: SelectionColour);
        fixture.Document.Fonts.AddFallback(FieldProbe.Aran);

        var field = fixture.Add<TextBox>();
        field.Value = Latin + Arabic;
        fixture.Document.Focus(field);
        fixture.Update();

        var line = FieldProbe.Block(field).Lines[0];

        // Vacuous otherwise, for the reason `Vixen.Ui.Tests` states: one run, or two facing the same
        // way, and both readings of every boundary are the same pixel.
        Assert.Equal(2, line.Runs.Length);
        Assert.NotEqual(line.Runs[0].Level % 2, line.Runs[1].Level % 2);

        return (fixture, field);
    }

    static List<DrawCommand> Bands(ControlFixture fixture) =>
        fixture.Document.Drawing.Commands
            .Where(command => command.Kind == DrawCommandKind.Rectangle
                && command.Color.R == 1f
                && command.Color.G == 0f
                && command.Color.B == 1f)
            .ToList();

    [Fact]
    public void A_selection_crossing_a_direction_change_is_painted_as_two_bands() {
        var (fixture, field) = Bidi();
        using var owned = fixture;

        // The second Latin letter and the first Arabic one, with the second Arabic letter drawn
        // between them and outside the selection.
        field.MoveCaret(1);
        field.MoveCaret(3, extend: true);
        fixture.Update();

        var bands = Bands(fixture);

        Assert.Equal(2, bands.Count);

        var line = FieldProbe.Block(field).Lines[0];
        var expected = new List<(float X, float Width)>();
        line.VisualRanges(1, 3, expected);

        Assert.Equal(expected.Count, bands.Count);

        // Same widths, in the same left-to-right order, which is what says the field is painting
        // what the resolver returned rather than two rectangles of its own.
        foreach (var pair in expected.OrderBy(range => range.X).Zip(bands.OrderBy(band => band.X))) {
            Assert.Equal(pair.First.Width, pair.Second.Width, 0.01f);
        }

        // ⚠ And the gap is the point. The unselected letter sits between the two bands, and a field
        // that painted one rectangle would have covered it while passing every count above.
        var gap = bands.Max(band => band.X) - bands.Min(band => band.X + band.Width);

        Assert.True(gap > 1f, "the unselected letter between the two bands is not painted over");
    }

    [Fact]
    public void A_selection_inside_one_direction_is_still_a_single_band() {
        var (fixture, field) = Bidi();
        using var owned = fixture;

        field.MoveCaret(0);
        field.MoveCaret(2, extend: true);
        fixture.Update();

        // The compatibility half: splitting a selection that needs splitting must not split one that
        // does not, or every ordinary highlight becomes several quads for nothing.
        var band = Assert.Single(Bands(fixture));

        Assert.Equal(FieldProbe.Block(field).Lines[0].Runs[0].Width, band.Width, 0.01f);
    }
}
