// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Tracing;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Vixen.Core.Diagnostics.Tests;

/// <summary>
///     The sinks doc 13 asks for, and the rate limiting in front of them.
/// </summary>
/// <remarks>
///     Each sink is tested through <see cref="TestLog" />'s generated call sites rather than the
///     <c>ILogger</c> extension methods, for the reason ADR-008 gives: that is the path the engine
///     takes, and it is the one where the event id — which is what the rate limiter treats as a
///     message's identity — actually arrives.
/// </remarks>
public class SinkTests {
    [Fact]
    public void A_burst_gets_through_and_the_rest_of_the_window_is_counted() {
        var clock = new ManualTimeProvider();
        var limiter = new LogRateLimiter(TimeSpan.FromSeconds(1), burst: 2, timeProvider: clock);

        Assert.True(limiter.TryAdmit("Test", 2001, LogLevel.Warning, out var first));
        Assert.Equal(0, first);
        Assert.True(limiter.TryAdmit("Test", 2001, LogLevel.Warning, out _));

        for (var i = 0; i < 4812; i++) {
            Assert.False(limiter.TryAdmit("Test", 2001, LogLevel.Warning, out _));
        }

        Assert.Equal(4812, limiter.SuppressedCount);

        // The next window opens and the first line through it carries what the last one swallowed —
        // which is the whole point: a log that silently drops 4 812 lines is lying about the shape
        // of what happened.
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(limiter.TryAdmit("Test", 2001, LogLevel.Warning, out var suppressed));
        Assert.Equal(4812, suppressed);

        Assert.True(limiter.TryAdmit("Test", 2001, LogLevel.Warning, out var second));
        Assert.Equal(0, second);
    }

    [Fact]
    public void Identity_is_the_category_and_the_event_id_together() {
        var clock = new ManualTimeProvider();
        var limiter = new LogRateLimiter(TimeSpan.FromSeconds(1), burst: 1, timeProvider: clock);

        Assert.True(limiter.TryAdmit("Vixen.Graphics", 2001, LogLevel.Warning, out _));
        Assert.False(limiter.TryAdmit("Vixen.Graphics", 2001, LogLevel.Warning, out _));

        // A different event, and a different category, each get their own budget.
        Assert.True(limiter.TryAdmit("Vixen.Graphics", 2002, LogLevel.Warning, out _));
        Assert.True(limiter.TryAdmit("Vixen.Assets", 2001, LogLevel.Warning, out _));
        Assert.Equal(3, limiter.TrackedEventCount);
    }

    [Fact]
    public void Critical_records_are_never_suppressed() {
        var clock = new ManualTimeProvider();
        var limiter = new LogRateLimiter(TimeSpan.FromSeconds(1), burst: 1, timeProvider: clock);

        for (var i = 0; i < 100; i++) {
            Assert.True(limiter.TryAdmit("Test", 1, LogLevel.Critical, out _));
        }

        Assert.Equal(0, limiter.SuppressedCount);
    }

    [Fact]
    public void A_full_table_admits_novel_events_rather_than_losing_them() {
        var clock = new ManualTimeProvider();
        var limiter = new LogRateLimiter(TimeSpan.FromSeconds(1), burst: 1, maxTrackedEvents: 2, timeProvider: clock);

        Assert.True(limiter.TryAdmit("Test", 1, LogLevel.Warning, out _));
        Assert.True(limiter.TryAdmit("Test", 2, LogLevel.Warning, out _));

        // Both windows are still open, so nothing can be evicted — and the first report of a third
        // event has to survive that. A limiter that drops it because two other events filled its
        // table is worse than no limiter.
        Assert.True(limiter.TryAdmit("Test", 3, LogLevel.Error, out _));
        Assert.Equal(1, limiter.UntrackedCount);
        Assert.Equal(2, limiter.TrackedEventCount);

        // Once the tracked windows have passed with nothing pending, the table makes room instead.
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(limiter.TryAdmit("Test", 4, LogLevel.Error, out _));
        Assert.Equal(1, limiter.UntrackedCount);
    }

    [Fact]
    public void Resetting_forgets_the_windows_but_keeps_the_totals() {
        var clock = new ManualTimeProvider();
        var limiter = new LogRateLimiter(TimeSpan.FromSeconds(1), burst: 1, timeProvider: clock);

        Assert.True(limiter.TryAdmit("Test", 1, LogLevel.Warning, out _));
        Assert.False(limiter.TryAdmit("Test", 1, LogLevel.Warning, out _));

        limiter.Reset();

        Assert.Equal(0, limiter.TrackedEventCount);
        Assert.Equal(1, limiter.SuppressedCount);
        Assert.True(limiter.TryAdmit("Test", 1, LogLevel.Warning, out var suppressed));
        Assert.Equal(0, suppressed);
    }

