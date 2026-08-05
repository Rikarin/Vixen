// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Ai;

/// <summary>What a mob does when it is pulled too far.</summary>
public enum LeashBehaviour {
    /// <summary>Walk back and forget everything.</summary>
    Reset,

    /// <summary>Walk back, forget everything, and refuse to be hit on the way.</summary>
    Evade,

    /// <summary>Be where it started, at once.</summary>
    Teleport
}

/// <summary>Where a leash currently is.</summary>
public enum LeashState {
    /// <summary>Within the tether. Nothing is happening.</summary>
    Held,

    /// <summary>Past the tether and not yet past the break. Being watched.</summary>
    Stretched,

    /// <summary>Past the break. Going home.</summary>
    Broken
}

/// <summary>How far a mob may be pulled, and what happens when it is.</summary>
/// <remarks>
///     ⚠ <b>Two radii and not one, and this is the reason for the type.</b> A single radius makes a
///     mob standing on the boundary flicker between chasing and resetting once per frame, because the
///     player is moving and the comparison keeps changing sides. The tether is where it starts
///     worrying and the break is where it gives up; nothing happens in between, and coming back inside
///     the tether — not merely inside the break — is what clears it.
/// </remarks>
[DataContract("Leash")]
public sealed class LeashDefinition {
    /// <summary>How far it will follow before it starts to give up, in metres.</summary>
    public float Tether { get; set; } = 40f;

    /// <summary>How far before it does, in metres.</summary>
    public float Break { get; set; } = 60f;

    /// <summary>How long it may spend stretched before it gives up anyway, in seconds. Zero for never.</summary>
    public float Patience { get; set; } = 5f;

    /// <summary>What it does then.</summary>
    public LeashBehaviour Behaviour { get; set; }

    /// <summary>Whether it comes back at full health.</summary>
    /// <remarks>
    ///     ⚠ <b>True by default, and turning it off is a decision.</b> A mob that keeps its damage
    ///     across a reset can be whittled down over a dozen pulls by one player who never has to win
    ///     a fight — which is the oldest exploit in the genre.
    /// </remarks>
    public bool HealsOnReset { get; set; } = true;
}

/// <summary>What a leash check concluded.</summary>
/// <param name="State">Where it is now.</param>
/// <param name="Changed">Whether that is different from a moment ago.</param>
/// <param name="ShouldReset">Whether the caller should send it home.</param>
public readonly record struct LeashVerdict(LeashState State, bool Changed, bool ShouldReset);

/// <summary>One mob's leash: how far it has been pulled and for how long.</summary>
/// <remarks>
///     ⚠ <b>It is handed a distance rather than two positions.</b> Working out how far apart two
///     things are needs the scene, and an AI library that owned that would be a second spatial query —
///     the same boundary <c>PvpMatch.Occupy</c> and <c>InteractionNode</c> sit on.
/// </remarks>
public sealed class Leash {
    float stretchedSince = float.PositiveInfinity;

    /// <summary>Makes one, held.</summary>
    /// <param name="definition">How far and what happens.</param>
    public Leash(LeashDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        Definition = definition;
    }

    /// <summary>How far and what happens.</summary>
    public LeashDefinition Definition { get; }

    /// <summary>Where it is.</summary>
    public LeashState State { get; private set; }

    /// <summary>How far it will follow before it starts to give up.</summary>
    public float Tether => MathF.Max(0f, Definition.Tether);

    /// <summary>How far before it does, never inside the tether.</summary>
    public float Break => MathF.Max(Tether, Definition.Break);

    /// <summary>How long it has been stretched, or zero.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>The time.</returns>
    public float StretchedFor(float now) =>
        float.IsPositiveInfinity(stretchedSince) ? 0f : MathF.Max(0f, now - stretchedSince);

    /// <summary>Says how far it has been pulled and asks what to do.</summary>
    /// <param name="distance">How far it is from where it started.</param>
    /// <param name="now">The clock.</param>
    /// <returns>What to do.</returns>
    public LeashVerdict Check(float distance, float now) {
        var before = State;

        if (distance <= Tether) {
            // Inside the *tether*, not merely inside the break — which is the hysteresis.
            State = LeashState.Held;
            stretchedSince = float.PositiveInfinity;

            return new(State, before != State, false);
        }

        if (distance >= Break) {
            State = LeashState.Broken;

            return new(State, before != State, true);
        }

        if (State != LeashState.Stretched) {
            State = LeashState.Stretched;
            stretchedSince = now;

            return new(State, true, false);
        }

        // Patience is what stops a mob being kited round a pillar for ever at exactly tether plus one.
        if (Definition.Patience > 0f && StretchedFor(now) >= Definition.Patience) {
            State = LeashState.Broken;

            return new(State, true, true);
        }

        return new(State, false, false);
    }

    /// <summary>Puts it back to held. What finishing a reset does.</summary>
    public void Release() {
        State = LeashState.Held;
        stretchedSince = float.PositiveInfinity;
    }
}
