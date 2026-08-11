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

    /// <summary>The sub-pixel offset to take a frame's sample at, in pixels, centred on zero.</summary>
    /// <param name="frameIndex">Which term of the sequence. Wrapped by the caller, if it wraps.</param>
    /// <returns>A point in <c>[−0.5, 0.5]²</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         The Halton (2, 3) sequence: the standard choice, because it fills the pixel more
    ///         evenly than random offsets at every prefix length — which is what matters when the
    ///         camera stops after eight frames rather than after a thousand.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One sequence, here, and <c>TemporalAntialiasingRenderer.Jitter</c> is a call to
    ///         it.</b> Two copies of a Halton would be two copies that agree until somebody changes
    ///         one, and what would then disagree is the offset the camera took its sample at and the
    ///         offset the pass believes it took — which is a resolve that reads its history from the
    ///         wrong place and looks exactly like a resolve that is merely soft.
    ///     </para>
    /// </remarks>
    public static Vector2 SubpixelJitter(int frameIndex) =>
        new(Halton(frameIndex + 1, 2) - 0.5f, Halton(frameIndex + 1, 3) - 0.5f);

    /// <summary>Van der Corput's radical inverse in a given base — one term of a Halton sequence.</summary>
    static float Halton(int index, int radix) {
        var result = 0f;
        var fraction = 1f / radix;

        while (index > 0) {
            result += (index % radix) * fraction;
            index /= radix;
            fraction /= radix;
        }

        return result;
    }

    /// <summary>Offsets a projection by a sub-pixel amount, in clip space.</summary>
    /// <param name="matrix">A projection, or a view-projection — see the remarks; both work.</param>
    /// <param name="jitter">
    ///     The offset in normalised device coordinates, which is <c>2 × pixels / size</c>. Zero
    ///     returns the matrix unchanged.
    /// </param>
    /// <returns>The offset matrix.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>What temporal antialiasing needs from the camera.</b> The resolve averages samples
    ///         taken at different points inside the pixel; taking them is the projection's job, and a
    ///         TAA pass fed an unjittered camera averages the same sample over and over — a frame that
    ///         gets blurrier and no sharper. <c>TemporalAntialiasingRenderer.Jitter</c> is the sequence
    ///         and this is where it is applied.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Column one gains the jitter times column four, and the shape of that is
    ///         load-bearing.</b> The engine's matrices are row-vector, so a clip position is
    ///         <c>v × M</c> and the perspective divide is by column four; adding <c>j × column4</c> to
    ///         column one therefore moves the result by exactly <c>j</c> in NDC at every depth, which
    ///         is what a sub-pixel offset has to mean. On a bare projection that reduces to
    ///         <c>M31 -= j.X</c>, and it is tempting to write only that — but on a
    ///         <em>view</em>-projection column four is not <c>(0, 0, −1, 0)</c> and the shortcut
    ///         silently shears the frame instead of shifting it.
    ///     </para>
    ///     <para>
    ///         Which means jittering the projection and then multiplying by the view gives the same
    ///         matrix as jittering the product, and both callers here rely on that: the view's matrix
    ///         is built from the transform's inverse while <c>RenderCamera.Projection</c> is built
    ///         from the field of view, and a screen-space pass that inverts one to unproject a depth
    ///         buffer drawn with the other has to find the same offset in both.
    ///     </para>
    /// </remarks>
    public static Matrix4x4 Jittered(in Matrix4x4 matrix, Vector2 jitter) {
        if (jitter == Vector2.Zero) {
            return matrix;
        }

        return new(
            matrix.M11 + (jitter.X * matrix.M14), matrix.M12 + (jitter.Y * matrix.M14), matrix.M13, matrix.M14,
            matrix.M21 + (jitter.X * matrix.M24), matrix.M22 + (jitter.Y * matrix.M24), matrix.M23, matrix.M24,
            matrix.M31 + (jitter.X * matrix.M34), matrix.M32 + (jitter.Y * matrix.M34), matrix.M33, matrix.M34,
            matrix.M41 + (jitter.X * matrix.M44), matrix.M42 + (jitter.Y * matrix.M44), matrix.M43, matrix.M44
        );
    }

    /// <summary>The volume the camera can see, for culling.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="transform">Its entity's world transform.</param>
    /// <param name="aspectRatio">Width over height, used when the camera's own is zero.</param>
    /// <returns>The frustum in world space.</returns>
    public static BoundingFrustum Frustum(in Camera camera, in WorldTransform transform, float aspectRatio = 0f) =>
        new(ViewProjection(in camera, in transform, aspectRatio));
}
