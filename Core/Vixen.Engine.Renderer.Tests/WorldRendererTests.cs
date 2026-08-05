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
///     An entity carrying a mesh reference is drawn.
/// </summary>
/// <remarks>
///     <para>
///         <b>The sentence <c>MeshRenderable</c> used to end with was "authored, saved, compiled,
///         loaded and invisible."</b> Everything either side of the reference was finished — the
///         catalog resolved it, the manager loaded it, the extraction system reconciled entities into
///         render objects — and between them stood one function returning an empty mesh. This is the
///         test that the sentence is no longer true.
///     </para>
///     <para>
///         The source is a fake rather than an <c>AssetManager</c>, deliberately. What is being asserted
///         is the <em>protocol</em> — ask, be told "not yet", ask again, draw when it lands — and a real
///         manager would answer instantly from memory and never exercise the case the whole design is
///         about.
///     </para>
/// </remarks>
public sealed class WorldRendererTests : IDisposable {
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

    /// <summary>
    ///     A mesh that has not loaded is waited for, and drawn on the frame it arrives.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both halves matter. That an unloaded mesh is <em>waited</em> for rather than dropped is
    ///         what makes an asynchronous load safe inside a frame: the entity keeps no render handle, so
    ///         next frame's reconciliation asks again, and nothing needs a queue. That it is drawn when
    ///         it lands is the join itself.
    ///     </para>
    ///     <para>
    ///         The retry is the part that would rot silently. An extraction that gave up on the first
    ///         miss would draw every mesh in a level exactly never, and a level whose meshes happened to
    ///         be in memory already would still look right in every test.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_mesh_reference_is_waited_for_and_then_drawn() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out var source);

        var entity = loop.World.Create();

