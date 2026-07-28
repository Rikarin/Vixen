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

    /// <summary>Declares what this behaviour replicates.</summary>
    /// <returns>Its root module.</returns>
    /// <remarks>
    ///     Called once. Everything a <see cref="SyncVar{T}" /> needs to know about its place on the
    ///     wire is decided here and never again, which is what lets both ends agree about a layout
    ///     they never exchange.
    /// </remarks>
    protected abstract NetworkModule Build();

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
