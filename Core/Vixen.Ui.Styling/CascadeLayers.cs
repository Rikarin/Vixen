// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>The cascade layers a stylesheet declared, in the order they were declared.</summary>
/// <remarks>
///     <para>
///         Layers exist so that "which of these two rules wins" can be answered by <i>where the rule
///         lives</i> rather than by how specific its selector happens to be. That is what makes the
///         utility system in [doc 09](../../docs/plan/09-ui-framework.md) work at all: a generated
///         `.p-4` is one class and a hand-written `.card .body` is two, so without layers the
///         utility loses every time and the only fix is `!important` everywhere. With
///         <c>@layer base, components, utilities</c> the answer is settled once, in one line, and
///         specificity never enters into it. That line is Vixen's actual ladder — every theme sheet
///         in the tree opens with it, and <c>docs/guide/ui/cascade-layers.md</c> is what it means.
///     </para>
///     <para>
///         ⚠ <b>A layer only wins if something is in a lower one, which is a less obvious condition
///         than it sounds.</b> The ladder above was declared and emitted into for a release before
///         anything else joined it, and a lone <c>@layer utilities</c> against a tree of unlayered
///         component sheets is strictly <i>worse</i> than no layers at all — the utility loses to a
///         one-tag selector it would have beaten on specificity alone. The mechanism was never wrong;
///         it simply had nobody to argue with.
///     </para>
///     <para>
///         <b>Unlayered styles are not layer zero.</b> They sit <i>above</i> every layer for normal
///         declarations and <i>below</i> every layer for important ones, which is the same reversal
///         importance applies to origins. <see cref="Unlayered" /> is a distinct index rather than a
///         position in the list precisely so that neither direction can be got by accident.
///     </para>
///     <para>
///         Nested layers (<c>@layer a.b</c>) are flattened to their full dotted name. A nested layer
///         orders inside its parent, and because a parent is always declared before anything can
///         nest inside it, declaration order over full names already gives that — no tree needed.
///     </para>
/// </remarks>
public sealed class CascadeLayers {
    /// <summary>The index standing for "not in any layer".</summary>
    public const int Unlayered = -1;

    readonly Dictionary<string, int> indices = new(StringComparer.Ordinal);
    readonly List<string> order = [];

    /// <summary>How many layers have been declared.</summary>
    public int Count => order.Count;

    /// <summary>The layers, in declaration order.</summary>
    public IReadOnlyList<string> Order => order;

    /// <summary>Declares a layer, or returns the index it already has.</summary>
    /// <param name="name">Its full dotted name.</param>
    /// <returns>Its index, which is its position in the cascade.</returns>
    /// <remarks>
    ///     Re-declaring a layer does <i>not</i> move it. A stylesheet that says
    ///     <c>@layer base, theme;</c> and later opens <c>@layer base { … }</c> has added rules to a
    ///     layer whose position was fixed by the first statement, which is the entire reason the
    ///     statement form exists.
    /// </remarks>
    public int Declare(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (indices.TryGetValue(name, out var existing)) {
            return existing;
        }

        // `@layer a.b` implies `a`, and implies it *here* if nothing has declared it yet.
        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0) {
            Declare(name[..lastDot]);
        }

        var index = order.Count;
        order.Add(name);
        indices[name] = index;
        return index;
    }

    /// <summary>Looks a layer up without declaring it.</summary>
    /// <param name="name">Its full dotted name.</param>
    /// <param name="index">Receives its index.</param>
    /// <returns>Whether it has been declared.</returns>
    public bool TryGetIndex(string name, out int index) {
        ArgumentNullException.ThrowIfNull(name);
        return indices.TryGetValue(name, out index);
    }

    /// <summary>Where a layer sorts among normal declarations.</summary>
    /// <param name="layer">A layer index, or <see cref="Unlayered" />.</param>
    /// <returns>A rank that compares the way the cascade needs, higher winning.</returns>
    /// <remarks>
    ///     Later layers beat earlier ones, and unlayered styles beat all of them. The rank is
    ///     computed against <see cref="Count" /> as it stands, so it must be asked after the whole
    ///     stylesheet has loaded — which is also when the cascade runs.
    /// </remarks>
    public int NormalRank(int layer) => layer == Unlayered ? order.Count : layer;

    /// <summary>Where a layer sorts among <c>!important</c> declarations.</summary>
    /// <param name="layer">A layer index, or <see cref="Unlayered" />.</param>
    /// <returns>A rank that compares the way the cascade needs, higher winning.</returns>
    /// <remarks>
    ///     The mirror image of <see cref="NormalRank" />: earlier layers beat later ones, and
    ///     unlayered styles lose to all of them. This is not a quirk — it is what makes a layer
    ///     declared first mean "these are the defaults, and when I insist on one I mean it", which is
    ///     the only reading under which layering a reset stylesheet is useful.
    /// </remarks>
    public int ImportantRank(int layer) => layer == Unlayered ? -1 : order.Count - 1 - layer;

    /// <summary>The name of a layer index, for diagnostics.</summary>
    /// <param name="layer">A layer index, or <see cref="Unlayered" />.</param>
    /// <returns>Its name, or a description of not being in one.</returns>
    public string NameOf(int layer) => layer == Unlayered ? "(unlayered)" : order[layer];
}
