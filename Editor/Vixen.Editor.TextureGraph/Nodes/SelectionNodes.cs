// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>One colour of an image as a mask — § D12's <c>id</c> bake with one island picked out.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § M8's colour / ID selection mask, and the node an artist reaches for to say
///         "just the bolts".</b> <c>Source/Mesh Map</c> already binds the <c>id</c> map and already
///         samples it nearest; what was missing was the node that turns that picture of arbitrary
///         colours into a mask, so until now the map was readable and unusable.
///     </para>
///     <para>
///         ⚠ <b>The setting is a <em>colour</em> and not an id, and that is the one decision in this
///         node.</b> An id map does not contain an id: <c>MapBaker.IdColour</c> paints island
///         <c>n</c> with the hue <c>frac(n · φ)</c> and <c>MeshMapBake.Identifiers</c> writes that
///         colour into the PNG, so the index never reaches a graph and there is nothing to type a
///         number against. The alternative — a free-text "which id" field — is the failure this
///         workstream keeps producing: an artist cannot know the number without eyedropping the bake
///         anyway, and having eyedropped it they would then be typing in a number that names the
///         colour they already have. A colour is what they have and a colour is what this takes.
///     </para>
///     <para>
///         ⚠ <b>And the plugin could not have been asked instead.</b> "Which ids did the bake
///         produce" is a question about a <em>mesh</em>, and <c>MeshMapInputNode</c>'s own remarks
///         say at length that a graph names no mesh and must not: the compiled plan is the same plan
///         for every mesh and the mesh enters at the evaluation. So a dropdown of this bake's ids
///         cannot exist at the point the setting is edited — not because nothing offers one, but
///         because the graph being edited is not yet about any particular bake.
///         <c>SettingDefinition.Accepted</c> answers a fixed vocabulary, which the nine usages are
///         and a mesh's island count is not.
///     </para>
///     <para>
///         ⚠ <b>A tolerance over an index map is nominal, not ordinal, and the kernel is written in
///         the only space where that is true.</b> Two islands one index apart are a golden angle
///         apart in hue — the palette is built that way on purpose — so a comparison written against
///         a decoded index with a <c>±</c> would select an island that merely happens to be numbered
///         next door. The comparison is a Euclidean distance in linear RGB, and
///         <c>TextureSelectionDeviceTests</c> fixes both directions of it: ids 0 and 1, whose colours
///         are far apart, and ids 0 and 89, whose colours are a hundredth of a turn apart.
///     </para>
///     <para>
///         ⚠ <b>What the default tolerance costs, because an artist cannot see it.</b> The nearest
///         colour to island <c>n</c> belongs to a Fibonacci-distant island, so the palette's own
///         resolution runs out: 0 and 89 are about 0.019 apart in linear RGB. The default of 0.02
///         separates the first eighty-nine islands and a mesh with more parts than that needs a
///         smaller one. That is a property of the bake's palette rather than of this node.
///     </para>
/// </remarks>
[Node(
    "Analysis/Colour Select",
    Preview = true,
    Summary = "One colour of an image as a mask — the id bake's islands, picked by colour rather than by number."
)]
sealed partial class ColourSelectNode : TextureNode {
    /// <summary>The picture to select out of. Read nearest, never interpolated.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The colour to match, linear. Its alpha is not read.</summary>
    /// <remarks>
    ///     ⚠ <b>Alpha is deliberately not part of the distance.</b> A baked map's alpha is the bake's
    ///     gutter — inside the chart or outside it — so folding it into the match would make every
    ///     selection also a coverage test, and a colour picked off a fully opaque region would then
    ///     never match a texel the dilation had written.
    /// </remarks>
    [Input(Name = "Colour", Default = [1f, 0f, 0f, 1f])]
    public Float4 Colour;

    /// <summary>How far from that colour still counts, as a distance in linear RGB.</summary>
    [Input]
    public Scalar Tolerance = 0.02f;

    /// <summary>How much further the mask takes to reach zero. 0 is a hard step.</summary>
    [Input]
    public Scalar Softness = 0f;

    /// <summary>The mask.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        // ⚠ `Read` rather than `ReadGrey`: the whole point is a colour, and a grey input is a
        // legitimate — if unusual — thing to select one value out of, which the compiler's own splat
        // makes work without a second node.
        var source = emitter.Read("Input");

        // Grey, because a mask is one channel. A colour output here would be three copies of the
        // same number and a `Colour/Blend` mask port that had to grey it again.
        var target = emitter.Write("Out", TextureChannels.Grey);

        if (source < 0) {
            return;
        }

        emitter.Dispatch(
            TextureSelections.ColourSelect(
                target,
                source,
                emitter.Number("Colour", 0),
                emitter.Number("Colour", 1),
                emitter.Number("Colour", 2),
                emitter.Number(nameof(Tolerance)),
                emitter.Number(nameof(Softness))
            )
        );
    }
}
