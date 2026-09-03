// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using JoltPhysicsSharp;
using Vixen.Physics.Bodies;

namespace Vixen.Physics;

/// <summary>
///     Per-pair collision suppression: two named bodies that are not tested against one another, while
///     both still collide with everything else.
/// </summary>
/// <remarks>
///     <para>
///         <b>Layers cannot express this and that is why it is here.</b> A layer says "this kind of
///         thing does not hit that kind of thing", which is a statement about two <i>classes</i>. A
///         ragdoll's upper arm and forearm are the same class as every other limb — they must not
///         push each other apart at the elbow and must still hit the floor, the wall and the other
///         ragdoll's forearm. Thirty-two layers cannot say that, and a layer per limb per character
///         runs out at the second character.
///     </para>
///     <para>
///         <b>Jolt's mechanism, unchanged.</b> Every participating body is given a
///         <c>CollisionGroup</c> naming one shared <c>GroupFilterTable</c>, a single group id, and a
///         sub-group id of its own; the table holds the disabled pairs. A body that never
///         participates has no filter at all, and Jolt's rule — a null filter on both sides collides,
///         and differing group ids always collide — means such a body is untouched by any of this.
///     </para>
///     <para>
///         ⚠ <b>Suppression has two independent sources and one table</b>, because a pair can be
///         disabled by a caller who said so and by a joint that implies it, and either alone must
///         keep it disabled. A pair is disabled in Jolt exactly when
///         <see cref="SetPairCollision" /> was told to and has not been told otherwise, <i>or</i> at
///         least one live constraint over that pair asked for it. So re-enabling a pair a joint still
///         suppresses does nothing visible, and destroying the last such joint restores whatever the
///         caller last said — which is the only arrangement where neither source can silently undo
///         the other.
///     </para>
/// </remarks>
public sealed partial class PhysicsWorld {
    /// <summary>The one group id every participating body shares.</summary>
    /// <remarks>
    ///     Jolt's table only consults its pairs when two bodies' <i>group</i> ids match, so one id for
    ///     everybody and a sub-group per body is what makes a single flat table express arbitrary
    ///     pairs. A second group id would buy separate tables, which is the design Jolt intends for a
    ///     ragdoll per character — and which would then need a filter object per ragdoll and a
    ///     lifetime for each. One table costs a bit per pair and no lifetimes at all.
    /// </remarks>
    static readonly CollisionGroupID SharedGroup = new(0u);

    /// <summary>A slot's <c>SubGroup</c> when the body is in no group.</summary>
    /// <remarks>
    ///     ⚠ <b>Minus one rather than zero</b>, because zero is a perfectly good sub-group id and
    ///     <c>BodySlot</c> is a struct in an array that starts out zeroed. Nothing reads a slot the
    ///     body's handle does not match, but a sentinel that a zeroed slot satisfies would make the
    ///     first body ever created a member of a group nobody put it in.
    /// </remarks>
    internal const int NoSubGroup = -1;

    /// <summary>Which body holds each sub-group id, indexed by that id.</summary>
    /// <remarks>
    ///     Ids are handed out and never reused, including when the body that held one is destroyed.
    ///     Reuse would mean a pair disabled for a limb that no longer exists silently applying to
    ///     whatever body took its index — the same aliasing <see cref="BodySlot" />'s whole-handle
    ///     check exists to stop, and much harder to see because the symptom is two unrelated bodies
    ///     passing through each other.
    /// </remarks>
    readonly List<BodyHandle> subGroupOwners = [];

    /// <summary>The pairs a caller disabled by hand, as packed sub-group pairs.</summary>
    readonly HashSet<long> suppressedByCaller = [];

    /// <summary>How many live constraints ask for each pair to be suppressed.</summary>
    readonly Dictionary<long, int> suppressedByConstraint = [];

    /// <summary>
    ///     Every table this world has ever built. All but the last are dead and all are kept anyway.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Kept rather than freed on growth, deliberately.</b> A <c>GroupFilter</c> is a Jolt
    ///     <c>RefTarget</c> and every body holding one has taken a reference; growing re-points every
    ///     member at the new table first, so the old one <i>should</i> be free to drop — but "should"
    ///     about a refcount across an interop boundary is a native abort with no managed frame in it,
    ///     and the cost of being wrong is far above the cost of being sure. Growth doubles, so a world
    ///     with a thousand grouped bodies keeps seven small tables until it is disposed.
    /// </remarks>
    readonly List<GroupFilterTable> filterTables = [];

    GroupFilterTable? pairFilter;
    int pairFilterCapacity;

    /// <summary>How many bodies have been given a sub-group id.</summary>
    /// <remarks>
    ///     A body only ever gains one by being named in a suppression, so in a world that uses none
    ///     this is zero and no table has been built. Useful to a test that wants to prove the feature
    ///     costs nothing when it is not used.
    /// </remarks>
    public int GroupedBodyCount => subGroupOwners.Count;

