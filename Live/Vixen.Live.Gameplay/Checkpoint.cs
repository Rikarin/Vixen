// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Gameplay;
using Vixen.Gameplay.Loot;

namespace Vixen.Live.Gameplay;

/// <summary>Why a character is being written down.</summary>
public enum CheckpointReason {
    /// <summary>Nothing needs writing.</summary>
    None,

    /// <summary>Enough time has passed.</summary>
    Cadence,

    /// <summary>They are leaving for another realm, and the next holder reads what is stored.</summary>
    Transfer,

    /// <summary>They are logging out.</summary>
    Logout,

    /// <summary>Somebody asked.</summary>
    Manual
}

/// <summary>When a character's counters are written down.</summary>
/// <remarks>
///     <para>
///         <b>Counters go in the profile and assets go in the ledger</b> — see
///         <see cref="LedgerBridge" /> for the other half. The difference decides the cadence: a
///         quantity of an asset can be duplicated, so every movement of one is a row; a level cannot,
///         because writing 42 twice leaves you at 42. So a level is written on a cadence and a crash
///         loses at most one interval of experience.
///     </para>
///     <para>
///         ⚠ <b>That loss is the correct trade rather than a compromise.</b> Making a counter durable
///         per kill puts a database round trip on the combat path, which ADR-016 forbids outright —
///         <em>"a frame that awaits one has a p99 measured in milliseconds and a p99.9 measured in
///         seconds"</em>.
///     </para>
///     <para>
///         ⚠ <b>A failed write leaves it dirty <em>and</em> does not restart the clock.</b> Clearing
///         the flag loses the interval for good; restarting the clock means a store that is briefly
///         unhappy is retried a whole cadence later, which turns a five-second outage into five
///         minutes of lost progress.
///     </para>
///     <para>
///         ⚠ <b>Transfer and logout write only when there is something to write.</b> "Always on
///         transfer" reads as unconditional and should not be: a character nobody changed has the
///         same bytes stored, and a round trip to write them is a round trip inside the overlap
///         window L2 spends on loading a map.
///     </para>
/// </remarks>
public sealed class CheckpointPolicy {
    CheckpointReason forced;

    /// <summary>Makes one.</summary>
    /// <param name="cadence">How long between writes. Zero for on-demand only.</param>
    public CheckpointPolicy(TimeSpan cadence) => Cadence = cadence;

    /// <summary>How long between writes.</summary>
    public TimeSpan Cadence { get; }

    /// <summary>Whether anything has changed since the last successful write.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>When the last successful write happened.</summary>
    public DateTimeOffset Written { get; private set; }

    /// <summary>How many writes have failed in a row.</summary>
    public int Failures { get; private set; }

    /// <summary>How many have succeeded.</summary>
    public int Writes { get; private set; }

    /// <summary>Says something changed.</summary>
    public void Touch() => IsDirty = true;

    /// <summary>Says a write must happen at the next opportunity.</summary>
    /// <param name="reason">Why.</param>
    public void Force(CheckpointReason reason) {
        if (reason != CheckpointReason.None) {
            forced = reason;
        }
    }

    /// <summary>Whether to write now.</summary>
    /// <param name="now">The clock.</param>
    /// <param name="reason">Why, or <see cref="CheckpointReason.None" />.</param>
    /// <returns>Whether to write.</returns>
    public bool Due(DateTimeOffset now, out CheckpointReason reason) {
        reason = CheckpointReason.None;

        // Nothing to write. A forced reason is kept rather than consumed, so a transfer that finds a
        // clean character still forces the first write after anything changes.
        if (!IsDirty) {
            return false;
        }

        if (forced != CheckpointReason.None) {
            reason = forced;

            return true;
        }

        if (Cadence > TimeSpan.Zero && now - Written >= Cadence) {
            reason = CheckpointReason.Cadence;

            return true;
        }

        return false;
    }

    /// <summary>Says a write landed.</summary>
    /// <param name="now">When.</param>
    public void Wrote(DateTimeOffset now) {
        IsDirty = false;
        forced = CheckpointReason.None;
        Failures = 0;
        Written = now;
        Writes++;
    }

