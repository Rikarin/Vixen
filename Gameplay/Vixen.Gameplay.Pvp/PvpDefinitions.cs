// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Pvp;

/// <summary>What kind of match it is.</summary>
public enum MatchKind {
    /// <summary>Small, symmetric, round-based.</summary>
    Arena,

    /// <summary>Larger, and won on objectives.</summary>
    Battleground,

    /// <summary>Two people who agreed to it.</summary>
    Duel
}

/// <summary>The composable objective types doc 28 asks for.</summary>
/// <remarks>
///     ⚠ <b>Four, and the list is meant to stay short.</b> Doc 28: <em>"a small set of composable node
///     types with scoring and win conditions, so a new battleground is a map plus a
///     <c>.vxdef</c>"</em>. Every battleground anybody has shipped is these four arranged differently —
///     what varies is the map, the counts and the scoring, none of which is code.
/// </remarks>
public enum PvpObjectiveKind {
    /// <summary>Stand on it until it flips, then hold it for points.</summary>
    CapturePoint,

    /// <summary>Push it along a track while the other team pushes back.</summary>
    Payload,

    /// <summary>Take theirs to yours.</summary>
    FlagReturn,

    /// <summary>Hold nodes; each one ticks resources.</summary>
    ResourceControl
}

/// <summary>Why a match operation was refused.</summary>
public enum PvpRefusal {
    /// <summary>It was not.</summary>
    None,

    /// <summary>The match is not running.</summary>
    NotRunning,

    /// <summary>There is no such team or objective.</summary>
    Unknown,

    /// <summary>They are not in this match.</summary>
    NotAPlayer
}

/// <summary>How a match ends.</summary>
public enum MatchOutcome {
    /// <summary>It has not.</summary>
    Running,

    /// <summary>Somebody reached the score.</summary>
    Score,

    /// <summary>The clock ran out and somebody was ahead.</summary>
    Time,

    /// <summary>The clock ran out and nobody was.</summary>
    Draw,

    /// <summary>Everybody on one side left.</summary>
    Forfeit
}

/// <summary>One thing a team can hold or move.</summary>
[DataContract("PvpObjective")]
public sealed class PvpObjectiveDefinition {
    /// <summary>What it is called within its map.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What kind it is.</summary>
    public PvpObjectiveKind Kind { get; set; }

    /// <summary>How long one player takes to flip it, in seconds.</summary>
    public float CaptureSeconds { get; set; } = 8f;

    /// <summary>How many points holding it scores per tick.</summary>
    public int PointsPerTick { get; set; } = 1;

    /// <summary>How long a tick is, in seconds.</summary>
    public float TickSeconds { get; set; } = 2f;

    /// <summary>How many points taking it scores once, for a flag or a payload stage.</summary>
    public int PointsOnCapture { get; set; }

    /// <summary>Which team it starts owned by, or −1 for neutral.</summary>
    public int StartingOwner { get; set; } = -1;
}

/// <summary>A battleground, an arena or a duel: a map, its teams and how it is won.</summary>
[DataContract("PvpMapDefinition")]
public sealed record PvpMapDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What kind of match it is.</summary>
    public MatchKind Kind { get; set; }

    /// <summary>The address of the map it happens on.</summary>
    public string Scene { get; set; } = string.Empty;

    /// <summary>How many teams.</summary>
    public int Teams { get; set; } = 2;

    /// <summary>How many on each.</summary>
    public int TeamSize { get; set; } = 5;

    /// <summary>What it takes to win, or zero for a match won only on the clock.</summary>
    public int ScoreToWin { get; set; } = 500;

    /// <summary>How long it may run, in seconds. Zero for no limit.</summary>
    public float TimeLimit { get; set; } = 900f;

    /// <summary>How many rounds, for an arena. One for anything else.</summary>
    public int Rounds { get; set; } = 1;

    /// <summary>Its objectives.</summary>
    public List<PvpObjectiveDefinition> Objectives { get; set; } = [];

    /// <summary>What being in one is — <c>Pvp.Battleground</c>. Empty for one nothing asks about.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }
    }
}

/// <summary>An objective with its numbers clamped.</summary>
public sealed class PvpObjective {
    internal PvpObjective(PvpObjectiveDefinition definition, int index) {
        Definition = definition;
        Index = index;
    }

    /// <summary>What it was compiled from.</summary>
    public PvpObjectiveDefinition Definition { get; }

    /// <summary>Which of its map's objectives it is.</summary>
    public int Index { get; }

    /// <summary>What it is called within its map.</summary>
    public string Id => Definition.Id;

