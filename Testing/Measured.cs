// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;
using Xunit;

namespace Vixen.Testing;

/// <summary>
///     Counts the bytes a piece of work asks for, once it has stopped growing whatever it grows.
/// </summary>
/// <remarks>
///     <para>
///         Per-thread allocation rather than <c>GC.GetTotalMemory</c>, because the latter measures the
///         heap after whatever the collector has done to it and is not a count of what this code asked
///         for. A single-digit byte count here is not noise to be tolerated — the paths these tests
///         guard own every buffer they touch, so the answer is zero or something is wrong.
///     </para>
///     <para>
///         <b>Which is why the collector has to be kept out of the measurement.</b>
///         <c>GC.GetAllocatedBytesForCurrentThread</c> is the thread's allocated total less the unused
///         remainder of the allocation context it is currently holding, and a collection retires that
///         context — so the counter steps up by as much as a whole context, some eight kilobytes, at
///         whichever collection happens to land inside the measured window, whether or not the measured
///         code allocated a byte of it. Whether one lands depends on what else ran in the process, so
///         the artefact moves with test ordering and machine load rather than with the code under
///         measurement. The same artefact is described at <c>FuzzSession.Weigh</c>, where a window long
///         enough to absorb it was the right answer. It is not the right answer here: the claim is
///         exactly zero, and a tolerance wide enough to hide a collection is wide enough to hide the
///         per-frame allocation these tests exist to catch.
///     </para>
///     <para>
///         Collecting first is most of the defence. It hands the loop an <i>empty</i> allocation
///         context, and work that allocates nothing never asks for another one — so a collection
///         landing mid-window, including one provoked by whichever other test class is running
///         alongside this one, finds nothing to settle up and the reading stays at zero. The artefact
///         can therefore only ever inflate a reading that was already non-zero, which is what
///         <see cref="GC.CollectionCount" /> either side is for: a non-zero reading with a collection
///         in it is thrown away and measured again rather than reported. A number that survives
///         <see cref="Attempts" /> of that is a number the collector cannot explain.
///     </para>
///     <para>
///         <b>A caller whose work has a side effect must count it rather than predict it.</b> Because a
///         non-zero reading is measured again, <paramref name="work" /> runs <i>at least</i>
///         <c>warmUp + passes</c> times and possibly several times that. Assert that a counter the work
///         advances is non-zero, or read it after the measurement — never against an arithmetic
///         expectation of how many times the work should have run.
///     </para>
/// </remarks>
static class Measured {
    /// <summary>How many times a non-zero reading with a collection in it is measured again.</summary>
    const int Attempts = 4;

    /// <summary>
    ///     Runs <paramref name="work" /> <paramref name="warmUp" /> times, then counts the bytes the
    ///     next <paramref name="passes" /> of it allocate on this thread.
    /// </summary>
    public static long Bytes(Action work, int warmUp = 200, int passes = 1_000) {
        for (var index = 0; index < warmUp; index++) {
            work();
        }

        for (var attempt = 1; ; attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var collections = GC.CollectionCount(0);
            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var index = 0; index < passes; index++) {
                work();
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            if (allocated == 0 || GC.CollectionCount(0) == collections) {
                return allocated;
            }

            if (attempt == Attempts) {
                Assert.Fail(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"All {Attempts} measurements read non-zero with a collection inside them, the last of them {allocated:N0} B. The loop starts on an empty allocation context, so work that allocates nothing reads zero straight through a collection — this work is allocating, and the collection only means that number is an upper bound rather than the amount."
                    )
                );
            }
        }
    }

    /// <summary>
    ///     Measures as <see cref="Bytes" /> does and fails if the answer is not zero, naming the types
    ///     the allocation came from rather than only the size of it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the assertion doc 12 § allocation gates asks for; <see cref="Bytes" /> stays for
    ///         the callers that want the number itself, including the ones that assert it is
    ///         <i>non</i>-zero.
    ///     </para>
    ///     <para>
    ///         <b>The naming is a separate pass, and it has to be.</b> The counted window is the one
    ///         thing that must not be disturbed, and it is far too small to name anything from — see
    ///         <see cref="AllocationNames" />, which carries the measurements. So the count is taken
    ///         exactly as before, including the re-measurement of a reading with a collection in it, and
    ///         only a reading that has already failed is handed to a second, longer, armed run. Nothing
    ///         the explanatory run does can change the number being reported, and no allocation from a
    ///         discarded re-measurement can reach the message.
    ///     </para>
    /// </remarks>
    /// <param name="work">The work whose steady state is meant to allocate nothing.</param>
    /// <param name="warmUp">Passes run before the counter is read.</param>
    /// <param name="passes">Passes counted.</param>
    /// <param name="because">
    ///     What the caller knows and the message cannot work out — why this path owns its buffers, what
    ///     the cost would be per frame. It is prefixed to the count and the names.
    /// </param>
    public static void NothingAllocated(Action work, int warmUp = 200, int passes = 1_000, string? because = null) {
        var allocated = Bytes(work, warmUp, passes);

        if (allocated == 0) {
            return;
        }

        Assert.Fail(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{(because is null ? string.Empty : because + " ")}Expected 0 B, measured {allocated:N0} B over {passes:N0} passes ({(double)allocated / passes:N2} B/pass). {AllocationNames.Explain(work, allocated, passes)}"
            )
        );
    }
}