    /// <summary>Says a write did not land.</summary>
    /// <remarks>
    ///     ⚠ It stays dirty, it stays forced, and <see cref="Written" /> does not move — so the next
    ///     <see cref="Due" /> says yes again immediately rather than in a cadence's time.
    /// </remarks>
    public void Failed() => Failures++;

    /// <summary>Starts the clock without writing. What loading a character does.</summary>
    /// <param name="now">The clock.</param>
    public void Loaded(DateTimeOffset now) {
        IsDirty = false;
        forced = CheckpointReason.None;
        Written = now;
    }
}

/// <summary>Runs of bad luck, kept in the character's profile.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28 is firm that this has to be durable</b> — <em>"a pity counter that resets on a
///         realm crash is a support ticket"</em> — and it is a counter rather than an asset, so it
///         belongs in the profile and not in the ledger. A pity count written twice is the same
///         count; a sword written twice is two swords.
///     </para>
///     <para>
///         ⚠ <b>Synchronous, and it can be, which the ledger could not.</b> A loot roll asks for a
///         count mid-frame and the answer is in memory; what makes it durable is the checkpoint
///         underneath, not a round trip here. That is exactly why counters and assets are stored
///         differently.
///     </para>
///     <para>
///         ⚠ <b>A hit clears the count rather than decrementing it.</b> Pity exists to bound a run of
///         bad luck, and a run that has ended is zero — decrementing would carry a hundred failures
///         into the next hundred attempts and make the guarantee unbounded.
///     </para>
///     <para>
///         ⚠ <b>One of these is one character's, and <see cref="PityKey.Player" /> is ignored.</b> A
///         profile already names the character, so the player half is redundant — and storing it
///         would put a realm-scoped gameplay id in the database, which is the one thing
///         <see cref="IGameplayIdentity" /> exists to prevent. The failure that would cause is quiet
///         and exactly the support ticket doc 28 named: after a transfer the id is different, every
///         lookup misses, and a character's pity counters read zero with the rows still sitting in
///         the profile.
///     </para>
/// </remarks>
public sealed class ProfilePityStore : IPityStore, IProfileSection {
    readonly Dictionary<uint, int> attempts = [];
    readonly CheckpointPolicy? checkpoint;

    /// <summary>Makes one.</summary>
    /// <param name="checkpoint">What to tell when a count moves, or null for a test.</param>
    public ProfilePityStore(CheckpointPolicy? checkpoint = null) => this.checkpoint = checkpoint;

    /// <inheritdoc />
    public ProfileSectionId Id => ProfileSections.Pity;

    /// <summary>How many tables this character has a run going on.</summary>
    public int Count => attempts.Count;

    /// <inheritdoc />
    public int AttemptsOf(PityKey key) => attempts.GetValueOrDefault(key.Table.Value);

    /// <inheritdoc />
    public void Record(PityKey key, bool hit) {
        if (hit) {
            // Cleared, not decremented — see the remarks on the type.
            if (attempts.Remove(key.Table.Value)) {
                checkpoint?.Touch();
            }

            return;
        }

        attempts[key.Table.Value] = attempts.GetValueOrDefault(key.Table.Value) + 1;
        checkpoint?.Touch();
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Save() {
        if (attempts.Count == 0) {
            return default;
        }

        var bytes = new byte[4 + (attempts.Count * 8)];
        var span = bytes.AsSpan();

        BinaryPrimitives.WriteInt32LittleEndian(span, attempts.Count);

        var offset = 4;

        // Ordered, so two realms holding the same counts write the same bytes and a checkpoint on an
        // unchanged character is a no-op rather than a rewrite.
        foreach (var (table, count) in attempts.OrderBy(pair => pair.Key)) {
            BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], table);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 4)..], count);
            offset += 8;
        }

        return bytes;
    }

    /// <inheritdoc />
    public void Load(ReadOnlyMemory<byte> bytes) {
        attempts.Clear();

        if (bytes.Length < 4) {
            return;
        }

        var span = bytes.Span;
        var count = BinaryPrimitives.ReadInt32LittleEndian(span);
        var offset = 4;

        for (var index = 0; index < count && offset + 8 <= span.Length; index++) {
            attempts[BinaryPrimitives.ReadUInt32LittleEndian(span[offset..])] =
                BinaryPrimitives.ReadInt32LittleEndian(span[(offset + 4)..]);
            offset += 8;
        }
    }
}
