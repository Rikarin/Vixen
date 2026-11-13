// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>Where a height field shadows itself under one light.</summary>
/// <remarks>
///     <para>
///         <b>
///             One where lit, nought where something between the texel and the light stands above the
///             ray to it
///         </b>
///         — Patina #176. A march toward the light keeps the steepest rise, so a wall of
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

/// <summary>A height field pushed out along one direction, every raised shape trailing a side.</summary>
/// <remarks>
///     <para>
///         <b>
///             Each texel takes the highest of the heights behind it along <c>Angle</c>, up to <c>Length</c> away,
///             each lowered by how far back it is
///         </b>
///         (Patina #168's height extrude). At a <c>Falloff</c> of nought a
///         shape is drawn out at its full height, a block's side seen at an angle; at one its side ramps down to
///         nought at the full length, a slope. Nothing ends lower than it was.
///     </para>
///     <para>
///         ⚠ <b>A bounded walk</b>, <c>Analysis/Directional Distance</c>'s: at most 512 taps a texel, so a length
///         longer than 512 texels strides further than one. It wraps on a graph that tiles.
///     </para>
/// </remarks>
[Node(
    "Surface/Height Extrude",
    Preview = true,
    Summary = "Pushes a height field's shapes out along one direction, each trailing a side."
)]
sealed partial class HeightExtrudeNode : TextureNode {
    /// <summary>The height field. A single channel.</summary>
    [Input(Name = "Height")]
    public Image HeightMap;

    /// <summary>The direction the shapes are pushed, in radians: 0 is to the right, π/2 down.</summary>
    [Input(SoftMinimum = -MathF.PI, SoftMaximum = MathF.PI)]
    public Scalar Angle = MathF.PI / 4f;

    /// <summary>How far they are pushed, as a fraction of the image's longer side.</summary>
    [Input(Minimum = 0f, SoftMaximum = 0.5f)]
    public Scalar Length = 0.05f;

    /// <summary>How far the side falls over the length: 0 keeps every shape's height, 1 ramps it to nought.</summary>
    [Input(Minimum = 0f, Maximum = 1f)]
    public Scalar Falloff = 0f;

    /// <summary>The extruded height.</summary>
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
            TextureRelief.HeightExtrude(
                target,
                height,
                emitter.Number(nameof(Angle)),
                emitter.Number(nameof(Length)),
                emitter.Number(nameof(Falloff))
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

        emitter.Dispatch(
            TextureRelief.NormalBlend(target, background, foreground, mask, emitter.Number(nameof(Opacity)))
        );
    }
}

/// <summary>The march the three traced nodes share, each asking it one question.</summary>
/// <remarks>
///     <b>One kernel, three readings</b> — Patina #176's ray-traced occlusion, bent normal and height irradiance are
///     what the same rays found, so they agree with one another texel by texel. <c>Height Scale</c> is
///     <c>Ambient Occlusion</c>'s and <c>Height Shadow</c>'s, so all of them agree about how tall a height of one is.
///     The ports are written out on each node, because a node's ports are read off its own fields.
/// </remarks>
static class HeightRaysCompile {
    /// <summary>Emits one traced node.</summary>
    /// <param name="emitter">The node's emitter.</param>
    /// <param name="binding">The node's binding, which says whether <c>Light</c> is wired.</param>
    /// <param name="reading">What the node asks the rays.</param>
    public static void Emit(TextureEmitter emitter, NodeBinding binding, TextureHeightRays reading) {
        ArgumentNullException.ThrowIfNull(emitter);

        var irradiance = reading == TextureHeightRays.Irradiance;
        var height = emitter.ReadGrey("Height");
        var lit = irradiance && binding.IsConnected("Light");
        var light = lit ? emitter.Read("Light", TextureChannels.Colour) : -1;
        var target = emitter.Write(
            "Out",
            reading == TextureHeightRays.Occlusion ? TextureChannels.Grey : TextureChannels.Colour
        );

        if (height < 0 || (lit && light < 0)) {
            return;
        }

        (float, float, float)? sky = irradiance
            ? (emitter.Number("Sky", 0), emitter.Number("Sky", 1), emitter.Number("Sky", 2))
            : null;

        emitter.Dispatch(
            TextureRelief.HeightRays(
                target,
                height,
                reading,
                light,
                emitter.Number("Radius"),
                emitter.Integer("Samples"),
                emitter.Number("Height Scale"),
                sky
            )
        );
    }
}

/// <summary>How much of the sky each texel of a height field sees, by rays traced over it.</summary>
/// <remarks>
///     <para>
///         <b>The share of a cosine-weighted hemisphere of rays that gets out</b> — Patina #176's ray-traced
///         occlusion. Each ray is marched over the field and asked whether anything stands above it, so a gap under
///         a ledge lets light in where <c>Surface/Ambient Occlusion</c>'s horizon search, which keeps one steepest
///         rise per direction, cannot. Flat ground is exactly one; the foot of a tall wall about a half.
///     </para>
///     <para>
///         ⚠ <b>The same rays at every texel</b>, an even spiral rather than a jitter, so the picture rolls with its
///         input and tiles. Few samples show as soft bands round tall shapes; more smooth them.
///     </para>
/// </remarks>
[Node(
    "Surface/Traced Occlusion",
    Preview = true,
    Summary = "How much of the sky each texel of a height field sees, by rays traced over it."
)]
sealed partial class TracedOcclusionNode : TextureNode {
    /// <summary>The height field. A single channel.</summary>
    [Input(Name = "Height")]
    public Image HeightMap;

