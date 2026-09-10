// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Vixen.Core.Diagnostics;
using Xunit;

namespace Vixen.Editor.Profiler.Tests;

/// <summary>
///     The editor's half of doc 13 § Trace export — "<c>vixen trace record --duration 10s</c> from
///     the CLI, or the editor's capture button".
/// </summary>
/// <remarks>
///     <para>
///         <b>What is under test is the join, not the exporter.</b> <c>TraceExporter</c> has its own
///         tests and the CLI verb has its own; what had no caller at all was the path from a capture
///         sitting in the panel to a file on disk —
///         <a href="https://github.com/Rikarin/Vixen/issues/346">#346</a>. So these assert over the
///         document's events rather than over a return value: a join that wrote a well-formed file
///         with nobody's samples in it is exactly the failure that would otherwise pass.
///     </para>
///     <para>
///         ⚠ <b>A <see cref="BufferedProfileSource" />, never <c>LocalProfileSource</c>.</b> That one
///         reads the process's static rings, which every other test in this run is also writing into,
///         so the event count would depend on what xunit scheduled beside it.
///     </para>
/// </remarks>
public sealed class TraceExportTests : IDisposable {
    static readonly ProfilingKey Update = ProfilingKey.Register("Export.Update");
    static readonly ProfilingKey Draw = ProfilingKey.Register("Export.Draw");

    /// <summary>A directory of this test's own, removed whatever the test did.</summary>
    readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "vixen-trace-export-" + Guid.NewGuid().ToString("N")
    );

    /// <summary>The moment every name in this file is built from, so no assertion reads a clock.</summary>
    static readonly DateTimeOffset When = new(2026, 9, 10, 14, 3, 27, TimeSpan.Zero);

    /// <inheritdoc />
    public void Dispose() {
        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }
    }

    static ProfilerModel Recorded(string source = "Editor") {
        ProfilerModel model = new();
        BufferedProfileSource buffered = new(source);

        model.Add(buffered);
        model.Start();

        buffered.Offer(new(7, "Main", [new(Update, 0, 100, 40, 3), new(Draw, 1, 110, 20, 3)]));
        model.Stop();

        return model;
    }

    /// <summary>A capture reaches the file as its own scopes, on its own thread.</summary>
    [Fact]
    public void AnExportedCaptureCarriesTheScopesItRecorded() {
        var path = Recorded().ExportTrace(directory, When);

        Assert.NotNull(path);
        Assert.True(File.Exists(path), $"'{path}' was returned and is not there.");

        using var document = JsonDocument.Parse(File.ReadAllText(path!));
        var events = document.RootElement.GetProperty("traceEvents").EnumerateArray().ToArray();

        var complete = events
            .Where(entry => entry.GetProperty("ph").GetString() == "X")
            .Select(entry => entry.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["Export.Update", "Export.Draw"], complete);

        // The thread's *name*, which is the metadata record the exporter writes first — without it
        // a viewer labels the track with a bare number, and a join that lost the thread id would
        // still have produced the two events above.
        var named = Assert.Single(events, entry => entry.GetProperty("ph").GetString() == "M");

        Assert.Equal("Main", named.GetProperty("args").GetProperty("name").GetString());
        Assert.Equal(7, named.GetProperty("tid").GetInt32());
    }

    /// <summary>
    ///     ⚠ An empty capture is refused rather than written, and this is the assertion that matters
    ///     most.
    /// </summary>
    /// <remarks>
    ///     <c>TraceExporter</c> over no threads writes a well-formed document with an empty
    ///     <c>traceEvents</c> array, which opens in the viewer and reads as a process that did
    ///     nothing at all. Handing somebody the path to one of those is worse than telling them there
    ///     was nothing to export — the same reason the CLI verb refuses a zero duration.
    /// </remarks>
    [Fact]
    public void ExportingWithNothingCapturedWritesNoFile() {
        ProfilerModel model = new();
        model.Add(new BufferedProfileSource("Editor"));

        Assert.Null(model.ExportTrace(directory, When));
        Assert.False(Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any());
    }

    /// <summary>Two exports in one session are two files.</summary>
    /// <remarks>
    ///     ⚠ The name carries the moment for this reason alone. A fixed name — or a name built from
    ///     the source only — means the second press silently replaces the capture somebody took
    ///     before making the change they are measuring, which is the one file the comparison needed.
    /// </remarks>
    [Fact]
    public void TwoExportsDoNotOverwriteEachOther() {
        var model = Recorded();

        var first = model.ExportTrace(directory, When);
        var second = model.ExportTrace(directory, When.AddSeconds(1));

        Assert.NotEqual(first, second);
        Assert.Equal(2, Directory.EnumerateFiles(directory).Count());
    }

    /// <summary>The file is named for the source and the moment, in the CLI verb's own shape.</summary>
    [Theory]
    [InlineData("Editor", "Editor-20260910-140327.json")]
    [InlineData("Game", "Game-20260910-140327.json")]

    // ⚠ A source is named by whoever added it, and an attached device announces its own name. A
    // slash in it would be a directory that does not exist rather than a file name.
    [InlineData("Pixel 8 / usb", "Pixel-8---usb-20260910-140327.json")]
    [InlineData("///", "capture-20260910-140327.json")]
    public void TheFileIsNamedForItsSourceAndItsMoment(string source, string expected) =>
        Assert.Equal(expected, ProfilerModel.TraceFileName(source, When));

    /// <summary>The directory is created rather than required to exist.</summary>
    /// <remarks>
    ///     A project has no <c>Traces</c> folder until the first recording, and an editor that
    ///     answered the first press with "directory not found" would be one where the button never
    ///     works on a fresh checkout.
    /// </remarks>
    [Fact]
    public void ExportingCreatesTheFolderItWritesInto() {
        var nested = Path.Combine(directory, "Traces");

        Assert.False(Directory.Exists(nested));
        Assert.NotNull(Recorded().ExportTrace(nested, When));
        Assert.True(Directory.Exists(nested));
    }
}
