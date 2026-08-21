// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Play mode steps a real system graph, and a stop puts the scene back as it was.</summary>
/// <remarks>
///     ⚠ <b>These are the tests that would have caught the thing they were written for.</b>
///     <c>PlayModeController.ShouldTick</c> was covered — thoroughly, by
///     <see cref="SceneTests" /> — and had no caller in the product for as long as it existed, so
///     every assertion about pausing and stepping passed against an editor in which pressing Play
///     advanced nothing. A state machine tested in isolation is a test of the state machine; what
///     was missing was one that asserts a frame *happened*.
/// </remarks>
public class PlayGraphTests {
    static PlayGraphTests() => SceneBehaviorRegistry.Register<PlayCounter>();

    static SceneDocument Document(World world) =>
        new(new EditorProject(new ProjectPaths(Path.Combine(Path.GetTempPath(), "vixen-play-graph"))),
            world,
            AssetId.Empty,
            "Untitled");

    /// <summary>The transform pass runs, which is what "a frame happened" looks like with no scripts.</summary>
    /// <remarks>
    ///     Nothing here calls <c>TransformSystem.Resolve</c>. The composed matrix can only come from
    ///     the graph's own <c>PreRender</c>, which is the whole assertion.
    /// </remarks>
    [Fact]
    public void Ticking_runs_the_transform_pass_that_the_editor_otherwise_runs_by_hand() {
        using var world = new World("Scene");

        var parent = Hierarchy.CreateTransform(world, LocalTransform.At(new Vector3(10f, 0f, 0f)));
        var child = Hierarchy.CreateTransform(world, LocalTransform.At(new Vector3(1f, 0f, 0f)));

        Hierarchy.SetParent(world, child, parent);

        using var play = new PlayModeController(world);

        Assert.True(play.Play());
        Assert.NotNull(play.Loop);
        Assert.True(play.Tick(TimeSpan.FromMilliseconds(16)));

        Assert.Equal(new Vector3(11f, 0f, 0f), world.Read<WorldTransform>(child).Position);
    }

    /// <summary>A session with nothing running is not a session, so there is nothing to tick.</summary>
    /// <remarks>
    ///     ⚠ <b>And the step is not spent.</b> <c>ShouldTick</c> consumes, so a <c>Tick</c> that asked
    ///     it before checking for a loop would eat the frame Step Frame had just queued — which
    ///     presents as a Step button that has to be pressed twice.
    /// </remarks>
    [Fact]
    public void Ticking_a_stopped_session_does_nothing_and_spends_no_step() {
        using var world = new World("Scene");
        using var play = new PlayModeController(world);

        play.Step();
        play.Stop();

        Assert.False(play.Tick(TimeSpan.FromMilliseconds(16)));
        Assert.Equal(0, play.PendingSteps);
        Assert.Null(play.Loop);
    }

    /// <summary>Pause stops the frames; a step gives back exactly one.</summary>
    [Fact]
    public void Pausing_stops_the_frames_and_a_step_runs_exactly_one() {
        using var world = new World("Scene");
        var entity = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        var scene = Document(world);

        scene.Behaviors.Add(entity, new PlayCounter());

        using var play = new PlayModeController(world, scene.Behaviors);

        play.Play();

        // Two frames: the first drains Awake and OnEnable, the second is the one Start is eligible in
        // — doc 04's one-frame deferral — so counting starts on the third.
        for (var frame = 0; frame < 5; frame++) {
            Assert.True(play.Tick(TimeSpan.FromMilliseconds(16)));
        }

        var running = Ticks(play, entity);

        Assert.True(running > 0, "the behaviour's Update never ran");

        play.Pause();
        Assert.False(play.Tick(TimeSpan.FromMilliseconds(16)));
        Assert.Equal(running, Ticks(play, entity));

        play.Step();
        Assert.True(play.Tick(TimeSpan.FromMilliseconds(16)));
        Assert.Equal(running + 1, Ticks(play, entity));

        Assert.False(play.Tick(TimeSpan.FromMilliseconds(16)));
        Assert.Equal(running + 1, Ticks(play, entity));
    }