    [Fact]
    public void A_rate_limited_sink_records_what_it_dropped_on_the_line_that_follows() {
        var clock = new ManualTimeProvider();
        var sink = new RingBufferSink(64) {
            RateLimiter = new(TimeSpan.FromSeconds(1), burst: 1, timeProvider: clock)
        };

        var logger = sink.CreateLogger("Test");

        for (var i = 0; i < 5; i++) {
            TestLog.Warning(logger);
        }

        Assert.Equal(1, sink.Count);

        clock.Advance(TimeSpan.FromSeconds(1));
        TestLog.Warning(logger);

        var records = sink.Snapshot();

        Assert.Equal(2, records.Length);
        Assert.Equal(0, records[0].SuppressedCount);
        Assert.Equal(4, records[1].SuppressedCount);
    }

    [Fact]
    public void One_filter_configures_every_sink_that_shares_it() {
        // What a host wants when the editor changes a level: one place, not five sinks that have to
        // be walked and kept in step.
        var filter = new LogFilter { MinimumLevel = LogLevel.Warning };
        var ring = new RingBufferSink(16, filter);
        var output = new StringWriter();
        using var console = new ConsoleSink(output, output, filter: filter);

        filter.SetCategoryLevel("Vixen.Assets", LogLevel.Debug);

        Assert.True(ring.IsEnabled("Vixen.Assets.Loader", LogLevel.Debug));
        Assert.True(console.IsEnabled("Vixen.Assets.Loader", LogLevel.Debug));
        Assert.False(console.IsEnabled("Vixen.Graphics.Device", LogLevel.Information));
        Assert.Same(filter, ring.Filter);
    }

