// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Collections;

namespace Vixen.Graphics;

/// <summary>A pool of GPU queries.</summary>
/// <remarks>
///     A marker a backend subclasses, on the same terms as <see cref="GpuBuffer" /> and every other
///     resource here: instances live in a backend-owned table and are reached only through
///     <see cref="QueryPoolHandle" />.
/// </remarks>
public abstract class GpuQueryPool {
    /// <summary>Only a backend derives from this.</summary>
    protected GpuQueryPool() { }
}

/// <summary>A query pool, by handle.</summary>
public readonly record struct QueryPoolHandle(Handle<GpuQueryPool> Value) {
    /// <summary>No pool.</summary>
    public static QueryPoolHandle Null => default;

    /// <summary>Whether this refers to anything.</summary>
    public bool IsValid => Value != Handle<GpuQueryPool>.Null;
}

/// <summary>What a pool's queries measure.</summary>
/// <remarks>
///     One member, and it is not an oversight. Occlusion and pipeline-statistics queries are real
///     and are not what E4 needs; adding them now would mean guessing at the shape of a result set
///     — a statistics query resolves to a struct whose fields depend on which counters were asked
///     for — before anything reads one. The enum exists so that adding them is an addition rather
///     than a change to every signature below.
/// </remarks>
public enum QueryKind : byte {
    /// <summary>A GPU clock reading, written where the command list says.</summary>
    Timestamp = 0
}

/// <summary>What to create a query pool as.</summary>
/// <param name="Kind">What its queries measure.</param>
/// <param name="Count">How many queries it holds.</param>
/// <param name="Name">
///     A name for the debugger and the validation layers, on the same terms as every other
///     description here.
/// </param>
public readonly record struct QueryPoolDescription(QueryKind Kind, int Count, string Name = "");

/// <summary>
///     How a raw timestamp difference becomes a duration, and the one number a caller cannot guess.
/// </summary>
/// <remarks>
///     <para>
///         A GPU timestamp is a tick of a clock whose rate is the device's business — Vulkan reports
///         it as <c>VkPhysicalDeviceLimits.timestampPeriod</c>, nanoseconds per tick, and it is a
///         float because on several vendors it is not an integer. Two readings subtracted give ticks;
///         ticks times the period give nanoseconds. Nothing else about the value is portable, and in
///         particular the zero point is not: a timestamp is comparable with another timestamp from
///         the same device and with nothing at all on the CPU.
///     </para>
///     <para>
///         ⚠ <b>That is why a GPU timeline is drawn relative to its own first reading.</b> Lining a
///         GPU scope up against a CPU one needs a calibrated pair
///         (<c>VK_EXT_calibrated_timestamps</c>), which is an extension a good many drivers do not
///         have. Doc 13's frame breakdown is a GPU timeline beside a CPU one rather than a single
///         merged track, and this is the reason.
///     </para>
/// </remarks>
public static class GpuTimestamps {
    /// <summary>Turns a tick count into nanoseconds.</summary>
    /// <param name="ticks">The difference between two readings from one device.</param>
    /// <param name="period">
    ///     The device's <see cref="GraphicsDeviceFeatures.TimestampPeriod" />, in nanoseconds
    ///     per tick.
    /// </param>
    /// <returns>The duration in nanoseconds, or zero when the device reports no period.</returns>
    public static double ToNanoseconds(ulong ticks, float period) => period <= 0f ? 0d : ticks * (double)period;

    /// <summary>Turns a tick count into milliseconds.</summary>
    /// <param name="ticks">The difference between two readings from one device.</param>
    /// <param name="period">The device's <see cref="GraphicsDeviceFeatures.TimestampPeriod" />.</param>
    /// <returns>The duration in milliseconds.</returns>
    public static double ToMilliseconds(ulong ticks, float period) => ToNanoseconds(ticks, period) / 1_000_000d;
}
