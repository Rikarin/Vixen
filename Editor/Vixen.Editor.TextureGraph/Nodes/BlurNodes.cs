// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>A gaussian blur.</summary>
/// <remarks>
///     <para>
///         <b>Two dispatches and a scratch image, for <c>Blur</c>'s reason:</b> the kernel does one
///         axis, and a gaussian is separable, so a sigma of <c>s</c> is <c>2·(3s)+1</c> taps per axis
///         rather than the square of that in one pass. The intermediate is an image this node asks
///         for rather than something the kernel hides, because an image in a plan is written exactly
///         once — which is the invariant that lets <c>TexturePoolSchedule</c> free the scratch the
///         moment the second dispatch has read it.
///     </para>
///     <para>
///         ⚠ <b>Past a sigma of about 21 the kernel clamps and the picture stops being the graph's
///         </b> — <c>TextureFilters.Ceilings</c> holds the number and <c>TexturePlan.Check</c>
///         reports it as a warning against the <em>resolved</em> radius, which is the one that
///         depends on the bake. Nothing is repeated here: a second ceiling in a node is a second
///         thing to keep in step with a shader.
///     </para>
/// </remarks>
[Node("Filters/Blur HQ", Preview = true, Summary = "A separable gaussian, in texels at the base resolution.")]
sealed partial class BlurHqNode : TextureNode {
    /// <summary>What to blur.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The standard deviation, in texels at the graph's base resolution.</summary>
    [Input]
    public Scalar Sigma = 1f;

    /// <summary>The blurred image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var scratch = emitter.Scratch(TextureEmitter.FormatOf(emitter.Resolved));
        var target = emitter.Write("Out");

        if (source < 0) {
            return;
        }

        var sigma = emitter.Number(nameof(Sigma));

        emitter.Dispatch(TextureFilters.BlurHqOp(scratch, source, sigma));
        emitter.Dispatch(TextureFilters.BlurHqOp(target, scratch, sigma, vertical: true));
    }
}

/// <summary>A box smear along one direction.</summary>
/// <remarks>
///     ⚠ <b>Its angle runs from +x <em>towards +y, and +y is down the image</em></b>, which is the
///     convention every angle in this assembly's filter kernels takes. A quarter turn is therefore
///     downwards on screen rather than upwards, and the picture under the opposite guess is a smear
///     that looks entirely correct until it is composited with something that agrees with the
///     convention.
/// </remarks>
[Node("Filters/Directional Blur", Preview = true, Summary = "A box smear along a continuous direction.")]
sealed partial class DirectionalBlurNode : TextureNode {
    /// <summary>What to smear.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The direction, in radians from +x towards +y.</summary>
    [Input]
    public Scalar Angle = 0f;

    /// <summary>How far it reaches each way, in texels at the base resolution.</summary>
    [Input]
    public Scalar Length = 4f;

    /// <summary>The smeared image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var target = emitter.Write("Out");

        if (source < 0) {
            return;
        }

        emitter.Dispatch(
            TextureFilters.DirectionalBlurOp(target, source, emitter.Number(nameof(Angle)), emitter.Number(nameof(Length)))
        );
    }
}

/// <summary>A smear along the ray from a centre.</summary>
/// <remarks>
///     ⚠ <b>Its <c>Amount</c> is a fraction of the distance to the centre and not a length</b>, so it
///     is deliberately <em>not</em> a <see cref="TextureParameterUnit.TexelsAtBase" />: scaling it
///     with the bake would make the same graph a different material at 4K, in the opposite direction
///     from the bug doc 48 § D8 is about. ⚠ And <c>Samples</c> of 1 is a copy at any amount, which is
///     a node that looks like it never ran.
/// </remarks>
[Node("Filters/Radial Blur", Preview = true, Summary = "A smear along the ray from a centre.")]
sealed partial class RadialBlurNode : TextureNode {
    /// <summary>What to smear.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How much of the distance to the centre the samples span. 0 is a copy.</summary>
    [Input]
    public Scalar Amount = 0.2f;

    /// <summary>Where the rays meet along x, in 0..1 of the image.</summary>
    [Input(Name = "Centre X")]
    public Scalar CentreX = 0.5f;

    /// <summary>Where they meet along y.</summary>
    [Input(Name = "Centre Y")]
    public Scalar CentreY = 0.5f;

    /// <summary>How many samples the span is cut into. ⚠ 1 is a copy at any amount.</summary>
    [Input]
    public Int Samples = 16;

    /// <summary>The smeared image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var target = emitter.Write("Out");

        if (source < 0) {
            return;
        }

        emitter.Dispatch(
            TextureFilters.RadialBlurOp(
                target,
                source,
                emitter.Number(nameof(Amount)),
                emitter.Number("Centre X"),
                emitter.Number("Centre Y"),
                emitter.Integer(nameof(Samples))
            )
        );
    }
}

/// <summary>A box whose radius is read from a map, per texel.</summary>
/// <remarks>
///     ⚠ <b>The radius map is <em>measured</em> rather than composited, so a colour arriving at it is
///     a type error naming the port.</b> The kernel reads its red channel and nothing else; a colour
///     silently reduced to red would be a blur whose width came from whichever channel the artist
///     happened not to be thinking about. A graph that means a colour's luminance says so with a
///     <c>Grayscale</c> node.
/// </remarks>
[Node("Filters/Non-Uniform Blur", Preview = true, Summary = "A box blur whose radius is read from a map.")]
sealed partial class NonUniformBlurNode : TextureNode {
    /// <summary>What to blur.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The map whose value scales the radius. A single channel.</summary>
    [Input(Name = "Radius Map")]
    public Image RadiusMap;

    /// <summary>What a fully lit texel is worth, in texels at the base resolution.</summary>
    [Input(Name = "Max Radius")]
    public Scalar MaxRadius = 4f;

    /// <summary>The blurred image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var radiusMap = emitter.ReadGrey("Radius Map");
        var target = emitter.Write("Out");

        if (source < 0 || radiusMap < 0) {
            return;
        }

        emitter.Dispatch(TextureFilters.NonUniformBlurOp(target, source, radiusMap, emitter.Number("Max Radius")));
    }
}

/// <summary>An unsharp mask.</summary>
/// <remarks>
///     <b>Its <c>Amount</c> is a ratio and its <c>Radius</c> is a length</b>, which is the whole of
///     the § D8 decision for this node: the radius scales with the bake so that a 1K graph sharpens
///     the same detail at 4K, and the amount does not, because multiplying a difference by four
///     is not the same picture.
/// </remarks>
[Node("Filters/Sharpen", Preview = true, Summary = "An unsharp mask: a box subtracted and added back.")]
sealed partial class SharpenNode : TextureNode {
    /// <summary>What to sharpen.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How much of the difference is added back. 0 is a copy.</summary>
    [Input]
    public Scalar Amount = 1f;

    /// <summary>The half-width of the subtracted box, in texels at the base resolution.</summary>
    [Input]
    public Scalar Radius = 1f;

    /// <summary>The sharpened image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var target = emitter.Write("Out");

        if (source < 0) {
            return;
        }

        emitter.Dispatch(
            TextureFilters.SharpenOp(target, source, emitter.Number(nameof(Amount)), emitter.Number(nameof(Radius)))
        );
    }
}
