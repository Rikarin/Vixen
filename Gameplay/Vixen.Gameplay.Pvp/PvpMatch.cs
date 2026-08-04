// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Pvp;

/// <summary>One objective's live state.</summary>
/// <remarks>
///     ⚠ <b>Progress is a signed number towards one team, and taking a point back has to pass through
///     neutral.</b> Two separate per-team meters would let a point flip the instant the last defender
///     dies, because the attackers' meter had been filling the whole time they were being held off.
///     One meter that has to be pushed down to zero and then up again is what makes a contested point
///     take time to lose as well as to gain.
/// </remarks>
public sealed class ObjectiveState {
    internal ObjectiveState(PvpObjective objective) {
        Objective = objective;
        Owner = objective.StartingOwner;
        Progress = objective.StartingOwner >= 0 ? 1f : 0f;
        Capturing = objective.StartingOwner;
    }

    /// <summary>What it is.</summary>
    public PvpObjective Objective { get; }

    /// <summary>Which team holds it, or −1.</summary>
    public int Owner { get; internal set; }

    /// <summary>Which team the meter is filling for, or −1 when it is empty.</summary>
    public int Capturing { get; internal set; }

    /// <summary>How full the meter is, from zero to one.</summary>
    public float Progress { get; internal set; }

    /// <summary>Whether more than one team is on it.</summary>
    public bool IsContested { get; internal set; }

    /// <summary>How long since it last scored.</summary>
    public float SinceTick { get; internal set; }

    /// <summary>How many of each team are on it. Set by the caller each tick.</summary>
    public int[] Present { get; internal set; } = [];
}

/// <summary>One PvP match: teams, objectives, score and the clock.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A contested objective does not move in either direction.</b> Not "the bigger group
///         wins slowly" — frozen. The alternative makes numbers the whole game, and it makes standing
///         on a point you already hold worth doing, which is how a battleground turns into everybody
///         sitting still.
///     </para>
///     <para>
///         ⚠ <b>Score ticks for holding, and a capture pays once.</b> Both, because they are different
///         mechanics: resource control is won by holding more for longer, and a flag is won by taking
///         it. A map uses whichever of the two numbers it sets.
///     </para>
///     <para>
///         ⚠ <b>The clock is checked after the score.</b> A team that reaches the winning score on the
///         same tick the clock runs out has won on score, not drawn — the same rule the dynamic-event
///         director has, and the same reason: the work was done.
///     </para>
/// </remarks>
public sealed class PvpMatch {
    readonly ObjectiveState[] objectives;
    readonly int[] scores;
    readonly int[] rounds;
    readonly Dictionary<PlayerId, int> teams = [];

    /// <summary>Starts a match.</summary>
    /// <param name="map">Which map.</param>
    public PvpMatch(PvpMap map) {
        ArgumentNullException.ThrowIfNull(map);

        Map = map;
        scores = new int[map.Teams];
        rounds = new int[map.Teams];
        objectives = new ObjectiveState[map.Objectives.Length];

        for (var index = 0; index < objectives.Length; index++) {
            objectives[index] = new(map.Objectives[index]) { Present = new int[map.Teams] };
        }
    }

    /// <summary>Which map.</summary>
    public PvpMap Map { get; }

    /// <summary>How long it has been running.</summary>
    public float Elapsed { get; private set; }

    /// <summary>Which round it is on, counting from one.</summary>
    public int Round { get; private set; } = 1;

    /// <summary>How it ended, or <see cref="MatchOutcome.Running" />.</summary>
    public MatchOutcome Outcome { get; private set; } = MatchOutcome.Running;

    /// <summary>Which team won, or −1.</summary>
    public int Winner { get; private set; } = -1;

    /// <summary>Whether it is over.</summary>
    public bool IsOver => Outcome != MatchOutcome.Running;

    /// <summary>Its objectives' live state.</summary>
    public ReadOnlySpan<ObjectiveState> Objectives => objectives;

    /// <summary>Everybody in it, and which team they are on.</summary>
    public IReadOnlyDictionary<PlayerId, int> Teams => teams;

    /// <summary>Raised when an objective changes hands.</summary>
    public event Action<ObjectiveState, int>? Captured;

    /// <summary>Raised when the match ends.</summary>
    public event Action<PvpMatch>? Ended;

    /// <summary>What a team has scored this round.</summary>
    /// <param name="team">Which team.</param>
    /// <returns>Their score.</returns>
    public int ScoreOf(int team) => (uint)team < (uint)scores.Length ? scores[team] : 0;

    /// <summary>How many rounds a team has taken.</summary>
    /// <param name="team">Which team.</param>
    /// <returns>How many.</returns>
    public int RoundsWonBy(int team) => (uint)team < (uint)rounds.Length ? rounds[team] : 0;

    /// <summary>Which team somebody is on, or −1.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Their team.</returns>
    public int TeamOf(PlayerId player) => teams.GetValueOrDefault(player, -1);

    /// <summary>Puts somebody on a team.</summary>
    /// <param name="player">Who.</param>
    /// <param name="team">Which team.</param>
    /// <returns>The refusal, or <see cref="PvpRefusal.None" />.</returns>
    public PvpRefusal Join(PlayerId player, int team) {
        if ((uint)team >= (uint)scores.Length || !player.IsSome) {
            return PvpRefusal.Unknown;
        }

        teams[player] = team;

        return PvpRefusal.None;
    }

