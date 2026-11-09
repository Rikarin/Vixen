// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>Where a height field shadows itself under one light.</summary>
/// <remarks>
///     <para>
///         <b>One where lit, nought where something between the texel and the light stands above the
///         ray to it</b> — Patina #176. A march toward the light keeps the steepest rise, so a wall of
///         height <c>h</c> casts a shadow <c>h · Height Scale · width / tan(Elevation)</c> texels long on
///         flat ground. <c>Height Scale</c> is <c>Ambient Occlusion</c>'s, so the two agree about how
///         tall a height of one is.
///     </para>
///     <para>
///         ⚠ <b>A light straight overhead casts nothing</b>, and neither does a <c>Height Scale</c> of
///         nought: both are a white picture. <c>Softness</c> is the light's angular size, which is what
///         turns a hard edge into a penumbra. Multiply the result into a base colour or an occlusion; a
///         grey under one is what a stylised material's cast shadow is made of.
///     </para>
/// </remarks>
[Node("Surface/Height Shadow", Preview = true, Summary = "Where a height field shadows itself under one light.")]
sealed partial class HeightShadowNode : TextureNode {
    /// <summary>The height field. A single channel.</summary>
    [Input(Name = "Height")]
    public Image HeightMap;

    /// <summary>The direction the light is in, in radians: 0 is to the right, π/2 below, and −3π/4 up and to the left.</summary>
    [Input(SoftMinimum = -MathF.PI, SoftMaximum = MathF.PI)]
    public Scalar Angle = -3f * MathF.PI / 4f;

    /// <summary>How high the light stands above the horizon, in radians: π/2 is overhead.</summary>
    [Input(Minimum = 0f, Maximum = MathF.PI / 2f)]
    public Scalar Elevation = MathF.PI / 4f;

    /// <summary>How far a shadow can be cast, in texels at the base resolution.</summary>
    [Input(Minimum = 1f, SoftMaximum = 512f)]
    public Scalar Radius = 64f;

    /// <summary>How tall a height of one is, as a fraction of the image's width. ⚠ Zero casts nothing.</summary>
    [Input(Name = "Height Scale", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar HeightScale = 0.1f;

    /// <summary>The light's angular size in radians, which is how wide the penumbra is. Zero is a hard edge.</summary>
    [Input(Minimum = 0f, SoftMaximum = 0.5f)]
    public Scalar Softness = 0f;

    /// <summary>The shadow.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var height = emitter.ReadGrey("Height");
        var target = emitter.Write("Out", TextureChannels.Grey);

        if (height < 0) {
            return;
        }

        emitter.Dispatch(
            TextureRelief.HeightShadow(
                target,
                height,
                emitter.Number(nameof(Angle)),
                emitter.Number(nameof(Elevation)),
                emitter.Number(nameof(Radius)),
                emitter.Number("Height Scale"),
                emitter.Number(nameof(Softness))
            )
        );
    }
}

/// <summary>One normal map faded into another, as directions.</summary>
/// <remarks>
///     <para>
///         <b>A cross-fade, not a combine</b> — Patina #176's normal mix. At nought the background, at one
///         the foreground, and between them the normalised mix of the two directions. A <c>Colour/Mix</c>
///         of two normal maps shortens every vector it mixes, which a renderer reads as a flatter, bluer
///         surface wherever the mask is grey; <c>Surface/Normal Combine</c> adds a detail map's bumps to a
///         base's and is what layering detail wants instead.
///     </para>
///     <para>
///         <b><c>Mask</c> is optional</b>: wired, it scales <c>Opacity</c> per texel by its red channel.
///     </para>
///     <para>
///         <b>A composite, so no filter layer</b>, as <c>Colour/Mix</c> is none: a stack fades one normal map into
///         another with a normal layer, its mask and a normal blend mode.
///     </para>
/// </remarks>
[Node(
    "Surface/Normal Blend",
    Preview = true,
    Adjustment = false,
    Summary = "One normal map faded into another as directions, by a mask."
)]
sealed partial class NormalBlendNode : TextureNode {
    /// <summary>The normal map where the mask is nought.</summary>
    [Input(Name = "Background")]
    public Image Background;

    /// <summary>The normal map where the mask is one.</summary>
    [Input(Name = "Foreground")]
    public Image Foreground;

    /// <summary>Optional. Scales the opacity per texel; unwired, the opacity holds everywhere.</summary>
    [Input(Name = "Mask")]
    public Image Mask;

    /// <summary>How much of the foreground reaches the result.</summary>
    [Input(Minimum = 0f, Maximum = 1f)]
    public Scalar Opacity = 1f;

    /// <summary>The blended normal map.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var background = emitter.Read("Background", TextureChannels.Colour);
        var foreground = emitter.Read("Foreground", TextureChannels.Colour);
        var masked = Binding.IsConnected("Mask");
        var mask = masked ? emitter.ReadGrey("Mask") : -1;
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (background < 0 || foreground < 0 || (masked && mask < 0)) {
            return;
        }

        emitter.Dispatch(TextureRelief.NormalBlend(target, background, foreground, mask, emitter.Number(nameof(Opacity))));
    }
}
