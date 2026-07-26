// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Vixen.Core;

/// <summary>
///     Records native and GPU resources that must be disposed by hand, so that "something is not
///     being released" turns into a list of allocation sites instead of a memory graph that grows.
/// </summary>
/// <remarks>
///     <para>
///         Managed memory does not need this — the GC finds it. What needs it is everything the GC
///         cannot see: Vulkan buffers and images, native allocations, mapped ranges, file handles.
///         A resource type calls <see cref="Track" /> in its constructor and <see cref="Untrack" />
///         in its <c>Dispose</c>; whatever is still live at shutdown was leaked, and its stack says
///         where it came from.
///     </para>
///     <para>
///         <b>Compiled out of release builds.</b> <see cref="IsSupported" /> is a compile-time
///         constant, so in a build without <c>DEBUG</c> or <c>VIXEN_MEMORY_DEBUG</c> the tracking
///         methods have no body and calls to them fold away. Guard expensive argument construction
///         with <c>if (LeakTracker.IsSupported)</c> and the JIT will remove the whole block.
///     </para>
/// </remarks>
public static class LeakTracker {
#if DEBUG || VIXEN_MEMORY_DEBUG
    /// <summary>Whether this build can track at all. A compile-time constant.</summary>
    public const bool IsSupported = true;
#else
    /// <summary>Whether this build can track at all. A compile-time constant.</summary>
    public const bool IsSupported = false;
#endif

    /// <summary>The handle returned by <see cref="Track" /> when nothing was recorded.</summary>
    public const long NotTracked = 0;

#if DEBUG || VIXEN_MEMORY_DEBUG
    static readonly ConcurrentDictionary<long, LeakReport> Live = new();
    static long nextId;
    static bool enabled = true;
    static bool captureStackTraces = true;
#endif

    /// <summary>
    ///     Whether tracking is currently recording. Settable so a soak test or a level load can turn
    ///     it off for a stretch; always <see langword="false" /> where <see cref="IsSupported" /> is.
    /// </summary>
    public static bool IsEnabled {
#if DEBUG || VIXEN_MEMORY_DEBUG
        get => enabled;
        set => enabled = value;
#else
        get => false;
        set { }
#endif
    }

    /// <summary>
    ///     Whether each tracked resource captures a stack trace. On by default where supported: the
    ///     stack is the entire value of the report. Turn it off when the capture cost distorts what
    ///     is being measured.
    /// </summary>
    public static bool CaptureStackTraces {
#if DEBUG || VIXEN_MEMORY_DEBUG
        get => captureStackTraces;
        set => captureStackTraces = value;
#else
        get => false;
        set { }
#endif
    }

    /// <summary>How many tracked resources are still live.</summary>
    public static int LiveCount =>
#if DEBUG || VIXEN_MEMORY_DEBUG
        Live.Count;
#else
        0;
#endif

    /// <summary>Records a resource as live.</summary>
    /// <param name="category">
    ///     What kind of resource it is — <c>"VkBuffer"</c>, <c>"NativeArray"</c>. Reports group on it,
    ///     so a constant is worth more here than a formatted string.
    /// </param>
    /// <param name="description">Which one it is: a debug name, a size, a URL.</param>
    /// <returns>
    ///     A handle to pass to <see cref="Untrack" />, or <see cref="NotTracked" /> if nothing was
    ///     recorded. Resource types should store it regardless and pass it back unconditionally.
    /// </returns>
    public static long Track(string category, string? description = null) {
#if DEBUG || VIXEN_MEMORY_DEBUG
        if (!enabled) {
            return NotTracked;
        }

        var id = Interlocked.Increment(ref nextId);
        var stack = captureStackTraces ? new StackTrace(1, true).ToString() : null;
        Live[id] = new(id, category, description, stack);
        return id;
#else
        return NotTracked;
#endif
    }

    /// <summary>Records a resource as released.</summary>
    /// <param name="handle">The handle <see cref="Track" /> returned. <see cref="NotTracked" /> is ignored.</param>
    /// <returns>
    ///     <see langword="false" /> if the handle was not live — which means either a double release
    ///     or a handle from before <see cref="IsEnabled" /> was turned on.
    /// </returns>
    public static bool Untrack(long handle) {
#if DEBUG || VIXEN_MEMORY_DEBUG
        return handle != NotTracked && Live.TryRemove(handle, out _);
#else
        return false;
#endif
    }

    /// <summary>Everything still live, oldest first.</summary>
    /// <returns>The live resources, or an empty array where tracking is not supported.</returns>
    public static LeakReport[] Snapshot() {
#if DEBUG || VIXEN_MEMORY_DEBUG
        var reports = Live.Values.ToArray();
        Array.Sort(reports, static (left, right) => left.Handle.CompareTo(right.Handle));
        return reports;
#else
        return [];
#endif
    }

    /// <summary>
    ///     Renders <see cref="Snapshot" /> as a report to log at shutdown: a count per category,
    ///     then the individual entries with their stacks.
    /// </summary>
    /// <returns>The report, or an empty string when nothing is live.</returns>
    public static string FormatReport() {
        var reports = Snapshot();
        if (reports.Length == 0) {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{reports.Length} undisposed resource(s):");

        foreach (var group in reports.GroupBy(static report => report.Category).OrderBy(static g => g.Key, StringComparer.Ordinal)) {
            builder.Append(CultureInfo.InvariantCulture, $"\n  {group.Key}: {group.Count()}");
        }

        foreach (var report in reports) {
            builder.Append(CultureInfo.InvariantCulture, $"\n\n[{report.Handle}] {report.Category}");
            if (report.Description is not null) {
                builder.Append(CultureInfo.InvariantCulture, $" — {report.Description}");
            }

            if (report.StackTrace is not null) {
                builder.Append('\n').Append(report.StackTrace);
            }
        }

        return builder.ToString();
    }

    /// <summary>Forgets every tracked resource. For tests, which must not see each other's leaks.</summary>
    public static void Reset() {
#if DEBUG || VIXEN_MEMORY_DEBUG
        Live.Clear();
#endif
    }
}

/// <summary>One tracked resource, as reported by <see cref="LeakTracker.Snapshot" />.</summary>
/// <param name="Handle">The tracking handle; also the order in which resources were tracked.</param>
/// <param name="Category">The resource kind.</param>
/// <param name="Description">Which resource it is, if the caller said.</param>
/// <param name="StackTrace">Where it was allocated, if stack capture was on.</param>
public sealed record LeakReport(long Handle, string Category, string? Description, string? StackTrace);
