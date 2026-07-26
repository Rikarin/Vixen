// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Vixen.Core.Diagnostics.Tests;

/// <summary>
///     Logging and profiling. The profiler tests take a lock over shared static state, so they live
///     in one class — xunit runs a class serially, and two of them racing would sample each other.
/// </summary>
public class DiagnosticsTests : IDisposable {
    static readonly ProfilingKey Outer = ProfilingKey.Register("Test.Outer");
    static readonly ProfilingKey Inner = ProfilingKey.Register("Test.Inner");

    public DiagnosticsTests() {
        Profiler.Reset();
        Profiler.IsEnabled = false;
    }

    public void Dispose() {
        Profiler.Reset();
        Profiler.IsEnabled = false;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Registering_the_same_name_twice_gives_the_same_key() {
        var first = ProfilingKey.Register("Test.Repeated");
        var second = ProfilingKey.Register("Test.Repeated");

        Assert.Equal(first, second);
        Assert.Equal("Test.Repeated", first.Name);
        Assert.True(first.IsValid);
        Assert.True(ProfilingKey.TryGet("Test.Repeated", out var looked));
        Assert.Equal(first, looked);
    }

    [Fact]
    public void An_uninitialised_key_is_detectably_unregistered() {
        // Ids start at 1 precisely so this holds, rather than a zeroed key aliasing whichever scope
        // was declared first.
        Assert.False(default(ProfilingKey).IsValid);
        Assert.Equal(ProfilingKey.None, default);
        Assert.Equal("<none>", ProfilingKey.None.Name);
        Assert.False(ProfilingKey.TryGet("never registered", out _));
        Assert.Throws<ArgumentException>(() => ProfilingKey.Register("  "));
    }

    [Fact]
    public void A_disabled_profiler_records_nothing() {
        Profiler.IsEnabled = false;

        using (Profiler.Begin(Outer)) {
            Thread.Sleep(1);
        }

        Assert.Empty(Profiler.Collect().SelectMany(static thread => thread.Samples));
    }

    [Fact]
    public void An_enabled_profiler_records_a_scope_with_its_duration() {
        Profiler.IsEnabled = true;

        using (Profiler.Begin(Outer)) {
            Thread.Sleep(2);
        }

        var sample = Assert.Single(Profiler.Collect().SelectMany(static thread => thread.Samples));

        Assert.Equal(Outer, sample.Key);
        Assert.Equal(0, sample.Depth);
        Assert.True(sample.DurationMilliseconds >= 1.0, $"Expected at least 1 ms, got {sample.DurationMilliseconds}.");
        Assert.True(sample.DurationMicroseconds > sample.DurationMilliseconds);
    }

    [Fact]
    public void Nested_scopes_record_their_depth() {
        Profiler.IsEnabled = true;

        using (Profiler.Begin(Outer)) {
            using (Profiler.Begin(Inner)) {
                using (Profiler.Begin(Inner)) {
                    // Deliberately empty: the shape is what is being measured.
                }
            }
        }

        var samples = Profiler.Collect().SelectMany(static thread => thread.Samples).ToArray();

        Assert.Equal(3, samples.Length);

        // Inner scopes close first, so they are recorded first — depths 2, 1, 0.
        Assert.Equal(new[] { 2, 1, 0 }, samples.Select(static sample => sample.Depth));
        Assert.Equal(Outer, samples[^1].Key);
    }

    [Fact]
    public void Samples_are_attributed_to_the_frame_they_ran_in() {
        Profiler.IsEnabled = true;

        var first = Profiler.BeginFrame();
        using (Profiler.Begin(Outer)) { }

        var second = Profiler.BeginFrame();
        using (Profiler.Begin(Outer)) { }

        Assert.Equal(first + 1, second);

        var samples = Profiler.Collect().SelectMany(static thread => thread.Samples).ToArray();
        Assert.Equal(new[] { first, second }, samples.Select(static sample => sample.FrameIndex));
    }

    [Fact]
    public void Each_thread_records_into_its_own_ring() {
        Profiler.IsEnabled = true;

        using (Profiler.Begin(Outer)) { }

        var thread = new Thread(() => {
                Thread.CurrentThread.Name = "worker";
                using (Profiler.Begin(Inner)) { }
            }
        );

        thread.Start();
        thread.Join();

        var collected = Profiler.Collect();

        Assert.Equal(2, collected.Length);
        Assert.Contains(collected, entry => entry.ThreadName == "worker");
        Assert.All(collected, entry => Assert.Single(entry.Samples));
    }

    [Fact]
    public void Collecting_empties_the_rings() {
        Profiler.IsEnabled = true;
        using (Profiler.Begin(Outer)) { }

        Assert.Single(Profiler.Collect().SelectMany(static thread => thread.Samples));
        Assert.Empty(Profiler.Collect().SelectMany(static thread => thread.Samples));
    }

    [Fact]
    public void An_overflowing_ring_keeps_the_newest_samples_and_says_how_many_it_dropped() {
        // A truncated trace that does not admit it is worse than no trace.
        Profiler.Reset();
        Profiler.CapacityPerThread = 8;
        Profiler.IsEnabled = true;

        try {
            for (var i = 0; i < 50; i++) {
                using (Profiler.Begin(Outer)) { }
            }

            Assert.True(Profiler.DroppedSampleCount >= 40);
            Assert.Equal(8, Profiler.Collect().Single().Samples.Length);
        } finally {
            Profiler.CapacityPerThread = Profiler.DefaultCapacityPerThread;
        }
    }

    [Fact]
    public void A_trace_exports_as_json_a_viewer_will_open() {
        Profiler.IsEnabled = true;
        Profiler.BeginFrame();

        using (Profiler.Begin(Outer)) {
            using (Profiler.Begin(Inner)) { }
        }

        using var stream = new MemoryStream();
        TraceExporter.WriteChromeTrace(Profiler.Collect(), stream);

        stream.Position = 0;
        using var document = JsonDocument.Parse(stream);
        var events = document.RootElement.GetProperty("traceEvents").EnumerateArray().ToArray();

        // One metadata record naming the thread, plus one complete event per scope.
        Assert.Equal("M", events[0].GetProperty("ph").GetString());
        Assert.Equal(3, events.Length);

        var scopes = events[1..];
        Assert.All(scopes, element => Assert.Equal("X", element.GetProperty("ph").GetString()));
        Assert.Contains(scopes, element => element.GetProperty("name").GetString() == "Test.Inner");
        Assert.All(scopes, element => Assert.True(element.GetProperty("dur").GetDouble() >= 0));
    }

    [Fact]
    public void A_summary_reports_the_worst_scope_first() {
        Profiler.IsEnabled = true;

        using (Profiler.Begin(Outer)) {
            Thread.Sleep(3);
        }

        using (Profiler.Begin(Inner)) { }

        var summary = TraceExporter.Summarize(Profiler.Collect());

        Assert.Contains("Test.Outer", summary, StringComparison.Ordinal);
        Assert.Contains("Test.Inner", summary, StringComparison.Ordinal);
        Assert.True(
            summary.IndexOf("Test.Outer", StringComparison.Ordinal)
            < summary.IndexOf("Test.Inner", StringComparison.Ordinal)
        );

        Assert.Equal("No samples recorded.", TraceExporter.Summarize([]));
    }

    [Fact]
    public void A_log_record_carries_its_category_level_and_event_id() {
        var sink = new RingBufferSink(16);
        var logger = sink.CreateLogger("Vixen.Graphics.VulkanDevice");

        TestLog.DeviceLost(logger, 42);

        var record = Assert.Single(sink.Snapshot());

        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Equal(2001, record.EventId.Id);
        Assert.Equal("Vixen.Graphics.VulkanDevice", record.Category);
        Assert.Equal("Device lost after 42 ms", record.Message);
        Assert.Null(record.Exception);
        Assert.Equal(Environment.CurrentManagedThreadId, record.ThreadId);
    }

    [Fact]
    public void The_sink_drops_what_is_below_the_minimum_level() {
        var sink = new RingBufferSink(16) { MinimumLevel = LogLevel.Warning };
        var logger = sink.CreateLogger("Test");

        TestLog.Debug(logger);
        TestLog.Line(logger, 0);
        TestLog.Warning(logger);
        TestLog.Error(logger);

        Assert.Equal(2, sink.Count);
        Assert.All(sink.Snapshot(), record => Assert.True(record.Level >= LogLevel.Warning));
        Assert.False(logger.IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void The_longest_matching_category_prefix_wins() {
        // Which is what makes "verbose asset loading without render spam" a two-line configuration
        // rather than an ordering puzzle.
        var sink = new RingBufferSink(64) { MinimumLevel = LogLevel.Warning };
        sink.SetCategoryLevel("Vixen", LogLevel.Error);
        sink.SetCategoryLevel("Vixen.Assets", LogLevel.Trace);

        Assert.True(sink.IsEnabled("Vixen.Assets.Loader", LogLevel.Trace));
        Assert.False(sink.IsEnabled("Vixen.Graphics.Device", LogLevel.Warning));
        Assert.True(sink.IsEnabled("Vixen.Graphics.Device", LogLevel.Error));

        // Anything outside the configured prefixes falls back to the global minimum.
        Assert.True(sink.IsEnabled("Something.Else", LogLevel.Warning));
        Assert.False(sink.IsEnabled("Something.Else", LogLevel.Information));

        sink.ClearCategoryLevels();
        Assert.False(sink.IsEnabled("Vixen.Assets.Loader", LogLevel.Trace));
    }

    [Fact]
    public void The_ring_keeps_the_most_recent_lines_and_counts_what_it_dropped() {
        var sink = new RingBufferSink(4);
        var logger = sink.CreateLogger("Test");

        for (var i = 0; i < 10; i++) {
            TestLog.Line(logger, i);
        }

        var snapshot = sink.Snapshot();

        Assert.Equal(4, snapshot.Length);
        Assert.Equal("line 6", snapshot[0].Message);
        Assert.Equal("line 9", snapshot[^1].Message);
        Assert.Equal(6, sink.DroppedCount);
    }

    [Fact]
    public void An_exception_is_kept_alongside_the_message() {
        var sink = new RingBufferSink(4);
        var logger = sink.CreateLogger("Test");
        var failure = new InvalidOperationException("boom");

        TestLog.Failed(logger, failure);

        var record = Assert.Single(sink.Snapshot());
        Assert.Same(failure, record.Exception);
    }

    [Fact]
    public void Clearing_the_sink_keeps_the_dropped_count() {
        var sink = new RingBufferSink(2);
        var logger = sink.CreateLogger("Test");

        for (var i = 0; i < 5; i++) {
            TestLog.Line(logger, i);
        }

        var dropped = sink.DroppedCount;
        sink.Clear();

        Assert.Equal(0, sink.Count);
        Assert.Equal(dropped, sink.DroppedCount);
    }

    [Fact]
    public void Level_None_records_nothing_whatever_the_configuration_says() {
        var sink = new RingBufferSink(4) { MinimumLevel = LogLevel.Trace };
        Assert.False(sink.IsEnabled("Test", LogLevel.None));
    }
}
