// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>
///     FXAA: the cheap antialiasing, and the one that needs nothing but the colour it is given.
/// </summary>
/// <remarks>
///     <para>
///         Luminance edges found from the frame itself, blended along their direction. It needs no
///         history, no motion vectors, no depth and no second sample per pixel, which is why it is the
///         fallback everywhere the others cannot go — mobile, a frame with no motion vectors, a
///         debug view.
///     </para>
///     <para>
///         What it costs is texture detail: it cannot tell an edge in the geometry from an edge in an
///         albedo map, so it softens both. That is the trade, and it is why
///         <see cref="TemporalAntialiasingRenderer" /> is the default where it can run.
///     </para>
/// </remarks>
public sealed class FxaaRenderer() : PostEffectRenderer(
    FxaaKeys.ShaderName,
    FxaaKeys.UsedPermutationKeys,
    FxaaKeys.ConstantBufferBinding
) {
    /// <summary>The texture it antialiases.</summary>
    public required string Source { get; init; }

    /// <summary>Whether the subpixel pass runs, which softens single-pixel features.</summary>
    public bool Subpixel { get; set; } = true;

    /// <summary>The contrast an edge needs before it is antialiased at all.</summary>
    public float EdgeThreshold { get; set; } = 0.125f;

    /// <summary>The floor under that, so a dark area's noise is not treated as edges.</summary>
    public float EdgeThresholdMinimum { get; set; } = 0.0312f;

    /// <summary>How much of the subpixel blend is applied.</summary>
    public float SubpixelQuality { get; set; } = 0.75f;

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        parameters.Set(FxaaKeys.Subpixel, Subpixel);
        parameters.Set(FxaaKeys.TexelSize, TexelSize(frame.Size));
        parameters.Set(FxaaKeys.EdgeThreshold, EdgeThreshold);
        parameters.Set(FxaaKeys.EdgeThresholdMin, EdgeThresholdMinimum);
        parameters.Set(FxaaKeys.SubpixelQuality, SubpixelQuality);

        Read(bindings, FxaaKeys.SourceBinding, Source);

        // Linear, and it matters more here than anywhere else in the chain: FXAA's whole final step
        // is one bilinear tap placed off-centre along the edge, so a point sampler turns the blend
        // into a copy and the effect silently does nothing.
        Sample(bindings, FxaaKeys.SourceSamplerBinding, Samplers!.LinearClamp);
    }
}

/// <summary>
///     Contrast-adaptive sharpening: puts back the detail the rest of the chain took out.
/// </summary>
/// <remarks>
///     Every antialiasing pass and every upscale is a blur, and a frame that has been through both
///     looks soft in a way that reads as low resolution. This is the standard answer — sharpen by an
///     amount that depends on the local contrast, so flat areas are left alone and a ringing halo
///     never appears around a high-contrast edge.
/// </remarks>
public sealed class SharpenRenderer() : PostEffectRenderer(
    SharpenKeys.ShaderName,
    SharpenKeys.UsedPermutationKeys,
    SharpenKeys.ConstantBufferBinding
) {
    /// <summary>The texture it sharpens.</summary>
    public required string Source { get; init; }

    /// <summary>Whether each channel adapts on its own rather than on luminance.</summary>
    public bool PerChannel { get; set; } = true;

    /// <summary>How much sharpening is applied, 0 to 1.</summary>
    public float Sharpness { get; set; } = 0.5f;

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        parameters.Set(SharpenKeys.PerChannel, PerChannel);
        parameters.Set(SharpenKeys.TexelSize, TexelSize(frame.Size));
        parameters.Set(SharpenKeys.Sharpness, Sharpness);

        Read(bindings, SharpenKeys.SourceBinding, Source);

        // Point, deliberately: the kernel reads named neighbours a texel apart, and a linear tap at a
        // texel centre is the same value at a higher cost — until the target is a different size from
        // the source, where it would quietly average two of them and blur what this exists to sharpen.
        Sample(bindings, SharpenKeys.PointSamplerBinding, Samplers!.PointClamp);
    }
}

