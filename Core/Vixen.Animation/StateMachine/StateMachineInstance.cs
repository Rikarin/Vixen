// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Motions;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.StateMachine;

/// <summary>
///     One character's position in a state machine: which state, how far through, and whatever it is
///     still fading out of.
/// </summary>
/// <remarks>
///     <para>
///         <b>Playback is a stack of states, not a current-and-next pair.</b> A transition pushes
///         its destination on top and fades it in; the states below keep playing and keep their own
///         time. When the top reaches full weight everything under it is dropped.
///     </para>
///     <para>
///         That is the design that makes interruption fall out rather than be bolted on. A
///         transition interrupted halfway is a third state pushed on top of two that are already
///         blending, and it fades in over the blend they were producing — which is exactly what a
///         person means by "interrupt": what was on screen keeps being on screen, and the new thing
///         arrives over it. The alternative, snapshotting the blended pose and fading from a frozen
///         copy, is cheaper and produces a visible hitch: the pose the player was looking at stops
///         moving at the moment the new input lands.
///     </para>
///     <para>
///         The stack is capped at <see cref="MaxConcurrentStates" />. Past that the cost is real —
///         each entry is a full motion evaluation — and the contribution of the oldest is under a
///         percent by construction, so it is dropped rather than blended.
///     </para>
/// </remarks>
public sealed class StateMachineInstance {
    /// <summary>How many states may be blending at once before the oldest is dropped.</summary>
    public const int MaxConcurrentStates = 4;

    readonly AnimationStateMachine machine;
    readonly AnimationParameters parameters;
    readonly PoseScratch scratch;
    readonly List<Entry> entries = [];

    /// <summary>Starts a character in a machine's default state.</summary>
    /// <param name="machine">The graph.</param>
    /// <param name="parameters">The values its conditions and blend trees read.</param>
    /// <param name="scratch">Where blends get their temporary poses.</param>
    public StateMachineInstance(
        AnimationStateMachine machine,
        AnimationParameters parameters,
        PoseScratch scratch
    ) {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(scratch);

        this.machine = machine;
        this.parameters = parameters;
        this.scratch = scratch;

        entries.Add(new(machine.DefaultState, 0f));
    }

    /// <summary>The graph being run.</summary>
    public AnimationStateMachine Machine => machine;

    /// <summary>
    ///     Where the clips it plays report their constraint tags, or <see langword="null" /> to
    ///     report none.
    /// </summary>
    /// <remarks>
    ///     A property rather than another parameter on <see cref="Evaluate" />, for the reason
    ///     <c>parameters</c> and <c>scratch</c> are constructor arguments: it belongs to the character
    ///     and not to the frame, and threading it through four call sites that have no interest in it
    ///     would make every one of them know about constraints. <see cref="Animator" /> sets it.
    /// </remarks>
    public Constraints.ConstraintTagBuffer? Constraints { get; set; }

    /// <summary>Which state is on top — the one transitions are evaluated from.</summary>
    public int CurrentState => entries[^1].State;

    /// <summary>What it is called.</summary>
    public string CurrentStateName => machine[CurrentState].Name;

    /// <summary>How far through the current state playback is, as a fraction of its length.</summary>
    public float NormalizedTime => entries[^1].Time;

    /// <summary>Whether anything is still fading out underneath the current state.</summary>
    public bool IsTransitioning => entries.Count > 1;

    /// <summary>How far the current transition has got, in <c>[0, 1]</c>.</summary>
    public float TransitionProgress => entries.Count > 1 ? entries[^1].Fade : 1f;

    /// <summary>How many states are blending.</summary>
    public int ActiveStateCount => entries.Count;

    /// <summary>Goes to a state, whatever the graph says.</summary>
    /// <param name="state">Which state.</param>
    /// <param name="crossfade">How long to fade over, in seconds. Zero is a cut.</param>
    /// <param name="offset">Where in the state to start, as a fraction of its length.</param>
    /// <remarks>
    ///     The escape hatch every graph needs. Cutscenes, respawns and debug tooling all have to be
    ///     able to say where a character is without authoring a transition from everywhere.
    /// </remarks>
    public void Play(int state, float crossfade = 0f, float offset = 0f) {
        ArgumentOutOfRangeException.ThrowIfNegative(state);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(state, machine.States.Length);

        Push(state, crossfade, offset, TransitionInterruption.None);
    }

