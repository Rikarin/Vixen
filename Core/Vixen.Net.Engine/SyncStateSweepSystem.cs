// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Behaviors;
using Vixen.Net.Replication;

namespace Vixen.Net.Engine;

/// <summary>Marks every behaviour whose state or lists changed this frame, once, at the end of it.</summary>
/// <remarks>
///     <para>
///         <b>The caller <see cref="NetworkBehaviour.MarkChanged" />'s own remarks already claimed
///         existed.</b> That method says it is "called by the sync system rather than by a setter",
///         and until this system there was no sync system: every caller in the tree was a test, so a
///         game that set a <see cref="SyncVar{T}" /> and did not remember to call
///         <c>MarkChanged()</c> by hand had its state stay on the server for ever. Nothing failed —
///         <see cref="SyncStateReplicator{T}" /> simply never saw the entity, because the entity had
///         no <see cref="SyncStateVersion" /> for the change filter to notice.
///     </para>
///     <para>
///         <b>A sweep rather than a push, which is the design and not a shortcut.</b> A
///         <see cref="SyncVar{T}" /> setter cannot mark the entity itself: a behaviour that sets ten
///         of them in one frame would touch the component ten times, and a setter has no way to know
///         it was the last. So the dirt accumulates in managed fields and something walks them once,
///         after everything that writes them has run.
///     </para>
///     <para>
///         <b>Ordering is the whole of the correctness argument, and it is invisible when it is
///         wrong.</b> A sweep that ran <i>before</i> the behaviours would mark last frame's writes,
///         and the snapshot would ship every change one frame late — which presents as interpolation
///         that feels slightly heavy rather than as a bug. Hence
///         <c>[UpdateAfter(typeof(BehaviorLateUpdateSystem))]</c>: <c>LateUpdate</c> is the last
///         phase in which game logic runs at all, and that system is the last thing in it that can
///         set a <see cref="SyncVar{T}" />. <c>SyncStateSweepOrderTests</c> asserts the placement off
///         <see cref="SystemGraph.Plan" /> rather than trusting this paragraph.
///     </para>
///     <para>
///         ⚠ <b>The other half of that ordering is the game's, because <c>Capture</c> is the game's.</b>
///         <see cref="ReplicationServer.Capture" /> is not a system — a server decides for itself when
///         its tick is — so nothing here can express "before the capture" as an attribute. The rule is
///         that a capture must come after this phase in the same tick: a server that runs
///         <c>EngineLoop.Frame</c> and then captures is already right, and a server that captures
///         inside <c>SystemPhase.FixedUpdate</c> — where <see cref="NetworkTransformCaptureSystem" />
///         lives — is one frame behind and should call <see cref="Sweep" /> itself instead, which is
///         public for exactly that reason and the same reason
///         <see cref="NetworkTransformCaptureSystem.Publish" /> is.
///     </para>
///     <para>
///         <b>The dirt is cleared by the capture, not by the sweep, and that is why a sweep is
///         idempotent between captures rather than after one.</b>
///         <see cref="SyncStateReplicator{T}" /> calls <c>ClearDirty</c> as it writes the record —
///         clearing it here instead would mean a field that changed in a frame the server did not
///         capture is never sent at all. The visible consequence is that a sweep running twice
///         between two captures marks twice: two increments of a counter nothing reads the value of,
///         which is exactly what the counter was chosen to make cheap.
///     </para>
///     <para>
///         <b>What it costs.</b> The query is <c>BehaviorRef</c> and <see cref="NetworkId" />
///         together, so it visits networked entities that carry behaviours and nothing else — a scene
///         of a thousand props costs nothing, and neither does a thousand behaviours on entities the
///         wire has never heard of. Per visited behaviour it is a field-by-field
///         <see cref="NetworkModule.IsDirty" /> with an early exit on the first dirty one, and a
///         <see cref="ISyncList.HasPending" /> per declared list: O(networked behaviours × fields),
///         with no allocation once the scratch list has grown. It is deliberately <b>not</b> O(dirty)
///         — being O(dirty) is what a push from the setter would buy, and that is the design this
///         type's second paragraph rejects.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.LateUpdate)]
[UpdateAfter(typeof(BehaviorLateUpdateSystem))]
public sealed class SyncStateSweepSystem : SystemBase, IDeclaredAccess {
    readonly BehaviorStore store;

