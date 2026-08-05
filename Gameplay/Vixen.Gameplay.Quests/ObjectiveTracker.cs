// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Quests;

/// <summary>An objective moved.</summary>
/// <param name="Objective">Which one, by index.</param>
/// <param name="Amount">By how much. Negative for a level that went down.</param>
/// <param name="Instigator">Who did it.</param>
/// <param name="Completed">Whether that finished it — true exactly once per objective.</param>
public readonly record struct ObjectiveAdvance(int Objective, int Amount, PlayerId Instigator, bool Completed);

/// <summary>
///     A set of objectives, their progress, and the subscriptions that move it. What a quest stage and
///     a dynamic event are both made of.
/// </summary>
/// <remarks>
///     <para>
///         <b>One tracker, two owners, and that is doc 28's "the same machine with the scope
///         moved".</b> A quest stage tracks with an owner set, so only that player's kills count; a
///         dynamic event tracks with no owner, so everybody's do. Nothing else about them differs, and
///         writing it twice would be two implementations of "no objective completes twice".
///     </para>
///     <para>
///         ⚠ <b>Completion is latched.</b> Progress is a number that can move either way — a
///         <c>Collect</c> objective is a level, and a rescaled event can want more than it did a minute
///         ago — but <em>finished</em> is a one-way door. Without the latch, selling an ore un-finishes
///         a stage that has already advanced, and doc 28's property that no objective completes twice
///         becomes a property that it completes as often as a player likes.
///     </para>
///     <para>
///         ⚠ <b>It unsubscribes on <see cref="Dispose" /> and a caller must.</b> A tracker holds one
///         subscription per objective plus one per failure verb, and a stage that advanced without
///         dropping its old tracker would go on counting kills for an objective nobody can see.
///     </para>
/// </remarks>
public sealed class ObjectiveTracker : IDisposable {
    readonly ObjectiveTemplate[] objectives;
    readonly int[] required;
    readonly float[] progress;
    readonly bool[] completed;
    readonly List<GameplayEventSubscription> subscriptions = [];
    readonly GameplayEventBus bus;
    readonly PlayerId owner;

    bool disposed;

    /// <summary>Starts tracking, subscribing immediately.</summary>
    /// <param name="bus">Where the events come from.</param>
    /// <param name="objectives">What to track.</param>
    /// <param name="owner">Whose events count, or <see cref="PlayerId.None" /> for everybody's.</param>
    /// <param name="scale">What to multiply the counts by, for a scaled event.</param>
    public ObjectiveTracker(
        GameplayEventBus bus,
        ReadOnlySpan<ObjectiveTemplate> objectives,
        PlayerId owner = default,
        float scale = 1f
    ) {
        ArgumentNullException.ThrowIfNull(bus);

        this.bus = bus;
        this.owner = owner;
        this.objectives = objectives.ToArray();
        required = new int[objectives.Length];
        progress = new float[objectives.Length];
        completed = new bool[objectives.Length];

        for (var index = 0; index < this.objectives.Length; index++) {
            required[index] = Required(this.objectives[index], scale);
        }

        Subscribe();
    }

    /// <summary>How many objectives there are.</summary>
    public int Count => objectives.Length;

    /// <summary>How many of them have to be finished.</summary>
    public int RequiredCount => objectives.Count(objective => !objective.IsOptional);

