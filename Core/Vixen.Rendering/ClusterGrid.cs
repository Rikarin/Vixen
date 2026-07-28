// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>
///     The froxel grid clustered light culling bins into — the host's half of
///     <c>Raven/Library/Pipeline/ClusterCulling.rvn</c>.
/// </summary>
/// <remarks>
///     <para>
///         Every number here is duplicated from the shader, and that is not a smell to be refactored
///         away: they are <c>const</c> there rather than permutations because they size an array
///         <em>inside a struct</em>, and a struct's shape cannot depend on a variant while the host
///         binds one buffer. So both sides have to agree by construction, and a test asserts they do
///         rather than a comment asking.
///     </para>
///     <para>
///         <strong>A grid rather than screen tiles</strong>, which is one extra dimension for one
///         specific problem: a 2D tile holding both a nearby wall and distant sky gets a list long
///         enough for both, and every fragment in it pays. Slicing depth bounds that — a fragment only
///         reads lights that reach its own slab.
///     </para>
///     <para>
///         <strong>The slices are exponential.</strong> A linear split spends most of its slices on
///         distance nothing occupies; <c>near · (far/near)^(k/n)</c> gives every slice the same ratio
///         of depths, which is how perspective actually distributes what a camera sees — and is
///         invertible in closed form, so a fragment finds its slice with a logarithm instead of a
///         search.
///     </para>
/// </remarks>
public static class ClusterGrid {
    /// <summary>Tiles across the screen.</summary>
    public const int TilesX = 16;

    /// <summary>Tiles down it.</summary>
    public const int TilesY = 9;

    /// <summary>Depth slices.</summary>
    public const int Slices = 24;

    /// <summary>
    ///     How many lights one cluster can hold.
    /// </summary>
    /// <remarks>
    ///     The cost of the culling pass having no atomic: a cluster owns a fixed slice of the output
    ///     buffer that only one invocation ever writes, which removes the sharing rather than
    ///     synchronising it. Thirty-two is a budget and not a bound on the scene — a cluster that
    ///     would have held more drops the rest, which shows as lights winking out in the densest part
    ///     of a scene rather than as a crash.
    /// </remarks>
    public const int Capacity = 32;

    /// <summary>How many clusters the grid has.</summary>
    public const int Count = TilesX * TilesY * Slices;

    /// <summary>The workgroup size the culling shader declares.</summary>
    /// <remarks>
    ///     4×4×4, so the three dispatch dimensions map straight onto the grid's and an invocation's
    ///     cluster is its thread id. The grid is not a multiple of four in <c>y</c>, so the tail
    ///     invocations have no cluster and return — which is why <see cref="GroupCount" /> rounds up
    ///     and the shader bounds-tests.
    /// </remarks>
    public static Int3 WorkgroupSize => new(4, 4, 4);

    /// <summary>How many workgroups a full cull dispatches.</summary>
    public static Int3 GroupCount =>
        new(
            (TilesX + WorkgroupSize.X - 1) / WorkgroupSize.X,
            (TilesY + WorkgroupSize.Y - 1) / WorkgroupSize.Y,
            (Slices + WorkgroupSize.Z - 1) / WorkgroupSize.Z
        );

    /// <summary>How many bytes the cluster buffer needs.</summary>
    /// <remarks>
    ///     <c>Count × sizeof(ClusterLights)</c>, and worth looking at: about 445 KB at this grid size
    ///     and capacity. That is the number a project trades against — halving the capacity halves it,
    ///     and doubling the slices doubles it.
    /// </remarks>
    public static long BufferSize => (long)Count * Marshal.SizeOf<ClusterLights>();

    /// <summary>The flat index of a cluster, which is also its slot in the buffer.</summary>
    /// <remarks>
    ///     x fastest, then y, then slice: two fragments in neighbouring tiles at the same depth land
    ///     in adjacent slots, which is the access pattern a fragment wave actually has.
    /// </remarks>
    public static int Index(int x, int y, int slice) => x + (y * TilesX) + (slice * TilesX * TilesY);

