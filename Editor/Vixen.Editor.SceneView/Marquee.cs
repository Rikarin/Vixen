// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.SceneView;

/// <summary>A rubber-band rectangle being dragged across a pane, in render pixels.</summary>
/// <remarks>
///     <para>
///         <b>Two corners rather than an origin and a size, because a drag goes in any direction.</b>
///         Storing a width and a height means every consumer has to cope with a negative one, and the
///         one that forgets is the hit test rather than the drawing — so a band dragged up and to the
///         left selects nothing while looking exactly like one that works.
///     </para>
///     <para>
///         ⚠ <b>A band smaller than <see cref="MinimumSize" /> is a click.</b> Nobody presses a mouse
///         button without moving the pointer a pixel or two, so a marquee that resolved at any size at
///         all would take every click in empty space away from the picker — which is the gesture that
///         deselects. The threshold is in render pixels and is deliberately generous.
///     </para>
/// </remarks>
/// <param name="Anchor">Where the drag started, in render pixels from the pane's top-left.</param>
/// <param name="Corner">Where the pointer is now.</param>
/// <param name="Additive">Whether the result extends the selection rather than replacing it.</param>
public readonly record struct Marquee(Vector2 Anchor, Vector2 Corner, bool Additive) {
    /// <summary>How far the pointer has to travel before a press becomes a band, in render pixels.</summary>
    public const float MinimumSize = 3f;

    /// <summary>The left edge.</summary>
    public float Left => MathF.Min(Anchor.X, Corner.X);

    /// <summary>The top edge.</summary>
    public float Top => MathF.Min(Anchor.Y, Corner.Y);

    /// <summary>The right edge.</summary>
    public float Right => MathF.Max(Anchor.X, Corner.X);

    /// <summary>The bottom edge.</summary>
    public float Bottom => MathF.Max(Anchor.Y, Corner.Y);

    /// <summary>How wide, never negative.</summary>
    public float Width => Right - Left;

    /// <summary>How tall, never negative.</summary>
    public float Height => Bottom - Top;

    /// <summary>Whether the drag has gone far enough to be a band rather than a click.</summary>
    /// <remarks>
    ///     ⚠ <b>Either dimension, not both.</b> Dragging along a row of objects is a band a few pixels
    ///     tall and several hundred wide, and requiring both would turn that gesture back into a click
    ///     — which deselects everything the user was in the middle of gathering.
    /// </remarks>
    public bool IsBand => Width >= MinimumSize || Height >= MinimumSize;

    /// <summary>Whether a point in render pixels is inside the band.</summary>
    /// <param name="point">The point.</param>
    /// <returns>Whether it is in.</returns>
    public bool Contains(Vector2 point) =>
        point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    /// <summary>Whether an axis-aligned rectangle in render pixels touches the band.</summary>
    /// <param name="left">Its left edge.</param>
    /// <param name="top">Its top edge.</param>
    /// <param name="right">Its right edge.</param>
    /// <param name="bottom">Its bottom edge.</param>
    /// <returns>Whether the two overlap at all.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Touching, not containing, and that is the choice both reference editors make by
    ///         default.</b> A band that only took what it fully enclosed cannot select anything larger
    ///         than the pane — a floor, a wall, a building — so the gesture stops working precisely
    ///         where a scene gets big. Unreal offers the strict rule as a preference and Unity does
    ///         not offer it at all; the preference is worth having and is not what makes the gesture
    ///         work.
    ///     </para>
    ///     <para>
    ///         Edges count as touching, so a band dragged exactly along an object's silhouette takes
    ///         it. The alternative is a gesture that fails only at the pixel somebody aimed at.
    ///     </para>
    /// </remarks>
    public bool Touches(float left, float top, float right, float bottom) =>
        left <= Right && right >= Left && top <= Bottom && bottom >= Top;

    /// <summary>The same band with the pointer somewhere else.</summary>
    /// <param name="corner">Where the pointer is now.</param>
    /// <returns>The band.</returns>
    public Marquee To(Vector2 corner) => this with { Corner = corner };
}
