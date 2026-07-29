// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.NodeGraph;

/// <summary>What a search will accept: a node type with a port a dragged wire could land on.</summary>
/// <param name="Kind">What the wire carries, or what the port it left wants.</param>
/// <param name="Direction">Which way the port the search is looking for faces.</param>
/// <remarks>
///     <para>
///         <b>The direction is the one being looked <i>for</i>, not the one dragged from.</b> A wire
///         dragged off an output is looking for an input, so a drag from an output produces a filter
///         whose <see cref="Direction" /> is <see cref="PortDirection.Input" />. Naming it after the
///         far end is what stops every call site from having to remember to invert it.
///     </para>
///     <para>
///         <see cref="PortKind.Dynamic" /> accepts every vector, which is what makes a
///         <c>Lerp</c> offered for a colour as well as for a float — the same rule
///         <see cref="PortKinds.Accepts" /> applies to a wire that has already been made.
///     </para>
/// </remarks>
public readonly record struct PortFilter(PortKind Kind, PortDirection Direction) {
    /// <summary>Whether a port would take, or produce, what the filter is looking for.</summary>
    /// <param name="port">The candidate port.</param>
    /// <returns><see langword="true" /> if a wire could be made.</returns>
    public bool Accepts(PortDefinition port) {
        ArgumentNullException.ThrowIfNull(port);

        if (port.Direction != Direction) {
            return false;
        }

        var (source, target) = Direction == PortDirection.Input ? (Kind, port.Kind) : (port.Kind, Kind);

        // ⚠ A dynamic port takes any vector, and only a vector. It is not a wildcard: it resolves to
        // a width, and there is no width a texture and a float agree on — which is exactly what the
        // compiler reports as a type error, so offering the node here would offer a wire that is
        // refused the moment it is compiled.
        if (source == PortKind.Dynamic) {
            return target == PortKind.Dynamic || PortKinds.IsVector(target);
        }

        return target == PortKind.Dynamic ? PortKinds.IsVector(source) : PortKinds.Accepts(source, target);
    }
}

/// <summary>One thing the create search found.</summary>
/// <param name="Type">The node type.</param>
/// <param name="Port">
///     The port a dragged wire would land on, or empty when the search was not from a wire.
/// </param>
/// <param name="Score">How well it matched. Higher is better.</param>
public readonly record struct NodeSearchResult(NodeTypeDefinition Type, string Port, int Score);

/// <summary>
///     Ranking a node library against what somebody typed, and against the wire they dragged.
/// </summary>
/// <remarks>
///     <para>
///         <b>Ranked rather than filtered, and the two are different features.</b> A menu that hid
///         everything not matching makes an author who typed <c>lerp</c> and meant <c>Mix</c> find
///         nothing; one that ranks shows the best guess first and the rest under it. Only a query that
///         matches nothing at all produces nothing.
///     </para>
///     <para>
///         <b>The score is a small integer with a stated ladder</b> rather than a fuzzy distance,
///         because what an author expects from a create menu is boring: an exact title first, then
///         titles that start with what was typed, then titles that contain it, then the category, then
///         the summary. A subsequence matcher — which the command palette does use, because a command
///         is looked up by a phrase — puts <c>Sample Texture 2D</c> above <c>Sample</c> for the query
///         <c>st</c>, which reads as a shuffle.
///     </para>
///     <para>
///         ⚠ <b>Ties break on the path, ordinally.</b> Two node types that match equally well must
///         come out in the same order every time or the item under the cursor moves between
///         keystrokes that changed nothing.
///     </para>
/// </remarks>
public static class NodeSearch {
    /// <summary>The score an exact title match gets.</summary>
    public const int Exact = 1000;

    /// <summary>A title that starts with the query.</summary>
    public const int Prefix = 800;

    /// <summary>A word of the title that starts with it.</summary>
    public const int WordPrefix = 600;

    /// <summary>A title that contains it anywhere.</summary>
    public const int Contains = 400;

    /// <summary>A category that contains it.</summary>
    public const int Category = 200;