    /// <summary>Stops two bodies from colliding with one another, or allows it again.</summary>
    /// <param name="first">One body.</param>
    /// <param name="second">The other.</param>
    /// <param name="enabled">Whether the two may collide.</param>
    /// <exception cref="PhysicsHandleException">Either handle is stale.</exception>
    /// <remarks>
    ///     <para>
    ///         The body-level facility. A ragdoll needs it for the pairs that have <i>no</i> joint
    ///         between them — the two thighs, an upper arm and the chest — which is exactly the set a
    ///         constraint-level flag cannot reach.
    ///     </para>
    ///     <para>
    ///         Suppressing a body against itself is refused rather than ignored, because it can only
    ///         be a mistake and the alternative is a call that silently does nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ Takes effect on the next step's broad phase. Two bodies already overlapping and
    ///         already being pushed apart stop being pushed; they do not un-push.
    ///     </para>
    /// </remarks>
    public void SetPairCollision(BodyHandle first, BodyHandle second, bool enabled) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (first == second) {
            throw new PhysicsHandleException($"A body cannot be told whether it collides with itself: {first}.");
        }

        var key = PairKey(SubGroupOf(first), SubGroupOf(second));

        if (enabled) {
            suppressedByCaller.Remove(key);
        } else {
            suppressedByCaller.Add(key);
        }

