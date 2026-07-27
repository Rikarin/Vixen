// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>The flat arrays every compiled selector points into.</summary>
/// <remarks>
///     A selector is a tree — compounds holding simple selectors, some of which hold whole nested
///     selector lists for <c>:is()</c> and <c>:not()</c>. Storing it as one it would be an object per
///     node, and a stylesheet has thousands of them. Three growable lists and a pair of indices per
///     level says the same thing with no per-node object and with the parts of a selector that are
///     walked together sitting next to each other.
/// </remarks>
public sealed class SelectorTable {
    readonly List<CompoundSelector> compounds = [];
    readonly List<SimpleSelector> simples = [];
    readonly List<Selector> nested = [];

    /// <summary>How many compounds have been recorded.</summary>
    public int CompoundCount => compounds.Count;

    /// <summary>How many simple selectors have been recorded.</summary>
    public int SimpleCount => simples.Count;

    /// <summary>How many nested selectors have been recorded.</summary>
    public int NestedCount => nested.Count;

    internal CompoundSelector Compound(int index) => compounds[index];

    internal SimpleSelector Simple(int index) => simples[index];

    internal Selector Nested(int index) => nested[index];

    internal int AddCompound(CompoundSelector compound) {
        compounds.Add(compound);
        return compounds.Count - 1;
    }

    internal int AddSimple(SimpleSelector simple) {
        simples.Add(simple);
        return simples.Count - 1;
    }

    internal int AddNested(Selector selector) {
        nested.Add(selector);
        return nested.Count - 1;
    }
}
