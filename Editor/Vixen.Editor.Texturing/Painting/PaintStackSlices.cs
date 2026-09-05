// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
///         ⚠ <b>A nested paint layer is refused rather than mis-sliced, and that is a real
///         limitation with a real reason.</b> A paint layer inside a group has three things over it
///         and not one: its siblings above it, the group's own mask and opacity, and the layers over
///         the group. Only the first is a suffix of a list. Expressing the other two means compiling
///         a stack whose <em>input</em> is a backdrop image — a stack graph with an <c>Input</c> node
///         — which <c>LayerStackGraph</c> does not build and which is not this slice's file. So the
///         refusal names #851, and the day that is built this method's <c>switch</c> gains a case
///         instead of the artist getting a composite that is subtly wrong under a group.
///     </para>
/// </remarks>
static class PaintStackSlices {
    /// <summary>Cuts a set around one of its top-level layers.</summary>
    /// <param name="set">The set.</param>
    /// <param name="layerId">The painted layer's <c>Id</c>.</param>
    /// <returns>The two halves, or a refusal.</returns>
    /// <exception cref="ArgumentNullException">The set is null.</exception>
    public static PaintSlices Split(TextureSetAsset set, string layerId) {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(layerId);

        var index = -1;

        for (var layer = 0; layer < set.Layers.Count; layer++) {
            if (string.Equals(set.Layers[layer].Id, layerId, StringComparison.Ordinal)) {
                index = layer;

                break;
            }
        }

        if (index < 0) {
            return Nested(set, layerId)
                ? new(
                    null,
                    null,
                    $"The layer '{layerId}' is inside a group. A group's mask and opacity apply to "
                    + "everything under them, so the half of the stack above a nested paint layer is not a "
                    + "suffix of a list — it needs a stack compiled over a backdrop image, which is #851. "
                    + "Move the layer out of the group to paint on it in this build."
                )
                : new(
                    null,
                    null,
                    $"The set '{set.Name}' has no layer with the id '{layerId}'. An id is stable across "
                    + "renames and reorders, so a missing one is a stale reference rather than a moved layer."
                );
        }

        // ⚠ The painted layer is in neither half. It is the thing between them, and it is the live
        // `PaintImage` — a composite that included it would composite the stroke twice.
        return new(
            set with { Layers = [.. set.Layers[..index]] },
            set with { Layers = [.. set.Layers[(index + 1)..]] },
            ""
        );
    }

    static bool Nested(TextureSetAsset set, string layerId) {
        foreach (var layer in set.Layers) {
            if (Contains(layer.Children, layerId)) {
                return true;
            }
        }

        return false;
    }

    static bool Contains(List<LayerAsset> layers, string layerId) {
        foreach (var layer in layers) {
            if (string.Equals(layer.Id, layerId, StringComparison.Ordinal) || Contains(layer.Children, layerId)) {
                return true;
            }
        }

        return false;
    }
}
