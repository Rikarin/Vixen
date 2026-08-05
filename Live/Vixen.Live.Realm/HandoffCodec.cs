// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Live.Transfer;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;

namespace Vixen.Live.Realm;

/// <summary>Writes and reads the volatile half of a player, with the codec that writes snapshots.</summary>
/// <remarks>
///     <para>
///         Doc 27 § The overlap: <i>"the handoff payload reuses the replication codec"</i>, and it is
///         not a convenience. Four things fall out of it and none would be free otherwise:
///     </para>
///     <list type="bullet">
///         <item>a component that replicates transfers with no extra code;</item>
///         <item>the encoding is already fuzzed and already pinned by doc 16's wire corpus;</item>
///         <item>the bit-exactness gate covers realm-to-realm agreement for nothing;</item>
///         <item>there is one wire format for a component rather than two that can drift.</item>
///     </list>
///     <para>
///         ⚠ <b>Nothing durable is in here.</b> ADR-021: inventory, currency and progression stay in
///         the database behind <c>IPlayerGrain</c>, and what travels is position, velocity, buffs
///         with their remaining durations, cooldowns, combat state, the animation graph's position.
///         If this payload is lost, an abort costs the player nothing — which is the property that
///         makes every failure path in <see cref="SourceTransfer" /> cheap.
///     </para>
///     <para>
///         ⚠ <b>The two realms must agree about which replicators exist and in what order.</b> They
///         do, because they are the same build — ADR-022 makes the version pair a placement filter,
///         so a shard on another build is one this player is never sent to. The type id is written
///         anyway and checked on the way in, because "cannot happen" and "is not checked" should not
///         be the same sentence.
///     </para>
/// </remarks>
public static class HandoffCodec {
    /// <summary>The format this writes. Bumped when the framing changes, not when a component does.</summary>
    public const byte Version = 1;

    /// <summary>Encodes everything a replicator will carry for one entity.</summary>
    /// <param name="world">The world it lives in.</param>
    /// <param name="entity">The player's entity.</param>
    /// <param name="replicators">What to write, in a stable order.</param>
    /// <param name="buffer">Scratch. Sized by the caller; 4 KB is generous for one player.</param>
    /// <returns>The payload, or empty if it did not fit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="replicators" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A component the entity does not have is skipped rather than written empty</b>, and the
    ///     count says how many followed. A fixed slot per replicator would make the payload grow with
    ///     the game's component vocabulary rather than with what this player actually is.
    /// </remarks>
    public static ImmutableArray<byte> Write(
        World world,
        Entity entity,
        IReadOnlyList<IComponentReplicator> replicators,
        Span<byte> buffer
    ) {
        ArgumentNullException.ThrowIfNull(replicators);

        var writer = new BitWriter(buffer);

        writer.Write(Version, 8);

        // The count is written before the records, so a reader knows when to stop without a
        // terminator that a component's own bits could accidentally spell.
        var present = 0;

        for (var index = 0; index < replicators.Count; index++) {
            if (replicators[index].Has(world, entity)) {
                present++;
            }
        }

        writer.WriteVariable((uint)present);

        foreach (var replicator in replicators) {
            if (!replicator.Has(world, entity)) {
                continue;
            }

            writer.WriteUInt32(replicator.TypeId);
            replicator.Write(world, entity, ref writer);
        }

        if (writer.Overflowed) {
            return [];
        }

        return [.. buffer[..writer.BytesWritten]];
    }

    /// <summary>Applies a payload to an entity on the receiving realm.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity to become this player.</param>
    /// <param name="payload">What <see cref="Write" /> produced.</param>
    /// <param name="replicators">What this build knows how to read.</param>
    /// <param name="applied">How many components were put on.</param>
    /// <returns>Whether the whole payload was well-formed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="replicators" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A payload that does not read cleanly leaves the transfer to fail rather than
    ///         half-applying.</b> The source has not committed yet — it is waiting for the
    ///         acknowledgement this cannot now send — so refusing is a transfer that did not happen,
    ///         and the player is still being simulated where they were. Applying half of one would be
    ///         the only way this design could produce a player who is somewhere with the wrong body.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which takes two passes, because there is no third option.</b> A record is not
    ///         length-prefixed and a replicator's only entry point is <c>Apply</c>, so the only way to
    ///         find out whether the whole payload reads is to read the whole payload — and reading it
    ///         is writing it. So it is read onto a scratch entity first and onto the real one only if
    ///         all of it landed. The scratch entity is created and destroyed inside this call and is
    ///         never seen by a system; the alternative, undoing what the first pass did, cannot be
    ///         written at all, because <c>Apply</c> adds a component and nothing records what was
    ///         there before.
    ///     </para>
    ///     <para>
    ///         The second pass cannot fail where the first did not: it is the same bytes through the
    ///         same replicators, and a replicator's answer is a property of the bits rather than of
    ///         the entity it lands on. It is checked anyway, and a disagreement is reported as a
    ///         refusal, because that assumption is about somebody else's implementation.
    ///     </para>
    /// </remarks>
    public static bool Apply(
        World world,
        Entity entity,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<IComponentReplicator> replicators,
        out int applied
    ) {
        ArgumentNullException.ThrowIfNull(replicators);
        ArgumentNullException.ThrowIfNull(world);

        applied = 0;

        var rehearsal = world.Create();

        try {
            if (!Walk(world, rehearsal, payload, replicators, out _)) {
                return false;
            }
        } finally {
            world.Destroy(rehearsal);
        }

        return Walk(world, entity, payload, replicators, out applied);
    }

    /// <summary>Reads the payload onto one entity, stopping at the first thing that does not read.</summary>
    static bool Walk(
        World world,
        Entity entity,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<IComponentReplicator> replicators,
        out int applied
    ) {
        applied = 0;

        var reader = new BitReader(payload);

        if (!reader.TryRead(8, out var version) || version != Version) {
            return false;
        }

        if (!reader.TryReadVariable(out var count)) {
            return false;
        }

        for (var index = 0u; index < count; index++) {
            if (!reader.TryReadUInt32(out var typeId)) {
                return false;
            }

            var replicator = Find(replicators, typeId);

            // An unknown type id cannot be skipped: the records are not length-prefixed, so there is
            // no way to find where the next one starts. Refusing is the honest answer, and ADR-022
            // is what makes it unreachable in a fleet whose versions are filtered.
            if (replicator is null || !replicator.Apply(world, entity, ref reader)) {
                return false;
            }

            applied++;
        }

        return !reader.Failed;
    }

    /// <summary>Builds the payload for a whole handoff.</summary>
    /// <param name="player">Whose.</param>
    /// <param name="epoch">The lease epoch the target will hold.</param>
    /// <param name="tick">The source tick the state was sampled at.</param>
    /// <param name="world">The world.</param>
    /// <param name="entity">Their entity.</param>
    /// <param name="replicators">What to carry.</param>
    /// <param name="buffer">Scratch.</param>
    /// <returns>The message the source sends at t5.</returns>
    public static RealmHandoff Handoff(
        PlayerKey player,
        long epoch,
        long tick,
        World world,
        Entity entity,
        IReadOnlyList<IComponentReplicator> replicators,
        Span<byte> buffer
    ) =>
        new(player, epoch, tick, Write(world, entity, replicators, buffer));

    static IComponentReplicator? Find(IReadOnlyList<IComponentReplicator> replicators, uint typeId) {
        for (var index = 0; index < replicators.Count; index++) {
            if (replicators[index].TypeId == typeId) {
                return replicators[index];
            }
        }

        return null;
    }
}
