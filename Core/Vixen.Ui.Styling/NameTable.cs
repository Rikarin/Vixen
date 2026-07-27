// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>Turns the strings a selector and an element share into integers.</summary>
/// <remarks>
///     <para>
///         Matching asks "is this element's class list one of the ones this rule wants" tens of
///         thousands of times a frame, and doing it on strings means a hash and a comparison every
///         time. Interning once at parse time and at element-construction time makes the inner loop
///         integer equality, which is the difference between a matcher that can run over a
///         ten-thousand-row grid and one that cannot.
///     </para>
///     <para>
///         Names are compared ordinally and case-sensitively. That is not the HTML rule, and it is
///         deliberate: VXML's own rule is that a PascalCase tag is a component and a lowercase tag is
///         an intrinsic element ([09](../../docs/plan/09-ui-framework.md)), so case carries meaning
///         and folding it away would make <c>Button</c> and <c>button</c> the same selector.
///     </para>
/// </remarks>
public sealed class NameTable {
    /// <summary>The id no name has.</summary>
    public const int None = -1;

    readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
    readonly List<string> names = [];

    /// <summary>How many distinct names have been interned.</summary>
    public int Count => names.Count;

    /// <summary>Interns a name, adding it if it is new.</summary>
    /// <param name="name">The name.</param>
    /// <returns>Its id.</returns>
    public int Intern(string name) {
        ArgumentNullException.ThrowIfNull(name);

        if (ids.TryGetValue(name, out var id)) {
            return id;
        }

        id = names.Count;
        names.Add(name);
        ids[name] = id;
        return id;
    }

    /// <summary>Looks a name up without interning it.</summary>
    /// <param name="name">The name.</param>
    /// <returns>Its id, or <see cref="None" /> if it has never been seen.</returns>
    /// <remarks>
    ///     What a selector referring to a class no element has ever carried gets. Such a selector can
    ///     never match, and saying so with an id nothing equals is cheaper than saying so with a
    ///     special case.
    /// </remarks>
    public int Lookup(string name) => ids.TryGetValue(name, out var id) ? id : None;

    /// <summary>The name behind an id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The name.</returns>
    public string NameOf(int id) => names[id];
}
