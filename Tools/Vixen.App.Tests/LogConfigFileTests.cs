// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;
using Vixen.Core.IO;
using Vixen.Platform.Headless;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     <c>vixen.log.yaml</c>, which is the half of doc 13's per-category rule that was missing.
/// </summary>
/// <remarks>
///     ⚠ <b>The oracle is <see cref="LogFilter.IsEnabled" /> and not the rule count</b>, because a
///     count is what a reader that parsed the file and applied it to the wrong filter would also
///     report. Every assertion here asks whether a record in a named category would be kept — which
///     is the only question the file exists to answer, and the one a filter nobody fed answers
///     wrongly.
/// </remarks>
public sealed class LogConfigFileTests {
    /// <summary>The shape a project would commit, and what each line has to do.</summary>
    [Fact]
    public void CategoryRulesFromTheFileDecideWhatIsKept() {
        var filter = new LogFilter { MinimumLevel = LogLevel.Information };

        var rules = LogConfigFile.Apply(
            """
            minimumLevel: Warning
            categories:
              Vixen.Assets: Trace
              Vixen.Graphics: Error
            """,
            filter,
            honourMinimumLevel: true
        );

        Assert.Equal(2, rules);
        Assert.Equal(LogLevel.Warning, filter.MinimumLevel);

        // The whole point of the feature, in one pair: verbose asset loading, no render spam.
        Assert.True(filter.IsEnabled("Vixen.Assets.Catalog", LogLevel.Trace));
        Assert.False(filter.IsEnabled("Vixen.Graphics.Vulkan", LogLevel.Warning));

        // And a category nobody named still obeys the global level.
        Assert.False(filter.IsEnabled("Vixen.App", LogLevel.Information));
        Assert.True(filter.IsEnabled("Vixen.App", LogLevel.Warning));
    }

    /// <summary>
    ///     ⚠ The flag wins over the file's <c>minimumLevel</c>, and the file's category rules apply
    ///     anyway — they are two decisions, not one, because the command line has no per-category
    ///     form to disagree with.
    /// </summary>
    [Fact]
    public void TheCommandLineLevelSurvivesTheFileButTheCategoryRulesStillLand() {
        var filter = new LogFilter { MinimumLevel = LogLevel.Debug };

        var rules = LogConfigFile.Apply(
            """
            minimumLevel: Critical
            categories:
              Vixen.Assets: Trace
            """,
            filter,
            honourMinimumLevel: false
        );

        Assert.Equal(1, rules);
        Assert.Equal(LogLevel.Debug, filter.MinimumLevel);
        Assert.True(filter.IsEnabled("Vixen.Assets", LogLevel.Trace));
    }

    /// <summary>A file a project has created and not yet filled in is not an error.</summary>
    [Fact]
    public void AnEmptyFileChangesNothing() {
        var filter = new LogFilter { MinimumLevel = LogLevel.Information };

        Assert.Equal(0, LogConfigFile.Apply(string.Empty, filter, honourMinimumLevel: true));
        Assert.Equal(LogLevel.Information, filter.MinimumLevel);
        Assert.Equal(0, filter.CategoryRuleCount);
    }

