// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>
///     Occlusion and a sun shadow, traced through the global distance field.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The companion to <see cref="AmbientOcclusionRenderer" />, not a replacement.</strong>
///         GTAO searches the depth buffer, so it is sharp, cheap and completely blind to anything off
///         screen — a wall behind the camera does not darken the floor in front of it. This searches
///         the field, so it sees the whole world at the field's own resolution and nothing finer. A
///         frame wants both, multiplied: contact darkening from one, the room from the other.
///     </para>
///     <para>
///         <strong>It traces a sun shadow too, because the march is already paid for.</strong> A
///         distance field answers "how much of the sky is blocked" and "how much of the sun is
///         blocked" with the same walk over the same data, and the penumbra falls out of how close
///         the ray passed rather than out of a filter kernel — which is what a cascade cannot do at
///         any resolution.
///     </para>
///     <para>
///         <strong>It binds no part of the clipmap itself.</strong> The volumes, their textures and
///         their sampler live in the frame's set 0, put there by
///         <c>GlobalDistanceFieldRenderer</c> — because the field follows the camera and belongs to
///         the frame, not to this pass. What this owns is the depth, the normals and the numbers of
///         its own march. The pair has to agree on one thing and only one: the compose-slot prefix
///         those names are written under.
///     </para>
///     <para>
///         <strong>Occlusion in red, sun visibility in green.</strong> Two numbers a shading pass
///         multiplies into different terms, so they are kept apart. Pre-combining them into one
///         "darkness" darkens direct lighting with ambient occlusion, which is the classic mistake
///         that makes a scene look dirty rather than grounded.
///     </para>
/// </remarks>
public sealed class DistanceFieldAoRenderer() : PostEffectRenderer(
    DistanceFieldAoKeys.ShaderName,
    DistanceFieldAoKeys.UsedPermutationKeys,
    DistanceFieldAoKeys.ConstantBufferBinding
) {
    /// <summary>The depth it reconstructs world positions from.</summary>
    public required string Depth { get; init; }

    /// <summary>The normals it orients the occlusion integral by.</summary>
    public required string Normals { get; init; }

    /// <summary>The shader behind the field slot — what the march actually reads.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>NoDistanceField</c> by default, which answers "nothing is near" and makes this pass a
    ///         constant: fully open, fully lit. That is the honest default, because a project with no
    ///         clipmap has no field to trace and the alternative would be a pass that refuses to
    ///         compile until something it does not own exists.
    ///     </para>
    ///     <para>
    ///         <c>GlobalDistanceField</c> is what a frame with a
    ///         <c>GlobalDistanceFieldRenderer</c> in it sets, and the two have to agree: this name is
    ///         also the compose-slot prefix that renderer writes its bindings under.
    ///     </para>
    /// </remarks>
    public string Source { get; set; } = MaterialCompiler.EmptyFieldShader;

    /// <summary>The slot the shader declares for it.</summary>
    const string Slot = "distanceField";

    /// <summary>Whether to trace a sun shadow alongside the occlusion.</summary>
    /// <remarks>
    ///     A permutation, so with it off the march is not merely skipped — it is not compiled, and the
    ///     pass carries none of the sun's uniforms either.
    /// </remarks>
    public bool SunShadow { get; set; } = true;

    /// <summary>Clip space back to world space, for turning a depth into a position.</summary>
    /// <remarks>
    ///     World rather than view, unlike <see cref="AmbientOcclusionRenderer" />, and it has to be:
    ///     the field is in world space and follows the camera without rotating with it.
    /// </remarks>
    public Matrix4x4 InverseViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>How far up the normal the occlusion integral looks, in metres.</summary>
    /// <remarks>
    ///     Larger than a screen-space radius by an order of magnitude, because it is measuring a room
    ///     rather than a crease. Below about a cell of the finest level it measures nothing at all.
    /// </remarks>
    public float OcclusionRadius { get; set; } = 2f;

    /// <summary>How many steps it takes along that.</summary>
    public int OcclusionSamples { get; set; } = 5;

    /// <summary>How dark the occlusion goes.</summary>
    public float OcclusionStrength { get; set; } = 1f;

    /// <summary>Which way the sun is, in world space.</summary>
    public Vector3 SunDirection { get; set; } = new(0f, 1f, 0f);

    /// <summary>How far to march toward it before calling the point lit.</summary>
    public float SunDistance { get; set; } = 200f;

    /// <summary>The reciprocal of the sun's angular radius. Larger is a sharper shadow.</summary>
    public float SunSoftness { get; set; } = 8f;

    /// <summary>How far off the surface a shadow ray starts.</summary>
    /// <remarks>
    ///     The same quantity a shadow map's depth bias is, with the same failure at both ends: without
    ///     it every lit surface shadows itself, and with too much every contact shadow detaches from
    ///     what casts it. A cell of the finest level is the honest scale, because that is the
    ///     resolution of the thing being traced.
    /// </remarks>
    public float ShadowBias { get; set; } = 0.2f;

    /// <summary>How many steps a shadow march may take.</summary>
    public int ShadowSteps { get; set; } = 64;

    /// <summary>How near counts as arrived, for both marches.</summary>
    public float SurfaceThreshold { get; set; } = 0.05f;

    /// <summary>What fraction of the reported distance each step actually takes.</summary>
    /// <remarks>
    ///     Below one because a trilinear field over-reports near a convex corner, which is exactly a
    ///     step that crosses the surface and a ray that comes out the other side.
    /// </remarks>
    public float StepScale { get; set; } = 0.9f;

    /// <summary>What fraction of the frame it runs at.</summary>
    /// <remarks>
    ///     Half, as screen-space occlusion is, and for a stronger reason: both signals are low
    ///     frequency, and this one costs a whole march per pixel rather than a few taps.
    /// </remarks>
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

        // Without this the slot is unbound, the compiler refuses the variant, and the pass draws
        // nothing while looking exactly like a pass nobody scheduled.
        Pass.Composition = ShaderComposition.Of([new KeyValuePair<string, string>(Slot, Source)]);

        parameters.Set(DistanceFieldAoKeys.SunShadow, SunShadow);

        parameters.Set(DistanceFieldAoKeys.InverseViewProjection, InverseViewProjection);
        parameters.Set(DistanceFieldAoKeys.OcclusionRadius, OcclusionRadius);
        parameters.Set(DistanceFieldAoKeys.OcclusionSamples, OcclusionSamples);
        parameters.Set(DistanceFieldAoKeys.OcclusionStrength, OcclusionStrength);

        parameters.Set(DistanceFieldAoKeys.SunDirection, SunDirection);
        parameters.Set(DistanceFieldAoKeys.SunDistance, SunDistance);
        parameters.Set(DistanceFieldAoKeys.SunSoftness, SunSoftness);
        parameters.Set(DistanceFieldAoKeys.ShadowBias, ShadowBias);
        parameters.Set(DistanceFieldAoKeys.ShadowSteps, ShadowSteps);

        parameters.Set(DistanceFieldAoKeys.SurfaceThreshold, SurfaceThreshold);
        parameters.Set(DistanceFieldAoKeys.StepScale, StepScale);

        Read(bindings, DistanceFieldAoKeys.DepthBufferBinding, Depth);
        Read(bindings, DistanceFieldAoKeys.NormalBufferBinding, Normals);

        // Point, and it has to be: a bilinear tap of a depth buffer is the average of two depths,
        // which is a surface that exists nowhere — and here it would start a march from inside
        // geometry, which reports a hit at once and paints a black pixel along every silhouette.
        Sample(bindings, DistanceFieldAoKeys.PointSamplerBinding, Samplers!.PointClamp);
    }
}
