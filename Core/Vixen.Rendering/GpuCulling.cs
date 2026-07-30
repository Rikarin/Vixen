// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering;

/// <summary>
///     Which of a two-phase cull's two dispatches a set of records is for.
/// </summary>
/// <remarks>
///     <para>
///         One-phase culling is <see cref="Main" /> alone, and it is correct: the depth it tests
///         against is a frame old, so an object that was hidden last frame and is not now is drawn one
///         frame late. That is a popping artefact at the trailing edge of anything the camera moves
///         past, and no amount of conservatism in the test removes it — the information simply was not
///         in the frame.
///     </para>
///     <para>
///         Two-phase adds <see cref="Late" />: draw what <see cref="Main" /> found, reduce
///         <em>that</em> depth back into the pyramid, and ask again about everything main rejected.
///         The second answer is tested against this frame's own depth, so it is late by nothing. What
///         it costs is a second reduction, a second dispatch and a second set of draws — which is why
///         it is a shape a document chooses rather than the only one on offer.
///     </para>
/// </remarks>
public enum CullPhase {
    /// <summary>The pass that runs before anything is drawn, against last frame's depth.</summary>
    Main,

    /// <summary>
    ///     The pass that runs after the main pass's draws, against the depth they left behind.
    /// </summary>
    /// <remarks>
    ///     Its answer is the <em>difference</em> — what is visible now and was not drawn by the main
    ///     pass — because that is what a second set of draws must be given. The union would draw every
    ///     visible object twice.
    /// </remarks>
    Late
}

/// <summary>
///     The records, the dispatch shape and the test that <c>Raven/Library/Pipeline/Culling.rvn</c>
///     and the host have to agree about.
/// </summary>
/// <remarks>
///     <para>
///         Everything here is duplicated from the shader, and — as with <see cref="ClusterGrid" /> —
///         that is not a smell to be refactored away. The host packs bytes the shader reads as
///         structs, so the two sides agree by construction or not at all, and a copy that can be
///         evaluated on the CPU is what lets a test say they do without a GPU in the room.
///     </para>
///     <para>
///         <strong><see cref="IsVisible" /> is the mirror, and it is the interesting one.</strong> It
///         is a transliteration of the shader's <c>Visible</c>, so a randomised comparison against
///         <see cref="VisibilityGroup" /> — which is the definition — says whether the arithmetic the
///         device will run is the arithmetic the engine means. What it cannot say is whether the
///         shader still contains that arithmetic; that is what the source assertion in
///         <c>GpuVisibilityGroupTests</c> is for.
///     </para>
/// </remarks>
public static class GpuCulling {
    /// <summary>The shader's name, and the effect key the group resolves.</summary>
    /// <remarks>
    ///     From the generated keys rather than spelled here, which is the point of them: the name,
    ///     the permutation and every binding index come from the reflection checked in beside the
    ///     shader, so a rename in the <c>.rvn</c> is a compile error rather than a group that quietly
    ///     stops finding its bindings and culls on the CPU for ever.
    /// </remarks>
    public const string ShaderName = CullingKeys.ShaderName;

    /// <summary>How many objects one word of the device's answer holds.</summary>
    /// <remarks>
    ///     Thirty-two where the host's bitset packs sixty-four, because a 64-bit integer is optional
    ///     on Vulkan and absent from WebGPU. The pairing is free — two device words side by side are
    ///     one host word, which is all <see cref="Unpack" /> does.
    /// </remarks>
    public const int WordSize = 32;

    /// <summary>How many words one invocation owns, and therefore how many it writes.</summary>
    /// <remarks>
    ///     One, and it matters for the same reason the CPU path's batch size is a multiple of 64: an
    ///     invocation that owned part of a word would need an atomic <c>or</c> to combine with the
    ///     invocations owning the rest of it.
    /// </remarks>
    public const int WordsPerInvocation = 1;

    /// <summary>The workgroup size the culling shader declares.</summary>
    public const int WorkgroupSize = 64;

    /// <summary>The bit of <see cref="CullObject.Flags" /> that says the slot holds a live object.</summary>
    public const uint Alive = 1u;

