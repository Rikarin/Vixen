// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;

namespace Vixen.Editor.Texturing.Layers;

/// <summary>Which layers a splat map weighs, and the set that resolves one layer's coverage.</summary>
/// <remarks>
///     <para>
///         <b>The layer-list half of <a href="https://github.com/Rikarin/Vixen/issues/1124">#1124</a>.</b>
///         A splat map's channel <c>i</c> is layer <c>i</c> of one material's layer list, which is
///         the reason no texture graph can produce one
///         (<a href="https://github.com/Rikarin/Vixen/issues/1118">#1118</a>) and the reason the verb
///         lives here: the layer stack is the only thing in the editor that <em>has</em> a layer list.
///     </para>
///     <para>
///         ⚠ <b>Nothing here composites anything.</b> The coverage of one layer is answered by the
///         ordinary stack build under <c>LayerStackGraph.Weights</c> — the same masks, the same mask
///         effects, the same opacity folding and the same <c>Blend</c> kernel a picture goes through —
///         and the arithmetic that turns the answers into channels is <c>MaterialBake.Splat</c>'s.
///         What this file contributes is the two decisions in between: which layers are the material's
///         layers, and what a set that asks for one layer's coverage looks like.
///     </para>
/// </remarks>
static class LayerStackSplat {
    /// <summary>How many layers one splat map can weigh, which is how many channels it has.</summary>
    /// <remarks>
    ///     Four, and it is a fact about a texture rather than a limit somebody chose — see
    ///     <c>TexturedMaterialLayersFeature.PaintedChannels</c>, whose own default is three because a
    ///     one- or three-channel texture samples alpha as 1.
    /// </remarks>
    public const int MaxLayers = 4;

    /// <summary>The usage the coverage set writes, which is the one that binds to no material feature.</summary>
    /// <remarks>
    ///     ⚠ <b><c>mask</c> rather than a usage of its own</b>, because a coverage is exactly what
    ///     <c>MaterialMapUsage.Mask</c> already means — "a mask this graph produced for
    ///     another graph, or for a layer stack" — and it is read back in this process rather than
    ///     written to a file. Adding a usage would have to be added to <c>TextureUsages.Known</c> in
    ///     another assembly as well, which is the drift
    ///     <c>MaterialBakeRouteDeviceTests.Every_usage_an_output_node_accepts_is_one_the_baker_writes</c>
    ///     exists to catch — for a value no <c>Output</c> node would ever carry.
    /// </remarks>
    public const string Usage = "mask";

    /// <summary>The layers a splat map for this set would weigh, bottom first.</summary>
    /// <param name="set">The texture set.</param>
    /// <returns>The layers, in the order their channels go.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The top level only, and a group counts as one layer.</b> A group's children are
    ///         one thing an artist arranged to act as one — the group's own mask and opacity apply to
    ///         all of them — and flattening it would put four channels into a stack the panel shows as
    ///         two rows. It is also what makes the count an artist can see match the count the
    ///         material lists.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Bottom first, because that is both orders at once.</b>
    ///         <c>TextureSetAsset.Layers</c> is stored bottom first and
    ///         <c>TexturedMaterialLayersFeature.Layers</c> is innermost first, so index 0 here is the
    ///         base of the stack and the map's red — and a reader who assumed the panel's order, which
    ///         draws the top row first, would write a map whose channels are reversed and whose
    ///         picture is a lit surface of the wrong layers.
    ///     </para>
    ///     <para>
    ///         <b>A disabled layer is not one of them.</b> It contributes nothing to the picture, so a
    ///         channel for it would be zero everywhere and would push a layer the artist can see past
    ///         the fourth channel.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<LayerAsset> Layers(TextureSetAsset set) {
        ArgumentNullException.ThrowIfNull(set);

        var layers = ImmutableArray.CreateBuilder<LayerAsset>();

        foreach (var layer in set.Layers) {
            if (layer.Enabled) {
                layers.Add(layer);
            }
        }