/// <summary>
///     The lens: vignette, chromatic aberration and grain, in one pass because they are one look.
/// </summary>
/// <remarks>
///     Three effects rather than one, each behind its own permutation, so a frame that wants only
///     grain emits only grain. Together at the end of the chain because all three are what a camera
///     does to an image rather than what a scene does to light — and because each is one or two taps,
///     where three passes would be three full-screen reads of an HDR target.
/// </remarks>
public sealed class VignetteRenderer() : PostEffectRenderer(
    VignetteKeys.ShaderName,
    VignetteKeys.UsedPermutationKeys,
    VignetteKeys.ConstantBufferBinding
), IPostProcessTarget {
    PostProcessOverlay applied;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Recorded rather than applied, so the authored properties stay what the document set and
    ///     the overlay is laid over them each frame. See <c>IPostProcessTarget</c>.
    /// </remarks>
    public void Apply(in PostProcessOverlay overlay) => applied = overlay;

    /// <summary>The texture it darkens, tints and grains.</summary>
    public required string Source { get; init; }

    /// <summary>Whether the corners are darkened.</summary>
    public bool UseVignette { get; set; } = true;

    /// <summary>Whether the channels are offset toward the edges.</summary>
    public bool UseChromaticAberration { get; set; } = true;

    /// <summary>Whether film grain is added.</summary>
    public bool UseGrain { get; set; } = true;

    /// <summary>Whether grain is scaled by luminance, which is what film actually does.</summary>
    public bool LuminanceWeightedGrain { get; set; } = true;

    /// <summary>Whether the image is warped radially before anything else reads it.</summary>
    /// <remarks>
    ///     ⚠ First in the shader, and it has to be: distortion moves where a pixel comes from, so
    ///     everything after it samples the warped coordinate and stays registered. Applied last it
    ///     would warp the vignette and the grain along with the picture.
    /// </remarks>
    public bool UseLensDistortion { get; set; }

    /// <summary>What colour the corners go towards.</summary>
    public Vector3 VignetteColour { get; set; } = Vector3.Zero;

    /// <summary>Where the vignette is centred, in UV.</summary>
    public Vector2 VignetteCentre { get; set; } = new(0.5f, 0.5f);

    /// <summary>0 follows the aspect ratio, 1 is a circle whatever shape the screen is.</summary>
    public float VignetteRoundness { get; set; }

    /// <summary>How far the image is pushed out (positive) or pulled in (negative) at the edges.</summary>
    public float DistortionIntensity { get; set; }

    /// <summary>Per-axis multipliers, so one axis can be left undistorted.</summary>
    public Vector2 DistortionScale { get; set; } = Vector2.One;

    /// <summary>Where the distortion is centred, in UV.</summary>
    public Vector2 DistortionCentre { get; set; } = new(0.5f, 0.5f);

    /// <summary>A zoom applied after the warp, to hide the border a positive distortion pulls in.</summary>
    public float DistortionZoom { get; set; } = 1f;

    /// <summary>How dark the corners go.</summary>
    public float VignetteIntensity { get; set; } = 0.4f;

    /// <summary>How gradually the vignette comes on.</summary>
    public float VignetteSmoothness { get; set; } = 0.5f;

    /// <summary>How far the channels separate at the edge of the frame.</summary>
    public float AberrationStrength { get; set; } = 0.003f;

    /// <summary>How much grain is added.</summary>
    public float GrainIntensity { get; set; } = 0.04f;

    /// <summary>How large the grain is.</summary>
    public float GrainScale { get; set; } = 1f;

    /// <summary>
    ///     Which frame this is, so the grain moves.
    /// </summary>
    /// <remarks>
    ///     Grain that does not change between frames is not grain — it is a texture stuck to the
    ///     screen, and it is far more distracting than none at all. A host advances this every frame.
    /// </remarks>
    public float FrameIndex { get; set; }

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        parameters.Set(VignetteKeys.UseLensDistortion, UseLensDistortion);
        parameters.Set(VignetteKeys.VignetteColor, VignetteColour);
        parameters.Set(VignetteKeys.VignetteCenter, VignetteCentre);
        parameters.Set(VignetteKeys.VignetteRoundness, VignetteRoundness);
        parameters.Set(VignetteKeys.DistortionIntensity, DistortionIntensity);
        parameters.Set(VignetteKeys.DistortionScale, DistortionScale);
        parameters.Set(VignetteKeys.DistortionCenter, DistortionCentre);
        parameters.Set(VignetteKeys.DistortionZoom, DistortionZoom);

        // The screen's shape, so a vignette at roundness zero is an ellipse the same shape as the
        // frame. Taken from the frame rather than authored: an aspect ratio a document typed would be
        // wrong on every window that was not the one it was typed for.
        parameters.Set(
            VignetteKeys.AspectRatio,
            frame.Size.Y > 0 ? (float)frame.Size.X / frame.Size.Y : 1f
        );

        parameters.Set(VignetteKeys.UseVignette, UseVignette);
        parameters.Set(VignetteKeys.UseChromaticAberration, UseChromaticAberration);
        parameters.Set(VignetteKeys.UseGrain, UseGrain);
        parameters.Set(VignetteKeys.LuminanceWeightedGrain, LuminanceWeightedGrain);

        parameters.Set(
            VignetteKeys.VignetteIntensity,
            applied.VignetteIntensity?.Over(VignetteIntensity) ?? VignetteIntensity
        );

        parameters.Set(
            VignetteKeys.VignetteSmoothness,
            applied.VignetteSmoothness?.Over(VignetteSmoothness) ?? VignetteSmoothness
        );

        parameters.Set(
            VignetteKeys.AberrationStrength,
            applied.AberrationStrength?.Over(AberrationStrength) ?? AberrationStrength
        );

        parameters.Set(VignetteKeys.GrainIntensity, applied.GrainIntensity?.Over(GrainIntensity) ?? GrainIntensity);
        parameters.Set(VignetteKeys.GrainScale, GrainScale);
        parameters.Set(VignetteKeys.FrameIndex, FrameIndex);

        Read(bindings, VignetteKeys.SourceBinding, Source);
        Sample(bindings, VignetteKeys.LinearSamplerBinding, Samplers!.LinearClamp);
    }
}

