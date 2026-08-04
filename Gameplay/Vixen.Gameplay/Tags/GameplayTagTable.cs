// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay;

/// <summary>The baked tag tree: every tag a build knows, numbered so that a prefix test is a range test.</summary>
/// <remarks>
///     <para>
///         Built once, by <see cref="GameplayTagTableBuilder" />, out of every tag every definition in
///         the content build mentions. Immutable afterwards, shared by every system, and identical on
///         every machine that holds the same content — which is the property the wire depends on.
///     </para>
///     <para>
///         <b>Numbering is a pre-order walk with siblings in ordinal order</b>, so a tag's descendants
///         occupy the contiguous half-open range <c>[index, end)</c> and
///         <see cref="GameplayTagRange.Contains" /> is two comparisons. It is also a pure function of
///         the <em>set</em> of names, not of the order they were added — asserted, because the
///         alternative is a table that depends on which file an importer read first and therefore on
///         a directory listing.
///     </para>
///     <para>
///         ⚠ <b>A pure function of the set means adding a tag renumbers the ones after it.</b> That is
///         the price of the range test and it is paid in one place: an index is valid against one
///         table and nowhere else. Compare <see cref="BuildHash" /> in the session handshake, and
///         persist <see cref="SymbolOf" /> rather than an index — see <see cref="GameplayTag" />.
///     </para>
/// </remarks>
public sealed class GameplayTagTable {
    /// <summary>How deep a tag may be. Refused above this, rather than overflowing a walk.</summary>
    /// <remarks>
    ///     ⚠ <b>A limit, because the walk is recursive and the input is content.</b> Thirty-two is
    ///     four times the deepest tag anybody has written down and small enough that a malformed
    ///     <c>.vxdef</c> — a name that is a thousand dots — is a build error rather than a stack
    ///     overflow in whatever process read it.
    /// </remarks>
    public const int MaximumDepth = 32;

    readonly string[] names;
    readonly Symbol[] symbols;
    readonly uint[] parents;
    readonly uint[] ends;
    readonly byte[] depths;
    readonly Dictionary<string, uint> byName;
    readonly Dictionary<uint, uint> bySymbol;

    internal GameplayTagTable(
        string[] names,
        Symbol[] symbols,
        uint[] parents,
        uint[] ends,
        byte[] depths,
        uint buildHash
    ) {
        this.names = names;
        this.symbols = symbols;
        this.parents = parents;
        this.ends = ends;
        this.depths = depths;
        BuildHash = buildHash;

        byName = new(names.Length, StringComparer.Ordinal);
        bySymbol = new(names.Length);

        for (var index = 1u; index < names.Length; index++) {
            byName[names[index]] = index;
            bySymbol[symbols[index].Id] = index;
        }
    }

    /// <summary>A table with no tags in it. What a host that has loaded no content has.</summary>
    public static GameplayTagTable Empty { get; } = new GameplayTagTableBuilder().Build();

    /// <summary>How many tags it holds, implied parents included.</summary>
    public int Count => names.Length - 1;

    /// <summary>Every tag in it, as one range.</summary>
    /// <remarks>
    ///     The whole table is itself a contiguous range, because the pre-order walk numbers from one
    ///     with no gaps. Useful for "any tag at all" and for asserting a range is in bounds.
    /// </remarks>
    public GameplayTagRange All => new(1, (uint)names.Length);

    /// <summary>
    ///     A hash of the whole table — every name, in index order. Two hosts whose tables disagree
    ///     disagree about what every index means, so this is what a handshake compares.
    /// </summary>
    /// <remarks>
    ///     Contributed to the catalog's build hash rather than exchanged on its own
    ///     ([16](../../../docs/plan/16-networking.md) § Security compares the catalog before
    ///     dispatching anything). It is here so that a test, a log line and a diagnostic can name the
    ///     mismatch precisely instead of reporting "content differs".
    /// </remarks>
    public uint BuildHash { get; }

