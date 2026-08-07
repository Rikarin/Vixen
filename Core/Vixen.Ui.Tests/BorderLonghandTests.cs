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
    [Fact]
    public void A_single_edge_width_draws_that_edge_only() {
        using var document = Drawn(".probe { border-bottom-width: 2px; border-color: #ff0000; }");

        var border = Assert.Single(Borders(document));

        Assert.Equal(2f, border.Height);
        Assert.Equal(40f, border.Width);
        Assert.Equal(20f, border.Y);
    }

    /// <summary>A colour on one edge colours that edge, and does not vanish the rest.</summary>
    [Fact]
    public void A_single_edge_colour_paints_that_edge() {
        using var document = Drawn(
            ".probe { border-width: 2px; border-color: #ff0000; border-bottom-color: #00ff00; }"
        );

        var borders = Borders(document);

        Assert.Equal(4, borders.Count);
        Assert.Contains(borders, command => command.Color.G > 0.5f && command.Color.R < 0.5f);
        Assert.Equal(3, borders.Count(command => command.Color.R > 0.5f && command.Color.G < 0.5f));
    }

    /// <summary>A radius on one corner rounds that corner, and leaves the others square.</summary>
    [Fact]
    public void A_single_corner_radius_rounds_that_corner_only() {
        using var document = Drawn(".probe { border-top-left-radius: 6px; background-color: #0000ff; }");

        var rectangle = Assert.Single(
            document.Drawing.Commands.Where(command => command.Kind == DrawCommandKind.Rectangle)
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
            document.Drawing.Commands.Where(command => command.Kind == DrawCommandKind.Rectangle)
        );

        Assert.True(rectangle.HasStyle);
        Assert.Equal(6f, document.Drawing.Boxes[rectangle.Offset].Corners.BottomRight.X);
    }
}
