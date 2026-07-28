// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;

namespace Vixen.Net.Engine;

/// <summary>Replicates one kind of <see cref="NetworkBehaviour" />'s state.</summary>
/// <typeparam name="T">The behaviour.</typeparam>
/// <remarks>
///     <para>
///         <b>An ordinary <see cref="IComponentReplicator" />, which is the point.</b> Behaviour-held
///         state joins the pipeline at the same place a <c>[Replicated]</c> struct does, so it gets
///         the same delta encoding, the same per-connection baselines, the same priority shedding and
///         the same per-field bandwidth attribution — none of which is implemented twice. The lane
///         layout comes from the module, and the module's fields declared theirs.
///     </para>
///     <para>
///         The component it watches is <see cref="SyncStateVersion" /> rather than anything holding
///         the values: the values are in managed fields the ECS cannot see, so what the change
///         versions track is the fact that they moved. <c>Write</c> then asks the behaviour itself.
///     </para>
///     <para>
///         <b>A client creates the behaviour when the first record about it arrives.</b> The server
///         decides an object has one; the client finds out by being told, which is the same rule
///         <see cref="NetworkId" /> follows and for the same reason.
///     </para>
/// </remarks>
public sealed class SyncStateReplicator<T> : IComponentReplicator where T : NetworkBehaviour, new() {
    readonly BehaviorStore store;
    readonly WireLane[] lanes;

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<SyncStateVersion>.Id;

    /// <inheritdoc />
    public uint TypeId { get; }

    /// <inheritdoc />
    public string TypeName { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     Reliable-eventual, which is what <c>docs/plan/16</c> asks for for this authoring style. A
    ///     score or an inventory is not a position: it does not supersede itself thirty times a
    ///     second, and a client that missed one is wrong until told again rather than briefly stale.
    /// </remarks>
    public Channel Channel => Channel.ReliableUnordered;

    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<SyncStateVersion>.Id]);

    /// <inheritdoc />
    public ReadOnlySpan<WireLane> Lanes => lanes;

    /// <summary>Creates a replicator for one behaviour type.</summary>
    /// <param name="store">Where the behaviours live.</param>
    /// <exception cref="InvalidOperationException">The behaviour's layout is not fixed-width.</exception>
    public SyncStateReplicator(BehaviorStore store) {
        ArgumentNullException.ThrowIfNull(store);

        this.store = store;
        TypeName = typeof(T).FullName!;
        TypeId = ReplicationRegistry.HashTypeName(TypeName);

        // The layout is a property of the type, not of any one instance, so it is taken once from a
        // throwaway. Two instances of the same behaviour whose modules differed would be a layout
        // that depends on run-time state, which is exactly what cannot work.
        var prototype = new T();
        prototype.State.Seal();
        lanes = [.. prototype.State.Lanes];
    }

    /// <inheritdoc />
    public bool Has(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return world.Has<SyncStateVersion>(entity) && store.Get<T>(entity) is not null;
    }

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        var behaviour = store.Get<T>(entity);

        if (behaviour is null) {
            return;
        }

        behaviour.State.Write(ref writer);
        behaviour.State.ClearDirty();
    }

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        ArgumentNullException.ThrowIfNull(world);

        var behaviour = store.Get<T>(entity);

        if (behaviour is null) {
            if (!world.Has<SyncStateVersion>(entity)) {
                world.Add(entity, new SyncStateVersion());
            }

            behaviour = store.Add<T>(entity);
            behaviour.State.Seal();
        }

        return behaviour.State.Apply(ref reader);
    }
}
