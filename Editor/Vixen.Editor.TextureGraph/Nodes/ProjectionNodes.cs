// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>A picture put onto a mesh by where it is in the world rather than by its UVs.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D10's layer projection, as a node</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/815">#815</a>. <c>LayerProjection</c> has
///         had <c>Triplanar</c> and <c>Planar</c> as members since M7 and <c>LayerStackGraph</c>
///         refused both, because a projection needs a node that samples an image by a projected world
///         position and there was none. This is that node; the stack's own wiring is
///         <c>LayerStackGraph.Project</c>.
///     </para>
///     <para>
///         ⚠ <b>Why it is worth having at all, stated because it is easy to read as a novelty.</b> A
///         triplanar fill is what makes a smart material work on a mesh whose UVs it was never
///         authored for — the whole premise of a shipped material library. Without it every
///         <c>.vxsmartmat</c> is a material for one atlas.
///     </para>
///     <para>
///         ⚠ <b>Two mesh maps, both required, and they are two different encodings.</b>
///         <c>Position</c> is § D12's <c>position</c> bake and is <em>unsigned</em> — 0..1 across the
///         mesh's own bounds, needing no decode; <c>Normal</c> is the <c>world</c> bake and is
///         <em>signed</em>, <c>0.5 + 0.5·n</c>. Wiring the tangent-space <c>normal</c> map into
///         <c>Normal</c> instead of the world one compiles, bakes, and produces a picture whose
///         planes are chosen by the surface detail rather than by which way the surface faces — which
///         looks like a noisy blend and not like a wrong input.
///     </para>
///     <para>
///         <b>Both mesh-map ports are ordinary image inputs rather than something this node fetches
///         itself</b>, which is <c>Source/Mesh Map</c>'s design and not a shortcut: a graph names no
///         mesh, the bake decides which mesh a <c>meshmap:position</c> resolves to, and a node that
///         reached for a map directly would be a second path to the same external table.
///     </para>
/// </remarks>
[Node(
    "Space/Triplanar",
    Preview = true,
    Summary = "A picture projected onto a mesh by its world position, blended by the world normal."
)]
sealed partial class TriplanarNode : TextureNode {
    /// <summary>What the axis setting is called, which is what a picker reads it back off by.</summary>
    public const string Setting = "Axis";

    /// <summary>The picture to project. Wrapped, so it tiles across the mesh.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>§ D12's <c>position</c> bake. ⚠ The world one, not the atlas's UVs.</summary>
    [Input(Name = "Position")]
    public Image Position;

    /// <summary>§ D12's <c>world</c> bake. ⚠ The world normal, not the tangent-space one.</summary>
    [Input(Name = "Normal")]
    public Image Normal;

    /// <summary>How many times the picture repeats across the position range.</summary>
    [Input]
    public Scalar Scale = 1f;

    /// <summary>How hard the seam between two planes is. Ignored by a single-axis projection.</summary>
    [Input]
    public Scalar Sharpness = 4f;

    /// <summary>Which projection: all three planes blended, or one alone.</summary>
    /// <remarks>
    ///     ⚠ <b>The accepted values are here and nowhere else</b> — the <c>#964</c> rule. A picker,
    ///     the refusal <c>TextureSettings.Enum</c> writes and the inspector's dropdown all read this
    ///     one declaration, so there is no second list of the four to fall out of step with
    ///     <c>TextureProjectionAxis</c>.
    /// </remarks>
    [Setting(
        Name = Setting,
        Summary = "Triplanar blends the three planes by the world normal; X, Y and Z each take one alone.",
        Accepted = ["Triplanar", "X", "Y", "Z"]
    )]
    public string Axis = "Triplanar";

    /// <summary>The projected picture, in the atlas.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var position = emitter.Read("Position");
        var normal = emitter.Read("Normal");

        // Colour, because a projection is a resampling of the picture it was given and the two mesh
        // maps are three-channel measurements whatever the source was. A grey source arrives splatted
        // by the compiler's own promotion, which is § Part 4's rule and costs one shared op.
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0 || position < 0 || normal < 0) {
            return;
        }

        emitter.Dispatch(
            TextureProjections.Triplanar(
                target,
                source,
                position,
                normal,
                emitter.Number(nameof(Scale)),
                emitter.Number(nameof(Sharpness)),
                TextureSettings.Enum(emitter, Setting, TextureProjectionAxis.Triplanar)
            )
        );
    }
}
