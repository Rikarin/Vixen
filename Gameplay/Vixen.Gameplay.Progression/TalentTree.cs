// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Progression;

/// <summary>Why an allocation was refused.</summary>
public enum TalentRejection {
    /// <summary>It is legal.</summary>
    None = 0,

    /// <summary>It names a node this tree does not have.</summary>
    UnknownNode,

    /// <summary>More ranks of something than it has.</summary>
    TooManyRanks,

    /// <summary>More points than the character has.</summary>
    NotEnoughPoints,

    /// <summary>A node whose row is not open yet.</summary>
    RowLocked,

    /// <summary>A node whose prerequisite is not taken.</summary>
    MissingPrerequisite
}

/// <summary>What was wrong with an allocation, and where.</summary>
/// <param name="Rejection">Why it was refused.</param>
/// <param name="Node">Which node, or the empty string.</param>
/// <param name="Message">What is wrong, in a sentence.</param>
public readonly record struct TalentVerdict(TalentRejection Rejection, string Node, string Message) {
    /// <summary>The verdict on a legal allocation.</summary>
    public static TalentVerdict Legal { get; } = new(TalentRejection.None, string.Empty, string.Empty);

    /// <summary>Whether it is legal.</summary>
    public bool IsLegal => Rejection == TalentRejection.None;
}

/// <summary>A compiled talent node.</summary>
public sealed class TalentNode {
    readonly GameplayTag[] tags;
    readonly Modifier[] modifiers;
    readonly (int Node, int Ranks)[] requires;

    internal TalentNode(
        TalentNodeDefinition definition,
        int index,
        GameplayTag[] tags,
        Modifier[] modifiers,
        (int Node, int Ranks)[] requires
    ) {
        Definition = definition;
        Index = index;
        this.tags = tags;
        this.modifiers = modifiers;
        this.requires = requires;
    }

    /// <summary>What it was compiled from.</summary>
    public TalentNodeDefinition Definition { get; }

    /// <summary>Where it sits in the tree's node list.</summary>
    public int Index { get; }

    /// <summary>What it is called within its tree.</summary>
    public string Id => Definition.Id;

    /// <summary>How many times it can be taken, never below one.</summary>
    public int MaximumRanks => Math.Max(1, Definition.MaximumRanks);

    /// <summary>What each rank costs, never below one.</summary>
    public int CostPerRank => Math.Max(1, Definition.CostPerRank);

    /// <summary>How many points must be spent in the tree before it opens.</summary>
    public int RequiredPoints => Math.Max(0, Definition.RequiredPoints);

    /// <summary>What having any rank of it grants.</summary>
    public ReadOnlySpan<GameplayTag> GrantsTags => tags;

    /// <summary>What it does per rank, with no source stamped on.</summary>
    public ReadOnlySpan<Modifier> Modifiers => modifiers;

    /// <summary>What else must be taken first, as node indices and rank counts.</summary>
    public ReadOnlySpan<(int Node, int Ranks)> Requires => requires;
}

/// <summary>How many ranks of each node somebody has taken. The durable half.</summary>
/// <remarks>
///     A dictionary of ids rather than an array of indices, because this is what a save holds and a
///     node's index moves when a designer inserts one above it.
/// </remarks>
public sealed class TalentAllocation {
    readonly Dictionary<string, int> ranks = new(StringComparer.Ordinal);

    /// <summary>How many nodes have any rank.</summary>
    public int Count => ranks.Count;

    /// <summary>Everything taken, by node id.</summary>
    public IReadOnlyDictionary<string, int> Ranks => ranks;

    /// <summary>How many ranks of one node.</summary>
    /// <param name="node">Its id.</param>
    /// <returns>The count, or zero.</returns>
    public int RanksOf(string node) => ranks.GetValueOrDefault(node);

    /// <summary>Sets how many ranks of one node.</summary>
    /// <param name="node">Its id.</param>
    /// <param name="count">How many. Zero removes it.</param>
    /// <returns>The allocation, so calls chain.</returns>
    public TalentAllocation Set(string node, int count) {
        if (count <= 0) {
            ranks.Remove(node);
        } else {
            ranks[node] = count;
        }

        return this;
    }

    /// <summary>Forgets everything. What a respec does before the new allocation arrives.</summary>
    public void Clear() => ranks.Clear();

    /// <summary>A copy, for an editor's undo or a speculative validation.</summary>
    /// <returns>The copy.</returns>
    public TalentAllocation Copy() {
        var copy = new TalentAllocation();

        foreach (var (node, count) in ranks) {
            copy.ranks[node] = count;
        }

        return copy;
    }
}

/// <summary>A compiled talent tree, and the thing that says whether an allocation is legal.</summary>
/// <remarks>
///     <para>
///         <b>Validated whole, not click by click, and doc 28 § Progression is explicit about why:
///         "a client-built talent tree is a client-chosen power level".</b> The client sends an
///         allocation and the server checks it from scratch. That is one pass over a few dozen nodes,
///         it happens when somebody respecs rather than every frame, and it is the only form of the
///         check that survives a patch changing what a node costs.
///     </para>
///     <para>
///         ⚠ <b>Validating a final allocation forces every rule to be a property of the allocation.</b>
///         A row gate — "five points anywhere in this tree" — is a total and checks fine. "You must
///         have taken A before B" is a property of a <em>sequence</em>, is not expressible here, and
///         is not missed: it is unverifiable after a respec anyway.
///     </para>
/// </remarks>
public sealed class TalentTree {
    readonly TalentNode[] nodes;
    readonly Dictionary<string, int> byId;

    internal TalentTree(TalentTreeDefinition definition, TalentNode[] nodes, Dictionary<string, int> byId) {
        Definition = definition;
        this.nodes = nodes;
        this.byId = byId;
    }

