// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Instances;

/// <summary>Whose lockout it is.</summary>
public enum LockoutScope {
    /// <summary>One character's. Another character on the account may go again.</summary>
    Character,

    /// <summary>The whole account's.</summary>
    Account
}

/// <summary>When a lockout lifts.</summary>
/// <remarks>
///     ⚠ <b>A reset is an absolute boundary, not a timer from when somebody entered.</b> "Weekly"
///     means the same instant for everybody on the realm — otherwise every player's reset drifts to
///     wherever their first run happened to fall, and a guild that raids on Wednesdays finds half its
///     roster locked. <see cref="LockoutPolicy.NextResetAfter" /> is where that is computed and it is
///     the only place a reset time comes from.
/// </remarks>
public enum LockoutReset {
    /// <summary>Never. What a one-off story instance has.</summary>
    None,

    /// <summary>At the daily boundary.</summary>
    Daily,

    /// <summary>At the weekly boundary.</summary>
    Weekly
}

/// <summary>Where an encounter is.</summary>
public enum EncounterStatus {
    /// <summary>Not begun.</summary>
    Waiting,

    /// <summary>Being fought.</summary>
    Engaged,

    /// <summary>Beaten.</summary>
    Defeated,

    /// <summary>Everybody died and it has reset.</summary>
    Wiped
}

/// <summary>Why an instance operation was refused.</summary>
public enum InstanceRefusal {
    /// <summary>It was not.</summary>
    None,

    /// <summary>This build has no such instance or difficulty.</summary>
    Unknown,

    /// <summary>Somebody is locked to it already.</summary>
    LockedOut,

    /// <summary>There are too few or too many of them.</summary>
    BadSize,

    /// <summary>A requirement is not met.</summary>
    Requirements,

    /// <summary>The run is over.</summary>
    Closed,

    /// <summary>That encounter cannot be started from here.</summary>
    OutOfOrder
}

/// <summary>How long a completion keeps somebody out, and whose it is.</summary>
[DataContract("LockoutPolicy")]
public sealed class LockoutPolicyDefinition {
    /// <summary>Whose lockout it is.</summary>
    public LockoutScope Scope { get; set; }

    /// <summary>When it lifts.</summary>
    public LockoutReset Reset { get; set; }

    /// <summary>How many runs are allowed before it bites. One is the usual.</summary>
    public int Completions { get; set; } = 1;
}

/// <summary>One difficulty of an instance.</summary>
[DataContract("InstanceDifficulty")]
public sealed class DifficultyDefinition {
    /// <summary>What it is called within its instance — <c>heroic</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What being in one is — <c>Instance.Heroic</c>. Empty for one nothing asks about.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>What the enemies' health is multiplied by.</summary>
    public float HealthScale { get; set; } = 1f;

    /// <summary>What their damage is multiplied by.</summary>
    public float DamageScale { get; set; } = 1f;

    /// <summary>How it locks people out.</summary>
    public LockoutPolicyDefinition Lockout { get; set; } = new();

    /// <summary>What has to be true to run it.</summary>
    public List<RequirementDefinition> Requires { get; set; } = [];
}

/// <summary>One fight inside an instance.</summary>
[DataContract("Encounter")]
public sealed class EncounterDefinition {
    /// <summary>What it is called within its instance.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The address of the behaviour tree that scripts it, or empty.</summary>
    /// <remarks>
    ///     Doc 28 puts encounter scripting on <c>Core/Vixen.Ai</c>'s behaviour trees, so what is here
    ///     is the address and nothing else. An instance library that contained a scripting language
    ///     would be a second one.
    /// </remarks>
    public string Script { get; set; } = string.Empty;

    /// <summary>Whether beating it is a checkpoint a wipe returns to.</summary>
    public bool IsCheckpoint { get; set; } = true;

    /// <summary>Whether it has to be beaten before anything after it may be started.</summary>
    public bool IsGate { get; set; }
}

