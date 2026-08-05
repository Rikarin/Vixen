// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Water;

/// <summary>What kind of thing disturbed the water.</summary>
/// <remarks>
///     ⚠ <b>Two and not a spectrum, because the two are told apart by <em>time</em> and nothing
///     else.</b> A splash is one event; a wake is a continuous one. Anything a consumer wants to do
///     differently — a burst of spray against a trailing V, a loud noise against a steady hiss —
///     follows from that, and a third value would be a third thing for every consumer to have an
///     opinion about.
/// </remarks>
public enum WaterDisturbanceKind {
    /// <summary>Something moving through the surface: a hull under way, a swimmer.</summary>
    Wake,

    /// <summary>Something arriving at it: a body entering the water, an impact.</summary>
    Splash
}

/// <summary>Something disturbed the water here, this hard, this wide.</summary>
/// <param name="Position">Where, on the ground plane.</param>
/// <param name="Radius">How wide the disturbance is, in metres.</param>
/// <param name="Strength">How hard, in metres a second. Negative pushes the surface down.</param>
/// <param name="Kind">What sort of thing it was.</param>
/// <param name="Height">Where the surface was, in world units — for a consumer that places geometry.</param>
/// <remarks>
///     <para>
///         <b>[35 § D12](../../docs/plan/35-water.md#d12-ripples-are-a-sliding-window-height-field-and-they-are-displacement-not-geometry)'s
///         wake and splash hooks, as one event with two consumers.</b> A ripple field turns it into an
///         injection; <c>Vixen.Vfx</c> turns it into a burst of spray. Which is the same shape as
///         § D2's one-evaluator rule and is there for the same reason: two producers — one for the
///         simulation and one for the particles — is a wake whose spray is not where the ripple is,
///         and the frame it stops agreeing on is the frame something changed in only one of them.
///     </para>
///     <para>
///         ⚠ <b>In the kernel, so a dedicated server produces the same events and drops them.</b> A
///         headless build has no particles and still simulates the boat that would have made them;
///         putting the event where the renderer is would mean the two builds disagreed about how the
///         hull was moving, which is a desync with an entirely innocent-looking cause.
///     </para>
///     <para>
///         ⚠ <b>A strength in metres a second, which is a <em>rate</em> and not a displacement.</b>
///         See <see cref="WaterRipples.Inject" />: a source that pushed the height down would carve a
///         permanent dent in the lake, where one that pushes the rate down makes a depression that
///         springs back.
///     </para>
/// </remarks>
public readonly record struct WaterDisturbance(
    Vector2 Position,
    float Radius,
    float Strength,
    WaterDisturbanceKind Kind,
    float Height
);

/// <summary>A step's disturbances, for whoever wants them.</summary>
/// <remarks>
///     <para>
///         <b>A bounded ring rather than a list</b>, and the bound is § D12's own: "an unbounded
///         number of sources is how this feature becomes a frame-time cliff". What does not fit is
///         counted into <see cref="Overflowed" /> rather than dropped in silence.
///     </para>
///     <para>
///         ⚠ <b>Drained by the consumer and cleared by the producer, not the other way round.</b>
///         There is more than one consumer — the ripple field and the particles — so a queue that
///         emptied itself on the first read would give the second one nothing, and the symptom is a
///         wake with no spray or spray with no wake depending on the order two systems were added in.
///         <see cref="Clear" /> is the step's own call.
///     </para>
/// </remarks>
public sealed class WaterDisturbances {
    readonly WaterDisturbance[] queued;

    /// <summary>Creates a queue with a stated budget.</summary>
    /// <param name="budget">How many disturbances one step may hold.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="budget" /> is not positive.</exception>
    public WaterDisturbances(int budget = 64) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);

        queued = new WaterDisturbance[budget];
    }

    /// <summary>How many one step may hold.</summary>
    public int Budget => queued.Length;

    /// <summary>How many this step has.</summary>
    public int Count { get; private set; }

    /// <summary>How many it had to refuse.</summary>
    /// <remarks>
    ///     ⚠ <b>Non-zero is a wake some hulls do not make</b>, and the arbitrariness is stated rather
    ///     than hidden: the budget is spent in arrival order, so a scene over it has <em>some</em>
    ///     disturbances rather than merely fewer. A priority somebody assigns is a second thing to
    ///     author and would still drop something.
    /// </remarks>
    public int Overflowed { get; private set; }

    /// <summary>This step's disturbances.</summary>
    public ReadOnlySpan<WaterDisturbance> Queued => queued.AsSpan(0, Count);

    /// <summary>Adds one, if it fits.</summary>
    /// <param name="disturbance">The disturbance.</param>
    /// <returns>Whether it fitted.</returns>
    public bool Add(in WaterDisturbance disturbance) {
        if (Count >= queued.Length) {
            Overflowed++;

            return false;
        }

        queued[Count++] = disturbance;

        return true;
    }

    /// <summary>Empties it, at the end of a step.</summary>
    public void Clear() {
        Count = 0;
        Overflowed = 0;
    }
}
