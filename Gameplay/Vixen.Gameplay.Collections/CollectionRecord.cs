// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Collections;

/// <summary>How far one criterion has got.</summary>
/// <param name="Achievement">Which achievement.</param>
/// <param name="Criterion">Which of its criteria.</param>
/// <param name="Progress">How far, capped at what it takes.</param>
public readonly record struct CriterionProgress(DefId Achievement, int Criterion, int Progress);

/// <summary>One account's collection: what it has, and how far the rest has got.</summary>
/// <remarks>
///     <para>
///         <b>Account-wide, which is doc 28's word, and the reason there are two types here.</b>
///         Unlocks are <em>"all account-wide, all durable"</em> — a mount earned on one character is
///         owned by all of them. What each character <em>shows</em> is not: two alts have different
///         transmog and different titles from the same collection. So the durable set is this type and
///         the presentation is <see cref="Wardrobe" />, one per character, reading from it.
///     </para>
///     <para>
///         ⚠ <b>One subscription, and a watch is dropped the moment its criterion is done.</b> A naive
///         achievement system subscribes every criterion of every achievement and keeps them for ever,
///         so a mature account pays for thousands of dead filters on every kill. Here the cost falls
///         as an account completes things, which is the opposite curve and the one that matters: the
///         accounts with the most live watches are the new ones, which generate the fewest events.
///     </para>
///     <para>
///         ⚠ <b>An earned achievement never un-earns.</b> Requirements are checked on the way in and
///         never again — a title revoked, an item sold or a patch that raises a threshold must not
///         take back something somebody already did.
///     </para>
///     <para>
///         ⚠ <b>Earning cascades and terminates.</b> An achievement grants a tag and unlocks
///         collectibles, either of which can complete another achievement's requirements; that is
///         resolved in a loop rather than by recursion, and it stops because nothing is ever earned
///         twice.
///     </para>
/// </remarks>
public sealed class CollectionRecord : IRequirementContext {
    /// <summary>The value naming how many of one kind they have — <c>Collection.Mount</c>.</summary>
    /// <remarks>
    ///     <b>What makes "own fifty mounts" one ordinary requirement.</b> The kernel's algebra has a
    ///     tag test and a value test, and a tag test answers "has any", not "has fifty". Rather than a
    ///     third requirement kind that only collections would ever use, the record answers a handful
    ///     of values — one per <see cref="CollectibleKind" />, plus the totals below — so the
    ///     achievement is authored as <c>Value Collection.Mount AtLeast 50</c> and nothing new exists.
    /// </remarks>
    public const string ValuePrefix = "Collection.";

    /// <summary>The value naming how many things they have in all.</summary>
    public const string TotalValue = "Collection.Total";

    /// <summary>The value naming how many points they have.</summary>
    public const string PointsValue = "Collection.Points";

    /// <summary>The value naming how many achievements they have earned.</summary>
    public const string EarnedValue = "Collection.Earned";

    static readonly AttributeId[] KindValues = [
        .. Enum.GetValues<CollectibleKind>().Select(kind => AttributeId.From(ValuePrefix + kind))
    ];

    static readonly AttributeId Total = AttributeId.From(TotalValue);
    static readonly AttributeId PointsOf = AttributeId.From(PointsValue);
    static readonly AttributeId EarnedOf = AttributeId.From(EarnedValue);

    readonly Dictionary<uint, Unlock> unlocks = [];
    readonly Dictionary<uint, int> earned = [];
    readonly Dictionary<uint, int[]> progress = [];
    readonly List<Watch> watches = [];
    readonly HashSet<uint> dirty = [];
    readonly IRequirementContext context;

    GameplayEventSubscription? subscription;
    int order;