    /// <summary>The bit of <see cref="CullView.Flags" /> that says the view can be occlusion tested.</summary>
    /// <remarks>
    ///     Per view, because a view that is new this frame has no previous depth and no previous
    ///     matrix — and testing it against another view's would occlude by arithmetic that was never
    ///     about it.
    /// </remarks>
    public const uint Occluders = 1u;

    /// <summary>What a clip-space <c>w</c> has to exceed to be in front of the camera at all.</summary>
    /// <remarks>
    ///     <c>Const.Epsilon</c> in the shader, and the same number here. Anything at or behind it
    ///     projects to a rectangle that wraps around the screen rather than growing, so the object is
    ///     kept rather than tested.
    /// </remarks>
    public const float ClipEpsilon = 0.0001f;

    /// <summary>The name of the shader that builds the depth pyramid.</summary>
    public const string ReduceShaderName = HiZReduceKeys.ShaderName;

    /// <summary>The name of the shader that turns the bits into indirect draw arguments.</summary>
    public const string ArgumentsShaderName = DrawArgumentsKeys.ShaderName;

    /// <summary>The permutation key selecting the variant that tests against that pyramid.</summary>
    public static string OcclusionKey => CullingKeys.Occlusion.Name;

    /// <summary>The permutation key selecting the second pass of a two-phase cull.</summary>
    /// <remarks>
    ///     Meaningful only together with <see cref="OcclusionKey" />: without a pyramid there is
    ///     nothing a second test could learn that the first did not, so the two are asked for
    ///     together — see <see cref="CullPhase" />.
    /// </remarks>
    public static string LateKey => CullingKeys.Late.Name;

    /// <summary>The workgroup size the reduction shader declares, in each of its two dimensions.</summary>
    public const int ReduceWorkgroupSize = 8;

    /// <summary>One level of a depth pyramid, as a texel of it.</summary>
    /// <param name="x">The texel's column.</param>
    /// <param name="y">Its row.</param>
    /// <param name="level">Which level it is in.</param>
    /// <returns>The furthest depth in that texel's footprint.</returns>
    /// <remarks>
    ///     What <see cref="IsOccluded(in CullObject, in CullView, Int2, OccluderDepth)" /> reads the pyramid through, so that the mirror of the shader
    ///     can be evaluated against a pyramid a test built rather than against a device.
    /// </remarks>
    public delegate float OccluderDepth(int x, int y, int level);

