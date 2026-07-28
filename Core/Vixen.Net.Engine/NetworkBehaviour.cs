// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Engine.Behaviors;
using Vixen.Net.Replication;

namespace Vixen.Net.Engine;

/// <summary>A behaviour whose state the network carries.</summary>
/// <remarks>
///     <para>
///         A <see cref="NetworkModule" /> with a <c>Behavior</c>'s lifecycle attached to it. Declare
///         <see cref="SyncVar{T}" />s and nested modules in the constructor; the layout is fixed when
///         the behaviour is attached, and from then on setting one is a local assignment plus one
///         write to a component, which is what puts the entity in the next capture.
///     </para>
///     <para>
///         <b>Deriving from <c>Behavior</c> rather than being a component is what the whole package is
///         for.</b> <c>Vixen.Net</c> and <c>Vixen.Engine</c> are siblings and neither may reference the
///         other — networking is optional and nothing below the engine is allowed to depend on it —
///         so the type that needs to see both lives above both, here.
///     </para>
/// </remarks>
public abstract class NetworkBehaviour : Behavior {
    readonly List<ISyncList> lists = [];

    NetworkModule? state;

    /// <summary>Which networked object this is part of.</summary>
    public NetworkId NetworkId => Has<NetworkId>() ? Read<NetworkId>() : NetworkId.None;

    /// <summary>Whether this end is the one that decides. Set by whoever attaches the behaviour.</summary>
    /// <remarks>
    ///     Nothing here enforces it — a client that writes a <see cref="SyncVar{T}" /> is overwritten by
    ///     the next snapshot rather than stopped, which is the same rule the ECS-native style follows.
    ///     This is here so a behaviour can decide what to do in <c>Update</c> without asking a session.
    /// </remarks>
    public bool IsServer { get; set; }

    /// <summary>The state this behaviour replicates, fixed the first time it is asked for.</summary>
    public NetworkModule State => state ??= Build();

    /// <summary>The lists this behaviour replicates, in the order they were declared.</summary>
    /// <remarks>
    ///     <b>Order is the wire format.</b> Nothing about a list's identity is sent — the record's
    ///     type index names the behaviour, and its lists are a property of the type — so both ends
    ///     walk this in the same order or read each other's lists as their own. Declaring one outside
    ///     the constructor is therefore the same mistake as declaring a <see cref="SyncVar{T}" />
    ///     there, and <see cref="DeclareList{TList}" /> is where it is refused.
    /// </remarks>
    public IReadOnlyList<ISyncList> Lists => lists;

    /// <summary>Declares what this behaviour replicates.</summary>
    /// <returns>Its root module.</returns>
    /// <remarks>
    ///     Called once. Everything a <see cref="SyncVar{T}" /> needs to know about its place on the
    ///     wire is decided here and never again, which is what lets both ends agree about a layout
    ///     they never exchange.
    /// </remarks>
    protected abstract NetworkModule Build();

    /// <summary>Declares a list. Call from a constructor, before the behaviour is used.</summary>
    /// <typeparam name="TList">The list's type.</typeparam>
    /// <param name="list">The list.</param>
    /// <param name="name">What to call it, for diagnostics and the bandwidth report.</param>
    /// <returns>The list, so a declaration can be one line.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="InvalidOperationException">The behaviour is already attached to an entity.</exception>
    protected TList DeclareList<TList>(TList list, string name) where TList : ISyncList {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(name);

        if (World is not null) {
            throw new InvalidOperationException(
                "This behaviour is already attached, so its lists are fixed. Declare them in the constructor: "
                + "a list added later would shift every list after it and both ends would read each other's."
            );
        }

        list.Rename(name);
        lists.Add(list);

        return list;
    }

    /// <summary>Tells the ECS that one of this behaviour's lists changed.</summary>
    /// <remarks>
    ///     Separate from <see cref="MarkChanged" /> so a list changing does not re-send a score, and a
    ///     score changing does not re-send a list — they are different records for the same reason
    ///     they are different components.
    /// </remarks>
    public void MarkListsChanged() {
        if (World is null || IsDestroyed) {
            return;
        }

        if (!Has<SyncListVersion>()) {
            World.Add(Entity, new SyncListVersion());
        }

        World.Get<SyncListVersion>(Entity).Value++;
    }

    /// <summary>
    ///     Tells the ECS that something in this behaviour changed, so the next capture looks at it.
    /// </summary>
    /// <remarks>
    ///     Called by the sync system rather than by a setter: a behaviour that set ten sync vars in
    ///     one frame should touch the component once, and a setter cannot know it was the last.
    /// </remarks>
    public void MarkChanged() {
        if (World is null || IsDestroyed) {
            return;
        }

        if (!Has<SyncStateVersion>()) {
            World.Add(Entity, new SyncStateVersion());
        }

        World.Get<SyncStateVersion>(Entity).Value++;
    }
}