        loop.World.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });
        loop.World.Add(entity, new MeshRenderable { Mesh = Reference });

        // Frame one: the mesh is not here, so the entity is counted and left alone.
        Frame(loop, renderer);

        Assert.Equal(1, renderer.Extraction!.Waiting);
        Assert.Equal(0, renderer.Extraction.ObjectCount);
        Assert.Equal(1, source.Asked);

        // It arrives, and the very next reconciliation draws it — without anything having told the
        // extraction that it had.
        source.Deliver();
        Frame(loop, renderer);

        Assert.Equal(0, renderer.Extraction.Waiting);
        Assert.Equal(1, renderer.Extraction.ObjectCount);
        Assert.Equal(1, renderer.Host.System.Objects.Count);
    }

    /// <summary>
    ///     A renderer with no source draws no referenced mesh, and says how many it did not draw.
    /// </summary>
    /// <remarks>
    ///     What a project with no content mounted is. Worth a number rather than a silence: an empty
    ///     screen with a scene full of entities is otherwise indistinguishable from a camera pointing
    ///     the wrong way.
    /// </remarks>
    [Fact]
    public void Without_a_source_nothing_referenced_is_drawn() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out _, mounted: false);

        var entity = loop.World.Create();

        loop.World.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });
        loop.World.Add(entity, new MeshRenderable { Mesh = Reference });

        Frame(loop, renderer);

        Assert.Equal(1, renderer.Extraction!.Waiting);
        Assert.Equal(0, renderer.Extraction.ObjectCount);
    }

    /// <summary>
    ///     One mesh drawn by two entities is one slice of the shared buffer.
    /// </summary>
    /// <remarks>
    ///     The whole point of a residency cache, and the thing a per-entity load would quietly undo: a
    ///     thousand instances of a rock is one rock's geometry. Asserted by asking the source how many
    ///     times it was asked, because the buffer's own occupancy would also be one if the second entity
    ///     had simply failed.
    /// </remarks>
    [Fact]
    public void Two_entities_drawing_one_mesh_share_its_geometry() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out var source);

        foreach (var offset in (float[])[0f, 4f]) {
            var entity = loop.World.Create();

            loop.World.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(new(offset, 0f, 0f)) });
            loop.World.Add(entity, new MeshRenderable { Mesh = Reference });
        }

        source.Deliver();
        Frame(loop, renderer);

        Assert.Equal(2, renderer.Extraction!.ObjectCount);

        // One slice, two claims on it. Asked twice because reconciliation is per entity, and that is
        // exactly why the count that matters is the residency's rather than the source's.
        Assert.Equal(1, renderer.Residency.Count);
        Assert.Equal(2, renderer.Residency.ClaimsOn(GeometryKey.Of(Reference)));
        Assert.Equal(2, source.Delivered);
    }

    /// <summary>
    ///     A world built from mounted content draws a mesh, in its own material, with its own texture.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The end of the shippability chain, asserted in one place.</b> Everything else in this
    ///         file uses a fake source to pin one protocol; this uses the content manager a game has, the
    ///         chunks a build writes, and <see cref="WorldRenderer.Draw" /> rather than the host's — so
    ///         the material reaches the entity, the texture reaches the material, and the pixels reach a
    ///         command list, all through the calls an application actually makes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Frames, not calls.</b> A mesh lands one frame after it is asked for, a material the
    ///         same, and a texture one frame after that — so the loop runs several times, which is
    ///         exactly what a level start looks like. A test that asked once would be asserting that
    ///         loading is synchronous, which is the thing this design refuses to be.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AMountedWorldDrawsItsMeshInItsOwnMaterial() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out _, mounted: false);

        renderer.Mount(Mounted());

        var entity = loop.World.Create();

        loop.World.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });
        loop.World.Add(entity, new MeshRenderable { Mesh = Rock, Material = Painted });

        // Long enough for three asynchronous hops and no longer: a count that has to grow to keep this
        // passing is a load that has stopped being asynchronous and started being slow.
        for (var frame = 0; frame < 16 && renderer.Extraction!.ObjectCount == 0; frame++) {
            loop.Frame(TimeSpan.FromMilliseconds(16));

            var commands = device.BeginCommandList();

            renderer.Draw(commands);
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);

            Thread.Sleep(10);
        }

        Assert.Equal(1, renderer.Extraction!.ObjectCount);

        // Its own material, not the host's — which is the whole of gap two.
        var id = loop.World.Read<RenderHandle>(entity).Object;
        var index = renderer.Host.System.Objects.Data.Data(renderer.Materials.MaterialIndex)[id.Index];

        Assert.True(index > 0);
        Assert.NotSame(renderer.Extraction.Material, renderer.Materials.Materials[index]);

        // And its own texture, which is gap three: the view is in the material's parameters under the
        // name its feature samples, so the feature has something to give a table slot to.
        for (var frame = 0; frame < 8; frame++) {
            var commands = device.BeginCommandList();

            renderer.Draw(commands);
            commands.Finish();

            Thread.Sleep(10);
        }

        var key = ParameterKeys.New<TextureViewHandle>("baseColorMap");
        var material = renderer.Materials.Materials[index];

        Assert.True(material.Parameters.Has(key));
        Assert.True(material.Parameters.Get(key).IsValid);
    }

    /// <summary>Mounting content before registering the systems wires them, and so does after.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two orders, both legitimate, and each needs its own line of wiring.</b>
    ///         <see cref="WorldRenderer.Register" /> passes whatever sources exist to the extraction it
    ///         creates, and <see cref="WorldRenderer.Mount" /> pushes them into whatever extraction
    ///         exists — so a project that mounts its content at start-up and one that mounts it when a
    ///         level loads both work.
    ///     </para>
    ///     <para>
    ///         ⚠ Asserted directly rather than through a drawn frame, because a frame only ever exercises
    ///         one of the two: the surviving line covers for the missing one and the test passes with half
    ///         the wiring gone.
    ///     </para>
    /// </remarks>
    [Fact]
    public void MountingEitherSideOfRegisteringWiresTheSources() {
        using var loop = new EngineLoop();

        // Mounted after, which is what Build does: Register created the extraction with no sources and
        // Mount has to push them in.
        using (var late = Build(loop, out _, mounted: false)) {
            late.Mount(Mounted());

            Assert.NotNull(late.Extraction!.Meshes);
            Assert.NotNull(late.Extraction.Materials);
        }

        // And mounted before, where Register is the one that has to carry them over.
        using var early = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);

        early.Host.Builder.Views["Camera"] = new("camera");
        early.Host.Load(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));
        early.Mount(Mounted());
        early.Register(loop, RenderStageMask.None);

        Assert.NotNull(early.Extraction!.Meshes);
        Assert.NotNull(early.Extraction.Materials);
    }

    /// <summary>
    ///     The table is built to the same capacity the effect path's set-4 layout states.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two numbers that have to be one.</b> The table owns a descriptor set allocated from a
    ///         layout of its own length; <c>MeshRenderFeature</c> and <c>GpuClusterResolve</c> bind that
    ///         set against pipelines whose set-4 layout <see cref="EffectLoader" /> built to
    ///         <see cref="EffectLoader.BindlessCapacity" />. Different lengths are incompatible
    ///         descriptor set layouts — undefined behaviour, and the kind a driver is free to tolerate,
    ///         which is exactly what MoltenVK did while this renderer asked for the device's ceiling.
    ///     </para>
    ///     <para>
    ///         Asserted against the loader's own default rather than against the literal, so the test
    ///         fails on the drift rather than on the number: a project moving the constant moves both
    ///         sides and this stays green, and a project moving one side alone finds out here.
    ///     </para>
    ///     <para>
    ///         ⚠ The device's ceiling is what the failure looked like — <see cref="NullDevice" /> reports
    ///         five hundred thousand, so the pre-fix renderer produced a table of that and this assertion
    ///         is what distinguishes the two.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheTableIsSizedToWhatTheEffectPathStates() {
        using var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);

        Assert.NotNull(renderer.Table);
        Assert.Equal(new EffectLoader(device).BindlessCapacity, renderer.Table.Capacity);
        Assert.NotEqual(device.Features.MaxBindlessDescriptors, renderer.Table.Capacity);
    }

    /// <summary>
    ///     A hundred frames of the same shape settle at one set per frame in flight.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The leak <see cref="DescriptorAllocator" />'s own doc comment describes, at the level
    ///         that was missing it.</b> That the allocator recycles is already asserted where it lives;
    ///         what nothing asserted is that anything ever tells it a frame has begun.
    ///         <see cref="WorldRenderer.MaterialDescriptors" /> is the frame's, no other type can reach
    ///         it, and <see cref="WorldRenderer.Draw" /> is the only call in a frame that is both after
    ///         <see cref="IGraphicsDevice.BeginFrame" /> and before the first pass allocates — so if
    ///         that call is not there, it is nowhere, and every frame's sets are handed out and never
    ///         given back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The frame allocates here rather than through a pass, and it has to.</b> A set is
    ///         allocated against a resolved shader's layout, and nothing resolves against
    ///         <see cref="NullDevice" /> without a compiled effect — so a fixture that waited for a pass
    ///         to ask would assert against zero sets and pass whatever <c>Draw</c> did. This asks the
    ///         renderer's allocator for exactly what a pass asks it for, at the point in the frame a
    ///         pass asks.
    ///     </para>
    ///     <para>
    ///         The write differs frame to frame because a real one does: a per-view block moves through
    ///         <c>EffectConstants</c>' ring as its camera moves, so the offset a set is written with is
    ///         not last frame's. That is what makes the missed call visible — an unchanging frame would
    ///         be answered from a stale cache and settle at one set, which looks like health.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheFramesDescriptorSetsAreRecycledRatherThanAccumulated() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out _, mounted: false);

        var layout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerView,
                [new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex)],
                "view"
            )
        );

        var block = device.CreateBuffer(new(4096, BufferUsage.Uniform, Name: "Block"));
        var descriptors = renderer.MaterialDescriptors;
        var settled = 0;

        for (var frame = 0; frame < 64; frame++) {
            device.BeginFrame();

            var list = device.BeginCommandList();

            renderer.Draw(list);
            descriptors.Allocate(layout, [DescriptorWrite.Uniform(0, block, frame % 16 * 256, 256)]);

            list.Finish();
            device.GraphicsQueue.Submit([list]);
            device.EndFrame();

            if (frame == descriptors.FramesInFlight - 1) {
                settled = descriptors.SetCount;
            }
        }

        // One set per frame in flight, reached within that many frames and never exceeded. A renderer
        // that never begins the frame creates one per distinct write instead, which is a number that
        // depends on how long the run was rather than on how deep the ring is.
        Assert.Equal(descriptors.FramesInFlight, settled);
        Assert.Equal(settled, descriptors.SetCount);
    }

    /// <summary>
    ///     A set asked for in two frames is written in both, not answered from the first.
    /// </summary>
    /// <remarks>
    ///     The worse half of the same missed call, and the half that is not a leak: the cache is
    ///     content-addressed over handles that name transient graph memory, and the next frame's graph
    ///     is free to give that memory to something else. A cache that outlived its frame would hand
    ///     frame <c>N + 1</c> a set still pointing at frame <c>N</c>'s attachments — a frame that draws
    ///     the wrong texture rather than one that fails.
    /// </remarks>
    [Fact]
    public void TheDescriptorCacheDoesNotSurviveTheFrame() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out _, mounted: false);

        var layout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerView,
                [new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex)],
                "view"
            )
        );

        var block = device.CreateBuffer(new(256, BufferUsage.Uniform, Name: "Block"));
        var descriptors = renderer.MaterialDescriptors;

        for (var frame = 0; frame < 4; frame++) {
            device.BeginFrame();

            var list = device.BeginCommandList();

            renderer.Draw(list);
            descriptors.Allocate(layout, [DescriptorWrite.Uniform(0, block, 0, 256)]);

            list.Finish();
            device.GraphicsQueue.Submit([list]);
            device.EndFrame();

            // Each frame writes its own set. Reuse within a frame is the point of the cache and is
            // fine; reuse across one is a descriptor nobody rewrote, and the counters are per frame
            // only because BeginFrame resets them.
            Assert.Equal(1, descriptors.WriteCount);
            Assert.Equal(0, descriptors.ReuseCount);
        }
    }

    /// <summary>
    ///     A texture the frame let go of gets its table slot back.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="BindlessTable" />'s per-frame call, missed in the same place and for the same
    ///         reason. It costs no correctness — a released index simply stays in the ring — so the
    ///         failure is a level that streams textures in and out walking the high-water mark up to
    ///         <see cref="BindlessTable.Capacity" /> and then refusing a texture on a machine with
    ///         descriptors to spare.
    ///     </para>
    ///     <para>
    ///         Slot zero belongs to the missing-map fallback for the life of the renderer, so the first
    ///         index a test sees is one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheBindlessTableGetsItsReleasedSlotsBack() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out _, mounted: false);

        Assert.NotNull(renderer.Table);

        var streamed = View("streamed");
        var index = renderer.Table.Add(streamed);

        Assert.True(renderer.Table.Remove(streamed));

        // The ring comes back round to the slot the release went into on the frame after the last one
        // that could still be naming it.
        for (var frame = 0; frame < renderer.Table.FramesInFlight; frame++) {
            DrawOne(renderer);
        }

        Assert.Equal(index, renderer.Table.Add(View("replacement")));
        Assert.Equal((int)index + 1, renderer.Table.HighWaterMark);
    }

    /// <summary>Runs one device frame through the renderer, as an application's does.</summary>
    void DrawOne(WorldRenderer renderer) {
        device.BeginFrame();

        var list = device.BeginCommandList();

        renderer.Draw(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
        device.EndFrame();
    }

    /// <summary>A view of a texture, for giving the table something to hold.</summary>
    TextureViewHandle View(string name) =>
        device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.Sampled, Name: name))
        );

    static readonly AssetReference Rock = new(new AssetId(Guid.NewGuid()), SubAssetId.Main);
    static readonly AssetReference Painted = new(new AssetId(Guid.NewGuid()), SubAssetId.Main);
    static readonly AssetReference Bark = new(new AssetId(Guid.NewGuid()), SubAssetId.Main);

    /// <summary>A content manager holding a mesh, a material and the texture it samples.</summary>
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
                Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
                Normals = [new(0f, 0f, 1f), new(0f, 0f, 1f), new(0f, 0f, 1f)],
                TexCoords = [new(0f, 0f), new(1f, 0f), new(0f, 1f)],
                Indices = [0, 1, 2]
            }
        );

        var pixels = new TextureData(PixelFormat.Rgba8UNorm, 2, 2);

        pixels.PixelSpan().Fill(0x40);

        var texture = database.WriteRaw(
            ContentHash.TypeId(typeof(TextureData)),
            [],
            Ktx2.Write(pixels),
            CompressionMethod.None
        );

        var material = database.Write(
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
                new("granite", material, "Main", ContentProvider.Local, [], [], 0, Reference: Painted),
                new("rock", mesh, "Main", ContentProvider.Local, [], [], 0, Reference: Rock)
            ],
            [new("Main", "", default, 0, 0, CompressionMethod.None, [])]
        );

        return new(catalog, new LocalBundleSource(files, new("/bundles")));
    }

    /// <summary>
    ///     The reference every entity here draws.
    /// </summary>
    /// <remarks>
    ///     A field and not a property, which is not a style point: as a property returning
    ///     <c>AssetId.New()</c> it handed every entity a <em>different</em> mesh, and the sharing test
    ///     below is precisely the one that would have passed anyway while asserting nothing.
    /// </remarks>
    static readonly AssetReference Reference = new(AssetId.New(), SubAssetId.Main);

    /// <summary>Runs one frame of the loop and one of the renderer.</summary>
    void Frame(EngineLoop loop, WorldRenderer renderer) {
        loop.Frame(TimeSpan.FromMilliseconds(16));

        var list = device.BeginCommandList();

        renderer.Host.Draw(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    WorldRenderer Build(EngineLoop loop, out DeferredMeshSource source, bool mounted = true) {
        var renderer = new WorldRenderer(device, effects, vertexCapacity: 4096, indexCapacity: 8192);

        renderer.Host.Builder.Views["Camera"] = new("camera");
        renderer.Host.Load(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));
        renderer.Host.FrameSize = new(64, 64);

        source = new();

        if (mounted) {
            renderer.Source = source;
        }

        var stages = renderer.Host.Builder.Stages.Values.Aggregate(
            RenderStageMask.None,
            (mask, stage) => mask | stage.Mask
        );

        renderer.Register(loop, stages);

        return renderer;
    }

    /// <summary>A source that answers "not yet" until it is told to answer.</summary>
    /// <remarks>
    ///     The behaviour a real content manager has and a test fixture usually does not: an in-memory
    ///     one answers on the first ask, which is the one case the retry path never sees.
    /// </remarks>
    sealed class DeferredMeshSource : IMeshSource {
        bool ready;

        public int Asked { get; private set; }

        public int Delivered { get; private set; }

        public void Deliver() => ready = true;

        public bool TryGet(AssetReference reference, out MeshData mesh) {
            Asked++;
            mesh = null!;

            if (!ready) {
                return false;
            }

            Delivered++;
            mesh = Quad();

            return true;
        }

        static MeshData Quad() =>
            new() {
                Name = "quad",
                Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
                Normals = [new(0f, 0f, 1f), new(0f, 0f, 1f), new(0f, 0f, 1f)],
                TexCoords = [new(0f, 0f), new(1f, 0f), new(0f, 1f)],
                Indices = [0, 1, 2]
            };
    }

    /// <inheritdoc />
    public void Dispose() => device.Dispose();
}