    /// <summary>What kind it is.</summary>
    public PvpObjectiveKind Kind => Definition.Kind;

    /// <summary>How long one player takes to flip it, never below a tenth of a second.</summary>
    public float CaptureSeconds => MathF.Max(0.1f, Definition.CaptureSeconds);

    /// <summary>How many points holding it scores per tick.</summary>
    public int PointsPerTick => Math.Max(0, Definition.PointsPerTick);

    /// <summary>How long a tick is, never below a hundredth of a second.</summary>
    public float TickSeconds => MathF.Max(0.01f, Definition.TickSeconds);

    /// <summary>How many points taking it scores once.</summary>
    public int PointsOnCapture => Math.Max(0, Definition.PointsOnCapture);

    /// <summary>Which team it starts owned by, or −1.</summary>
    public int StartingOwner => Definition.StartingOwner;
}

/// <summary>A map with its objectives compiled.</summary>
public sealed class PvpMap {
    readonly PvpObjective[] objectives;

    internal PvpMap(PvpMapDefinition definition, DefId scene, GameplayTag tag, PvpObjective[] objectives) {
        Definition = definition;
        Scene = scene;
        Tag = tag;
        this.objectives = objectives;
    }

    /// <summary>What it was compiled from.</summary>
    public PvpMapDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>What kind of match it is.</summary>
    public MatchKind Kind => Definition.Kind;

    /// <summary>Which map it happens on.</summary>
    public DefId Scene { get; }

    /// <summary>What being in one is.</summary>
    public GameplayTag Tag { get; }

    /// <summary>How many teams, never below two.</summary>
    public int Teams => Math.Max(2, Definition.Teams);

    /// <summary>How many on each, never below one.</summary>
    public int TeamSize => Math.Max(1, Definition.TeamSize);

    /// <summary>What it takes to win, or zero.</summary>
    public int ScoreToWin => Math.Max(0, Definition.ScoreToWin);

    /// <summary>How long it may run, or zero.</summary>
    public float TimeLimit => MathF.Max(0f, Definition.TimeLimit);

    /// <summary>How many rounds, never below one.</summary>
    public int Rounds => Math.Max(1, Definition.Rounds);

    /// <summary>Its objectives.</summary>
    public ReadOnlySpan<PvpObjective> Objectives => objectives;
}

/// <summary>Every PvP map a build knows, compiled once.</summary>
public sealed class PvpLibrary {
    readonly Dictionary<uint, PvpMap> maps;
    readonly string[] problems;

    PvpLibrary(Dictionary<uint, PvpMap> maps, string[] problems) {
        this.maps = maps;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static PvpLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Every map, in address order.</summary>
    public IEnumerable<PvpMap> Maps => maps.Values.OrderBy(map => map.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static PvpLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var maps = new Dictionary<uint, PvpMap>();

        foreach (var definition in catalog.OfType<PvpMapDefinition>()) {
            if (definition.ScoreToWin <= 0 && definition.TimeLimit <= 0f) {
                problems.Add(
                    $"'{definition.Address}' has no score to win and no time limit, so nothing can end it."
                );
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var objectives = new PvpObjective[definition.Objectives.Count];

            for (var index = 0; index < objectives.Length; index++) {
                var objective = definition.Objectives[index];

                if (objective.Id.Length == 0) {
                    problems.Add($"'{definition.Address}' has an objective with no id.");
                } else if (!seen.Add(objective.Id)) {
                    problems.Add($"'{definition.Address}' has two objectives called '{objective.Id}'.");
                }

                if (objective.StartingOwner >= definition.Teams) {
                    problems.Add(
                        $"'{definition.Address}' objective '{objective.Id}' starts owned by team "
                        + $"{objective.StartingOwner}, and there are only {definition.Teams}."
                    );
                }

                if (objective.PointsPerTick <= 0 && objective.PointsOnCapture <= 0) {
                    problems.Add(
                        $"'{definition.Address}' objective '{objective.Id}' scores nothing on capture and "
                        + "nothing per tick, so holding it does nothing."
                    );
                }

                objectives[index] = new(objective, index);
            }

            if (definition.ScoreToWin > 0 && objectives.Length == 0) {
                problems.Add(
                    $"'{definition.Address}' is won on score and has no objectives, so nobody can score."
                );
            }

            maps.Add(
                definition.Id.Value,
                new(definition, DefId.From(definition.Scene), tags.Resolve(definition.Tag), objectives)
            );
        }

        return new(maps, [.. problems]);
    }

    /// <summary>Finds a map.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public PvpMap? Find(DefId id) => maps.GetValueOrDefault(id.Value);
}