    /// <summary>
    ///     A mistyped level names itself. "The log config is broken" is not something anybody can
    ///     act on, and this is the one file whose whole job is to be edited by hand.
    /// </summary>
    [Fact]
    public void AMistypedLevelNamesTheKeyAndTheValue() {
        var filter = new LogFilter();

        var failure = Assert.Throws<FormatException>(
            () => LogConfigFile.Apply(
                """
                categories:
                  Vixen.Assets: Chatty
                """,
                filter,
                honourMinimumLevel: true
            )
        );

        Assert.Contains("Vixen.Assets", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Chatty", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Trace", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A document of the wrong shape says what shape it is instead.</summary>
    [Fact]
    public void AListWhereAMappingWasExpectedIsRefused() {
        var failure = Assert.Throws<FormatException>(
            () => LogConfigFile.Apply("- Vixen.Assets\n- Vixen.Graphics\n", new LogFilter(), true)
        );

        Assert.Contains("list", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Both locations are read, and the machine's beats the shipped one where they disagree.
    /// </summary>
    [Fact]
    public void TheMachinesFileWinsOverTheShippedOne() {
        var filter = new LogFilter { MinimumLevel = LogLevel.Information };
        var files = new VirtualFileSystem();
        var shipped = new MemoryFileProvider();
        var machine = new MemoryFileProvider();

        shipped.Seed(new("/vixen.log.yaml"), "categories:\n  Vixen.Assets: Error\n  Vixen.Ui: Trace\n");
        machine.Seed(new("/vixen.log.yaml"), "categories:\n  Vixen.Assets: Trace\n");

        files.Mount(MountPoints.App, shipped);
        files.Mount(MountPoints.Data, machine);

        var log = new RecordingLogger();

        LogConfigFile.ApplyStandardLocations(files, filter, honourMinimumLevel: true, log);

        Assert.True(filter.IsEnabled("Vixen.Assets", LogLevel.Trace));

        // The shipped file's other rule is still in force: the second file overrides the prefixes it
        // names and does not replace the first wholesale.
        Assert.True(filter.IsEnabled("Vixen.Ui", LogLevel.Trace));

        // ⚠ Both reads are reported. A configuration file nobody read is otherwise
        // indistinguishable from one that was read and agreed with the defaults.
        Assert.Equal(2, log.Records.Count);
        Assert.Contains(log.Records, record => record.Contains("/app/", StringComparison.Ordinal));
        Assert.Contains(log.Records, record => record.Contains("/data/", StringComparison.Ordinal));
    }

    /// <summary>A run with no file at all logs nothing and changes nothing.</summary>
    [Fact]
    public void NoFileIsSilentAndHarmless() {
        var filter = new LogFilter { MinimumLevel = LogLevel.Information };
        var files = new VirtualFileSystem();
        var log = new RecordingLogger();

        files.Mount(MountPoints.App, new MemoryFileProvider());

        LogConfigFile.ApplyStandardLocations(files, filter, honourMinimumLevel: true, log);

        Assert.Empty(log.Records);
        Assert.Equal(0, filter.CategoryRuleCount);
        Assert.Equal(LogLevel.Information, filter.MinimumLevel);
    }

    /// <summary>
    ///     A broken file is a warning and the application still boots — with the levels it would
    ///     have had, rather than half of the file's.
    /// </summary>
    [Fact]
    public void ABrokenFileWarnsAndTheHostCarriesOn() {
        var filter = new LogFilter { MinimumLevel = LogLevel.Information };
        var files = new VirtualFileSystem();
        var provider = new MemoryFileProvider();

        provider.Seed(new("/vixen.log.yaml"), "categories:\n  Vixen.Assets: Chatty\n");
        files.Mount(MountPoints.App, provider);

        var log = new RecordingLogger();

        LogConfigFile.ApplyStandardLocations(files, filter, honourMinimumLevel: true, log);

        var record = Assert.Single(log.Records);

        Assert.Contains("Warning", record, StringComparison.Ordinal);
        Assert.Contains("Chatty", record, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The boot path calls it.</b> This is the assertion the rest of the file cannot make:
    ///     a reader that parses perfectly and is wired to nothing is this repository's commonest
    ///     defect, and every test above would pass on the day <c>AppBuilder</c> stopped calling it.
    ///     So this one goes through <c>VixenApp.Create(…).Build(game)</c> and asks the filter the
    ///     running application's sinks actually share.
    /// </summary>
    [Fact]
    public void TheHostReadsTheFileWhileBooting() {
        using var files = new TemporaryFileSystemHost();

        File.WriteAllText(
            Path.Combine(files.ApplicationDirectory, LogConfigFile.FileName),
            "categories:\n  Vixen.Assets: Trace\n"
        );

        using var application = VixenApp
            .Create(["--vixen-variant", "Debug", "--vixen-workers", "1", "--vixen-frame-limit", "0"])
            .WithPlatform(new HeadlessPlatform(new() { FileSystem = files }))
            .Build(new SilentGame());

        var filter = application.Services.Logs.Filter;

        Assert.True(
            filter.IsEnabled("Vixen.Assets.Catalog", LogLevel.Trace),
            "the host booted without reading vixen.log.yaml, so the file is a finished thing nothing calls."
        );

        // And the default is untouched where the file said nothing, which is what makes the line
        // above a statement about this rule rather than about the level having moved wholesale.
        Assert.False(filter.IsEnabled("Vixen.Graphics", LogLevel.Trace));
    }

    /// <summary>A game that does nothing, so the boot path is all that is under test.</summary>
    sealed class SilentGame : Game;

    /// <summary>Keeps every line it is given, so a branch that says nothing is visible.</summary>
    sealed class RecordingLogger : ILogger {
        public List<string> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) =>
            Records.Add($"{logLevel}: {formatter(state, exception)}");
    }
}
