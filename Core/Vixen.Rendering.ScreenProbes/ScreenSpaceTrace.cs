// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.ScreenProbes;

/// <summary>A ray against the frame's own depth buffer — doc 19 § L3's trace order, first stage.</summary>
/// <remarks>
///     <para>
///         <b>What a screen trace is for.</b> The depth buffer holds geometry the distance field may
///         not — skinned meshes, alpha-tested foliage, anything too small or too mobile for a signed
///         distance representation — so the trace order asks the screen first and marches the field
///         only where the screen has no answer. A hit gives back <i>nothing</i>, exactly as a field
///         hit does: a surface's own radiance is the § L4 surface cache, and until it exists a screen
///         hit is an occlusion, honest rather than convenient.
///     </para>
///     <para>
///         <b>Deliberately the naive march, and the HZB traversal is owed with this as its
///         baseline.</b> A fixed count of equal steps along the ray, each projected and compared —
///         deterministic, so the device kernel can be held to it texel by texel. The hierarchical
///         march that skips empty space through the depth pyramid changes how fast the answer is
///         found, not what the answer is, and it lands against frames this version defines. (It also
///         wants the pyramid's <i>other</i> reduction: <c>HiZReduce</c> keeps the farthest texel per
///         cell for occlusion culling, and empty-space skipping wants the nearest.)
///     </para>
///     <para>
///         <b>A sample is occluded when it stands behind a surface, within its thickness.</b> Depth
///         is reversed, so behind is a <i>smaller</i> device depth; the shell is
///         <see cref="Thickness" /> deep in device-depth units — the first form, exact under an
///         orthographic camera and a stated approximation under a perspective one, where a linear
///         thickness is owed with the pyramid. A sky texel occludes nothing, and a ray that leaves
///         the viewport stops being the screen's to answer — the caller's field march continues
///         regardless, because a screen miss never proves the world empty.
///     </para>
/// </remarks>
public sealed class ScreenSpaceTrace {
    /// <summary>The clip-divide guard — the Raven library's <c>Const.Epsilon</c>, by value.</summary>
    const float Epsilon = 0.0001f;

    readonly ReconstructedScreenSurface surface;

    /// <summary>Builds a trace over one frame's buffers.</summary>
    /// <param name="surface">The snapshot whose depth is marched — placement's own.</param>
    /// <exception cref="ArgumentNullException">There is no surface.</exception>
    public ScreenSpaceTrace(ReconstructedScreenSurface surface) {
        ArgumentNullException.ThrowIfNull(surface);

        this.surface = surface;
    }

    /// <summary>The view-projection of the camera that drew the depth being marched.</summary>
    /// <remarks>
    ///     The forward matrix, not the inverse the surface reconstructs with — the host has both and
    ///     inverting an inverse would manufacture error. ⚠ The two must be one camera: this against a
    ///     different frame's depth tests rays against surfaces that exist nowhere.
    /// </remarks>
    public Matrix4x4 ViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>How many equal steps a ray takes over its distance.</summary>
    public int Steps { get; set; } = 32;

    /// <summary>How deep behind a surface a sample still counts as inside it, in device depth.</summary>
    /// <remarks>
    ///     What keeps a ray from slipping through a wall the buffer only sees the front of — and what
    ///     lets one pass <i>behind</i> a nearby object instead of being occluded by everything in
    ///     front of the far plane.
    /// </remarks>
    public float Thickness { get; set; } = 0.02f;

    /// <summary>Whether a ray from a point is stopped by something the screen can see.</summary>
    /// <param name="origin">Where the ray starts, in world space.</param>
    /// <param name="direction">Where it goes, normalised.</param>
    /// <param name="maxDistance">How far it looks.</param>
    /// <returns>True when a sample lands inside a surface's shell.</returns>
    /// <remarks>
    ///     Samples at the middle of each step, so no sample sits at the origin — where the probe's
    ///     own surface would occlude every tangent ray — and none at the exact far end.
    /// </remarks>
    public bool Hit(Vector3 origin, Vector3 direction, float maxDistance) {
        var viewport = surface.Viewport;
        var depth = surface.Depth;
        var step = maxDistance / Steps;

        for (var i = 0; i < Steps; i++) {
            var world = origin + (direction * ((i + 0.5f) * step));
            var clip = Matrix4x4.TransformVector4(new(world, 1f), ViewProjection);

            // Behind the camera's plane there is no pixel to ask.
            if (clip.W <= Epsilon) {
                continue;
            }

            var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;

            // Outside the depth range the buffer never saw it — nearer than near or beyond far.
            if (ndc.Z <= 0f || ndc.Z >= 1f) {
                continue;
            }

            var x = (int)MathF.Floor(((ndc.X * 0.5f) + 0.5f) * viewport.X);
            var y = (int)MathF.Floor(((ndc.Y * 0.5f) + 0.5f) * viewport.Y);

            // Off the viewport, the rest of the ray is not the screen's to answer.
            if (x < 0 || y < 0 || x >= viewport.X || y >= viewport.Y) {
                return false;
            }

            var surfaceDepth = depth[(y * viewport.X) + x];

            // A sky texel occludes nothing.
            if (surfaceDepth <= 0f) {
                continue;
            }

            // Behind the surface — smaller device depth, because depth is reversed — and within
            // its shell.
            if (ndc.Z < surfaceDepth && ndc.Z > surfaceDepth - Thickness) {
                return true;
            }
        }

        return false;
    }
}
