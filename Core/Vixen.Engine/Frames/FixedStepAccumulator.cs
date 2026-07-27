// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Engine.Frames;

/// <summary>
///     Turns a variable frame delta into a whole number of fixed simulation steps.
/// </summary>
/// <remarks>
///     <para>
///         Simulation that is a function of the frame delta is not reproducible, is not stable at
///         low frame rates, and cannot be replayed — three properties physics, netcode and the
///         determinism tests all need. So the simulation advances in fixed steps and the renderer
///         interpolates between the last two, which is what <see cref="Alpha" /> is for.
///     </para>
///     <para>
///         <b>The catch-up clamp is the part that matters.</b> A frame that took a second — a
///         breakpoint, a shader compile, a laptop lid — owes sixty steps. Running all of them makes
///         the next frame take a second too, which owes sixty more: the spiral of death. Clamping
///         discards the debt instead, so the simulation runs slow for one frame and then recovers.
///         Time that is dropped is dropped visibly, via <see cref="DroppedSteps" />, rather than
///         quietly.
///     </para>
///     <para>
///         <b>Counted in ticks, not in seconds.</b> A <see cref="TimeSpan" /> is exact in ticks and
///         approximate in <c>double</c> seconds, and the difference shows up at once: a hundred
///         milliseconds against a sixtieth-of-a-second step divides to 5.999… in floating point and
///         owes five steps instead of six. Integer division of ticks owes six, and a loop fed the
///         same delta for an hour banks exactly the remainder it should rather than drifting.
///     </para>
/// </remarks>
public sealed class FixedStepAccumulator {
    long accumulated;

    /// <summary>How long one step is. Sixty a second by default.</summary>
    public TimeSpan Step { get; }

    /// <summary>The most steps one frame may run before the rest of the debt is discarded.</summary>
    public int MaxStepsPerFrame { get; }

    /// <summary>How much time is banked and not yet stepped.</summary>
    public TimeSpan Pending => TimeSpan.FromTicks(accumulated);

    /// <summary>
    ///     How far the frame is between the last completed step and the next, from 0 to 1 — what a
    ///     renderer interpolates with so a 60 Hz simulation looks smooth at 144 Hz.
    /// </summary>
    public float Alpha => (float)accumulated / Step.Ticks;

    /// <summary>How many steps have been discarded to the clamp since the accumulator was made.</summary>
    public long DroppedSteps { get; private set; }

    /// <summary>How many steps have been run since the accumulator was made.</summary>
    public long TotalSteps { get; private set; }

    /// <summary>Creates an accumulator.</summary>
    /// <param name="step">How long one step is.</param>
    /// <param name="maxStepsPerFrame">The catch-up clamp.</param>
    public FixedStepAccumulator(TimeSpan step = default, int maxStepsPerFrame = 5) {
        // Ticks, not FromSeconds(1d / 60d): that overload rounds to the nearest millisecond, so the
        // "sixtieth of a second" it hands back is seventeen milliseconds — a 2% error that shows up
        // as a missing step in every frame whose length is a whole number of milliseconds.
        var chosen = step == default ? TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60) : step;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(chosen, TimeSpan.Zero, nameof(step));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStepsPerFrame, 1);

        Step = chosen;
        MaxStepsPerFrame = maxStepsPerFrame;
    }

    /// <summary>Banks a frame's elapsed time and reports how many steps to run.</summary>
    /// <param name="elapsed">The frame's elapsed time. Negative is refused.</param>
    /// <returns>How many fixed steps this frame owes, from zero to <see cref="MaxStepsPerFrame" />.</returns>
    public int Advance(TimeSpan elapsed) {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero, nameof(elapsed));

        accumulated += elapsed.Ticks;
        var step = Step.Ticks;
        var owed = accumulated / step;

        if (owed <= MaxStepsPerFrame) {
            accumulated -= owed * step;
            TotalSteps += owed;
            return (int)owed;
        }

        DroppedSteps += owed - MaxStepsPerFrame;
        TotalSteps += MaxStepsPerFrame;

        // The debt goes, and the fractional remainder with it. Keeping the remainder would mean the
        // frame after a stall still starts part-way through a step, which is a smaller version of
        // the same problem.
        accumulated -= owed * step;
        return MaxStepsPerFrame;
    }

    /// <summary>The clock one fixed step sees.</summary>
    /// <param name="frame">The frame's clock, for the total and the time scale.</param>
    /// <returns>A clock whose elapsed time is exactly one step.</returns>
    /// <remarks>
    ///     The elapsed time is the step and not the frame's, because a system in
    ///     <c>SystemPhase.FixedUpdate</c> that multiplied by the frame delta would be doing the
    ///     variable-step thing this class exists to prevent.
    /// </remarks>
    public GameTime StepTime(GameTime frame) =>
        frame with { Elapsed = Step * frame.TimeScale, UnscaledElapsed = Step };

    /// <summary>Throws away the banked time, without counting it as dropped.</summary>
    /// <remarks>For a loop that has just loaded a scene, where the time before it means nothing.</remarks>
    public void Reset() => accumulated = 0;
}
