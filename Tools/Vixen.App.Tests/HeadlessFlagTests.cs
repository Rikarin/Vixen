// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>What <c>--vixen-headless</c> reaches, and the two ways a head takes it away again.</summary>
/// <remarks>
///     <para>
///         <b>The flag has two consumers and they fail differently.</b>
///         <see cref="PlatformHost.Create" /> reads <see cref="AppConfig.Headless" /> and is what
///         decides whether SDL is touched at all; <c>Game.OnConfigure</c> reads the same field to
///         decide whether the window it asks for is visible. The first is the one that matters, and
///         it is defeated silently — by <see cref="AppBuilder.WithPlatform" />, which takes
///         precedence over the factory on purpose, so a head that supplies its own platform never
///         asks the question. Four samples did exactly that, and one of them
///         (<c>Samples/12</c>) logged <c>on Desktop (macOS)</c> on a run whose command line said
///         <c>--vixen-headless</c>.
///     </para>
///     <para>
///         ⚠ <b>The second is <em>not</em> what opens a window, and a reader who fixes only that
///         will believe the bug is gone.</b> Under the headless platform a
///         <see cref="HeadlessWindow" /> has no picture whatever <c>WindowOptions.IsVisible</c> says;
///         the only consequence is a synthetic <see cref="PlatformEventKind.WindowShown" /> that
///         nothing consumes. Both are pinned here so the difference between them stays written down.
///     </para>
/// </remarks>
public sealed class HeadlessFlagTests {
    /// <summary>The checkout, found by walking up to the solution file.</summary>
    static string Root {
        get {
            var directory = AppContext.BaseDirectory;

            while (directory is not null && !File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                directory = Path.GetDirectoryName(directory);
            }

            return directory ?? throw new InvalidOperationException("no Vixen.slnx above the test binary");
        }
    }

