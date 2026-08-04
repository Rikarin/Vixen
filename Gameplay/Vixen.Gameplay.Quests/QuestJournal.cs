// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Quests;

/// <summary>Where a quest is.</summary>
public enum QuestStatus {
    /// <summary>Not taken.</summary>
    None,

    /// <summary>Taken, and a stage is running.</summary>
    Active,

    /// <summary>Every stage is done and the reward has not been collected.</summary>
    ReadyToTurnIn,

    /// <summary>Turned in.</summary>
    TurnedIn,

    /// <summary>Failed — a clock ran out, or an escortee died.</summary>
    Failed,

    /// <summary>Given up on.</summary>
    Abandoned
}

/// <summary>Why a quest could not be accepted, advanced or turned in.</summary>
public enum QuestRefusal {
    /// <summary>It could.</summary>
    None,

    /// <summary>This build has no such quest.</summary>
    UnknownQuest,

    /// <summary>It is already on the journal.</summary>
    AlreadyActive,

    /// <summary>It has been done and does not repeat.</summary>
    AlreadyDone,

    /// <summary>A requirement is not met.</summary>
    Requirements,

    /// <summary>It is not on the journal at all.</summary>
    NotActive,

    /// <summary>Its objectives are not finished.</summary>
    NotFinished,

    /// <summary>The reward has a choice and the turn-in did not make one, or made an impossible one.</summary>
    BadChoice
}

/// <summary>What happened to a quest.</summary>
/// <param name="Quest">Which quest.</param>
/// <param name="Status">Where it is now.</param>
/// <param name="Stage">Which stage, or −1.</param>
public readonly record struct QuestChange(DefId Quest, QuestStatus Status, int Stage);

/// <summary>One quest on a journal: which stage, how far into it, and how long it has left.</summary>
public sealed class ActiveQuest : IDisposable {
    internal ActiveQuest(QuestTemplate template) => Template = template;

    /// <summary>What it is.</summary>
    public QuestTemplate Template { get; }

    /// <summary>Which quest.</summary>
    public DefId Id => Template.Id;

    /// <summary>Where it is.</summary>
    public QuestStatus Status { get; internal set; } = QuestStatus.Active;

    /// <summary>Which stage is running, or the last one reached.</summary>
    public int Stage { get; internal set; }

    /// <summary>How long the current stage has been running, in seconds.</summary>
    public float Elapsed { get; internal set; }

    /// <summary>What is tracking the current stage, or null when there is no stage running.</summary>
    public ObjectiveTracker? Tracker { get; internal set; }

    /// <summary>The stage that is running, or null.</summary>
    public StageTemplate? CurrentStage =>
        (uint)Stage < (uint)Template.Stages.Length ? Template.Stages[Stage] : null;

    /// <summary>Whether it is over, however it ended.</summary>
    public bool IsTerminal =>
        Status is QuestStatus.TurnedIn or QuestStatus.Failed or QuestStatus.Abandoned;

    /// <inheritdoc />
    public void Dispose() {
        Tracker?.Dispose();
        Tracker = null;
    }
}

/// <summary>One character's quests: what is taken, how far along it is, and what it has finished.</summary>
/// <remarks>
///     <para>
///         <b>Two collaborators rather than one, and the split is deliberate.</b> A journal evaluates
///         requirements against an <see cref="IRequirementContext" /> and grants its tags into a
///         <see cref="GameplayTagSet" />, and in a real game those are different objects — the level
///         and the profession live in a <c>ProgressionState</c>, the combat state in a
///         <see cref="GameplaySubject" />'s effects. Taking one object would mean either depending on
///         progression, which doc 28's spine forbids, or pretending a character's state has one owner,
///         which it does not. <see cref="CompositeRequirementContext" /> is how a caller puts them
///         together.
///     </para>
///     <para>
///         ⚠ <b>Turning a quest in grants its tag into that set, and a quest chain is a
///         requirement.</b> "You must have finished the prologue" is
///         <c>HasTag(Quest.Completed.Prologue)</c>, which is the same algebra a vendor uses — so the
///         greyed-out giver and the refused accept cannot disagree, and there is no second mechanism
///         for quest chains to be got wrong in.
///     </para>
///     <para>
///         ⚠ <b>Every stage transition drops the old tracker before making the new one.</b> A tracker
///         is a live set of bus subscriptions; leaving one behind is a stage that goes on counting
///         after nobody is looking, and — since the objectives of two stages usually count the same
///         verb — a second stage that starts already half done.
///     </para>
/// </remarks>
public sealed class QuestJournal : IDisposable {
    readonly Dictionary<uint, ActiveQuest> active = [];
    readonly Dictionary<uint, QuestStatus> history = [];
    readonly GameplayTagSet tags;