    /// <summary>Finds a tag by its dotted name.</summary>
    /// <param name="name">The name — <c>Damage.Fire.Burn</c>.</param>
    /// <param name="tag">The tag, or <see cref="GameplayTag.None" />.</param>
    /// <returns>Whether the table has it.</returns>
    public bool TryResolve(string? name, out GameplayTag tag) {
        if (name is not null && byName.TryGetValue(name, out var index)) {
            tag = new(index);

            return true;
        }

        tag = GameplayTag.None;

        return false;
    }

    /// <summary>Finds a tag by its dotted name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The tag, or <see cref="GameplayTag.None" /> when the table does not have it.</returns>
    /// <remarks>
    ///     ⚠ <b>Missing is <see cref="GameplayTag.None" /> and not an exception</b>, because the
    ///     commonest caller is a rule loaded from content that a later build may have removed the tag
    ///     for, and a realm that throws while loading a definition is a realm that will not start.
    ///     <see cref="Require" /> is the form for a tag the calling code cannot work without.
    /// </remarks>
    public GameplayTag Resolve(string? name) => TryResolve(name, out var tag) ? tag : GameplayTag.None;

    /// <summary>Finds a tag by its dotted name, and refuses to carry on without it.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The tag.</returns>
    /// <exception cref="ArgumentException">The table does not have it.</exception>
    public GameplayTag Require(string name) =>
        TryResolve(name, out var tag)
            ? tag
            : throw new ArgumentException(
                $"No tag is called '{name}'. A tag exists because a definition mentions it, so this is "
                + "either a spelling or content that has not been built.",
                nameof(name)
            );

    /// <summary>Finds a tag by the symbol of its name — the form durable state is stored in.</summary>
    /// <param name="symbol">The symbol, as <see cref="SymbolOf" /> produced it.</param>
    /// <param name="tag">The tag, or <see cref="GameplayTag.None" />.</param>
    /// <returns>Whether this table has it.</returns>
    /// <remarks>
    ///     <b>The rehydration path, and the reason a save is not invalidated by a content update.</b>
    ///     A symbol is a hash of the name and does not move when the table is rebuilt; an index does.
    ///     A tag that no longer exists comes back as <see cref="GameplayTag.None" />, which is a rule
    ///     that matches nothing rather than a load that fails.
    /// </remarks>
    public bool TryResolve(Symbol symbol, out GameplayTag tag) {
        if (symbol.IsSome && bySymbol.TryGetValue(symbol.Id, out var index)) {
            tag = new(index);

            return true;
        }

        tag = GameplayTag.None;

        return false;
    }

    /// <summary>The dotted name of a tag.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The name, or the empty string for <see cref="GameplayTag.None" /> and for an index this table does not have.</returns>
    public string NameOf(GameplayTag tag) => Holds(tag) ? names[tag.Index] : string.Empty;

    /// <summary>The interned hash of a tag's name — what durable state stores instead of an index.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The symbol, or <see cref="Symbol.None" />.</returns>
    public Symbol SymbolOf(GameplayTag tag) => Holds(tag) ? symbols[tag.Index] : Symbol.None;

    /// <summary>The tag one level up — <c>Damage.Fire</c> for <c>Damage.Fire.Burn</c>.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The parent, or <see cref="GameplayTag.None" /> for a root.</returns>
    public GameplayTag ParentOf(GameplayTag tag) => Holds(tag) ? new(parents[tag.Index]) : GameplayTag.None;

    /// <summary>How many segments a tag has. A root is one.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The depth, or zero for a tag this table does not have.</returns>
    public int DepthOf(GameplayTag tag) => Holds(tag) ? depths[tag.Index] : 0;

    /// <summary>The tag and everything beneath it, as the range a match tests against.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The range, or <see cref="GameplayTagRange.Empty" />.</returns>
    /// <remarks>
    ///     <b>Resolve once, match many times.</b> Everything that holds an authored prefix — a
    ///     requirement, an immunity, a loot condition, a quest objective's filter — is expected to
    ///     keep the range rather than the tag, so that the frame path never reads this table.
    /// </remarks>
    public GameplayTagRange RangeOf(GameplayTag tag) =>
        Holds(tag) ? new(tag.Index, ends[tag.Index]) : GameplayTagRange.Empty;

