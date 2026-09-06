// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Texturing.Layers;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>A texture set cut in two around the layer being painted.</summary>
/// <param name="Below">The set holding everything under the painted layer.</param>
/// <param name="Above">The set holding everything over it.</param>
/// <param name="Refusal">Why the cut could not be made, or empty when it was.</param>
readonly record struct PaintSlices(TextureSetAsset? Below, TextureSetAsset? Above, string Refusal) {
    /// <summary>Whether the cut was made.</summary>
    public bool Succeeded => Below is not null && Above is not null;
}

/// <summary>
///     Cuts a texture set into the two halves <see cref="PaintComposite" /> caches.
/// </summary>
/// <remarks>
///     <para>
///         <b>The pure half of the composite's seam, and the reason <see cref="IPaintStack" /> is an
///         interface with one method.</b> Evaluating a slice needs the compiler, a device and a
///         read-back; deciding <em>which layers are in it</em> needs none of those, so it is here,
///         where a test can check it without an adapter.
///     </para>
///     <para>
///         ⚠ <b>A paint layer inside a group is sliced when the group is transparent to the
///         compositor and refused when it is not, and where that line falls is
///         <a href="https://github.com/Rikarin/Vixen/issues/851">#851</a>'s answer.</b> The original
///         refusal said a group is a compositing boundary, which is true of <em>some</em> groups.
///         <c>LayerStackGraph.Group</c> passes a <see cref="LayerBlendMode.Copy" /> group's children
///         straight onto the cursor — "grouping layers under the default mode must not change the
///         picture" — so when such a group also has opacity 1, no mask and is enabled, the flattened
///         list <em>is</em> the composite order, exactly, and the prefix and suffix of that flattened
///         list are the two halves. That covers the reason artists group layers at all: to organise
///         them.
///     </para>
///     <para>
///         ⚠ <b>Any of the five properties failing makes the group a real boundary again, and it
///         still refuses.</b> An isolated group composites its children onto transparency and blends
///         the result back with its own operator; a mask or an opacity applies to that result. Both
///         put an operation between the painted layer and the canvas that no prefix/suffix pair can
///         express — it needs a stack compiled over a backdrop image, a graph with an <c>Input</c>
///         node, which <c>LayerStackGraph</c> does not build. The refusal names which of the five it
///         was, because "move it out of the group" is bad advice when the fix is to set the group's
///         opacity back to one.
///     </para>
///     <para>
///         ⚠ <b>The fifth is <see cref="LayerAsset.Channels" />, and the enumeration asserted itself
///         complete without it —
///         <a href="https://github.com/Rikarin/Vixen/issues/890">#890</a>.</b> The other four are
///         properties of how a group <em>composites</em>; this one is a property of <em>which
///         channels it composites into at all</em>, and <c>LayerStackGraph.Layer</c> applies it to
///         groups exactly as it applies it to leaves. So a group restricted to <c>baseColor</c>
///         contributes nothing to <c>roughness</c> — while a flattened child carries its own
///         <c>Channels</c>, which is empty, which means <em>all</em>. Intersecting the restriction
///         onto each child would fix the two halves and not the layer between them: the painted
///         layer is the live <c>PaintImage</c> that <c>PaintComposite</c> puts between the halves
///         whole, with no channel list to intersect anything onto. So this one refuses too.
///     </para>
///     <para>
///         ⚠ <b>The upper half's channels default to <em>transparency</em> and the lower half's do
///         not, and getting that backwards makes the painted layer invisible.</b>
///         <c>LayerStackGraph</c> starts every channel from <c>ChannelAsset.Default</c>, which for
///         every channel a stack ships with is opaque — so an upper half compiled with the authored
///         defaults produces an opaque picture, and <c>PaintComposite.Over(anything, Above)</c> is
///         then <c>Above</c>. The lower half keeps them, because it really is the bottom of the
///         stack and its default really is what the canvas starts as.
///     </para>
/// </remarks>
static class PaintStackSlices {
    /// <summary>Cuts a set around one of its layers, flattening the transparent groups over it.</summary>
    /// <param name="set">The set.</param>
    /// <param name="layerId">The painted layer's <c>Id</c>.</param>
    /// <returns>The two halves, or a refusal.</returns>
    /// <exception cref="ArgumentNullException">The set is null.</exception>
    public static PaintSlices Split(TextureSetAsset set, string layerId) {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(layerId);

        List<LayerAsset> below = [];
        List<LayerAsset> above = [];

        if (Cut(set.Layers, layerId, below, above) is { } refusal) {
            return new(null, null, refusal);
        }

        if (below.Count == 0 && above.Count == 0 && !Contains(set.Layers, layerId)) {
            return new(
                null,
                null,
                $"The set '{set.Name}' has no layer with the id '{layerId}'. An id is stable across "
                + "renames and reorders, so a missing one is a stale reference rather than a moved layer."
            );
        }

        // ⚠ The painted layer is in neither half. It is the thing between them, and it is the live
        // `PaintImage` — a composite that included it would composite the stroke twice.
        return new(
            set with { Layers = below },
            set with { Layers = above, Channels = [.. set.Channels.Select(Transparent)] },
            ""
        );
    }

