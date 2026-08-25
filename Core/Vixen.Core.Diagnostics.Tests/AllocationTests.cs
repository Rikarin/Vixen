// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Xunit;

namespace Vixen.Core.Diagnostics.Tests;

/// <summary>
///     What a log line costs the allocator, measured rather than asserted in prose.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A deterministic counter, never a clock.</b>
///         <see cref="GC.GetAllocatedBytesForCurrentThread" /> returns the exact number of bytes this
///         thread has allocated; it does not vary with machine load, core count or what else is
///         running, so a bound written against it means the thing it names. A wall-clock budget in
///         its place would measure CPU availability and fail on a busy CI box while a real
///         regression walked through.
///     </para>
///     <para>
///         <b>Why these numbers are the ones to pin.</b> Doc 13 asks for a ring that is "UTF-8
///         encoded, structured fields intact" with the enabled path near zero. The measured floor
///         says that is unreachable through <c>ILogger.Log&lt;TState&gt;</c> at any implementation
///         quality: the generated state is a struct reachable only through
///         <c>IReadOnlyList&lt;KeyValuePair&lt;string, object?&gt;&gt;</c>, so reading a field boxes
///         the state once and every value-type argument again, and the formatter's contract is to
///         return a <see cref="string" />. Measured on this checkout: reading the structured fields
///         costs 56 B/line for a single <c>int</c> argument and the formatter alone costs 40 B/line,
///         against 128 B/line for what the ring does today. Packing bytes would move the cost, not
///         remove it — so what is worth defending is that the number does not grow, and that the
///         disabled path stays at exactly zero.
///     </para>
/// </remarks>
public class AllocationTests {
    const int Iterations = 10_000;

    /// <summary>
    ///     The ceiling for one enabled line. Measured at 128 B — an 88-byte <see cref="LogRecord" />
    ///     and the 40-byte message string the <c>ILogger</c> formatter contract obliges. The bound is
    ///     loose enough for a runtime that lays <see cref="DateTimeOffset" /> out differently and
    ///     tight enough that adding one more allocation to the write path fails it.
    /// </summary>
    const long EnabledCeiling = 160;

    /// <summary>Bytes this thread allocates running <paramref name="body" /> the measured count.</summary>
    /// <remarks>
    ///     <para>
    ///         The warm-up is not politeness: the first calls pay for the JIT, the generated
    ///         formatter's one-time state and the ring's backing array, none of which is per-line
    ///         cost. Ten thousand iterations then make a single stray byte visible as a fractional
    ///         one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this counter cannot see is an allocation that never escapes.</b> Found by
    ///         sabotaging these tests: a bare <c>_ = new object();</c> added to the disabled path
    ///         did not move the number at all, because the JIT's escape analysis removes it — the
    ///         sabotage only registered once the reference was stored in a field. So a green result
    ///         means "nothing reaches the heap and survives", which is the property worth having,
    ///         and not "no allocation was written in the source".
    ///     </para>
    /// </remarks>
    static long BytesPerCall(Action<int> body) {
        for (var index = 0; index < 1_000; index++) {
            body(index);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < Iterations; index++) {
            body(index);
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / Iterations;
    }

    [Fact]
    public void A_disabled_line_allocates_nothing_at_all() {
        var sink = new RingBufferSink(capacity: 1024) { MinimumLevel = LogLevel.Warning };
        var logger = sink.CreateLogger("Vixen.Probe");

        // Exactly zero, not "small": the generated method returns before touching its arguments, and
        // that is the property that makes leaving log statements in warm code affordable. A single
        // byte here would mean something on the path started touching them.
        Assert.Equal(0, BytesPerCall(_ => TestLog.Debug(logger)));
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void An_enabled_line_allocates_a_bounded_amount() {
        var sink = new RingBufferSink(capacity: 1024);
        var logger = sink.CreateLogger("Vixen.Probe");

        var bytes = BytesPerCall(index => TestLog.Line(logger, index));

        Assert.InRange(bytes, 1, EnabledCeiling);

        // ⚠ Asserted, because a sink that stopped writing would pass the bound perfectly. An
        // allocation ceiling met by doing nothing is the failure mode the number cannot see.
        Assert.Equal(1024, sink.Count);
    }

    [Fact]
    public void A_record_the_wrap_overwrites_is_lost_whole_and_never_in_half() {
        var sink = new RingBufferSink(capacity: 4);
        var logger = sink.CreateLogger("Vixen.Probe");

        for (var index = 0; index < 100; index++) {
            TestLog.Line(logger, index);
        }

        // The ring holds one reference per slot, so a wrap replaces exactly one whole record. There
        // is no state in which half of a record survives — which is the property a byte-packed ring
        // would have to reintroduce a defence for, since a wrap landing mid-record would leave a
        // trailing fragment, and a fragment cut inside a multi-byte UTF-8 sequence is a decode
        // error rather than a truncation.
        var snapshot = sink.Snapshot();

        Assert.Equal(4, snapshot.Length);
        Assert.Equal(96, sink.DroppedCount);

        for (var index = 0; index < snapshot.Length; index++) {
            Assert.Equal($"line {96 + index}", snapshot[index].Message);
            Assert.Equal("Vixen.Probe", snapshot[index].Category);
            Assert.Equal(LogLevel.Information, snapshot[index].Level);
        }
    }
}