    /// <summary>The range a dotted prefix names.</summary>
    /// <param name="name">The prefix — <c>Damage.Fire</c>.</param>
    /// <returns>The range, or <see cref="GameplayTagRange.Empty" /> when the table has no such tag.</returns>
    /// <remarks>
    ///     ⚠ <b>An unknown prefix is an empty range, which matches nothing.</b> The other reading —
    ///     an unknown prefix matching everything — is how a boss ends up immune to all damage because
    ///     somebody misspelled a tag, and it is exactly the failure that never shows up in a review.
    /// </remarks>
    public GameplayTagRange RangeOf(string? name) =>
        TryResolve(name, out var tag) ? RangeOf(tag) : GameplayTagRange.Empty;

    /// <summary>Whether a tag is, or is beneath, another.</summary>
    /// <param name="tag">The tag being tested.</param>
    /// <param name="prefix">The tag it might be under.</param>
    /// <returns>Whether it matches.</returns>
    /// <remarks>
    ///     The convenience form, and it reads the table. Where the prefix is authored and fixed — the
    ///     overwhelming majority — resolve a <see cref="GameplayTagRange" /> once instead.
    /// </remarks>
    public bool Matches(GameplayTag tag, GameplayTag prefix) => RangeOf(prefix).Contains(tag);

    /// <summary>Whether a tag is, or is beneath, a named prefix.</summary>
    /// <param name="tag">The tag being tested.</param>
    /// <param name="prefix">The dotted prefix.</param>
    /// <returns>Whether it matches.</returns>
    public bool Matches(GameplayTag tag, string? prefix) => RangeOf(prefix).Contains(tag);

    /// <summary>Whether an index belongs to this table at all.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Whether it is one of ours.</returns>
    public bool Holds(GameplayTag tag) => tag.Index > 0 && tag.Index < names.Length;
}

/// <summary>Composes a <see cref="GameplayTagTable" /> out of the names a content build collected.</summary>
/// <remarks>
///     <para>
///         Add names in any order, including duplicates and children whose parents were never
///         mentioned — <c>Damage.Fire.Burn</c> implies <c>Damage</c> and <c>Damage.Fire</c>, and the
///         builder adds them, because a tag tree with a hole in it cannot be walked in pre-order and
///         a designer writing one leaf should not have to declare its ancestors.
///     </para>
///     <para>
///         ⚠ <b>What it refuses is what a vocabulary's builder is for.</b> Two names differing only in
///         case, and two names whose <see cref="Symbol" /> collides, are both accepted silently by
///         everything downstream and are both content bugs with no symptom until a rule matches the
///         wrong thing. Refusing here turns each into a build error naming both spellings — the
///         discipline <c>BlackboardLayoutBuilder</c> and <c>MoveSet.Compose</c> already follow.
///     </para>
/// </remarks>
public sealed class GameplayTagTableBuilder {
    readonly Dictionary<string, Node> nodes = new(StringComparer.Ordinal);
    readonly List<Node> roots = [];

    /// <summary>How many distinct tags have been named so far, implied parents included.</summary>
    public int Count => nodes.Count;

    /// <summary>Adds a tag, and every ancestor it implies.</summary>
    /// <param name="name">The dotted name — <c>Damage.Fire.Burn</c>.</param>
    /// <returns>The builder, so declarations chain.</returns>
    /// <exception cref="ArgumentException">The name is empty, has an empty segment, or is deeper than <see cref="GameplayTagTable.MaximumDepth" />.</exception>
    public GameplayTagTableBuilder Add(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var depth = 0;
        Node? parent = null;
        var start = 0;

        while (start <= name.Length) {
            var dot = name.IndexOf('.', start);
            var stop = dot < 0 ? name.Length : dot;

            if (stop == start) {
                throw new ArgumentException(
                    $"'{name}' has an empty segment. A tag is dotted words — 'Damage.Fire.Burn' — with "
                    + "nothing missing between the dots.",
                    nameof(name)
                );
            }

            if (++depth > GameplayTagTable.MaximumDepth) {
                throw new ArgumentException(
                    $"'{name}' is more than {GameplayTagTable.MaximumDepth} segments deep.",
                    nameof(name)
                );
            }

            var qualified = name[..stop];

            if (!nodes.TryGetValue(qualified, out var node)) {
                node = new(qualified, name[start..stop], depth);
                nodes.Add(qualified, node);
                (parent?.Children ?? roots).Add(node);
            }

            parent = node;

            if (dot < 0) {
                break;
            }

            start = dot + 1;
        }

        return this;
    }

