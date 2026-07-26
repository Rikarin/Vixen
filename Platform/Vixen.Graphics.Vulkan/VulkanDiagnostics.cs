// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace Vixen.Graphics.Vulkan;

/// <summary>Everything the validation layers have said, so that a test can assert they said nothing.</summary>
/// <remarks>
///     <para>
///         Validation-clean-in-debug is a stated non-negotiable
///         ([00](../../docs/plan/00-vision-and-principles.md)), and a warning printed to the console
///         is not a gate — it is a thing that scrolls past. This exists so the test suite can fail on
///         one.
///     </para>
///     <para>
///         Written to by the debug-messenger callback, which Vulkan invokes on whatever thread hit the
///         problem, so it is a concurrent collection rather than a list with a lock: the callback runs
///         inside a driver call and blocking there is a good way to turn a warning into a deadlock.
///     </para>
///     <para>
///         Process-wide static, deliberately. The callback Vulkan holds is a bare function pointer
///         with no room for a <c>this</c>, and the alternative — a pinned per-instance context — buys
///         nothing when the question being asked is "did anything at all complain during this test".
///     </para>
/// </remarks>
public static class VulkanDiagnostics {
    /// <summary>How many messages to keep before dropping them.</summary>
    /// <remarks>
    ///     A bound rather than a growing list: a backend that has gone wrong can produce a validation
    ///     message per draw call, and the failure should be a readable test report rather than an
    ///     out-of-memory.
    /// </remarks>
    public const int Capacity = 64;

    static readonly ConcurrentQueue<string> Recorded = new();

    static int errors;
    static int warnings;

    /// <summary>How many errors the layers have reported.</summary>
    public static int ErrorCount => Volatile.Read(ref errors);

    /// <summary>How many warnings the layers have reported.</summary>
    public static int WarningCount => Volatile.Read(ref warnings);

    /// <summary>What they said, up to <see cref="Capacity" /> messages.</summary>
    public static IReadOnlyCollection<string> Messages => Recorded;

    /// <summary>Forgets everything, so that a test can attribute what follows to itself.</summary>
    public static void Reset() {
        Recorded.Clear();
        Volatile.Write(ref errors, 0);
        Volatile.Write(ref warnings, 0);
    }

    internal static void Record(bool isError, string message) {
        if (isError) {
            Interlocked.Increment(ref errors);
        } else {
            Interlocked.Increment(ref warnings);
        }

        if (Recorded.Count >= Capacity) {
            Recorded.TryDequeue(out _);
        }

        Recorded.Enqueue(message);
    }
}
