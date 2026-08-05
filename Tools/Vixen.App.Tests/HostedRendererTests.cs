// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Renderer;
using Vixen.Engine.Transforms;
using Vixen.Graphics;
using Vixen.Platform.Headless;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Vixen.Rendering.Terrain;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     That a game gets a drawn frame, which is the step the host stopped one short of.
/// </summary>
/// <remarks>
///     <para>
///         <c>WorldRenderer</c> was the whole join between a world and a picture and nothing outside a
///         test project constructed one — so "a game draws its scene" was, like "a game gets a world"
///         before it, a thing the plan assumed and no test had ever observed. These run the real host
///         against the headless platform and the Null backend, which records every command and draws
///         none of them: the graph is built, the passes are ordered and the barriers are placed, so
///         everything except the pixels is under test on a machine with no GPU.
///     </para>
///     <para>
///         Which is also the arrangement <c>--vixen-frames N</c> uses in CI, and the reason a
///         dedicated server and a client are one program.
///     </para>
/// </remarks>
public sealed class HostedRendererTests : IDisposable {
    readonly TemporaryFileSystemHost files = new();

    public void Dispose() => files.Dispose();

    [Fact]
    public void AGameGetsARendererWithoutAskingForOne() {
        using var application = Build(new SilentGame());

        var graphics = application.Services.Graphics;

        Assert.NotNull(graphics);
        Assert.NotNull(graphics.Renderer);
        Assert.Same(graphics, application.Services.Registry.Get<AppGraphics>());
        Assert.Same(graphics.Renderer, application.Services.Registry.Get<WorldRenderer>());
        Assert.Same(graphics.Device, application.Services.Registry.Get<IGraphicsDevice>());
    }

    /// <summary>
    ///     The frame the host runs is a frame the renderer records. Without this the whole stack is
    ///     built, wired and never asked for anything — which is the state it was in.
    /// </summary>
    [Fact]
    public void TheHostFrameDrawsTheWorldsFrame() {
        using var application = Build(new SilentGame());
        var graphics = application.Services.Graphics!;

        application.Initialise();
        application.RunFrame();
        application.RunFrame();
        application.RunFrame();

        // The renderer's own counter rather than the game's callback count: a host that called
        // OnRender three times and drew nothing would pass any assertion made on the latter.
        Assert.Equal(3, graphics.FrameCount);
    }

    /// <summary>
    ///     ⚠ The window's image is lent to the document <em>every</em> frame. A swapchain hands out a
    ///     different image per acquire, so an import bound once names whichever one came first and two
    ///     frames in three are drawn into something nobody is presenting.
    /// </summary>
    [Fact]
    public void TheImageIsLentToTheFrameUnderTheNameTheDocumentWrites() {
        using var application = Build(new SilentGame());
        var graphics = application.Services.Graphics!;

        application.Initialise();
        application.RunFrame();

        var first = graphics.Renderer.Host.Compositor!.Imports["SceneColour"];

        application.RunFrame();

        var second = graphics.Renderer.Host.Compositor!.Imports["SceneColour"];

        Assert.NotEqual(default, first.View);
        Assert.NotEqual(default, second.View);
    }

    /// <summary>
    ///     <c>OnRender</c> is offered the frame's own command list, opened and with the scene already
    ///     in it, so an application's overlay records on top of the world rather than under it.
    /// </summary>
    [Fact]
    public void TheGamesRenderHookGetsTheOpenCommandList() {
        var game = new RecordingGame();
        using var application = Build(game);

        application.Initialise();
        application.RunFrame();

        Assert.True(game.HadCommands);
    }

