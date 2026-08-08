// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui;

/// <summary>A box's four corners, each with its own horizontal and vertical radius.</summary>
/// <param name="TopLeft">Its horizontal and vertical radius.</param>
/// <param name="TopRight">Ditto.</param>
/// <param name="BottomRight">Ditto.</param>
/// <param name="BottomLeft">Ditto.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Elliptical, not circular</b>, which is CSS's <c>border-radius: 40px / 20px</c> and is
///         what a pill-shaped button whose height is not its width actually needs. A circular radius
///         is the special case where the two are equal, and <see cref="Uniform" /> writes it.
///     </para>
///     <para>
///         Clockwise from the top left, which is the order CSS lists them in and the order the shader
///         indexes them by. Written down because the alternative — anticlockwise, or starting
///         elsewhere — draws a plausible shape with the wrong corner rounded, and that is a mistake
///         that survives a review.
///     </para>
/// </remarks>
public readonly record struct CornerRadii(
    Vector2 TopLeft,
    Vector2 TopRight,
    Vector2 BottomRight,
    Vector2 BottomLeft
) {
    /// <summary>The same circular radius on every corner.</summary>
    /// <param name="radius">The radius.</param>
    /// <returns>The radii.</returns>
    public static CornerRadii Uniform(float radius) {
        var both = new Vector2(radius, radius);
        return new(both, both, both, both);
    }

    /// <summary>A circular radius per corner, clockwise from the top left.</summary>
    /// <param name="topLeft">The top-left radius.</param>
    /// <param name="topRight">The top-right radius.</param>
    /// <param name="bottomRight">The bottom-right radius.</param>
    /// <param name="bottomLeft">The bottom-left radius.</param>
    /// <returns>The radii.</returns>
    public static CornerRadii Circular(float topLeft, float topRight, float bottomRight, float bottomLeft) =>
        new(
            new Vector2(topLeft, topLeft),
            new Vector2(topRight, topRight),
            new Vector2(bottomRight, bottomRight),
            new Vector2(bottomLeft, bottomLeft)
        );

    /// <summary>Whether every corner is square.</summary>
    public bool IsSquare =>
        TopLeft == Vector2.Zero
        && TopRight == Vector2.Zero
        && BottomRight == Vector2.Zero
        && BottomLeft == Vector2.Zero;

    /// <summary>Whether all four corners are the same circle, and what its radius is.</summary>
    /// <param name="radius">The shared radius, or zero when they are not all the same circle.</param>
    /// <returns>Whether one <c>float</c> describes the whole box.</returns>
    /// <remarks>
    ///     ⚠ <b>This is what keeps the cheap path cheap, and it is why it tests circularity and not
    ///     just equality.</b> <see cref="DrawCommand.Radius" /> is a single <c>float</c>, so a box
    ///     this returns <see langword="true" /> for needs no entry in the draw list's side buffer at
    ///     all — which in a real interface is nearly every box. Four equal but <i>elliptical</i>
    ///     corners are equal to each other and still not expressible in one number, so they have to
    ///     fail this test; returning true for them would silently draw a pill as a circle-cornered
    ///     rectangle.
    /// </remarks>
    public bool IsUniformCircular(out float radius) {
        radius = TopLeft.X;

        return TopLeft.X == TopLeft.Y
            && TopLeft == TopRight
            && TopLeft == BottomRight
            && TopLeft == BottomLeft;
    }
}

/// <summary>What a box needs beyond a colour and a size.</summary>
/// <param name="Corners">Its corner radii.</param>
/// <param name="GradientEnd">The colour at the far end of its gradient.</param>
/// <param name="GradientAxis">
///     Which way the gradient runs, as a direction in the box's own space. A zero axis means the box
///     is filled flat and <paramref name="GradientEnd" /> is not read — unless <see cref="Shape" />
///     says otherwise, because a radial gradient has no direction at all.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>A side buffer, not more fields on <see cref="DrawCommand" />.</b> Eight radius floats,
///         two colours, an axis and a stop list would more than double the size of the struct the
///         frame diff compares every frame — and every text run, every path and every plain rectangle
///         would pay for them. The draw list already keeps variable and rare things beside the
///         commands, for glyphs and for path segments, and this is the same argument with a different
///         shape.
///     </para>
///     <para>
///         ⚠ <b>A zero axis was the sentinel for "no gradient", and <see cref="Shape" /> is what
///         replaced it</b> — because a radial gradient has no axis, so the sentinel could no longer
///         tell "flat" from "round". The zero axis still <i>implies</i> flat, and a non-zero one still
///         implies linear, which is what keeps every caller written against the three-parameter form
///         drawing what it drew.
///     </para>
///     <para>
///         ⚠ <b>Three of the four new members normalise their zero on read, in one place each.</b>
///         A record struct's <c>default</c> has to be the sensible value — this type is reached
///         through <c>default(BoxStyle)</c> on every box that has no side-buffer entry — and the
///         sensible value for a stop list is not <c>(0, 0, 0)</c>. The <c>init</c> accessors normalise
///         in the other direction so that stating a default explicitly still compares equal to leaving
///         it out; without that, two identical styles would differ and the frame diff would redraw.
///     </para>
/// </remarks>
public readonly record struct BoxStyle(CornerRadii Corners, Color4 GradientEnd, Vector2 GradientAxis) {
    readonly GradientShape shape;
    readonly GradientStops stops;

    /// <summary>Which family of gradient fills this box.</summary>
    /// <remarks>
    ///     Unset means linear when there is an axis and none without one, which is what the two-stop
    ///     implementation meant by a zero axis. Every caller predating this member keeps its picture.
    /// </remarks>
    public GradientShape Shape {
        get => shape != GradientShape.None ? shape
            : GradientAxis != Vector2.Zero ? GradientShape.Linear
            : GradientShape.None;
        init => shape = value;
    }

    /// <summary>Which space the stops are interpolated in.</summary>
    public GradientSpace Space { get; init; }

    /// <summary>The colour at the middle stop. Read only when <see cref="HasVia" />.</summary>
    public Color4 GradientVia { get; init; }

    /// <summary>Whether there is a middle stop at all.</summary>
    /// <remarks>
    ///     ⚠ A flag rather than "the via colour is not transparent", because <c>via-transparent</c> is
    ///     a real Tailwind utility and a gradient that fades out through the middle and back is the
    ///     one thing it draws.
    /// </remarks>
    public bool HasVia { get; init; }

    /// <summary>Where the three stops sit along the ramp.</summary>
    public GradientStops Stops {
        get => stops.OrNatural();
        init => stops = value == GradientStops.Default ? default : value;
    }

    /// <summary>Corners and nothing else.</summary>
    /// <param name="corners">The radii.</param>
    /// <returns>The style.</returns>
    public static BoxStyle Rounded(CornerRadii corners) => new(corners, default, Vector2.Zero);

    /// <summary>A gradient down the box, which is what a panel or a button usually wants.</summary>
    /// <param name="end">The colour at the bottom. The command's own colour is the top.</param>
    /// <param name="corners">The radii.</param>
    /// <returns>The style.</returns>
    public static BoxStyle Vertical(Color4 end, CornerRadii corners = default) =>
        new(corners, end, new Vector2(0, 1));

    /// <summary>Whether this box is filled with more than one colour.</summary>
    public bool HasGradient => Shape != GradientShape.None;
}