/// <summary>
///     Names the types an already-failed allocation reading came from, by re-running the work with the
///     runtime's allocation sampler armed.
/// </summary>
/// <remarks>
///     <para>
///         <b>⚠ This is not what docs/plan/12 and docs/plan/15 § R4 specify, because that cannot be
///         built.</b> Both documents say the gate "names the exact allocation via a
///         <c>GCHeapAllocationEventSource</c> listener". There is no such event source in the runtime;
///         what exists is <c>Microsoft-Windows-DotNETRuntime</c>. Measured on .NET 10.0.11, macOS
///         arm64, workstation GC, against an in-process <see cref="EventListener" />:
///     </para>
///     <list type="bullet">
///         <item>
///             <c>GCAllocationTick</c>, the event the plan documents mean, <b>is never delivered at
///             all</b>. Not at keyword <c>GC</c> (0x1) at Informational, not with every keyword bit set
///             at Verbose, and not for a window of 96 MB in which some nine hundred other runtime
///             events arrived.
///         </item>
///         <item>
///             <c>AllocationSampled</c> <i>is</i> delivered, and only at keyword
///             <see cref="AllocationSamplingKeyword" /> — of all sixty-four keyword bits, at both
///             Informational and Verbose, that one bit is the only one that turns it on.
///         </item>
///         <item>
///             <b>It is sampled at roughly one allocation per 100 KB</b>, and the budget is not
///             tunable through the provider's filter arguments: seven candidate argument names all
///             left the rate at 43–55 samples per 4.8 MB against an unfiltered 50. Measured over
///             96,000,040 B: 941 samples, 102,019 B apiece.
///         </item>
///         <item>
///             <b>There is no call site.</b> The payload is
///             <c>AllocationKind, ClrInstanceID, TypeID, TypeName, Address, ObjectSize,
///             SampledByteOffset</c> — no stack — and the callback runs on the EventPipe dispatcher
///             thread, never the allocating one, so a <see cref="StackTrace" /> taken inside it names
///             the dispatcher. Across every sweep run, samples observed on the allocating thread: 0.
///         </item>
///     </list>
///     <para>
///         <b>So a 48-byte allocation cannot be seen where it happens.</b> The windows these gates fail
///         on are of the order of 9,640 B (200 passes) or 48,040 B (1,000 passes); at both of those the
///         sampler produced <i>nothing</i>. What it can do is name the <i>type</i>, once enough of the
///         same allocation has gone by — which is why this re-runs the work far more times than the
///         gate did. Measured: 5,000 passes / 240,040 B gave four samples and the guilty type was 100%
///         of the named bytes; 100,000 passes / 4,800,040 B gave forty-five, still 100%.
///     </para>
///     <para>
///         <b>The other thread is the trap.</b> Samples arrive from the whole process, so a second test
///         collection allocating in parallel drowns the answer — in a deliberate two-thread run the
///         report was 5,521,520 B of the noise type against 4,224 B of the real one. Every sample is
///         therefore filtered by <c>OSThreadId</c>, and the measuring thread's own id is learned by
///         writing <see cref="MeasuringThreadMarker" /> and reading the id back off it, because a
///         managed <see cref="EventSource" /> dispatches synchronously on the thread that wrote it
///         while the runtime's native events do not.
///     </para>
///     <para>
///         <b>Verify the instrument.</b> Three things can go wrong silently — the runtime source is
///         absent, the marker never arrives so no sample can be attributed, or the sampler is armed and
///         genuinely sees nothing. Each produces a different sentence, and only the third claims that
///         nothing was found; the first two say the instrument did not run. A message with no names in
///         it is never ambiguous about which of those happened.
///     </para>
/// </remarks>
sealed class AllocationNames : EventListener {
    /// <summary>
    ///     Bit 43, <c>AllocationSamplingKeyword</c>. Found by sweeping every bit at both levels rather
    ///     than read off a manifest; nothing else produces <c>AllocationSampled</c>.
    /// </summary>
    const EventKeywords AllocationSamplingKeyword = (EventKeywords)0x0000_0800_0000_0000;