    readonly QueryDescription candidates = new QueryDescription().WithAll<BehaviorRef, NetworkId>();

    // Collected first and marked afterwards, which is not tidiness. `MarkChanged` adds a component
    // to an entity that may not have one yet, and a structural change moves the row out of the chunk
    // this walk is holding — so marking inside the loop would rewrite the memory being iterated.
    readonly List<NetworkBehaviour> dirtyState = [];
    readonly List<NetworkBehaviour> dirtyLists = [];

    /// <inheritdoc />
    /// <remarks>
    ///     Declared at construction rather than with attributes, for the reason
    ///     <c>NetworkTransformCaptureSystem</c> gives: naming a component type in a generic call is
    ///     what assigns it an id, and an attribute can only look one up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<NetworkId>()
        .Read<BehaviorRef>()
        .Write<SyncStateVersion>()
        .Write<SyncListVersion>()
        .Build();

    /// <summary>How many state marks this has made.</summary>
    public long MarkedStateCount { get; private set; }

    /// <summary>How many list marks this has made.</summary>
    public long MarkedListCount { get; private set; }

    /// <summary>How many networked behaviours the last sweep looked at.</summary>
    /// <remarks>
    ///     The cost of the pass, in the only unit that scales with the game rather than with the
    ///     engine. A number far larger than the count of things that actually change is a game whose
    ///     behaviours are networked and need not be.
    /// </remarks>
    public int LastVisitedCount { get; private set; }

    /// <summary>Creates the system.</summary>
    /// <param name="store">Where the behaviours live. The same store the replicators were given.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store" /> is null.</exception>
    public SyncStateSweepSystem(BehaviorStore store) {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Sweep(context.World);

        return dependency;
    }

    /// <summary>Marks everything whose state or lists changed since the last sweep.</summary>
    /// <param name="world">The world.</param>
    /// <returns>How many marks were made, state and lists together.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>
    ///     Public so a server loop that drives its own schedule — which is what <c>Samples/08</c>
    ///     does — can call it immediately before its <c>Capture</c> rather than standing up a runner.
    /// </remarks>
    public int Sweep(World world) {
        ArgumentNullException.ThrowIfNull(world);

        // Refused rather than tolerated, because the wrong answer is silence. `BehaviorStore.AllOn`
        // reads the entity's `BehaviorRef` out of the store's *own* world, so a store built for a
        // different world hands back nothing for every entity and the sweep reports a clean frame
        // for ever — which is the exact failure this system exists to remove, reintroduced one level
        // up.
        if (!ReferenceEquals(store.World, world)) {
            throw new ArgumentException(
                $"This sweep's behaviour store belongs to world '{store.World.Name}' and was asked to "
                + $"sweep '{world.Name}'. It would find no behaviours at all and say so as a clean frame.",
                nameof(world)
            );
        }

        dirtyState.Clear();
        dirtyLists.Clear();
        LastVisitedCount = 0;

        foreach (var chunk in world.Chunks(candidates)) {
            foreach (var entity in chunk.Entities) {
                // `AllOn` answers from the entity's `BehaviorRef`, which is one component however
                // many stores share the world. Everything here is a call on the behaviour itself
                // rather than a bucket lookup, so a foreign store's behaviour is marked correctly
                // rather than indexing a stranger's array — the trap `BehaviorStore.Destroy`
                // documents does not reach this pass.
                foreach (var behavior in store.AllOn(entity)) {
                    if (behavior is not NetworkBehaviour networked || networked.IsDestroyed) {
                        continue;
                    }

                    LastVisitedCount++;

                    if (networked.State.IsDirty) {
                        dirtyState.Add(networked);
                    }

                    if (HasPendingLists(networked)) {
                        dirtyLists.Add(networked);
                    }
                }
            }
        }

        foreach (var networked in dirtyState) {
            networked.MarkChanged();
        }

        foreach (var networked in dirtyLists) {
            networked.MarkListsChanged();
        }

        MarkedStateCount += dirtyState.Count;
        MarkedListCount += dirtyLists.Count;

        return dirtyState.Count + dirtyLists.Count;
    }

    static bool HasPendingLists(NetworkBehaviour behaviour) {
        var lists = behaviour.Lists;

        for (var index = 0; index < lists.Count; index++) {
            if (lists[index].HasPending) {
                return true;
            }
        }

        return false;
    }
}
