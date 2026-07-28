// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Xr;

/// <summary>The projection matrix a headset's frustum asks for.</summary>
/// <remarks>
///     <para>
///         <b>Why this is not <see cref="Matrix4x4.PerspectiveFieldOfView" />.</b> That takes one
///         vertical field of view and an aspect ratio, which describes a frustum symmetric about the
///         view axis. A headset's is not: the lenses are canted outwards, so each eye sees several
///         degrees further towards its own side than towards the nose. Projecting with a symmetric
///         matrix puts the two eyes' images at slightly different places, which the visual system
///         reads as the world being the wrong size and the viewer reads as a headache.
///     </para>
///     <para>
///         <b>It is the same convention as the rest of the engine.</b> Right-handed, row-vector, and
///         reverse-Z — the near plane maps to 1 and the far to 0, the depth test is <c>GREATER</c>
///         and depth clears to 0. Feeding a headset a matrix from some other engine's convention is
///         the second most common way to get a black eye buffer; the first is forgetting that the
///         runtime owns the swapchain.
///     </para>
/// </remarks>
public static class XrProjection {
    /// <summary>Builds a projection from four half-angles.</summary>
    /// <param name="fov">The frustum's angles, in radians.</param>
    /// <param name="nearPlane">The near plane's distance in metres. Positive.</param>
    /// <param name="farPlane">
    ///     The far plane's distance, or <c>0</c> for an infinite one. Infinite is the better default
    ///     under reverse-Z — it costs nothing in precision and is one fewer number to tune — and it is
    ///     what a runtime that reports no far plane wants.
    /// </param>
    /// <returns>The projection.</returns>
    /// <exception cref="ArgumentException">The frustum has no volume.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The near plane is not positive, or the far plane is nearer.</exception>
    public static Matrix4x4 FromFieldOfView(in XrFieldOfView fov, float nearPlane = 0.05f, float farPlane = 0f) {
        if (!fov.IsValid) {
            throw new ArgumentException(
                $"A frustum from {fov.AngleLeft} to {fov.AngleRight} and {fov.AngleDown} to "
                + $"{fov.AngleUp} radians encloses nothing.",
                nameof(fov)
            );
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nearPlane);

        if (farPlane != 0f) {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(farPlane, nearPlane);
        }

        var left = MathF.Tan(fov.AngleLeft);
        var right = MathF.Tan(fov.AngleRight);
        var up = MathF.Tan(fov.AngleUp);
        var down = MathF.Tan(fov.AngleDown);

        var width = right - left;
        var height = up - down;

        // The two off-centre terms sit in the third row rather than the fourth, because this engine
        // multiplies row vectors: clip.x is x·M11 + z·M31, so the shear that recentres an asymmetric
        // frustum is a function of z. Putting it in the fourth row — which is what a column-vector
        // derivation gives — produces a projection that is wrong by a constant rather than by a
        // perspective, and it looks almost right at one distance.
        var horizontalOffset = (right + left) / width;
        var verticalOffset = (up + down) / height;

        if (farPlane == 0f) {
            return new Matrix4x4(
                2f / width, 0f, 0f, 0f,
                0f, 2f / height, 0f, 0f,
                horizontalOffset, verticalOffset, 0f, -1f,
                0f, 0f, nearPlane, 0f
            );
        }

        var range = nearPlane / (farPlane - nearPlane);

        return new Matrix4x4(
            2f / width, 0f, 0f, 0f,
            0f, 2f / height, 0f, 0f,
            horizontalOffset, verticalOffset, range, -1f,
            0f, 0f, farPlane * range, 0f
        );
    }
}
