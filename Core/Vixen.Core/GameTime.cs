// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Core;

/// <summary>
///     The clock as a frame sees it: how long the last frame took, how much time has accumulated,
///     and how many frames have gone by. Passed by value into every update, so nothing can advance
///     time behind another subsystem's back.
/// </summary>
/// <remarks>
///     <para>
///         Two clocks run at once. <see cref="Elapsed" /> and <see cref="Total" /> are scaled by
///         <see cref="TimeScale" /> and are what gameplay should use — pausing is
///         <c>TimeScale = 0</c>, and everything downstream stops without knowing why.
///         <see cref="UnscaledElapsed" /> ignores the scale and is what UI animation, profiling and
///         anything that must keep moving during a pause should use.
///     </para>
///     <para>
///         The fixed-step accumulator that decides how many simulation steps a frame owes lives in
///         <c>Vixen.Engine</c>. This type only records what a step or a frame was handed.
///     </para>
/// </remarks>
[DataContract]
public readonly record struct GameTime(
    TimeSpan Total,
    TimeSpan Elapsed,
    TimeSpan UnscaledElapsed,
    long FrameCount,
    float TimeScale
) : ISpanFormattable {
    /// <summary>Time before the first frame: nothing elapsed, running at normal speed.</summary>
    public static GameTime Zero => new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, 1f);

    /// <summary>
    ///     <see cref="Elapsed" /> in seconds — the delta almost every caller actually wants, at the
    ///     precision the rest of the engine works in.
    /// </summary>
    public float DeltaSeconds => (float)Elapsed.TotalSeconds;

    /// <summary><see cref="UnscaledElapsed" /> in seconds.</summary>
    public float UnscaledDeltaSeconds => (float)UnscaledElapsed.TotalSeconds;

    /// <summary>
    ///     <see cref="Total" /> in seconds, at <see cref="double" /> precision because it grows
    ///     without bound and a <see cref="float" /> stops resolving a 60 Hz frame after a few hours.
    /// </summary>
    public double TotalSeconds => Total.TotalSeconds;

    /// <summary>Whether time is stopped — a paused game, or a stepped one between steps.</summary>
    public bool IsPaused => TimeScale == 0f;

    /// <summary>
    ///     Produces the next frame's time: scales <paramref name="unscaledElapsed" />, adds it to
    ///     the total, and counts the frame.
    /// </summary>
    /// <param name="unscaledElapsed">Wall-clock time since the previous frame.</param>
    /// <param name="timeScale">
    ///     Rate to run at — <c>1</c> for real time, <c>0</c> to pause, above <c>1</c> to
    ///     fast-forward. Must not be negative: rewinding time is a simulation feature, not an
    ///     arithmetic one, and letting it through here would silently corrupt every accumulator
    ///     downstream.
    /// </param>
    /// <returns>The time for the frame about to run.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="unscaledElapsed" /> or <paramref name="timeScale" /> is negative.
    /// </exception>
    public GameTime Advance(TimeSpan unscaledElapsed, float timeScale = 1f) {
        ArgumentOutOfRangeException.ThrowIfLessThan(unscaledElapsed, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(timeScale);

        var elapsed = timeScale == 1f ? unscaledElapsed : unscaledElapsed * timeScale;
        return new(Total + elapsed, elapsed, unscaledElapsed, FrameCount + 1, timeScale);
    }

    /// <summary>Renders the frame number and both deltas, for logs and overlays.</summary>
    /// <returns>The time in text.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"frame {FrameCount} @ {Total.TotalSeconds:F3}s (dt {Elapsed.TotalMilliseconds:F2}ms, scale {TimeScale:F2})"
    );

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc />
    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) =>
        destination.TryWrite(
            CultureInfo.InvariantCulture,
            $"frame {FrameCount} @ {Total.TotalSeconds:F3}s (dt {Elapsed.TotalMilliseconds:F2}ms, scale {TimeScale:F2})",
            out charsWritten
        );
}
