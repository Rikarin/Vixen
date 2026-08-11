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

    /// <summary>
    ///     What the fog looks like away from the sun, <b>as a radiance in cd/m²</b>, when
    ///     <see cref="Frame" /> has no sky in it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A lerp target, therefore a radiance and not a tint.</b> The shader ends in
    ///         <c>lerp(colour, tint, amount)</c> against a scene in cd/m², so this is what a pixel
    ///         <em>becomes</em> once the fog is thick. The old default of <c>(0.5, 0.6, 0.7)</c> in a
    ///         frame lit at ninety thousand lux was not a pale veil, it was a lerp toward black:
    ///         distance did not haze over, it faded out. <c>WaterRenderer.LightFrom</c> and
    ///         <c>VolumetricFogRenderer.SunColour</c> are the same mistake in the two passes either
    ///         side of this one, and this is the third.
    ///     </para>
    ///     <para>
    ///         Prefer <see cref="Frame" />: the physical quantity is the sky's <b>mean radiance</b>,
    ///         which is a fact of the scene, and a document cannot carry one. The default is a clear
    ///         daylight sky and is the same number <c>VolumetricFogRenderer.AmbientColour</c> defaults
    ///         to, that being the same quantity for the marched half of the same medium.
    ///     </para>
    /// </remarks>
    public Vector3 Colour { get; set; } = new(1400f, 1680f, 2200f);

    /// <summary>How quickly it accumulates with distance.</summary>
    public float Density { get; set; } = 0.02f;

    /// <summary>Where it begins, for the linear mode.</summary>
    public float Start { get; set; } = 10f;

    /// <summary>Where it is fully opaque, for the linear mode.</summary>
    public float End { get; set; } = 200f;

    /// <summary>The altitude the authored density holds at.</summary>
    public float Height { get; set; }

    /// <summary>How fast density thins above <see cref="Height" />, per world unit.</summary>
    /// <remarks>
    ///     Distinct from <see cref="HeightFalloff" />, which is <em>whether</em> altitude thins the
    ///     fog at all. The shader spells the two apart for the same reason. ⚠ Zero is not "off" — it
    ///     is a fog of uniform density at every altitude, which is what <see cref="HeightFalloff" />
    ///     being false already means, more cheaply.
    /// </remarks>
    public float HeightFalloffRate { get; set; } = 0.05f;

    /// <summary>Which way the light travels, when no <see cref="Sun" /> answers.</summary>
    /// <remarks>
    ///     ⚠ The scattering peak lands where this points away from. Left at the default the fog
    ///     brightens toward straight up rather than toward whatever lights the scene.
    /// </remarks>
    public Vector3 SunDirection { get; set; } = new(0f, -1f, 0f);

    /// <summary>
    ///     What the fog looks like straight down-sun, <b>as a radiance in cd/m²</b>, when
    ///     <see cref="Sun" /> has no directional light in it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="Colour" /> plus the sun's illuminance, and that sum is the whole
    ///         point.</b> The shader blends <c>lerp(fogColour, sunColour, p)</c> with <c>p</c> the
    ///         phase function in sr⁻¹, so a sun term written as a sum collapses the blend to
    ///         <c>fogColour + p·E</c> — the single-scattering source function of a medium under a sky
    ///         of radiance <see cref="Colour" /> and a directional light of illuminance <c>E</c> lux.
    ///         The old default of <c>(1, 0.9, 0.7)</c> was a tint five decades under a sun of ninety
    ///         thousand, which is a forward peak that could only ever darken the pixel it brightened.
    ///     </para>
    ///     <para>
    ///         ⚠ The identity holds while <c>p ≤ 1</c>. Past that the shader's clamp holds the peak at
    ///         <see cref="Colour" /> <c>+ E</c> rather than tracking <c>p</c> to its 1.5 sr⁻¹ maximum
    ///         — a third of the brightness inside about eleven degrees of the sun, in exchange for a
    ///         bound at high <see cref="SunAnisotropy" />. See <c>Fog.rvn</c>'s
    ///         <c>HenyeyGreenstein</c>.
    ///     </para>
    ///     <para>
    ///         Prefer <see cref="Sun" />, on <c>VolumetricFogRenderer.Sun</c>'s argument: the sun that
    ///         shades the frame, the sun that casts its shadows and the sun this fog scatters have to
    ///         be one fact, and a document cannot carry one. The default is <see cref="Colour" />'s
    ///         plus <c>VolumetricFogRenderer.SunColour</c>'s, so a document with both fog nodes in it
    ///         describes one sun.
    ///     </para>
    /// </remarks>
    public Vector3 SunColour { get; set; } = new(91400f, 82680f, 65200f);

    /// <summary>Where the frame's directional light comes from, or null to use the authored pair.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>VolumetricFogRenderer.Sun</c>'s property, from <c>CompositorBuilder.Sun</c>, which is
    ///         the source <c>ShadowMapRenderer</c> and <c>VirtualShadowRenderer</c> already fit their
    ///         cascades along. <c>RenderLight.Direction</c> points the way the light travels and
    ///         <c>RenderLight.Radiance</c> is, for a directional light, an <b>illuminance in lux</b> —
    ///         which is what the sum in <see cref="SunColour" /> is built from.
    ///     </para>
    ///     <para>
    ///         ⚠ Availability rather than preference: a source with no directional light in it leaves
    ///         the authored pair alone rather than blacking the peak out, because a frame between
    ///         scenes has no sun for a frame or two and fog that flashed would be worse than fog that
    ///         lagged.
    ///     </para>
    /// </remarks>
    public ISunSource? Sun { get; set; }

    /// <summary>The frame's own lighting, which is where the sky's mean radiance comes from.</summary>
    /// <remarks>
    ///     <para>
    ///         Read per frame rather than snapshotted, and it has to be: <c>SceneLighting.Environment</c>
    ///         is filled by the frame's sky node after the compositor is built, so a copy taken at
    ///         wire-up is null forever. <c>VolumetricFogRenderer.Frame</c> takes the same object from
    ///         the same <c>CompositorBuilder.SceneConstants</c> for the cascades it marches against.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The sky term is the environment's mean radiance and not its <c>L00</c>.</b> An SH
    ///         projection of a uniform environment of radiance <c>L</c> has <c>L00 = L·Y₀·4π</c>, so
    ///         handing the coefficient over is 3.54× too much sky. <c>WaterRenderer.LightFrom</c>
    ///         derives it from the same two fields for the same reason.
    ///     </para>
    /// </remarks>
    public SceneConstants? Frame { get; set; }

    /// <summary>Henyey–Greenstein anisotropy, 0 isotropic to just under 1 sharply forward.</summary>
    public float SunAnisotropy { get; set; } = 0.7f;

    /// <summary>The marched volume <c>VolumetricFogRenderer</c> filled, or empty for the falloff alone.</summary>
    /// <remarks>
    ///     ⚠ Naming it is what turns the permutation on. It is not additive decoration: the volume
    ///     replaces the analytic term out to <see cref="VolumeFar" /> and the analytic term carries on
    ///     beyond it, so a name here changes what the first sixty-four metres of every ray are.
    /// </remarks>
    public string Volume { get; init; } = "";

    /// <summary>The volume's own near plane, in view depth.</summary>
    /// <remarks>
    ///     ⚠ <b>These three must be the numbers the dispatch actually used.</b> The composite inverts
    ///     the grid's Z distribution to find a slice, so a near, a far or a slice count that differs
    ///     from <c>VolumetricFogRenderer</c>'s reads the wrong slice for every pixel — smooth,
    ///     plausible, and wrong everywhere, which is this pass's recurring failure.
    /// </remarks>
    public float VolumeNear { get; set; } = 0.5f;

    /// <summary>The volume's own far plane. Past this the analytic falloff is what runs.</summary>
    public float VolumeFar { get; set; } = 64f;

    /// <summary>How many slices it cut between the two.</summary>
    public int VolumeSlices { get; set; } = 64;

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        var volumetric = Volume is { Length: > 0 } volume && frame.Has(volume);

        parameters.Set(FogKeys.Mode, Mode);
        parameters.Set(FogKeys.HeightFalloff, applied.FogHeightFalloff?.Over(HeightFalloff) ?? HeightFalloff);
        parameters.Set(FogKeys.SunScattering, applied.FogSunScattering?.Over(SunScattering) ?? SunScattering);
        parameters.Set(FogKeys.Volumetric, volumetric);

        if (View is { } view) {
            CameraPosition = view.Position;

            if (Matrix4x4.Invert(view.ViewProjection, out var inverse)) {
                InverseViewProjection = inverse;
            }

            // The camera's own planes, for turning the reverse-Z device depth into the view depth the
            // grid's Z distribution is expressed in. Taken from the same camera the unprojection is,
            // so the two cannot describe different frusta.
            if (view.Camera is { } camera) {
                parameters.Set(FogKeys.CameraNear, camera.NearPlane);
                parameters.Set(FogKeys.CameraFar, camera.FarPlane);
            }
        }

        parameters.Set(FogKeys.InverseViewProjection, InverseViewProjection);
        parameters.Set(FogKeys.CameraPosition, CameraPosition);

        // ⚠ The frame's own sky and sun where there are any, because both of this pass's colours are
        // photometric quantities of the scene rather than settings of the document. See Scattering.
        var (direction, tint, peak) = Scattering();

        // The overlay is laid over the derived value, not over the authored one: a volume that wants
        // a different fog is asking for a different *radiance*, and lerping toward it from a number
        // four decades away would make the volume's weight a brightness control.
        parameters.Set(FogKeys.FogColor, applied.FogColour?.Over(tint) ?? tint);
        parameters.Set(FogKeys.Density, applied.FogDensity?.Over(Density) ?? Density);
        parameters.Set(FogKeys.FogStart, Start);
        parameters.Set(FogKeys.FogEnd, End);
        parameters.Set(FogKeys.FogHeight, Height);
        parameters.Set(FogKeys.HeightFalloffRate, HeightFalloffRate);
        parameters.Set(FogKeys.SunDirection, direction);
        parameters.Set(FogKeys.SunColor, peak);
        parameters.Set(FogKeys.SunAnisotropy, SunAnisotropy);
        parameters.Set(FogKeys.VolumeNear, VolumeNear);
        parameters.Set(FogKeys.VolumeFar, VolumeFar);
        parameters.Set(FogKeys.VolumeSlices, (float)Math.Max(VolumeSlices, 1));

        Read(bindings, FogKeys.SourceBinding, Source);
        Read(bindings, FogKeys.DepthBufferBinding, Depth);

        // ⚠ Bound only when there is one, because there is no neutral 3D texture to stand in with —
        // and the binding folds out of the variant with the permutation, so the set is still written
        // whole. A volume named but not declared leaves the analytic path, which is the honest
        // outcome for a document whose fog node runs without the dispatches that fill it.
        if (volumetric) {
            Read(bindings, FogKeys.FogVolumeBinding, Volume);
        }

        Sample(bindings, FogKeys.LinearSamplerBinding, Samplers!.LinearClamp);
    }

    /// <summary>The three numbers the medium scatters by, from the frame's lighting where it has any.</summary>
    /// <returns>
    ///     Which way the light travels, the radiance away from the peak, and the radiance at it.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         <b>Both colours are radiances in cd/m², and neither is a tint.</b> The shader composites
    ///         <c>lerp(colour, lerp(fog, sun, p), amount)</c> into a scene lit in cd/m², so the two
    ///         targets stand in for a surface — and the sum <c>sun = fog + E</c> is what turns that
    ///         inner blend into <c>fog + p·E</c>, which is the single-scattering source function.
    ///         Deriving both here rather than at two call sites is what keeps the sum true: a sky
    ///         taken from the frame and a sun taken from the document is a peak that is not a peak.
    ///     </para>
    ///     <para>
    ///         ⚠ Each half falls through to its authored value independently, on
    ///         <c>VolumetricFogRenderer.Sunlight</c>'s rule. A frame between scenes has no sun and no
    ///         sky for a frame or two, and fog that went black for them would flash.
    ///     </para>
    /// </remarks>
    (Vector3 Direction, Vector3 Tint, Vector3 Peak) Scattering() {
        // ⚠ Y₀ = 0.282095, and Y₀·4π = 3.5449 is what separates a mean radiance from the coefficient
        // an SH projection stores. `Intensity` is the artistic scale every other ambient term is
        // multiplied by, so the fog reads the sky the rest of the frame is lit by.
        var tint = Frame?.Lighting?.Environment is { } sky
            ? sky.Irradiance.L00 * (0.282095f * sky.Intensity)
            : Colour;

        var star = Sun?.Sun;

        // ⚠ The peak is built from an *illuminance*, never from a colour, and the authored fallback is
        // read back through the same identity: `SunColour - Colour` is what the pair was saying the
        // sun carried. With neither a sun nor a sky this returns the authored pair exactly.
        var illuminance = star is { } lit ? lit.Radiance : SunColour - Colour;

        return (star?.Direction ?? SunDirection, tint, tint + illuminance);
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
