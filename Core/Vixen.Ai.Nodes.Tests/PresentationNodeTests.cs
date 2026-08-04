// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Ecs;
using Vixen.Animation.Motions;
using Vixen.Animation.StateMachine;
using Vixen.Audio;
using Vixen.Audio.Ecs;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Ai.Nodes.Tests;

public class PlayAnimationTests {
    [Fact]
    public void ItPlaysTheStateAndFinishesWhenTheStateHas() {
        var level = new Level();
        var animator = Rig.Animator(out _);
        var actor = level.World.Create(LocalTransform.Identity, new AnimatorComponent { Value = animator });
        var task = new PlayAnimationTask("Body", "Wave", crossfade: 0f);
        var state = new byte[PlayAnimationTask.StateSize];
        var context = Board.Context(level, actor, Board.Empty());

        Assert.Equal(ActionStatus.Running, task.Tick(in context, state, 0f));
        Assert.Equal("Wave", animator.Layers[0].States.CurrentStateName);

        // Half a second of a one-second clip.
        Rig.Advance(animator, 0.5f);
        Assert.Equal(ActionStatus.Running, task.Tick(in context, state, 0.5f));

        Rig.Advance(animator, 0.6f);
        Assert.Equal(ActionStatus.Succeeded, task.Tick(in context, state, 0.6f));
    }

    [Fact]
    public void WithoutWaitingItSucceedsAtOnce() {
        var level = new Level();
        var animator = Rig.Animator(out _);
        var actor = level.World.Create(LocalTransform.Identity, new AnimatorComponent { Value = animator });
        var task = new PlayAnimationTask("Body", "Wave", wait: false);
        var context = Board.Context(level, actor, Board.Empty());

        Assert.Equal(ActionStatus.Succeeded, task.Tick(in context, new byte[PlayAnimationTask.StateSize], 0f));
        Assert.Equal("Wave", animator.Layers[0].States.CurrentStateName);
    }

    [Fact]
    public void AnUnknownStateOrLayerOrAnEntityWithNoAnimatorFails() {
        var level = new Level();
        var animator = Rig.Animator(out _);
        var actor = level.World.Create(LocalTransform.Identity, new AnimatorComponent { Value = animator });
        var bare = level.World.Create(LocalTransform.Identity);
        var context = Board.Context(level, actor, Board.Empty());
        var missing = Board.Context(level, bare, Board.Empty());
        var state = new byte[PlayAnimationTask.StateSize];

        Assert.Equal(ActionStatus.Failed, new PlayAnimationTask("Body", "Nope").Tick(in context, state, 0f));
        Assert.Equal(ActionStatus.Failed, new PlayAnimationTask("Legs", "Wave").Tick(in context, state, 0f));
        Assert.Equal(ActionStatus.Failed, new PlayAnimationTask("Body", "Wave").Tick(in missing, state, 0f));
    }
}

public class PlaySoundTests {
    [Fact]
    public void ItStartsTheSourceAndWaitsTheLengthOfTheClip() {
        var level = new Level();
        var actor = level.World.Create(LocalTransform.Identity, AudioSource.Default, new AudioClipRef());
        var clip = Rig.Clip(seconds: 0.5f);
        var task = new PlaySoundTask(clip, wait: true, gain: 0.25f);
        var state = new byte[PlaySoundTask.StateSize];
        var context = Board.Context(level, actor, Board.Empty());

        Assert.Equal(ActionStatus.Running, task.Tick(in context, state, 0f));
        Assert.Equal(AudioPlayback.Playing, level.World.Get<AudioSource>(actor).Playback);
        Assert.Equal(0.25f, level.World.Get<AudioSource>(actor).Gain);
        Assert.Same(clip, level.World.Get<AudioClipRef>(actor).Clip);

        Assert.Equal(ActionStatus.Running, task.Tick(in context, state, 0.3f));
        Assert.Equal(ActionStatus.Succeeded, task.Tick(in context, state, 0.3f));
    }

    /// <summary>⚠ A one-shot the tree has forgotten about is a sound with no owner.</summary>
    [Fact]
    public void AbortingStopsIt() {
        var level = new Level();
        var actor = level.World.Create(LocalTransform.Identity, AudioSource.Default, new AudioClipRef());
        var task = new PlaySoundTask(Rig.Clip(2f), wait: true);
        var state = new byte[PlaySoundTask.StateSize];
        var context = Board.Context(level, actor, Board.Empty());

        task.Tick(in context, state, 0f);
        task.Abort(in context, state);

        Assert.Equal(AudioPlayback.Stopped, level.World.Get<AudioSource>(actor).Playback);
    }

    /// <summary>
    ///     ⚠ It fails rather than adding the components. A tree step happens inside a chunk walk, and
    ///     a structural change there invalidates every span the walk is holding.
    /// </summary>
    [Fact]
    public void AnEntityWithNoAudioSourceFailsRatherThanGrowingOne() {
        var level = new Level();
        var bare = level.World.Create(LocalTransform.Identity);
        var context = Board.Context(level, bare, Board.Empty());

        Assert.Equal(
            ActionStatus.Failed,
            new PlaySoundTask(Rig.Clip(1f)).Tick(in context, new byte[PlaySoundTask.StateSize], 0f)
        );

        Assert.False(level.World.Has<AudioSource>(bare));
    }
}

/// <summary>The smallest animator and the shortest clip that make the two tasks answerable.</summary>
static class Rig {
    public static Animator Animator(out Skeleton skeleton) {
        skeleton = Skeleton.Create(
            new SkeletonData {
                Name = "test",
                Joints = [new() { Name = "Root", Parent = -1 }, new() { Name = "Hand", Parent = 0 }]
            }
        );

        var animator = new Animator(skeleton);
        var idle = new AnimationState("Idle", new ClipMotion(Clip(skeleton, "Idle", 1f)));
        var wave = new AnimationState("Wave", new ClipMotion(Clip(skeleton, "Wave", 1f)));

        animator.AddLayer("Body", new AnimationStateMachine([idle, wave]));

        return animator;
    }

    /// <summary>Steps the animator so that a state's normalised time actually advances.</summary>
    public static void Advance(Animator animator, float seconds) => animator.Update(seconds);

    public static AudioClip Clip(float seconds) => new() {
        SampleRate = 48_000,
        Channels = 1,
        Format = AudioSampleFormat.Float32,
        Samples = new byte[(int)(48_000 * seconds) * 4]
    };

    static AnimationClip Clip(Skeleton skeleton, string name, float seconds) =>
        AnimationClip.Create(
            new AnimationClipData {
                Name = name,
                Duration = seconds,
                Channels = [
                    new() {
                        Target = "Hand",
                        PositionTimes = [0f, seconds],
                        Positions = [Vector3.Zero, new(1f, 0f, 0f)]
                    }
                ]
            },
            skeleton
        );
}
