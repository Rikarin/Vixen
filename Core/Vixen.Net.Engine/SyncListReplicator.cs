// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;

namespace Vixen.Net.Engine;

/// <summary>A list a behaviour replicates, as the replicator sees it.</summary>
/// <remarks>
///     Non-generic, because a behaviour's lists are of different element types and the thing that
///     writes them does not care which — it writes bits. <see cref="SyncList{T}" /> is the only
///     implementation, and the interface exists so that one replicator handles a behaviour's whole
///     collection rather than one per element type.
/// </remarks>
public interface ISyncList {
    /// <summary>What it is called, for diagnostics and for the bandwidth report.</summary>
    string Name { get; }

    /// <summary>Gives it the name its declaration chose.</summary>
    /// <param name="name">The name.</param>
    void Rename(string name);

    /// <summary>How many are in it.</summary>
    int Count { get; }

    /// <summary>Whether there is anything to send.</summary>
    /// <remarks>
    ///     On the interface rather than on <see cref="SyncList{T}" /> alone because
    ///     <see cref="SyncStateSweepSystem" /> is the caller, and it holds a
    ///     <see cref="NetworkBehaviour" />'s lists as <c>ISyncList</c> — the element type is the one
    ///     thing about a list a sweep over every behaviour cannot know.
    /// </remarks>
    bool HasPending { get; }

    /// <summary>Writes the whole list.</summary>
    /// <param name="writer">Where the bits go.</param>
    /// <returns>Whether it fit.</returns>
    bool WriteWhole(ref BitWriter writer);

    /// <summary>Takes a list as it arrived.</summary>
    /// <param name="reader">Where the bits come from.</param>
    /// <returns>Whether it was well-formed.</returns>
    bool Apply(ref BitReader reader);

    /// <summary>Marks whatever was outstanding as dealt with.</summary>
    void ClearPending();
}

/// <summary>Replicates one kind of <see cref="NetworkBehaviour" />'s lists.</summary>
/// <remarks>
///     <para>
///         <b>The whole list, every time it changes — and that is a correction to what this package
///         used to claim.</b> <see cref="SyncList{T}" /> keeps a log of operations, and the design note
///         beside it said those ops go on the wire and that the reliable channel's ordering makes
///         per-connection bookkeeping unnecessary. That is true of a broadcast and false of a
///         snapshot, which is why it was never wired up: a snapshot goes to the connections an
///         interest resolver returns, so "everyone receives every op exactly once" is not something
///         this pipeline offers. An object that walks into somebody's interest has to be told the
///         list, not the last op.
///     </para>
///     <para>
///         <b>Sending the state rather than the events makes every one of those cases disappear.</b>
///         A late joiner, a reconnect, a lost snapshot, an object crossing an interest boundary and a
///         player who was in another scene are all the same thing to a receiver: here is the list. The
///         existing baseline machinery already re-sends until acknowledged and then stops, so nothing
///         new is needed on the wire — the record format was never fixed-width, only the <i>delta</i>
///         path is, and it correctly declines a replicator that declares no lanes.
///     </para>
///     <para>
///         <b>What it costs is bandwidth proportional to the list on the tick it changes.</b> A
///         hundred-item inventory is a few hundred bytes when somebody picks something up, on a
///         channel that will deliver it, seconds apart. A list changing every tick is a list being
///         used as something it is not — and if one genuinely must be, the shape that fixes it is the
///         one <c>NetworkAnimatorParameters</c> and <c>NetworkBones</c> use: a fixed capacity, which
///         buys back per-element delta encoding at one bit for an element that did not move. That
///         needs a fixed-width element type, which a general <see cref="SyncList{T}" /> does not have.
///     </para>
///     <para>
///         The op log is not wasted by this: it still drives <see cref="SyncList{T}.Changed" />, which
///         is what a UI binds to, and locally an op is exactly the notification a caller wants.
///     </para>
/// </remarks>
/// <typeparam name="T">The behaviour.</typeparam>
public sealed class SyncListReplicator<T> : IComponentReplicator where T : NetworkBehaviour, new() {
    readonly BehaviorStore store;

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<SyncListVersion>.Id;

    /// <inheritdoc />
    public uint TypeId { get; }

    /// <inheritdoc />
    public string TypeName { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     Reliable, because a list is not a position: it does not supersede itself thirty times a
    ///     second, and a client that missed one is wrong until told again rather than briefly stale.
    /// </remarks>
    public Channel Channel => Channel.ReliableUnordered;

    /// <summary>Below <c>SyncVar</c> state, which is smaller and more urgent.</summary>
    public int Priority => 8;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<SyncListVersion>.Id]);

    /// <summary>
    ///     None, which is what tells the server to send whole records rather than differences.
    /// </summary>
    /// <remarks>
    ///     <b>Not an omission — differencing a list lane by lane is actively wrong.</b> Inserting at
    ///     the front shifts every element, so a one-item insert would encode as "all of it changed"
    ///     and cost more than sending it. An empty layout is the documented way to say "this record
    ///     goes whole", and the server's own lane check would refuse a mismatched one anyway.
    /// </remarks>
    public ReadOnlySpan<WireLane> Lanes => [];

    /// <summary>Creates a replicator for one behaviour type's lists.</summary>
    /// <param name="store">Where the behaviours live.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store" /> is null.</exception>
    public SyncListReplicator(BehaviorStore store) {
        ArgumentNullException.ThrowIfNull(store);

        this.store = store;
        TypeName = typeof(T).FullName! + ".Lists";
        TypeId = ReplicationRegistry.HashTypeName(TypeName);
    }

    /// <inheritdoc />
    public bool Has(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return world.Has<SyncListVersion>(entity) && store.Get<T>(entity) is { Lists.Count: > 0 };
    }

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        if (store.Get<T>(entity) is not { } behaviour) {
            return;
        }

        // Every list, in declaration order, which both ends walk. No count and no names on the wire:
        // the behaviour type is what the record's type index already names, and its lists are a
        // property of the type rather than of the instance.
        foreach (var list in behaviour.Lists) {
            list.WriteWhole(ref writer);
            list.ClearPending();
        }
    }

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        ArgumentNullException.ThrowIfNull(world);

        var behaviour = store.Get<T>(entity);

        if (behaviour is null) {
            if (!world.Has<SyncListVersion>(entity)) {
                world.Add(entity, new SyncListVersion());
            }

            behaviour = store.Add<T>(entity);
        }

        foreach (var list in behaviour.Lists) {
            if (!list.Apply(ref reader)) {
                return false;
            }
        }

        return true;
    }
}