    /// <summary>
    ///     ⚠ <b>The authored behaviour is not the one that ran</b>, which is what makes leaving play
    ///     mode mean what the notification says it means.
    /// </summary>
    [Fact]
    public void A_behaviour_runs_during_play_and_the_authored_one_comes_back_untouched() {
        using var world = new World("Scene");
        var entity = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        var scene = Document(world);
        var original = scene.Behaviors.Add(entity, new PlayCounter { Ticks = 0, Speed = 4.5f });

        using var play = new PlayModeController(world, scene.Behaviors);

        play.Play();

        // ⚠ What is on the entity while playing is a *copy*, not the authored object. `AllOn` reads
        // the entity's own link rather than a store's buckets — the link is one component whichever
        // store attached it — so the assertion that means anything here is identity.
        Assert.NotSame(original, Assert.Single(scene.Behaviors.AllOn(entity).ToArray()));

        for (var frame = 0; frame < 5; frame++) {
            play.Tick(TimeSpan.FromMilliseconds(16));
        }

        Assert.True(Ticks(play, entity) > 0);

        var restored = play.Stop([entity]);
        var now = Assert.Single(restored);
        var back = scene.Behaviors.Get<PlayCounter>(now);

        Assert.NotNull(back);
        Assert.NotSame(original, back);

        // The values somebody typed, and none of the counting the session did.
        Assert.Equal(4.5f, back.Speed);
        Assert.Equal(0, back.Ticks);
    }

    /// <summary>Stopping runs the session's teardown rather than dropping the objects on the floor.</summary>
    /// <remarks>
    ///     ⚠ <b>Before the restore clears the world, which is the only order that works.</b> A
    ///     teardown after <c>World.Clear</c> has no entity to walk, so every behaviour would keep
    ///     whatever <c>Awake</c> acquired — and the leak tracker would then report it as a leak this
    ///     controller caused.
    /// </remarks>
    [Fact]
    public void Stopping_destroys_the_behaviours_the_session_woke() {
        using var world = new World("Scene");
        var entity = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        var scene = Document(world);

        scene.Behaviors.Add(entity, new PlayCounter());

        using var play = new PlayModeController(world, scene.Behaviors);

        play.Play();
        play.Tick(TimeSpan.FromMilliseconds(16));

        var session = Assert.IsType<PlayCounter>(play.Loop!.Behaviors.AllOn(entity).ToArray().Single());

        Assert.True(session.WasAwoken);
        Assert.False(session.WasDestroyed);

        play.Stop();

        Assert.True(session.WasDestroyed);
    }

    /// <summary>A behaviour the session cannot take over is named rather than skipped.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the loud half of the design.</b> Nothing registered <see cref="Anonymous" />,
    ///     so its values cannot cross the snapshot — and a session that carried on regardless would
    ///     let the snapshot copy the live instance, hand it back on a dead handle, and present as a
    ///     script that mysteriously stopped working.
    /// </remarks>
    [Fact]
    public void A_behaviour_with_no_binder_is_left_alone_and_named() {
        using var world = new World("Scene");
        var entity = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        var scene = Document(world);

        var stranded = scene.Behaviors.Add(entity, new Anonymous());

        using var play = new PlayModeController(world, scene.Behaviors);

        play.Play();

        Assert.Equal(["Anonymous"], play.Unsupported);

        // Left exactly where it was — the same object, still attached — rather than half-moved into
        // a session that has no way to give it back.
        Assert.Same(stranded, Assert.Single(scene.Behaviors.AllOn(entity).ToArray()));

        // ⚠ And the stop leaves it alone too. `Teardown` walks the entity's `BehaviorRef`, which is
        // one component however many stores share the world, so it is handed this behaviour — and
        // the session's store refuses it because it is not the session's. Before the store checked,
        // that was a `KeyNotFoundException` out of the middle of Stop.
        play.Stop();

        Assert.False(stranded.IsDestroyed);
    }

    static int Ticks(PlayModeController play, Entity entity) =>
        play.Loop!.Behaviors.Get<PlayCounter>(entity)?.Ticks ?? -1;
}

/// <summary>A behaviour that records that a frame reached it.</summary>
[DataContract("PlayCounter")]
public sealed class PlayCounter : Behavior {
    /// <summary>How many times <c>Update</c> has run.</summary>
    public int Ticks { get; set; }

    /// <summary>An authored value, here to be checked for having survived unchanged.</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>Whether the lifecycle drain reached this instance.</summary>
    [DataMemberIgnore]
    public bool WasAwoken { get; private set; }

    /// <summary>Whether the teardown reached it.</summary>
    [DataMemberIgnore]
    public bool WasDestroyed { get; private set; }

    /// <inheritdoc />
    protected override void Awake() => WasAwoken = true;

    /// <inheritdoc />
    protected override void Update() => Ticks++;

    /// <inheritdoc />
    protected override void OnDestroy() => WasDestroyed = true;
}

/// <summary>A behaviour nothing registered, standing in for one a scene could not carry.</summary>
public sealed class Anonymous : Behavior;
