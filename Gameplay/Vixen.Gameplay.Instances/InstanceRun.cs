// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Instances;

/// <summary>Every instance a build knows, compiled once.</summary>
public sealed class InstanceLibrary {
    readonly Dictionary<uint, Instance> instances;
    readonly string[] problems;

    InstanceLibrary(Dictionary<uint, Instance> instances, string[] problems) {
        this.instances = instances;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static InstanceLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Every instance, in address order.</summary>
    public IEnumerable<Instance> Instances =>
        instances.Values.OrderBy(instance => instance.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static InstanceLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var instances = new Dictionary<uint, Instance>();

        foreach (var definition in catalog.OfType<InstanceDefinition>()) {
            if (definition.Difficulties.Count == 0) {
                problems.Add($"'{definition.Address}' has no difficulties, so nobody can enter it.");
            }

            if (definition.MinimumPlayers > definition.MaximumPlayers) {
                problems.Add(
                    $"'{definition.Address}' wants at least {definition.MinimumPlayers} and at most "
                    + $"{definition.MaximumPlayers}, which no group satisfies."
                );
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var difficulties = new Difficulty[definition.Difficulties.Count];

            for (var index = 0; index < difficulties.Length; index++) {
                var difficulty = definition.Difficulties[index];

                if (difficulty.Id.Length == 0) {
                    problems.Add($"'{definition.Address}' has a difficulty with no id, so nothing can name it.");
                } else if (!seen.Add(difficulty.Id)) {
                    problems.Add($"'{definition.Address}' has two difficulties called '{difficulty.Id}'.");
                }

                difficulties[index] = new(
                    difficulty,
                    index,
                    tags.Resolve(difficulty.Tag),
                    RequirementSet.Compile(difficulty.Requires, tags)
                );
            }

            var encounters = new HashSet<string>(StringComparer.Ordinal);

            foreach (var encounter in definition.Encounters) {
                if (encounter.Id.Length == 0) {
                    problems.Add($"'{definition.Address}' has an encounter with no id.");
                } else if (!encounters.Add(encounter.Id)) {
                    problems.Add($"'{definition.Address}' has two encounters called '{encounter.Id}'.");
                }
            }

            if (definition.Encounters.Count > 0 && !definition.Encounters.Exists(entry => entry.IsCheckpoint)) {
                problems.Add(
                    $"'{definition.Address}' has no checkpoint, so a wipe on the last fight sends the group "
                    + "back to the door."
                );
            }

            instances.Add(
                definition.Id.Value,
                new(definition, DefId.From(definition.Scene), difficulties, [.. definition.Encounters])
            );
        }

        return new(instances, [.. problems]);
    }

    /// <summary>Finds an instance.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public Instance? Find(DefId id) => instances.GetValueOrDefault(id.Value);
}

/// <summary>One group's run through one instance.</summary>
/// <remarks>
///     <para>
///         <b>Doc 27's <c>Instance</c> shard kind with gameplay on top.</b> What is here is the run:
///         which difficulty, who is in it, how far they got, and what a wipe costs them.
///     </para>
///     <para>
///         ⚠ <b>The difficulty is chosen once and cannot change.</b> A lockout is per <c>(instance,
///         difficulty)</c>, so a group that could switch halfway would have one lockout covering two —
///         which is the shape of every "clear it on normal then swap to heroic" exploit.
///     </para>
///     <para>
///         ⚠ <b>A lockout is issued on the first <em>defeat</em>, not on entry.</b> Somebody who walked
///         in, saw the group fall apart and left has not used their week; somebody who killed the first
///         boss has. It is issued once and extended never — a second kill on the same run must not push
///         the reset out.
///     </para>
///     <para>
///         ⚠ <b>A wipe resets what was being fought and nothing else.</b> A boss that is dead stays
///         dead — that is what makes a raid night's progress progress — and <see cref="Checkpoint" />
///         says where to resume from. What a wipe costs is the attempt.
///     </para>
/// </remarks>
public sealed class InstanceRun {
    readonly EncounterStatus[] statuses;
    readonly int[] attempts;
    readonly List<PlayerId> participants = [];

    InstanceRun(Instance instance, Difficulty difficulty, IEnumerable<PlayerId> party) {
        Instance = instance;
        Difficulty = difficulty;
        statuses = new EncounterStatus[instance.Encounters.Length];
        attempts = new int[instance.Encounters.Length];
        participants.AddRange(party);
    }

    /// <summary>Which instance.</summary>
    public Instance Instance { get; }

    /// <summary>Which difficulty. Fixed for the life of the run.</summary>
    public Difficulty Difficulty { get; }

    /// <summary>Who is in it.</summary>
    public IReadOnlyList<PlayerId> Participants => participants;

    /// <summary>Whether every fight is beaten.</summary>
    public bool IsCleared {
        get {
            foreach (var status in statuses) {
                if (status != EncounterStatus.Defeated) {
                    return false;
                }
            }

            return statuses.Length > 0;
        }
    }

    /// <summary>Whether the run has been closed.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>How many fights have been beaten.</summary>
    public int Defeated => statuses.Count(status => status == EncounterStatus.Defeated);

    /// <summary>The furthest checkpoint reached, or −1 for none.</summary>
    public int Checkpoint { get; private set; } = -1;

    /// <summary>Whether a lockout has been issued for this run.</summary>
    public bool IsLocked { get; private set; }

    /// <summary>Raised whenever a fight changes state.</summary>
    public event Action<int, EncounterStatus>? Changed;

    /// <summary>Where a fight is.</summary>
    /// <param name="encounter">Which one.</param>
    /// <returns>Its status.</returns>
    public EncounterStatus StatusOf(int encounter) =>
        (uint)encounter < (uint)statuses.Length ? statuses[encounter] : EncounterStatus.Waiting;

    /// <summary>How many times a fight has been tried.</summary>
    /// <param name="encounter">Which one.</param>
    /// <returns>How many.</returns>
    public int AttemptsOn(int encounter) => (uint)encounter < (uint)attempts.Length ? attempts[encounter] : 0;

    /// <summary>Whether a group may go, and why not.</summary>
    /// <param name="instance">Which instance.</param>
    /// <param name="difficultyId">Which difficulty.</param>
    /// <param name="party">Who is going.</param>
    /// <param name="lockouts">Where lockouts are kept.</param>
    /// <param name="now">The clock, in seconds since the fleet's epoch.</param>
    /// <param name="context">What their requirements are evaluated against, or null to skip them.</param>
    /// <returns>The refusal, or <see cref="InstanceRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>One locked-out member refuses the whole group.</b> Letting the rest in leaves somebody
    ///     standing at the door while their party clears without them, which is worse than being told
    ///     no — and a group that could enter short is a group that summons the locked member inside.
    /// </remarks>
    public static InstanceRefusal CanEnter(
        Instance instance,
        string difficultyId,
        IReadOnlyList<PlayerId> party,
        ILockoutStore lockouts,
        double now,
        IRequirementContext? context = null
    ) {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(lockouts);

        if (instance.FindDifficulty(difficultyId) is not { } difficulty) {
            return InstanceRefusal.Unknown;
        }

        if (party.Count < instance.MinimumPlayers || party.Count > instance.MaximumPlayers) {
            return InstanceRefusal.BadSize;
        }

        if (context is not null && !difficulty.Requirements.IsMetBy(context)) {
            return InstanceRefusal.Requirements;
        }

        if (!difficulty.Lockout.IsSome) {
            return InstanceRefusal.None;
        }

        foreach (var player in party) {
            if (lockouts.Find(player, instance.Id, difficulty.Id) is { } lockout
                && now < lockout.Expires
                && lockout.Completions >= difficulty.Lockout.Completions) {
                return InstanceRefusal.LockedOut;
            }
        }

        return InstanceRefusal.None;
    }

    /// <summary>Starts a run.</summary>
    /// <param name="instance">Which instance.</param>
    /// <param name="difficultyId">Which difficulty.</param>
    /// <param name="party">Who is going.</param>
    /// <param name="lockouts">Where lockouts are kept.</param>
    /// <param name="now">The clock.</param>
    /// <param name="run">The run, when it started.</param>
    /// <param name="context">What their requirements are evaluated against.</param>
    /// <returns>The refusal, or <see cref="InstanceRefusal.None" />.</returns>
    public static InstanceRefusal Enter(
        Instance instance,
        string difficultyId,
        IReadOnlyList<PlayerId> party,
        ILockoutStore lockouts,
        double now,
        out InstanceRun? run,
        IRequirementContext? context = null
    ) {
        run = null;

        var refusal = CanEnter(instance, difficultyId, party, lockouts, now, context);

        if (refusal != InstanceRefusal.None) {
            return refusal;
        }

        run = new(instance, instance.FindDifficulty(difficultyId)!, party);

        return InstanceRefusal.None;
    }

    /// <summary>Begins a fight.</summary>
    /// <param name="encounter">Which one.</param>
    /// <returns>The refusal, or <see cref="InstanceRefusal.None" />.</returns>
    public InstanceRefusal Engage(int encounter) {
        if (IsClosed) {
            return InstanceRefusal.Closed;
        }

        if ((uint)encounter >= (uint)statuses.Length) {
            return InstanceRefusal.Unknown;
        }

        if (statuses[encounter] == EncounterStatus.Defeated) {
            return InstanceRefusal.OutOfOrder;
        }

        // A gate before it that is still standing means this one cannot be reached, whatever the
        // group thinks it is doing.
        for (var before = 0; before < encounter; before++) {
            if (Instance.Encounters[before].IsGate && statuses[before] != EncounterStatus.Defeated) {
                return InstanceRefusal.OutOfOrder;
            }
        }

        statuses[encounter] = EncounterStatus.Engaged;
        attempts[encounter]++;
        Changed?.Invoke(encounter, EncounterStatus.Engaged);

        return InstanceRefusal.None;
    }

    /// <summary>Wins a fight, which may lock the group in.</summary>
    /// <param name="encounter">Which one.</param>
    /// <param name="lockouts">Where lockouts are kept.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The refusal, or <see cref="InstanceRefusal.None" />.</returns>
    public InstanceRefusal Defeat(int encounter, ILockoutStore lockouts, double now) {
        ArgumentNullException.ThrowIfNull(lockouts);

        if (IsClosed) {
            return InstanceRefusal.Closed;
        }

        if ((uint)encounter >= (uint)statuses.Length || statuses[encounter] == EncounterStatus.Defeated) {
            return InstanceRefusal.Unknown;
        }

        statuses[encounter] = EncounterStatus.Defeated;

        if (Instance.Encounters[encounter].IsCheckpoint) {
            Checkpoint = Math.Max(Checkpoint, encounter);
        }

        Changed?.Invoke(encounter, EncounterStatus.Defeated);

        // Issued once, on the first kill, and never extended: a second boss on the same run must not
        // push the reset out to a week from now.
        if (!IsLocked && Difficulty.Lockout.IsSome) {
            IsLocked = true;

            var expires = Difficulty.Lockout.NextResetAfter(now);

            foreach (var player in participants) {
                var existing = lockouts.Find(player, Instance.Id, Difficulty.Id);
                var completions = existing is { } previous && now < previous.Expires ? previous.Completions + 1 : 1;

                lockouts.Save(new(player, Instance.Id, Difficulty.Id, expires, completions));
            }
        }

        return InstanceRefusal.None;
    }

    /// <summary>Everybody died. Whatever was being fought resets; whatever was beaten stays beaten.</summary>
    /// <returns>How many fights were reset.</returns>
    /// <remarks>
    ///     ⚠ <b>Only what was <em>engaged</em>.</b> A boss that is dead stays dead — that is what makes
    ///     a raid night's progress progress — and a fight that had not been started was not lost. What
    ///     a wipe costs is the attempt, which <see cref="AttemptsOn" /> counts and
    ///     <see cref="Checkpoint" /> says where to resume from.
    /// </remarks>
    public int Wipe() {
        if (IsClosed) {
            return 0;
        }

        var reset = 0;

        for (var encounter = 0; encounter < statuses.Length; encounter++) {
            if (statuses[encounter] != EncounterStatus.Engaged) {
                continue;
            }

            statuses[encounter] = EncounterStatus.Wiped;
            Changed?.Invoke(encounter, EncounterStatus.Wiped);
            reset++;
        }

        return reset;
    }

    /// <summary>Closes the run. What the shard being retired does.</summary>
    public void Close() => IsClosed = true;
}
