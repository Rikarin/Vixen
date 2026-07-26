// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     The region of a render target being drawn into, and the depth range mapped onto it. Floats
///     rather than integers, because that is what both Vulkan and D3D12 take.
/// </summary>
/// <remarks>
///     <see cref="MinDepth" /> and <see cref="MaxDepth" /> are the *viewport transform*, which is a
///     separate thing from reverse-Z. Reverse-Z lives in the projection matrix; the viewport range
///     stays 0 to 1 and only changes when something deliberately compresses depth, such as a
///     first-person weapon rendered into a sliver of the buffer.
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Viewport : IEquatable<Viewport>, IFormattable {
    /// <summary>The left edge, in pixels.</summary>
    public readonly float X;

    /// <summary>The top edge, in pixels.</summary>
    public readonly float Y;

    /// <summary>The width, in pixels.</summary>
    public readonly float Width;

    /// <summary>The height, in pixels.</summary>
    public readonly float Height;

    /// <summary>The depth written for the near end of the range.</summary>
    public readonly float MinDepth;

    /// <summary>The depth written for the far end of the range.</summary>
    public readonly float MaxDepth;

    /// <summary>Builds a viewport covering a region, with the full depth range.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public Viewport(float x, float y, float width, float height)
        : this(x, y, width, height, 0f, 1f) { }

    /// <summary>Builds a viewport covering a region, with an explicit depth range.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="minDepth">The depth written for the near end.</param>
    /// <param name="maxDepth">The depth written for the far end.</param>
    public Viewport(float x, float y, float width, float height, float minDepth, float maxDepth) {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        MinDepth = minDepth;
        MaxDepth = maxDepth;
    }

    /// <summary>Builds a viewport covering a rectangle, with the full depth range.</summary>
    /// <param name="bounds">The region.</param>
    public Viewport(Rectangle bounds)
        : this(bounds.X, bounds.Y, bounds.Width, bounds.Height) { }

    /// <summary>The region, as a rectangle.</summary>
    public Rectangle Bounds => new(X, Y, Width, Height);

    /// <summary>Width divided by height, for feeding to a projection.</summary>
    public float AspectRatio => Height == 0f ? 0f : Width / Height;

    /// <summary>
    ///     Where a world-space point lands on screen. The result's Z is the depth that would be
    ///     written, which under reverse-Z is near 1 for close geometry.
    /// </summary>
    /// <param name="point">The world-space point.</param>
    /// <param name="worldViewProjection">World, view and projection combined, in that order.</param>
    /// <returns>The screen position in pixels, with depth in Z.</returns>
    /// <remarks>
    ///     Y is flipped: our projections put +Y up in normalised device coordinates, and screen
    ///     space has its origin at the top-left with Y increasing downward. Making the GPU agree is
    ///     the backend's job — on Vulkan that is a negative viewport height — and this method
    ///     describes the mapping, not the API call.
    /// </remarks>
    public Vector3 Project(Vector3 point, in Matrix4x4 worldViewProjection) {
        var clip = new Vector4(point, 1f) * worldViewProjection;
        var device = MathUtil.IsZero(clip.W) ? clip.Xyz : clip.Xyz / clip.W;

        return new(
            X + ((device.X + 1f) * 0.5f * Width),
            Y + ((1f - device.Y) * 0.5f * Height),
            MinDepth + (device.Z * (MaxDepth - MinDepth))
        );
    }

    /// <summary>
    ///     The world-space point a screen position came from. The inverse of
    ///     <see cref="Project" />, and how a mouse position becomes a picking ray.
    /// </summary>
    /// <param name="point">The screen position in pixels, with depth in Z.</param>
    /// <param name="worldViewProjection">The same transform <see cref="Project" /> was given.</param>
    /// <returns>The world-space point, or <see cref="Vector3.Zero" /> if the transform is singular.</returns>
    public Vector3 Unproject(Vector3 point, in Matrix4x4 worldViewProjection) {
        if (!Matrix4x4.Invert(worldViewProjection, out var inverse)) {
            return Vector3.Zero;
        }

        var range = MaxDepth - MinDepth;
        var device = new Vector3(
            (((point.X - X) / Width) * 2f) - 1f,
            1f - (((point.Y - Y) / Height) * 2f),
            MathUtil.IsZero(range) ? 0f : (point.Z - MinDepth) / range
        );

        var world = new Vector4(device, 1f) * inverse;
        return MathUtil.IsZero(world.W) ? world.Xyz : world.Xyz / world.W;
    }

    /// <summary>
    ///     The ray through a screen position, pointing away from the camera. What a click becomes.
    /// </summary>
    /// <param name="screenPosition">The screen position in pixels.</param>
    /// <param name="viewProjection">The view and projection combined.</param>
    /// <returns>The picking ray in world space.</returns>
    /// <remarks>
    ///     Built from the near and far points rather than from the camera's position and a direction,
    ///     so it is correct for an orthographic projection too — where every pixel's ray starts
    ///     somewhere different and they are all parallel.
    /// </remarks>
    public Ray GetPickingRay(Vector2 screenPosition, in Matrix4x4 viewProjection) {
        // Reverse-Z: the near plane is at depth 1 and the far plane at 0, so the ray runs from the
        // MaxDepth end toward the MinDepth end.
        var near = Unproject(new(screenPosition, MaxDepth), viewProjection);
        var far = Unproject(new(screenPosition, MinDepth), viewProjection);
        return new(near, far - near);
    }

    /// <summary>Whether two viewports agree to within a tolerance.</summary>
    /// <param name="left">The first viewport.</param>
    /// <param name="right">The second viewport.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if every field is within tolerance.</returns>
    public static bool NearEqual(Viewport left, Viewport right, float tolerance = MathUtil.ZeroTolerance) =>
        Rectangle.NearEqual(left.Bounds, right.Bounds, tolerance)
        && MathUtil.NearEqual(left.MinDepth, right.MinDepth, tolerance)
        && MathUtil.NearEqual(left.MaxDepth, right.MaxDepth, tolerance);

    /// <summary>Exact equality, IEEE semantics. See <see cref="NearEqual" />.</summary>
    /// <param name="left">The first viewport.</param>
    /// <param name="right">The second viewport.</param>
    /// <returns><see langword="true" /> if every field is equal.</returns>
    public static bool operator ==(Viewport left, Viewport right) =>
        left.X == right.X && left.Y == right.Y && left.Width == right.Width && left.Height == right.Height
        && left.MinDepth == right.MinDepth && left.MaxDepth == right.MaxDepth;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first viewport.</param>
    /// <param name="right">The second viewport.</param>
    /// <returns><see langword="true" /> if anything differs.</returns>
    public static bool operator !=(Viewport left, Viewport right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(Viewport other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Viewport other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height, MinDepth, MaxDepth);

    /// <inheritdoc />
    public override string ToString() => ToString(null, null);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) {
        formatProvider ??= VectorFormat.DefaultProvider;
        return
            $"{{{Bounds.ToString(format, formatProvider)} Depth:{MinDepth.ToString(format, formatProvider)}..{MaxDepth.ToString(format, formatProvider)}}}";
    }
}