    /// <summary>Every <c>.cs</c> file under <c>Samples/</c>, with its comment lines removed.</summary>
    /// <remarks>
    ///     ⚠ <b>The comments have to go, and finding that out is most of why this helper exists.</b>
    ///     The samples now carry paragraphs explaining what an unconditional <c>IsVisible = true</c>
    ///     and a hand-built <c>DesktopPlatform</c> cost — so a scan over the raw text would fail on
    ///     the documentation of the very thing it is checking has gone.
    /// </remarks>
    static IEnumerable<(string Path, string Code)> SampleSources() {
        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(Root, "Samples"), "*.cs", SearchOption.AllDirectories)) {
            var code = string.Join(
                '\n',
                File.ReadLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            );

            yield return (Path.GetRelativePath(Root, file), code);
        }
    }

    /// <summary>
    ///     <c>OnConfigure</c> can see the flag, which is what makes
    ///     <c>IsVisible = !config.Headless</c> a sentence rather than a constant.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The sabotage this catches is an ordering change.</b>
    ///     <see cref="AppConfig.Apply" /> runs before <c>OnConfigure</c> deliberately — a game that
    ///     hard-codes something means it — and moving it after would leave every
    ///     <c>!config.Headless</c> in the tree reading <see langword="false" /> and every sample
    ///     window visible again, with no compiler error and no failing assertion anywhere else.
    /// </remarks>
    [Fact]
    public void TheFlagHasBeenAppliedBeforeTheGameIsConfigured() {
        var game = new Watcher();

        using var application = VixenApp.Create(["--vixen-headless"]).Build(game);

        Assert.True(game.SawHeadless);
        Assert.False(game.SawVisible);
    }

    /// <summary>And the other side of it, so the assertion above is not true by construction.</summary>
    /// <remarks>
    ///     A platform is handed in rather than left to the factory, because the point being made is
    ///     about what the <em>game</em> saw and building this case through
    ///     <see cref="PlatformHost" /> would be a windowed run on any machine with a display server.
    /// </remarks>
    [Fact]
    public void WithoutTheFlagTheGameSeesAWindowItMayShow() {
        var game = new Watcher();
        using var platform = new HeadlessPlatform(new() { Organisation = "Vixen", Application = "Test" });

        using var application = new AppBuilder(AppArguments.Parse([]))
            .WithPlatform(platform)
            .Build(game);

        Assert.False(game.SawHeadless);
        Assert.True(game.SawVisible);
    }

    /// <summary>A game may still say it outright, with no flag on the command line.</summary>
    /// <remarks>
    ///     Only this direction is asserted. The opposite — a game clearing
    ///     <see cref="AppConfig.Headless" /> that the operator set — is legitimate too and is what
    ///     the ordering exists for, but exercising it here would open a real window on the test
    ///     machine.
    /// </remarks>
    [Fact]
    public void AGameThatSaysHeadlessGetsItWithNoFlag() {
        using var application = VixenApp.Create([]).Build(new AlwaysHeadless());

        Assert.Equal("Headless", application.Services.Platform.Name);
    }

    /// <summary>
    ///     ⚠ A supplied platform is never asked about the flag — the trap the samples fell into.
    /// </summary>
    /// <remarks>
    ///     Pinned as deliberate rather than fixed. An editor's play mode, an Android activity and a
    ///     UIKit view controller all hand over a platform the operating system owns, and a builder
    ///     that second-guessed them from a command-line flag would be unusable on a phone. What was
    ///     wrong was four desktop samples doing it for a reason that had expired —
    ///     <c>DesktopPlatformOptions.RequestGpuSurface</c> defaults to <see langword="true" />, so
    ///     the hand-built platform was identical to the one the factory makes.
    /// </remarks>
    [Fact]
    public void ASuppliedPlatformBeatsTheFactoryEvenWhenTheFlagIsGiven() {
        var factory = new CountingFactory();
        using var platform = new HeadlessPlatform(new() { Organisation = "Vixen", Application = "Test" });

        using var application = new AppBuilder(AppArguments.Parse(["--vixen-headless"]))
            .WithPlatformFactory(factory)
            .WithPlatform(platform)
            .Build(new Watcher());

        Assert.Equal(0, factory.Asked);
        Assert.Same(platform, application.Services.Platform);
    }

    /// <summary>
    ///     No sample builds a desktop platform for itself, because that is how the flag was lost.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A source scan rather than a behavioural test, and it has to be: the failure is that
    ///         <see cref="AppConfig.Headless" /> is never <em>read</em>, which no run of the host can
    ///         observe from the inside. Reproducing it properly means starting a sample process on a
    ///         machine with a display server and watching a window appear, which is the thing this
    ///         whole task exists to stop happening.
    ///     </para>
    ///     <para>
    ///         Scoped to files that also go through <see cref="VixenApp" />. The Android and iOS
    ///         heads pass a platform their operating system owns and have no factory to fall back to;
    ///         <c>Samples/02</c> builds a platform and a window with no <see cref="AppConfig" />
    ///         anywhere, so there is no flag there to honour and nothing here to say about it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void NoSampleTakesThePlatformChoiceAwayFromTheHost() {
        var offenders = new List<string>();

        foreach (var (path, code) in SampleSources()) {
            if (code.Contains("VixenApp.", StringComparison.Ordinal)
                && code.Contains("new DesktopPlatform(", StringComparison.Ordinal)) {
                offenders.Add(path);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A sample that goes through VixenApp must let PlatformHost choose the platform, because "
            + "a platform handed to WithPlatform is taken ahead of the factory and --vixen-headless "
            + "is then never consulted. RequestGpuSurface already defaults to true, so there is "
            + $"nothing to gain by building one: {string.Join(", ", offenders)}"
        );
    }

    /// <summary>The sample corpus is really there, so the two scans above mean something.</summary>
    /// <remarks>
    ///     ⚠ <b>Both scans assert an <em>absence</em>.</b> They collect offenders and require the
    ///     list to be empty, which is exactly what an empty corpus produces: a corpus that stopped
    ///     arriving, or a <c>VixenApp.</c> spelling that drifted, would leave both of them green
    ///     having read nothing at all. The floor is deliberately loose — this pins that samples are
    ///     found and that some of them really do go through <see cref="VixenApp" />, not how many
    ///     there happen to be today.
    /// </remarks>
    [Fact]
    public void TheSamplesBeingScannedAreActuallyThere() {
        var sources = SampleSources().ToList();

        Assert.True(sources.Count >= 10, $"Only {sources.Count} sample .cs files were found under Samples/.");

        var throughVixenApp = sources.Count(source => source.Code.Contains("VixenApp.", StringComparison.Ordinal));

        Assert.True(
            throughVixenApp > 0,
            "No sample mentions VixenApp., so NoSampleTakesThePlatformChoiceAwayFromTheHost scanned "
            + "a corpus in which its subject cannot appear."
        );
    }

    /// <summary>And no sample assigns over the flag in <c>OnConfigure</c> either.</summary>
    /// <remarks>
    ///     The lesser half, and worth a gate of its own because it is the half that <em>looks</em>
    ///     like the bug. <c>IsVisible = true</c> beside a <c>config.Headless</c> the host filled in a
    ///     moment earlier is an override written by accident, even where the platform underneath it
    ///     makes the override inert.
    /// </remarks>
    [Fact]
    public void NoSampleAssignsAVisibleWindowOverTheFlag() {
        var offenders = new List<string>();

        foreach (var (path, code) in SampleSources()) {
            if (!code.Contains("OnConfigure(AppConfig", StringComparison.Ordinal)) {
                continue;
            }

            if (Regex.IsMatch(code, @"IsVisible\s*=\s*true", RegexOptions.None, TimeSpan.FromSeconds(5))) {
                offenders.Add(path);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A game's OnConfigure runs after AppConfig.Apply, so an unconditional IsVisible = true "
            + "is an assignment over whatever --vixen-headless asked for. Write "
            + $"IsVisible = !config.Headless: {string.Join(", ", offenders)}"
        );
    }

    /// <summary>A game that records what the host had already decided by the time it was asked.</summary>
    sealed class Watcher : Game {
        public bool SawHeadless { get; private set; }

        public bool SawVisible { get; private set; }

        protected internal override void OnConfigure(AppConfig config) {
            SawHeadless = config.Headless;

            config.Name = "Headless flag test";
            config.UseEngine = false;
            config.Graphics.Enabled = false;
            config.Window = new() { IsVisible = !config.Headless };

            SawVisible = config.Window.Value.IsVisible;
        }
    }

    /// <summary>A game that is always a server, which is a thing a game is allowed to be.</summary>
    sealed class AlwaysHeadless : Game {
        protected internal override void OnConfigure(AppConfig config) {
            config.Name = "Headless flag test";
            config.UseEngine = false;
            config.Graphics.Enabled = false;
            config.Window = null;
            config.Headless = true;
        }
    }

    /// <summary>A factory that reports whether it was consulted at all.</summary>
    sealed class CountingFactory : IPlatformFactory {
        public int Asked { get; private set; }

        public IPlatform Create(AppConfig config) {
            Asked++;

            return new HeadlessPlatform(new() { Organisation = "Vixen", Application = "Test" });
        }
    }
}
