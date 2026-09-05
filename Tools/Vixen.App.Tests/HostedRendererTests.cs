// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization.Storage;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Renderer;
using Vixen.Engine.Transforms;
using Vixen.Graphics;
using Vixen.Platform.Headless;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Rendering.Materials;
using Vixen.Rendering.PostFx;
using Vixen.Rendering.Terrain;
using Vixen.Shaders.Generated;
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
    ///     The frame's compositor is built against the application's own job system.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same object, and that is the assertion.</b> The job scheduler's two tiers are
    ///         a choice between two things a worker could pick up next, so frame work and the work
    ///         that would rather be late than make a frame late have to be queued on <i>one</i>
    ///         scheduler for there to be a choice at all. A compositor given a scheduler of its own
    ///         would have both of them, each with its own workers and neither with anything to yield
    ///         to — which is the shape that reads as wired and buys nothing.
    ///     </para>
    ///     <para>
    ///         This is also the end of the chain the background tier waited on: <c>AppBuilder</c>
    ///         makes it, <c>AppServices.Jobs</c> names it, <c>EngineLoop</c> steps the world on it,
    ///         and <c>GlobalDistanceFieldRenderer</c> is what finally puts something in the second
    ///         tier. See <c>Core/Vixen.Core.Threading/README.md</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheCompositorIsBuiltAgainstTheApplicationsOwnScheduler() {
        using var application = Build(new SilentGame());

        Assert.Same(
            application.Services.Jobs,
            application.Services.Graphics!.Renderer.Host.Builder.Jobs
        );
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

    /// <summary>
    ///     The host's quality tier reaches the per-object light budget, and the shader compiled
    ///     against it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same last step as the ground's budgets, on the array one along from the
    ///         cascades.</b> Every tier named a <c>maxLightsPerObject</c> — four on Low — and nothing
    ///         read any of them: <c>ForwardLightingRenderFeature</c> kept its constructor's eight,
    ///         whatever the settings screen said. A quality knob that changes no behaviour is a
    ///         defect on its own, before anything about shaders.
    ///     </para>
    ///     <para>
    ///         And the second half, which is what makes the first mean anything:
    ///         <c>ClusteredShading.rvn</c> sizes <c>lights[MaxLights]</c> from a permutation declared
    ///         sixteen, so a host that moved its own number and published nothing would have the
    ///         shorter of the two win in silence. <c>CompositorBuilder</c> publishes it as the frame
    ///         is built, which is why this can be asserted off the material feature after
    ///         <c>Build</c> and before anything has drawn.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheHostsQualityTierReachesThePerObjectLightBudget() {
        var game = new TieredGame(QualityTier.Low);
        using var application = Build(game);

        var renderer = application.Services.Graphics!.Renderer;
        var tier = RenderQuality.Resolve(QualityTier.Low);

        Assert.Equal(tier.MaxLightsPerObject, renderer.Lighting.MaxLightsPerObject);

        // Which is not the feature's own default, so the assertion above cannot be passing on it.
        Assert.NotEqual(new ForwardLightingRenderFeature().MaxLightsPerObject, renderer.Lighting.MaxLightsPerObject);

        // And the shader the frame's draws resolve to is compiled for the same number.
        var key = ForwardLightingRenderFeature.MaxLightsKey("ForwardPlus");

        Assert.Equal(tier.MaxLightsPerObject, renderer.Materials.Permutations.Get(key));
        Assert.Contains(key, renderer.Materials.PermutationKeys["ForwardPlus"]);
    }

    /// <summary>
    ///     A layered material's <c>LayerCount</c> is in the effect key of the host that draws, after
    ///     the host has registered the pass's own generated keys over the top.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The assertion that was missing, and the shape of test this repository keeps
    ///         paying for.</b> The engine registered the key in <c>WorldRenderer</c>'s constructor and
    ///         a unit test exercised that registration on a fresh <c>MaterialRenderFeature</c> — green,
    ///         and green under every sabotage, while the defect stood untouched in both shipping
    ///         samples: an architecture rule with no false positives that was satisfied by exactly the
    ///         defect it was meant to catch. What no test read was <em>what a host ends up with</em>,
    ///         and a host ends up with whatever it assigned last.
    ///     </para>
    ///     <para>
    ///         <see cref="LayeredGame" /> is that line and nothing else, copied from
    ///         <c>Samples/03-PbrShowcase/PbrShowcaseGame.cs</c> and
    ///         <c>Samples/13-ThirdPersonShooter/Arena.cs</c>, which both make it in <c>OnInitialise</c>
    ///         — after the renderer was constructed. Five golden device suites make it too. ⚠ The line
    ///         is right and stays: <c>ForwardPlusKeys.UsedPermutationKeys</c> is what the pass's own
    ///         reflection reports, and <c>LayerCount</c> is a <em>composed</em> surface's permutation
    ///         that no pass reflection can carry. It is the dropping that had to become impossible.
    ///     </para>
    ///     <para>
    ///         Both halves are asserted, because keeping the registered key by throwing the host's own
    ///         list away would be the same defect facing the other way — a pass compiled without
    ///         clustered lights or shadows.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AHostsOwnPermutationKeysDoNotDropTheOnesTheRendererRegistered() {
        var game = new LayeredGame();
        using var application = Build(game);

        var renderer = application.Services.Graphics!.Renderer;

        application.Initialise();

        Assert.True(game.Assigned, "The game never made the assignment, so nothing was proved.");

        var keys = renderer.Materials.PermutationKeys["ForwardPlus"];

        Assert.Contains(MaterialKeys.LayerCount("ForwardPlus"), keys);
        Assert.All(ForwardPlusKeys.UsedPermutationKeys, key => Assert.Contains(key, keys));
    }

    /// <summary>Two tiers, two budgets — otherwise the fold could be a constant.</summary>
    [Fact]
    public void ADifferentTierIsADifferentLightBudget() {
        var low = new TieredGame(QualityTier.Low);
        var high = new TieredGame(QualityTier.High);

        using var quietly = Build(low);
        using var lavishly = Build(high);

        Assert.True(
            quietly.Services.Graphics!.Renderer.Lighting.MaxLightsPerObject
            < lavishly.Services.Graphics!.Renderer.Lighting.MaxLightsPerObject
        );
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
    ///     A frame document that names its own tier moves the ground and the texture pool, not only
    ///     the post chain.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The half of the waterfall that used to stop at the passes.</b> The
    ///     <c>!StandardFrame</c> expansion replaces the node during the build, so a host that read
    ///     the tier off the built document read nothing — and a project shipping an Epic frame over
    ///     a host defaulting elsewhere got Epic post effects over another tier's terrain and texture
    ///     budgets, with the two halves of one frame disagreeing about what tier it was. Asserted
    ///     against <c>RenderQuality.Resolve</c> rather than literals, on
    ///     <see cref="TheHostsQualityTierReachesTheGroundsBudgets" />' terms.
    /// </remarks>
    [Fact]
    public void TheFramesOwnTierOutVotesTheHostsForTheGroundAndTheTexturePool() {
        var game = Framed(QualityTier.Low, new() { Quality = QualityTier.Epic });
        using var application = Build(game);

        var epic = RenderQuality.Resolve(QualityTier.Epic);
        var textures = application.Services.Graphics!.Renderer.Textures;

        Assert.Equal(epic.GrassResidentCells, game.Terrain.Vegetation.GrassResidentCells);
        Assert.Equal(epic.FoliageCellBudget, game.Terrain.Vegetation.FoliageCellBudget);
        Assert.Equal(epic.TerrainStreamingMegabytes, game.Terrain.Vegetation.TerrainStreamingMegabytes);
        Assert.Equal(epic.StreamingPoolMegabytes, textures.PoolMegabytes);
        Assert.Equal(epic.MipBias, textures.MipBias);

        // And the host's own pick was genuinely out-voted rather than never consulted: Low says
        // something different about every one of them.
        var low = RenderQuality.Resolve(QualityTier.Low);

        Assert.NotEqual(low.GrassResidentCells, game.Terrain.Vegetation.GrassResidentCells);
        Assert.NotEqual(low.StreamingPoolMegabytes, textures.PoolMegabytes);
    }

    /// <summary>
    ///     A document that names no tier keeps taking the platform's pick — the reading a settings
    ///     screen depends on, and the one a fix for the paragraph above could quietly break.
    /// </summary>
    [Fact]
    public void AFrameThatNamesNoTierStillTakesTheHostsPick() {
        var game = Framed(QualityTier.Low, new());
        using var application = Build(game);

        var low = RenderQuality.Resolve(QualityTier.Low);

        Assert.Equal(low.GrassResidentCells, game.Terrain.Vegetation.GrassResidentCells);
        Assert.Equal(low.StreamingPoolMegabytes, application.Services.Graphics!.Renderer.Textures.PoolMegabytes);
    }

    /// <summary>
    ///     And the document's <em>inline</em> preset reaches them too — per parameter, over the
    ///     tier's own column.
    /// </summary>
    /// <remarks>
    ///     The knob a document uses to move one budget without moving the tier: the overlay is the
    ///     waterfall's top layer, and a fold that read only <c>quality:</c> would carry it as far as
    ///     the passes and drop it here.
    /// </remarks>
    [Fact]
    public void TheDocumentsInlinePresetReachesTheGroundAndTheTexturePool() {
        var game = Framed(
            QualityTier.Low,
            new() {
                Quality = QualityTier.Low,
                Preset = new() {
                    Low = new() {
                        Vegetation = new() { GrassResidentCells = 77 },
                        Textures = new() { StreamingPoolMegabytes = 333 }
                    }
                }
            }
        );

        using var application = Build(game);

        Assert.Equal(77, game.Terrain.Vegetation.GrassResidentCells);
        Assert.Equal(333, application.Services.Graphics!.Renderer.Textures.PoolMegabytes);

        // Siblings the overlay says nothing about stay the tier's, which is the per-parameter rule
        // rather than a whole column being replaced.
        Assert.Equal(
            RenderQuality.Resolve(QualityTier.Low).FoliageCellBudget,
            game.Terrain.Vegetation.FoliageCellBudget
        );
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

    const string FrameAddress = "frames/main";

    /// <summary>
    ///     A game whose project ships a frame document, published where the host will mount it.
    /// </summary>
    /// <remarks>
    ///     Through real content rather than by handing the asset over, because the thing under test
    ///     is the host reading a document it loaded by address — which is the only way a shipped
    ///     game's frame ever arrives, and the step where the document's own tier used to be lost.
    /// </remarks>
    AuthoredFrameGame Framed(QualityTier host, StandardFrameAsset frame) {
        Publish(
            Path.Combine(files.ApplicationDirectory, ContentMount.FolderName),
            FrameAddress,
            new GraphicsCompositorAsset { Game = frame }
        );

        return new(host);
    }

    /// <summary>Writes a one-asset content build the way `vixen content build` lays one out.</summary>
    static void Publish<TAsset>(string directory, string address, TAsset asset) {
        Directory.CreateDirectory(directory);

        var scratch = new VirtualFileSystem();
        scratch.Mount(new("/odb"), new MemoryFileProvider());

        var backend = new FileOdbBackend(scratch, new("/odb"));
        var id = new ObjectDatabase(backend).Write(asset);

        var bundle = new BundleWriter();
        bundle.AddAll(backend);
        File.WriteAllBytes(Path.Combine(directory, "Main.bundle"), bundle.Build());

        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            default,
            "Windows",
            [new(address, id, "Main", ContentProvider.Local, [], [], 0)],
            [new("Main", "", default, 0, 0, CompressionMethod.Lz4, [])]
        );

        File.WriteAllBytes(Path.Combine(directory, ContentMount.CatalogFileName), CatalogFormat.Write(catalog));
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
    ///     A game that registers the shading pass's generated permutation keys, which is the one line
    ///     every host that draws materials writes.
    /// </summary>
    sealed class LayeredGame : SilentGame {
        /// <summary>Whether the host actually got as far as the line under test.</summary>
        public bool Assigned { get; private set; }

        protected internal override void OnInitialise() {
            base.OnInitialise();

            if (Services.Graphics is not { } graphics) {
                return;
            }

            graphics.Renderer.Materials.PermutationKeys["ForwardPlus"] = ForwardPlusKeys.UsedPermutationKeys;
            Assigned = true;
        }
    }

    /// <summary>A game that says which tier it is and nothing else.</summary>
    sealed class TieredGame(QualityTier tier) : SilentGame {
        protected internal override void OnConfigure(AppConfig config) {
            base.OnConfigure(config);
            config.Graphics.Quality = tier;
        }
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

    /// <summary>
    ///     <see cref="GroundGame" /> with a project frame under it: the ground, the effect set that
    ///     owns <c>!StandardFrame</c>, and the address the document was published at.
    /// </summary>
    /// <remarks>
    ///     Both factories, and in this order: the effect set's transform expands the frame node, and
    ///     the terrain factory's has to see the expanded document rather than the preset that stands
    ///     for it. Constructing them is also what registers their YAML and type-registry tags, which
    ///     is what lets the published document deserialise at all.
    /// </remarks>
    sealed class AuthoredFrameGame(QualityTier tier) : SilentGame {
        public TerrainFactory Terrain { get; } = new();

        protected internal override void OnConfigure(AppConfig config) {
            base.OnConfigure(config);

            config.Graphics.Quality = tier;
            config.Graphics.Compositor = FrameAddress;
            config.Graphics.Factories.Add(new PostEffectFactory());
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