    /// <summary>What it was compiled from.</summary>
    public TalentTreeDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>Its nodes, in the order they were authored.</summary>
    public ReadOnlySpan<TalentNode> Nodes => nodes;

    /// <summary>Finds a node.</summary>
    /// <param name="id">Its id within the tree.</param>
    /// <returns>It, or null.</returns>
    public TalentNode? Find(string id) => byId.TryGetValue(id, out var index) ? nodes[index] : null;

    /// <summary>What an allocation costs.</summary>
    /// <param name="allocation">The allocation.</param>
    /// <returns>The points, counting only nodes this tree has.</returns>
    public int CostOf(TalentAllocation allocation) {
        ArgumentNullException.ThrowIfNull(allocation);

        var cost = 0;

        foreach (var (id, count) in allocation.Ranks) {
            if (Find(id) is { } node) {
                cost += node.CostPerRank * Math.Max(0, count);
            }
        }

        return cost;
    }

    /// <summary>Whether an allocation is legal for somebody with this many points.</summary>
    /// <param name="allocation">What they say they have taken.</param>
    /// <param name="points">How many points they have.</param>
    /// <returns>The verdict.</returns>
    public TalentVerdict Validate(TalentAllocation allocation, int points) {
        ArgumentNullException.ThrowIfNull(allocation);

        var spent = 0;

        foreach (var (id, count) in allocation.Ranks) {
            if (Find(id) is not { } node) {
                return new(TalentRejection.UnknownNode, id, $"'{id}' is not a node in {Definition.DisplayName}.");
            }

            if (count < 0 || count > node.MaximumRanks) {
                return new(
                    TalentRejection.TooManyRanks,
                    id,
                    $"'{id}' has {node.MaximumRanks} ranks and the allocation takes {count}."
                );
            }

            spent += node.CostPerRank * count;
        }

        if (spent > points) {
            return new(
                TalentRejection.NotEnoughPoints,
                string.Empty,
                $"The allocation spends {spent} points and the character has {points}."
            );
        }

        foreach (var (id, _) in allocation.Ranks) {
            var node = Find(id)!;
            var earlier = SpentAbove(allocation, node.RequiredPoints);

            if (earlier < node.RequiredPoints) {
                return new(
                    TalentRejection.RowLocked,
                    id,
                    $"'{id}' needs {node.RequiredPoints} points spent in earlier rows and the allocation "
                    + $"spends {earlier}."
                );
            }

            foreach (var (required, ranks) in node.Requires) {
                if (allocation.RanksOf(nodes[required].Id) < ranks) {
                    return new(
                        TalentRejection.MissingPrerequisite,
                        id,
                        $"'{id}' needs {ranks} rank(s) of '{nodes[required].Id}'."
                    );
                }
            }
        }

        return TalentVerdict.Legal;
    }

    /// <summary>What an allocation spends on nodes in rows above a gate.</summary>
    /// <remarks>
    ///     ⚠ <b>Rows above, not the whole tree, and the difference is what the gate means.</b> "Five
    ///     points before this row opens" cannot count the point being spent <em>on</em> the row —
    ///     otherwise the last point of row one opens row two and is also allowed to be in it, and a
    ///     five-point gate is really a four-point one.
    ///     <para>
    ///         The obvious way to say that is "points spent <em>before</em> this node", which is a
    ///         property of a sequence and therefore uncheckable from a finished allocation. "Points
    ///         on nodes with a lower gate" is the same rule as a property of the allocation, and it is
    ///         the reason the row gate is a number rather than a row index.
    ///     </para>
    /// </remarks>
    int SpentAbove(TalentAllocation allocation, int gate) {
        var spent = 0;

        foreach (var (id, count) in allocation.Ranks) {
            if (Find(id) is { } node && node.RequiredPoints < gate) {
                spent += node.CostPerRank * Math.Max(0, count);
            }
        }

        return spent;
    }

    /// <summary>What an allocation does to a character's stats.</summary>
    /// <param name="allocation">The allocation.</param>
    /// <param name="source">What the modifiers are removable by — usually the tree itself.</param>
    /// <param name="into">Where to put them.</param>
    /// <returns>How many were produced.</returns>
    /// <remarks>
    ///     ⚠ <b>A rank multiplies the value rather than repeating the modifier.</b> Five separate
    ///     +2 % modifiers from one source cannot be told apart on removal and, for a multiplicative
    ///     bucket, compose to something other than +10 % — which is a balance difference nobody
    ///     authored.
    /// </remarks>
    public int Modifiers(TalentAllocation allocation, ModifierSource source, ICollection<Modifier> into) {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(into);

        var produced = 0;

        // Node order rather than dictionary order, so the modifiers arrive in a sequence that is a
        // property of the content — the attribute set sorts them anyway, and a stable order makes a
        // diff of two characters' talents readable.
        foreach (var node in nodes) {
            var ranks = allocation.RanksOf(node.Id);

            if (ranks <= 0) {
                continue;
            }

            foreach (ref readonly var modifier in node.Modifiers) {
                into.Add(modifier with { Value = modifier.Value * ranks, Source = source });
                produced++;
            }
        }

        return produced;
    }

    /// <summary>What an allocation grants, as tags.</summary>
    /// <param name="allocation">The allocation.</param>
    /// <param name="into">Where to put them.</param>
    public void Tags(TalentAllocation allocation, ICollection<GameplayTag> into) {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(into);

        foreach (var node in nodes) {
            if (allocation.RanksOf(node.Id) <= 0) {
                continue;
            }

            foreach (var tag in node.GrantsTags) {
                if (tag.IsSome) {
                    into.Add(tag);
                }
            }
        }
    }
}