    /// <summary>Makes a journal for one character.</summary>
    /// <param name="library">Where the quests come from.</param>
    /// <param name="bus">Where the events come from.</param>
    /// <param name="owner">Whose events count. Zero counts everybody's, which is what a test does.</param>
    /// <param name="context">What requirements are evaluated against, or null for none.</param>
    /// <param name="tags">Where quest tags are granted, or null to keep them to itself.</param>
    public QuestJournal(
        QuestLibrary library,
        GameplayEventBus bus,
        ulong owner = 0,
        IRequirementContext? context = null,
        GameplayTagSet? tags = null
    ) {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(bus);

        Library = library;
        Bus = bus;
        Owner = owner;
        Context = context;
        this.tags = tags ?? new GameplayTagSet();
    }

    /// <summary>Where the quests come from.</summary>
    public QuestLibrary Library { get; }

    /// <summary>Where the events come from.</summary>
    public GameplayEventBus Bus { get; }

    /// <summary>Whose events count.</summary>
    public ulong Owner { get; }

    /// <summary>What requirements are evaluated against.</summary>
    public IRequirementContext? Context { get; }

    /// <summary>The tags its quests have granted.</summary>
    public GameplayTagSet Tags => tags;

    /// <summary>Every quest on it, in the order they were accepted.</summary>
    public IEnumerable<ActiveQuest> Active => active.Values;

    /// <summary>How many quests are on it.</summary>
    public int Count => active.Count;

    /// <summary>Raised whenever a quest changes state.</summary>
    public event Action<QuestChange>? Changed;

    /// <summary>Raised whenever an objective moves, with the quest it belongs to.</summary>
    public event Action<DefId, ObjectiveAdvance>? Advanced;

    /// <summary>Where a quest is.</summary>
    /// <param name="quest">Which one.</param>
    /// <returns>Its status.</returns>
    public QuestStatus StatusOf(DefId quest) =>
        active.TryGetValue(quest.Value, out var entry) ? entry.Status : history.GetValueOrDefault(quest.Value);

    /// <summary>One quest on the journal.</summary>
    /// <param name="quest">Which one.</param>
    /// <returns>It, or null.</returns>
    public ActiveQuest? Find(DefId quest) => active.GetValueOrDefault(quest.Value);

    /// <summary>Whether a quest can be accepted, and why not.</summary>
    /// <param name="quest">Which one.</param>
    /// <returns>The refusal, or <see cref="QuestRefusal.None" />.</returns>
    /// <remarks>
    ///     <b>What the client calls to grey out a giver.</b> The same method the realm calls before
    ///     accepting, out of one assembly — doc 28 § Requirements' whole point.
    /// </remarks>
    public QuestRefusal CanAccept(DefId quest) {
        if (Library.FindQuest(quest) is not { } template) {
            return QuestRefusal.UnknownQuest;
        }

        if (active.ContainsKey(quest.Value)) {
            return QuestRefusal.AlreadyActive;
        }

        if (template.Repeat == QuestRepeat.Once && history.GetValueOrDefault(quest.Value) == QuestStatus.TurnedIn) {
            return QuestRefusal.AlreadyDone;
        }

        if (Context is not null && !template.Requirements.IsMetBy(Context)) {
            return QuestRefusal.Requirements;
        }

        return QuestRefusal.None;
    }

    /// <summary>Takes a quest.</summary>
    /// <param name="quest">Which one.</param>
    /// <returns>The refusal, or <see cref="QuestRefusal.None" /> when it was taken.</returns>
    public QuestRefusal Accept(DefId quest) {
        var refusal = CanAccept(quest);

        if (refusal != QuestRefusal.None) {
            return refusal;
        }

        var entry = new ActiveQuest(Library.FindQuest(quest)!);

        active.Add(quest.Value, entry);

        foreach (var tag in entry.Template.GrantsTags) {
            tags.Add(tag);
        }

        Begin(entry, 0);

        return QuestRefusal.None;
    }

    /// <summary>Gives a quest up, dropping its progress and its tags.</summary>
    /// <param name="quest">Which one.</param>
    /// <returns>Whether it was on the journal.</returns>
    public bool Abandon(DefId quest) => Finish(quest, QuestStatus.Abandoned);

    /// <summary>Fails a quest outright. What a scripted defeat does.</summary>
    /// <param name="quest">Which one.</param>
    /// <returns>Whether it was on the journal.</returns>
    public bool Fail(DefId quest) => Finish(quest, QuestStatus.Failed);

    /// <summary>Whether a quest can be turned in, and why not.</summary>
    /// <param name="quest">Which one.</param>
    /// <param name="choice">Which reward was picked, or −1 when there is no choice.</param>
    /// <returns>The refusal, or <see cref="QuestRefusal.None" />.</returns>
    public QuestRefusal CanTurnIn(DefId quest, int choice = -1) {
        if (active.TryGetValue(quest.Value, out var entry)) {
            return entry.Status != QuestStatus.ReadyToTurnIn
                ? QuestRefusal.NotFinished
                : entry.Template.Reward.IsValidChoice(choice)
                    ? QuestRefusal.None
                    : QuestRefusal.BadChoice;
        }

        return QuestRefusal.NotActive;
    }

