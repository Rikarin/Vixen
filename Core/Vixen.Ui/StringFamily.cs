// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>A set of strings whose ids share a prefix and whose keys the code already has.</summary>
/// <remarks>
///     <para>
///         <b>The shape a declaration class had no way to express.</b> A <see cref="StringId" />
///         property declares one string; a mode that registers a command per tool, per digit or per
///         debug flag has a <em>family</em> of them, and until this existed the only way to write one
///         was at the call site — <c>new StringId("editor.command." + id, label)</c>. That reads
///         fine and is a hole: the id exists only at run time, so it is in no <c>All</c> list, so
///         <see cref="Strings.Template" /> does not export it, so no translator's template contains
///         it and translating that half of a toolset is impossible.
///     </para>
///     <para>
///         ⚠ <b>And it was invisible to the gate written to find exactly that.</b>
///         <c>./build.sh CheckStrings</c> needs a string literal where the id goes, so a
///         concatenation was never in its census — which is why the census could report a ceiling of
///         zero while the ids that genuinely could not be declared were the ones it could not see.
///         The census counts constructions now, and this is the shape that lets a family stop being
///         one.
///     </para>
///     <para>
///         <b>What a call site does with it</b> is index it by the key it already has — a command
///         id, a tool name, a slot number — and pass the answer where it used to pass a construction.
///         The family's <see cref="All" /> goes into the declaration class's <c>All</c> list with a
///         spread, so every member reaches a translator the same way a single declaration does.
///     </para>
///     <para>
///         ⚠ <b><see cref="this" /> throws rather than inventing a string.</b> A key no member covers
///         is a command whose label was never declared; answering with the key would put a dotted id
///         on a menu and answering with an empty string would put nothing there, and both are the
///         quiet failure this type exists to end. It is thrown while a mode registers its commands,
///         which is deterministic and covered by that mode's own tests.
///     </para>
/// </remarks>
public sealed class StringFamily {
    readonly Dictionary<string, StringId> members = new(StringComparer.Ordinal);
    readonly List<StringId> ordered = [];

    /// <summary>Declares a family.</summary>
    /// <param name="prefix">What every id starts with: <c>editor.command.</c>.</param>
    /// <param name="members">Each member's key and the source text it says, in declaration order.</param>
    /// <remarks>
    ///     The key is what a call site has and the id is <paramref name="prefix" /> followed by it,
    ///     which is the convention the call sites were already spelling out by hand.
    /// </remarks>
    public StringFamily(string prefix, IEnumerable<KeyValuePair<string, string>> members) {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(members);

        Prefix = prefix;

        foreach (var (key, source) in members) {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(source);

            var id = new StringId(prefix + key, source);

            // ⚠ Last one wins in the map and the first one keeps its place in the list, so a family
            // written with a duplicated key exports one entry rather than two under one id. A
            // catalogue is a map; two entries would be a translation one of which is unreachable,
            // which is the defect VXS0311 refuses between two declarations.
            if (!this.members.TryAdd(key, id)) {
                continue;
            }

            ordered.Add(id);
        }
    }

    /// <summary>What every one of its ids starts with.</summary>
    public string Prefix { get; }

    /// <summary>Every string it declares, in declaration order.</summary>
    /// <remarks>
    ///     Spread into the declaration class's own <c>All</c> — <c>[.., .. Commands.All]</c> — which
    ///     is what puts a family in a translator's template.
    /// </remarks>
    public IReadOnlyList<StringId> All => ordered;

    /// <summary>The string a key names.</summary>
    /// <param name="key">The key, without <see cref="Prefix" />.</param>
    /// <returns>Its declaration.</returns>
    /// <exception cref="KeyNotFoundException">The family has no such member.</exception>
    public StringId this[string key] {
        get {
            ArgumentNullException.ThrowIfNull(key);

            return members.TryGetValue(key, out var id)
                ? id
                : throw new KeyNotFoundException(
                    $"No member '{key}' in the string family '{Prefix}'. Every string a surface shows "
                    + "is declared, so a key the family does not carry is a word in no translator's "
                    + "template — add it to the declaration rather than building the id here."
                );
        }
    }

    /// <summary>Whether it has a member.</summary>
    /// <param name="key">The key, without <see cref="Prefix" />.</param>
    /// <returns>Whether it declares one.</returns>
    public bool Covers(string key) {
        ArgumentNullException.ThrowIfNull(key);

        return members.ContainsKey(key);
    }
}