    /// <summary>Bytes the runtime allocates, on average, between two samples. Measured at 102,019.</summary>
    const long SampleInterval = 100 * 1024;

    /// <summary>
    ///     How many samples the explanatory run aims for. Four was already enough for the guilty type
    ///     to be every named byte; this is that with room for a noisy neighbour.
    /// </summary>
    const int WantedSamples = 24;

    /// <summary>How many <see cref="Drain" /> objects are allocated between two checks of the drain.</summary>
    const int DrainBatch = 4_096;

    /// <summary>
    ///     ⚠ A hang check, not a bound. The explanatory run happens on a test that has already failed,
    ///     so it may take as long as it takes — but it must not run the suite out of its own timeout
    ///     when the work is expensive enough that the sample would never arrive.
    /// </summary>
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     ⚠ Matched exactly, and the sentinel is a top-level type for that reason. It was nested at
    ///     first and matched on <c>"+Drain"</c>; the runtime does not render a nested type's
    ///     <c>TypeName</c> that way, so the sentinel was never recognised, every explanatory run sat on
    ///     the <see cref="Patience" /> ceiling for twenty seconds and still reported the right answer.
    ///     A substring match would have hidden it again and would swallow any type under test whose own
    ///     name contained the sentinel's.
    /// </summary>
    static readonly string DrainName = typeof(AllocationDrainSentinel).FullName!;

    static readonly AllocationDrainSentinel?[] sink = new AllocationDrainSentinel?[1];

    readonly Lock gate = new();
    readonly Dictionary<string, Sampled> mine = [];
    EventSource? runtime;
    volatile bool armed;
    long measuringThread = -1;
    int elsewhere;
    int drained;

    protected override void OnEventSourceCreated(EventSource eventSource) {
        // ⚠ Called from the base constructor, which runs before this class's constructor body — so
        // everything it touches has to be a field initializer rather than something a constructor set.
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime") {
            runtime = eventSource;
        } else if (eventSource.Name == MeasuringThreadMarker.SourceName) {
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs data) {
        if (!armed) {
            return;
        }

        if (data.EventSource.Name == MeasuringThreadMarker.SourceName) {
            // Synchronous on the writing thread, so this id is the measuring thread's own.
            Volatile.Write(ref measuringThread, (long)data.OSThreadId);

            return;
        }

        if (data.EventName != "AllocationSampled") {
            return;
        }

        var names = data.PayloadNames;

        if (names is null) {
            return;
        }

        var typeName = "(unnamed)";
        long size = 0;

        for (var index = 0; index < names.Count; index++) {
            if (names[index] == "TypeName") {
                typeName = data.Payload?[index] as string ?? typeName;
            } else if (names[index] == "ObjectSize") {
                size = Convert.ToInt64(data.Payload?[index], CultureInfo.InvariantCulture);
            }
        }

        if ((long)data.OSThreadId != Volatile.Read(ref measuringThread)) {
            Interlocked.Increment(ref elsewhere);

            return;
        }

        if (typeName == DrainName) {
            Interlocked.Increment(ref drained);

            return;
        }

        lock (gate) {
            var seen = mine.GetValueOrDefault(typeName);
            mine[typeName] = new(seen.Count + 1, seen.Bytes + size);
        }
    }

    /// <summary>
    ///     Runs <paramref name="work" /> again, long enough for the sampler to see it, and returns the
    ///     sentence that follows the byte count in the failure message.
    /// </summary>
    public static string Explain(Action work, long allocated, int passes) {
        using var names = new AllocationNames();

        return names.Run(work, allocated, passes);
    }

