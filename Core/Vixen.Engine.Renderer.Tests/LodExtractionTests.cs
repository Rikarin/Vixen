// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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

    /// <summary>Where a group's near fixture sits, and where its far one does.</summary>
    /// <remarks>
    ///     ⚠ <b>Three of these four numbers used to put the near group <em>exactly</em> on its own
    ///     first threshold, and that is the whole of #1189.</b> A unit cube's bounding sphere has
    ///     radius √3⁄2, this camera's <c>ScreenHeightScale</c> is 1⁄tan 30° = √3, and
    ///     <c>LodRenderFeature.Height</c> is radius × scale ÷ distance — so a group at z = −3 covered
    ///     (√3⁄2 × √3) ÷ 3 = <b>0.5 of the viewport, bit for bit</b>, against a first threshold of
    ///     0.5. <c>Choose</c> takes <c>height &gt;= threshold</c>, so the whole test turned on the
    ///     last bit of <c>MathF.Tan(π⁄6)</c> — whose true value sits within a hair of a rounding tie,
    ///     so glibc's <c>tanf</c> answers one ulp above what Windows's and Apple's answer. One ulp
    ///     above flips <c>ScreenHeightScale</c> one ulp below √3, the height to 0.49999997, and the
    ///     near group from level 0 to level 1 — on Linux only, which is exactly the leg it failed on
    ///     and neither of the two it passed. Nothing about LOD was wrong: the fixture was standing on
    ///     the fence. The margin below is 25 % of the threshold, against an error of about 6 × 10⁻⁸.
    /// </remarks>
    static readonly float[] Thresholds = [0.4f, 0.1f];

    /// <summary>The same list, out of order, for the group that has to be refused.</summary>
    static readonly float[] Ascending = [0.1f, 0.4f];

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

        var levels = Group(loop.World, new(0f, 0f, -3f), Thresholds);

        Frame(loop, renderer);

        Assert.Equal(1, renderer.LodExtraction!.GroupCount);
        Assert.Equal(3, renderer.LodExtraction.Assigned);

        var objects = Objects(loop.World, levels);

        Shows(renderer, camera, objects, level: 0);
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

        var levels = Group(loop.World, new(0f, 0f, -400f), Thresholds);

        Frame(loop, renderer);

        var objects = Objects(loop.World, levels);

        Shows(renderer, camera, objects, level: 2);
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

        var orphaned = loop.World.Read<RenderHandle>(level).Object;

        Assert.True(
            renderer.Host.System.Visibility.IsVisible(camera.Index, orphaned),
            "a level whose parent carries no thresholds is in no group, so nothing may hide it: "
            + Shown(renderer, camera, [orphaned])
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

        var near = Group(loop.World, new(0f, 0f, -3f), Thresholds);
        var far = Group(loop.World, new(0f, 0f, -400f), Thresholds);

        Frame(loop, renderer);

        Assert.Equal(2, renderer.LodExtraction!.GroupCount);
        Assert.Equal(6, renderer.LodExtraction.Assigned);

        var nearObjects = Objects(loop.World, near);
        var farObjects = Objects(loop.World, far);

        Shows(renderer, camera, nearObjects, level: 0, "the near group");
        Shows(renderer, camera, farObjects, level: 2, "the far group");
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

        var first = Group(loop.World, new(0f, 0f, -3f), Thresholds);

        Frame(loop, renderer);

        // The sentinel "no LOD" group plus this one.
        Assert.Equal(2, renderer.Lods.Groups.Count);

        loop.World.Destroy(loop.World.Read<Parent>(first[0]).Value);

        foreach (var level in first) {
            loop.World.Destroy(level);
        }

        Frame(loop, renderer);

        Assert.Equal(0, renderer.LodExtraction!.GroupCount);

        Group(loop.World, new(0f, 0f, -3f), Thresholds);
        Frame(loop, renderer);

        Assert.Equal(1, renderer.LodExtraction.GroupCount);
        Assert.Equal(2, renderer.Lods.Groups.Count);
    }

    /// <summary>
    ///     A cross-fade started by a level change ends, because the frame's clock reaches the feature.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>LodRenderFeature.DeltaTime</c> was fed by neither renderer</b>, so
    ///         <c>CrossFadeDuration</c> was unreachable through <c>WorldRenderer</c>: a project that
    ///         set one got a transition whose elapsed time never moved and therefore never ended —
    ///         two levels of one object drawn on top of each other, for ever, from the moment the
    ///         camera crossed a threshold. The default of zero is what kept it invisible, because a
    ///         hard swap looks identical either way.
    ///     </para>
    ///     <para>
    ///         <b>Counted in frames rather than measured in seconds.</b> The loop is handed a fixed
    ///         16 ms per frame, so this is arithmetic rather than a wall-clock budget — and both
    ///         halves are asserted, because the end-of-fade half alone would pass just as well on a
    ///         fade that never started.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The margins are wide because the fade does not advance once per frame.</b>
    ///         <c>LodRenderFeature.Select</c> calls <c>Advance</c> once per <em>visible member</em> of
    ///         the group, and a fade is exactly the state in which two members are visible — so the
    ///         elapsed time grows by roughly twice the frame's delta while a fade is running, and by
    ///         one delta while it is not. That was unobservable while <c>DeltaTime</c> was always
    ///         zero, and it is a defect of its own rather than something to encode here: see
    ///         <see href="https://github.com/Rikarin/Vixen/issues/1183" />. This test asserts the
    ///         order — started, still running, finished — which is true at either rate.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ACrossFadeEndsBecauseTheFramesDeltaReachesTheFeature() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out var camera);

        // A second, against a sixteen-millisecond frame: long enough that "a few frames in it is still
        // fading" cannot be an accident of rounding, short enough that the bound below is not a hang
        // check.
        renderer.Lods.CrossFadeDuration = 1f;

        var levels = Group(loop.World, new(0f, 0f, -3f), Thresholds);

        Frame(loop, renderer);

        var objects = Objects(loop.World, levels);

        Fading(renderer, camera, objects, fine: true, coarse: false, "before the camera moved, only the finest level");

        // The camera retreats until the group is at its coarsest level — the same distance
        // `TheSameGroupFarAwayShowsItsCoarsestLevel` uses, reached by moving the view rather than the
        // object so nothing about the group itself changes.
        Retreat(camera, 397f);
        Frame(loop, renderer);

        // Both ends of the transition are drawn, which is what a cross-fade costs and is the only
        // evidence from outside the feature that one started at all.
        Fading(renderer, camera, objects, fine: true, coarse: true, "the frame the cross-fade started on");

        // Three more frames — a twentieth of the duration at most — and it is still fading. This is
        // the half that cannot be satisfied by a fade that ends immediately, which is what a
        // zero-length one does.
        for (var frame = 0; frame < 3; frame++) {
            Frame(loop, renderer);
        }

        Fading(renderer, camera, objects, fine: true, coarse: true, "three frames into a one-second cross-fade");

        // And well past it, the level it was fading out of is gone. Without the delta this is never
        // true: the elapsed time never grows, so the transition never retires and both levels of the
        // object are drawn on top of each other for the rest of the session.
        for (var frame = 0; frame < 200; frame++) {
            Frame(loop, renderer);
        }

        Fading(renderer, camera, objects, fine: false, coarse: true, "well past the end of the cross-fade");
    }

    /// <summary>Moves the camera back along +Z, rebuilding the frustum it culls with.</summary>
    /// <remarks>
    ///     The frustum as well as the position, because <c>LodRenderFeature</c> only reaches an object
    ///     the visibility pass kept — a camera moved without one culls the group away and every "is
    ///     visible" assertion below would be false for the wrong reason.
    /// </remarks>
    static void Retreat(RenderView camera, float distance) {
        var position = new Vector3(0f, 0f, distance);
        var view = Matrix4x4.LookAt(position, position + new Vector3(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(FieldOfView, 1f, 0.1f, 10000f);

        camera.Position = position;
        camera.Frustum = new(view * projection);
    }

    /// <summary>
    ///     A group whose thresholds do not descend is refused rather than thrown, and counted.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An inspector edits these one keystroke at a time</b>, so a list is ascending for as
    ///     long as it takes to finish the second box — and <c>LodRenderFeature.Add</c> throws on one.
    ///     A frame loop that throws out of extraction takes the editor with it, so the group is left
    ///     unregistered and every level draws, which is the picture the scene had before anybody
    ///     authored it. The counter is what stops that being indistinguishable from working.
    /// </remarks>
    [Fact]
    public void AGroupWhoseThresholdsAscendIsRefusedRatherThanThrown() {
        using var loop = new EngineLoop();
        using var renderer = Build(loop, out var camera);

        var levels = Group(loop.World, new(0f, 0f, -3f), Ascending);

        Frame(loop, renderer);

        Assert.Equal(1, renderer.LodExtraction!.Malformed);
        Assert.Equal(0, renderer.LodExtraction.GroupCount);
        Assert.Equal(0, renderer.LodExtraction.Assigned);

        // And every level is drawn, rather than one of them being hidden by a group that was never
        // registered.
        var objects = Objects(loop.World, levels);

        Assert.All(
            objects,
            id => Assert.True(
                renderer.Host.System.Visibility.IsVisible(camera.Index, id),
                "a group whose thresholds ascend is never registered, so every level of it draws: "
                + Shown(renderer, camera, objects)
            )
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Diagnosis ----------------------------------------------------------

    /// <summary>Asserts a group drew one level and hid the rest, saying which it drew if it did not.</summary>
    /// <param name="renderer">The renderer that drew the frame.</param>
    /// <param name="view">The view whose choice is being asked about.</param>
    /// <param name="objects">One group's levels, finest first.</param>
    /// <param name="level">The level this group should have chosen.</param>
    /// <param name="group">What to call it, for a test with more than one.</param>
    /// <remarks>
    ///     ⚠ <b>Written because the whole of what CI reported was <c>Assert.True() Failure /
    ///     Expected: True / Actual: False</c>, and that is the first defect in #1189.</b> A bare
    ///     <c>Assert.True</c> on a LOD selection says nothing about which level was shown, which was
    ///     hidden, or — in the two-group test — which group decided wrongly, and the two runners it
    ///     failed on are not on anybody's desk. Every assertion in this file carries the numbers the
    ///     choice was made from now, which is what identified the fixture as the defect rather than
    ///     the feature.
    /// </remarks>
    static void Shows(
        WorldRenderer renderer,
        RenderView view,
        RenderObjectId[] objects,
        int level,
        string group = "the group"
    ) {
        for (var candidate = 0; candidate < objects.Length; candidate++) {
            var visible = renderer.Host.System.Visibility.IsVisible(view.Index, objects[candidate]);

            Assert.True(
                visible == (candidate == level),
                $"{group} should draw level {level} and hide the rest. " + Shown(renderer, view, objects)
            );
        }
    }

    /// <summary>Asserts which ends of a cross-fade a view is drawing.</summary>
    /// <param name="renderer">The renderer that drew the frame.</param>
    /// <param name="view">The view whose choice is being asked about.</param>
    /// <param name="objects">The group's levels, finest first.</param>
    /// <param name="fine">Whether level 0 should be drawn.</param>
    /// <param name="coarse">Whether level 2 should be drawn.</param>
    /// <param name="when">Where in the transition this is, for the message.</param>
    /// <remarks>
    ///     A cross-fade is the one state in which two members of a group are visible at once, so
    ///     <see cref="Shows" />'s "one and only one" is the wrong shape for it. The middle level is
    ///     asserted hidden throughout either way: it is neither end of this transition.
    /// </remarks>
    static void Fading(
        WorldRenderer renderer,
        RenderView view,
        RenderObjectId[] objects,
        bool fine,
        bool coarse,
        string when
    ) {
        var visibility = renderer.Host.System.Visibility;
        var drawn = $"{when}, the group should draw " + Wanted(fine, coarse) + ". " + Shown(renderer, view, objects);

        Assert.True(visibility.IsVisible(view.Index, objects[0]) == fine, drawn);
        Assert.False(visibility.IsVisible(view.Index, objects[1]), drawn);
        Assert.True(visibility.IsVisible(view.Index, objects[2]) == coarse, drawn);

        static string Wanted(bool fine, bool coarse) => (fine, coarse) switch {
            (true, true) => "both ends of the transition",
            (true, false) => "level 0 alone",
            (false, true) => "level 2 alone",
            _ => "nothing at all"
        };
    }

    /// <summary>What a view drew of one group, and the three numbers that decided it.</summary>
    /// <param name="renderer">The renderer that drew the frame.</param>
    /// <param name="view">The view whose choice is being reported.</param>
    /// <param name="objects">The levels to report on, finest first.</param>
    /// <returns>A sentence a reader with no machine in front of them can diagnose from.</returns>
    /// <remarks>
    ///     ⚠ <b>The radius, the distance and the scale rather than the screen height itself.</b>
    ///     <c>LodRenderFeature.Height</c> is private and re-computing it here would be a second
    ///     implementation of the thing under test — the shape this repository files under "verify the
    ///     instrument first". These three are the feature's own inputs, read back off the objects it
    ///     read, and their quotient is the number to compare against the thresholds by hand.
    /// </remarks>
    static string Shown(WorldRenderer renderer, RenderView view, RenderObjectId[] objects) {
        List<string> levels = [];

        for (var level = 0; level < objects.Length; level++) {
            var bounds = renderer.Host.System.Objects[objects[level]].Bounds;
            var seen = renderer.Host.System.Visibility.IsVisible(view.Index, objects[level]) ? "shown" : "hidden";
            var distance = Vector3.Distance(bounds.Center, view.Position);

            levels.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"level {level} {seen} (radius {bounds.Radius:R}, distance {distance:R})"
                )
            );
        }

        var thresholds = string.Join(
            ", ",
            Thresholds.Select(value => value.ToString("R", CultureInfo.InvariantCulture))
        );

        return string.Create(
            CultureInfo.InvariantCulture,
            $"It drew: {string.Join("; ", levels)}. Thresholds [{thresholds}], "
            + $"screen-height scale {view.ScreenHeightScale:R}."
        );
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
