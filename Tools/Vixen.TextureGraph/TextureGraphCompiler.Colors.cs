// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Vixen.NodeGraph;

namespace Vixen.TextureGraph;

/// <summary>Colour knobs: what each scope's are worth, and the expressions that try to compute with one.</summary>
public sealed partial class TextureGraphCompiler {
    /// <summary>The colour knobs of the graph a sub-graph node is written in, as the walk sees it mid-way.</summary>
    /// <param name="scope">The scope the node is written in.</param>
    /// <param name="problems">One sentence per override of that scope that keeps its default.</param>
    /// <remarks>
    ///     ⚠ <b>Followed through <see cref="SubGraphScope.Outer" />, not the expansions table</b>, which the walk
    ///     asking this has not finished writing. A scope with a type and no outer one was made by somebody other than
    ///     the walk, and is read as written in the author's own graph, which is where every such scope used to be.
    /// </remarks>
    IReadOnlyDictionary<string, ImmutableArray<float>> ColorsIn(
        SubGraphScope scope,
        out ImmutableArray<string> problems
    ) {
        if (scope.Type.Length == 0) {
            problems = [];

            return ParameterColors;
        }

        IReadOnlyDictionary<string, ImmutableArray<float>>? outer = null;

        return TextureGraphParameters.Colors(
            ParametersOf(scope.Type),
            scope.Settings,
            name => Lane(outer ??= scope.Outer is { } around ? ColorsIn(around, out _) : ParameterColors, name),
            out problems
        );
    }

    /// <summary>The colour knobs of one expansion once the walk is over, or the author's own for zero.</summary>
    /// <param name="expansion">The expansion.</param>
    /// <param name="problems">One sentence per override of that expansion that keeps its default.</param>
    IReadOnlyDictionary<string, ImmutableArray<float>> ColorsAt(int expansion, out ImmutableArray<string> problems) {
        if (expansion == 0) {
            problems = [];

            return ParameterColors;
        }

        IReadOnlyDictionary<string, ImmutableArray<float>>? outer = null;

        return TextureGraphParameters.Colors(
            ParametersOf(Scope(expansion)),
            Overrides(expansion),
            name => Lane(outer ??= ColorsAt(Parent(expansion), out _), name),
            out problems
        );
    }

    IReadOnlyList<TextureGraphParameter> ParametersOf(string type) =>
        (SubGraphSource as ITextureGraphLibrary)?.ParametersOf(type) ?? [];

    static ImmutableArray<float>? Lane(IReadOnlyDictionary<string, ImmutableArray<float>> colors, string name) =>
        colors.TryGetValue(name, out var lanes) ? lanes : null;

    /// <summary>The colour knob an expression computes with, or null when it names none.</summary>
    /// <param name="colors">The scope's colour knobs.</param>
    /// <param name="text">The expression.</param>
    /// <remarks>
    ///     A colour is four numbers and an expression folds to one, so <c>tint * 0.5</c> has no answer; Raven would be
    ///     handed a name nothing declares and say only that (Patina #272).
    /// </remarks>
    static string? Computed(IReadOnlyDictionary<string, ImmutableArray<float>> colors, string text) {
        foreach (Match name in Identifier().Matches(text)) {
            if (colors.ContainsKey(name.Value)) {
                return name.Value;
            }
        }

        return null;
    }

    /// <summary>A name as Raven spells one, not part of a longer name or a number.</summary>
    [GeneratedRegex(@"(?<![\w.])[A-Za-z_]\w*")]
    private static partial Regex Identifier();
}