    /// <summary>Whether every non-optional objective is finished.</summary>
    public bool IsComplete {
        get {
            for (var index = 0; index < objectives.Length; index++) {
                if (!objectives[index].IsOptional && !completed[index]) {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Whether something failed it.</summary>
    public bool IsFailed { get; private set; }

    /// <summary>Which objective failed it, or −1.</summary>
    public int FailedBy { get; private set; } = -1;

    /// <summary>Raised when an objective moves.</summary>
    public event Action<ObjectiveAdvance>? Advanced;

    /// <summary>Raised the first time something fails it.</summary>
    public event Action<int>? Failed;

    /// <summary>What it is tracking.</summary>
    public ReadOnlySpan<ObjectiveTemplate> Objectives => objectives;

    /// <summary>How far along an objective is.</summary>
    /// <param name="index">Which one.</param>
    /// <returns>The progress, never above what is required.</returns>
    public int ProgressOf(int index) =>
        (uint)index < (uint)progress.Length ? Math.Min((int)progress[index], required[index]) : 0;

    /// <summary>How much an objective needs.</summary>
    /// <param name="index">Which one.</param>
    /// <returns>The count, scaled.</returns>
    public int RequiredOf(int index) => (uint)index < (uint)required.Length ? required[index] : 0;

    /// <summary>Whether an objective is finished.</summary>
    /// <param name="index">Which one.</param>
    /// <returns>Whether it is.</returns>
    public bool IsCompleteAt(int index) => (uint)index < (uint)completed.Length && completed[index];

    /// <summary>Advances the timed objectives.</summary>
    /// <param name="delta">How much time passed, in seconds.</param>
    /// <returns>How many objectives moved.</returns>
    public int Tick(float delta) {
        if (IsFailed || delta <= 0f) {
            return 0;
        }

        var moved = 0;

        for (var index = 0; index < objectives.Length; index++) {
            if (!objectives[index].IsTimed || completed[index]) {
                continue;
            }

            var before = ProgressOf(index);

            progress[index] += delta;

            var latched = Latch(index);
            var after = ProgressOf(index);

            // A whole second has to pass before a survival objective reports anything: the tracker
            // counts in seconds and a frame is a fraction of one, so reporting every tick would be
            // sixty "progress" notifications a second for a number that has not changed.
            if (after == before && !latched) {
                continue;
            }

            Advanced?.Invoke(new(index, after - before, owner, completed[index]));
            moved++;
        }

        return moved;
    }

    /// <summary>Raises what the objectives need, for an event that grew.</summary>
    /// <param name="scale">The new multiplier.</param>
    /// <returns>How many objectives now need more.</returns>
    /// <remarks>
    ///     ⚠ <b>It raises and never lowers.</b> A requirement that fell when somebody left would let an
    ///     objective complete because a player logged out, and one that fell below the progress already
    ///     made would complete it retroactively — which is the "completes twice" bug wearing a hat.
    /// </remarks>
    public int Rescale(float scale) {
        var raised = 0;

        for (var index = 0; index < objectives.Length; index++) {
            var wanted = Required(objectives[index], scale);

            if (wanted <= required[index]) {
                continue;
            }

            required[index] = wanted;
            raised++;
        }

        return raised;
    }

    /// <summary>Fails it, as a stage's clock running out does.</summary>
    /// <param name="objective">Which objective is to blame, or −1 for none.</param>
    /// <returns>Whether this is what failed it.</returns>
    public bool Fail(int objective = -1) {
        if (IsFailed) {
            return false;
        }

        IsFailed = true;
        FailedBy = objective;
        Failed?.Invoke(objective);

        return true;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var subscription in subscriptions) {
            bus.Unsubscribe(subscription);
        }

        subscriptions.Clear();
    }

    static int Required(ObjectiveTemplate objective, float scale) =>
        Math.Max(1, (int)MathF.Ceiling(objective.Count * MathF.Max(1f, scale)));

    void Subscribe() {
        for (var slot = 0; slot < objectives.Length; slot++) {
            var index = slot;
            var objective = objectives[index];

            if (objective.Filter.IsSome) {
                subscriptions.Add(bus.Subscribe(objective.Filter, (in GameplayEvent e) => Observe(index, e)));
            }

            if (objective.FailFilter.IsSome) {
                subscriptions.Add(
                    bus.Subscribe(
                        objective.FailFilter,
                        (in GameplayEvent e) => {
                            if (Counts(e) && !completed[index]) {
                                Fail(index);
                            }
                        }
                    )
                );
            }
        }
    }

    void Observe(int index, in GameplayEvent gameplayEvent) {
        if (IsFailed || completed[index] || !Counts(gameplayEvent)) {
            return;
        }

        var objective = objectives[index];
        var amount = objective.Kind.Advance(objective, gameplayEvent);

        if (amount == 0) {
            return;
        }

        if (amount < 0 && !objective.IsLevel) {
            // ⚠ A tally never goes backwards. An event that reported a negative amount against a Kill
            // objective would be somebody un-killing something, and the safe reading of a poster this
            // library does not control is to ignore it rather than to give the credit back.
            return;
        }

        var before = ProgressOf(index);

        progress[index] = MathF.Max(0f, progress[index] + amount);
        Latch(index);

        Advanced?.Invoke(new(index, ProgressOf(index) - before, gameplayEvent.Instigator, completed[index]));
    }

    bool Counts(in GameplayEvent gameplayEvent) => !owner.IsSome || gameplayEvent.Instigator == owner;

    bool Latch(int index) {
        if (completed[index] || progress[index] < required[index]) {
            return false;
        }

        completed[index] = true;

        return true;
    }
}