/// <summary>An instance: a map, its difficulties, and the fights in it.</summary>
[DataContract("InstanceDefinition")]
public sealed record InstanceDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The address of the map it happens on.</summary>
    public string Scene { get; set; } = string.Empty;

    /// <summary>The fewest who may go.</summary>
    public int MinimumPlayers { get; set; } = 1;

    /// <summary>The most.</summary>
    public int MaximumPlayers { get; set; } = 5;

    /// <summary>Its difficulties. The first is the default.</summary>
    public List<DifficultyDefinition> Difficulties { get; set; } = [];

    /// <summary>Its fights, in the order they are meant to be done.</summary>
    public List<EncounterDefinition> Encounters { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var difficulty in Difficulties) {
            if (difficulty.Tag.Length > 0) {
                tags.Add(difficulty.Tag);
            }

            foreach (var requirement in difficulty.Requires) {
                if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                    tags.Add(requirement.Subject);
                }
            }
        }
    }
}

/// <summary>A lockout policy, with the reset arithmetic that makes it fleet-wide.</summary>
public sealed class LockoutPolicy {
    internal LockoutPolicy(LockoutPolicyDefinition definition) => Definition = definition;

    /// <summary>The one a difficulty gets when nothing was authored: nobody is locked out.</summary>
    public static LockoutPolicy Open { get; } = new(new() { Reset = LockoutReset.None, Completions = int.MaxValue });

    /// <summary>What it was compiled from.</summary>
    public LockoutPolicyDefinition Definition { get; }

    /// <summary>Whose lockout it is.</summary>
    public LockoutScope Scope => Definition.Scope;

    /// <summary>When it lifts.</summary>
    public LockoutReset Reset => Definition.Reset;

    /// <summary>How many runs are allowed, never below one.</summary>
    public int Completions => Math.Max(1, Definition.Completions);

    /// <summary>Whether it ever locks anybody out.</summary>
    public bool IsSome => Reset != LockoutReset.None;

    /// <summary>The next reset boundary at or after a moment.</summary>
    /// <param name="seconds">The moment, in seconds since whatever epoch the fleet counts from.</param>
    /// <returns>The boundary, or infinity for a policy that never resets.</returns>
    /// <remarks>
    ///     ⚠ <b>Absolute, so everybody's reset is the same instant.</b> A duration added to whenever
    ///     somebody happened to enter gives every player their own schedule, and a guild cannot plan a
    ///     raid night around a boundary that is different for each of its members.
    /// </remarks>
    public double NextResetAfter(double seconds) {
        const double Day = 86400d;
        const double Week = Day * 7d;

        return Reset switch {
            LockoutReset.Daily => Math.Floor(seconds / Day) * Day + Day,
            LockoutReset.Weekly => Math.Floor(seconds / Week) * Week + Week,
            _ => double.PositiveInfinity
        };
    }
}

/// <summary>A difficulty with its names resolved.</summary>
public sealed class Difficulty {
    internal Difficulty(DifficultyDefinition definition, int index, GameplayTag tag, RequirementSet requirements) {
        Definition = definition;
        Index = index;
        Tag = tag;
        Requirements = requirements;
        Lockout = new(definition.Lockout ?? new LockoutPolicyDefinition());
    }

    /// <summary>What it was compiled from.</summary>
    public DifficultyDefinition Definition { get; }

    /// <summary>Which of its instance's difficulties it is.</summary>
    public int Index { get; }

    /// <summary>What it is called within its instance.</summary>
    public string Id => Definition.Id;

    /// <summary>What being in one is.</summary>
    public GameplayTag Tag { get; }

    /// <summary>What the enemies' health is multiplied by, never below zero.</summary>
    public float HealthScale => MathF.Max(0f, Definition.HealthScale);

    /// <summary>What their damage is multiplied by, never below zero.</summary>
    public float DamageScale => MathF.Max(0f, Definition.DamageScale);

    /// <summary>How it locks people out.</summary>
    public LockoutPolicy Lockout { get; }

    /// <summary>What has to be true to run it.</summary>
    public RequirementSet Requirements { get; }
}

/// <summary>An instance with its names resolved.</summary>
public sealed class Instance {
    readonly Difficulty[] difficulties;
    readonly EncounterDefinition[] encounters;