        return layers.ToImmutable();
    }

    /// <summary>What a stack with more layers than channels is told.</summary>
    /// <param name="set">Which set.</param>
    /// <param name="layers">How many it has.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    ///     ⚠ <b>A refusal rather than the first four, and the direction matters.</b> Taking four of
    ///     six writes a map that draws — a surface missing two layers, with the weights of the four
    ///     it kept normalised over a total that is short — and nothing in the frame says which two
    ///     went. The artist's fix is to group layers or to shorten the stack, and neither is one they
    ///     can make after the fact.
    /// </remarks>
    public static string Crowded(TextureSetAsset set, int layers) =>
        $"'{set.Name}' has {layers.ToString(CultureInfo.InvariantCulture)} layers and a splat map has "
        + $"{MaxLayers.ToString(CultureInfo.InvariantCulture)} channels, one per layer. Group the layers that "
        + "belong together — a group is one layer here, with its own mask and opacity — or shorten the stack. "
        + "Writing the first four would draw a surface missing the rest, with the weights it kept normalised "
        + "over a total that is short, and nothing would say so.";

    /// <summary>A set that resolves one layer's coverage and nothing else.</summary>
    /// <param name="set">The set the layer belongs to, for its name and its model.</param>
    /// <param name="layer">The layer.</param>
    /// <returns>The set to compile under <c>LayerStackGraph.Weights</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One channel, starting from opaque black.</b> The base has to be a number the
    ///         compositor can lerp towards, and zero in red is "nothing covers this texel" — where
    ///         starting from transparency would make the first layer's own alpha the group-isolation
    ///         case rather than a coverage, which is <c>LayerStackGraph.Group</c>'s whole #832 story
    ///         arriving where nobody asked for it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The layer composites with <c>Copy</c> whatever its own operator is, and that is a
    ///         decision rather than a simplification.</b> <c>Blend</c> computes
    ///         <c>amount = opacity · mask · alpha</c> and then mixes by the operator: over a black
    ///         backdrop a <c>Multiply</c> layer mixes to black and weighs <em>nothing</em>, however
    ///         much of the texel it covers. The operator says how a layer's colour combines with what
    ///         is under it; how much of the texel it takes is the amount, and the amount is what a
    ///         splat channel is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And its channel enables are cleared, because they name real usages.</b> A layer
    ///         restricted to <c>baseColor</c> writes nothing under a channel called <c>mask</c> —
    ///         <c>LayerAsset.Writes</c> would send the walk straight past it — and a layer that
    ///         contributes to the picture would come back weighing zero everywhere.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>All the way down, and it used to be the top layer only</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1182">#1182</a>. A group's children
    ///         are layers, <c>LayerStackGraph.Composite</c> asks each of them the same question, and
    ///         a group whose children are each restricted to a real usage — which is exactly how an
    ///         artist builds a group that paints base colour from one child and roughness from
    ///         another — had every child turned away, composited nothing onto its backdrop and came
    ///         back weighing <b>zero</b> while painting perfectly in the picture. ⚠ Silently: the
    ///         verb wrote a splat map with one channel at nought and said nothing, and the remaining
    ///         weights were then normalised over a total that is short — the very picture
    ///         <see cref="Crowded" /> refuses to write for the "more than four layers" case.
    ///     </para>
    ///     <para>
    ///         <b>Everything else is the author's and is kept</b>: the mask stack, its effects, the
    ///         opacity, the children of a group and the layer's own kind. Those are what a coverage
    ///         <em>is</em>.
    ///     </para>
    /// </remarks>
    public static TextureSetAsset Coverage(TextureSetAsset set, LayerAsset layer) {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(layer);

        return set with {
            Channels = [new ChannelAsset { Usage = Usage, Default = [0f, 0f, 0f, 1f] }],
            Layers = [Unrestricted(layer) with { Blend = LayerBlendMode.Copy }]
        };
    }

    /// <summary>The layer with its channel enables cleared, and its children's, and theirs.</summary>
    /// <remarks>
    ///     Recursive because <c>Children</c> is the one recursive member of a layer and a group nests:
    ///     clearing one level answers for a group of fills and not for a group of groups, which is the
    ///     same arrangement one row further in a panel.
    /// </remarks>
    static LayerAsset Unrestricted(LayerAsset layer) {
        List<LayerAsset> children = [];

        foreach (var child in layer.Children) {
            children.Add(Unrestricted(child));
        }

        return layer with { Channels = [], Children = children };
    }
}
