// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>A height lit from one direction, as a grey about a mid tone.</summary>
/// <remarks>
///     ⚠ <b>An emboss is not a normal map and its output is one channel.</b> It answers "what does
///     this relief look like lit from there", which is a grey; a graph that wants a surface normal
///     wants <c>Height to Normal</c>. ⚠ Its <c>Intensity</c> multiplies a slope taken per unit of
///     <em>image width</em> rather than per texel, which is what makes the same relief the same
///     picture at 1K and at 4K without the evaluator scaling anything — so it is not a
///     <see cref="TextureParameterUnit.TexelsAtBase" />.
/// </remarks>
[Node("Filters/Emboss", Preview = true, Summary = "A height field lit from one direction, as a grey.")]
sealed partial class EmbossNode : TextureNode {
    /// <summary>The height field. A single channel.</summary>
    [Input(Name = "Height")]
    public Image Height;

    /// <summary>Where the light is, in radians from +x towards +y.</summary>
    [Input]
    public Scalar Angle = 0f;

    /// <summary>How far above the surface it is, in radians. A quarter turn flattens the relief.</summary>
    [Input]
    public Scalar Elevation = 0.5f;

    /// <summary>How steep the relief reads. ⚠ 0 is a flat mid grey — a node that looks like it failed.</summary>
    [Input]
    public Scalar Intensity = 0.1f;

    /// <summary>The lit relief.</summary>
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
            TextureFilters.EmbossOp(
                target,
                height,
                emitter.Number(nameof(Angle)),
                emitter.Number(nameof(Elevation)),
                emitter.Number(nameof(Intensity))
            )
        );
    }
}

/// <summary>A displacement by the gradient of a grey field.</summary>
/// <remarks>
///     ⚠ <b>The slope is taken per unit of <em>image width</em>, so a ramp spanning the whole image
///     has a slope of one</b> — which is what makes <c>Intensity</c> readable as "how far a unit of
///     slope pushes", in texels at the base resolution. A warp field of low contrast displaces
///     proportionally less, and that is the number, not a bug.
/// </remarks>
[Node("Filters/Warp", Preview = true, Summary = "A displacement along the gradient of a grey field.")]
sealed partial class WarpNode : TextureNode {
    /// <summary>What to displace.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The field whose gradient pushes it. A single channel.</summary>
    [Input(Name = "Warp")]
    public Image Warp;

    /// <summary>How far a unit of slope displaces, in texels at the base resolution.</summary>
    [Input]
    public Scalar Intensity = 0f;

    /// <summary>The displaced image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var warp = emitter.ReadGrey("Warp");
        var target = emitter.Write("Out");

        if (source < 0 || warp < 0) {
            return;
        }

        emitter.Dispatch(TextureFilters.WarpOp(target, source, warp, emitter.Number(nameof(Intensity))));
    }
}

/// <summary>A displacement along one direction by the value of a grey field.</summary>
/// <remarks>
///     ⚠ <b>Its field is read raw and never centred</b>, which is the difference between this and
///     <c>Vector Warp</c>: a fully lit texel displaces by the whole intensity along the angle and a
///     black one does not move at all, so the picture never displaces backwards. A field an artist
///     expected to be signed will simply push everything one way, which looks like a warp and is the
///     wrong one.
/// </remarks>
[Node("Filters/Directional Warp", Preview = true, Summary = "A displacement along one angle by a grey field's value.")]
sealed partial class DirectionalWarpNode : TextureNode {
    /// <summary>What to displace.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The field whose value pushes it, raw. A single channel.</summary>
    [Input(Name = "Warp")]
    public Image Warp;

    /// <summary>Which way, in radians from +x towards +y.</summary>
    [Input]
    public Scalar Angle = 0f;

    /// <summary>What a fully lit texel displaces by, in texels at the base resolution.</summary>
    [Input]
    public Scalar Intensity = 0f;

    /// <summary>The displaced image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var warp = emitter.ReadGrey("Warp");
        var target = emitter.Write("Out");

