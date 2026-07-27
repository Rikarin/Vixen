// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace Vixen.Platform.Ios;

/// <summary>When something happened, in the ticks the rest of the engine measures in.</summary>
/// <remarks>
///     UIKit timestamps a touch with <c>UITouch.Timestamp</c>, which is seconds since boot on the
///     same clock as <c>CACurrentMediaTime</c> — a different origin and a different unit from
///     <see cref="Stopwatch" />, which is what every <see cref="PlatformEvent" /> carries. Converting
///     is possible and is deliberately not done: the offset between the two would have to be
///     sampled once and would drift, and nothing yet reads an event timestamp for anything finer
///     than ordering. Stamped on arrival instead, and this exists so that decision has one place to
///     be revisited when input latency starts being measured.
/// </remarks>
static class IosClock {
    /// <summary>Now.</summary>
    public static long Now => Stopwatch.GetTimestamp();
}