/// <summary>
///     Depth fog, with an optional height falloff and sun scattering.
/// </summary>
/// <remarks>
///     A post-process rather than a per-material term, and that is what makes it affordable: fog
///     depends on the distance from the camera to whatever was drawn, which the depth buffer already
///     holds for every pixel of the frame. Adding it in every material's shader would mean every
///     material carrying the fog's parameters and evaluating it whether it is on or not.
/// </remarks>
public sealed class FogRenderer() : PostEffectRenderer(
    FogKeys.ShaderName,
    FogKeys.UsedPermutationKeys,
    FogKeys.ConstantBufferBinding
), IPostProcessTarget {
    PostProcessOverlay applied;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Recorded rather than applied, so the authored properties stay what the document set and
    ///     the overlay is laid over them each frame. See <c>IPostProcessTarget</c>.
    /// </remarks>
    public void Apply(in PostProcessOverlay overlay) => applied = overlay;

    /// <summary>The colour it fogs.</summary>
    public required string Source { get; init; }

    /// <summary>The depth it reads distance from.</summary>
    public required string Depth { get; init; }

    /// <summary>Which falloff curve is used.</summary>
    public int Mode { get; set; } = 2;

    /// <summary>Whether fog thins with altitude, as an atmosphere does.</summary>
    public bool HeightFalloff { get; set; } = true;

    /// <summary>Whether looking toward the sun brightens the fog.</summary>
    public bool SunScattering { get; set; } = true;

    /// <summary>Clip space back to world, for reconstructing where a pixel was.</summary>
    /// <summary>The view the two below are derived from, or null to set them by hand.</summary>
    /// <remarks>
    ///     ⚠ <b>A document has no other way to reach a camera.</b> The matrix and the position are a
    ///     host's to set per frame, which works for a host assembling this in C# and leaves a
    ///     <c>!Fog</c> node with an identity matrix and a camera at the origin — fog that is correct
    ///     for a view nobody is looking through. <see cref="SkyRenderer.View" /> is the same
    ///     arrangement for the same reason.
    /// </remarks>
    public RenderView? View { get; set; }

    /// <summary>Clip back to world, when no <see cref="View" /> supplies it.</summary>
    public Matrix4x4 InverseViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>Where the camera is, which the distance is measured from.</summary>
    public Vector3 CameraPosition { get; set; }

    /// <summary>The fog's colour.</summary>
    public Vector3 Colour { get; set; } = new(0.5f, 0.6f, 0.7f);

    /// <summary>How quickly it accumulates with distance.</summary>
    public float Density { get; set; } = 0.02f;

    /// <summary>Where it begins, for the linear mode.</summary>
    public float Start { get; set; } = 10f;

    /// <summary>Where it is fully opaque, for the linear mode.</summary>
    public float End { get; set; } = 200f;

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        parameters.Set(FogKeys.Mode, Mode);
        parameters.Set(FogKeys.HeightFalloff, HeightFalloff);
        parameters.Set(FogKeys.SunScattering, SunScattering);

        if (View is { } view) {
            CameraPosition = view.Position;

            if (Matrix4x4.Invert(view.ViewProjection, out var inverse)) {
                InverseViewProjection = inverse;
            }
        }

        parameters.Set(FogKeys.InverseViewProjection, InverseViewProjection);
        parameters.Set(FogKeys.CameraPosition, CameraPosition);
        parameters.Set(FogKeys.FogColor, applied.FogColour?.Over(Colour) ?? Colour);
        parameters.Set(FogKeys.Density, applied.FogDensity?.Over(Density) ?? Density);
        parameters.Set(FogKeys.FogStart, Start);
        parameters.Set(FogKeys.FogEnd, End);

        Read(bindings, FogKeys.SourceBinding, Source);
        Read(bindings, FogKeys.DepthBufferBinding, Depth);
        Sample(bindings, FogKeys.LinearSamplerBinding, Samplers!.LinearClamp);
    }
}

