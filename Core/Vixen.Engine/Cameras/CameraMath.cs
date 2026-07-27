// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Transforms;

namespace Vixen.Engine.Cameras;

/// <summary>The matrices a <see cref="Camera" /> and its transform produce.</summary>
/// <remarks>
///     Free functions rather than properties on the component, because a component is data and these
///     are derived every frame from two components at once. Keeping them out of the struct is also
///     what stops anyone caching a stale one on the entity.
/// </remarks>
public static class CameraMath {
    /// <summary>The world-to-view matrix: the inverse of where the camera is.</summary>
    /// <param name="transform">The camera entity's world transform.</param>
    /// <returns>The view matrix, or the identity if the transform is singular.</returns>
    public static Matrix4x4 View(in WorldTransform transform) =>
        Matrix4x4.Invert(transform.Value, out var inverse) ? inverse : Matrix4x4.Identity;

    /// <summary>The view-to-clip matrix.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="aspectRatio">Width over height, used when the camera's own is zero.</param>
    /// <returns>The projection, reverse-Z in both modes.</returns>
    /// <remarks>
    ///     Reverse-Z throughout, because that is what the rest of the engine is built for: an
    ///     attachment clears to 0 and the depth test is <c>GREATER</c>
    ///     ([05](../../../docs/plan/05-graphics-rhi.md)). A projection that disagreed would render a
    ///     picture that is correct except that everything is behind everything else.
    /// </remarks>
    public static Matrix4x4 Projection(in Camera camera, float aspectRatio = 0f) {
        var aspect = camera.AspectRatio > 0f ? camera.AspectRatio : aspectRatio;

        if (aspect <= 0f) {
            throw new ArgumentOutOfRangeException(
                nameof(aspectRatio),
                "The camera's aspect ratio is zero — meaning 'ask the target' — and no target ratio "
                + "was given. One of the two has to say."
            );
        }

        return camera.Orthographic
            ? Matrix4x4.Orthographic(
                camera.OrthographicHeight * aspect,
                camera.OrthographicHeight,
                camera.NearPlane,
                camera.FarPlane
            )
            : Matrix4x4.PerspectiveFieldOfView(camera.FieldOfView, aspect, camera.NearPlane, camera.FarPlane);
    }

    /// <summary>The world-to-clip matrix.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="transform">Its entity's world transform.</param>
    /// <param name="aspectRatio">Width over height, used when the camera's own is zero.</param>
    /// <returns>View times projection, in that order.</returns>
    public static Matrix4x4 ViewProjection(in Camera camera, in WorldTransform transform, float aspectRatio = 0f) =>
        View(in transform) * Projection(in camera, aspectRatio);

    /// <summary>The volume the camera can see, for culling.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="transform">Its entity's world transform.</param>
    /// <param name="aspectRatio">Width over height, used when the camera's own is zero.</param>
    /// <returns>The frustum in world space.</returns>
    public static BoundingFrustum Frustum(in Camera camera, in WorldTransform transform, float aspectRatio = 0f) =>
        new(ViewProjection(in camera, in transform, aspectRatio));
}
