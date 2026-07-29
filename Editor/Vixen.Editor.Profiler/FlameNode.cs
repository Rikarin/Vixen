// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Diagnostics;

namespace Vixen.Editor.Profiler;

/// <summary>One scope in a flame chart: a sample, what ran inside it, and where it draws.</summary>
/// <remarks>
///     <para>
///         <b>The tree the rings do not keep.</b> A <see cref="ProfilerSample" /> carries a depth and
///         two timestamps and nothing about its parent — which is exactly right for the recording
///         side, where a parent pointer would be a second store on the hot path. Rebuilding the
///         nesting is a walk over the samples in begin order, and it happens once per capture rather
///         than once per frame.
///     </para>
///     <para>
///         ⚠ <b>Samples arrive in <i>completion</i> order, not in begin order.</b> A scope is
///         recorded when it closes, so a parent lands in the ring after every child it contains —
///         which means the obvious reading of the array builds every tree upside down.
///         <see cref="Build" /> sorts before it walks, and that sort is the whole trick.
///     </para>
/// </remarks>
public sealed class FlameNode {
    readonly List<FlameNode> children = [];

    FlameNode(ProfilerSample sample, int level) {
        Sample = sample;
        Level = level;
    }

    /// <summary>The scope itself.</summary>
    public ProfilerSample Sample { get; }

    /// <summary>Which row it draws on, counting from zero at the root.</summary>
    /// <remarks>
    ///     ⚠ <b>The tree's depth rather than the sample's.</b> A ring that wrapped mid-frame can
    ///     hand over a child whose parent went over the side; the sample still says depth three, and
    ///     drawing it on row three would leave three empty rows above a bar with nothing over it.
    ///     Rebuilt from where the node actually landed, an orphan draws as a root — which is what it
    ///     is, as far as anything left in the capture can tell.
    /// </remarks>
    public int Level { get; }

    /// <summary>What ran inside it, in begin order.</summary>
    public IReadOnlyList<FlameNode> Children => children;

    /// <summary>What it is called.</summary>
    public string Name => Sample.Key.Name;

    /// <summary>How long it ran, in milliseconds.</summary>
    public double Milliseconds => Sample.DurationMilliseconds;

    /// <summary>How long it ran with its children's time taken out, in milliseconds.</summary>
    /// <remarks>
    ///     The column a profiler is actually read for: an inclusive time says which subsystem is
    ///     expensive and a self time says which <i>function</i> is, and the two disagree exactly
    ///     where the answer is interesting.
    /// </remarks>
    public double SelfMilliseconds {
        get {
            var ticks = (long)Sample.DurationTicks;

            foreach (var child in children) {
                ticks -= child.Sample.DurationTicks;
            }

            // A parent whose children overlap it by a tick or two — which rounding at the ring's
            // resolution can produce — is not a scope with negative self time.
            return Math.Max(0L, ticks) * 1000d / Stopwatch.Frequency;
        }
    }

    /// <summary>When it ended, in <see cref="Stopwatch" /> ticks.</summary>
    public long EndTicks => Sample.BeginTicks + Sample.DurationTicks;

    /// <summary>How many nodes are in this subtree, including this one.</summary>
    public int Count {
        get {
            var total = 1;

            foreach (var child in children) {
                total += child.Count;
            }

            return total;
        }
    }

    /// <summary>How many rows deep this subtree goes.</summary>
    public int Height {
        get {
            var deepest = 0;

            foreach (var child in children) {
                deepest = Math.Max(deepest, child.Height);
            }

            return deepest + 1;
        }
    }

    /// <summary>Rebuilds one thread's nesting from its flat samples.</summary>
    /// <param name="samples">The thread's samples, in whatever order the ring gave them.</param>
    /// <returns>The depth-zero scopes, in begin order, each holding its children.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="samples" /> is null.</exception>
    public static IReadOnlyList<FlameNode> Build(ProfilerSample[] samples) {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Length == 0) {
            return [];
        }

        // Copied before sorting: the array belongs to the capture, and a view that reordered the
        // caller's samples would change what a summary walking the same array sees.
        var ordered = new ProfilerSample[samples.Length];
        samples.CopyTo(ordered, 0);

        // Begin first, then depth: two scopes that begin in the same tick — which happens whenever a
        // parent's first child opens immediately — have to come out parent-first or the child is
        // considered before there is anything to nest it under.
        Array.Sort(
            ordered,
            static (left, right) => left.BeginTicks != right.BeginTicks
                ? left.BeginTicks.CompareTo(right.BeginTicks)
                : left.Depth.CompareTo(right.Depth)
        );

        List<FlameNode> roots = [];
        List<FlameNode> open = [];

        foreach (var sample in ordered) {
            // Close everything this sample is not inside. Two conditions and both are needed: the
            // depth says "this is a sibling or an uncle", and the end tick says "the open scope
            // finished before this one started" — which is what catches a frame boundary, where the
            // next root begins at depth zero after the previous root closed.
            while (open.Count > 0 && (open[^1].Sample.Depth >= sample.Depth || open[^1].EndTicks <= sample.BeginTicks)) {
                open.RemoveAt(open.Count - 1);
            }

            var node = new FlameNode(sample, open.Count);

            if (open.Count == 0) {
                roots.Add(node);
            } else {
                open[^1].children.Add(node);
            }

            open.Add(node);
        }

        return roots;
    }

    /// <summary>Calls <paramref name="visit" /> on this node and everything under it, parents first.</summary>
    /// <param name="visit">What to call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="visit" /> is null.</exception>
    public void Walk(Action<FlameNode> visit) {
        ArgumentNullException.ThrowIfNull(visit);

        visit(this);

        foreach (var child in children) {
            child.Walk(visit);
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} {Milliseconds:0.###} ms ({children.Count} child scopes)";
}