    /// <summary>Makes an empty collection.</summary>
    /// <param name="library">Where the collectibles and achievements come from.</param>
    /// <param name="context">What an achievement's requirements are evaluated against as well as its own tags, or null.</param>
    /// <param name="tags">Where the tags an unlock grants go, or null to keep them to itself.</param>
    public CollectionRecord(
        CollectionLibrary library,
        IRequirementContext? context = null,
        GameplayTagSet? tags = null
    ) {
        ArgumentNullException.ThrowIfNull(library);

        Library = library;
        Tags = tags ?? new GameplayTagSet();

        // Its own tags always count — "own fifty mounts" asks about tags this record granted — and a
        // caller's context is composed on top so an achievement can also ask about a level.
        this.context = context is null ? this : new CompositeRequirementContext(this, context);

        foreach (var achievement in library.Achievements) {
            Arm(achievement);
        }
    }

    /// <summary>Where the collectibles and achievements come from.</summary>
    public CollectionLibrary Library { get; }

    /// <summary>The tags their unlocks and achievements have granted.</summary>
    public GameplayTagSet Tags { get; }

    /// <summary>How many things they have.</summary>
    public int Count => unlocks.Count;

    /// <summary>How many achievements they have earned.</summary>
    public int Earned => earned.Count;

    /// <summary>What those achievements are worth.</summary>
    public int Points { get; private set; }

    /// <summary>How many filters are still listening. Falls as an account completes things.</summary>
    public int Watching => watches.Count;

    /// <summary>Everything they have, in the order they got it.</summary>
    public IEnumerable<Unlock> Unlocks => unlocks.Values.OrderBy(unlock => unlock.Order);

    /// <inheritdoc />
    GameplayTagSet? IRequirementContext.Tags => Tags;

    /// <summary>Raised when something is unlocked for the first time.</summary>
    public event Action<Collectible, Unlock>? Collected;

    /// <summary>Raised when an achievement is earned.</summary>
    public event Action<Achievement>? Achieved;

    /// <inheritdoc />
    bool IRequirementContext.TryGetValue(AttributeId subject, out float value) {
        for (var kind = 0; kind < KindValues.Length; kind++) {
            if (KindValues[kind] == subject) {
                value = CountOf((CollectibleKind)kind);

                return true;
            }
        }

        if (subject == Total) {
            value = unlocks.Count;

            return true;
        }

        if (subject == PointsOf) {
            value = Points;

            return true;
        }

        if (subject == EarnedOf) {
            value = earned.Count;

            return true;
        }

        value = 0f;

        return false;
    }

    /// <summary>Whether they have something.</summary>
    /// <param name="collectible">Which.</param>
    /// <returns>Whether they do.</returns>
    public bool IsUnlocked(DefId collectible) => unlocks.ContainsKey(collectible.Value);

    /// <summary>Where something came from.</summary>
    /// <param name="collectible">Which.</param>
    /// <returns>The unlock, or null.</returns>
    public Unlock? SourceOf(DefId collectible) =>
        unlocks.TryGetValue(collectible.Value, out var unlock) ? unlock : null;

    /// <summary>How many of one sort they have.</summary>
    /// <param name="kind">Which sort.</param>
    /// <returns>How many.</returns>
    public int CountOf(CollectibleKind kind) {
        var counted = 0;

        foreach (var unlock in unlocks.Values) {
            if (Library.Find(unlock.Collectible)?.Kind == kind) {
                counted++;
            }
        }

        return counted;
    }

    /// <summary>Gives them something.</summary>
    /// <param name="collectible">What.</param>
    /// <param name="source">How they came by it.</param>
    /// <param name="from">What exactly — the boss, the quest, the achievement.</param>
    /// <returns>Whether it was new to them.</returns>
    public bool Unlock(Collectible collectible, UnlockSource source = UnlockSource.Unknown, DefId from = default) {
        ArgumentNullException.ThrowIfNull(collectible);

        if (!Grant(collectible, source, from)) {
            return false;
        }

        // An unlock moves a tag and a count, and any requirement anywhere could be asking about
        // either — so everything unearned is a candidate. Unlocks are rare; kills are not.
        MarkAll();
        Settle();

        return true;
    }

