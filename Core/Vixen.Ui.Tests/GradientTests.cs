// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What the draw list makes of a computed <c>background-image</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here began as its own opposite.</b> Before this file's subject
///         existed, <c>background-image</c> was a line in <c>InertProperties.txt</c> — the cascade
///         resolved a real <c>linear-gradient(…)</c> and the draw list contained nothing at all. The
///         first version of each test below asserted that emptiness, which is what established the
///         gap rather than claiming it.
///     </para>
///     <para>
///         ⚠ <b>The refusals are tested as hard as the successes, and they are the more important
///         half.</b> A gradient this engine cannot draw has to be visibly absent rather than quietly
///         approximated, so "three stops paints nothing" is a feature with a test and not an
///         omission — see <see cref="GradientRefusal" />.
///     </para>
/// </remarks>
public class GradientTests {
    static UiDocument Drawn(string css, string probe = ".probe { width: 40px; height: 20px; }") {
        var document = new UiDocument(200f, 200f);
        document.Load(probe + " " + css);
        document.Root.Add("div", classNames: "probe");
        document.Update();
        document.Draw();

        return document;
    }

    /// <summary>The rectangles that carry a gradient, and the style each one points at.</summary>
    static IReadOnlyList<BoxStyle> Gradients(UiDocument document) =>
        [
            .. document.Drawing.Commands
                .Where(command => command is { Kind: DrawCommandKind.Rectangle, HasStyle: true })
                .Select(command => document.Drawing.Boxes[command.Offset])
                .Where(style => style.HasGradient)
        ];

    static DrawCommand GradientCommand(UiDocument document) =>
        document.Drawing.Commands.Single(
            command => command is { Kind: DrawCommandKind.Rectangle, HasStyle: true }
                && document.Drawing.Boxes[command.Offset].HasGradient
        );

    /// <summary>The direction the axis points, normalised, so a test can name it without lengths.</summary>
    static Vector2 Direction(Vector2 axis) => Vector2.Normalize(axis);

    /// <summary>A hex code as the linear colour the cascade turns it into.</summary>
    static Color4 Hex(string text) {
        Assert.True(Color.TryParseHex(text, out var colour));
        return colour.ToLinear();
    }

    static void AssertClose(Vector2 expected, Vector2 actual) {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
    }

    // ── The thing that was owed ─────────────────────────────────────────────────────────────

    /// <summary>The composed form, exactly as `bg-linear-to-r from-* to-*` computes it.</summary>
    /// <remarks>
    ///     ⚠ <b>Hex stops and not `rgb(…)`, which is the notation this actually arrives in.</b> A
    ///     value containing a <c>var()</c> is left verbatim by the cascade, and the substitution that
    ///     turns <c>--tw-gradient-stops</c> into colours happens after the only step that would have
    ///     normalised them. Writing this test with <c>rgb(…)</c> would have passed against a parser
    ///     that never worked on a single real utility class.
    /// </remarks>
    [Fact]
    public void The_composed_two_stop_form_reaches_the_side_buffer() {
        using var document = Drawn(
            ".probe { background-image: linear-gradient(to right, #ff0000 0%, #0000ff 100%); }"
        );

        var style = Assert.Single(Gradients(document));

        Assert.True(style.HasGradient);
        AssertClose(new Vector2(1f, 0f), Direction(style.GradientAxis));
        Assert.Equal(Hex("#0000ff"), style.GradientEnd);

        // The near stop is the command's own colour: the shader has one colour and one end, and the
        // start is the thing it lerps *from*.
        Assert.Equal(Hex("#ff0000"), GradientCommand(document).Color);
    }

    /// <summary>And the hand-written form, which the cascade normalises to `rgb(…)` instead.</summary>
    /// <remarks>
    ///     ⚠ Both notations, in one property, in one document. `background-color: #f00` comes back
    ///     normalised because ExCSS parsed it; a composed gradient never can be. A parser written
    ///     against either one alone is half a parser.
    /// </remarks>
    [Fact]
    public void The_hand_written_form_parses_through_the_same_path() {
        using var document = Drawn(
            ".probe { background-image: linear-gradient(to bottom, rgb(255, 0, 0), rgb(0, 0, 255)); }"
        );

        var style = Assert.Single(Gradients(document));

        AssertClose(new Vector2(0f, 1f), Direction(style.GradientAxis));
        Assert.Equal(Hex("#0000ff"), style.GradientEnd);
    }

    // ── Directions ──────────────────────────────────────────────────────────────────────────