    /// <summary>The view-space depth at which a slice begins.</summary>
    public static float SliceDepth(int slice, float near, float far) =>
        near * MathF.Pow(far / near, slice / (float)Slices);

    /// <summary>Which slice a view-space depth falls in — the inverse of <see cref="SliceDepth" />.</summary>
    /// <remarks>
    ///     Clamped rather than left to run off the end, because a fragment can be outside: one exactly
    ///     on the far plane rounds to <see cref="Slices" /> and would read the cluster after the last.
    /// </remarks>
    public static int SliceOf(float viewDepth, float near, float far) {
        var ratio = MathF.Log(MathF.Max(viewDepth, near) / near)
            / MathF.Max(MathF.Log(far / near), 1e-6f);

        return Math.Clamp((int)(ratio * Slices), 0, Slices - 1);
    }

    /// <summary>
    ///     How far in front of the camera a view-space position is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Negated, because the engine's view space is right-handed.</strong>
    ///         <see cref="Matrix4x4.LookAt" /> says it outright — the camera looks down −Z — and
    ///         <see cref="Matrix4x4.PerspectiveFieldOfView" /> agrees, taking <c>w</c> from <c>-z</c>.
    ///         Every depth in this grid is a positive distance, so this is the one place the two
    ///         conventions meet, on both sides: <c>ClusterGrid.DepthOf</c> in the shader is the same
    ///         line.
    ///     </para>
    ///     <para>
    ///         It was missing from both, and the shape of that failure is worth remembering.
    ///         <c>Transform.ViewRay</c> pointed down <em>+Z</em>, so a cluster's box came out mirrored
    ///         in z from the light positions tested against it — nearly nothing intersected, every
    ///         cluster list came back empty, and the clustered path lit a scene by the sun alone. A
    ///         handedness mistake produces an empty result rather than a wrong-looking one.
    ///     </para>
    /// </remarks>
    public static float DepthOf(Vector3 positionView) => -positionView.Z;

    /// <summary>The screen UV a view-space position projects to, as the shader computes it.</summary>
    /// <remarks>
    ///     The inverse of the ray the culling pass builds its corners from, rather than a
    ///     projection-matrix multiply that would compute the same number a second way — two
    ///     derivations of one quantity is how a fragment ends up reading a cluster the culler filled
    ///     for somewhere else.
    /// </remarks>
    public static Vector2 UvOf(Vector3 positionView, Vector2 tanHalfFov) {
        var depth = MathF.Max(DepthOf(positionView), 1e-6f);

        return new(
            (positionView.X / (depth * tanHalfFov.X) * 0.5f) + 0.5f,
            (positionView.Y / (depth * tanHalfFov.Y) * 0.5f) + 0.5f
        );
    }

    /// <summary>The cluster a shaded fragment belongs to, from its view-space position.</summary>
    /// <remarks>
    ///     The host's copy of <c>ClusterGrid.Of</c>, duplicated for the reason the constants above are
    ///     — the two sides have to agree by construction, and a copy that can be evaluated on the CPU
    ///     is what lets a test say they do.
    /// </remarks>
    public static int Of(Vector3 positionView, Vector2 tanHalfFov, float near, float far) {
        var uv = UvOf(positionView, tanHalfFov);
        var x = Math.Clamp((int)(Math.Clamp(uv.X, 0f, 1f) * TilesX), 0, TilesX - 1);
        var y = Math.Clamp((int)(Math.Clamp(uv.Y, 0f, 1f) * TilesY), 0, TilesY - 1);

        return Index(x, y, SliceOf(DepthOf(positionView), near, far));
    }

    /// <summary>
    ///     The view-space box one cluster occupies, as the culling pass builds it.
    /// </summary>
    /// <remarks>
    ///     Eight corners — four tile corners at each of the slab's two depths — reduced to their min
    ///     and max. Larger than the wedge it bounds, which costs a few false positives at the corners
    ///     and never a false negative; a false negative is a light that vanishes.
    /// </remarks>
    public static BoundingBox Bounds(int x, int y, int slice, Vector2 tanHalfFov, float near, float far) {
        var lower = new Vector2((float)x / TilesX, (float)y / TilesY);
        var upper = new Vector2((float)(x + 1) / TilesX, (float)(y + 1) / TilesY);

        var start = SliceDepth(slice, near, far);
        var end = SliceDepth(slice + 1, near, far);

        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);