    /// <summary>
    ///     The other half of the join: a camera placed in the world is what the frame is seen
    ///     through. Until <c>CameraExtractionSystem</c> existed, <c>Camera</c> was a component
    ///     nothing read and every host steered a view by hand.
    /// </summary>
    [Fact]
    public void TheScenesCameraAimsTheFramesView() {
        using var application = Build(new SilentGame());
        var graphics = application.Services.Graphics!;
        var world = application.Services.Engine!.World;

        var entity = Hierarchy.CreateTransform(world, LocalTransform.At(new(0f, 2f, 10f)));
        world.Add(entity, Camera.Perspective);

        application.Initialise();
        application.RunFrame();

        var expected = CameraMath.ViewProjection(
            Camera.Perspective,
            world.Read<WorldTransform>(entity),
            graphics.Camera.AspectRatio
        );

        Assert.True(graphics.Camera.Found);
        Assert.Equal(1, graphics.Camera.CameraCount);
        Assert.Equal(new Vector3(0f, 2f, 10f), graphics.View.Position);
        Assert.Equal(expected, graphics.View.ViewProjection);

        // The aspect is the frame's, not a constant: a camera whose own ratio is zero means "ask the
        // target", and the target is what the host sized the swapchain to.
        Assert.Equal(1280f / 720f, graphics.Camera.AspectRatio, 3);
    }

    /// <summary>
    ///     Lowest <see cref="Camera.Order" /> wins, which is what that field has always said it
    ///     meant. A scene with a menu camera and a world camera in it is ordinary.
    /// </summary>
    [Fact]
    public void TheLowestOrderCameraIsTheOneTheFrameUses() {
        using var application = Build(new SilentGame());
        var graphics = application.Services.Graphics!;
        var world = application.Services.Engine!.World;

        var second = Hierarchy.CreateTransform(world, LocalTransform.At(new(100f, 0f, 0f)));
        world.Add(second, Camera.Perspective with { Order = 5 });

        var first = Hierarchy.CreateTransform(world, LocalTransform.At(new(0f, 0f, 3f)));
        world.Add(first, Camera.Perspective with { Order = -1 });

        application.Initialise();
        application.RunFrame();

        Assert.Equal(2, graphics.Camera.CameraCount);
        Assert.Equal(new Vector3(0f, 0f, 3f), graphics.View.Position);
    }

    /// <summary>
    ///     A world with no camera in it leaves the view alone and says so. Zeroing it would draw a
    ///     black frame through a degenerate matrix, which reads as a broken renderer rather than as a
    ///     level somebody has not finished.
    /// </summary>
    [Fact]
    public void AWorldWithNoCameraSaysSoRatherThanRenderingFromNowhere() {
        using var application = Build(new SilentGame());
        var graphics = application.Services.Graphics!;

        application.Initialise();
        application.RunFrame();

        Assert.False(graphics.Camera.Found);
        Assert.Equal(0, graphics.Camera.CameraCount);
        Assert.Equal(Matrix4x4.Identity, graphics.View.ViewProjection);
    }

    /// <summary>
    ///     The extraction is wired to the stage the document declares, because a stage's index is
    ///     assigned by the render system and a mask of none extracts every object into no pass at
    ///     all — a correct-looking wiring that draws nothing.
    /// </summary>
    [Fact]
    public void TheExtractionDrawsIntoTheStageTheDocumentDeclares() {
        using var application = Build(new SilentGame());
        var graphics = application.Services.Graphics!;

        Assert.NotEqual(RenderStageMask.None, graphics.Stages);
        Assert.Equal(graphics.Stages, graphics.Renderer.Extraction!.Stages);
        Assert.Equal(graphics.Stages, graphics.View.Stages);
    }

    /// <summary>
    ///     The built-in frame declares the three names <see cref="GraphicsOptions" /> binds by
    ///     default. Renaming one on either side without the other is a black window and no error.
    /// </summary>
    [Fact]
    public void TheDefaultFrameDeclaresTheNamesTheDefaultOptionsBind() {
        var options = new GraphicsOptions();
        var frame = AppGraphics.DefaultFrame;

        Assert.Contains(frame.Stages, stage => stage.Name == options.Stage);
        Assert.Contains(frame.Resources, resource => resource.Name == options.Output);
    }