    /// <summary>Takes something back — a refund, a season ending, a mistake.</summary>
    /// <param name="collectible">What.</param>
    /// <returns>Whether they had it.</returns>
    /// <remarks>
    ///     ⚠ <b>This is why <see cref="Wardrobe.Resolve" /> falls back.</b> Revoking an appearance
    ///     somebody is wearing must not make them invisible, and the only way to guarantee that is for
    ///     the wardrobe to check the unlock every time it resolves rather than to be told about this.
    /// </remarks>
    public bool Revoke(Collectible collectible) {
        ArgumentNullException.ThrowIfNull(collectible);

        if (!unlocks.Remove(collectible.Id.Value)) {
            return false;
        }

        if (collectible.Tag.IsSome) {
            Tags.Remove(collectible.Tag);
        }

        return true;
    }

    /// <summary>Whether they have earned an achievement.</summary>
    /// <param name="achievement">Which.</param>
    /// <returns>Whether they have.</returns>
    public bool IsEarned(Achievement achievement) =>
        achievement is not null && earned.ContainsKey(achievement.Id.Value);

    /// <summary>How far one criterion has got.</summary>
    /// <param name="achievement">Which achievement.</param>
    /// <param name="criterion">Which of its criteria.</param>
    /// <returns>How far.</returns>
    public int ProgressOf(Achievement achievement, int criterion) {
        ArgumentNullException.ThrowIfNull(achievement);

        if (!progress.TryGetValue(achievement.Id.Value, out var counts) || (uint)criterion >= (uint)counts.Length) {
            return 0;
        }

        return counts[criterion];
    }

    /// <summary>Everything they have earned, in the order they earned it.</summary>
    /// <returns>Them.</returns>
    public IEnumerable<Achievement> Achievements() =>
        earned
            .OrderBy(pair => pair.Value)
            .Select(pair => Library.FindAchievement(new(pair.Key)))
            .Where(achievement => achievement is not null)!;

    /// <summary>Starts listening for what its criteria count.</summary>
    /// <param name="bus">Where events are posted.</param>
    /// <remarks>
    ///     One subscription with an unfiltered verb, and the per-criterion filters are tested inside.
    ///     A subscription per criterion would put thousands of dead filters on the bus's own list, and
    ///     the bus tests them all in order.
    /// </remarks>
    public void Attach(GameplayEventBus bus) {
        ArgumentNullException.ThrowIfNull(bus);

        Detach();
        subscription = bus.Subscribe(GameplayEventFilter.Everything, Observe);
    }

    /// <summary>Stops listening.</summary>
    /// <returns>Whether it was.</returns>
    public bool Detach() {
        var was = subscription?.Cancel() == true;

        subscription = null;

        return was;
    }

    /// <summary>Counts an event against whatever is waiting for it.</summary>
    /// <param name="gameplayEvent">What happened.</param>
    /// <remarks>
    ///     Public so a caller with no bus can drive this directly, and so a test can. It is what
    ///     <see cref="Attach" /> subscribes.
    /// </remarks>
    public void Observe(in GameplayEvent gameplayEvent) {
        var advanced = false;

        // Backwards, because a satisfied watch is removed in place.
        for (var index = watches.Count - 1; index >= 0; index--) {
            var watch = watches[index];

            if (!watch.Criterion.Filter.Matches(gameplayEvent)) {
                continue;
            }

            if (Advance(watch, gameplayEvent.Amount)) {
                watches.RemoveAt(index);

                // Only the achievement whose criterion just finished is a candidate. A kill must not
                // cost a walk over every achievement in the build.
                dirty.Add(watch.Achievement.Id.Value);
                advanced = true;
            }
        }

        if (advanced) {
            Settle();
        }
    }

