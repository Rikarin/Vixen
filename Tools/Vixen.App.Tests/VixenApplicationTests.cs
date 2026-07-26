// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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

        protected internal override void OnUpdate(GameTime time) {
            if (++Frames >= frames) {
                Services.Platform.Lifecycle.RequestQuit();
            }
        }
    }
}