    /// <summary>Adds several tags.</summary>
    /// <param name="names">The dotted names.</param>
    /// <returns>The builder, so declarations chain.</returns>
    public GameplayTagTableBuilder AddRange(IEnumerable<string> names) {
        ArgumentNullException.ThrowIfNull(names);

        foreach (var name in names) {
            Add(name);
        }

        return this;
    }

    /// <summary>Numbers the tree and produces the table.</summary>
    /// <returns>The table.</returns>
    /// <exception cref="InvalidOperationException">Two names differ only in case, or two names collide as symbols.</exception>
    public GameplayTagTable Build() {
        var count = nodes.Count;
        var names = new string[count + 1];
        var symbols = new Symbol[count + 1];
        var parents = new uint[count + 1];
        var ends = new uint[count + 1];
        var depths = new byte[count + 1];

        names[0] = string.Empty;

        // Ordinal, and on the segment rather than on the qualified name. Sorting qualified names
        // would put a sibling whose first character is below '.' — a hyphen, a digit in some
        // vocabularies — between a tag and its own children, which is precisely the thing that must
        // not happen to a range.
        var ordered = new List<Node>(roots);
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.Segment, right.Segment));

        var next = 1u;

        foreach (var root in ordered) {
            Number(root, 0u, ref next, names, parents, ends, depths);
        }

        var caseFolded = new Dictionary<string, string>(count, StringComparer.OrdinalIgnoreCase);
        var interned = new Dictionary<uint, string>(count);
        var hash = 2166136261u;

        for (var index = 1; index <= count; index++) {
            var name = names[index];

            if (!caseFolded.TryAdd(name, name)) {
                throw new InvalidOperationException(
                    $"'{name}' and '{caseFolded[name]}' are the same tag spelled two ways. Tags are "
                    + "case-sensitive, so this is two tags a rule cannot tell apart and a designer "
                    + "cannot see the difference between."
                );
            }

            var symbol = Symbol.Intern(name);

            if (!interned.TryAdd(symbol.Id, name)) {
                throw new InvalidOperationException(
                    $"'{name}' and '{interned[symbol.Id]}' hash to the same symbol, so durable state "
                    + "cannot tell them apart. Rename one — the odds are about one in fifty thousand "
                    + "at three hundred tags, which is why this is checked rather than assumed."
                );
            }

            symbols[index] = symbol;

            foreach (var character in name) {
                hash ^= character;
                hash *= 16777619u;
            }

            // A separator, so that {"AB", "C"} and {"A", "BC"} are different tables. Without it the
            // hash is a concatenation and two different vocabularies agree by accident.
            hash ^= '\n';
            hash *= 16777619u;
        }

        return new(names, symbols, parents, ends, depths, hash);
    }

    static void Number(
        Node node,
        uint parent,
        ref uint next,
        string[] names,
        uint[] parents,
        uint[] ends,
        byte[] depths
    ) {
        var index = next++;

        names[index] = node.Name;
        parents[index] = parent;
        depths[index] = (byte)node.Depth;

        node.Children.Sort(static (left, right) => string.CompareOrdinal(left.Segment, right.Segment));

        foreach (var child in node.Children) {
            Number(child, index, ref next, names, parents, ends, depths);
        }

        // Written on the way out, which is what makes the subtree contiguous: everything numbered
        // between entering and leaving this node is beneath it, and nothing else is.
        ends[index] = next;
    }

    sealed class Node(string name, string segment, int depth) {
        public string Name { get; } = name;

        public string Segment { get; } = segment;

        public int Depth { get; } = depth;

        public List<Node> Children { get; } = [];
    }
}