        foreach (var uv in (Vector2[])[lower, new(upper.X, lower.Y), new(lower.X, upper.Y), upper]) {
            var ray = ViewRay(uv, tanHalfFov);

            foreach (var depth in (float[])[start, end]) {
                minimum = Vector3.Min(minimum, ray * depth);
                maximum = Vector3.Max(maximum, ray * depth);
            }
        }

        return new(minimum, maximum);
    }

    /// <summary>The view-space ray through a point on the screen — <c>Transform.ViewRay</c>.</summary>
    /// <remarks>
    ///     Down −Z, and scaled by a positive distance rather than by a view-space z. That sign is the
    ///     whole of <see cref="DepthOf" />'s remarks, from the other end.
    /// </remarks>
    static Vector3 ViewRay(Vector2 uv, Vector2 tanHalfFov) =>
        new(((uv.X * 2f) - 1f) * tanHalfFov.X, ((uv.Y * 2f) - 1f) * tanHalfFov.Y, -1f);

    /// <summary>
    ///     Writes the camera parameters that place a fragment in the grid, under one shader's names.
    /// </summary>
    /// <param name="parameters">Where the values go.</param>
    /// <param name="camera">The camera the grid is built in.</param>
    /// <param name="shaderName">The pass whose keys to write — the culler, or whatever shades.</param>
    /// <remarks>
    ///     <para>
    ///         <strong>Both passes are filled from here, and that is the entire reason it exists.</strong>
    ///         The culler bins a light into a froxel from these four numbers and a fragment finds its
    ///         own froxel from the same four; give them a different aspect ratio, or a far plane one
    ///         of them rounded, and every fragment reads the list that was filled for somewhere else.
    ///         Nothing about that failure looks like a mismatch — it looks like lights that flicker
    ///         near the edges of the screen.
    ///     </para>
    ///     <para>
    ///         The vertical half-tangent is the camera's field of view and the horizontal one is that
    ///         times the aspect ratio, which is the pair <c>ClusterCulling.UvOf</c> divides by. The
    ///         shader's own defaults — <c>float2(1, 0.5625)</c> — are exactly a 16:9 camera at a
    ///         ninety-degree horizontal field of view, so a host that writes nothing gets a grid for a
    ///         camera it probably does not have.
    ///     </para>
    /// </remarks>
    public static void Apply(ParameterCollection parameters, in RenderCamera camera, string shaderName) {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrEmpty(shaderName);

        var vertical = MathF.Tan(camera.FieldOfView * 0.5f);

        parameters.Set(
            ParameterKeys.New<Vector2>($"{shaderName}.tanHalfFov"),
            new(vertical * camera.AspectRatio, vertical)
        );

        parameters.Set(ParameterKeys.New<float>($"{shaderName}.nearPlane"), camera.NearPlane);
        parameters.Set(ParameterKeys.New<float>($"{shaderName}.farPlane"), camera.FarPlane);
    }
}

/// <summary>One cluster's light list, exactly as <c>ClusterLights</c> in the shader declares it.</summary>
/// <remarks>
///     A count beside the indices rather than a sentinel, because a shading pass wants to leave the
///     loop rather than test every entry — and <c>0</c> is a perfectly good light index, so no value
///     is free to mean "no more". Declared here only to size the buffer: the host never writes one,
///     because the whole point of the pass is that the GPU does.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct ClusterLights {
    /// <summary>How many of <see cref="Indices" /> are live.</summary>
    public uint Count;

    /// <summary>Indices into the scene's light buffer.</summary>
    [System.Runtime.CompilerServices.InlineArray(ClusterGrid.Capacity)]
    public struct IndexArray {
        uint element;
    }

    /// <summary>The lights that reach this cluster.</summary>
    public IndexArray Indices;
}