    /// <summary>
    ///     Fills the two halves with the flattened layers under and over the painted one.
    /// </summary>
    /// <param name="layers">The layers to look in, bottom first.</param>
    /// <param name="layerId">What is being painted.</param>
    /// <param name="below">Appended what ends up under it.</param>
    /// <param name="above">Appended what ends up over it.</param>
    /// <returns>A refusal, or <see langword="null" /> — which includes "not in this list".</returns>
    /// <remarks>
    ///     ⚠ <b>The suffix is built inside out, which is what makes the flattening the composite
    ///     order rather than a plausible-looking reordering.</b> A layer inside a group composites
    ///     before its group's own siblings do, so the siblings above it inside the group come first
    ///     in the upper half and the layers above the group come after them. Appending the outer ones
    ///     first would put a layer that composites later underneath one that composites earlier — a
    ///     difference invisible on a stack of <c>Copy</c> layers and wrong the moment one is not.
    /// </remarks>
    static string? Cut(List<LayerAsset> layers, string layerId, List<LayerAsset> below, List<LayerAsset> above) {
        for (var index = 0; index < layers.Count; index++) {
            var layer = layers[index];

            if (string.Equals(layer.Id, layerId, StringComparison.Ordinal)) {
                below.AddRange(layers[..index]);
                above.AddRange(layers[(index + 1)..]);

                return null;
            }

            if (!Contains(layer.Children, layerId)) {
                continue;
            }

            // ⚠ `Contains` is kind-agnostic, so a non-group carrying `Children` — which only a
            // hand-edited .vxlayers produces — used to fall past this loop with both halves still
            // empty, and past the missing-id guard, and return *success* with two empty stacks and
            // every other layer silently gone. #892.
            if (layer.Kind != LayerKind.Group) {
                var carrier = layer.Name.Length > 0 ? layer.Name : layer.Id;

                return $"The layer '{carrier}' is a {layer.Kind} and carries '{layerId}' in its Children, "
                    + "which only a Group composites. A stack that nests under anything else did not come "
                    + "from the editor, and there is no composite order to take a prefix and a suffix of. "
                    + "Move the layer to a Group, or to the top level.";
            }

            if (Opaque(layer) is { } why) {
                return why;
            }

            below.AddRange(layers[..index]);

            if (Cut(layer.Children, layerId, below, above) is { } inner) {
                return inner;
            }

            above.AddRange(layers[(index + 1)..]);

            return null;
        }

        return null;
    }

    /// <summary>Why a group is a compositing boundary, or <see langword="null" /> when it is not.</summary>
    static string? Opaque(LayerAsset group) {
        var name = group.Name.Length > 0 ? group.Name : group.Id;

        if (!group.Enabled) {
            return $"The group '{name}' is switched off, so nothing inside it reaches the picture at all. "
                + "Painting into it would show a stroke the bake does not have. Switch the group on.";
        }

        if (group.Blend != LayerBlendMode.Copy) {
            return $"The group '{name}' composites with '{group.Blend}', which isolates it: its children are "
                + "composited onto transparency and the result is blended back with that operator. What sits "
                + "over a paint layer inside it is therefore not a suffix of a list — it needs a stack "
                + "compiled over a backdrop image, which is #851. Set the group to Copy, or move the layer "
                + "out of it.";
        }

        if (group.Opacity < 1f) {
            return $"The group '{name}' is at "
                + $"{(group.Opacity * 100f).ToString("0.#", CultureInfo.InvariantCulture)}% "
                + "opacity, which applies to everything inside it after the fact. That is an operation between "
                + "a paint layer in the group and the canvas that no prefix and suffix can express — #851. "
                + "Set the group to 100%, or move the layer out of it.";
        }

        if (group.Mask.Source != LayerMaskSource.None || group.Mask.Layers.Count > 0) {
            return $"The group '{name}' has a mask, which applies to everything inside it after the fact. "
                + "That is an operation between a paint layer in the group and the canvas that no prefix and "
                + "suffix can express — #851. Take the mask off the group, or move the layer out of it.";
        }

        if (group.Channels.Count > 0) {
            return $"The group '{name}' is restricted to {string.Join(", ", group.Channels)}, and a group's "
                + "channel restriction applies to everything inside it: the compiler gates every layer, "
                + "groups included, on whether it writes the channel being compiled. Flattening its children "
                + "out of it would put them into every other channel too, because a layer's own empty "
                + "channel list means all of them — #890. Clear the group's channel restriction, or move "
                + "the layer out of it.";
        }

        return null;
    }

    /// <summary>The same channel, starting from nothing rather than from its authored default.</summary>
    static ChannelAsset Transparent(ChannelAsset channel) => channel with { Default = [0f, 0f, 0f, 0f] };

    static bool Contains(List<LayerAsset> layers, string layerId) {
        foreach (var layer in layers) {
            if (string.Equals(layer.Id, layerId, StringComparison.Ordinal) || Contains(layer.Children, layerId)) {
                return true;
            }
        }

        return false;
    }
}
