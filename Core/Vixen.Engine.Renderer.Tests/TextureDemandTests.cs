// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.IO;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml;
using Vixen.Ecs;
using Vixen.Engine.Frames;
using Vixen.Engine.Renderer;
using Vixen.Engine.Transforms;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     What a frame is looking at is what decides how big its textures are.
/// </summary>
/// <remarks>
///     <para>
///         <b>The half of streaming that had nothing calling it.</b> <c>Want</c> existed, the
///         projected-size estimate existed, and no line in the engine joined a drawable's bounds to
///         the textures its material samples — so every texture in a scene wanted to be complete and
///         the pool bounded memory without prioritising it. These are the four claims
///         <see cref="TextureDemand" /> has to make good: screen size drives the width, the maximum
///         over a texture's users wins, a texture that leaves the view stops being asked for, and the
///         width does not oscillate at a threshold.
///     </para>
///     <para>
///         ⚠ <b>Distances are chosen mid-rung, never on one.</b> A width is a <c>ceil</c> of a
///         product of floats, so a distance contrived to land on exactly 256 texels is a test that
///         passes or fails on the last bit of a multiply. Every placement here aims between two
///         powers of two, and what is asserted is the rung it lands in.
///     </para>
/// </remarks>
public sealed class TextureDemandTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    const string Document = """
        version: 2
        resources:
          - name: SceneColour
            format: Rgba16Float
            usage: ColourTarget, Sampled
          - name: SceneDepth
            format: Depth32Float
            usage: DepthStencilTarget
        stages:
          - name: Opaque
        game: !Sequence
          name: Frame
          children:
            - !RenderPass
              name: Main
              colourTargets: [SceneColour]
              depthTarget: SceneDepth
              children:
                - !SingleStage
                  name: OpaqueDraw
                  view: Camera
                  stage: Opaque
        """;

    /// <summary>How many pixels tall the frame is, which is what turns a projected size into texels.</summary>
    const int Height = 512;

    /// <summary>
    ///     A drawable's screen size is what its texture's wanted width is, and moving it away
    ///     narrows that.
    /// </summary>
    /// <remarks>
    ///     The claim in its simplest form. A texture asked for at 256 texels when the object fills a
    ///     quarter of the screen and at 64 when it fills a sixteenth is a texture whose residency
    ///     follows the camera — which is the whole difference between a budget that bounds memory and
    ///     one that spends it on what is being looked at.
    /// </remarks>
    [Fact]
    public void AnObjectsScreenSizeIsWhatItsTextureIsWantedAt() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out var view);

        var entity = Place(loop, Near, Vector3.Zero);

        Settle(loop, renderer);

        var radius = loop.World.Read<RenderHandle>(entity).Local.Radius;

        // Between 128 and 256 texels, so the rung is 256 and no rounding decides it.
        Move(loop, entity, DistanceFor(radius, 200f));
        Frame(loop, renderer);

        Assert.True(renderer.Demand!.TryGetWidth(Bark, out var near));
        Assert.Equal(256, near);

        // And four times further out is two rungs down, taken in one frame: the band only ever holds
        // up a move to an adjacent rung.
        Move(loop, entity, DistanceFor(radius, 50f));
        Frame(loop, renderer);

        Assert.True(renderer.Demand.TryGetWidth(Bark, out var far));
        Assert.Equal(64, far);
    }

    /// <summary>
    ///     One texture on two materials is wanted at the size of whichever is nearest.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The maximum, not the last one seen.</b> A texture is shared by everything painted
    ///     with it, and taking whichever drawable the object list happened to end with would make a
    ///     wall's albedo blurry because a rock in the distance also uses it — and would change answer
    ///     when an unrelated entity was created, because that is what reorders the list.
    /// </remarks>
    [Fact]
    public void ATextureIsWantedAtTheSizeOfItsNearestUser() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out var view);

        var near = Place(loop, Near, Vector3.Zero);
        var far = Place(loop, Far, Vector3.Zero);

        Settle(loop, renderer);

        var radius = loop.World.Read<RenderHandle>(near).Local.Radius;

        Move(loop, near, DistanceFor(radius, 200f));
        Move(loop, far, DistanceFor(radius, 50f));

        Frame(loop, renderer);

        // Two materials, one texture, and the near one decides.
        Assert.True(renderer.Demand!.TryGetWidth(Bark, out var width));
        Assert.Equal(256, width);

        // Swapping which of them is near swaps nothing: it is still the maximum.
        Move(loop, near, DistanceFor(radius, 50f));
        Move(loop, far, DistanceFor(radius, 200f));

        Frame(loop, renderer);

        Assert.True(renderer.Demand.TryGetWidth(Bark, out width));
        Assert.Equal(256, width);

        // And only when both have gone does the want fall.
        Move(loop, near, DistanceFor(radius, 50f));
        Move(loop, far, DistanceFor(radius, 50f));
        Frame(loop, renderer);

        Assert.True(renderer.Demand.TryGetWidth(Bark, out width));
        Assert.Equal(64, width);
    }

    /// <summary>
    ///     A texture whose users have all left the view is not asked for at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Not asked for, rather than asked for at zero.</b> A want of zero texels means the
    ///         smallest level, which is a request; the absence of a want is what leaves the pages to
    ///         age in the least-recently-used order and be the next thing evicted when something in
    ///         front of the camera needs the room. Re-asking at the old width is exactly how a
    ///         texture that has left the view never gives its pages back.
    ///     </para>
    ///     <para>
    ///         Culled by being put behind the camera rather than by being destroyed, because
    ///         destroying it would also remove the material and the claim is about visibility.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ATextureThatLeavesTheViewStopsBeingWanted() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out var view);

        var entity = Place(loop, Near, Vector3.Zero);

        Settle(loop, renderer);

        var radius = loop.World.Read<RenderHandle>(entity).Local.Radius;

        Move(loop, entity, DistanceFor(radius, 200f));
        Frame(loop, renderer);

        Assert.Equal(1, renderer.Demand!.Sized);
        Assert.True(renderer.Demand.TryGetWidth(Bark, out _));

        // Behind the eye, which the frustum rejects. The entity, the material and the texture are all
        // still here; only the visibility changed.
        loop.World.Set(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(new(0f, 0f, 40f)) });

        // ⚠ Two frames, because the survey reads the previous frame's cull — see TextureDemand. The
        // first frame here is the one that culls it; the second is the one that stops asking.
        Frame(loop, renderer);
        Frame(loop, renderer);

        Assert.Equal(0, renderer.Demand.Sized);
        Assert.False(renderer.Demand.TryGetWidth(Bark, out var width));
        Assert.Equal(0, width);
    }

    /// <summary>
    ///     A camera drifting across a rung boundary does not move the rung, and therefore records no
    ///     swap.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The churn claim, and the reason the rungs are powers of two.</b> A swap is a fresh
    ///         image, a fresh view and a whole mip tail copied up — so a wanted width that flips
    ///         either side of a mip level's width would pay that on alternate frames for ever. What
    ///         removes it is a dead band around the <em>same</em> number the level decision turns on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted against the mechanism being off as well as on.</b> Sixty frames with no
    ///         promotion in them is also what a survey that never ran would produce, so the same
    ///         motion with <see cref="TextureDemand.Hysteresis" /> at zero has to churn — and it does,
    ///         once per crossing in each direction.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AWidthDriftingAcrossARungDoesNotOscillate() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out var view);

        var entity = Place(loop, Near, Vector3.Zero);

        Settle(loop, renderer);

        var radius = loop.World.Read<RenderHandle>(entity).Local.Radius;

        // Either side of 256 texels by a few per cent — a camera breathing, not a camera travelling.
        var under = DistanceFor(radius, 248f);
        var over = DistanceFor(radius, 268f);

        Move(loop, entity, under);
        Frame(loop, renderer);

        Assert.Equal(256, Width(renderer));

        // ⚠ Until the pages the width asked for have arrived, and only then. A texture growing from
        // its pinned floor into the level it was asked for swaps on the way up, and those swaps are
        // the feature working — counting them as churn would fail this test for the one thing it is
        // supposed to allow. 256 texels of a 1024 chain is level two.
        Resident(loop, renderer, level: 2);

        // ⚠ And then until the demand stops moving, which residency alone does not imply. The level
        // being here says the pages arrived; the promotions the arrival caused are recorded a frame
        // or two later, and on a loaded machine two of them landed *inside* the breathing loop below
        // and were counted as churn the band was supposed to have prevented. Measured on CI: two
        // promotions expected, four seen, in a loop whose width assertion passed every frame.
        Quiet(loop, renderer);

        var swaps = renderer.Painted!.StreamingSwaps;
        var promotions = renderer.Demand!.Promotions;
        var demotions = renderer.Demand.Demotions;

        // Or the assertion below would hold for a texture that never swapped at all.
        Assert.True(swaps > 0, "the texture never reached the level its width asked for");

        // The second copy, which is what makes the real cost of a pool roughly twice its budget: the
        // image holds the tail the width asked for, and the pool is already holding those pages.
        //
        // ⚠ Not equality, which is what this said and what made it the most-failed test in CI. The
        // pool drops nothing until something evicts it, and eviction is least-recently-used under
        // pressure — so the fine pages this object streamed while it sat on the camera during
        // `Settle` are still resident after it moves back and the width drops to 256. Whether they
        // arrived before the move is a race with the loader's task, which is why this passed on an
        // idle machine and failed on a loaded one: measured on CI, a pool of 1,376,256 B — levels
        // zero, one and two — against an image of 87,381 B, which is the level-two tail and is the
        // number that was right.
        Assert.InRange(
            renderer.Painted.StreamedImageBytes,
            1,
            renderer.Painted.Streaming!.ResidentBytes + renderer.Painted.Streaming.PageSize
        );

        for (var frame = 0; frame < 60; frame++) {
            Move(loop, entity, frame % 2 == 0 ? over : under);
            Frame(loop, renderer);

            Assert.Equal(256, Width(renderer));

            Thread.Sleep(1);
        }

        Assert.Equal(promotions, renderer.Demand.Promotions);
        Assert.Equal(demotions, renderer.Demand.Demotions);

        // The number the whole band exists to hold still. A swap is an upload, and sixty frames of a
        // camera breathing must not buy one.
        Assert.Equal(swaps, renderer.Painted.StreamingSwaps);

        // And with the band off, the same motion crosses the boundary every frame it moves.
        renderer.Demand.Hysteresis = 0f;

        for (var frame = 0; frame < 8; frame++) {
            Move(loop, entity, frame % 2 == 0 ? over : under);
            Frame(loop, renderer);
        }

        Assert.True(renderer.Demand.Promotions > promotions, "no rung was ever promoted with the band off");
        Assert.True(renderer.Demand.Demotions > demotions, "no rung was ever demoted with the band off");
    }

    /// <summary>
    ///     With no pool there is no survey, no width and nothing per frame — exactly as it was.
    /// </summary>
    /// <remarks>
    ///     <see cref="AssetTextureStreamingTests.WithNoPoolATextureIsLoadedWholeExactlyAsItAlwaysWas" />
    ///     makes the claim about the recorded commands; this makes it about the one thing that was
    ///     added afterwards. A renderer whose textures are loaded whole builds no
    ///     <see cref="TextureDemand" /> at all, so the object list is not walked and no want is said.
    /// </remarks>
    [Fact]
    public void WithNoPoolNothingIsSurveyedAtAll() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out _, pool: 0);

        Place(loop, Near, Vector3.Zero);
        Settle(loop, renderer);

        Assert.NotNull(renderer.Painted);
        Assert.Null(renderer.Painted!.Streaming);
        Assert.Null(renderer.Demand);

        for (var frame = 0; frame < 8; frame++) {
            Frame(loop, renderer);
        }

        Assert.Equal(0L, renderer.Painted.StreamingSwaps);
        Assert.Equal(0L, renderer.Painted.StreamingRefusals);
    }

    /// <summary>
    ///     A frame with no height surveys nothing rather than wanting the smallest level of
    ///     everything.
    /// </summary>
    /// <remarks>
    ///     ⚠ The failure this rules out is silent and is a quality drop: with no pixels there is no
    ///     texel count, and a survey that ran anyway would ask for zero texels — which means <em>the
    ///     smallest level</em> — for every texture in the scene. Wanting nothing leaves each texture
    ///     in the branch it was in before any of this existed.
    /// </remarks>
    [Fact]
    public void AFrameWithNoHeightSizesNothing() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out _);

        var entity = Place(loop, Near, Vector3.Zero);

        Settle(loop, renderer);

        var radius = loop.World.Read<RenderHandle>(entity).Local.Radius;

        Move(loop, entity, DistanceFor(radius, 200f));
        Frame(loop, renderer);

        Assert.Equal(1, renderer.Demand!.Sized);

        renderer.Host.FrameSize = default;
        Frame(loop, renderer);

        Assert.Equal(0, renderer.Demand.Sized);
    }

    // --- The world ----------------------------------------------------------

    static readonly AssetReference Rock = new(new AssetId(Guid.NewGuid()), SubAssetId.Main);
    static readonly AssetReference Near = new(new AssetId(Guid.NewGuid()), SubAssetId.Main);
    static readonly AssetReference Far = new(new AssetId(Guid.NewGuid()), SubAssetId.Main);
    static readonly AssetReference Bark = new(new AssetId(Guid.NewGuid()), SubAssetId.Main);

    /// <summary>How far away an object of a radius has to be to want a texture of a width.</summary>
    /// <remarks>
    ///     <see cref="TextureStreamer.WantedWidth(in BoundingSphere, RenderView, float)" /> rearranged.
    ///     The view's <c>ScreenHeightScale</c> is one here — a ninety-degree field of view — so it
    ///     drops out.
    /// </remarks>
    static float DistanceFor(float radius, float width) => radius * Height / width;

    /// <summary>The width the last frame settled on for the shared texture.</summary>
    static int Width(WorldRenderer renderer) {
        Assert.True(renderer.Demand!.TryGetWidth(Bark, out var width));
        return width;
    }

    /// <summary>Creates an entity painted in a material, at the origin.</summary>
    static Entity Place(EngineLoop loop, AssetReference material, Vector3 at) {
        var entity = loop.World.Create();

        loop.World.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(at) });
        loop.World.Add(entity, new MeshRenderable { Mesh = Rock, Material = material });

        return entity;
    }

    /// <summary>Moves one down the axis the camera looks along.</summary>
    static void Move(EngineLoop loop, Entity entity, float distance) =>
        loop.World.Set(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(new(0f, 0f, -distance)) });

    /// <summary>Runs frames until the streamed texture's resident level is the one asked for.</summary>
    /// <remarks>
    ///     Stream number zero because there is one streamed texture in this content and it is the
    ///     first thing registered. Sleeping rather than spinning: a page arrives on a task, and a
    ///     loop that only records frames never yields to the one carrying the bytes.
    /// </remarks>
    void Resident(EngineLoop loop, WorldRenderer renderer, int level) {
        var streaming = renderer.Painted!.Streaming!;

        for (var frame = 0; frame < 600; frame++) {
            Frame(loop, renderer);

            if (streaming.Textures > 0 && streaming.ResidentLevel(0) <= level) {
                // One more, so the swap the arrival caused is recorded before the count is read.
                Frame(loop, renderer);
                return;
            }

            Thread.Sleep(5);
        }

        Assert.Fail($"the texture never became resident down to level {level}");
    }

    /// <summary>Runs frames until the demand and the swaps have held still for several of them.</summary>
    /// <remarks>
    ///     What "settled" has to mean before a churn count is read: a page that arrived is a promotion
    ///     the next frame and a swap the frame after, so the three numbers stop moving one after the
    ///     other rather than together. Five quiet frames rather than one, and a cap that fails loudly
    ///     — a demand that never stops moving with the camera still is the defect this file is about,
    ///     and it should say so here rather than as a count that is two too high further down.
    /// </remarks>
    void Quiet(EngineLoop loop, WorldRenderer renderer) {
        var demand = renderer.Demand!;
        var painted = renderer.Painted!;
        var quiet = 0;

        var last = (demand.Promotions, demand.Demotions, painted.StreamingSwaps);

        for (var frame = 0; frame < 600 && quiet < 5; frame++) {
            Frame(loop, renderer);

            var now = (demand.Promotions, demand.Demotions, painted.StreamingSwaps);

            quiet = now == last ? quiet + 1 : 0;
            last = now;

            Thread.Sleep(1);
        }

        Assert.True(quiet >= 5, $"the demand never settled: {last}");
    }

    /// <summary>Runs one frame of the loop and one of the renderer.</summary>
    void Frame(EngineLoop loop, WorldRenderer renderer) {
        loop.Frame(TimeSpan.FromMilliseconds(16));

        var list = device.BeginCommandList();

        renderer.Draw(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    /// <summary>
    ///     Runs frames until every entity has a render object and every material has its textures.
    /// </summary>
    /// <remarks>
    ///     A mesh lands one frame after it is asked for, a material the same, and the compiled
    ///     material's texture list one frame after that — so nothing here can be asserted on the
    ///     first frame, and a count that has to grow to keep this passing is a load that has stopped
    ///     being asynchronous.
    /// </remarks>
    void Settle(EngineLoop loop, WorldRenderer renderer) {
        for (var frame = 0; frame < 32; frame++) {
            Frame(loop, renderer);

            if (renderer.Extraction!.ObjectCount > 0 && renderer.Demand?.Sized > 0) {
                return;
            }

            Thread.Sleep(5);
        }

        // A renderer with no pool has no survey and nothing to wait for beyond the objects.
        if (renderer.Demand is null && renderer.Extraction!.ObjectCount > 0) {
            return;
        }

        Assert.Fail("the world never reached a frame with a sized texture in it");
    }

    /// <summary>A renderer over mounted content, looking down the negative Z axis.</summary>
    WorldRenderer Build(EngineLoop loop, out RenderView view, int pool = 8) {
        var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);

        // ⚠ Before Mount, because the pool is sized when the texture source is built.
        renderer.Textures = new() { PoolMegabytes = pool };

        view = new("camera");

        // A ninety-degree vertical field of view, so ScreenHeightScale is exactly one and every
        // distance in this file is a plain ratio.
        const float Fov = MathF.PI / 2f;

        view.Camera = new RenderCamera(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY, Fov, 1f, 0.1f, 4096f);
        view.ScreenHeightScale = 1f / MathF.Tan(Fov * 0.5f);

        renderer.Host.Builder.Views["Camera"] = view;
        renderer.Host.Load(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));
        renderer.Host.FrameSize = new(Height, Height);
        renderer.Mount(Mounted());

        var stages = renderer.Host.Builder.Stages.Values.Aggregate(
            RenderStageMask.None,
            (mask, stage) => mask | stage.Mask
        );

        renderer.Register(loop, stages);

        return renderer;
    }

    /// <summary>
    ///     A content manager holding a mesh, one streamable texture, and two materials sampling it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>1024 square, and that is not for realism.</b> A texture is streamed only when its
    ///     level data exceeds one 64 KiB page, and the rungs this file crosses have to land on
    ///     <em>different mip levels</em> or a swap could never happen however much the width moved.
    ///     At 1024 an <c>R8</c> chain is twenty-two pages and 256 and 512 texels are two levels apart.
    /// </remarks>
    static AssetManager Mounted() {
        var files = new VirtualFileSystem();
        var storage = new MemoryFileProvider();

        files.Mount(new("/store"), storage);
        files.Mount(new("/bundles"), storage);

        var backend = new FileOdbBackend(files, new("/store/odb"));
        var database = new ObjectDatabase(backend);

        var mesh = database.Write(
            new MeshData {
                Name = "rock",
                Positions = [new(-1f, -1f, 0f), new(1f, -1f, 0f), new(1f, 1f, 0f)],
                Normals = [new(0f, 0f, 1f), new(0f, 0f, 1f), new(0f, 0f, 1f)],
                TexCoords = [new(0f, 0f), new(1f, 0f), new(1f, 1f)],
                Indices = [0, 1, 2]
            }
        );

        var pixels = new TextureData(PixelFormat.R8UNorm, 1024, 1024);

        for (var level = 0; level < pixels.LevelCount; level++) {
            pixels.LevelSpan(level).Fill((byte)(0x10 + level));
        }

        var texture = database.WriteRaw(
            ContentHash.TypeId(typeof(TextureData)),
            [],
            Ktx2.Write(pixels),
            CompressionMethod.None
        );

        // Two materials naming one texture, which is what makes the maximum a claim about the
        // texture rather than about a material.
        var near = database.Write(
            new MaterialContent {
                Features = [new TexturedMetalRoughnessFeature()],
                Textures = [new("baseColorMap", Bark)]
            }
        );

        var far = database.Write(
            new MaterialContent {
                Features = [new TexturedMetalRoughnessFeature()],
                Textures = [new("baseColorMap", Bark)]
            }
        );

        var bundle = new BundleWriter();
        bundle.AddAll(backend);

        using (var target = files.OpenWrite(new("/bundles/Main.bundle"))) {
            target.Write(bundle.Build());
        }

        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            default,
            "Windows",
            [
                new("bark", texture, "Main", ContentProvider.Local, [], [], 0, Reference: Bark),
                new("near", near, "Main", ContentProvider.Local, [], [], 0, Reference: Near),
                new("far", far, "Main", ContentProvider.Local, [], [], 0, Reference: Far),
                new("rock", mesh, "Main", ContentProvider.Local, [], [], 0, Reference: Rock)
            ],
            [new("Main", "", default, 0, 0, CompressionMethod.None, [])]
        );

        return new(catalog, new LocalBundleSource(files, new("/bundles")));
    }

    /// <inheritdoc />
    public void Dispose() => device.Dispose();
}
