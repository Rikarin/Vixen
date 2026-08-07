// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What the draw list makes of the per-edge and per-corner border longhands.</summary>
/// <remarks>
///     ⚠ <b>Written first against the behaviour that was there, and then inverted.</b> Every
///     assertion in this file began as its own opposite — the measurement that established the gap
///     rather than a claim about it. The commit history is the proof, and the reason the file is
///     shaped as one test per longhand rather than one test per feature.
/// </remarks>
public class BorderLonghandTests {
    static UiDocument Drawn(string css) {
        var document = new UiDocument(200f, 200f);
        document.Load(".probe { width: 40px; height: 20px; } " + css);
        document.Root.Add("div", classNames: "probe");
        document.Update();
        document.Draw();

        return document;
    }

    static IReadOnlyList<DrawCommand> Borders(UiDocument document) =>
        [.. document.Drawing.Commands.Where(command => command.Kind == DrawCommandKind.Border)];

    /// <summary>The rectangles an element with no background of its own drew.</summary>
    /// <remarks>
    ///     ⚠ Which is what a non-uniform border is made of. The box shader resolves a ring from one
    ///     thickness and one colour, so edges that differ cannot be one command — they are bands, and
    ///     a band is a filled rectangle rather than a hollow one. The probes below set no
    ///     <c>background-color</c> precisely so that every rectangle in the list is a band.
    /// </remarks>
    static IReadOnlyList<DrawCommand> Bands(UiDocument document) =>
        [.. document.Drawing.Commands.Where(command => command.Kind == DrawCommandKind.Rectangle)];

    /// <summary>The uniform case, which is the one that always worked and must stay identical.</summary>
    [Fact]
    public void A_uniform_border_is_one_command() {
        using var document = Drawn(".probe { border-width: 2px; border-color: #ff0000; }");

        var border = Assert.Single(Borders(document));

        Assert.Equal(2f, border.Thickness);
        Assert.Equal(44f, border.Width);
        Assert.False(border.HasStyle);
    }

    /// <summary>A width on one edge draws that edge, and only that edge.</summary>
    /// <remarks>
    ///     The rule twenty-one times over in the editor's own themes, and it used to draw nothing at
    ///     all: the builder read <c>Edge.Top</c>'s thickness for the whole ring, so a bottom rule was
    ///     a thickness of zero and no command. Its sibling failure is the reason the assertion checks
    ///     the rectangle rather than counting — <c>border-top-width</c> did emit a command, and the
    ///     command was a ring around all four sides.
    /// </remarks>
    [Fact]
    public void A_single_edge_width_draws_that_edge_only() {
        using var document = Drawn(".probe { border-bottom-width: 2px; border-color: #ff0000; }");

        var band = Assert.Single(Bands(document));

        Assert.Equal(2f, band.Height);
        Assert.Equal(40f, band.Width);
        Assert.Equal(20f, band.Y);
        Assert.Equal(0f, band.X);
    }

    /// <summary>The same, for the edge that used to be the one that worked.</summary>
    [Fact]
    public void A_top_width_no_longer_rings_the_whole_box() {
        using var document = Drawn(".probe { border-top-width: 2px; border-color: #ff0000; }");

        var band = Assert.Single(Bands(document));

        Assert.Equal(0f, band.Y);
        Assert.Equal(2f, band.Height);
        Assert.Equal(40f, band.Width);
    }

    /// <summary>A colour on one edge colours that edge, and does not vanish the rest.</summary>
    [Fact]
    public void A_single_edge_colour_paints_that_edge() {
        using var document = Drawn(
            ".probe { border-width: 2px; border-color: #ff0000; border-bottom-color: #00ff00; }"
        );

        var bands = Bands(document);

        Assert.Equal(4, bands.Count);

        var green = Assert.Single(bands, command => command.Color.G > 0.5f && command.Color.R < 0.5f);

        // The bottom edge of a 44x24 border box, and nothing else.
        Assert.Equal(22f, green.Y);
        Assert.Equal(2f, green.Height);
        Assert.Equal(3, bands.Count(command => command.Color.R > 0.5f && command.Color.G < 0.5f));
    }

    /// <summary>A per-edge colour on its own does not make the whole border disappear.</summary>
    /// <remarks>
    ///     ⚠ The sabotage this pair exists for. <c>border-b-accent</c> was not merely inert: because
    ///     the builder read <c>border-top-color</c> and nothing else, an element given a bottom colour
    ///     and no top one had <i>no</i> colour as far as the draw list was concerned, and its border
    ///     vanished rather than staying whatever it had been.
    /// </remarks>
    [Fact]
    public void A_bottom_colour_alone_still_draws_the_bottom_edge() {
        using var document = Drawn(".probe { border-width: 2px; border-bottom-color: #00ff00; }");

        var band = Assert.Single(Bands(document));

        Assert.Equal(22f, band.Y);
        Assert.True(band.Color.G > 0.5f);
    }

    /// <summary>A radius on one corner rounds that corner, and leaves the others square.</summary>
    [Fact]
    public void A_single_corner_radius_rounds_that_corner_only() {
        using var document = Drawn(".probe { border-top-left-radius: 6px; background-color: #0000ff; }");

        var rectangle = Assert.Single(
            document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.Rectangle
        );

        Assert.True(rectangle.HasStyle, "a non-uniform radius needs the side buffer");

        var style = document.Drawing.Boxes[rectangle.Offset];

        Assert.Equal(6f, style.Corners.TopLeft.X);
        Assert.Equal(0f, style.Corners.TopRight.X);
        Assert.Equal(0f, style.Corners.BottomRight.X);
        Assert.Equal(0f, style.Corners.BottomLeft.X);
    }

    /// <summary>The corner the old code could not see at all.</summary>
    [Fact]
    public void A_bottom_right_radius_is_not_ignored() {
        using var document = Drawn(".probe { border-bottom-right-radius: 6px; background-color: #0000ff; }");

        var rectangle = Assert.Single(
            document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.Rectangle
        );

        Assert.True(rectangle.HasStyle);
        Assert.Equal(6f, document.Drawing.Boxes[rectangle.Offset].Corners.BottomRight.X);
    }
}
