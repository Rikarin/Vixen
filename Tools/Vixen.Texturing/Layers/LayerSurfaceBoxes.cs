// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Texturing.Layers;

/// <summary>
///     Decals and paths read through the box the bake they read says it spans, rather than the one they were placed with
///     (Patina #133).
/// </summary>
/// <remarks>
///     <para>
///         A decal or a path keeps the box its <c>position</c> map was normalised in beside its numbers, because the
///         numbers are in the model's units and the map is not. That box is whatever the client knew when it placed
///         them, and it goes stale: a model swapped for one with other bounds, a bake from a high mesh whose bounds
///         differ, or a position map written with another range all move the decal by the difference.
///     </para>
///     <para>
///         ⚠ <b>A host that has the bake knows the box exactly</b> — the baker says it beside the map — so it hands
///         that box here and the stored one is only the fallback for a bake that did not say. Nothing in the document
///         changes: the box is a fact about the bake, not about the decal.
///     </para>
/// </remarks>
public static class LayerSurfaceBoxes {
    /// <summary>The stack with every decal's and path's box replaced, or the same stack when none has one to replace.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="box">The box: the least corner's three numbers, then the greatest's.</param>
    /// <returns>The stack read through that box.</returns>
    /// <exception cref="ArgumentException">The box is not six numbers.</exception>
    public static LayerStackAsset Of(LayerStackAsset stack, IReadOnlyList<float> box) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(box);

        if (box.Count != 6) {
            throw new ArgumentException("A box is six numbers, its least corner then its greatest.", nameof(box));
        }

        float[] numbers = [..box];
        var changed = false;
        List<TextureSetAsset> sets = new(stack.Sets.Count);

        foreach (var set in stack.Sets) {
            var layers = Of(set.Layers, numbers, ref changed);

            sets.Add(ReferenceEquals(layers, set.Layers) ? set : set with { Layers = layers });
        }

        return changed ? stack with { Sets = sets } : stack;
    }

    static List<LayerAsset> Of(List<LayerAsset> layers, float[] box, ref bool changed) {
        List<LayerAsset>? rewritten = null;

        for (var index = 0; index < layers.Count; index++) {
            var layer = layers[index];
            var children = Of(layer.Children, box, ref changed);
            var placed = layer with {
                Children = children,
                Decal = layer.Decal is { } decal && !decal.Box.AsSpan().SequenceEqual(box)
                    ? decal with { Box = box }
                    : layer.Decal,
                SurfacePath = layer.SurfacePath is { } path && !path.Box.AsSpan().SequenceEqual(box)
                    ? path with { Box = box }
                    : layer.SurfacePath
            };

            if (!ReferenceEquals(placed.Decal, layer.Decal)
                || !ReferenceEquals(placed.SurfacePath, layer.SurfacePath)
                || !ReferenceEquals(children, layer.Children)) {
                changed = true;
                rewritten ??= [..layers];
                rewritten[index] = placed;
            }
        }

        return rewritten ?? layers;
    }
}