    /// <summary>Turns a quest in and says what it owes.</summary>
    /// <param name="quest">Which one.</param>
    /// <param name="choice">Which reward was picked, or −1.</param>
    /// <param name="reward">What is owed, or null.</param>
    /// <returns>The refusal, or <see cref="QuestRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>It reports the reward and does not pay it.</b> Doc 28's authority table puts quest
    ///     rewards in the grain, and a journal that handed out an item would be a realm deciding a
    ///     durable question. What comes back is a list; paying it is the caller's ledger transaction.
    /// </remarks>
    public QuestRefusal TurnIn(DefId quest, int choice, out QuestReward? reward) {
        reward = null;

        var refusal = CanTurnIn(quest, choice);

        if (refusal != QuestRefusal.None) {
            return refusal;
        }

        var entry = active[quest.Value];

        reward = entry.Template.Reward;

        // Before the change is announced, not after: a listener that starts the next quest in a chain
        // asks whether its prerequisite tag is held, and it has to already be.
        if (entry.Template.Tag.IsSome) {
            tags.Add(entry.Template.Tag);
        }

        Finish(quest, QuestStatus.TurnedIn);

        return QuestRefusal.None;
    }

    /// <summary>Advances every stage clock and every timed objective.</summary>
    /// <param name="delta">How much time passed, in seconds.</param>
    /// <returns>How many quests changed state.</returns>
    /// <remarks>
    ///     ⚠ <b>The only thing on this type that costs anything per frame, and it is per <em>timed</em>
    ///     objective.</b> Everything else is a subscription: a journal of forty quests with no clocks
    ///     does no work at all between events, which is doc 28's "costs nothing when nothing dies".
    /// </remarks>
    public int Tick(float delta) {
        if (delta <= 0f || active.Count == 0) {
            return 0;
        }

        var changed = 0;

        // Over a copy, because completing a stage mutates the tracker set and finishing a quest can
        // remove the entry this loop is standing on.
        foreach (var entry in active.Values.ToArray()) {
            if (entry.IsTerminal || entry.Tracker is not { } tracker) {
                continue;
            }

            entry.Elapsed += delta;
            tracker.Tick(delta);

            var limit = entry.CurrentStage?.TimeLimit ?? 0f;

            if (limit > 0f && entry.Elapsed >= limit && !tracker.IsFailed) {
                tracker.Fail();
            }

            if (Settle(entry)) {
                changed++;
            }
        }

        return changed;
    }

    /// <inheritdoc />
    public void Dispose() {
        foreach (var entry in active.Values) {
            entry.Dispose();
        }

        active.Clear();
    }

    void Begin(ActiveQuest entry, int stage) {
        entry.Tracker?.Dispose();
        entry.Tracker = null;
        entry.Stage = stage;
        entry.Elapsed = 0f;

        if (entry.CurrentStage is not { } current) {
            // A quest with no stages left is finished. A quest with no stages at all is finished the
            // moment it is accepted, which the library already reports as a content problem.
            entry.Status = QuestStatus.ReadyToTurnIn;
            Changed?.Invoke(new(entry.Id, entry.Status, stage));

            return;
        }

        var tracker = new ObjectiveTracker(Bus, current.Objectives, Owner);

        tracker.Advanced += advance => {
            Advanced?.Invoke(entry.Id, advance);

            if (advance.Completed) {
                Settle(entry);
            }
        };

        tracker.Failed += _ => Settle(entry);

        entry.Tracker = tracker;
        entry.Status = QuestStatus.Active;

        Changed?.Invoke(new(entry.Id, entry.Status, stage));

        // A stage whose objectives are all already satisfied — one with none, or one a rescale
        // finished — has to settle now rather than waiting for an event that may never come.
        Settle(entry);
    }

    bool Settle(ActiveQuest entry) {
        if (entry.IsTerminal || entry.Tracker is not { } tracker) {
            return false;
        }

        if (tracker.IsFailed) {
            Finish(entry.Id, QuestStatus.Failed);

            return true;
        }

        if (!tracker.IsComplete) {
            return false;
        }

        if (entry.Stage + 1 < entry.Template.Stages.Length) {
            Begin(entry, entry.Stage + 1);

            return true;
        }

        entry.Tracker.Dispose();
        entry.Tracker = null;
        entry.Status = QuestStatus.ReadyToTurnIn;
        Changed?.Invoke(new(entry.Id, entry.Status, entry.Stage));

        return true;
    }

    bool Finish(DefId quest, QuestStatus status) {
        if (!active.Remove(quest.Value, out var entry)) {
            return false;
        }

        entry.Dispose();
        entry.Status = status;
        history[quest.Value] = status;

        foreach (var tag in entry.Template.GrantsTags) {
            tags.Remove(tag);
        }

        Changed?.Invoke(new(quest, status, entry.Stage));

        return true;
    }
}
