// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>
///     The transforms a spot light and a point light are shadowed from.
/// </summary>
/// <remarks>
///     <para>
///         Where <see cref="ShadowCascades" /> has to <em>invent</em> a volume — a directional light
///         has no position, so a cascade is fitted to the camera and everything hard about it follows
///         from that — a punctual light already is one. A spot light's shadow frustum is its cone; a
///         point light's is six of them. There is nothing to stabilise, because nothing moves when
///         the camera does.
///     </para>
///     <para>
///         Which is why these are a handful of lines and cascades are two hundred: the difficulty in
///         cascaded shadow maps was never the projection.
///     </para>
/// </remarks>
public static class ShadowProjections {
    /// <summary>The widest cone that can still be rendered with one perspective projection.</summary>
    /// <remarks>
    ///     A cone approaching a hemisphere needs a field of view approaching 180°, where a
    ///     perspective projection's extent goes to infinity and its depth precision goes to nothing. A
    ///     light wider than this wants the point light's six faces instead, and clamping here means it
    ///     renders slightly wrong rather than not at all.
    /// </remarks>
    public const float MaximumConeAngle = 1.48f;

    /// <summary>
    ///     The world-to-clip transform a spot light casts through.
    /// </summary>
    /// <param name="position">Where the light is.</param>
    /// <param name="direction">Which way it points, away from the light.</param>
    /// <param name="outerAngle">Its outer cone half-angle in radians.</param>
    /// <param name="range">Where its contribution reaches zero, which is its far plane.</param>
    /// <param name="near">Its near plane.</param>
    /// <remarks>
    ///     The field of view is twice the outer half-angle exactly, so the shadow map's texels are
    ///     spent on the cone and on nothing else. Fitting a wider frustum "to be safe" is the common
    ///     mistake and costs resolution everywhere for a margin that lights nothing.
    /// </remarks>
    public static Matrix4x4 Spot(
        Vector3 position,
        Vector3 direction,
        float outerAngle,
        float range,
        float near = 0.05f
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(range);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(near);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(range, near);

        var forward = Vector3.Normalize(direction);
        var angle = Math.Clamp(outerAngle, 0.01f, MaximumConeAngle);

        var view = Matrix4x4.LookAt(position, position + forward, UpFor(forward));
        var projection = Matrix4x4.PerspectiveFieldOfView(angle * 2f, 1f, near, range);

        return view * projection;
    }

    /// <inheritdoc cref="CubeMapping.Cube" />
    public static Matrix4x4 Cube(Vector3 position, CubeFace face, float range, float near = 0.05f) =>
        CubeMapping.Cube(position, face, range, near);

    /// <inheritdoc cref="CubeMapping.CubeAxes" />
    public static (Vector3 Forward, Vector3 Up) CubeAxes(CubeFace face) => CubeMapping.CubeAxes(face);

    /// <summary>How many shadow tiles a light of this kind needs.</summary>
    /// <remarks>
    ///     Six for a point light and one for a spot, which is the whole cost argument between them:
    ///     a shadowed point light is six times the culling, six times the draws and six times the
    ///     atlas of a shadowed spot. A designer who reaches for a point light where a spot would do
    ///     is making that trade without being told, which is why it is a number here rather than an
    ///     implementation detail.
    /// </remarks>
    public static int TileCount(LightKind kind) =>
        kind switch {
            LightKind.Point => 6,
            LightKind.Spot => 1,
            _ => 0
        };

    /// <summary>
    ///     The same transform, landing in one tile of an atlas instead of in the whole texture.
    /// </summary>
    /// <param name="viewProjection">World to the light's clip space.</param>
    /// <param name="scale">How much of the atlas the tile is, as a fraction.</param>
    /// <param name="offset">Where it starts, as a fraction, with the origin top-left.</param>
    /// <returns>A matrix whose <c>NdcToUv</c> addresses the atlas directly.</returns>
    /// <remarks>
    ///     <para>
    ///         <see cref="ShadowCascades.AtlasProjection" />'s arithmetic, for an atlas whose tiles are
    ///         not cascades. The same argument applies word for word: a shader given the raw matrix
    ///         does <c>NdcToUv(M · p)</c>, which addresses the whole texture — so with sixteen tiles in
    ///         it every lookup lands a sixteenth of the way into somebody else's, and reads a
    ///         <em>plausible</em> depth from it. Nothing about the result looks like a mismatch; it
    ///         looks like a shadow in the wrong place.
    ///     </para>
    ///     <para>
    ///         Folded into the matrix rather than applied to the UV afterwards because the shader then
    ///         has one multiply and no per-tile arithmetic, and because the alternative is a scale and
    ///         an offset that a shading pass and a caster pass have to agree about twice.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The two axes are not symmetric, and that asymmetry is the whole of this
    ///             function's difficulty.
    ///         </b>
    ///         <c>Transform.NdcToUv</c> negates y and not x, so the y
    ///         translation is the x one's mirror: <c>1 − scale − 2·offset</c> against
    ///         <c>2·offset + scale − 1</c>. Get it wrong and every lookup lands in the tile of
    ///         whichever row is opposite — reading a depth that is real, from the wrong light or the
    ///         wrong cascade, which draws a shadow and draws it in the wrong place. <c>ShadowCascades</c>
    ///         had exactly that for four days, because the test that guarded it computed its UV the
    ///         way this used to assume rather than the way a shader does.
    ///     </para>
    /// </remarks>
    public static Matrix4x4 Tile(in Matrix4x4 viewProjection, Vector2 scale, Vector2 offset) {
        var tile = new Matrix4x4(
            scale.X,
            0f,
            0f,
            0f,
            0f,
            scale.Y,
            0f,
            0f,
            0f,
            0f,
            1f,
            0f,
            2f * offset.X + scale.X - 1f,
            1f - scale.Y - 2f * offset.Y,
            0f,
            1f
        );

        return viewProjection * tile;
    }

    /// <summary>An up vector that is not parallel to <paramref name="forward" />.</summary>
    static Vector3 UpFor(Vector3 forward) => MathF.Abs(forward.Y) > 0.99f ? new(0f, 0f, 1f) : new(0f, 1f, 0f);
}
