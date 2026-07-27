// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Frames;
using Vixen.Platform.Headless;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     That the host and the engine compose, which nothing had ever checked. Both were built and
///     tested on their own; nothing in the shipping path referenced <c>Vixen.Engine</c> at all, so
///     "a game gets a world" was a thing the plan assumed and no test had ever observed.
/// </summary>
public sealed class HostedEngineTests : IDisposable {
    readonly TemporaryFileSystemHost files = new();

    public void Dispose() => files.Dispose();

    [Fact]
    public void AGameGetsAWorldWithoutAskingForOne() {
        using var application = Build(new SilentGame());

        var engine = Assert.IsType<EngineLoop>(application.Services.Engine);

        Assert.NotNull(engine.World);
        Assert.Same(engine, application.Services.Registry.Get<EngineLoop>());
        Assert.Same(engine.World, application.Services.Registry.Get<World>());
    }

    /// <summary>
    ///     The frame the host runs is the frame the engine runs. Without this the behaviours a game
    ///     attaches never tick, and the failure is silent — nothing throws, the world simply never
    ///     moves.
    /// </summary>
    [Fact]
    public void TheHostFrameDrivesTheEngineFrame() {
        var game = new CountingGame();
        using var application = Build(game);
        var engine = application.Services.Engine!;

        application.Initialise();
        application.RunFrame();
        application.RunFrame();
        application.RunFrame();

        // The engine's own clock, not the game's callback count: a host that called OnUpdate three
        // times and the engine none would pass any assertion made on the latter.
        Assert.Equal(3, engine.Time.FrameCount);
        Assert.Equal(3, game.Ticks);
    }

    /// <summary>
    ///     The engine is handed the <em>unscaled</em> delta and the scale separately, because that is
    ///     what its own contract takes. Passing the already-scaled value with the scale again squares
    ///     it, and half speed becomes a quarter — which nothing notices until somebody times a
    ///     slow-motion effect.
    /// </summary>
    [Fact]
    public void TheTimeScaleIsAppliedOnceRatherThanTwice() {
        using var application = Build(new SilentGame());
        var engine = application.Services.Engine!;

        application.Initialise();
        application.TimeScale = 0.5f;
        application.RunFrame();

        Assert.Equal(application.Time.Elapsed, engine.Time.Elapsed);
        Assert.Equal(application.Time.UnscaledElapsed * 0.5, engine.Time.Elapsed);
    }

    /// <summary>
    ///     Before the game's own update, because <c>OnUpdate</c> is where an application reads the
    ///     world it is about to render, and reading it before it has been stepped renders last
    ///     frame's positions.
    /// </summary>
    /// <remarks>
    ///     The first frame has no behaviour in it, and that is <c>BehaviorStore</c>'s deliberate
    ///     one-frame deferral rather than the host getting the order wrong: a behaviour queued
    ///     before a drain becomes eligible in that drain and runs from the next one. Asserting the
    ///     whole sequence pins both facts, so a change to either is a change to this test.
    /// </remarks>
    [Fact]
    public void TheEngineRunsBeforeTheGamesOwnUpdate() {
        var game = new OrderingGame();
        using var application = Build(game);

        application.Initialise();
        application.RunFrame();
        application.RunFrame();
        application.RunFrame();

        Assert.Equal(["update", "behaviour", "update", "behaviour", "update"], game.Order);
    }

    /// <summary>
    ///     A batch tool, a server with its own loop and a UI-only application each want the host and
    ///     not the world. One line in <c>OnConfigure</c>.
    /// </summary>
    [Fact]
    public void AHeadThatDoesNotWantAWorldSaysSoAndGetsNone() {
        using var application = Build(new NoEngineGame());

        Assert.Null(application.Services.Engine);

        application.Initialise();
        application.RunFrame();
    }

    /// <summary>
    ///     Pausing reaches both clocks. If it reached only the host's, the accumulator would keep
    ///     owing steps for the paused time and pay them all at once on resume — which looks like the
    ///     game fast-forwarding through however long the menu was open.
    /// </summary>
    [Fact]
    public void PausingStopsTheSimulationRatherThanStoringUpAThousandSteps() {
        var game = new CountingGame();
        using var application = Build(game);

        application.Initialise();
        application.TimeScale = 0f;

        for (var frame = 0; frame < 10; frame++) {
            application.RunFrame();
        }

        Assert.Equal(TimeSpan.Zero, application.Services.Engine!.Time.Total);
        Assert.Equal(0, application.Services.Engine.FixedStep.TotalSteps);

        // And the frames themselves still ran, which is what keeps a paused game drawing.
        Assert.Equal(10, game.Ticks);
    }

    [Fact]
    public void TheFixedStepComesFromTheConfigurationWhenAGameNamesOne() {
        using var application = Build(new SlowStepGame());

        Assert.Equal(TimeSpan.FromSeconds(0.1), application.Services.Engine!.FixedStep.Step);
    }

    VixenApplication Build(Game game) =>
        VixenApp.Create(["--vixen-workers", "1", "--vixen-frame-limit", "0"])
            .WithPlatform(new HeadlessPlatform(new HeadlessPlatformOptions { FileSystem = files }))
            .Build(game);

    class SilentGame : Game {
        protected internal override void OnConfigure(AppConfig config) => config.Window = null;
    }

    /// <summary>Counts the frames the host ran, from outside the engine.</summary>
    sealed class CountingGame : SilentGame {
        public int Ticks { get; private set; }

        protected internal override void OnUpdate(GameTime time) => Ticks++;
    }

    sealed class NoEngineGame : SilentGame {
        protected internal override void OnConfigure(AppConfig config) {
            base.OnConfigure(config);
            config.UseEngine = false;
        }
    }

    sealed class SlowStepGame : SilentGame {
        protected internal override void OnConfigure(AppConfig config) {
            base.OnConfigure(config);
            config.FixedStep = TimeSpan.FromSeconds(0.1);
        }
    }

    /// <summary>Records whether a behaviour's update landed before the game's own.</summary>
    sealed class OrderingGame : SilentGame {
        public List<string> Order { get; } = [];

        protected internal override void OnInitialise() {
            var engine = Services.Engine!;
            engine.Behaviors.Add(engine.World.Create(), new Recorder(Order));
        }

        protected internal override void OnUpdate(GameTime time) => Order.Add("update");
    }

    sealed class Recorder(List<string> order) : Behavior {
        protected override void Update() => order.Add("behaviour");
    }
}