    [Fact]
    public void The_console_writes_an_aligned_line_and_sends_errors_to_standard_error() {
        var output = new StringWriter();
        var error = new StringWriter();
        using var sink = new ConsoleSink(output, error, LogLevel.Trace) {
            UseColour = false,
            ShowTimestamps = false,
            CategoryWidth = 12
        };

        var logger = sink.CreateLogger("Vixen.Graphics.VulkanDevice");
        TestLog.DeviceLost(logger, 42);
        TestLog.Failed(logger, new InvalidOperationException("boom"));

        // Truncated from the front, keeping the tail: "…ulkanDevice" says which logger wrote the
        // line and "Vixen.Graph…" does not.
        Assert.Equal("warn …ulkanDevice Device lost after 42 ms", output.ToString().TrimEnd());

        var failure = error.ToString();
        Assert.Contains("fail", failure, StringComparison.Ordinal);
        Assert.Contains("Something failed", failure, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void The_console_paints_the_level_when_colour_is_on() {
        var output = new StringWriter();
        using var sink = new ConsoleSink(output, output, LogLevel.Trace) {
            UseColour = true,
            ShowTimestamps = false
        };

        TestLog.Warning(sink.CreateLogger("Test"));

        Assert.Contains("\e[33mwarn\e[0m", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_console_appends_the_repeat_count() {
        var clock = new ManualTimeProvider();
        var output = new StringWriter();
        using var sink = new ConsoleSink(output, output, LogLevel.Trace) {
            UseColour = false,
            ShowTimestamps = false,
            RateLimiter = new(TimeSpan.FromSeconds(1), burst: 1, timeProvider: clock)
        };

        var logger = sink.CreateLogger("Test");

        for (var i = 0; i < 4813; i++) {
            TestLog.Warning(logger);
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        TestLog.Warning(logger);

        Assert.Contains("(repeated 4812 times)", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_platform_sink_escapes_what_syslog_would_read_as_a_conversion() {
        // A message that reached syslog(3) as its own format string would print whatever happened to
        // be in the next register, which is a log line that invents its own contents.
        Assert.Equal("100%% of the budget", PlatformSink.EscapeFormatSpecifiers("100% of the budget"));
        Assert.Equal("nothing to escape", PlatformSink.EscapeFormatSpecifiers("nothing to escape"));
    }

    [Fact]
    public void The_platform_sink_writes_to_whatever_this_platform_has() {
        // There is no portable way to read back logcat, the unified log or the debugger's output
        // window, so what is asserted is that the call reaches the operating system and returns:
        // a marshalling mistake in one of the three imports would throw here rather than in a
        // player's hands.
        using var sink = new PlatformSink("VixenTests", LogLevel.Trace);
        var logger = sink.CreateLogger("Vixen.Core.Diagnostics.Tests");

        TestLog.DeviceLost(logger, 42);
        TestLog.Failed(logger, new InvalidOperationException("boom"));

        Assert.Equal("VixenTests", sink.Tag);
        Assert.True(PlatformSink.IsSupported);
    }

    [Fact]
    public void The_event_source_sink_reaches_a_listener() {
        using var listener = new CapturingEventListener();
        using var sink = new EventSourceSink(LogLevel.Trace);

        TestLog.DeviceLost(sink.CreateLogger("Vixen.Graphics.VulkanDevice"), 42);

        var captured = Assert.Single(listener.Events);

        Assert.Equal(4, captured.EventId);
        Assert.Equal(EventLevel.Warning, captured.Level);
        Assert.Equal(2001, Assert.IsType<int>(captured.Payload![0]));
        Assert.Equal("Vixen.Graphics.VulkanDevice", captured.Payload[1]);
        Assert.Equal("Device lost after 42 ms", captured.Payload[2]);
        Assert.Equal("Vixen-Diagnostics-Log", EventSourceSink.ProviderName);
    }

    [Fact]
    public void The_remote_sink_streams_json_lines_without_blocking_the_caller() {
        var transport = new FakeTransport();
        using var sink = new RemoteSink(transport, minimumLevel: LogLevel.Trace);

        TestLog.DeviceLost(sink.CreateLogger("Vixen.Graphics.VulkanDevice"), 42);

        Assert.True(sink.Flush(TimeSpan.FromSeconds(5)), "the queue did not drain");

        var line = Assert.Single(transport.Lines);
        using var document = JsonDocument.Parse(line);

        Assert.Equal("Warning", document.RootElement.GetProperty("level").GetString());
        Assert.Equal(2001, document.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("Vixen.Graphics.VulkanDevice", document.RootElement.GetProperty("category").GetString());
        Assert.Equal("Device lost after 42 ms", document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void The_remote_sink_drops_rather_than_grows_when_nobody_is_attached() {
        var transport = new FakeTransport { IsConnected = false };
        using var sink = new RemoteSink(transport, capacity: 8, minimumLevel: LogLevel.Trace);
        var logger = sink.CreateLogger("Test");

        for (var i = 0; i < 40; i++) {
            TestLog.Line(logger, i);
        }

        Assert.Empty(transport.Lines);
        Assert.True(sink.DroppedCount >= 32, $"dropped {sink.DroppedCount}");
        Assert.True(sink.PendingCount <= 8);

        // And when the inspector does attach, what is left is the most recent lines rather than the
        // forty-line-old beginning of the queue.
        transport.IsConnected = true;
        Assert.True(sink.Flush(TimeSpan.FromSeconds(5)), "the queue did not drain");
        Assert.Contains("line 39", transport.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_sink_writes_json_lines_with_the_fields_still_fields() {
        var directory = Path.Combine(Path.GetTempPath(), $"vixen-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try {
            using (var sink = new ZLoggerFileSink(directory, "test", minimumLevel: LogLevel.Trace)) {
                TestLog.DeviceLost(sink.CreateLogger("Vixen.Graphics.VulkanDevice"), 42);
            }

            var file = Assert.Single(Directory.GetFiles(directory, "test-*.jsonl"));
            var line = Assert.Single(File.ReadAllLines(file));

            using var document = JsonDocument.Parse(line);

            Assert.Equal("Vixen.Graphics.VulkanDevice", document.RootElement.GetProperty("Category").GetString());
            Assert.Equal("Device lost after 42 ms", document.RootElement.GetProperty("Message").GetString());

            // The point of the sink: {Ms} is still a number in a field called Ms, not a fragment of
            // a sentence somebody has to parse back out.
            Assert.Equal(42, document.RootElement.GetProperty("Ms").GetInt32());
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_file_sink_records_a_suppressed_run_as_a_field() {
        var clock = new ManualTimeProvider();
        var directory = Path.Combine(Path.GetTempPath(), $"vixen-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try {
            using (var sink = new ZLoggerFileSink(directory, "test", minimumLevel: LogLevel.Trace) {
                RateLimiter = new(TimeSpan.FromSeconds(1), burst: 1, timeProvider: clock)
            }) {
                var logger = sink.CreateLogger("Test");

                for (var i = 0; i < 5; i++) {
                    TestLog.Warning(logger);
                }

                clock.Advance(TimeSpan.FromSeconds(1));
                TestLog.Warning(logger);
            }

            var file = Assert.Single(Directory.GetFiles(directory, "test-*.jsonl"));
            var lines = File.ReadAllLines(file);

            Assert.Equal(3, lines.Length);

            using var document = JsonDocument.Parse(lines[^1]);
            Assert.Equal(4, document.RootElement.GetProperty("SuppressedCount").GetInt32());
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A clock a test can move, so that a one-second window does not take a second.</summary>
    sealed class ManualTimeProvider : TimeProvider {
        long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan amount) => timestamp += amount.Ticks;
    }

    sealed class FakeTransport : IRemoteLogTransport {
        readonly Lock gate = new();
        readonly List<string> lines = [];

        public bool IsConnected { get; set; } = true;

        public IReadOnlyList<string> Lines {
            get {
                lock (gate) {
                    return [.. lines];
                }
            }
        }

        public void Send(ReadOnlySpan<byte> payload) {
            var text = Encoding.UTF8.GetString(payload);

            lock (gate) {
                lines.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
        }
    }

    sealed class CapturingEventListener : EventListener {
        readonly Lock gate = new();
        readonly List<EventWrittenEventArgs> events = [];

        public IReadOnlyList<EventWrittenEventArgs> Events {
            get {
                lock (gate) {
                    return [.. events];
                }
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource) {
            if (string.Equals(eventSource.Name, EventSourceSink.ProviderName, StringComparison.Ordinal)) {
                EnableEvents(eventSource, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData) {
            lock (gate) {
                events.Add(eventData);
            }
        }
    }
}