    /// <summary>A summary that contains it.</summary>
    public const int Summary = 100;

    /// <summary>Ranks a library against a query.</summary>
    /// <param name="registry">The node library.</param>
    /// <param name="query">What was typed. Empty offers everything.</param>
    /// <param name="filter">The wire that was dragged, when the search came from one.</param>
    /// <param name="limit">How many results to return, or zero for all of them.</param>
    /// <returns>The matches, best first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry" /> is null.</exception>
    public static ImmutableArray<NodeSearchResult> Rank(
        NodeTypeRegistry registry,
        string? query,
        PortFilter? filter = null,
        int limit = 0
    ) {
        ArgumentNullException.ThrowIfNull(registry);

        var text = query?.Trim() ?? "";
        List<NodeSearchResult> found = [];

        foreach (var definition in registry.Types) {
            var port = "";

            if (filter is { } wanted) {
                if (Landing(definition, wanted) is not { } landing) {
                    continue;
                }

                port = landing;
            }

            var score = Score(definition, text);

            if (score > 0) {
                found.Add(new(definition, port, score));
            }
        }

        found.Sort(static (left, right) =>
            left.Score != right.Score
                ? right.Score - left.Score
                : string.CompareOrdinal(left.Type.Path, right.Type.Path));

        return limit > 0 && found.Count > limit ? [.. found.Take(limit)] : [.. found];
    }

    /// <summary>The first port of a type a dragged wire could land on.</summary>
    /// <param name="definition">The node type.</param>
    /// <param name="filter">What the wire is looking for.</param>
    /// <returns>The port's name, or null when the type has none that would take it.</returns>
    /// <remarks>
    ///     The <i>first</i>, in declaration order, because that is the one an author means by dropping
    ///     a wire on a node they have just created — and because the generator already orders ports
    ///     the way the node is drawn, so "first" is "topmost" rather than an accident of a dictionary.
    /// </remarks>
    public static string? Landing(NodeTypeDefinition definition, PortFilter filter) {
        ArgumentNullException.ThrowIfNull(definition);

        foreach (var port in definition.Ports) {
            if (filter.Accepts(port)) {
                return port.Name;
            }
        }

        return null;
    }

    /// <summary>How well one node type matches a query.</summary>
    /// <returns>Its score, or zero when it does not match at all.</returns>
    static int Score(NodeTypeDefinition definition, string query) {
        if (query.Length == 0) {
            // Everything scores the same, so the tiebreak decides — which puts the whole library in
            // path order, which is the order a create menu is read in.
            return 1;
        }

        var title = definition.Title;

        if (string.Equals(title, query, StringComparison.OrdinalIgnoreCase)) {
            return Exact;
        }

        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) {
            // Shorter is better among prefix matches: `Sin` beats `Single Channel` for `si`, because
            // the shorter one is more nearly what was typed.
            return Prefix + Math.Max(0, 64 - title.Length);
        }

        if (StartsAWord(title, query)) {
            return WordPrefix + Math.Max(0, 64 - title.Length);
        }

        if (title.Contains(query, StringComparison.OrdinalIgnoreCase)) {
            return Contains + Math.Max(0, 64 - title.Length);
        }

        if (definition.Category.Contains(query, StringComparison.OrdinalIgnoreCase)) {
            return Category;
        }

        return definition.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ? Summary : 0;
    }

    /// <summary>Whether the query starts one of the words of a title.</summary>
    /// <remarks>
    ///     What makes <c>2d</c> find <c>Sample Texture 2D</c>. Words are split on spaces rather than
    ///     on case changes, because a node title is written by a person and already has the spaces in
    ///     it — splitting <c>UV</c> into <c>U</c> and <c>V</c> would help nobody.
    /// </remarks>
    static bool StartsAWord(string title, string query) {
        var start = 0;

        while (start < title.Length) {
            var space = title.IndexOf(' ', start);
            var word = space < 0 ? title[start..] : title[start..space];

            if (word.StartsWith(query, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }

            if (space < 0) {
                return false;
            }

            start = space + 1;
        }

        return false;
    }
}
