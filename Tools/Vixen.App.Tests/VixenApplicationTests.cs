// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Xunit;

namespace Vixen.App.Tests;

public sealed class VixenApplicationTests : IDisposable {
    readonly TemporaryFileSystemHost files = new();

    /// <summary>
    ///     The boot sequence, in the order it is documented to happen. Getting it wrong is the class
    ///     of bug where a game's <c>OnConfigure</c> reads a window that does not exist yet, or its
    ///     <c>OnInitialise</c> configures one that has already been created.
    /// </summary>
    [Fact]
    public void TheHooksRunInTheOrderTheDocumentationPromises() {
        var game = new RecordingGame();
        using var application = Build(game);

        application.Initialise();
        application.RunFrame();
        application.Shutdown();

        Assert.Equal(
            ["configure", "initialise", "update", "render", "shutdown"],
            game.Calls
        );
    }

    /// <summary>
    ///     <c>OnConfigure</c> runs before the platform exists — that is what it is for — so the
    ///     services must not be reachable from it, and the message has to say why rather than
    ///     throwing a null reference.
    /// </summary>
    [Fact]
    public void ServicesAreNotReachableBeforeTheyExist() {
        var game = new EarlyServiceGame();

        var thrown = Assert.Throws<InvalidOperationException>(() => Build(game));
        Assert.Contains("not available until OnInitialise", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServicesAreWiredAndAlsoInTheRegistry() {
        var game = new RecordingGame();
        using var application = Build(game);

        var services = application.Services;

        Assert.NotNull(services.Platform);
        Assert.NotNull(services.Window);
        Assert.NotNull(services.Jobs);
        Assert.NotNull(services.FileSystem);
        Assert.Same(services.Platform, services.Registry.Get<IPlatform>());
        Assert.Same(services.Window, services.Registry.Get<IWindow>());
    }

    /// <summary>
    ///     The platform's locations are mounted before the first frame, so nothing above ever sees a
    ///     native path.
    /// </summary>
    [Fact]
    public void TheStandardMountsExistBeforeTheGameRuns() {
        using var application = Build(new RecordingGame());

        Assert.True(application.Services.FileSystem.TryResolve(MountPoints.Data / "save.bin", out _, out _));
        Assert.True(application.Services.FileSystem.TryResolve(MountPoints.Cache / "shaders.bin", out _, out _));
    }

    [Fact]
    public void TheWindowIsShownAndTakesTheApplicationsName() {
        using var application = Build(new RecordingGame());
        application.Initialise();

        Assert.True(application.Services.Window!.IsVisible);
        Assert.Equal("Test", application.Services.Window.Title);
    }

    /// <summary>
    ///     A batch tool wants no window at all, which is not the same as a headless platform: the
    ///     headless platform still creates windows, because that is what keeps the frame loop one
    ///     shape.
    /// </summary>
    [Fact]
    public void AHeadWithNoWindowGetsNone() {
        using var application = Build(new NoWindowGame());

        Assert.Null(application.Services.Window);
        Assert.Empty(application.Services.Platform.Windows);
    }

    [Fact]
    public void TimeAdvancesAndFramesAreCounted() {
        using var application = Build(new RecordingGame());
        application.Initialise();

        for (var frame = 0; frame < 3; frame++) {
            application.RunFrame();
        }

        Assert.Equal(3, application.Time.FrameCount);
        Assert.True(application.Time.Total > TimeSpan.Zero);
    }

    /// <summary>
    ///     A quit is the platform's to raise and the host's to obey. Both routes have to work,
    ///     because one is the OS and the other is the game's own menu.
    /// </summary>
    [Fact]
    public void AQuitFromThePlatformStopsTheLoop() {
        var platform = NewPlatform();
        using var application = Build(new RecordingGame(), platform);
        application.Initialise();

        platform.Lifecycle.RequestQuit();
        application.RunFrame();

        Assert.True(application.IsStopping);
    }

    [Fact]
    public void StoppingItDirectlyAlsoStopsTheLoop() {
        using var application = Build(new RecordingGame());
        application.Initialise();

        application.Stop();

        Assert.True(application.IsStopping);
    }

    /// <summary>
    ///     Closing the window is what ends a normal application, and it must reach the loop through
    ///     the same event path a real window manager uses rather than a special case.
    /// </summary>
    [Fact]
    public void ClosingTheLastWindowEndsTheApplication() {
        var platform = NewPlatform();
        using var application = Build(new RecordingGame(), platform);
        application.Initialise();

        ((HeadlessWindow)application.Services.Window!).RequestClose();
        application.RunFrame();

        Assert.True(application.IsStopping);

        // Closed, not absent: a platform drops disposed windows at the start of the *next* pump, so
        // that an application enumerating the list inside its own event handling sees one that does
        // not change under it. The host has to notice by state rather than by count, and this is the
        // assertion that says which of the two it did.
        Assert.True(application.Services.Window!.IsClosed);
        Assert.All(platform.Windows, window => Assert.True(window.IsClosed));
    }

    /// <summary>
    ///     And that frame simulates and draws nothing, because by the time it would there is nowhere
    ///     to draw: the window was disposed inside this same frame's <c>PumpEvents</c>, and on macOS
    ///     disposing one destroys the Metal view whose layer the swapchain's surface was made from.
    ///     Rendering afterwards presents to freed memory — which does not reliably fault, and so
    ///     would be found much later and somewhere else.
    /// </summary>
    [Fact]
    public void TheFrameThatClosesTheLastWindowSimulatesAndDrawsNothing() {
        var platform = NewPlatform();
        var game = new RecordingGame();
        using var application = Build(game, platform);
        application.Initialise();

        ((HeadlessWindow)application.Services.Window!).RequestClose();
        application.RunFrame();

        Assert.True(application.IsStopping);
        Assert.Equal(["configure", "initialise"], game.Calls);
    }

    /// <summary>
    ///     It does still drain, though, and the order is the point rather than an accident: work an
    ///     event handler posted on the way out — saving settings, releasing a lock — is exactly the
    ///     work that has no later frame to run on.
    /// </summary>
    [Fact]
    public void WorkPostedOnTheWayOutStillRuns() {
        var platform = NewPlatform();
        var ran = false;
        using var application = Build(new RecordingGame(), platform);
        application.Initialise();

        ((HeadlessWindow)application.Services.Window!).RequestClose();
        application.Services.MainThread.Post(() => ran = true);
        application.RunFrame();

        Assert.True(application.IsStopping);
        Assert.True(ran);
    }

    /// <summary>
    ///     And nothing is lost at the other end. A game that stops itself from <c>OnUpdate</c> still
    ///     gets that frame's <c>OnRender</c> — the check is asked before the update, not after — so
    ///     "stopping frames draw nothing" costs no frame that was going to be drawn.
    /// </summary>
    [Fact]
    public void AGameThatStopsItselfMidFrameStillFinishesThatFrame() {
        var game = new StoppingGame(frames: 5);
        var application = Build(game);

        Assert.Equal(0, application.Run());
        Assert.Equal(5, game.Frames);
        Assert.Equal(5, game.Renders);
    }

    /// <summary>
    ///     Which is what makes "save before quitting?" possible: an application that handles the
    ///     close request keeps its window and stays running.
    /// </summary>
    [Fact]
    public void AGameThatHandlesTheCloseRequestKeepsItsWindow() {
        var platform = NewPlatform();
        var game = new InterceptingGame(PlatformEventKind.WindowCloseRequested);
        using var application = Build(game, platform);
        application.Initialise();

        ((HeadlessWindow)application.Services.Window!).RequestClose();
        application.RunFrame();

        Assert.False(application.IsStopping);
        Assert.Single(platform.Windows);
    }

    /// <summary>
    ///     Main-thread work drains after events, so something posted by an event handler runs in the
    ///     frame that handled the event rather than the next one. Draining first would leave a frame
    ///     of latency on every reaction to input, which is invisible in a test that posts from
    ///     outside the frame — so this one posts from inside <c>OnEvent</c>.
    /// </summary>
    [Fact]
    public void WorkPostedByAnEventHandlerRunsInThatSameFrame() {
        var platform = NewPlatform();
        var ran = 0;
        var frames = 0;

        var game = new PostingGame(() => ran = frames);
        using var application = Build(game, platform);
        application.Initialise();

        platform.Post(PlatformEvent.Keyboard(PlatformEventKind.KeyDown, 0, 0, Key.Space, KeyModifiers.None));

        frames = 1;
        application.RunFrame();

        Assert.Equal(1, ran);
    }

    [Fact]
    public void WorkPostedFromOutsideTheFrameRunsOnTheNextOne() {
        using var application = Build(new RecordingGame());
        application.Initialise();

        var ran = false;
        application.Services.MainThread.Post(() => ran = true);
        application.RunFrame();

        Assert.True(ran);
    }

    /// <summary>
    ///     Everywhere validation is on, a crash is rethrown so an attached debugger stops on it with
    ///     the stack intact. The shutdown sequence still runs.
    /// </summary>
    [Fact]
    public void ACrashInADevelopmentBuildIsRethrownAfterShutdown() {
        var game = new ThrowingGame();
        var application = Build(game, variant: BuildVariant.Development);

        Assert.Throws<InvalidOperationException>(() => application.Run());
        Assert.Contains("shutdown", game.Calls);
    }

    /// <summary>
    ///     On a player's machine there is no debugger, and the log ring is what the crash reporter
    ///     uploads — so a release build reports the failure through the exit code instead.
    /// </summary>
    [Fact]
    public void ACrashInAReleaseBuildBecomesAnExitCode() {
        var game = new ThrowingGame();
        var application = Build(game, variant: BuildVariant.Release);

        Assert.Equal(1, application.Run());
        Assert.Contains("shutdown", game.Calls);
    }

    [Fact]
    public void ACleanRunExitsWithZero() {
        var game = new StoppingGame(frames: 5);
        var application = Build(game);

        Assert.Equal(0, application.Run());
        Assert.Equal(5, game.Frames);
    }

    /// <summary>
    ///     Everything the host built is disposed, in the reverse of the order it was built. A
    ///     platform left alive after shutdown is a window left on screen.
    /// </summary>
    [Fact]
    public void ShutdownDisposesEverythingItBuilt() {
        var platform = NewPlatform();
        var game = new RecordingGame();
        var application = Build(game, platform);

        application.Initialise();
        application.Shutdown();

        Assert.Equal(ApplicationState.Stopping, platform.Lifecycle.State);
        Assert.True(game.Disposed);
    }

    [Fact]
    public void ShutdownBeforeInitialiseIsNotAnError() {
        var application = Build(new RecordingGame());
        application.Shutdown();
        application.Dispose();
    }

    /// <summary>Everything that logs on the way down is still able to.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It was not, and the comment on the line said the opposite.</b>
    ///         <see cref="DisposeBag" /> disposes in reverse registration order, and the logger
    ///         factory was registered last under a note explaining that this made it the last thing
    ///         torn down. It made it the <i>first</i>: every record the game, the graphics, the
    ///         engine, the content, the jobs and the platform wrote inside their own
    ///         <c>Dispose</c> went to a factory that had already closed its sinks.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The console kept working, which is what hid it.</b> Measured on
    ///         <c>Samples/03-PbrShowcase</c> with <c>--vixen-log-file</c>: the .jsonl ended at the
    ///         game's last shutdown line and the console carried a further Vulkan record 74 ms
    ///         later. So a developer watching a terminal saw a complete shutdown, and
    ///         <c>nuke SampleFrame</c> — which asserts over the file — was reading a log with the
    ///         whole teardown phase missing from it, including anything logged at Error.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first version of this test asked the factory whether it was disposed, and
    ///         the sabotage left it green.</b> <c>HostLoggerFactory.Dispose</c> disposes its
    ///         providers and then <i>clears the list</i>, so <c>CreateLogger</c> on a disposed
    ///         factory does not throw — it returns a fan-out over no providers, an
    ///         <see cref="ILogger" /> that accepts everything and writes it nowhere. A disposed
    ///         logger here is not dead, it is deaf, which is the same shape as the defect being
    ///         tested one level down. So the oracle has to be a provider that says what it received.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheLoggerOutlivesEverythingThatLogsOnTheWayDown() {
        var sink = new RecordingProvider();
        var game = new LoggingOnDisposeGame(sink);
        var application = Build(game);

        application.Initialise();

        Assert.Contains("initialise", sink.Records, StringComparer.Ordinal);

        application.Shutdown();

        Assert.True(game.Attempted, "the game's Dispose never ran, so nothing was tried.");

        Assert.Contains(
            "teardown",
            sink.Records
        );
    }

    public void Dispose() => files.Dispose();

    HeadlessPlatform NewPlatform() => new(new() { FileSystem = files });

    VixenApplication Build(
        Game game,
        HeadlessPlatform? platform = null,
        BuildVariant variant = BuildVariant.Debug
    ) =>
        VixenApp.Create(["--vixen-variant", variant.ToString(), "--vixen-workers", "1", "--vixen-frame-limit", "0"])
            .WithPlatform(platform ?? NewPlatform())
            .Build(game);

    /// <summary>A provider that keeps what it was given, so a dropped record is visible.</summary>
    sealed class RecordingProvider : ILoggerProvider {
        public List<string> Records { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Sink(Records);

        public void Dispose() { }

        sealed class Sink(List<string> records) : ILogger {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => records.Add(formatter(state, exception));
        }
    }

    /// <summary>A game that says something as it is torn down, the way every subsystem does.</summary>
    sealed class LoggingOnDisposeGame(ILoggerProvider sink) : Game {
        static readonly Action<ILogger, string, Exception?> Say =
            LoggerMessage.Define<string>(LogLevel.Information, new EventId(1), "{Phase}");

        public bool Attempted { get; private set; }

        protected internal override void OnConfigure(AppConfig config) => config.Name = "Test";

        protected internal override void OnInitialise() {
            // Added through the factory's own public seam, so what is under test is the order the
            // application tears things down in and not a stubbed factory of the test's own.
            Services.LoggerFactory.AddProvider(sink);
            Say(Services.LoggerFactory.CreateLogger("Teardown"), "initialise", null);
        }

        protected override void Dispose(bool disposing) {
            Attempted = true;
            Say(Services.LoggerFactory.CreateLogger("Teardown"), "teardown", null);
            base.Dispose(disposing);
        }
    }

    sealed class RecordingGame : Game {
        public List<string> Calls { get; } = [];

        public bool Disposed { get; private set; }

        protected internal override void OnConfigure(AppConfig config) {
            Calls.Add("configure");
            config.Name = "Test";
        }

        protected internal override void OnInitialise() => Calls.Add("initialise");

        protected internal override void OnUpdate(GameTime time) => Calls.Add("update");

        protected internal override void OnRender(GameTime time) => Calls.Add("render");

        protected internal override void OnShutdown() => Calls.Add("shutdown");

        protected override void Dispose(bool disposing) {
            base.Dispose(disposing);
            Disposed = true;
        }
    }

    sealed class NoWindowGame : Game {
        protected internal override void OnConfigure(AppConfig config) => config.Window = null;
    }

    sealed class EarlyServiceGame : Game {
        protected internal override void OnConfigure(AppConfig config) => _ = Services;
    }

    sealed class PostingGame(Action work) : Game {
        protected internal override bool OnEvent(in PlatformEvent platformEvent) {
            if (platformEvent.Kind == PlatformEventKind.KeyDown) {
                Services.MainThread.Post(work);
            }

            return false;
        }
    }

    sealed class InterceptingGame(PlatformEventKind swallow) : Game {
        protected internal override bool OnEvent(in PlatformEvent platformEvent) =>
            platformEvent.Kind == swallow;
    }

    sealed class ThrowingGame : Game {
        public List<string> Calls { get; } = [];

        protected internal override void OnUpdate(GameTime time) =>
            throw new InvalidOperationException("The game threw on purpose.");

        protected internal override void OnShutdown() => Calls.Add("shutdown");
    }

    sealed class StoppingGame(int frames) : Game {
        public int Frames { get; private set; }

        public int Renders { get; private set; }

        protected internal override void OnUpdate(GameTime time) {
            if (++Frames >= frames) {
                Services.Platform.Lifecycle.RequestQuit();
            }
        }

        protected internal override void OnRender(GameTime time) => Renders++;
    }
}
