// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>A height field turned into a tangent-space normal.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Green is <c>−∂h/∂v</c> with <c>v</c> pointing <em>down</em> the image, so a height
///         that rises downwards is green below a half.</b> That convention is not this node's choice
///         — it is worked out in <c>HeightToNormal.rvn</c> from the material library's own frame —
///         and it is the defect that survives every review, because a flipped green leaves the
///         lighting plausible. A graph that needs the other convention says so with a
///         <c>Normal Transform</c>.
///     </para>
///     <para>
///         <b>Grey in, colour out, and neither follows the other.</b> The height is measured, so a
///         colour arriving at it is a type error naming the port; the normal is three channels
///         however grey the height was.
///     </para>
/// </remarks>
[Node("Surface/Height to Normal", Preview = true, Summary = "A Sobel gradient turned into a tangent-space normal.")]
sealed partial class HeightToNormalNode : TextureNode {
    /// <summary>The height field. A single channel.</summary>
    [Input(Name = "Height")]
    public Image Height;

    /// <summary>How far the normal is bent. 1 is the height field's true slope.</summary>
    [Input]
    public Scalar Intensity = 1f;

    /// <summary>The Sobel's tap spacing, in texels at the base resolution.</summary>
    [Input]
    public Scalar Width = 1f;

    /// <summary>The normal map.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var height = emitter.ReadGrey("Height");
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (height < 0) {
            return;
        }

        emitter.Dispatch(
            TextureSurfaces.HeightToNormal(target, height, emitter.Number(nameof(Intensity)), emitter.Number(nameof(Width)))
        );
    }
}

/// <summary>Two normal maps combined, the detail reoriented into the base's frame.</summary>
/// <remarks>
///     ⚠ <b>Reoriented, not whiteout — and the two agree exactly where a lazy test looks.</b> On a
///     flat base every formula in the literature returns the detail unchanged, so a comparison
///     against a flat base proves nothing about which one this is. The difference appears only where
///     the base itself is steep, which is where a combine is worth having.
/// </remarks>
[Node("Surface/Normal Combine", Preview = true, Summary = "A detail normal reoriented into a base normal's frame.")]
sealed partial class NormalCombineNode : TextureNode {
    /// <summary>The map whose orientation is kept.</summary>
    [Input(Name = "Base")]
    public Image BaseMap;

    /// <summary>The map rotated into the base's frame.</summary>
    [Input(Name = "Detail")]
    public Image DetailMap;

    /// <summary>How much of the detail is applied. 0 is the base alone.</summary>
    [Input]
    public Scalar Opacity = 1f;

    /// <summary>The combined map.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var baseMap = emitter.Read("Base");
        var detailMap = emitter.Read("Detail");
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (baseMap < 0 || detailMap < 0) {
            return;
        }

        emitter.Dispatch(TextureSurfaces.NormalCombine(target, baseMap, detailMap, emitter.Number(nameof(Opacity))));
    }
}

/// <summary>Flip green, turn the frame, renormalise.</summary>
/// <remarks>
///     <b>The node that reconciles two conventions</b>, and the reason <c>Height to Normal</c> can
///     have exactly one. ⚠ <c>Renormalise</c> defaults on, matching the kernel: a normal read back
///     from an 8-bit file is not quite unit long, and a chain of transforms that never renormalised
///     would drift towards a flatter surface with every step.
/// </remarks>
[Node("Surface/Normal Transform", Preview = true, Summary = "A normal map's green flipped, its frame turned, renormalised.")]
sealed partial class NormalTransformNode : TextureNode {
    /// <summary>The normal map to transform.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>Whether green is negated about a half.</summary>
    [Input(Name = "Flip Green")]
    public Bool FlipGreen = false;

    /// <summary>How far the frame turns, in radians, clockwise on screen.</summary>
    [Input]
    public Scalar Rotation = 0f;

    /// <summary>Whether the result is made unit length.</summary>
    [Input]
    public Bool Renormalise = true;

    /// <summary>The transformed map.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0) {
            return;
        }

        emitter.Dispatch(
            TextureSurfaces.NormalTransform(
                target,
                source,
                emitter.Flag("Flip Green"),
                emitter.Number(nameof(Rotation)),
                emitter.Flag(nameof(Renormalise))
            )
        );
    }
}

/// <summary>The divergence of a normal field, centred on a half.</summary>
/// <remarks>
///     ⚠ <b>Not doc 48 § D12's mesh curvature.</b> This measures the map, so it sees the detail a
///     normal map carries and knows nothing about the shape it is wrapped around; a bake from a
///     high-poly mesh is a different node that does not exist yet. Convex is above a half and concave
///     below it, which is what makes it useful as an edge-wear mask.
/// </remarks>
[Node("Surface/Curvature", Preview = true, Summary = "A normal field's divergence, as a grey about a mid tone.")]
sealed partial class CurvatureNode : TextureNode {
    /// <summary>A tangent-space normal map.</summary>
    [Input(Name = "Normal")]
    public Image Normal;

    /// <summary>The central difference's half-width, in texels at the base resolution.</summary>
    [Input]
    public Scalar Radius = 1f;

    /// <summary>What the divergence is multiplied by before it is centred.</summary>
    [Input]
    public Scalar Intensity = 0.25f;

    /// <summary>The curvature.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var normal = emitter.Read("Normal");
        var target = emitter.Write("Out", TextureChannels.Grey);

        if (normal < 0) {
            return;
        }

        emitter.Dispatch(
            TextureSurfaces.Curvature(target, normal, emitter.Number(nameof(Radius)), emitter.Number(nameof(Intensity)))
        );
    }
}

/// <summary>A horizon search over a height field.</summary>
/// <remarks>
///     ⚠ <b><c>Height</c> — how tall a height of one is, as a fraction of the image's width — is the
///     parameter that makes this node look broken when it is left at zero.</b> A flat surface
///     occludes nothing, so the answer is one everywhere: a white image, which is exactly what a node
///     that never ran produces. The default is a tenth for that reason and not because a tenth is
///     right for any particular material. ⚠ Not doc 48 § D12's mesh bake either.
/// </remarks>
[Node("Surface/Ambient Occlusion", Preview = true, Summary = "A horizon search over a height field.")]
sealed partial class AmbientOcclusionNode : TextureNode {
    /// <summary>The height field. A single channel.</summary>
    [Input(Name = "Height")]
    public Image HeightMap;

    /// <summary>How far each ray marches, in texels at the base resolution.</summary>
    [Input]
    public Scalar Radius = 16f;

    /// <summary>How many directions are searched. Capped by the kernel at sixteen.</summary>
    [Input]
    public Int Samples = 8;

    /// <summary>How tall a height of one is, as a fraction of the image's width. ⚠ Zero is white.</summary>
    [Input(Name = "Height Scale")]
    public Scalar HeightScale = 0.1f;

    /// <summary>The occlusion.</summary>
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
            TextureSurfaces.AmbientOcclusion(
                target,
                height,
                emitter.Number(nameof(Radius)),
                emitter.Integer(nameof(Samples)),
                emitter.Number("Height Scale")
            )
        );
    }
}
