// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Ai.Nodes.Ecs;
using Vixen.Animation.Ecs;
using Vixen.Audio;
using Vixen.Audio.Ecs;
using Vixen.Core;

namespace Vixen.Ai.Nodes;

/// <summary>What a task waiting on something with a length remembers.</summary>
[StructLayout(LayoutKind.Sequential)]
struct PlaybackState {
    public float Previous;
    public int State;
    public int Started;
}

/// <summary>Plays an animation state on a layer, and optionally waits for it.</summary>
/// <param name="layer">Which layer of the animator. Empty means the first one.</param>
/// <param name="stateName">Which state to play.</param>
/// <param name="crossfade">How long to blend into it, in seconds.</param>
/// <param name="wait">Whether the task runs until the state has played through.</param>
/// <remarks>
///     <para>
///         doc 37 § Part 3's <c>PlayAnimation</c>. It asks the state machine to play a state; what
///         actually plays is that state's motion, which may be a clip, a blend tree or a
///         <c>MoveSetMotion</c> picking from a move set. That is the move-set half of the row, and it
///         is a property of how the state was authored rather than a second node here — a task that
///         reached past the state machine into a move set would be a second way to drive an animator,
///         and the two would disagree about what is playing.
///     </para>
///     <para>
///         ⚠ <b>"Played through" is a loop, not a time.</b> The task succeeds when the state's
///         normalised time reaches one <i>or wraps</i>, and also when something else has taken the
///         machine somewhere — a transition fired by a parameter, or another branch's task. Waiting
///         on a fixed duration instead would desynchronise the moment anybody changed the clip, and
///         would never end for a state that loops.
///     </para>
/// </remarks>
public sealed class PlayAnimationTask(string layer, string stateName, float crossfade = 0.15f, bool wait = true)
    : IAgentAction {
    /// <summary>How many bytes it needs.</summary>
    public static int StateSize => Unsafe.SizeOf<PlaybackState>();

    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) { }

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        var world = context.World;
        var entity = context.Entity;

        if (!world.Has<AnimatorComponent>(entity) || world.Get<AnimatorComponent>(entity).Value is not { } animator) {
            return ActionStatus.Failed;
        }

        var target = layer.Length > 0
            ? animator.Layer(layer)
            : animator.Layers.Count > 0 ? animator.Layers[0] : null;

        if (target is null) {
            return ActionStatus.Failed;
        }

        ref var playback = ref MemoryMarshal.AsRef<PlaybackState>(state);

        if (playback.Started == 0) {
            if (!target.States.Play(stateName, crossfade)) {
                return ActionStatus.Failed;
            }

            playback.Started = 1;
            playback.State = target.States.CurrentState;
            playback.Previous = target.States.NormalizedTime;

            return wait ? ActionStatus.Running : ActionStatus.Succeeded;
        }

        if (target.States.CurrentState != playback.State) {
            return ActionStatus.Succeeded;
        }

        var now = target.States.NormalizedTime;
        var wrapped = now + 1e-4f < playback.Previous;

        playback.Previous = now;

        return wrapped || now >= 1f ? ActionStatus.Succeeded : ActionStatus.Running;
    }

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) { }
}

/// <summary>Plays a clip on the agent's own audio source, and optionally waits for it.</summary>
/// <param name="clip">What to play.</param>
/// <param name="wait">Whether the task runs for the length of the clip.</param>
/// <param name="gain">Its linear gain.</param>
/// <remarks>
///     <para>
///         doc 37 § Part 3's <c>PlaySound</c>. It writes the entity's own <c>AudioSource</c> and
///         <c>AudioClipRef</c> rather than spawning anything: a sound a character makes belongs to the
///         character, so it moves with it, stops when it dies and is spatialised by the
///         <c>AudioSpatial</c> the character already carries.
///     </para>
///     <para>
///         ⚠ <b>The components must already be there, and the task fails rather than adding them.</b>
///         Adding a component is a structural change, and a tree step happens inside a chunk walk —
///         every span the walk is holding would be invalidated under it. It is also the right
///         default: a character that can make noises is authored with a source, and one that cannot
///         is a bug worth seeing.
///     </para>
/// </remarks>
public sealed class PlaySoundTask(AudioClip? clip, bool wait = false, float gain = 1f) : IAgentAction {
    /// <summary>How many bytes it needs.</summary>
    public static int StateSize => Unsafe.SizeOf<PlaybackState>();

    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) { }

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        var world = context.World;
        var entity = context.Entity;

        if (clip is null || !world.Has<AudioSource>(entity) || !world.Has<AudioClipRef>(entity)) {
            return ActionStatus.Failed;
        }

        ref var playback = ref MemoryMarshal.AsRef<PlaybackState>(state);

        if (playback.Started == 0) {
            ref var source = ref world.Get<AudioSource>(entity);

            world.Get<AudioClipRef>(entity).Clip = clip;
            source.Gain = gain;
            source.Playback = AudioPlayback.Playing;
            playback.Started = 1;

            return wait ? ActionStatus.Running : ActionStatus.Succeeded;
        }

        playback.Previous += delta;

        return playback.Previous >= (float)clip.Duration.TotalSeconds ? ActionStatus.Succeeded : ActionStatus.Running;
    }

    /// <inheritdoc />
    /// <remarks>⚠ Stops the sound. A one-shot the tree has forgotten about is a sound with no owner.</remarks>
    public void Abort(in AgentContext context, Span<byte> state) {
        var world = context.World;
        var entity = context.Entity;

        if (world.IsAlive(entity) && world.Has<AudioSource>(entity)) {
            world.Get<AudioSource>(entity).Playback = AudioPlayback.Stopped;
        }
    }
}

/// <summary>Keeps the agent's <see cref="AiFocus" /> pointed at what a key names.</summary>
/// <param name="key">The key holding a <c>Vector3</c> or an <c>Entity</c>.</param>
/// <remarks>
///     <para>
///         doc 37 § Part 3's <c>DefaultFocus</c>. Its value is that everything downstream reads one
///         place: a rotation task, an aim offset, a head-look constraint and a camera all want "what
///         is this character looking at", and without it each of them takes its own key.
///     </para>
///     <para>
///         ⚠ <b>It clears the focus when the key is unset, and that is the half people leave out.</b>
///         A focus nobody cleared is a guard that keeps staring at where an enemy was after it has
///         forgotten about it — which is a bug the head-look makes visible and the blackboard does
///         not.
///     </para>
/// </remarks>
public sealed class DefaultFocusService(BlackboardKey key) : BehaviorService {
    /// <inheritdoc />
    public override void Tick(in BehaviorContext context, Span<byte> state, float delta) {
        var agent = context.Agent;
        var world = agent.World;

        if (!world.Has<AiFocus>(agent.Entity)) {
            return;
        }

        ref var focus = ref world.Get<AiFocus>(agent.Entity);

        if (!AgentTarget.TryResolve(in agent, key, out var point, out var target)) {
            focus.HasFocus = false;
            focus.Target = Entity.Null;

            return;
        }

        focus.HasFocus = true;
        focus.Target = target;
        focus.Point = point;
    }

    /// <inheritdoc />
    /// <remarks>The branch that wanted the focus is over, so the focus goes with it.</remarks>
    public override void Leave(in BehaviorContext context, Span<byte> state) {
        var world = context.Agent.World;
        var entity = context.Agent.Entity;

        if (world.IsAlive(entity) && world.Has<AiFocus>(entity)) {
            world.Get<AiFocus>(entity) = default;
        }
    }
}
