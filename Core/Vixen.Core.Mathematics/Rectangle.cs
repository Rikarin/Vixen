// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     An axis-aligned rectangle in 2D: layout boxes, scissor regions, texture atlas entries,
///     clipping bounds.
/// </summary>
/// <remarks>
///     Stored as position plus size rather than two corners, because that is how layout and UI code
///     thinks and it is the form that keeps <see cref="Width" /> exact under translation. The origin
///     is the <b>top-left</b> and Y increases downward, matching the UV convention.
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Rectangle : IEquatable<Rectangle>, IFormattable {
    /// <summary>The left edge.</summary>
    public readonly float X;

    /// <summary>The top edge.</summary>
    public readonly float Y;

    /// <summary>The width. Negative widths are not meaningful and are not checked for.</summary>
    public readonly float Width;

    /// <summary>The height.</summary>
    public readonly float Height;

    /// <summary>The rectangle at the origin with no size.</summary>
    public static Rectangle Empty => default;

    /// <summary>Builds a rectangle from its position and size.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public Rectangle(float x, float y, float width, float height) {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>Builds a rectangle from its position and size.</summary>
    /// <param name="position">The top-left corner.</param>
    /// <param name="size">The width and height.</param>
    public Rectangle(Vector2 position, Vector2 size)
        : this(position.X, position.Y, size.X, size.Y) { }

    /// <summary>The rectangle spanning two corners, in either order.</summary>
    /// <param name="first">One corner.</param>
    /// <param name="second">The opposite corner.</param>
    /// <returns>The rectangle.</returns>
    public static Rectangle FromCorners(Vector2 first, Vector2 second) {
        var minimum = Vector2.Min(first, second);
        return new(minimum, Vector2.Max(first, second) - minimum);
    }

    /// <summary>The left edge.</summary>
    public float Left => X;

    /// <summary>The top edge.</summary>
    public float Top => Y;

    /// <summary>The right edge.</summary>
    public float Right => X + Width;

    /// <summary>The bottom edge.</summary>
    public float Bottom => Y + Height;

    /// <summary>The top-left corner.</summary>
    public Vector2 Position => new(X, Y);

    /// <summary>The width and height.</summary>
    public Vector2 Size => new(Width, Height);

    /// <summary>The midpoint.</summary>
    public Vector2 Center => new(X + (Width * 0.5f), Y + (Height * 0.5f));

    /// <summary>Whether the rectangle encloses nothing.</summary>
    public bool IsEmpty => Width <= 0f || Height <= 0f;

    /// <summary>Whether a point is inside or on the top-left edges.</summary>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true" /> if the point is within the rectangle.</returns>
    /// <remarks>
    ///     Half-open: the top and left edges are inside, the bottom and right are not. That is what
    ///     makes adjacent rectangles tile without a seam and without double-counting a hit.
    /// </remarks>
    public bool Contains(Vector2 point) =>
        point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;

    /// <summary>Whether another rectangle is entirely inside this one.</summary>
    /// <param name="other">The rectangle to test.</param>
    /// <returns><see langword="true" /> if it is contained.</returns>
    public bool Contains(Rectangle other) =>
        other.X >= X && other.Right <= Right && other.Y >= Y && other.Bottom <= Bottom;

    /// <summary>Whether two rectangles overlap.</summary>
    /// <param name="other">The rectangle to test.</param>
    /// <returns><see langword="true" /> if they overlap in both axes.</returns>
    public bool Intersects(Rectangle other) =>
        other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;

    /// <summary>The overlap of two rectangles.</summary>
    /// <param name="left">The first rectangle.</param>
    /// <param name="right">The second rectangle.</param>
    /// <returns>The overlap, or <see cref="Empty" /> if there is none.</returns>
    public static Rectangle Intersect(Rectangle left, Rectangle right) {
        var x = MathF.Max(left.X, right.X);
        var y = MathF.Max(left.Y, right.Y);
        var width = MathF.Min(left.Right, right.Right) - x;
        var height = MathF.Min(left.Bottom, right.Bottom) - y;

        return width <= 0f || height <= 0f ? Empty : new(x, y, width, height);
    }

    /// <summary>The smallest rectangle containing both.</summary>
    /// <param name="left">The first rectangle.</param>
    /// <param name="right">The second rectangle.</param>
    /// <returns>The union bound.</returns>
    public static Rectangle Union(Rectangle left, Rectangle right) {
        if (left.IsEmpty) {
            return right;
        }

        if (right.IsEmpty) {
            return left;
        }

        var x = MathF.Min(left.X, right.X);
        var y = MathF.Min(left.Y, right.Y);
        return new(x, y, MathF.Max(left.Right, right.Right) - x, MathF.Max(left.Bottom, right.Bottom) - y);
    }

    /// <summary>Grows the rectangle on every side.</summary>
    /// <param name="rectangle">The rectangle.</param>
    /// <param name="horizontal">How much to add to the left and right.</param>
    /// <param name="vertical">How much to add to the top and bottom.</param>
    /// <returns>The inflated rectangle. Negative amounts shrink it.</returns>
    public static Rectangle Inflate(Rectangle rectangle, float horizontal, float vertical) =>
        new(
            rectangle.X - horizontal,
            rectangle.Y - vertical,
            rectangle.Width + (horizontal * 2f),
            rectangle.Height + (vertical * 2f)
        );

    /// <summary>Moves the rectangle.</summary>
    /// <param name="rectangle">The rectangle.</param>
    /// <param name="offset">How far to move it.</param>
    /// <returns>The moved rectangle.</returns>
    public static Rectangle Offset(Rectangle rectangle, Vector2 offset) =>
        new(rectangle.Position + offset, rectangle.Size);

    /// <summary>Whether two rectangles agree to within a tolerance.</summary>
    /// <param name="left">The first rectangle.</param>
    /// <param name="right">The second rectangle.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if position and size are within tolerance.</returns>
    public static bool NearEqual(Rectangle left, Rectangle right, float tolerance = MathUtil.ZeroTolerance) =>
        Vector2.NearEqual(left.Position, right.Position, tolerance)
        && Vector2.NearEqual(left.Size, right.Size, tolerance);

    /// <summary>Exact equality, IEEE semantics. See <see cref="NearEqual" />.</summary>
    /// <param name="left">The first rectangle.</param>
    /// <param name="right">The second rectangle.</param>
    /// <returns><see langword="true" /> if position and size are equal.</returns>
    public static bool operator ==(Rectangle left, Rectangle right) =>
        left.X == right.X && left.Y == right.Y && left.Width == right.Width && left.Height == right.Height;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first rectangle.</param>
    /// <param name="right">The second rectangle.</param>
    /// <returns><see langword="true" /> if anything differs.</returns>
    public static bool operator !=(Rectangle left, Rectangle right) => !(left == right);

    /// <summary>Splits the rectangle into its components.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void Deconstruct(out float x, out float y, out float width, out float height) {
        x = X;
        y = Y;
        width = Width;
        height = Height;
    }

    /// <inheritdoc />
    public bool Equals(Rectangle other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Rectangle other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    /// <inheritdoc />
    public override string ToString() => ToString(null, null);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) {
        formatProvider ??= VectorFormat.DefaultProvider;
        return
            $"{{X:{X.ToString(format, formatProvider)} Y:{Y.ToString(format, formatProvider)} W:{Width.ToString(format, formatProvider)} H:{Height.ToString(format, formatProvider)}}}";
    }
}