        if (source < 0 || warp < 0) {
            return;
        }

        emitter.Dispatch(
            TextureFilters.DirectionalWarpOp(
                target,
                source,
                warp,
                emitter.Number(nameof(Angle)),
                emitter.Number(nameof(Intensity))
            )
        );
    }
}

/// <summary>A displacement by a signed two-channel map.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Red is x and green is y, both <em>biased</em>: a half is rest, one is
///         <c>+intensity</c> and zero is <c>−intensity</c>.</b> Reading the same bytes one-sidedly is
///         half the amplitude and never negative, and it looks entirely plausible.
///     </para>
///     <para>
///         ⚠ <b>Its vector port takes a colour and this node cannot refuse a grey one.</b> A
///         single-channel image has no green, so it displaces every texel by <c>−intensity</c>
///         vertically and by the map horizontally — a diagonal drift under a node that was asked for
///         a warp. <see cref="TextureEmitter.ReadGrey" /> is the strict direction and there is no
///         strict-colour counterpart to call —
///         <a href="https://github.com/Rikarin/Vixen/issues/734">#734</a> — so what a node can say
///         about a port that wants two signed channels, it says here.
///     </para>
/// </remarks>
[Node("Filters/Vector Warp", Preview = true, Summary = "A displacement by a signed two-channel map.")]
sealed partial class VectorWarpNode : TextureNode {
    /// <summary>What to displace.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The displacement map. ⚠ Red is x, green is y, both biased about a half.</summary>
    [Input(Name = "Vectors")]
    public Image Vectors;

    /// <summary>What a fully deflected channel displaces by, in texels at the base resolution.</summary>
    [Input]
    public Scalar Intensity = 0f;

    /// <summary>The displaced image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var vectors = emitter.Read("Vectors");
        var target = emitter.Write("Out");

        if (source < 0 || vectors < 0) {
            return;
        }

        emitter.Dispatch(TextureFilters.VectorWarpOp(target, source, vectors, emitter.Number(nameof(Intensity))));
    }
}

/// <summary>An iterative walk down a slope field, accumulating the source along it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>Intensity</c> is not the distance walked.</b> The path is <c>|∇h| · intensity</c>,
///         so a slope field spanning 0.1 rather than 1.0 walks a tenth as far — the number is the
///         distance only over a field of unit slope. Both this node's kernel and its builder said
///         "the whole distance walked" until it was measured.
///     </para>
///     <para>
///         ⚠ <b><c>Samples</c> changes the answer wherever the field curves, so it is part of the
///         node rather than a quality setting.</b> Turning it down is a different erosion, not a
///         cheaper one — and 0 is a copy.
///     </para>
/// </remarks>
[Node("Filters/Slope Blur", Preview = true, Summary = "An erosion, dilation or mean along a slope field.")]
sealed partial class SlopeBlurNode : TextureNode {
    /// <summary>How the walk accumulates: <c>Blend</c>, <c>Min</c> or <c>Max</c>.</summary>
    [Setting]
    public string Mode = "Blend";

    /// <summary>What the walk samples.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The slope field it walks down. A single channel.</summary>
    [Input(Name = "Slope")]
    public Image Slope;

    /// <summary>How far a unit of slope walks over all the steps together, in texels at the base resolution.</summary>
    [Input]
    public Scalar Intensity = 4f;

    /// <summary>How many steps the walk is cut into. ⚠ 0 is a copy, and the count is not a quality dial.</summary>
    [Input]
    public Int Samples = 8;

    /// <summary>The walked image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var mode = TextureSettings.Enum(emitter, nameof(Mode), TextureSlopeMode.Blend);
        var source = emitter.Read("Input");
        var slope = emitter.ReadGrey("Slope");
        var target = emitter.Write("Out");

        if (source < 0 || slope < 0) {
            return;
        }

        emitter.Dispatch(
            TextureFilters.SlopeBlurOp(
                target,
                source,
                slope,
                emitter.Number(nameof(Intensity)),
                emitter.Integer(nameof(Samples)),
                mode
            )
        );
    }
}