    /// <summary>
    ///     The host's quality tier reaches the ground's streaming budgets, which for a long time it
    ///     did not.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The last step of doc 39's waterfall, and the one nothing outside a test performed.</b>
    ///     <c>Vixen.Rendering.Terrain</c> cannot reference the assembly the tier table lives in, so
    ///     the resolved numbers cross to <c>TerrainFactory.Vegetation</c> as a plain-numbered copy —
    ///     and until <c>AppGraphics</c> did that folding, a shipped game got the terrain stack's
    ///     constructor defaults whatever tier it had selected. Asserted against
    ///     <c>RenderQuality.Resolve</c> rather than against literals, so the test says "the tier's
    ///     numbers" and not "these numbers, which somebody may have retuned".
    /// </remarks>
    [Fact]
    public void TheHostsQualityTierReachesTheGroundsBudgets() {
        var game = new GroundGame(QualityTier.Low);
        using var application = Build(game);

        var tier = RenderQuality.Resolve(QualityTier.Low);
        var vegetation = game.Terrain.Vegetation;

        Assert.Equal(tier.GrassDensityScale, vegetation.GrassDensityScale);
        Assert.Equal(tier.GrassCullDistanceScale, vegetation.GrassCullDistanceScale);
        Assert.Equal(tier.GrassResidentCells, vegetation.GrassResidentCells);
        Assert.Equal(tier.FoliageDensityScale, vegetation.FoliageDensityScale);
        Assert.Equal(tier.FoliageCullDistanceScale, vegetation.FoliageCullDistanceScale);
        Assert.Equal(tier.FoliageCellBudget, vegetation.FoliageCellBudget);
        Assert.Equal(tier.TerrainLodNearRange, vegetation.TerrainNearRange);
        Assert.Equal(tier.TerrainStreamingMegabytes, vegetation.TerrainStreamingMegabytes);

        // And the tier was actually consulted rather than the defaults happening to match: Low is
        // below the record's own numbers everywhere it says anything.
        Assert.NotEqual(new TerrainVegetationQuality(), vegetation);
    }

    /// <summary>Two tiers, two sets of budgets — otherwise the fold could be a constant.</summary>
    [Fact]
    public void ADifferentTierIsADifferentSetOfBudgets() {
        var low = new GroundGame(QualityTier.Low);
        var epic = new GroundGame(QualityTier.Epic);

        using var quietly = Build(low);
        using var lavishly = Build(epic);

        var quiet = low.Terrain.Vegetation;
        var lavish = epic.Terrain.Vegetation;

        Assert.True(quiet.FoliageCellBudget < lavish.FoliageCellBudget);
        Assert.True(quiet.GrassResidentCells < lavish.GrassResidentCells);
        Assert.True(quiet.TerrainStreamingMegabytes < lavish.TerrainStreamingMegabytes);
    }

    /// <summary>
    ///     A game that filled the budgets itself keeps them, on <c>TerrainFactory.Scene</c>'s terms:
    ///     the host configures what nobody has decided, and a head with its own opinion has decided.
    /// </summary>
    [Fact]
    public void BudgetsTheGameFilledItselfAreLeftAlone() {
        var chosen = new TerrainVegetationQuality { FoliageCellBudget = 7, GrassResidentCells = 9 };
        var game = new GroundGame(QualityTier.Low) { Chosen = chosen };

        using var application = Build(game);

        Assert.Equal(chosen, game.Terrain.Vegetation);
    }

    /// <summary>
    ///     A batch tool wants the host and not a device. One line in <c>OnConfigure</c>, and the
    ///     frame still runs — including <c>OnRender</c>, which is where such a head does its work.
    /// </summary>
    [Fact]
    public void AHeadThatDoesNotWantGraphicsSaysSoAndGetsNone() {
        var game = new NoGraphicsGame();
        using var application = Build(game);

        Assert.Null(application.Services.Graphics);

        application.Initialise();
        application.RunFrame();

        Assert.Equal(1, game.Renders);
    }

    /// <summary>
    ///     A device the caller supplied is not disposed with the application: it belongs to whoever
    ///     handed it over — an editor's play mode, an XR runtime — and taking it down would take
    ///     their window with it.
    /// </summary>
    [Fact]
    public void ADeviceHandedInIsNotDisposedWithTheApplication() {
        using var device = new Graphics.Null.NullDevice(new() { Record = true });

        using (var application = VixenApp.Create(Arguments)
                   .WithPlatform(new HeadlessPlatform(new HeadlessPlatformOptions { FileSystem = files }))
                   .WithGraphics(device)
                   .Build(new SilentGame())) {
            application.Initialise();
            application.RunFrame();

            Assert.Same(device, application.Services.Graphics!.Device);
        }

        // Still usable, which is the whole claim: a disposed NullDevice throws from here.
        Assert.NotNull(device.Adapter);
    }

