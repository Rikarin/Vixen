// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Platform.Headless;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     <c>--vixen-profile</c> and <c>--vixen-trace</c>: the switch that turns the CPU profiler on in a
///     hosted game, and the file it writes when the game stops.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What was missing was never the exporter.</b> <c>TraceExporter</c> has been finished
///         since it was written and was constructed by nothing outside its own tests;
///         <c>Profiler.IsEnabled</c> defaults to false and no host set it; and
///         <c>Profiler.BeginFrame</c> was called by the editor host and by nobody else, so a game's
///         samples were all attributed to frame zero. Three joins, one flag.
///     </para>
///     <para>
///         ⚠ <b>The scopes are named with a GUID.</b> The profiler is process-wide and
///         <c>Profiler.Collect</c> drains every thread's ring, so a parallel test's samples land in
///         this trace and this test's collection empties theirs. Every assertion below is about
///         events carrying this run's own key name.
///     </para>
/// </remarks>
public sealed class TraceExportTests : IDisposable {
    readonly TemporaryFileSystemHost files = new();
    readonly string directory = Path.Combine(Path.GetTempPath(), $"vixen-trace-{Guid.NewGuid():N}");

    public TraceExportTests() => Directory.CreateDirectory(directory);

    public void Dispose() {
        // ⚠ Put back, always. It is a static on the profiler, so a test that left it on would change
        // what every other test in this assembly costs and what it records.
        Profiler.IsEnabled = false;
        files.Dispose();

        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A game that opens one profiler scope per update, so a trace has something in it.</summary>
    sealed class SampledGame(ProfilingKey key) : Game {
        protected internal override void OnConfigure(AppConfig config) {
            config.Name = "Trace";
            config.Window = null;
            config.Graphics.Enabled = false;
            config.UseEngine = false;
        }

        protected internal override void OnUpdate(GameTime time) {
            using (Profiler.Begin(key)) {
                // Work enough that the sample has a duration the exporter will not round to nothing.
                Thread.SpinWait(64);
            }
        }

        protected internal override void OnRender(GameTime time) { }
    }

    VixenApplication Build(Game game, params string[] extra) =>
        VixenApp
            .Create([
                "--vixen-variant", "Debug", "--vixen-workers", "1", "--vixen-frame-limit", "0", .. extra
            ])
            .WithPlatform(new HeadlessPlatform(new() { FileSystem = files }))
            .Build(game);

    [Fact]
    public void TheProfilerIsOffUntilAFlagAsksForIt() {
        Profiler.IsEnabled = false;

        using var application = Build(new SampledGame(ProfilingKey.Register("Trace.Unasked")));
        application.Initialise();
        application.RunFrame();

        // The whole point of the flag: a host that was not asked leaves the instrumentation compiled
        // in and skipped, which is what makes leaving it compiled in affordable.
        Assert.False(Profiler.IsEnabled);
        Assert.Null(application.Services.Config.TracePath);
    }

    [Fact]
    public void ATracePathTurnsTheProfilerOnAndWritesWhatTheFramesRecorded() {
        Profiler.IsEnabled = false;

        var name = $"Trace.Scope.{Guid.NewGuid():N}";
        var key = ProfilingKey.Register(name);
        var path = Path.Combine(directory, "run.json");

        using (var application = Build(new SampledGame(key), "--vixen-trace", path)) {
            application.Initialise();

            // ⚠ Implied, not separately given. A path with the profiler off writes a well-formed
            // document with no events in it, which reads as "the frame did nothing".
            Assert.True(Profiler.IsEnabled);
            Assert.True(application.Services.Config.Profiling);

            for (var frame = 0; frame < 4; frame++) {
                application.RunFrame();
            }

            // Nothing is written until the run ends: the trace is the run, not a frame of it.
            Assert.False(File.Exists(path));

            application.Shutdown();
        }

        Assert.True(File.Exists(path), "the trace was not written");

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var mine = document.RootElement
            .GetProperty("traceEvents")
            .EnumerateArray()
            .Where(entry => entry.TryGetProperty("name", out var eventName)
                            && string.Equals(eventName.GetString(), name, StringComparison.Ordinal))
            .ToList();

        // Four updates, four scopes. The count is part of the assertion: an empty filter would make
        // every claim below hold over nothing.
        Assert.Equal(4, mine.Count);

        foreach (var entry in mine) {
            Assert.Equal("X", entry.GetProperty("ph").GetString());
            Assert.True(entry.GetProperty("dur").GetDouble() >= 0d);
        }

        // ⚠ The assertion that catches the missing `BeginFrame`. Without it every sample carries
        // frame zero — a trace that opens, looks complete, and attributes a whole run to one frame.
        var frames = mine.Select(entry => entry.GetProperty("args").GetProperty("frame").GetInt32())
            .Distinct()
            .ToList();

        Assert.Equal(4, frames.Count);
    }

    [Fact]
    public void AnUnwritableTracePathIsAWarningRatherThanAFailedShutdown() {
        Profiler.IsEnabled = false;

        // A directory where the file should be: the write fails, and the run still ends cleanly.
        var path = Path.Combine(directory, "occupied");
        Directory.CreateDirectory(path);

        using var application = Build(new SampledGame(ProfilingKey.Register("Trace.Refused")), "--vixen-trace", path);

        application.Initialise();
        application.RunFrame();

        // The record of it is log event 13037; what this asserts is that shutting down is not the
        // thing that fails, because everything the run was for has already happened.
        application.Shutdown();

        Assert.False(File.Exists(Path.Combine(path, "run.json")));
    }
}
