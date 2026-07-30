// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Diagnostics;

namespace Vixen.Editor.Profiler;

/// <summary>One scope name, and everything the capture recorded under it.</summary>
/// <param name="Key">Which scope.</param>
/// <param name="Calls">How many times it ran.</param>
/// <param name="TotalMilliseconds">How long, inclusive of everything nested inside it.</param>
/// <param name="SelfMilliseconds">How long with its children's time taken out.</param>
/// <param name="MaximumMilliseconds">The worst single call.</param>
public readonly record struct ProfileEntry(
    ProfilingKey Key,
    int Calls,
    double TotalMilliseconds,
    double SelfMilliseconds,
    double MaximumMilliseconds
) {
    /// <summary>What the scope is called.</summary>
    public string Name => Key.Name;

    /// <summary>The average call, in milliseconds.</summary>
    public double MeanMilliseconds => Calls == 0 ? 0d : TotalMilliseconds / Calls;
}

/// <summary>The table under the flame chart: every scope, aggregated.</summary>
/// <remarks>
///     <para>
///         <b>The half of a profiler that answers "what is slow", where the chart answers "what
///         happened".</b> A flame chart shows one frame's shape and is unreadable for a question
///         like "which scope costs the most across two hundred frames"; the table is that question
///         and nothing else.
///     </para>
///     <para>
///         ⚠ <b>Self time is summed per <i>node</i> and total time per <i>sample</i>, and mixing the
///         two is the classic way to get a table that does not add up.</b> A recursive scope appears
///         at two levels of the same tree — its inclusive time is counted twice if you sum samples,
///         which is why total here is the sum of durations and self is the sum of
///         <see cref="FlameNode.SelfMilliseconds" />: the second is what a subtree contributed and
///         is additive across the whole capture whatever the nesting did.
///     </para>
/// </remarks>
public static class ProfileSummary {
    /// <summary>Aggregates every thread's scopes by key.</summary>
    /// <param name="threads">The capture's threads.</param>
    /// <returns>The entries, most total time first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="threads" /> is null.</exception>
    public static IReadOnlyList<ProfileEntry> Build(IReadOnlyList<ProfileThread> threads) {
        ArgumentNullException.ThrowIfNull(threads);

        Dictionary<ProfilingKey, Accumulator> byKey = [];

        foreach (var thread in threads) {
            foreach (var root in thread.Roots) {
                root.Walk(
                    node => {
                        ref var entry = ref System.Runtime.InteropServices.CollectionsMarshal
                            .GetValueRefOrAddDefault(byKey, node.Sample.Key, out _);

                        entry.Calls++;
                        entry.TotalTicks += node.Sample.DurationTicks;
                        entry.SelfMilliseconds += node.SelfMilliseconds;
                        entry.MaximumTicks = Math.Max(entry.MaximumTicks, node.Sample.DurationTicks);
                    }
                );
            }
        }

        var entries = new List<ProfileEntry>(byKey.Count);

        foreach (var (key, accumulated) in byKey) {
            entries.Add(
                new(
                    key,
                    accumulated.Calls,
                    accumulated.TotalTicks * 1000d / Stopwatch.Frequency,
                    accumulated.SelfMilliseconds,
                    accumulated.MaximumTicks * 1000d / Stopwatch.Frequency
                )
            );
        }

        // Total descending, which is the order somebody opens the table to read. A tie breaks on the
        // name so that two scopes costing nothing do not swap places between captures and look like
        // something changed.
        entries.Sort(
            static (left, right) => left.TotalMilliseconds != right.TotalMilliseconds
                ? right.TotalMilliseconds.CompareTo(left.TotalMilliseconds)
                : string.CompareOrdinal(left.Name, right.Name)
        );

        return entries;
    }

    struct Accumulator {
        public int Calls;
        public long TotalTicks;
        public double SelfMilliseconds;
        public long MaximumTicks;
    }
}

/// <summary>What changed between two captures, for one scope.</summary>
/// <param name="Key">Which scope.</param>
/// <param name="Before">What it cost in the baseline, or <see langword="null" /> if it is new.</param>
/// <param name="After">What it costs now, or <see langword="null" /> if it has gone.</param>
public readonly record struct ProfileDelta(ProfilingKey Key, ProfileEntry? Before, ProfileEntry? After) {
    /// <summary>What the scope is called.</summary>
    public string Name => Key.Name;

    /// <summary>How much longer it takes now, in milliseconds. Negative is faster.</summary>
    public double TotalDelta => (After?.TotalMilliseconds ?? 0d) - (Before?.TotalMilliseconds ?? 0d);

    /// <summary>How many more times it ran.</summary>
    public int CallsDelta => (After?.Calls ?? 0) - (Before?.Calls ?? 0);

    /// <summary>
    ///     How much longer it takes, as a fraction of the baseline — or <see langword="null" /> when
    ///     there is no baseline to be a fraction of.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Null rather than infinity for a scope that is new.</b> A table sorted on ratio with
    ///     an infinity in it puts every newly-instrumented scope above every real regression, which
    ///     is the opposite of useful — a view shows "new" in that column instead.
    /// </remarks>
    public double? Ratio => Before is { TotalMilliseconds: > 0d } baseline
        ? TotalDelta / baseline.TotalMilliseconds
        : null;
}

/// <summary>Two captures, subtracted.</summary>
/// <remarks>
///     ⚠ <b>Compared by scope name and not by position, and normalised per frame.</b> Two captures
///     are almost never the same length — one is the four seconds before the change and one the
///     three after — so comparing totals says the shorter run is faster. Every figure here is
///     divided by the capture's frame count first, which is what makes "this scope got 0.4 ms
///     slower per frame" a sentence with meaning.
/// </remarks>
public static class CaptureComparison {
    /// <summary>Subtracts one capture from another.</summary>
    /// <param name="before">The baseline.</param>
    /// <param name="after">What to compare with it.</param>
    /// <returns>The scopes in either capture, biggest regression first.</returns>
    /// <exception cref="ArgumentNullException">Either capture is null.</exception>
    public static IReadOnlyList<ProfileDelta> Compare(ProfileCapture before, ProfileCapture after) {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var baseline = PerFrame(before);
        var current = PerFrame(after);

        List<ProfileDelta> deltas = [];

        foreach (var (key, entry) in current) {
            deltas.Add(new(key, baseline.TryGetValue(key, out var was) ? was : null, entry));
        }

        foreach (var (key, entry) in baseline) {
            if (!current.ContainsKey(key)) {
                deltas.Add(new(key, entry, null));
            }
        }

        // Regressions first, which is what somebody presses Compare to find. Descending on the
        // signed delta puts improvements at the bottom rather than hiding them.
        deltas.Sort(
            static (left, right) => left.TotalDelta != right.TotalDelta
                ? right.TotalDelta.CompareTo(left.TotalDelta)
                : string.CompareOrdinal(left.Name, right.Name)
        );

        return deltas;
    }

    static Dictionary<ProfilingKey, ProfileEntry> PerFrame(ProfileCapture capture) {
        var frames = capture.FrameCount;
        Dictionary<ProfilingKey, ProfileEntry> normalised = [];

        foreach (var entry in capture.Summary) {
            normalised[entry.Key] = entry with {
                Calls = entry.Calls / frames,
                TotalMilliseconds = entry.TotalMilliseconds / frames,
                SelfMilliseconds = entry.SelfMilliseconds / frames
            };
        }

        return normalised;
    }
}