    /// <summary>
    ///     A suspended application loses its surface and not its device — Android destroys the native
    ///     window, iOS takes the layer back — so the swapchain is dropped and the next frame builds one
    ///     from the resumed window's handle. Dropping the device instead would invalidate every
    ///     texture, buffer and pipeline the game is holding.
    /// </summary>
    [Fact]
    public void SuspendingDropsTheSwapChainAndTheNextFrameRebuildsIt() {
        using var application = Build(new WindowedGame());
        var graphics = application.Services.Graphics!;

        application.Initialise();
        application.RunFrame();

        var device = graphics.Device;
        Assert.NotNull(graphics.SwapChain);

        graphics.Suspend();
        Assert.Null(graphics.SwapChain);

        application.RunFrame();

        Assert.NotNull(graphics.SwapChain);
        Assert.Same(device, graphics.Device);
        Assert.Equal(2, graphics.FrameCount);
    }

    /// <summary>
    ///     A resize to the size it already is rebuilds nothing. A window opened maximised produces a
    ///     burst of resize events, and a rebuild per event is a device-wide wait and a fresh set of
    ///     undefined images several times before one frame is drawn.
    /// </summary>
    [Fact]
    public void ResizingToTheSameSizeRebuildsNothing() {
        using var application = Build(new WindowedGame());
        var graphics = application.Services.Graphics!;

        application.Initialise();
        application.RunFrame();

        var swapChain = graphics.SwapChain;

        graphics.Recreate();
        graphics.Recreate();

        Assert.Same(swapChain, graphics.SwapChain);
    }

    static string[] Arguments => ["--vixen-workers", "1", "--vixen-frame-limit", "0"];

    VixenApplication Build(Game game) =>
        VixenApp.Create(Arguments)
            .WithPlatform(new HeadlessPlatform(new HeadlessPlatformOptions { FileSystem = files }))
            .Build(game);

    /// <summary>
    ///     No window, so the frame is drawn at <see cref="GraphicsOptions.WindowlessSize" /> — which
    ///     is the head a server and a <c>--vixen-frames</c> run both are.
    /// </summary>
    class SilentGame : Game {
        protected internal override void OnConfigure(AppConfig config) => config.Window = null;
    }

    /// <summary>
    ///     With a window, so the swapchain is sized from it — the headless platform's windows report
    ///     a surface of <c>SurfaceKind.None</c>, which is exactly the case <c>GraphicsHost</c> answers
    ///     with the Null backend.
    /// </summary>
    sealed class WindowedGame : Game {
        protected internal override void OnConfigure(AppConfig config) => config.Window = new();
    }

    /// <summary>
    ///     A game with ground in it: the one line of <c>OnConfigure</c> that installs the terrain
    ///     node kind, which is also the whole installation the host recognises.
    /// </summary>
    sealed class GroundGame(QualityTier tier) : SilentGame {
        public TerrainFactory Terrain { get; } = new();

        /// <summary>Budgets the game decided for itself, or null to let the host's tier decide.</summary>
        public TerrainVegetationQuality? Chosen { get; init; }

        protected internal override void OnConfigure(AppConfig config) {
            base.OnConfigure(config);

            if (Chosen is { } opinion) {
                Terrain.Vegetation = opinion;
            }

            config.Graphics.Quality = tier;
            config.Graphics.Factories.Add(Terrain);
        }
    }

    sealed class RecordingGame : SilentGame {
        public bool HadCommands { get; private set; }

        protected internal override void OnRender(GameTime time) =>
            HadCommands = Services.Graphics?.Commands is not null;
    }

    sealed class NoGraphicsGame : SilentGame {
        public int Renders { get; private set; }

        protected internal override void OnConfigure(AppConfig config) {
            base.OnConfigure(config);
            config.Graphics.Enabled = false;
        }

        protected internal override void OnRender(GameTime time) => Renders++;
    }
}
