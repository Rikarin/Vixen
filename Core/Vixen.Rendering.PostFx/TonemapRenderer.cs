// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>
///     The pass every frame ends with: linear HDR in, displayable colour out.
/// </summary>
/// <remarks>
///     <para>
///         Everything upstream works in linear radiance with no upper bound, because that is what
///         light is and what makes exposure, bloom and additive lighting compose correctly. A display
///         takes neither of those things, so something has to map an unbounded range onto zero to one
///         and encode it — and doing it anywhere but the end means every effect after it works on
///         numbers that have already been squashed.
///     </para>
///     <para>
///         <strong>It ran before this existed, and that is the point of the change.</strong>
///         <c>Tonemap.rvn</c> has been the worked example for the full-screen node since the node was
///         written, configured by hand wherever a frame needed one — a shader name, five parameter
///         keys and three bindings, spelled out per host. As an effect it is one line in a document
///         like every other, and the grading LUT the shader has always taken finally has something
///         that binds one.
///     </para>
/// </remarks>
public sealed class TonemapRenderer() : PostEffectRenderer(
    TonemapKeys.ShaderName,
    TonemapKeys.UsedPermutationKeys,
    TonemapKeys.ConstantBufferBinding
) {
    /// <summary>The linear HDR colour it maps.</summary>
    public required string Source { get; init; }

    /// <summary>
    ///     A 3D colour lookup table, or empty for none.
    /// </summary>
    /// <remarks>
    ///     What a colourist authors: the whole grade as one texture, applied after the tonemap so the
    ///     values it indexes are display-referred and bounded. Empty leaves
    ///     <see cref="TonemapKeys.UseLut" /> false, which folds the sample and the table out of the
    ///     variant entirely.
    /// </remarks>
    public string Lut { get; init; } = "";

    /// <summary>Which curve maps the range: 0 Reinhard, 1 ACES, 2 AgX, 3 Uncharted.</summary>
    public int Operator { get; set; } = 1;

    /// <summary>Whether the result is encoded to sRGB here rather than by the swapchain.</summary>
    /// <remarks>
    ///     False by default, because a target declared with an sRGB format encodes on write and doing
    ///     it twice is a washed-out frame. True is for a target that does not — an offscreen buffer a
    ///     screenshot is taken from, or a backend without sRGB swapchains.
    /// </remarks>
    public bool EncodeSrgb { get; set; }

    /// <summary>What the scene's radiance is multiplied by before the curve.</summary>
    /// <remarks>Ignored when <see cref="ExposureBuffer" /> names one — see there for which wins.</remarks>
    public float Exposure { get; set; } = 1f;

    /// <summary>
    ///     The buffer <c>AutoExposure</c> left this frame's measured exposure in, or empty to use
    ///     <see cref="Exposure" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The buffer wins where it is named, and the two are a permutation apart rather than
    ///         a branch.</b> Naming one selects a variant that declares the binding and reads it;
    ///         leaving it empty selects the variant that existed before auto-exposure did, with no
    ///         buffer declared and none bound. A frame cannot accidentally get both.
    ///     </para>
    ///     <para>
    ///         <b>What it buys is that the number never crosses the bus.</b> The reduction produces it
    ///         on the device and the tonemap consumes it there; a host that read it back to set
    ///         <see cref="Exposure" /> would pay a stall and a frame of latency for a value it does
    ///         not use — which is the arrangement <c>AutoExposure.rvn</c> was written to avoid, and
    ///         the reason that pass is compute at all.
    ///     </para>
    /// </remarks>
    public string ExposureBuffer { get; init; } = "";

    /// <summary>The radiance that maps to white.</summary>
    public float WhitePoint { get; set; } = 4f;

    /// <summary>Contrast, applied around middle grey.</summary>
    public float Contrast { get; set; } = 1f;

    /// <summary>Saturation, 0 for greyscale.</summary>
    public float Saturation { get; set; } = 1f;

    /// <summary>White balance, in mireds away from neutral.</summary>
    public float Temperature { get; set; }

    /// <summary>What the shadows are tinted toward.</summary>
    public Vector3 ShadowTint { get; set; } = Vector3.One;

    /// <summary>What the highlights are tinted toward.</summary>
    public Vector3 HighlightTint { get; set; } = Vector3.One;

    /// <summary>How many entries the LUT has along one axis.</summary>
    /// <remarks>
    ///     Needed because a 3D LUT is sampled with a half-texel inset at both ends — without it the
    ///     first and last entries are half weighted, which shows as black and white points that are
    ///     slightly wrong and nothing else.
    /// </remarks>
    public float LutSize { get; set; } = 32f;

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        var graded = !string.IsNullOrEmpty(Lut);

        var measured = !string.IsNullOrEmpty(ExposureBuffer);

        parameters.Set(TonemapKeys.Operator, Operator);
        parameters.Set(TonemapKeys.UseLut, graded);
        parameters.Set(TonemapKeys.EncodeSrgb, EncodeSrgb);
        parameters.Set(TonemapKeys.UseExposureBuffer, measured);

        parameters.Set(TonemapKeys.Exposure, Exposure);
        parameters.Set(TonemapKeys.WhitePoint, WhitePoint);
        parameters.Set(TonemapKeys.Contrast, Contrast);
        parameters.Set(TonemapKeys.Saturation, Saturation);
        parameters.Set(TonemapKeys.Temperature, Temperature);
        parameters.Set(TonemapKeys.ShadowTint, ShadowTint);
        parameters.Set(TonemapKeys.HighlightTint, HighlightTint);
        parameters.Set(TonemapKeys.LutSize, LutSize);

        Read(bindings, TonemapKeys.SourceBinding, Source);

        // The table's binding exists in the default variant whether or not this frame has a table, and
        // a descriptor set with a hole in it is a validation error. The source stands in — a texture
        // that certainly exists — and `UseLut` is what stops it being read.
        Read(bindings, TonemapKeys.LutBinding, graded ? Lut : Source);

        Sample(bindings, TonemapKeys.SourceSamplerBinding, Samplers!.LinearClamp);
        Sample(bindings, TonemapKeys.LutSamplerBinding, Samplers!.LinearClamp);

        // ⚠ Bound only when the permutation reads it. Unlike the LUT above — whose binding exists in
        // every variant, so a stand-in has to fill it — the exposure buffer folds out of the variant
        // entirely when `UseExposureBuffer` is false, and there is no texture that would stand in for
        // a buffer anyway.
        if (measured) {
            ReadBuffer(bindings, TonemapKeys.ExposureBufferBinding, ExposureBuffer);
        }
    }
}