    /// <summary>Goes to a state by name, whatever the graph says.</summary>
    /// <param name="state">The state's name. An unknown name does nothing.</param>
    /// <param name="crossfade">How long to fade over, in seconds.</param>
    /// <param name="offset">Where in the state to start, as a fraction of its length.</param>
    /// <returns><see langword="true" /> if the machine has a state by that name.</returns>
    public bool Play(string state, float crossfade = 0f, float offset = 0f) {
        var index = machine.IndexOf(state);

        if (index < 0) {
            return false;
        }

        Play(index, crossfade, offset);
        return true;
    }

    /// <summary>Advances playback and poses the skeleton.</summary>
    /// <param name="deltaTime">How much time has passed, in seconds.</param>
    /// <param name="destination">One transform per joint.</param>
    /// <param name="events">Where events go, or <see langword="null" /> to fire none.</param>
    /// <param name="layer">Which layer to attribute events to.</param>
    /// <param name="weight">How much this layer contributes, for event attribution.</param>
    /// <param name="wantsRootMotion">Whether the return value will be used.</param>
    /// <returns>How far the root moved.</returns>
    /// <remarks>
    ///     Time and pose in one call, in this order: fades, then playback times, then transitions,
    ///     then the pose. A transition evaluated before the times were advanced would test an exit
    ///     time against last frame's position and fire a frame late; one evaluated after the pose
    ///     would produce a pose from a state the machine has already left.
    /// </remarks>
    public RootMotionDelta Evaluate(
        float deltaTime,
        Span<BoneTransform> destination,
        AnimationEventBuffer? events,
        int layer,
        float weight,
        bool wantsRootMotion
    ) {
        AdvanceFades(deltaTime);
        AdvanceTimes(deltaTime);
        EvaluateTransitions();

        return Blend(destination, events, layer, weight, wantsRootMotion);
    }

    void AdvanceFades(float deltaTime) {
        for (var index = 1; index < entries.Count; index++) {
            var entry = entries[index];

            entry.Fade = entry.FadeDuration > 0f
                ? MathUtil.Saturate(entry.Fade + (deltaTime / entry.FadeDuration))
                : 1f;

            entries[index] = entry;
        }

        // Everything under a state that has arrived is no longer visible. Dropping it here rather
        // than letting it blend at weight zero is what keeps a graph that transitions constantly
        // from evaluating four motions forever.
        if (entries.Count > 1 && entries[^1].Fade >= 1f) {
            var top = entries[^1];
            top.Fade = 1f;

            entries.Clear();
            entries.Add(top);
        }
    }

    void AdvanceTimes(float deltaTime) {
        for (var index = 0; index < entries.Count; index++) {
            var entry = entries[index];
            var state = machine[entry.State];
            var length = state.Motion.Length(parameters);

            entry.PreviousTime = entry.Time;

            // Normalised: one unit is one pass through the motion, whatever the motion's current
            // length is. A blend tree's length moves as its parameter does, and dividing here is
            // what stops a character's stride rate jumping when it crosses a threshold.
            entry.Time = AnimationClip.Advance(
                entry.Time,
                deltaTime * state.Speed / length,
                state.Wrap,
                1f,
                out entry.Loops
            );

            entries[index] = entry;
        }
    }

    void EvaluateTransitions() {
        // A transition still running decides whether anything may cut it short. Left to itself a
        // graph would re-evaluate the destination's transitions on the frame it started arriving,
        // which is how a chain of zero-length transitions turns into an infinite loop in one frame.
        if (entries.Count > 1) {
            switch (entries[^1].Interruption) {
                case TransitionInterruption.Source:
                    TryTransitionFrom(entries.Count - 2);
                    return;

                case TransitionInterruption.Destination:
                    TryAnyState();
                    TryTransitionFrom(entries.Count - 1);
                    return;

                case TransitionInterruption.SourceThenDestination:
                    TryAnyState();

                    if (!TryTransitionFrom(entries.Count - 2)) {
                        TryTransitionFrom(entries.Count - 1);
                    }

                    return;

                default:
                    return;
            }
        }

        if (!TryAnyState()) {
            TryTransitionFrom(entries.Count - 1);
        }
    }