    /// <summary>How far a ray is followed, in texels at the base resolution.</summary>
    [Input(Minimum = 1f, SoftMaximum = 128f)]
    public Scalar Radius = 32f;

    /// <summary>How many rays leave each texel. More is smoother and slower; at most 64.</summary>
    [Input(Minimum = 2f, Maximum = 64f)]
    public Int Samples = 32;

    /// <summary>How tall a height of one is, as a fraction of the image's width. ⚠ Zero is flat, and white.</summary>
    [Input(Name = "Height Scale", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar HeightScale = 0.1f;

    /// <summary>The share of the sky each texel sees.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        HeightRaysCompile.Emit(emitter, Binding, TextureHeightRays.Occlusion);
}

/// <summary>The direction a height field's open sky lies in, as a normal map.</summary>
/// <remarks>
///     <para>
///         <b>The mean direction of the rays that reach the sky</b> — Patina #176's bent normal from height. Where
///         nothing occludes it is straight up; beside a wall it leans away, toward the sky a texel can still see. A
///         renderer reads it in place of the normal when it looks up ambient light, which is what keeps a crevice
///         from being lit from the side it faces.
///     </para>
///     <para>
///         <b>Green is <c>Height to Normal</c>'s</b>, <c>v</c> down the image, so the two maps can be compared and
///         combined. A texel whose every ray is blocked keeps straight up.
///     </para>
/// </remarks>
[Node(
    "Surface/Bent Normal",
    Preview = true,
    Adjustment = false,
    Summary = "The direction a height field's open sky lies in, as a normal map."
)]
sealed partial class BentNormalNode : TextureNode {
    /// <summary>The height field. A single channel.</summary>
    [Input(Name = "Height")]
    public Image HeightMap;

    /// <summary>How far a ray is followed, in texels at the base resolution.</summary>
    [Input(Minimum = 1f, SoftMaximum = 128f)]
    public Scalar Radius = 32f;

    /// <summary>How many rays leave each texel. More is smoother and slower; at most 64.</summary>
    [Input(Minimum = 2f, Maximum = 64f)]
    public Int Samples = 32;

    /// <summary>How tall a height of one is, as a fraction of the image's width. ⚠ Zero is flat, and straight up.</summary>
    [Input(Name = "Height Scale", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar HeightScale = 0.1f;

    /// <summary>The bent normal map.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        HeightRaysCompile.Emit(emitter, Binding, TextureHeightRays.BentNormal);
}

/// <summary>The light a height field gathers from a sky and from its own lit neighbours.</summary>
/// <remarks>
///     <para>
///         <b>The colour the rays bring back</b> — Patina #176's height irradiance. A ray that reaches the sky
///         brings <c>Sky</c>; one that strikes the field brings <c>Light</c>'s colour where it struck, the light a
///         lit wall or a glowing seam throws across to the ground beside it. With a white sky and nothing wired to
///         <c>Light</c> it is <c>Traced Occlusion</c> in grey.
///     </para>
///     <para>
///         <b><c>Light</c> is optional</b>: unwired, a blocked ray brings back black. Wire a base colour lit by a
///         <c>Height Shadow</c>, or an emissive map, to have the field bounce it.
///     </para>
/// </remarks>
[Node(
    "Surface/Irradiance",
    Preview = true,
    Adjustment = false,
    Summary = "The light a height field gathers from a sky and from its own lit neighbours."
)]
sealed partial class IrradianceNode : TextureNode {
    /// <summary>The height field. A single channel.</summary>
    [Input(Name = "Height")]
    public Image HeightMap;

    /// <summary>Optional. What a ray that strikes the field brings back; unwired, black.</summary>
    [Input(Name = "Light")]
    public Image Light;

    /// <summary>What a ray that reaches the sky brings back.</summary>
    [Input(Name = "Sky", Default = [1f, 1f, 1f, 1f])]
    public Float4 Sky;

    /// <summary>How far a ray is followed, in texels at the base resolution.</summary>
    [Input(Minimum = 1f, SoftMaximum = 128f)]
    public Scalar Radius = 32f;

    /// <summary>How many rays leave each texel. More is smoother and slower; at most 64.</summary>
    [Input(Minimum = 2f, Maximum = 64f)]
    public Int Samples = 32;

    /// <summary>How tall a height of one is, as a fraction of the image's width. ⚠ Zero is flat, and all sky.</summary>
    [Input(Name = "Height Scale", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar HeightScale = 0.1f;

    /// <summary>The gathered light.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        HeightRaysCompile.Emit(emitter, Binding, TextureHeightRays.Irradiance);
}