    /// <summary>The four sides, in the engine's y-down screen space.</summary>
    /// <remarks>
    ///     ⚠ <b><c>to top</c> is negative y.</b> Written out per side rather than checked as a pair,
    ///     because a sign error here inverts exactly half the directions — and <c>to bottom</c>, the
    ///     one a spot check would look at, is the half that stays right.
    /// </remarks>
    [Theory]
    [InlineData("to top", 0f, -1f)]
    [InlineData("to right", 1f, 0f)]
    [InlineData("to bottom", 0f, 1f)]
    [InlineData("to left", -1f, 0f)]
    public void Each_side_keyword_points_the_axis_at_it(string direction, float x, float y) {
        using var document = Drawn(
            $".probe {{ background-image: linear-gradient({direction}, #ff0000, #0000ff); }}"
        );

        AssertClose(new Vector2(x, y), Direction(Assert.Single(Gradients(document)).GradientAxis));
    }

    /// <summary>A corner is not 45° unless the box is square, and this box is not.</summary>
    /// <remarks>
    ///     ⚠ <b>The measurement that says the CSS corner rule is implemented rather than approximated.</b>
    ///     The probe is 40×20, so <c>to bottom right</c> resolves to an axis along (h, w) = (20, 40) —
    ///     twice as steep as it is wide, and nothing like the (1, 1) a plain diagonal would give. Both
    ///     end on the corner, because the shader normalises by the box's extent along the axis; what
    ///     the rule fixes is where the *midpoint* falls, which is why asserting the corner alone would
    ///     pass against the wrong implementation.
    /// </remarks>
    [Theory]
    [InlineData("to bottom right", 20f, 40f)]
    [InlineData("to top right", 20f, -40f)]
    [InlineData("to bottom left", -20f, 40f)]
    [InlineData("to top left", -20f, -40f)]
    public void A_corner_keyword_follows_the_boxs_aspect_and_not_a_diagonal(string direction, float x, float y) {
        using var document = Drawn(
            $".probe {{ background-image: linear-gradient({direction}, #ff0000, #0000ff); }}"
        );

        AssertClose(Direction(new Vector2(x, y)), Direction(Assert.Single(Gradients(document)).GradientAxis));
    }

    /// <summary>All four angle units, because supporting three of them looks identical until it is not.</summary>
    [Theory]
    [InlineData("0deg", 0f, -1f)]
    [InlineData("90deg", 1f, 0f)]
    [InlineData("180deg", 0f, 1f)]
    [InlineData("0.25turn", 1f, 0f)]
    [InlineData("100grad", 1f, 0f)]
    [InlineData("3.14159265rad", 0f, 1f)]
    public void An_angle_resolves_in_every_unit_css_has(string angle, float x, float y) {
        using var document = Drawn(
            $".probe {{ background-image: linear-gradient({angle}, #ff0000, #0000ff); }}"
        );

        AssertClose(new Vector2(x, y), Direction(Assert.Single(Gradients(document)).GradientAxis));
    }

    /// <summary>No direction at all is `to bottom`, which is 180° and not zero.</summary>
    /// <remarks>
    ///     ⚠ Defaulting to zero would run every directionless gradient in the interface upside down,
    ///     and upside down is a picture rather than an error.
    /// </remarks>
    [Fact]
    public void A_gradient_with_no_direction_runs_down_the_box() {
        using var document = Drawn(".probe { background-image: linear-gradient(#ff0000, #0000ff); }");

        AssertClose(new Vector2(0f, 1f), Direction(Assert.Single(Gradients(document)).GradientAxis));
    }

    // ── Layering ────────────────────────────────────────────────────────────────────────────

    /// <summary>An element whose only background is a gradient still draws.</summary>
    /// <remarks>
    ///     ⚠ <b>The case every Tailwind gradient utility actually produces.</b>
    ///     <c>bg-linear-to-r from-accent to-surface-3</c> sets no <c>background-color</c> whatsoever, so
    ///     a builder that painted the image only over an existing fill would draw nothing at all on
    ///     exactly the classes this work exists to support.
    /// </remarks>
    [Fact]
    public void A_gradient_with_no_background_colour_still_paints() {
        using var document = Drawn(".probe { background-image: linear-gradient(to right, #ff0000, #0000ff); }");

        Assert.Single(Gradients(document));
    }

    /// <summary>With both, the colour is under the image — two layers, in CSS's order.</summary>
    /// <remarks>
    ///     ⚠ Not an alternative to the colour. A gradient whose near stop is <c>transparent</c> — which
    ///     is every <c>bg-linear-*</c> with no <c>from-*</c> — shows the flat colour through it, and
    ///     collapsing the two layers into one would lose that entirely.
    /// </remarks>
    [Fact]
    public void A_colour_and_an_image_are_two_layers_in_that_order() {
        using var document = Drawn(
            ".probe { background-color: #00ff00; background-image: linear-gradient(to right, #ff0000, #0000ff); }"
        );

        var rectangles = document.Drawing.Commands
            .Where(command => command.Kind == DrawCommandKind.Rectangle)
            .ToList();

        Assert.Equal(2, rectangles.Count);

        // The flat colour first, and it is genuinely flat.
        Assert.False(rectangles[0].HasStyle);
        Assert.Equal(Hex("#00ff00"), rectangles[0].Color);

        // Then the gradient, over it.
        Assert.True(rectangles[1].HasStyle);
        Assert.True(document.Drawing.Boxes[rectangles[1].Offset].HasGradient);
    }