    /// <summary>Advances a criterion by hand.</summary>
    /// <param name="achievement">Which achievement.</param>
    /// <param name="criterion">Which of its criteria.</param>
    /// <param name="amount">By how much.</param>
    /// <returns>Whether that criterion is now done.</returns>
    /// <remarks>
    ///     For the things no event describes — a one-off flag a script sets, a criterion a designer
    ///     wired to a cutscene.
    /// </remarks>
    public bool Note(Achievement achievement, int criterion, int amount = 1) {
        ArgumentNullException.ThrowIfNull(achievement);

        if ((uint)criterion >= (uint)achievement.Criteria.Length) {
            return false;
        }

        var index = watches.FindIndex(watch =>
            watch.Achievement.Id == achievement.Id && watch.Criterion.Index == criterion
        );

        if (index < 0) {
            return ProgressOf(achievement, criterion) >= achievement.Criteria[criterion].Count;
        }

        var done = Advance(watches[index], amount);

        if (done) {
            watches.RemoveAt(index);
            dirty.Add(achievement.Id.Value);
            Settle();
        }

        return done;
    }

    /// <summary>Re-checks every achievement whose criteria are done but whose requirements were not met.</summary>
    /// <returns>How many were earned.</returns>
    /// <remarks>
    ///     Called by whatever changed the player's standing — a level, a reputation, an item this
    ///     library cannot see. Everything an unlock or a criterion changes is already settled.
    /// </remarks>
    public int Refresh() {
        MarkAll();

        return Settle();
    }

    /// <summary>Puts a saved collection back, as it was, with no checks.</summary>
    /// <param name="saved">What they had.</param>
    /// <param name="achievements">What they had earned, in order.</param>
    /// <param name="counters">How far the rest had got.</param>
    /// <remarks>
    ///     ⚠ <b>Deliberately not a replay.</b> Re-running <see cref="Unlock" /> would re-derive
    ///     achievements against today's content, so a patch that raised a threshold would take back
    ///     something somebody already earned — and a patch that lowered one would hand out an
    ///     achievement with no notification anybody saw.
    /// </remarks>
    public void Restore(
        IEnumerable<Unlock> saved,
        IEnumerable<DefId>? achievements = null,
        IEnumerable<CriterionProgress>? counters = null
    ) {
        ArgumentNullException.ThrowIfNull(saved);

        unlocks.Clear();
        earned.Clear();
        progress.Clear();
        watches.Clear();
        dirty.Clear();
        Tags.Clear();
        Points = 0;
        order = 0;

        foreach (var unlock in saved.OrderBy(unlock => unlock.Order)) {
            unlocks[unlock.Collectible.Value] = unlock with { Order = ++order };

            if (Library.Find(unlock.Collectible) is { Tag.IsSome: true } collectible) {
                Tags.Add(collectible.Tag);
            }
        }

        var seen = 0;

        foreach (var id in achievements ?? []) {
            if (Library.FindAchievement(id) is not { } achievement || !earned.TryAdd(id.Value, ++seen)) {
                continue;
            }

            Points += achievement.Points;

            if (achievement.Tag.IsSome) {
                Tags.Add(achievement.Tag);
            }
        }

        foreach (var counter in counters ?? []) {
            if (Library.FindAchievement(counter.Achievement) is not { } achievement) {
                continue;
            }

            var counts = Counters(achievement);

            if ((uint)counter.Criterion < (uint)counts.Length) {
                counts[counter.Criterion] = Math.Clamp(
                    counter.Progress,
                    0,
                    achievement.Criteria[counter.Criterion].Count
                );
            }
        }

        foreach (var achievement in Library.Achievements) {
            Arm(achievement);
        }
    }

    /// <summary>How far everything unfinished has got, for a save.</summary>
    /// <returns>One entry per criterion that has been started and is not done.</returns>
    public IEnumerable<CriterionProgress> Counters() {
        foreach (var (id, counts) in progress.OrderBy(pair => pair.Key)) {
            for (var index = 0; index < counts.Length; index++) {
                if (counts[index] > 0) {
                    yield return new(new(id), index, counts[index]);
                }
            }
        }
    }