    internal Instance(InstanceDefinition definition, DefId scene, Difficulty[] difficulties, EncounterDefinition[] encounters) {
        Definition = definition;
        Scene = scene;
        this.difficulties = difficulties;
        this.encounters = encounters;
    }

    /// <summary>What it was compiled from.</summary>
    public InstanceDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>Which map it happens on.</summary>
    public DefId Scene { get; }

    /// <summary>Its difficulties.</summary>
    public ReadOnlySpan<Difficulty> Difficulties => difficulties;

    /// <summary>Its fights, in order.</summary>
    public ReadOnlySpan<EncounterDefinition> Encounters => encounters;

    /// <summary>The fewest who may go, never below one.</summary>
    public int MinimumPlayers => Math.Max(1, Definition.MinimumPlayers);

    /// <summary>The most.</summary>
    public int MaximumPlayers => Math.Max(MinimumPlayers, Definition.MaximumPlayers);

    /// <summary>Finds a difficulty.</summary>
    /// <param name="id">Its id within the instance.</param>
    /// <returns>It, or null.</returns>
    public Difficulty? FindDifficulty(string? id) {
        foreach (var difficulty in difficulties) {
            if (string.Equals(difficulty.Id, id, StringComparison.Ordinal)) {
                return difficulty;
            }
        }

        return null;
    }

    /// <summary>Which fight an id names.</summary>
    /// <param name="id">Its id within the instance.</param>
    /// <returns>Its index, or −1.</returns>
    public int IndexOfEncounter(string? id) {
        for (var index = 0; index < encounters.Length; index++) {
            if (string.Equals(encounters[index].Id, id, StringComparison.Ordinal)) {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>One lockout: who, to what, until when.</summary>
/// <param name="Subject">Whose. A character or an account, per the policy's scope.</param>
/// <param name="Instance">Which instance.</param>
/// <param name="Difficulty">Which difficulty of it.</param>
/// <param name="Expires">When it lifts, in seconds since the fleet's epoch.</param>
/// <param name="Completions">How many runs it has counted.</param>
public readonly record struct Lockout(
    PlayerId Subject,
    DefId Instance,
    string Difficulty,
    double Expires,
    int Completions
);

/// <summary>Where lockouts are kept.</summary>
/// <remarks>
///     ⚠ <b>An interface for the reason <c>IPityStore</c> is one, and doc 28 is specific about this
///     case:</b> lockouts live in <c>Live.Instances.Cluster</c> <em>"because they are fleet-wide"</em>.
///     A lockout one shard knew about is a lockout a player evades by zoning.
/// </remarks>
public interface ILockoutStore {
    /// <summary>What somebody is locked to.</summary>
    /// <param name="subject">Whose.</param>
    /// <param name="instance">Which instance.</param>
    /// <param name="difficulty">Which difficulty.</param>
    /// <returns>The lockout, or null.</returns>
    Lockout? Find(PlayerId subject, DefId instance, string difficulty);

    /// <summary>Records or extends one.</summary>
    /// <param name="lockout">The lockout.</param>
    void Save(Lockout lockout);

    /// <summary>Forgets whatever has lifted.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>How many were dropped.</returns>
    int Purge(double now);
}

/// <summary>An <see cref="ILockoutStore" /> in memory. For tests and single-process games.</summary>
public sealed class MemoryLockoutStore : ILockoutStore {
    readonly Dictionary<(PlayerId, uint, string), Lockout> lockouts = [];

    /// <summary>How many it holds.</summary>
    public int Count => lockouts.Count;

    /// <inheritdoc />
    public Lockout? Find(PlayerId subject, DefId instance, string difficulty) =>
        lockouts.TryGetValue((subject, instance.Value, difficulty ?? string.Empty), out var lockout) ? lockout : null;

    /// <inheritdoc />
    public void Save(Lockout lockout) =>
        lockouts[(lockout.Subject, lockout.Instance.Value, lockout.Difficulty ?? string.Empty)] = lockout;

    /// <inheritdoc />
    public int Purge(double now) {
        var stale = lockouts.Where(entry => now >= entry.Value.Expires).Select(entry => entry.Key).ToArray();

        foreach (var key in stale) {
            lockouts.Remove(key);
        }

        return stale.Length;
    }
}
