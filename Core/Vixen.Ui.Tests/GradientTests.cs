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

    [Fact]
    public void A_stop_may_be_a_color_mix() {
        // Falls out of the stop splitter already being depth-aware and the stop being parsed by
        // `StyleValueParser` — but worth a test, because `color-mix(in oklab, …, …)` is the first
        // value to reach here with commas *inside* a stop, and a splitter that had been written the
        // naive way would have read this as four stops and refused the gradient as a three-stop one.
        //
        // ⚠ It is also the shape `from-accent/50` compiles to, so it is not hypothetical. And it
        // leaves task #47 — which space a gradient *lerps between* its stops in — exactly where it
        // was: a stop is resolved to a linear colour before the shader sees it, so the question of
        // what happens between two stops is still open and still separate.
        using var document = Drawn(
            ".probe { background-image: linear-gradient(to right, "
            + "color-mix(in oklab, #ff0000 50%, transparent), #0000ff); }"
        );

        var style = Assert.Single(Gradients(document));
        var start = GradientCommand(document).Color;
        var red = Hex("#ff0000");

        Assert.Equal(Hex("#0000ff"), style.GradientEnd);
        Assert.Equal(red.R, start.R, 3);
        Assert.Equal(red.G, start.G, 3);
        Assert.Equal(red.B, start.B, 3);
        Assert.Equal(0.5f, start.A, 3);
    }

    [Fact]
    public void A_composed_stop_may_carry_a_mix_through_a_custom_property() {
        // ⚠ The two halves of the previous test put together, and the shape `from-accent/50`
        // actually compiles to: `UtilityComposition.StopList` builds the gradient out of `var()`
        // references, and the *colour* — commas and all — lives in the custom property. So the
        // substitution has to hand the reader a stop containing commas, and the reader's
        // depth-aware `SplitCommas` has to keep it whole. Either half being wrong reads the same
        // way from outside: a gradient refused as having three stops.
        using var document = Drawn(
            """
            .probe {
                --tw-gradient-from: color-mix(in oklab, #ff0000 50%, transparent);
                --tw-gradient-to: #0000ff;
                background-image: linear-gradient(to right, var(--tw-gradient-from) 0%, var(--tw-gradient-to) 100%);
            }
            """
        );

        var style = Assert.Single(Gradients(document));
        var start = GradientCommand(document).Color;

        Assert.Equal(Hex("#0000ff"), style.GradientEnd);
        Assert.Equal(Hex("#ff0000").R, start.R, 3);
        Assert.Equal(0.5f, start.A, 3);
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

    // ── The third stop ──────────────────────────────────────────────────────────────────────

    /// <summary>`via-*`, as the composed form actually computes it.</summary>
    /// <remarks>
    ///     ⚠ <b>All three positions stated, because that is what <c>--tw-gradient-*-position</c>'s
    ///     initial values put there.</b> A reader that only handled omitted positions would pass every
    ///     hand-written test and fail on the first real utility class — which is the same trap the
    ///     hex-versus-<c>rgb()</c> notation set, one level down.
    /// </remarks>
    [Fact]
    public void A_middle_stop_reaches_the_side_buffer_as_its_own_colour() {
        using var document = Drawn(
            ".probe { background-image: linear-gradient(to right, "
            + "#ff0000 0%, #00ff00 50%, #0000ff 100%); }"
        );

        var style = Assert.Single(Gradients(document));

        Assert.True(style.HasVia);
        Assert.Equal(Hex("#00ff00"), style.GradientVia);
        Assert.Equal(Hex("#0000ff"), style.GradientEnd);
        Assert.Equal(Hex("#ff0000"), GradientCommand(document).Color);
        Assert.Equal(new GradientStops(0f, 0.5f, 1f), style.Stops);
    }

    /// <summary>Two stops leave the middle lane unread rather than inventing a colour for it.</summary>
    [Fact]
    public void Two_stops_say_so_rather_than_synthesising_a_middle() {
        using var document = Drawn(
            ".probe { background-image: linear-gradient(to right, #ff0000, #0000ff); }"
        );

        Assert.False(Assert.Single(Gradients(document)).HasVia);
    }

    /// <summary>An unstated middle sits halfway between its neighbours, which is not always 50%.</summary>
    /// <remarks>
    ///     ⚠ <b>The case that separates CSS's rule from the constant it looks like.</b> With the ends
    ///     at 20% and 100% the middle lands at 60%, and a reader that wrote 0.5 there would agree with
    ///     CSS on every gradient whose ends are the ends — which is nearly all of them, and never the
    ///     one somebody debugs.
    /// </remarks>
    [Fact]
    public void An_unstated_middle_lands_between_its_neighbours_and_not_at_half() {
        using var document = Drawn(
            ".probe { background-image: linear-gradient(to right, #ff0000 20%, #00ff00, #0000ff); }"
        );

        var stops = Assert.Single(Gradients(document)).Stops;

        Assert.Equal(0.2f, stops.From, 4);
        Assert.Equal(0.6f, stops.Via, 4);
        Assert.Equal(1f, stops.To, 4);
    }

    // ── Stop positions ──────────────────────────────────────────────────────────────────────

    /// <summary>`from-10% to-40%` remaps the ramp instead of moving a colour.</summary>
    [Fact]
    public void Stop_positions_reach_the_side_buffer() {
        using var document = Drawn(
            ".probe { background-image: linear-gradient(to right, #ff0000 10%, #0000ff 40%); }"
        );

        var stops = Assert.Single(Gradients(document)).Stops;

        Assert.Equal(0.1f, stops.From, 4);
        Assert.Equal(0.4f, stops.To, 4);
    }

    /// <summary>A stop earlier than its predecessor is clamped up to it, which is a hard edge.</summary>
    /// <remarks>
    ///     ⚠ CSS's rule, and without it the shader's span goes negative and lands on its zero-width
    ///     branch — drawing the right picture by accident, which stops being true the moment either
    ///     side is touched.
    /// </remarks>
    [Fact]
    public void A_backwards_stop_list_becomes_a_hard_edge() {
        using var document = Drawn(
            ".probe { background-image: linear-gradient(to right, #ff0000 60%, #0000ff 20%); }"
        );

        var stops = Assert.Single(Gradients(document)).Stops;

        Assert.Equal(0.6f, stops.From, 4);
        Assert.Equal(0.6f, stops.To, 4);
    }

    /// <summary>A stop outside the box is kept outside it rather than clamped onto the edge.</summary>
    /// <remarks>
    ///     ⚠ <c>red -20%, blue 120%</c> is a ramp whose ends are off both edges, so what shows is its
    ///     middle. Clamping to <c>[0, 1]</c> here would flatten it to a full-width ramp — brighter at
    ///     both edges than the author asked for, and a picture rather than an error.
    /// </remarks>
    [Fact]
    public void A_stop_outside_the_box_stays_outside_it() {
        using var document = Drawn(
            ".probe { background-image: linear-gradient(to right, #ff0000 -20%, #0000ff 120%); }"
        );

        var stops = Assert.Single(Gradients(document)).Stops;

        Assert.Equal(-0.2f, stops.From, 4);
        Assert.Equal(1.2f, stops.To, 4);
    }

    // ── The two round shapes ────────────────────────────────────────────────────────────────

    /// <summary>`bg-radial` reaches the side buffer as a radial and carries no axis.</summary>
    /// <remarks>
    ///     ⚠ <b>The zero axis is the whole reason <see cref="BoxStyle.Shape" /> exists.</b> The
    ///     two-stop implementation used a zero axis as its "no gradient" sentinel, and a radial
    ///     gradient genuinely has no direction — so under the old rule this record would have been
    ///     erased on its way to the shader.
    /// </remarks>
    [Fact]
    public void A_radial_gradient_is_a_radial_with_no_direction() {
        using var document = Drawn(
            ".probe { background-image: radial-gradient(#ff0000, #0000ff); }"
        );

        var style = Assert.Single(Gradients(document));

        Assert.Equal(GradientShape.Radial, style.Shape);
        Assert.Equal(Vector2.Zero, style.GradientAxis);
        Assert.Equal(Hex("#0000ff"), style.GradientEnd);
    }

    /// <summary>And the default geometry spelled out is the same gradient.</summary>
    [Fact]
    public void Spelling_out_the_radial_default_changes_nothing() {
        using var document = Drawn(
            ".probe { background-image: radial-gradient(ellipse farthest-corner, #ff0000, #0000ff); }"
        );

        Assert.Equal(GradientShape.Radial, Assert.Single(Gradients(document)).Shape);
    }

    /// <summary>A conic gradient's `from` angle rides the axis lane, in CSS's own convention.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero is <i>up</i>, not right.</b> The shader recovers the angle with
    ///     <c>atan2(x, -y)</c>, which inverts the same <c>(sin θ, -cos θ)</c> the linear path writes —
    ///     so a conic gradient needs no lane of its own, and getting the convention wrong rotates
    ///     every one of them a quarter turn while still looking like a conic gradient.
    /// </remarks>
    [Theory]
    [InlineData("conic-gradient(#ff0000, #0000ff)", 0f, -1f)]
    [InlineData("conic-gradient(from 90deg, #ff0000, #0000ff)", 1f, 0f)]
    [InlineData("conic-gradient(from 0.5turn, #ff0000, #0000ff)", 0f, 1f)]
    public void A_conic_gradients_start_angle_rides_the_axis(string image, float x, float y) {
        using var document = Drawn($".probe {{ background-image: {image}; }}");

        var style = Assert.Single(Gradients(document));

        Assert.Equal(GradientShape.Conic, style.Shape);
        AssertClose(new Vector2(x, y), Direction(style.GradientAxis));
    }

    /// <summary>A round gradient on a zero-sized box is still not emitted.</summary>
    /// <remarks>
    ///     ⚠ Guarded by the layout, not by the axis: a radial gradient's axis is legitimately zero, so
    ///     the degenerate-box test had to stop being the same test as the no-gradient test.
    /// </remarks>
    [Fact]
    public void A_radial_on_a_zero_sized_box_is_still_a_box_with_no_area() {
        using var document = Drawn(
            ".probe { background-image: radial-gradient(#ff0000, #0000ff); }",
            ".probe { width: 0px; height: 0px; }"
        );

        Assert.DoesNotContain(document.Drawing.Commands, command => command.Kind == DrawCommandKind.Rectangle);
    }

    // ── The interpolation space ─────────────────────────────────────────────────────────────

    /// <summary>Which space a gradient interpolates in, and what an unhinted one means.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The default is sRGB, which is neither of the two answers that were already in the
    ///         tree.</b> The engine paints in linear RGB and the shader lerped there; CSS says an
    ///         unhinted gradient is sRGB. A hand-written <c>.vcss</c> rule should match a browser, so
    ///         CSS wins on the CSS path — and <see cref="GradientSpace.Linear" /> stays reachable for
    ///         <see cref="BoxStyle.Vertical" />, which has no CSS text and therefore no hint to honour.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>srgb-linear</c> is CSS's name for what this engine calls
    ///         <see cref="GradientSpace.Linear" />, and mapping it anywhere else would make the one
    ///         spelling that asks for the engine's own behaviour the one it cannot express.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("linear-gradient(to right, #ff0000, #0000ff)", GradientSpace.Srgb)]
    [InlineData("linear-gradient(in oklab, #ff0000, #0000ff)", GradientSpace.Oklab)]
    [InlineData("linear-gradient(to right in oklab, #ff0000, #0000ff)", GradientSpace.Oklab)]
    [InlineData("linear-gradient(in srgb, #ff0000, #0000ff)", GradientSpace.Srgb)]
    [InlineData("linear-gradient(in srgb-linear, #ff0000, #0000ff)", GradientSpace.Linear)]
    [InlineData("radial-gradient(in oklab, #ff0000, #0000ff)", GradientSpace.Oklab)]
    [InlineData("conic-gradient(from 45deg in oklab, #ff0000, #0000ff)", GradientSpace.Oklab)]
    public void The_interpolation_hint_is_honoured_rather_than_refused(string image, GradientSpace space) {
        using var document = Drawn($".probe {{ background-image: {image}; }}");

        Assert.Equal(space, Assert.Single(Gradients(document)).Space);
    }

    /// <summary>The geometry and the hint may arrive in either order, because CSS's grammar is a `||`.</summary>
    /// <remarks>
    ///     ⚠ Tailwind emits the first order every time, which is precisely why the second would have
    ///     gone unnoticed by a reader that took a prefix and then the rest.
    /// </remarks>
    [Fact]
    public void The_hint_may_come_before_the_direction() {
        using var document = Drawn(
            ".probe { background-image: linear-gradient(in oklab to right, #ff0000, #0000ff); }"
        );

        var style = Assert.Single(Gradients(document));

        Assert.Equal(GradientSpace.Oklab, style.Space);
        AssertClose(new Vector2(1f, 0f), Direction(style.GradientAxis));
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
    // A fourth stop. Three is a start, a middle and an end, and a fourth cannot be resampled into
    // them without being right at both ends and wrong in the interior.
    [InlineData("linear-gradient(to right, #ff0000, #00ff00, #0000ff, #ffffff)")]
    // The ramp runs once and is clamped at both ends, so repeating is a different shader.
    [InlineData("repeating-linear-gradient(to right, #ff0000, #0000ff)")]
    [InlineData("repeating-conic-gradient(#ff0000, #0000ff)")]
    // A polar interpolation space travels along a hue arc, which is not a lerp in any three lanes.
    [InlineData("linear-gradient(in oklch, #ff0000, #0000ff)")]
    [InlineData("linear-gradient(in hsl longer hue, #ff0000, #0000ff)")]
    [InlineData("linear-gradient(to right in lab, #ff0000, #0000ff)")]
    // An explicit centre or extent on a round gradient. The record has no lanes for either, which is
    // the trade that let all four of A11's owed pieces fit inside two more `Vector4`s.
    [InlineData("radial-gradient(at 20% 80%, #ff0000, #0000ff)")]
    [InlineData("radial-gradient(circle, #ff0000, #0000ff)")]
    [InlineData("radial-gradient(closest-side, #ff0000, #0000ff)")]
    [InlineData("radial-gradient(80px, #ff0000, #0000ff)")]
    [InlineData("radial-gradient(50% 30%, #ff0000, #0000ff)")]
    [InlineData("conic-gradient(from 45deg at top left, #ff0000, #0000ff)")]
    // A conic's angle has to be spelled `from <angle>`; a bare one is not CSS.
    [InlineData("conic-gradient(45deg, #ff0000, #0000ff)")]
    // A position this file cannot resolve. A length needs the gradient line, which is a function of
    // the box and is not known here.
    [InlineData("linear-gradient(to right, #ff0000 10px, #0000ff)")]
    [InlineData("linear-gradient(to right, #ff0000 calc(10% + 2px), #0000ff)")]
    // Two positions on one stop is CSS's shorthand for the same colour twice — a four-stop ramp.
    [InlineData("linear-gradient(to right, #ff0000 0% 40%, #0000ff)")]
    // A bare position between two stops is an interpolation *hint*, a different feature.
    [InlineData("linear-gradient(to right, #ff0000, 30%, #0000ff)")]
    // Not a gradient at all.
    [InlineData("url(paper.png)")]
    [InlineData("none")]
    // Malformed, or a direction with no reading.
    [InlineData("linear-gradient(to sideways, #ff0000, #0000ff)")]
    [InlineData("linear-gradient(to right, #ff0000)")]
    [InlineData("linear-gradient(to right, notacolour, #0000ff)")]
    // `to` is a linear gradient's word and `from` is a conic's; each on the other is meaningless.
    [InlineData("radial-gradient(to right, #ff0000, #0000ff)")]
    [InlineData("linear-gradient(from 45deg, #ff0000, #0000ff)")]
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
            + " background-image: repeating-linear-gradient(to right, #ff0000, #0000ff); }"
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
