// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Graphics;

namespace Vixen.TextureGraph;

public sealed partial class TexturePlanEvaluator {
    /// <summary>Whether each evaluation times its ops on the device, into <see cref="TextureBake.OpMilliseconds" />.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>For a host that says what a node cost</b> — Patina #182's timing badges, with
    ///         <c>TextureGraphCompiler.OpNodes</c> saying whose each op is. Off by default: a bake that nobody
    ///         reads the times of should not pay for a query pool.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A time is the gap between two timestamps, the one after this op and the one after the op
    ///             before it
    ///         </b>
    ///         , so it holds the op's barrier and its dispatch. A timestamp records when everything
    ///         before it finished, so an op that overlapped its neighbour on a deeply pipelined device can read
    ///         as less than it cost, and a tiny one as nought. Read them as a ranking of where a bake spends its
    ///         time, not as a stopwatch on each.
    ///     </para>
    ///     <para>
    ///         An op that ran on the CPU is left out — its time is the host's — and the op after it is
    ///         measured from a timestamp written when the bake carried on. A device that cannot time
    ///         (<see cref="GraphicsDeviceFeatures.CanTimeFrames" />) leaves the times empty and bakes as it did.
    ///     </para>
    /// </remarks>
    public bool TimeOps { get; set; }

    /// <summary>A pool for one evaluation's timestamps, or none when nothing times it.</summary>
    OpTimer? StartTiming(TexturePlan plan) =>
        TimeOps && device.Features.CanTimeFrames ? new(device, plan.Ops.Length + 1) : null;

    /// <summary>One evaluation's timestamps: one at the start, then one after each op that ran.</summary>
    sealed class OpTimer(IGraphicsDevice device, int capacity) : IDisposable {
        readonly QueryPoolHandle pool = device.CreateQueryPool(new(QueryKind.Timestamp, capacity, "texture graph ops"));

        /// <summary>Each timed op, with the query after it and the query it is measured from.</summary>
        readonly List<(int Op, int Since, int Query)> marks = [];

        int written;

        /// <summary>Resets the pool and writes the first timestamp, at the top of the first list.</summary>
        public void Start(ICommandList commands) {
            commands.ResetQueries(pool, 0, capacity);
            commands.WriteTimestamp(pool, written++);
        }

        /// <summary>Writes a timestamp after an op, which is that op's time when it ran on the device.</summary>
        public void After(ICommandList commands, int op, bool onDevice) {
            if (onDevice) {
                marks.Add((op, written - 1, written));
            }

            commands.WriteTimestamp(pool, written++);
        }

        /// <summary>Each timed op's milliseconds, once the device has finished; empty if it has no answer.</summary>
        public ImmutableDictionary<int, double> Read() {
            var ticks = new ulong[written];

            if (written == 0 || !device.TryResolveQueries(pool, 0, ticks)) {
                return ImmutableDictionary<int, double>.Empty;
            }

            var period = device.Features.TimestampPeriod;
            var times = ImmutableDictionary.CreateBuilder<int, double>();

            foreach (var (op, since, query) in marks) {
                var gap = ticks[query] >= ticks[since] ? ticks[query] - ticks[since] : 0;

                times[op] = GpuTimestamps.ToMilliseconds(gap, period);
            }

            return times.ToImmutable();
        }

        public void Dispose() => device.Destroy(pool);
    }
}