    /// <summary>A gradient keeps the element's corner radii rather than squaring them off.</summary>
    [Fact]
    public void A_gradient_carries_the_corners_it_is_drawn_inside() {
        using var document = Drawn(
            ".probe { border-radius: 4px 8px 12px 16px;"
            + " background-image: linear-gradient(to right, #ff0000, #0000ff); }"
        );

        var style = Assert.Single(Gradients(document));

        Assert.Equal(new Vector2(4f, 4f), style.Corners.TopLeft);
        Assert.Equal(new Vector2(8f, 8f), style.Corners.TopRight);
        Assert.Equal(new Vector2(12f, 12f), style.Corners.BottomRight);
        Assert.Equal(new Vector2(16f, 16f), style.Corners.BottomLeft);
    }

    // ── The refusals ────────────────────────────────────────────────────────────────────────

    /// <summary>Everything this engine cannot draw draws nothing, and never something else.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the test that makes the feature honest.</b> Each of these has an obvious
    ///     "reasonable" approximation — take the first and last of three stops, ignore the position,
    ///     treat a radial as a linear, drop the <c>in oklab</c> — and every one of them draws a
    ///     confident, plausible, wrong picture. Silence is the failure mode the parity programme
    ///     exists to remove, so the refusals are asserted rather than assumed.
    /// </remarks>
    [Theory]
    // A middle stop. `via-*` composes a real one and there is no third colour in `BoxStyle`.
    [InlineData("linear-gradient(to right, #ff0000 0%, #00ff00 50%, #0000ff 100%)")]
    // A stop that is not at its end. The shader's parameter has no scale or bias to remap with.
    [InlineData("linear-gradient(to right, #ff0000 10%, #0000ff 100%)")]
    [InlineData("linear-gradient(to right, #ff0000 0%, #0000ff 90%)")]
    // The two shapes `GradientAxis` cannot express: it is a direction, so it is linear by construction.
    [InlineData("radial-gradient(#ff0000, #0000ff)")]
    [InlineData("conic-gradient(#ff0000, #0000ff)")]
    [InlineData("repeating-linear-gradient(to right, #ff0000, #0000ff)")]
    // An interpolation hint changes the picture, which is the entire reason the syntax exists.
    [InlineData("linear-gradient(in oklab, #ff0000, #0000ff)")]
    [InlineData("linear-gradient(to right in oklab, #ff0000, #0000ff)")]
    // Not a gradient at all.
    [InlineData("url(paper.png)")]
    [InlineData("none")]
    // Malformed, or a direction with no reading.
    [InlineData("linear-gradient(to sideways, #ff0000, #0000ff)")]
    [InlineData("linear-gradient(to right, #ff0000)")]
    [InlineData("linear-gradient(to right, notacolour, #0000ff)")]
    public void A_gradient_this_engine_cannot_draw_paints_nothing(string image) {
        using var document = Drawn($".probe {{ background-image: {image}; }}");

        Assert.Empty(Gradients(document));
    }

    /// <summary>A refusal does not take the background colour down with it.</summary>
    /// <remarks>
    ///     ⚠ The two layers are independent, so an unreadable image leaves a flat element rather than
    ///     an invisible one — which is both what CSS does with an undrawable layer and the difference
    ///     between a missing feature and a broken control.
    /// </remarks>
    [Fact]
    public void A_refused_image_leaves_the_background_colour_alone() {
        using var document = Drawn(
            ".probe { background-color: #00ff00;"
            + " background-image: radial-gradient(#ff0000, #0000ff); }"
        );

        var rectangle = Assert.Single(
            document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.Rectangle
        );

        Assert.Equal(Hex("#00ff00"), rectangle.Color);
        Assert.False(rectangle.HasStyle);
    }

    /// <summary>A box with no area writes no gradient record.</summary>
    /// <remarks>
    ///     ⚠ A zero axis is the side buffer's sentinel for "no gradient", so a degenerate box that
    ///     wrote one anyway would produce a record saying *flat* and paint the near stop over the
    ///     whole element — the one colour of a gradient, which is the silent wrongness again.
    /// </remarks>
    [Fact]
    public void A_zero_sized_box_writes_no_gradient() {
        using var document = Drawn(
            ".probe { background-image: linear-gradient(to bottom right, #ff0000, #0000ff); }",
            ".probe { width: 0px; height: 0px; }"
        );

        Assert.Empty(Gradients(document));
    }
}