    string Run(Action work, long allocated, int passes) {
        if (runtime is null) {
            return "No names, and the instrument did not run: this process has no Microsoft-Windows-DotNETRuntime EventSource, so the allocation sampler could never be armed. That is a fact about the runtime here, not about the work.";
        }

        // Enough passes that WantedSamples of the work's own allocation should go by. Work that
        // allocates a fraction of a byte per pass on average still gets a whole number out of this.
        var perPass = Math.Max((double)allocated / passes, 1d / passes);
        var explanatory = Math.Max(passes, (long)Math.Ceiling(WantedSamples * SampleInterval / perPass));

        EnableEvents(runtime, EventLevel.Verbose, AllocationSamplingKeyword);
        armed = true;
        MeasuringThreadMarker.Log.Measuring();

        if (Volatile.Read(ref measuringThread) < 0) {
            armed = false;
            DisableEvents(runtime);

            return $"No names, and the instrument did not run: the {MeasuringThreadMarker.SourceName} marker event never came back, so no sample could have been attributed to this thread. The marker source failed to construct or was not enabled.";
        }

        var watch = Stopwatch.StartNew();
        long ran = 0;

        while (ran < explanatory && watch.Elapsed < Patience) {
            for (var index = 0; index < passes; index++) {
                work();
            }

            ran += passes;
        }

        // ⚠ Samples are dispatched asynchronously, so the loop finishing does not mean they have
        // arrived, and waiting on a clock for them would be a guess. Allocate a type of our own until
        // one of *its* samples comes back instead: EventPipe keeps a thread's events in order, so a
        // Drain sample is proof that everything this thread allocated before it has been dispatched.
        var drain = Stopwatch.StartNew();

        while (Volatile.Read(ref drained) == 0 && drain.Elapsed < Patience) {
            for (var index = 0; index < DrainBatch; index++) {
                sink[0] = new();
            }
        }

        var undrained = Volatile.Read(ref drained) == 0;
        armed = false;
        DisableEvents(runtime);

        return Report(ran, watch.Elapsed, undrained);
    }

    string Report(long ran, TimeSpan elapsed, bool undrained) {
        List<KeyValuePair<string, Sampled>> found;

        lock (gate) {
            found = [.. mine.OrderByDescending(pair => pair.Value.Bytes)];
        }

        var other = Volatile.Read(ref elsewhere);
        var message = new StringBuilder();

        if (found.Count == 0) {
            message.Append(
                CultureInfo.InvariantCulture,
                $"No names, though the instrument did run: the sampler was armed across {ran:N0} further passes and no sample landed on this thread. The runtime samples roughly one allocation in {SampleInterval:N0} B and cannot be made to sample more finely, so a reading this small is below what it can see — raise the pass count at the call site, or reproduce the regression where it allocates more."
            );
        } else {
            message.Append(
                CultureInfo.InvariantCulture,
                $"Sampled over {ran:N0} further passes ({elapsed.TotalSeconds:N1} s), this thread allocated:"
            );

            foreach (var (typeName, sampled) in found) {
                message.Append(
                    CultureInfo.InvariantCulture,
                    $"{Environment.NewLine}    {typeName} — {sampled.Count:N0} sample(s), {sampled.Bytes:N0} B seen"
                );
            }

            message.Append(
                CultureInfo.InvariantCulture,
                $"{Environment.NewLine}⚠ The runtime's sampler names the type, not the call site: the event carries no stack and fires on a dispatcher thread. It samples one allocation in ~{SampleInterval:N0} B, so these are proportions and not totals."
            );
        }

        if (other > 0) {
            message.Append(
                CultureInfo.InvariantCulture,
                $" {other:N0} sample(s) from other threads were discarded, since the count itself is per-thread."
            );
        }

        if (undrained) {
            message.Append(" ⚠ The drain gave up waiting, so this list may be short.");
        }

        return message.ToString();
    }

    readonly record struct Sampled(int Count, long Bytes);
}

/// <summary>
///     The type the drain allocates. Never part of the report; it is what the report waits on instead
///     of a clock. Top level so that the <c>TypeName</c> the runtime reports is exactly
///     <see cref="Type.FullName" />.
/// </summary>
sealed class AllocationDrainSentinel {
    public long A = 1, B = 2, C = 3, D = 4;
}

/// <summary>
///     Written once per explanatory run so that the measuring thread can learn its own OS thread id.
/// </summary>
/// <remarks>
///     A managed <see cref="EventSource" /> is dispatched to an in-process <see cref="EventListener" />
///     synchronously on the thread that wrote it, so the <c>OSThreadId</c> on this event is that
///     thread's — which is the only portable way to get the id that the runtime's own
///     <c>AllocationSampled</c> events are stamped with. The runtime's native events do not have that
///     property, which is also why this marker cannot double as the drain sentinel.
/// </remarks>
[EventSource(Name = SourceName)]
sealed class MeasuringThreadMarker : EventSource {
    public const string SourceName = "Vixen-Testing-Measured";

    public static readonly MeasuringThreadMarker Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Measuring() => WriteEvent(1);
}