    // Puts a watch on every criterion of every achievement that is neither earned nor already done.
    void Arm(Achievement achievement) {
        if (IsEarned(achievement)) {
            return;
        }

        foreach (var criterion in achievement.Criteria) {
            if (criterion.Filter.IsSome && ProgressOf(achievement, criterion.Index) < criterion.Count) {
                watches.Add(new(achievement, criterion));
            }
        }
    }

    bool Advance(Watch watch, int amount) {
        var counts = Counters(watch.Achievement);
        var index = watch.Criterion.Index;

        // Capped, so an event worth thirty does not bank progress a later criterion would inherit.
        counts[index] = Math.Clamp(counts[index] + Math.Max(0, amount), 0, watch.Criterion.Count);

        return counts[index] >= watch.Criterion.Count;
    }

    // Everything unearned becomes a candidate. Called when a *tag* or a *count* changed, which any
    // requirement anywhere could be asking about — but that is an unlock, which is rare, rather than
    // a kill, which is not.
    void MarkAll() {
        foreach (var achievement in Library.Achievements) {
            if (!IsEarned(achievement)) {
                dirty.Add(achievement.Id.Value);
            }
        }
    }

    /// Earns whatever candidate is now finished, and keeps going while awarding makes more candidates.
    /// An achievement's own tag and its unlocks can finish another one; this is the loop that resolves
    /// the cascade, and it terminates because nothing is ever earned twice.
    int Settle() {
        var total = 0;
        var pending = new List<uint>();

        while (dirty.Count > 0) {
            pending.Clear();
            pending.AddRange(dirty);
            dirty.Clear();

            foreach (var id in pending) {
                if (Library.FindAchievement(new(id)) is not { } achievement
                    || IsEarned(achievement)
                    || !IsFinished(achievement)) {
                    continue;
                }

                Award(achievement);
                total++;
            }
        }

        return total;
    }

    bool IsFinished(Achievement achievement) {
        foreach (var criterion in achievement.Criteria) {
            if (ProgressOf(achievement, criterion.Index) < criterion.Count) {
                return false;
            }

            // A criterion whose verb did not resolve can never advance. Compile reported it; here it
            // simply keeps the achievement unearned rather than making it free.
            if (!criterion.Filter.IsSome) {
                return false;
            }
        }

        return achievement.Requirements.IsMetBy(context);
    }

    void Award(Achievement achievement) {
        earned[achievement.Id.Value] = earned.Count + 1;
        Points += achievement.Points;

        if (achievement.Tag.IsSome) {
            Tags.Add(achievement.Tag);
        }

        foreach (var id in achievement.Unlocks) {
            if (Library.Find(id) is { } collectible) {
                Grant(collectible, UnlockSource.Achievement, achievement.Id);
            }
        }

        // Nothing this achievement's own criteria were waiting for can still be pending.
        watches.RemoveAll(watch => watch.Achievement.Id == achievement.Id);

        // Its tag and its unlocks can finish others, so everything unearned goes round again.
        MarkAll();

        Achieved?.Invoke(achievement);
    }

    bool Grant(Collectible collectible, UnlockSource source, DefId from) {
        if (unlocks.ContainsKey(collectible.Id.Value)) {
            return false;
        }

        var unlock = new Unlock(collectible.Id, source, from, ++order);

        unlocks.Add(collectible.Id.Value, unlock);

        if (collectible.Tag.IsSome) {
            Tags.Add(collectible.Tag);
        }

        Collected?.Invoke(collectible, unlock);

        return true;
    }

    int[] Counters(Achievement achievement) {
        if (progress.TryGetValue(achievement.Id.Value, out var counts)) {
            return counts;
        }

        counts = new int[achievement.Criteria.Length];
        progress.Add(achievement.Id.Value, counts);

        return counts;
    }

    readonly record struct Watch(Achievement Achievement, AchievementCriterion Criterion);
}
