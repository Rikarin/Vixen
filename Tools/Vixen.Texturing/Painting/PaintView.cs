// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Texturing.Painting;

/// <summary>A perspective camera in a mesh's own space, as a stroke remembers it.</summary>
/// <param name="Eye">Where it is.</param>
/// <param name="Right">The unit direction a pixel's column grows in.</param>
/// <param name="Up">The unit direction a pixel's row shrinks in.</param>
/// <param name="Forward">The unit direction it looks.</param>
/// <param name="VerticalFieldOfView">Radians, top of the picture to bottom.</param>
/// <param name="Width">The picture's width, in the units a stroke's positions are in.</param>
/// <param name="Height">Its height.</param>
/// <remarks>
///     <para>
///         <b>The camera is part of the stroke, because a stroke is a function of where the brush went
///         (Patina's ADR 0009).</b> A position on a picture means nothing once the camera has moved, so a
///         replay needs the camera the artist was looking through. Pixels and the mesh are all it takes, so
///         a client with any camera of its own can say this.
///     </para>
///     <para>
///         ⚠ <b>Right is carried rather than crossed from the other two, because which way that cross
///         points is the handedness of whoever made the camera.</b> A mirrored view is left-handed on
///         purpose, and <see cref="Mirrored" /> is only a reflection of the three axes because of it.
///     </para>
/// </remarks>
public readonly record struct PaintView(
    Vector3 Eye,
    Vector3 Right,
    Vector3 Up,
    Vector3 Forward,
    float VerticalFieldOfView,
    int Width,
    int Height
) {
    float HalfHeight => MathF.Tan(VerticalFieldOfView * 0.5f);

    float HalfWidth => HalfHeight * Width / Height;

    /// <summary>How far one picture unit reaches on a plane this far in front of the eye.</summary>
    /// <param name="depth">The distance along <see cref="Forward" />.</param>
    /// <returns>The size, in the mesh's units.</returns>
    public float PerPixel(float depth) => 2f * HalfHeight * depth / Height;

    /// <summary>How far in front of the eye a point is.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The distance along <see cref="Forward" />.</returns>
    public float Depth(Vector3 point) => Vector3.Dot(point - Eye, Forward);

    /// <summary>The ray through a position on the picture.</summary>
    /// <param name="position">Column and row, from the top left.</param>
    /// <returns>The ray, from the eye.</returns>
    public Ray RayAt(Vector2 position) {
        var across = ((position.X / Width * 2f) - 1f) * HalfWidth;
        var down = (1f - (position.Y / Height * 2f)) * HalfHeight;

        return new(Eye, Vector3.Normalize(Forward + (Right * across) + (Up * down)));
    }

    /// <summary>Where a point lands on the picture.</summary>
    /// <param name="point">The point.</param>
    /// <returns>Column and row, whether or not that is inside the picture.</returns>
    public Vector2 Project(Vector3 point) {
        var offset = point - Eye;
        var depth = Vector3.Dot(offset, Forward);

        return new(
            ((Vector3.Dot(offset, Right) / (depth * HalfWidth)) + 1f) * 0.5f * Width,
            (1f - (Vector3.Dot(offset, Up) / (depth * HalfHeight))) * 0.5f * Height
        );
    }

    /// <summary>This camera seen in a mirror: the picture of a point here is the picture of its reflection.</summary>
    /// <param name="plane">The mirror.</param>
    /// <returns>The reflected camera.</returns>
    public PaintView Mirrored(PaintSymmetry plane) =>
        this with {
            Eye = plane.Mirror(Eye),
            Right = plane.MirrorDirection(Right),
            Up = plane.MirrorDirection(Up),
            Forward = plane.MirrorDirection(Forward)
        };
}
