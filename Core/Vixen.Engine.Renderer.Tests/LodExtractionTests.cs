// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Ecs;
using Vixen.Engine.Frames;
using Vixen.Engine.Renderer;
using Vixen.Engine.Transforms;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Ecs;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     An authored LOD group is respected by the frame.
/// </summary>
/// <remarks>
///     <para>
///         <b><c>LodRenderFeature</c> was complete, tested and named by neither renderer.</b> Nothing
///         in the engine called <c>Add</c>, so no group was ever registered, so nothing was ever
///         switched or hidden — a scene authored with a three-level rock drew all three of them on
///         top of each other at every distance, with every counter reading healthy. This file is the
///         test that the sentence is no longer true, and it asserts it through the calls a game makes
///         rather than by constructing the feature: <c>WorldRenderer</c>, <c>Register</c>, an
///         <c>EngineLoop</c> and entities.
///     </para>
///     <para>
///         ⚠ <b>Primitives rather than mesh references, and that is what keeps this test about LOD.</b>
///         A <c>PrimitiveShape</c> needs no asset source and no residency round trip, so a level is
///         extracted on the frame it appears and the only asynchrony left in the test is the one being
///         asserted.
///     </para>
/// </remarks>
public sealed class LodExtractionTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    const float FieldOfView = MathF.PI / 3f;

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
    ///     A near group shows its finest level and the frame hides the other two.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The assertion is that the coarse levels are <em>hidden</em>, not that the fine one is
    ///     drawn.</b> Every level of this group is at the same place and the same size, so "the finest
    ///     one is visible" was true before any of this existed — it is the two bits that are cleared
    ///     that say a group was registered and a level was chosen.
    /// </remarks>
    [Fact]
    public void AnAuthoredGroupShowsOneLevelAndHidesTheRest() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out var camera);

        var levels = Group(loop.World, new(0f, 0f, -3f), [0.5f, 0.1f]);

        Frame(loop, renderer);

        Assert.Equal(1, renderer.LodExtraction!.GroupCount);
        Assert.Equal(3, renderer.LodExtraction.Assigned);

        var objects = Objects(loop.World, levels);

        Assert.True(renderer.Host.System.Visibility.IsVisible(camera.Index, objects[0]));
        Assert.False(renderer.Host.System.Visibility.IsVisible(camera.Index, objects[1]));
        Assert.False(renderer.Host.System.Visibility.IsVisible(camera.Index, objects[2]));
    }

    /// <summary>
    ///     The same group, far away, shows its coarsest level instead.
    /// </summary>
    /// <remarks>
    ///     The other half of the same claim, and the one that distinguishes "a group was registered"
    ///     from "level 0 happens to win": nothing about the wiring changes between these two tests,
    ///     only where the object is.
    /// </remarks>
    [Fact]
    public void TheSameGroupFarAwayShowsItsCoarsestLevel() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out var camera);

        var levels = Group(loop.World, new(0f, 0f, -400f), [0.5f, 0.1f]);

        Frame(loop, renderer);

        var objects = Objects(loop.World, levels);

        Assert.False(renderer.Host.System.Visibility.IsVisible(camera.Index, objects[0]));
        Assert.False(renderer.Host.System.Visibility.IsVisible(camera.Index, objects[1]));
        Assert.True(renderer.Host.System.Visibility.IsVisible(camera.Index, objects[2]));
    }

    /// <summary>
    ///     A child whose parent carries no thresholds is in no group and is drawn.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The predicate that could not be false without this.</b> Every other assertion here is
    ///     "a bit was cleared", and a producer that put every <c>LodLevel</c> in one group — or in
    ///     group zero — would satisfy them. A level dragged out of its group has to stop being a
    ///     level, and the only evidence of that is an object that stays visible while carrying a
    ///     <c>LodLevel</c> of 2.
    /// </remarks>
    [Fact]
    public void ALevelWhoseParentIsNotAGroupIsDrawn() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out var camera);

        var orphan = loop.World.Create();

        loop.World.Add(orphan, new WorldTransform { Value = Matrix4x4.Identity });

        var level = Level(loop.World, orphan, new(0f, 0f, -400f), 2);

        Frame(loop, renderer);

        Assert.Equal(0, renderer.LodExtraction!.GroupCount);
        Assert.Equal(0, renderer.LodExtraction.Assigned);

        Assert.True(
            renderer.Host.System.Visibility.IsVisible(camera.Index, loop.World.Read<RenderHandle>(level).Object)
        );
    }

    /// <summary>
    ///     Two groups sharing a threshold list choose their levels independently.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Why the group's identity is the parent entity and not the numbers.</b> Interning by
    ///     thresholds would make every rock in a level one group — <c>LodRenderFeature</c> chooses one
    ///     level per group per view — so a near rock and a far one would be locked to whichever of
    ///     them the walk reached last. Two groups at two distances, and each gets its own answer.
    /// </remarks>
    [Fact]
    public void TwoGroupsWithTheSameThresholdsDecideSeparately() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out var camera);

        var near = Group(loop.World, new(0f, 0f, -3f), [0.5f, 0.1f]);
        var far = Group(loop.World, new(0f, 0f, -400f), [0.5f, 0.1f]);

        Frame(loop, renderer);

        Assert.Equal(2, renderer.LodExtraction!.GroupCount);
        Assert.Equal(6, renderer.LodExtraction.Assigned);

        var nearObjects = Objects(loop.World, near);
        var farObjects = Objects(loop.World, far);

        Assert.True(renderer.Host.System.Visibility.IsVisible(camera.Index, nearObjects[0]));
        Assert.False(renderer.Host.System.Visibility.IsVisible(camera.Index, farObjects[0]));
        Assert.True(renderer.Host.System.Visibility.IsVisible(camera.Index, farObjects[2]));
    }

    /// <summary>
    ///     A group whose parent is destroyed gives its slot back to the next one.
    /// </summary>
    /// <remarks>
    ///     What stops a level that streams in and out walking the feature's group list up for ever.
    ///     The number that says so is the feature's own <c>Groups</c> count, because the producer's
    ///     <c>GroupCount</c> would read one either way.
    /// </remarks>
    [Fact]
    public void AGroupWhoseParentDiesReleasesItsSlot() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out _);

        var first = Group(loop.World, new(0f, 0f, -3f), [0.5f, 0.1f]);

        Frame(loop, renderer);

        // The sentinel "no LOD" group plus this one.
        Assert.Equal(2, renderer.Lods.Groups.Count);

        loop.World.Destroy(loop.World.Read<Parent>(first[0]).Value);

        foreach (var level in first) {
            loop.World.Destroy(level);
        }

        Frame(loop, renderer);

        Assert.Equal(0, renderer.LodExtraction!.GroupCount);

        Group(loop.World, new(0f, 0f, -3f), [0.5f, 0.1f]);
        Frame(loop, renderer);

        Assert.Equal(1, renderer.LodExtraction.GroupCount);
        Assert.Equal(2, renderer.Lods.Groups.Count);
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Fixture ------------------------------------------------------------

    /// <summary>A parent carrying thresholds and three children carrying levels 0, 1 and 2.</summary>
    static Entity[] Group(World world, Vector3 at, float[] thresholds) {
        var parent = world.Create();

        world.Add(parent, new WorldTransform { Value = Matrix4x4.Identity });
        world.Add(parent, new LodGroupComponent { Thresholds = thresholds });

        return [Level(world, parent, at, 0), Level(world, parent, at, 1), Level(world, parent, at, 2)];
    }

    /// <summary>One level of a group: a cube at a place, hanging off a parent.</summary>
    static Entity Level(World world, Entity parent, Vector3 at, int level) {
        var entity = world.Create();

        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(at) });
        world.Add(entity, new PrimitiveShape { Kind = PrimitiveKind.Cube });
        world.Add(entity, new Parent { Value = parent });
        world.Add(entity, new LodLevel { Level = level });

        return entity;
    }

    static RenderObjectId[] Objects(World world, Entity[] levels) =>
        [.. levels.Select(level => world.Read<RenderHandle>(level).Object)];

    void Frame(EngineLoop loop, WorldRenderer renderer) {
        loop.Frame(TimeSpan.FromMilliseconds(16));

        var list = device.BeginCommandList();

        renderer.Host.Draw(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    WorldRenderer Build(EngineLoop loop, out RenderView camera) {
        var renderer = new WorldRenderer(device, effects, vertexCapacity: 1 << 16, indexCapacity: 1 << 16);

        // ⚠ A perspective view, because `LodRenderFeature` skips any view whose `ScreenHeightScale`
        // is zero — which is what a shadow cascade and a bare `RenderView` both are. A test built on
        // the default view would pass every "the level is drawn" assertion and none of the others.
        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(FieldOfView, 1f, 0.1f, 10000f);

        camera = new("camera") {
            Position = Vector3.Zero,
            Frustum = new(view * projection),
            ScreenHeightScale = 1f / MathF.Tan(FieldOfView * 0.5f)
        };

        renderer.Host.Builder.Views["Camera"] = camera;
        renderer.Host.Load(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));
        renderer.Host.FrameSize = new(64, 64);

        var stages = renderer.Host.Builder.Stages.Values.Aggregate(
            RenderStageMask.None,
            (mask, stage) => mask | stage.Mask
        );

        renderer.Register(loop, stages);

        return renderer;
    }
}