        ApplyPair(key);
    }

    /// <summary>Whether two bodies are allowed to collide, as Jolt itself would answer it.</summary>
    /// <param name="first">One body.</param>
    /// <param name="second">The other.</param>
    /// <returns><see langword="true" /> unless the pair has been suppressed.</returns>
    /// <remarks>
    ///     ⚠ <b>Read out of the native table rather than out of this class's bookkeeping</b>, which is
    ///     the whole point of it existing. A readback that consulted <see cref="suppressedByCaller" />
    ///     would agree with the write that produced it whether or not that write ever reached Jolt —
    ///     and this binding has already shipped one setter that did not
    ///     (<c>BodyCreationSettings.MotionQuality</c>, worked around in <c>CreateBody</c>). This one
    ///     asks the object the solver asks.
    /// </remarks>
    public bool CanBodiesCollide(BodyHandle first, BodyHandle second) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        Check(first);
        Check(second);

        var firstSub = SubGroupIdOf(first);
        var secondSub = SubGroupIdOf(second);

        // Neither has ever been named in a suppression, so no table can have an opinion.
        return pairFilter is null
            || firstSub == NoSubGroup
            || secondSub == NoSubGroup
            || pairFilter.IsCollisionEnabled(new((uint)firstSub), new((uint)secondSub));
    }

    /// <summary>How many grouped bodies name a filter table that is no longer the live one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Zero is a hard invariant, and the failure it guards is undefined behaviour rather
    ///         than a wrong answer.</b> A body's <c>CollisionGroup</c> holds a pointer to one specific
    ///         table, and Jolt asks the <i>first</i> body's filter about a pair. If growth left an old
    ///         body naming a table too small to hold the other body's sub-group id, Jolt indexes a
    ///         bitmask past its end — which does not throw and does not reliably give either answer.
    ///     </para>
    ///     <para>
    ///         It is here because that is not testable any other way. A behavioural test against this
    ///         defect passes or fails on whatever the out-of-range bit happened to be, and one that
    ///         passed by luck is worse than none. So the assertion reads each body's group back out of
    ///         <c>BodyInterface</c> — native state, written by the same call the sabotage removes —
    ///         and compares the filter it names against the live one by address.
    ///     </para>
    /// </remarks>
    internal int StaleFilterCount() {
        if (pairFilter is null) {
            return 0;
        }

        var stale = 0;

        for (var id = 0; id < subGroupOwners.Count; id++) {
            var handle = subGroupOwners[id];

            if (!IsAlive(handle)) {
                continue;
            }

            var bodyId = new BodyID(handle.Value);
            var group = system.BodyInterface.GetCollisionGroup(in bodyId);

            if (group.GroupFilter is null || group.GroupFilter.Handle != pairFilter.Handle) {
                stale++;
            }
        }

        return stale;
    }

    /// <summary>Records that a constraint wants its two bodies not to collide.</summary>
    internal void SuppressForConstraint(BodyHandle first, BodyHandle second) {
        var key = PairKey(SubGroupOf(first), SubGroupOf(second));

        suppressedByConstraint[key] = suppressedByConstraint.GetValueOrDefault(key) + 1;
        ApplyPair(key);
    }

    /// <summary>Takes back one constraint's suppression of its two bodies.</summary>
    /// <remarks>
    ///     ⚠ <b>Refcounted, because two joints over one pair are ordinary</b> — a hinge and a distance
    ///     limiter on the same door, a cone and a twist on the same shoulder. Destroying one of them
    ///     must not let the pair start colliding while the other is still there, and a plain flag
    ///     would. The pair is only handed back to whatever <see cref="SetPairCollision" /> last said
    ///     when the count reaches zero.
    /// </remarks>
    internal void ReleaseForConstraint(BodyHandle first, BodyHandle second) {
        var firstSub = SubGroupIdOf(first);
        var secondSub = SubGroupIdOf(second);

        if (firstSub == NoSubGroup || secondSub == NoSubGroup) {
            return;
        }

        var key = PairKey(firstSub, secondSub);

        if (!suppressedByConstraint.TryGetValue(key, out var count)) {
            return;
        }

        if (count <= 1) {
            suppressedByConstraint.Remove(key);
        } else {
            suppressedByConstraint[key] = count - 1;
        }

        ApplyPair(key);
    }

    /// <summary>Writes one pair's current verdict into the native table.</summary>
    void ApplyPair(long key) {
        if (pairFilter is null) {
            return;
        }

        var first = new CollisionSubGroupID((uint)(key >> 32));
        var second = new CollisionSubGroupID((uint)(key & 0xFFFFFFFF));

        if (suppressedByCaller.Contains(key) || suppressedByConstraint.ContainsKey(key)) {
            pairFilter.DisableCollision(first, second);
        } else {
            pairFilter.EnableCollision(first, second);
        }
    }

    /// <summary>The sub-group id a body already has, or <see cref="NoSubGroup" />.</summary>
    int SubGroupIdOf(BodyHandle handle) {
        var index = handle.Index;

        return index < (uint)slots.Length && slots[index].Handle == handle.Value
            ? slots[index].SubGroup
            : NoSubGroup;
    }

    /// <summary>The sub-group id a body has, giving it one and a filter if this is its first.</summary>
    /// <exception cref="PhysicsHandleException">The handle is stale.</exception>
    int SubGroupOf(BodyHandle handle) {
        var index = Check(handle);

        if (slots[index].SubGroup != NoSubGroup) {
            return slots[index].SubGroup;
        }

        var id = subGroupOwners.Count;
        subGroupOwners.Add(handle);
        slots[index].SubGroup = id;

        // Grown before the body is pointed at it, so the body never names a table too small to hold
        // its own id — which Jolt indexes without checking.
        EnsureCapacity(id + 1);
        PointAtFilter(handle, id);

        return id;
    }

    /// <summary>Makes sure the table can hold a given number of sub-groups, rebuilding if not.</summary>
    /// <remarks>
    ///     A rebuild replays every disabled pair and re-points every member, because a
    ///     <c>GroupFilterTable</c>'s size is fixed at construction and its contents cannot be copied
    ///     out. Doubling, so the replay is amortised and a ragdoll built limb by limb does not rebuild
    ///     once per limb.
    /// </remarks>
    void EnsureCapacity(int wanted) {
        if (pairFilter is not null && wanted <= pairFilterCapacity) {
            return;
        }

        var capacity = Math.Max(16, pairFilterCapacity);

        while (capacity < wanted) {
            capacity *= 2;
        }

        var table = new GroupFilterTable((uint)capacity);
        filterTables.Add(table);

        pairFilter = table;
        pairFilterCapacity = capacity;

        // Every body first, so no live body is left naming the table that is about to stop being the
        // one the pairs are written into. Then the pairs, which is cheap: the set is the suppressions
        // that exist, not the pairs that could.
        for (var id = 0; id < subGroupOwners.Count; id++) {
            PointAtFilter(subGroupOwners[id], id);
        }

        foreach (var key in suppressedByCaller) {
            ApplyPair(key);
        }

        foreach (var key in suppressedByConstraint.Keys) {
            ApplyPair(key);
        }
    }

    /// <summary>Gives one body the current table, the shared group id and its own sub-group.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the body interface rather than through <c>BodyCreationSettings</c></b>, and not
    ///     only because a body joins a group long after it is created. It is also the path already
    ///     proved to work: <c>BodyCreationSettings.MotionQuality</c> does not reach the native
    ///     settings object in this binding, and the same shape of setter is not a thing to bet a
    ///     silent failure on twice.
    /// </remarks>
    void PointAtFilter(BodyHandle handle, int subGroup) {
        if (!IsAlive(handle)) {
            return;
        }

        var id = new BodyID(handle.Value);
        var group = new CollisionGroup(pairFilter!, SharedGroup, new((uint)subGroup));

        system.BodyInterface.SetCollisionGroup(in id, in group);
    }

    /// <summary>One key for a pair, ordered so that the two arguments may arrive either way round.</summary>
    static long PairKey(int first, int second) {
        var low = Math.Min(first, second);
        var high = Math.Max(first, second);

        return ((long)low << 32) | (uint)high;
    }

    /// <summary>Frees every table, after every body that named one is gone.</summary>
    void DisposeFilterTables() {
        foreach (var table in filterTables) {
            table.Dispose();
        }

        filterTables.Clear();
        pairFilter = null;
        pairFilterCapacity = 0;
    }
}
