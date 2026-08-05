// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Quests;

namespace Vixen.Live.Gameplay;

/// <summary>A character's journal and its objective counters, as a profile section.</summary>
/// <remarks>
///     <para>
///         <b>Counters, so the profile</b> — see <see cref="CheckpointPolicy" />. A quest's stage
///         written twice is still that stage. What a quest <em>pays</em> is assets and goes through
///         the ledger, which is why nothing about a reward is stored here: the turn-in is a ledger
///         write with an idempotency key, and a journal that also remembered "paid" would be a second
///         record of the same fact that could disagree with the first.
///     </para>
///     <para>
///         ⚠ <b>The history is written for quests this build no longer has; the active list is not.</b>
///         That looks inconsistent and is the point. History is what <c>QuestRepeat.Once</c> reads, so
///         losing an id lets somebody take a one-off quest again — and an id is all it needs. An
///         <em>active</em> quest with no template has no stages, no objectives and no tags, so there
///         is nothing to hold; the bytes stay in the profile and the quest comes back on a build that
///         knows it.
///     </para>
///     <para>
///         ⚠ <b>Loading raises no events, and that is what <c>ObjectiveTracker.Seat</c> is for.</b>
///         Replaying the advances that made this progress would announce every objective again, settle
///         every stage again and fire a reward chain a second time.
///     </para>
/// </remarks>
public sealed class QuestSection : IProfileSection {
    /// <summary>The format this reads and writes.</summary>
    public const int Version = 1;

    readonly QuestJournal journal;
    readonly CheckpointPolicy? checkpoint;

    /// <summary>Makes one over a character's journal.</summary>
    /// <param name="journal">Theirs.</param>
    /// <param name="checkpoint">What to tell when something moves, or null for a test.</param>
    public QuestSection(QuestJournal journal, CheckpointPolicy? checkpoint = null) {
        ArgumentNullException.ThrowIfNull(journal);

        this.journal = journal;
        this.checkpoint = checkpoint;

        // Subscribed rather than asked, because a journal is event-driven all the way down: an
        // objective advances from the bus and nothing calls the realm to say so.
        journal.Changed += _ => this.checkpoint?.Touch();
        journal.Advanced += (_, _) => this.checkpoint?.Touch();
    }

    /// <inheritdoc />
    public ProfileSectionId Id => ProfileSections.Quests;

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Save() {
        var writer = new ProfileWriter(128);
        var active = journal.Active.OrderBy(entry => entry.Id.Value).ToArray();
        var history = journal.History.ToArray();

        writer.Int32(Version);
        writer.Int32(active.Length);

        foreach (var entry in active) {
            writer.UInt32(entry.Id.Value);
            writer.Int32((int)entry.Status);
            writer.Int32(entry.Stage);
            writer.Single(entry.Elapsed);

            var tracker = entry.Tracker;
            var objectives = tracker?.Count ?? 0;

            // ⚠ Both fields, always, even for a quest with no tracker at all — a stage with no
            // objectives has one with a Count of zero and a quest that is ready to hand in has none.
            // Writing the failure only sometimes and reading it back on a different condition is a
            // desync that shows up as the *next* quest's bytes being read as this one's.
            writer.Int32(objectives);
            writer.Int32(tracker is { IsFailed: true } ? tracker.FailedBy : int.MinValue);

            for (var index = 0; index < objectives; index++) {
                // Exact rather than ProgressOf: a timed objective's progress is fractional seconds,
                // and truncating it on every save means a player who logs in and out often enough
                // never finishes one.
                writer.Single(tracker!.Exact(index));
                writer.Int32(tracker.IsCompleteAt(index) ? 1 : 0);
            }
        }

        writer.Int32(history.Length);

        foreach (var (quest, status) in history) {
            writer.UInt32(quest.Value);
            writer.Int32((int)status);
        }

        return writer.Written();
    }

    /// <inheritdoc />
    public void Load(ReadOnlyMemory<byte> bytes) {
        var reader = new ProfileReader(bytes.Span);

        if (bytes.Length == 0 || reader.Int32() != Version) {
            return;
        }

        for (var index = reader.Count(24); index > 0 && !reader.IsDone; index--) {
            var quest = new DefId(reader.UInt32());
            var status = (QuestStatus)reader.Int32();
            var stage = reader.Int32();
            var elapsed = reader.Single();
            var objectives = reader.Count(8);
            var failedBy = reader.Int32();

            // Seated even when the quest is unknown, so the objectives that follow are still read
            // past — a reader that stopped here would drop the history behind them too.
            var entry = journal.Seat(quest, stage, elapsed);

            for (var objective = 0; objective < objectives && !reader.IsDone; objective++) {
                // ⚠ Read into locals first. `entry?.Tracker?.Seat(objective, reader.Single(), …)`
                // reads like the same thing and is not: a null-conditional short-circuits the whole
                // expression, so for a quest this build has lost the arguments never run — and the
                // bytes they would have consumed are read as the *next* quest's.
                var progress = reader.Single();
                var completed = reader.Int32() != 0;

                entry?.Tracker?.Seat(objective, progress, completed);
            }

            if (entry?.Tracker is { } tracker && failedBy != int.MinValue) {
                tracker.SeatFailure(true, failedBy);
            }

            // After the progress, because Seat begins the stage and a stage begins Active.
            if (entry is not null && status == QuestStatus.ReadyToTurnIn) {
                QuestJournal.SeatReady(entry);
            }
        }

        for (var index = reader.Count(8); index > 0 && !reader.IsDone; index--) {
            journal.SeatHistory(new(reader.UInt32()), (QuestStatus)reader.Int32());
        }
    }
}
