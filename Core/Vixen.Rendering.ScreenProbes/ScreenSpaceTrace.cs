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
///         <b>The naive march is the baseline, and the HZB traversal landed against it.</b> A fixed
///         count of equal steps along the ray, each projected and compared — deterministic, so the
///         device kernel is held to it texel by texel. Setting <see cref="Pyramid" /> switches an
///         affine camera's march to the hierarchical DDA over <see cref="ScreenDepthPyramid" /> —
///         the <i>nearest</i> reduction, where <c>HiZReduce</c> keeps the farthest — with the
///         agreement and the fetch counts both asserted rather than claimed. The device kernels
///         still walk naively; their pyramid march is owed with this one as its referee.
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

    /// <summary>The nearest-per-cell pyramid the march skips empty screen with, or null for the
    ///     fixed-step walk.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The HZB traversal, and it changes how fast the answer is found, not where.</b> The
    ///         ray runs as a DDA over the pyramid's cells: a cell whose nearest surface is farther
    ///         than the whole segment crossing it cannot stop the ray and is skipped at whatever
    ///         level that is true, and only cells the ray could actually enter are tested texel by
    ///         texel — continuously, so a shell the fixed steps could straddle cannot be stepped
    ///         over. <see cref="Samples" /> counts the fetches, which is the claim, measured.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exact under an affine camera and declined under a perspective one</b> — a clip
    ///         <c>w</c> off one means depth is not linear along the ray in screen space, the skip
    ///         test would compare the wrong interval, and this falls back to the fixed-step walk
    ///         rather than skip wrongly. The perspective form interpolates <c>1/w</c> and belongs
    ///         with the linear-thickness work the naive march's remarks already name.
    ///     </para>
    /// </remarks>
    public ScreenDepthPyramid? Pyramid { get; set; }

    /// <summary>How many pyramid or depth fetches the last march made.</summary>
    public int Samples { get; private set; }

    /// <summary>Whether a ray from a point is stopped by something the screen can see.</summary>
    /// <param name="origin">Where the ray starts, in world space.</param>
    /// <param name="direction">Where it goes, normalised.</param>
    /// <param name="maxDistance">How far it looks.</param>
    /// <returns>True when a sample lands inside a surface's shell.</returns>
    /// <remarks>
    ///     Samples at the middle of each step, so no sample sits at the origin — where the probe's
    ///     own surface would occlude every tangent ray — and none at the exact far end.
    /// </remarks>
    public bool Hit(Vector3 origin, Vector3 direction, float maxDistance) =>
        TryHit(origin, direction, maxDistance, out _);

    /// <summary>The same march, keeping which pixel stopped the ray.</summary>
    /// <param name="origin">Where the ray starts, in world space.</param>
    /// <param name="direction">Where it goes, normalised.</param>
    /// <param name="maxDistance">How far it looks.</param>
    /// <param name="pixel">The pixel whose surface the ray landed inside.</param>
    /// <returns>True when a sample lands inside a surface's shell.</returns>
    /// <remarks>
    ///     The probes only ever ask <i>whether</i> — a screen hit is an occlusion there, because the
    ///     surface's radiance is the cache's to answer. A reflection asks <i>where</i>, because the
    ///     frame's own colour at that pixel is the whole point of tracing the screen first — SSR's
    ///     half of doc 19 § L5 — and the two questions are one march.
    /// </remarks>
    public bool TryHit(Vector3 origin, Vector3 direction, float maxDistance, out Int2 pixel) {
        Samples = 0;

        if (Pyramid is { } pyramid) {
            var fromClip = Matrix4x4.TransformVector4(new(origin, 1f), ViewProjection);
            var toClip = Matrix4x4.TransformVector4(new(origin + (direction * maxDistance), 1f), ViewProjection);

            // Affine or nothing: with w off one, depth is not linear along the ray in screen space
            // and the skip test would compare the wrong interval — decline rather than skip wrongly.
            if (MathF.Abs(fromClip.W - 1f) < 1e-4f && MathF.Abs(toClip.W - 1f) < 1e-4f) {
                return Hierarchical(pyramid, fromClip, toClip, out pixel);
            }
        }

        var viewport = surface.Viewport;
        var depth = surface.Depth;
        var step = maxDistance / Steps;

        pixel = default;

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

            Samples++;

            var surfaceDepth = depth[(y * viewport.X) + x];

            // A sky texel occludes nothing.
            if (surfaceDepth <= 0f) {
                continue;
            }

            // Behind the surface — smaller device depth, because depth is reversed — and within
            // its shell.
            if (ndc.Z < surfaceDepth && ndc.Z > surfaceDepth - Thickness) {
                pixel = new(x, y);

                return true;
            }
        }

        return false;
    }

    /// <summary>The DDA over the pyramid: skip what cannot stop the ray, test what could.</summary>
    /// <remarks>
    ///     Everything is linear in the ray parameter because the caller proved the camera affine:
    ///     pixel position and device depth both interpolate exactly, so a cell crossing is one
    ///     interval and the skip test is two endpoint compares. The texel test holds the naive
    ///     shell against the <i>segment</i> rather than a sample — continuous, so a shell the fixed
    ///     steps could straddle cannot be stepped over.
    /// </remarks>
    bool Hierarchical(ScreenDepthPyramid pyramid, Vector4 fromClip, Vector4 toClip, out Int2 pixel) {
        pixel = default;

        var viewport = surface.Viewport;

        var from = new Vector3(
            ((fromClip.X * 0.5f) + 0.5f) * viewport.X,
            ((fromClip.Y * 0.5f) + 0.5f) * viewport.Y,
            fromClip.Z
        );

        var to = new Vector3(
            ((toClip.X * 0.5f) + 0.5f) * viewport.X,
            ((toClip.Y * 0.5f) + 0.5f) * viewport.Y,
            toClip.Z
        );

        var delta = to - from;
        var along = 0f;
        var level = pyramid.Levels - 1;

        // A DDA visits O(levels · perimeter) cells; anything past this is a defect, and a budget
        // that ends a march is a miss in the only sense that matters.
        var budget = (16 * (viewport.X + viewport.Y + pyramid.Levels)) + 64;

        while (along < 1f && budget-- > 0) {
            var at = from + (delta * along);
            var px = (int)MathF.Floor(at.X);
            var py = (int)MathF.Floor(at.Y);

            // Off the viewport, the rest of the ray is not the screen's to answer.
            if (px < 0 || py < 0 || px >= viewport.X || py >= viewport.Y) {
                return false;
            }

            var size = 1 << level;
            var exit = ExitOf(from, delta, new((px >> level) << level, (py >> level) << level), size, along);

            Samples++;

            var nearest = pyramid.Nearest(level, new(px >> level, py >> level));
            var enter = MathF.Min(at.Z, from.Z + (delta.Z * exit));
            var leave = MathF.Max(at.Z, from.Z + (delta.Z * exit));

            // The whole crossing stays nearer than the cell's nearest surface — nothing here can
            // stop the ray. Skip it, and try a coarser cell for the next one.
            if (enter > nearest) {
                along = exit;
                level = Math.Min(level + 1, pyramid.Levels - 1);

                continue;
            }

            if (level > 0) {
                level--;

                continue;
            }

            // Texel level: the naive shell, clipped to the range the buffer saw, held against the
            // segment. A sky texel occludes nothing.
            var surfaceDepth = pyramid.Nearest(0, new(px, py));

            if (surfaceDepth > 0f && enter < surfaceDepth && leave > MathF.Max(surfaceDepth - Thickness, 0f)) {
                pixel = new(px, py);

                return true;
            }

            along = exit;
        }

        return false;
    }

    /// <summary>Where the ray leaves one cell's rectangle, in the ray parameter — always forward.</summary>
    static float ExitOf(Vector3 from, Vector3 delta, Int2 origin, int size, float along) {
        var exit = 1f;

        if (delta.X > 1e-6f) {
            exit = MathF.Min(exit, (origin.X + size - from.X) / delta.X);
        } else if (delta.X < -1e-6f) {
            exit = MathF.Min(exit, (origin.X - from.X) / delta.X);
        }

        if (delta.Y > 1e-6f) {
            exit = MathF.Min(exit, (origin.Y + size - from.Y) / delta.Y);
        } else if (delta.Y < -1e-6f) {
            exit = MathF.Min(exit, (origin.Y - from.Y) / delta.Y);
        }

        return MathF.Max(exit, along + 1e-5f);
    }
}
