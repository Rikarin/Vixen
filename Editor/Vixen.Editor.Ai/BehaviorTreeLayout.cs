// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;

namespace Vixen.Editor.Ai;

/// <summary>How far apart a laid-out tree puts things.</summary>
/// <param name="NodeWidth">How wide a box is, in graph units.</param>
/// <param name="RowHeight">How far apart two levels are.</param>
/// <param name="Gap">The least space between two boxes on one level.</param>
/// <param name="AttachmentHeight">How much each attachment row adds to a box.</param>
/// <remarks>
///     ⚠ <b>The defaults are <see cref="Default" />, not the primary constructor's.</b> A record
///     struct's <c>new()</c> is the <i>zero</i> value — the parameter defaults only apply when
///     somebody names the constructor — so a caller that passed nothing and a caller that wrote
///     <c>new()</c> would get a layout with a row height of zero, which stacks the whole tree on one
///     line and reads as the layout being broken rather than as an empty struct.
/// </remarks>
public readonly record struct BehaviorLayoutOptions(
    float NodeWidth,
    float RowHeight,
    float Gap,
    float AttachmentHeight
) {
    /// <summary>What a tree is laid out with when nobody has said otherwise.</summary>
    public static BehaviorLayoutOptions Default => new(180f, 130f, 24f, 18f);
}

/// <summary>Lays a tree out top-down, parents centred over their children.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The existing <c>NodeGraphLayout</c> is left-to-right by longest path, which is right
///         for dataflow and wrong for a tree</b> — doc 37 § D19 names this as one of the additions the
///         canvas needed. It landed here rather than in the framework because the layout needs the
///         <i>tree</i>, and the tree is <see cref="BehaviorTreeContent" />: a longest-path layout over
///         a projected graph would have to rediscover the parent-child structure it was projected
///         from, and would get the sibling order from the wires rather than from the list that
///         actually holds it.
///     </para>
///     <para>
///         Reingold–Tilford's shape, in its simple form: lay the leaves out left to right in order,
///         then put every parent at the midpoint of its children. That gives the two properties an
///         author reads a tree by — siblings are in priority order left to right, and a parent sits
///         over the branch it owns — and it is one pass down and one pass up.
///     </para>
///     <para>
///         ⚠ <b>It is a command, not something that happens on open.</b> Positions are authored data
///         (doc 37 § D5): a layout somebody spent an afternoon on is thrown away by an editor that
///         re-runs this every time the file is opened, and — worse, if order were derived from
///         position, which here it is not — it would silently change what the agent does.
///     </para>
/// </remarks>
public static class BehaviorTreeLayout {
    /// <summary>Lays out a whole tree, writing each node's position.</summary>
    /// <param name="model">The tree.</param>
    /// <param name="options">How far apart to put things.</param>
    /// <exception cref="ArgumentNullException"><paramref name="model" /> is null.</exception>
    public static void Apply(BehaviorTreeModel model, BehaviorLayoutOptions options = default) {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Content.Root is not { } root) {
            return;
        }

        var settings = options == default ? BehaviorLayoutOptions.Default : options;
        var cursor = 0f;

        Place(root, 0, settings, ref cursor);
        model.Replace(model.Content);
    }

    /// <summary>How tall a node's box is, which the row spacing has to clear.</summary>
    /// <param name="node">The node.</param>
    /// <param name="options">The spacing.</param>
    /// <returns>Its height in graph units.</returns>
    public static float HeightOf(BehaviorNodeContent node, BehaviorLayoutOptions options) {
        ArgumentNullException.ThrowIfNull(node);

        return 40f + ((node.Decorators.Count + node.Services.Count) * options.AttachmentHeight);
    }

    static float Place(BehaviorNodeContent node, int depth, BehaviorLayoutOptions options, ref float cursor) {
        node.Y = depth * options.RowHeight;

        if (node.Children.Count == 0) {
            node.X = cursor;
            cursor += options.NodeWidth + options.Gap;

            return node.X;
        }

        var first = 0f;
        var last = 0f;

        for (var index = 0; index < node.Children.Count; index++) {
            var placed = Place(node.Children[index], depth + 1, options, ref cursor);

            if (index == 0) {
                first = placed;
            }

            last = placed;
        }

        // The midpoint of the first and last child rather than the mean of all of them: a parent of
        // three children whose middle one has a wide subtree should still sit between the outer two,
        // which is where the branch it owns actually is.
        node.X = (first + last) * 0.5f;

        return node.X;
    }
}
