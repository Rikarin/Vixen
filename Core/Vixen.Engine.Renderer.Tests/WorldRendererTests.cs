// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
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