    /// <summary>
    ///     Whether a device can run any of this at all.
    /// </summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         One question, asked in three places, because the answer is the same for all of them:
    ///         the culling dispatch, the depth pyramid and the argument pass are compute shaders, and
    ///         <see cref="GraphicsDeviceFeatures.HasCompute" /> is false on WebGL2 and on a GL target
    ///         old enough to matter. Doc 06's "where capabilities allow" is this line.
    ///     </para>
    ///     <para>
    ///         <strong>Asked rather than found out.</strong> Creating a compute pipeline on a device
    ///         with no compute throws — the RHI says so in the exception's own text, "ask
    ///         <c>Features.HasCompute</c> and take the fallback path" — and an exception is not a
    ///         fallback. It escapes <see cref="GpuVisibilityGroup.Cull" /> to a caller that has no
    ///         answer to give the frame, on precisely the target the CPU path was kept for.
    ///     </para>
    /// </remarks>
    public static bool IsSupported(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);
        return device.Features.HasCompute;
    }

    /// <summary>
    ///     The effect key for one phase of the cull.
    /// </summary>
    /// <param name="occlusion">Whether this frame has a pyramid to test against.</param>
    /// <param name="phase">Which dispatch it is for.</param>
    /// <remarks>
    ///     <para>
    ///         Every permutation is named, including the ones that are off, rather than left to take
    ///         its default. A bundle records what a run asked for, and "Culling with no permutations"
    ///         would be a fourth variant nobody wants baked next to the three that are used — so the
    ///         two frames either side of a pyramid appearing ask for keys that differ in their
    ///         <em>value</em> rather than in their shape.
    ///     </para>
    ///     <para>
    ///         <strong><see cref="CullPhase.Late" /> without <paramref name="occlusion" /> is a real
    ///         variant</strong>, and it is the one a two-phase frame runs before it has ever built a
    ///         pyramid. It re-runs the frustum test, subtracts what the main pass already drew, and
    ///         reaches an empty difference — which is the answer, and is not the same thing as not
    ///         dispatching. Skipping it would leave the main pass's bits in the argument buffer for
    ///         the late draws to find, and every visible object would be drawn twice.
    ///     </para>
    /// </remarks>
    public static EffectKey Key(bool occlusion, CullPhase phase = CullPhase.Main) =>
        EffectKey.Of(
            ShaderName,
            [
                new(OcclusionKey, occlusion ? "true" : "false"),
                new(LateKey, phase == CullPhase.Late ? "true" : "false")
            ]
        );

    /// <summary>How many device words a store of this many objects needs.</summary>
    public static int WordsFor(int objectCount) => objectCount <= 0 ? 0 : ((objectCount + WordSize - 1) / WordSize);

    /// <summary>How many workgroups cover one view's words.</summary>
    public static int Groups(int wordCount) =>
        wordCount <= 0 ? 0 : ((wordCount + WorkgroupSize - 1) / WorkgroupSize);

    /// <summary>How many bytes a frame's answer occupies.</summary>
    /// <param name="viewCount">How many views.</param>
    /// <param name="wordCount">How many words each of them needs.</param>
    public static long BufferSize(int viewCount, int wordCount) =>
        (long)Math.Max(viewCount, 0) * Math.Max(wordCount, 0) * sizeof(uint);

    /// <summary>One object as the shader reads it.</summary>
    /// <param name="candidate">The object.</param>
    public static CullObject Pack(in RenderObject candidate) =>
        new() {
            Center = candidate.Bounds.Center,
            Radius = candidate.Bounds.Radius,
            StagesLow = (uint)candidate.Stages.Bits,
            StagesHigh = (uint)(candidate.Stages.Bits >> 32),
            Flags = candidate.IsAlive ? Alive : 0u
        };

    /// <summary>One view as the shader reads it.</summary>
    /// <param name="view">The view.</param>
    /// <param name="objectCount">How many object slots the store holds.</param>
    /// <param name="wordCount">How many words the view's answer occupies.</param>
    /// <param name="occluderProjection">
    ///     The matrix the depth pyramid was rendered with — last frame's for
    ///     <see cref="CullPhase.Main" />, this frame's for <see cref="CullPhase.Late" />. Ignored when
    ///     <paramref name="occluderLevels" /> is zero.
    /// </param>
    /// <param name="occluderLevels">
    ///     How many levels the pyramid has, or zero for a view with nothing to test against — which
    ///     is what clears <see cref="Occluders" /> and turns the occlusion half of the pass off for
    ///     this view alone.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="view" /> is null.</exception>
    public static CullView Pack(
        RenderView view,
        int objectCount,
        int wordCount,
        Matrix4x4 occluderProjection = default,
        int occluderLevels = 0
    ) {
        ArgumentNullException.ThrowIfNull(view);

        var packed = new CullView {
            Position = view.Position,
            MaximumDistance = view.MaximumDistance,
            StagesLow = (uint)view.Stages.Bits,
            StagesHigh = (uint)(view.Stages.Bits >> 32),
            ObjectCount = (uint)Math.Max(objectCount, 0),
            WordCount = (uint)Math.Max(wordCount, 0),
            OccluderProjection = occluderProjection,
            OccluderLevels = (uint)Math.Max(occluderLevels, 0),
            Flags = occluderLevels > 0 ? Occluders : 0u
        };

        var planes = view.Frustum.AsSpan();

        for (var i = 0; i < BoundingFrustum.PlaneCount; i++) {
            packed.Planes[i] = new(planes[i].Normal, planes[i].D);
        }

        return packed;
    }

    /// <summary>
    ///     Whether the shader would set this object's bit for this view.
    /// </summary>
    /// <param name="candidate">The packed object.</param>
    /// <param name="view">The packed view.</param>
    /// <remarks>
    ///     The three rejections before any geometry — dead, in no stage this view draws, beyond the
    ///     view's own distance — in the order the shader applies them and the order
    ///     <see cref="VisibilityGroup" /> applies them. Each is cheaper than the frustum test and each
    ///     removes objects the frustum would have accepted.
    /// </remarks>
    public static bool IsVisible(in CullObject candidate, in CullView view) {
        if ((candidate.Flags & Alive) == 0) {
            return false;
        }

        if ((candidate.StagesLow & view.StagesLow) == 0 && (candidate.StagesHigh & view.StagesHigh) == 0) {
            return false;
        }

        if (view.MaximumDistance > 0f) {
            var reach = view.MaximumDistance + candidate.Radius;
            var offset = candidate.Center - view.Position;

            if (Vector3.Dot(offset, offset) > reach * reach) {
                return false;
            }
        }

        for (var i = 0; i < BoundingFrustum.PlaneCount; i++) {
            if (Outside(view.Planes[i], candidate.Center, candidate.Radius)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Whether a sphere is entirely on the outer side of a plane, within the rounding slack.
    /// </summary>
    /// <param name="plane">The plane, as <c>(normal.xyz, d)</c>.</param>
    /// <param name="center">The sphere's centre.</param>
    /// <param name="radius">Its radius.</param>
    /// <remarks>
    ///     The slack is <see cref="MathUtil.RoundingSlack" /> times the magnitudes the dot product
    ///     just cancelled, which is what <see cref="BoundingSphere.Intersects(Plane)" /> does — and
    ///     it has to be, or the two paths disagree exactly at tangency, which is where a randomised
    ///     comparison spends most of its interesting cases. Erring towards keeping the object is the
    ///     right direction for a cull: a false positive is a draw call, a false negative is an object
    ///     that vanishes.
    /// </remarks>
    public static bool Outside(Vector4 plane, Vector3 center, float radius) {
        var normal = new Vector3(plane.X, plane.Y, plane.Z);
        var distance = Vector3.Dot(normal, center) + plane.W;

        var slack = MathUtil.RoundingSlack
            * (radius + Vector3.Dot(Vector3.Abs(center), Vector3.Abs(normal)) + MathF.Abs(plane.W));

        return distance < -radius - slack;
    }

    /// <summary>
    ///     The screen rectangle and the nearest depth of an object, as the occlusion test projects
    ///     them.
    /// </summary>
    /// <param name="candidate">The packed object.</param>
    /// <param name="view">The packed view, whose occluder matrix does the projecting.</param>
    /// <param name="minimum">The rectangle's lower corner in UV, and the object's furthest depth.</param>
    /// <param name="maximum">Its upper corner, and the object's nearest depth.</param>
    /// <returns>False when the object reaches behind the near plane and has no usable rectangle.</returns>
    /// <remarks>
    ///     <para>
    ///         The eight corners of the box around the sphere, not the sphere: a sphere's screen
    ///         bound is not the projection of its centre, and the box's bound is never smaller than
    ///         the sphere's. Larger costs a few objects that could have been culled; smaller costs an
    ///         object that vanishes.
    ///     </para>
    ///     <para>
    ///         The UVs are unclamped, so a caller can see how far off screen the rectangle reached.
    ///         The shader saturates them before choosing a level, and so does
    ///         <see cref="IsOccluded(in CullObject, in CullView, Int2, OccluderDepth)" />.
    ///     </para>
    /// </remarks>
    public static bool ScreenBounds(in CullObject candidate, in CullView view, out Vector3 minimum, out Vector3 maximum) =>
        ScreenBounds(candidate.Center, candidate.Radius, view, out minimum, out maximum);

    /// <summary>
    ///     The same, of any sphere.
    /// </summary>
    /// <param name="center">The sphere's centre.</param>
    /// <param name="radius">Its radius.</param>
    /// <param name="view">The packed view, whose occluder matrix does the projecting.</param>
    /// <param name="minimum">The rectangle's lower corner in UV, and the sphere's furthest depth.</param>
    /// <param name="maximum">Its upper corner, and the sphere's nearest depth.</param>
    /// <returns>False when the sphere reaches behind the near plane and has no usable rectangle.</returns>
    /// <remarks>
    ///     A sphere and not an object, which is improvement 6's sibling on the host: the object cull
    ///     and the cluster traversal ask the same question of the same pyramid with the same matrix,
    ///     and two implementations of "visible against last frame's pyramid" is two places for the
    ///     definition to drift — see <c>docs/plan/22-virtualized-geometry.md</c> improvement 3. The shader's
    ///     <c>Occluded</c> was refactored the same way and for the same reason.
    /// </remarks>
    public static bool ScreenBounds(
        Vector3 center,
        float radius,
        in CullView view,
        out Vector3 minimum,
        out Vector3 maximum
    ) {
        minimum = default;
        maximum = default;

        var started = false;

        for (var corner = 0; corner < 8; corner++) {
            var offset = new Vector3(
                (corner & 1) == 0 ? -radius : radius,
                (corner & 2) == 0 ? -radius : radius,
                (corner & 4) == 0 ? -radius : radius
            );

            var clip = Matrix4x4.TransformVector4(new(center + offset, 1f), view.OccluderProjection);

            if (clip.W <= ClipEpsilon) {
                return false;
            }

            var ndc = clip.Xyz / clip.W;
            var point = new Vector3((ndc.X * 0.5f) + 0.5f, (ndc.Y * 0.5f) + 0.5f, ndc.Z);

            minimum = started ? Vector3.Min(minimum, point) : point;
            maximum = started ? Vector3.Max(maximum, point) : point;
            started = true;
        }

        return true;
    }

    /// <summary>Which pyramid level a rectangle of this many level-0 texels is covered by.</summary>
    /// <param name="extent">The rectangle's width and height, in level-0 texels.</param>
    /// <param name="levels">How many levels the pyramid has.</param>
    /// <remarks>
    ///     The level at which the rectangle spans at most two texels, so that the four corner taps
    ///     cover all of it. One level finer leaves part of the rectangle untested, and an untested
    ///     part is where the object was actually visible.
    /// </remarks>
    public static int LevelFor(Vector2 extent, int levels) =>
        Math.Clamp((int)MathF.Ceiling(MathF.Log2(MathF.Max(MathF.Max(extent.X, extent.Y), 1f))), 0, levels - 1);

    /// <summary>The size of one level of a pyramid whose level 0 is <paramref name="size" />.</summary>
    /// <param name="size">Level 0's size in texels.</param>
    /// <param name="level">Which level.</param>
    /// <remarks>
    ///     Halved by flooring and never below one, which is how every API sizes a mip chain — and the
    ///     reason <c>HiZReduce.rvn</c> reduces a 3×3 block rather than a 2×2 one.
    /// </remarks>
    public static Int2 LevelSize(Int2 size, int level) =>
        new(Math.Max(1, size.X >> level), Math.Max(1, size.Y >> level));

    /// <summary>
    ///     Whether the shader would decide that last frame's depth hid this object entirely.
    /// </summary>
    /// <param name="candidate">The packed object.</param>
    /// <param name="view">The packed view.</param>
    /// <param name="size">The pyramid's level-0 size in texels.</param>
    /// <param name="depth">How to read the pyramid.</param>
    /// <returns>False for any object the pass keeps, which is every object it cannot rule out.</returns>
    /// <remarks>
    ///     The mirror of <c>Culling.Occluded</c>, and every branch of it errs towards keeping the
    ///     object: no flag, no rectangle, anything behind the near plane. The comparison is the
    ///     object's nearest point against the tile's furthest surface, which under reverse-Z is the
    ///     largest depth against the smallest — the only pair for which "definitely behind
    ///     everything" is true.
    /// </remarks>
    public static bool IsOccluded(in CullObject candidate, in CullView view, Int2 size, OccluderDepth depth) =>
        IsOccluded(candidate.Center, candidate.Radius, view, size, depth);

    /// <summary>The same, of any sphere — what the cluster traversal asks.</summary>
    /// <param name="center">The sphere's centre.</param>
    /// <param name="radius">Its radius.</param>
    /// <param name="view">The packed view.</param>
    /// <param name="size">The pyramid's level-0 size in texels.</param>
    /// <param name="depth">How to read the pyramid.</param>
    public static bool IsOccluded(
        Vector3 center,
        float radius,
        in CullView view,
        Int2 size,
        OccluderDepth depth
    ) {
        ArgumentNullException.ThrowIfNull(depth);

        if ((view.Flags & Occluders) == 0 || view.OccluderLevels == 0) {
            return false;
        }

        if (!ScreenBounds(center, radius, view, out var minimum, out var maximum)) {
            return false;
        }

        var lowest = new Vector2(Math.Clamp(minimum.X, 0f, 1f), Math.Clamp(minimum.Y, 0f, 1f));
        var highest = new Vector2(Math.Clamp(maximum.X, 0f, 1f), Math.Clamp(maximum.Y, 0f, 1f));

        var extent = (highest - lowest) * new Vector2(size.X, size.Y);
        var level = LevelFor(extent, (int)view.OccluderLevels);
        var levelSize = LevelSize(size, level);

        var x0 = Math.Clamp((int)(lowest.X * levelSize.X), 0, levelSize.X - 1);
        var y0 = Math.Clamp((int)(lowest.Y * levelSize.Y), 0, levelSize.Y - 1);
        var x1 = Math.Clamp((int)(highest.X * levelSize.X), 0, levelSize.X - 1);
        var y1 = Math.Clamp((int)(highest.Y * levelSize.Y), 0, levelSize.Y - 1);

        var furthest = MathF.Min(
            MathF.Min(depth(x0, y0, level), depth(x1, y0, level)),
            MathF.Min(depth(x0, y1, level), depth(x1, y1, level))
        );

        return maximum.Z < furthest;
    }

    /// <summary>
    ///     Whether the late pass would set this object's bit, given what the main pass decided about
    ///     it.
    /// </summary>
    /// <param name="candidate">The packed object.</param>
    /// <param name="view">The packed view, carrying the rebuilt pyramid's matrix and level count.</param>
    /// <param name="drawn">Whether the main pass already set this bit.</param>
    /// <param name="size">The rebuilt pyramid's level-0 size in texels.</param>
    /// <param name="depth">How to read that pyramid.</param>
    /// <remarks>
    ///     The mirror of the late variant's whole decision, in one line, and the shape of it is the
    ///     point: <em>not already drawn</em> comes first, because the answer a second set of draws
    ///     needs is the difference. An object the main pass drew is not re-examined at all — it is not
    ///     that it would fail the test, it is that passing would draw it twice.
    /// </remarks>
    public static bool IsLate(
        in CullObject candidate,
        in CullView view,
        bool drawn,
        Int2 size,
        OccluderDepth depth
    ) => !drawn && IsVisible(candidate, view) && !IsOccluded(candidate, view, size, depth);

    /// <summary>
    ///     Reassembles one view's device words into the host's 64-bit bitset.
    /// </summary>
    /// <param name="words">The device's words for one view, low half of each host word first.</param>
    /// <param name="destination">Where they go. Longer than needed is fine; shorter truncates.</param>
    /// <remarks>
    ///     A shift and an <c>or</c> rather than a reinterpreting cast, because the two are only the
    ///     same on a little-endian machine and this runs on whatever the host is.
    /// </remarks>
    public static void Unpack(ReadOnlySpan<uint> words, Span<ulong> destination) {
        var count = Math.Min(destination.Length, (words.Length + 1) / 2);

        for (var i = 0; i < count; i++) {
            var low = words[i * 2];
            var high = (i * 2) + 1 < words.Length ? words[(i * 2) + 1] : 0u;

            destination[i] = low | ((ulong)high << 32);
        }
    }
}

/// <summary>One object as <c>CullObject</c> in the shader declares it.</summary>
/// <remarks>
///     Thirty-two bytes, in two sixteen-byte halves so that no member straddles the alignment any
///     backend imposes — the same shape, and the same reason, as
///     <see cref="PunctualLightData" />.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct CullObject {
    /// <summary>The bounding sphere's centre, in world space.</summary>
    public Vector3 Center;

    /// <summary>Its radius.</summary>
    public float Radius;

    /// <summary>The low half of <see cref="RenderStageMask.Bits" />.</summary>
    /// <remarks>
    ///     Split in two because a 64-bit integer is optional on Vulkan and unavailable on WebGPU, and
    ///     a set intersection is the same test done twice.
    /// </remarks>
    public uint StagesLow;

    /// <summary>The high half of it.</summary>
    public uint StagesHigh;

    /// <summary><see cref="GpuCulling.Alive" /> when the slot holds a live object.</summary>
    public uint Flags;

    /// <summary>Four bytes of tail padding the shader declares and never reads.</summary>
    /// <remarks>
    ///     Declared rather than left to the compiler, so that <c>sizeof</c> is the same on every
    ///     runtime rather than on the ones that happen to round up.
    /// </remarks>
    public uint Padding;
}

/// <summary>One view as <c>CullView</c> in the shader declares it.</summary>
/// <remarks>
///     The counts ride along with the view rather than living in a uniform block, and that is what
///     lets one dispatch answer for every view at once: a dispatch covering all of them has no single
///     object count to put in a push constant, and a block written per view would mean a dispatch per
///     view.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct CullView {
    /// <summary>Six planes, which is what a frustum is.</summary>
    [InlineArray(BoundingFrustum.PlaneCount)]
    public struct PlaneArray {
        Vector4 element;
    }

    /// <summary>
    ///     The inward-facing planes as <c>(normal.xyz, d)</c>, in <see cref="BoundingFrustum" />'s
    ///     order: near, far, left, right, top, bottom.
    /// </summary>
    /// <remarks>
    ///     Planes rather than the view-projection matrix they came from, though the matrix is
    ///     sixteen floats where these are twenty-four. Extracting them in the shader would be a
    ///     second derivation of a quantity the host already has — and the host's is the one every
    ///     other consumer culls against, including <see cref="RenderView.Frustum" /> when a caller
    ///     sets it directly and there is no matrix to extract from.
    /// </remarks>
    public PlaneArray Planes;

    /// <summary>Where the view is, which is what the distance cutoff measures from.</summary>
    public Vector3 Position;

    /// <summary>Objects beyond this are culled whatever the frustum says. Zero disables it.</summary>
    public float MaximumDistance;

    /// <summary>The low half of the view's <see cref="RenderStageMask.Bits" />.</summary>
    public uint StagesLow;

    /// <summary>The high half of it.</summary>
    public uint StagesHigh;

    /// <summary>How many object slots the store holds.</summary>
    public uint ObjectCount;

    /// <summary>How many words this view's answer occupies, which is also the stride between views.</summary>
    public uint WordCount;

    /// <summary>The matrix the depth pyramid this view is tested against was rendered with.</summary>
    /// <remarks>
    ///     <para>
    ///         Which frame's that is depends on the phase, and it is the whole difference between the
    ///         two. <see cref="CullPhase.Main" /> runs before anything is drawn, so the newest depth it
    ///         can have is last frame's and this is last frame's matrix.
    ///         <see cref="CullPhase.Late" /> runs after the main pass's draws have been reduced into
    ///         the pyramid again, so both are this frame's.
    ///     </para>
    ///     <para>
    ///         Either way it is the matrix the depth was <em>drawn</em> with, which is the only one the
    ///         rectangle may be projected by: projecting this frame's rectangle onto last frame's
    ///         pixels compares a screen position against depths from somewhere else, which as the
    ///         camera turns is every object at the edges of the screen.
    ///     </para>
    /// </remarks>
    public Matrix4x4 OccluderProjection;

    /// <summary>How many levels the pyramid has, so a chosen level can be clamped to one that exists.</summary>
    public uint OccluderLevels;

    /// <summary><see cref="GpuCulling.Occluders" /> when this view has a pyramid to test against.</summary>
    public uint Flags;

    /// <summary>
    ///     <c>screenHeight / (2 tan(fov / 2))</c> — what an object-space error is multiplied by to
    ///     reach pixels, once divided by the distance.
    /// </summary>
    /// <remarks>
    ///     Precomputed here rather than derived from <see cref="OccluderProjection" /> in the shader,
    ///     because it is one number per view per frame either way and the host has the field of view
    ///     in the form it was authored in. It is also the field the "projecting error with the wrong
    ///     view" sabotage changes, which is worth being one number rather than a derivation.
    /// </remarks>
    public float ErrorScale;

    /// <summary>How many pixels of deviation a cluster may have before it is refined.</summary>
    /// <remarks>
    ///     No tail padding after it, which is what the two error fields are doing here rather than at
    ///     the end: the level count, the flags and the two of them are exactly one sixteen-byte row,
    ///     so the record stays two hundred and eight bytes on both sides.
    /// </remarks>
    public float ErrorThreshold;
}