/// <summary>
///     Outlines from depth and normal discontinuities — the editor's selection highlight, and the
///     stylised look that goes with cel shading.
/// </summary>
/// <remarks>
///     Screen space rather than geometry: the alternative is drawing every silhouette edge as a
///     second pass over the mesh, which needs adjacency data the importer would have to build and
///     scales with the scene rather than with the screen. What it cannot do is outline something
///     hidden behind something else, which is exactly what a selection outline usually wants — hence
///     the mask.
/// </remarks>
public sealed class OutlineRenderer() : PostEffectRenderer(
    OutlineKeys.ShaderName,
    OutlineKeys.UsedPermutationKeys,
    OutlineKeys.ConstantBufferBinding
) {
    /// <summary>The colour it draws over.</summary>
    public required string Source { get; init; }

    /// <summary>The depth it finds silhouettes in.</summary>
    public required string Depth { get; init; }

    /// <summary>The normals it finds creases in.</summary>
    public string Normals { get; init; } = "";

    /// <summary>A mask of what is selected, for an outline around one object.</summary>
    public string SelectionMask { get; init; } = "";

    /// <summary>Whether creases inside a silhouette are outlined too.</summary>
    public bool UseNormals { get; set; } = true;

    /// <summary>Whether only the masked object is outlined.</summary>
    public bool SelectionOnly { get; set; }

    /// <summary>The outline's colour.</summary>
    public Vector4 Colour { get; set; } = new(1f, 0.6f, 0f, 1f);

    /// <summary>How wide it is, in pixels.</summary>
    public float Thickness { get; set; } = 1.5f;

    /// <summary>The camera's near plane, for turning depth back into distance.</summary>
    public float NearPlane { get; set; } = 0.1f;

    /// <summary>The camera's far plane.</summary>
    public float FarPlane { get; set; } = 1000f;

    /// <summary>How much depth has to change across a pixel to count as an edge.</summary>
    public float DepthThreshold { get; set; } = 0.02f;

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        parameters.Set(OutlineKeys.UseNormals, UseNormals && !string.IsNullOrEmpty(Normals));
        parameters.Set(OutlineKeys.SelectionOnly, SelectionOnly && !string.IsNullOrEmpty(SelectionMask));

        parameters.Set(OutlineKeys.TexelSize, TexelSize(frame.Size));
        parameters.Set(OutlineKeys.OutlineColor, Colour);
        parameters.Set(OutlineKeys.Thickness, Thickness);
        parameters.Set(OutlineKeys.NearPlane, NearPlane);
        parameters.Set(OutlineKeys.FarPlane, FarPlane);
        parameters.Set(OutlineKeys.DepthThreshold, DepthThreshold);

        Read(bindings, OutlineKeys.SourceBinding, Source);
        Read(bindings, OutlineKeys.DepthBufferBinding, Depth);

        // The shader declares both in its default variant, so both bindings exist whether or not this
        // frame has something to put on them — and a set with a hole in it is a validation error. The
        // colour stands in: it is a texture that certainly exists and is certainly the right shape,
        // and the permutations above are what stop it being read.
        Read(bindings, OutlineKeys.NormalBufferBinding, string.IsNullOrEmpty(Normals) ? Source : Normals);

        Read(
            bindings,
            OutlineKeys.SelectionMaskBinding,
            string.IsNullOrEmpty(SelectionMask) ? Source : SelectionMask
        );

        Sample(bindings, OutlineKeys.PointSamplerBinding, Samplers!.PointClamp);
    }
}