    bool TryAnyState() {
        foreach (var transition in machine.AnyStateTransitions) {
            if (TryTake(transition, entries.Count - 1)) {
                return true;
            }
        }

        return false;
    }

    bool TryTransitionFrom(int entry) {
        foreach (var transition in machine[entries[entry].State].Transitions) {
            if (TryTake(transition, entry)) {
                return true;
            }
        }

        return false;
    }

    bool TryTake(AnimationTransition transition, int from) {
        var source = entries[from];

        if (!transition.CanTransitionToSelf && transition.Destination.Index == entries[^1].State) {
            return false;
        }

        if (transition.HasExitTime && !ExitTimeReached(source, transition.ExitTime)) {
            return false;
        }

        if (!transition.ConditionsHold(parameters)) {
            return false;
        }

        transition.ConsumeConditions(parameters);
        Push(transition.Destination.Index, transition.Duration, transition.Offset, transition.Interruption);

        return true;
    }

    /// <summary>Whether playback has reached a normalised exit time.</summary>
    static bool ExitTimeReached(in Entry entry, float exitTime) =>
        // A whole pass went by this step, so every point in the motion was crossed — including this
        // one, wherever in the pass it sits. Checking the interval instead would need the wrap
        // handled in two directions to say the same thing.
        entry.Loops > 0 || entry.Time >= MathF.Min(exitTime, 1f);

    void Push(int state, float duration, float offset, TransitionInterruption interruption) {
        var entry = new Entry(state, MathUtil.Saturate(offset)) {
            FadeDuration = MathF.Max(duration, 0f),
            Fade = duration > 0f ? 0f : 1f,
            Interruption = interruption
        };

        if (entry.Fade >= 1f) {
            // A cut. Nothing under it will ever be seen again, so nothing under it is kept.
            entries.Clear();
            entries.Add(entry);

            return;
        }

        if (entries.Count >= MaxConcurrentStates) {
            entries.RemoveAt(0);
        }

        entries.Add(entry);
    }

    RootMotionDelta Blend(
        Span<BoneTransform> destination,
        AnimationEventBuffer? events,
        int layer,
        float weight,
        bool wantsRootMotion
    ) {
        Span<float> contribution = stackalloc float[MaxConcurrentStates];
        var remaining = 1f;

        for (var index = entries.Count - 1; index >= 0; index--) {
            var fade = index == 0 ? 1f : entries[index].Fade;
            contribution[index] = remaining * fade;
            remaining *= 1f - fade;
        }

        var motion = EvaluateEntry(0, destination, events, layer, weight * contribution[0], wantsRootMotion);

        for (var index = 1; index < entries.Count; index++) {
            using var lease = scratch.Rent();

            var delta = EvaluateEntry(
                index,
                lease.Pose,
                events,
                layer,
                weight * contribution[index],
                wantsRootMotion
            );

            var fade = entries[index].Fade;
            PoseBlend.Lerp(destination, destination, lease.Pose, fade);
            motion = RootMotionDelta.Lerp(motion, delta, fade);
        }

        return motion;
    }

    RootMotionDelta EvaluateEntry(
        int index,
        Span<BoneTransform> destination,
        AnimationEventBuffer? events,
        int layer,
        float weight,
        bool wantsRootMotion
    ) {
        var entry = entries[index];
        var state = machine[entry.State];

        var context = new MotionContext(
            parameters,
            scratch,
            entry.Time,
            entry.PreviousTime,
            entry.Loops,
            wantsRootMotion,
            events,
            layer,
            state.Name,
            weight,
            Constraints
        );

        return state.Motion.Evaluate(context, destination);
    }

    struct Entry(int state, float time) {
        public readonly int State = state;
        public float Time = time;
        public float PreviousTime = time;
        public int Loops;
        public float Fade = 1f;
        public float FadeDuration;
        public TransitionInterruption Interruption;
    }
}
