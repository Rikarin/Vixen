// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>A normal map integrated back into the height field it came from.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The one node in doc 48's catalogue that is not a compute dispatch.</b> It is a
///         Poisson solve over doc 42 § B1's conjugate gradient, it runs on the CPU, and
///         <see cref="ITextureCpuOperation" /> carries the argument for why that is an exception to
///         § D3 rather than a precedent. What it costs a graph is not hidden: the bake's command list
///         ends here, the device is waited on, the normal map is copied into host memory and the
///         answer copied back — two full pipeline drains — so a chain of these serialises the whole
///         evaluation. It is worth it once.
///     </para>
///     <para>
///         ⚠ <b>The answer has mean zero and is therefore signed</b>, because a gradient field
///         determines a height only up to a constant and this is the one the input does not contain.
///         An <c>Output</c> wired straight to this writes a map that is half black in eight bits; a
///         <c>Levels</c> between them is what turns it into the <c>[0, 1]</c> height a material
///         wants, and it is the node whose entire job that is.
///     </para>
///     <para>
///         <b><see cref="Iterations" /> is a budget rather than a target</b>, which is the property
///         doc 42 § D5 chose over a residual test so that a bake is byte-identical across platforms.
///         More of it is a better answer and there is no number at which it announces it has
///         finished.
///     </para>
/// </remarks>
[Node(
    "Surface/Normal to Height",
    Preview = true,
    Summary = "A normal map integrated back into a height field, by a Poisson solve on the CPU."
)]
sealed partial class NormalToHeightNode : TextureNode {
    /// <summary>The normal map to integrate. Three channels, encoded <c>n · ½ + ½</c>.</summary>
    [Input(Name = "Normal")]
    public Image Normal;

    /// <summary>How many conjugate-gradient steps to spend. More is closer; nothing is exact.</summary>
    [Input]
    public Scalar Iterations = NormalToHeightOperation.DefaultIterations;

    /// <summary>The <c>intensity</c> the map was authored with, to be undone. 1 for a map from a file.</summary>
    [Input]
    public Scalar Intensity = 1f;

    /// <summary>The height field, mean zero.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var normal = emitter.Read("Normal");

        // ⚠ Grey out of a colour in, which is the reverse of `Height to Normal` and is not the
        // resolved channel kind. A height is one number per texel however many the normals had, and
        // taking `emitter.Resolved` here would allocate three channels of which two are copies.
        var target = emitter.Write("Out", TextureChannels.Grey);

        if (normal < 0) {
            return;
        }

        emitter.Dispatch(
            TextureCpuOperations.NormalToHeight(
                target,
                normal,
                emitter.Number(nameof(Iterations)),
                emitter.Number(nameof(Intensity))
            )
        );
    }
}
