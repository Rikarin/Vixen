// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>
///     Ground-truth-style ambient occlusion: how much of the sky a pixel can actually see.
/// </summary>
/// <remarks>
///     <para>
///         The term image-based lighting cannot supply on its own. A probe says what arrives from
///         every direction at the point it was baked; it knows nothing about the crease a pixel sits
///         in, the gap under a chair, or the corner where two walls meet — and those are most of what
///         makes a rendered room read as a room. This marches the depth buffer around each pixel and
///         measures the horizon in a few directions.
///     </para>
///     <para>
///         <strong>Half resolution by default</strong>, and that is not a quality setting so much as
///         an admission about the signal: occlusion from a hemisphere is low frequency almost
///         everywhere, so the sampling cost halves twice while the visible difference is confined to
///         the sharpest contact edges. It is the standard trade, and a project that wants it at full
///         resolution sets <see cref="Scale" />.
///     </para>
///     <para>
///         <strong>It writes occlusion, not colour.</strong> What consumes it is whatever multiplies
///         the ambient term — the forward pass's <c>occlusion</c> channel, or a deferred lighting
///         pass. Compositing it into the frame directly would darken direct lighting too, which is the
///         classic mistake that makes an over-occluded scene look dirty rather than grounded.
///     </para>
/// </remarks>
public sealed class AmbientOcclusionRenderer() : PostEffectRenderer(
    SsaoKeys.ShaderName,
    SsaoKeys.UsedPermutationKeys,
    SsaoKeys.ConstantBufferBinding
) {
    /// <summary>The depth it marches.</summary>
    public required string Depth { get; init; }

    /// <summary>The normals it orients the hemisphere by.</summary>
    public required string Normals { get; init; }

    /// <summary>How many directions each pixel searches in.</summary>
    /// <remarks>
    ///     A permutation, so the loop unrolls and a low setting costs no branch. Directions cost more
    ///     than steps: each one is a fresh march, where a step continues one.
    /// </remarks>
    public int Directions { get; set; } = 8;

    /// <summary>How far along each direction it steps.</summary>
    public int Steps { get; set; } = 6;

    /// <summary>Whether it also writes the average unoccluded direction.</summary>
    /// <remarks>
    ///     <para>
    ///         A bent normal is what turns occlusion from a multiplier into a direction to sample the
    ///         environment along, which is the difference between a crease that is uniformly darker
    ///         and one that reflects the wall beside it. Off by default: nothing consumes it yet.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A view-space direction</b>, encoded unsigned into rgb. Whatever eventually consumes
    ///         it needs the inverse of the same view matrix this pass is given. The spelling before the
    ///         arc integral accumulated screen-space xy against a constant z, which was not a direction
    ///         in any space — so there is nothing to be compatible with.
    ///     </para>
    /// </remarks>
    public bool BentNormal { get; set; }

    /// <summary>Clip space back to view space, for turning depth into a position.</summary>
    /// <summary>The view the projection below is derived from, or null to set it by hand.</summary>
    /// <remarks>
    ///     ⚠ A document has no other way to reach a camera, and an identity projection unprojects
    ///     every pixel to the same place — occlusion that is smooth, plausible and completely wrong.
    ///     <see cref="SkyRenderer.View" /> is the same arrangement for the same reason.
    /// </remarks>
    public RenderView? View { get; set; }

    /// <summary>Clip back to view, when no <see cref="View" /> supplies it.</summary>
    public Matrix4x4 InverseProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>How far the hemisphere reaches, in metres.</summary>
    public float Radius { get; set; } = 0.5f;

    /// <summary>How dark the occlusion goes.</summary>
    public float Intensity { get; set; } = 1f;

    /// <summary>How quickly a distant occluder stops counting, as a multiple of the radius.</summary>
    /// <remarks>
    ///     <para>
    ///         Without it, a wall across the room occludes a pixel as much as a fold in its own surface
    ///         — which is the halo of darkness around every foreground object that gives screen-space
    ///         occlusion its reputation.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The fade is toward no horizon at all, not toward a horizon of nought.</b> Under the
    ///         arc integral, angles are measured from the view vector, and a cosine of nought there is
    ///         an occluder at ninety degrees — most of a hemisphere. Fading toward it rather than
    ///         toward −1 would make every distant surface a wall, which is the halo this exists to
    ///         prevent, arriving through the knob that prevents it.
    ///     </para>
    ///     <para>
    ///         It fades linearly from the pixel outward, so an occluder halfway to the radius already
    ///         counts half. A scene whose real occluders sit well out along the march — a floor
    ///         receding steeply toward a wall is the usual one — wants this above one, at the cost of
    ///         reaching further for a halo.
    ///     </para>
    /// </remarks>
    public float Falloff { get; set; } = 1f;

    /// <summary>The elevation below which a horizon does not count, as a sine.</summary>
    /// <remarks>
    ///     <para>
    ///         The self-occlusion guard that pays for the march's first sample standing at one depth
    ///         texel — the shell that catches the wall a pixel actually touches. At one texel, depth
    ///         quantisation alone reads as a horizon on any surface seen at a slant; rejecting the
    ///         samples that lie almost in the tangent plane is what lets the march sample the near
    ///         field rather than skip it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It floors the sample, not the estimate.</b> Under the estimator this replaced, the
    ///         number was subtracted from the horizon term and the result renormalised, which is why
    ///         it read as a strength knob as much as a guard — a project that had dialled it for
    ///         overall darkness will find it does not do that any more. The arc integral has a
    ///         normalisation of its own that a second one fights, so this is a rejection and nothing
    ///         else: raising it lightens the contacts and leaves everything else alone.
    ///     </para>
    /// </remarks>
    public float Bias { get; set; } = 0.1f;

    /// <summary>What fraction of the frame it runs at.</summary>
    public float Scale { get; set; } = 0.5f;

    /// <inheritdoc />
    protected override Int2 TargetSize(Int2 frame) =>
        new(
            Math.Max((int)MathF.Ceiling(frame.X * Scale), 1),
            Math.Max((int)MathF.Ceiling(frame.Y * Scale), 1)
        );

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        parameters.Set(SsaoKeys.Directions, Directions);
        parameters.Set(SsaoKeys.Steps, Steps);
        parameters.Set(SsaoKeys.BentNormal, BentNormal);

        // The camera's projection, not the view's view-projection: this pass unprojects depth into
        // *view* space, where the occlusion integral lives. A view that is not a camera — a cascade,
        // a probe face — has none, and leaves whatever was set by hand.
        if (View?.Camera is { } camera && Matrix4x4.Invert(camera.Projection, out var inverse)) {
            InverseProjection = inverse;

            // The G-buffer's normal is world-space and the marched positions are view-space; the
            // shader rotates the former into the latter with this. Direction-only, so the
            // translation the matrix also carries is harmless — the shader multiplies with w = 0.
            parameters.Set(SsaoKeys.View, camera.View);

            // A view-space metre as a fraction of the screen, per axis — the projection's own
            // diagonal, without which the search radius is a UV circle rather than a world one.
            parameters.Set(
                SsaoKeys.ProjectionScale,
                new Vector2(camera.Projection.M11, camera.Projection.M22)
            );
        }

        parameters.Set(SsaoKeys.InverseProjection, InverseProjection);

        // The texel of the *source* depth, not of this pass's own half-resolution target: every step
        // of the march is a tap in the depth buffer's grid, and stepping in the target's would march
        // twice as far per step and measure a horizon that is not there.
        parameters.Set(SsaoKeys.TexelSize, TexelSize(frame.Size));
        parameters.Set(SsaoKeys.Radius, Radius);
        parameters.Set(SsaoKeys.Intensity, Intensity);
        parameters.Set(SsaoKeys.Falloff, Falloff);
        parameters.Set(SsaoKeys.Bias, Bias);

        Read(bindings, SsaoKeys.DepthBufferBinding, Depth);
        Read(bindings, SsaoKeys.NormalBufferBinding, Normals);

        // Point, and it has to be: a bilinear tap of a depth buffer is the average of two depths,
        // which is a surface that exists nowhere and reads as a halo along every silhouette.
        Sample(bindings, SsaoKeys.PointSamplerBinding, Samplers!.PointClamp);
    }
}
