// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Progression;

namespace Vixen.Live.Gameplay;

/// <summary>A character's level, professions, reputations and talents, as a profile section.</summary>
/// <remarks>
///     <para>
///         <b>Every one of these is a counter, so all of it goes in the profile.</b> A level written
///         twice is still that level and a reputation written twice is still that reputation — see
///         <see cref="CheckpointPolicy" /> for why that is the whole storage decision. Nothing here
///         can be duplicated by being written twice, which is what would send it to the ledger.
///     </para>
///     <para>
///         ⚠ <b>It writes what the character has, not what this build understands.</b> A profession
///         or a tree the running build has no definition for is carried through byte for byte. Doc 27
///         § Upgrades has an old realm and a new realm writing the same character during a rollout,
///         and a codec that dropped what it did not recognise would delete a new profession every
///         time somebody zoned onto an old shard — silently, and only for some players.
///     </para>
///     <para>
///         ⚠ <b>Loading goes through <c>ProgressionState</c>'s seating methods and never its rules.</b>
///         <c>SetLevel</c> zeroes experience, <c>Train</c> clamps to a track and <c>Allocate</c>
///         re-validates a build — right for play, wrong for a load. See <c>ProgressionState.Seat</c>.
///     </para>
/// </remarks>
public sealed class ProgressionSection : IProfileSection {
    /// <summary>The format this reads and writes.</summary>
    public const int Version = 1;

    readonly ProgressionState state;
    readonly CheckpointPolicy? checkpoint;

    /// <summary>Makes one over a character's progression.</summary>
    /// <param name="state">Theirs.</param>
    /// <param name="checkpoint">What to tell when something moves, or null for a test.</param>
    public ProgressionSection(ProgressionState state, CheckpointPolicy? checkpoint = null) {
        ArgumentNullException.ThrowIfNull(state);

        this.state = state;
        this.checkpoint = checkpoint;
    }

    /// <inheritdoc />
    public ProfileSectionId Id => ProfileSections.Progression;

    /// <summary>Says something moved, so the next checkpoint writes.</summary>
    /// <remarks>
    ///     Called by the realm rather than by the state, because <c>ProgressionState</c> is
    ///     <c>Gameplay/</c>'s and knows nothing about a checkpoint. A realm that awards experience
    ///     and forgets this loses the interval — which is why <c>PlayerProfile.Set</c> compares bytes
    ///     as a second line of defence rather than as the first.
    /// </remarks>
    public void Touch() => checkpoint?.Touch();

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Save() {
        var writer = new ProfileWriter(96);

        writer.Int32(Version);
        writer.Int32(state.Level);
        writer.Int32(state.Experience);
        writer.Int64(state.TotalExperience);
        writer.Int32(state.TalentPoints);
        writer.UInt32(state.Specialisation.Value);

        Write(ref writer, state.Skills);
        Write(ref writer, state.Standings);

        var trees = state.Allocations.ToArray();

        writer.Int32(trees.Length);

        foreach (var (tree, allocation) in trees) {
            writer.UInt32(tree.Value);
            writer.Int32(allocation.Count);

            // ⚠ Node names are strings and are ordered here, because a Dictionary's order is not a
            // promise. Two realms holding the same build have to produce the same bytes or every
            // checkpoint looks like a change.
            foreach (var (node, ranks) in allocation.Ranks.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
                writer.Text(node);
                writer.Int32(ranks);
            }
        }

        return writer.Written();
    }

    /// <inheritdoc />
    public void Load(ReadOnlyMemory<byte> bytes) {
        var reader = new ProfileReader(bytes.Span);

        if (bytes.Length == 0 || reader.Int32() != Version) {
            // ⚠ A version this build does not read is left alone rather than guessed at, and the
            // bytes stay in the profile untouched because the container holds them, not this. A
            // character who zones back to a build that understands them finds them intact.
            return;
        }

        var level = reader.Int32();
        var experience = reader.Int32();
        var total = reader.Int64();

        state.TalentPoints = reader.Int32();
        state.SeatSpecialisation(new(reader.UInt32()));

        // Read in one order and seated in another: TalentPoints is a plain property and the rest all
        // refresh themselves, so the sequence here follows the bytes rather than any dependency.
        state.Seat(level, experience, total);

        for (var index = reader.Count(8); index > 0; index--) {
            state.SeatSkill(new(reader.UInt32()), reader.Int32());
        }

        for (var index = reader.Count(8); index > 0; index--) {
            state.SeatStanding(new(reader.UInt32()), reader.Int32());
        }

        for (var index = reader.Count(8); index > 0 && !reader.IsDone; index--) {
            var tree = new DefId(reader.UInt32());
            var allocation = new TalentAllocation();

            for (var node = reader.Count(8); node > 0 && !reader.IsDone; node--) {
                allocation.Set(reader.Text(), reader.Int32());
            }

            state.SeatTalents(tree, allocation);
        }
    }

    static void Write(ref ProfileWriter writer, IEnumerable<KeyValuePair<DefId, int>> entries) {
        var values = entries.ToArray();

        writer.Int32(values.Length);

        foreach (var (id, value) in values) {
            writer.UInt32(id.Value);
            writer.Int32(value);
        }
    }
}