    /// <summary>Takes somebody out, and forfeits the match when a team is emptied.</summary>
    /// <param name="player">Who.</param>
    /// <returns>The refusal, or <see cref="PvpRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Only once somebody was on that team.</b> A match that forfeited because a side started
    ///     empty would end the moment it was created, before anybody had joined.
    /// </remarks>
    public PvpRefusal Leave(PlayerId player) {
        if (!teams.Remove(player, out var team)) {
            return PvpRefusal.NotAPlayer;
        }

        if (IsOver || teams.ContainsValue(team)) {
            return PvpRefusal.None;
        }

        for (var other = 0; other < scores.Length; other++) {
            if (other != team && teams.ContainsValue(other)) {
                Finish(MatchOutcome.Forfeit, other);

                break;
            }
        }

        return PvpRefusal.None;
    }

    /// <summary>Says how many of each team are standing on an objective.</summary>
    /// <param name="objective">Which one.</param>
    /// <param name="present">How many of each team, by team index.</param>
    /// <returns>The refusal, or <see cref="PvpRefusal.None" />.</returns>
    public PvpRefusal Occupy(int objective, ReadOnlySpan<int> present) {
        if ((uint)objective >= (uint)objectives.Length) {
            return PvpRefusal.Unknown;
        }

        var state = objectives[objective];
        var contenders = 0;

        for (var team = 0; team < state.Present.Length; team++) {
            state.Present[team] = team < present.Length ? Math.Max(0, present[team]) : 0;

            if (state.Present[team] > 0) {
                contenders++;
            }
        }

        state.IsContested = contenders > 1;

        return PvpRefusal.None;
    }

    /// <summary>Advances the match.</summary>
    /// <param name="delta">How much time passed, in seconds.</param>
    /// <returns>Whether it ended on this tick.</returns>
    public bool Tick(float delta) {
        if (IsOver || delta <= 0f) {
            return false;
        }

        Elapsed += delta;

        foreach (var state in objectives) {
            Advance(state, delta);
            Score(state, delta);
        }

        if (Map.ScoreToWin > 0) {
            for (var team = 0; team < scores.Length; team++) {
                if (scores[team] >= Map.ScoreToWin) {
                    return TakeRound(team);
                }
            }
        }

        // ⚠ After the score, so reaching the winning number on the tick the clock expires is a win.
        if (Map.TimeLimit > 0f && Elapsed >= Map.TimeLimit) {
            return TakeRound(Leader());
        }

        return false;
    }

    /// <summary>Which team is ahead, or −1 when two are level.</summary>
    /// <returns>The team.</returns>
    public int Leader() {
        var best = -1;
        var tied = false;

        for (var team = 0; team < scores.Length; team++) {
            if (best < 0 || scores[team] > scores[best]) {
                best = team;
                tied = false;
            } else if (scores[team] == scores[best]) {
                tied = true;
            }
        }

        return tied ? -1 : best;
    }

    void Advance(ObjectiveState state, float delta) {
        // Frozen while contested — not "the bigger group wins slowly".
        if (state.IsContested) {
            return;
        }

        var pushing = -1;

        for (var team = 0; team < state.Present.Length; team++) {
            if (state.Present[team] > 0) {
                pushing = team;

                break;
            }
        }

        if (pushing < 0 || pushing == state.Owner) {
            return;
        }

        var rate = delta / state.Objective.CaptureSeconds;

        if (state.Owner >= 0 || (state.Capturing >= 0 && state.Capturing != pushing)) {
            // Somebody else's meter has to come down to nothing before this team's can go up.
            state.Progress -= rate;

            if (state.Progress > 0f) {
                return;
            }

            state.Progress = 0f;

            if (state.Owner >= 0) {
                var lost = state.Owner;

                state.Owner = -1;
                state.Capturing = -1;
                Captured?.Invoke(state, lost);

                return;
            }
        }

        state.Capturing = pushing;
        state.Progress = MathF.Min(1f, state.Progress + rate);

        if (state.Progress < 1f) {
            return;
        }

        state.Owner = pushing;
        state.SinceTick = 0f;

        if (state.Objective.PointsOnCapture > 0) {
            scores[pushing] += state.Objective.PointsOnCapture;
        }

        Captured?.Invoke(state, pushing);
    }

    void Score(ObjectiveState state, float delta) {
        if (state.Owner < 0 || state.Objective.PointsPerTick <= 0) {
            return;
        }

        state.SinceTick += delta;

        while (state.SinceTick >= state.Objective.TickSeconds) {
            state.SinceTick -= state.Objective.TickSeconds;
            scores[state.Owner] += state.Objective.PointsPerTick;
        }
    }

    bool TakeRound(int team) {
        if (team >= 0) {
            rounds[team]++;
        }

        var needed = (Map.Rounds / 2) + 1;

        if (team >= 0 && rounds[team] >= needed) {
            return Finish(Map.ScoreToWin > 0 && scores[team] >= Map.ScoreToWin ? MatchOutcome.Score : MatchOutcome.Time, team);
        }

        if (Round >= Map.Rounds) {
            // Out of rounds with nobody having taken a majority: a draw is a real outcome, and
            // inventing a tiebreak here would be inventing one every game would then have to use.
            var best = -1;
            var tied = false;

            for (var other = 0; other < rounds.Length; other++) {
                if (best < 0 || rounds[other] > rounds[best]) {
                    best = other;
                    tied = false;
                } else if (rounds[other] == rounds[best]) {
                    tied = true;
                }
            }

            return Finish(tied || best < 0 ? MatchOutcome.Draw : MatchOutcome.Time, tied ? -1 : best);
        }

        Round++;
        Elapsed = 0f;
        Array.Clear(scores);

        for (var index = 0; index < objectives.Length; index++) {
            objectives[index] = new(Map.Objectives[index]) { Present = new int[Map.Teams] };
        }

        return false;
    }

    bool Finish(MatchOutcome outcome, int winner) {
        Outcome = outcome;
        Winner = winner;
        Ended?.Invoke(this);

        return true;
    }
}
