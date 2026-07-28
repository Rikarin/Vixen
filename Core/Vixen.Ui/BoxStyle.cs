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
}

/// <summary>What a box needs beyond a colour and a size.</summary>
/// <param name="Corners">Its corner radii.</param>
/// <param name="GradientEnd">The colour at the far end of its gradient.</param>
/// <param name="GradientAxis">
///     Which way the gradient runs, as a direction in the box's own space. A zero axis means the box
///     is filled flat and <paramref name="GradientEnd" /> is not read.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>A side buffer, not more fields on <see cref="DrawCommand" />.</b> Eight radius floats,
///         a colour and an axis would half again the size of the struct the frame diff compares every
///         frame — and every text run, every path and every plain rectangle would pay for them. The
///         draw list already keeps variable and rare things beside the commands, for glyphs and for
///         path segments, and this is the same argument with a different shape.
///     </para>
///     <para>
///         ⚠ <b>A zero axis is the sentinel for "no gradient"</b> rather than a flag beside it. A
///         gradient along no direction is not a gradient anybody can mean, so the value is free; a
///         separate <c>bool</c> would be a second thing to keep in step and a state — flag set, axis
///         zero — that has no meaning.
///     </para>
/// </remarks>
public readonly record struct BoxStyle(CornerRadii Corners, Color4 GradientEnd, Vector2 GradientAxis) {
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
    public bool HasGradient => GradientAxis != Vector2.Zero;
}
