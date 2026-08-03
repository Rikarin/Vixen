// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Animation.StateMachine;
using Vixen.Core.Mathematics;

namespace Vixen.Animation;

/// <summary>
///     Something that gets a look at the pose after the layers have been mixed and before anything
///     reads it — which is where IK goes.
/// </summary>
/// <remarks>
///     IK has to run after the blend, because it corrects the result and not any one of the things
///     that went into it: a foot placed on a slope has to be placed on the pose the character is
///     actually in, not on the walk cycle that is 60 % of it. And it has to run before skinning,
///     which is the other end of this hook.
/// </remarks>
public interface IPoseProcessor {
    /// <summary>Adjusts the pose.</summary>
    /// <param name="animator">The animator, for its skeleton and its parameters.</param>
    /// <param name="pose">The blended pose, in local space, to be written in place.</param>
    /// <param name="model">
    ///     A model-space buffer the processor may use and must assume is stale on entry. Solvers in
    ///     <c>Vixen.Animation.Ik</c> fill it themselves.
    /// </param>
    void Process(Animator animator, Span<BoneTransform> pose, Span<BoneTransform> model);
}

/// <summary>
///     One character's animation: the layers, the parameters they read, the pose they produce, and
///     what came out of it.
/// </summary>
/// <remarks>
///     <para>
///         The façade the rest of the engine talks to. Everything under it — clips, blend trees,
///         state machines, masks — is reachable and testable on its own, and a game that only wants
///         "play this, blend to that, tell me when a foot lands" never has to meet any of it.
///     </para>
///     <para>
///         <b>One update produces everything.</b> The pose, the root motion delta and the frame's
///         events all come out of the same pass, because they are three views of the same
///         evaluation and computing them separately would mean evaluating the graph more than once
///         against parameters that a script could change in between.
///     </para>
///     <para>
///         <b>Nothing here touches an entity.</b> An animator has a skeleton and a pose and no idea
///         what is wearing it — which is what lets a test drive one with a <c>for</c> loop, and what
///         keeps the ECS integration (<c>Vixen.Animation.Ecs</c>) to the thirty lines that copy a
///         root motion delta into a transform.
///     </para>
/// </remarks>
public sealed class Animator {
    readonly List<AnimationLayer> layers = [];
    readonly List<IPoseProcessor> processors = [];
    readonly BoneTransform[] model;

    /// <summary>Creates an animator for a skeleton.</summary>
    /// <param name="skeleton">The skeleton it poses.</param>
    /// <param name="parameters">
    ///     The parameter set its graphs read, or <see langword="null" /> for a fresh one.
    /// </param>
    public Animator(Skeleton skeleton, AnimationParameters? parameters = null) {
        ArgumentNullException.ThrowIfNull(skeleton);

        Skeleton = skeleton;
        Parameters = parameters ?? new AnimationParameters();
        Pose = new(skeleton);
        Scratch = new(skeleton.JointCount);
        Events = new();
        Constraints = new();
        model = new BoneTransform[skeleton.JointCount];
        RootJoint = FirstRoot(skeleton);
    }

    /// <summary>The skeleton being posed.</summary>
    public Skeleton Skeleton { get; }

    /// <summary>The values the graphs read. Game code writes these.</summary>
    public AnimationParameters Parameters { get; }

    /// <summary>The pose as of the last update.</summary>
    public SkeletonPose Pose { get; }

    /// <summary>Where blends get their temporary poses.</summary>
    public PoseScratch Scratch { get; }

    /// <summary>The events the last update produced.</summary>
    public AnimationEventBuffer Events { get; }

    /// <summary>The constraints the clips playing this frame carry, and how much of each.</summary>
    /// <remarks>
    ///     Collected during evaluation, for the reason events are: a tag becomes live in the middle
    ///     of a blend tree, and a <see cref="ConstraintStack" /> has to see every clip's contribution
    ///     before it can decide what a chain does.
    /// </remarks>
    public ConstraintTagBuffer Constraints { get; }

    /// <summary>How long the last update was, in seconds. Already scaled by <see cref="Speed" />.</summary>
    /// <remarks>
    ///     ⚠ Read by <see cref="IPoseProcessor" />s that carry state between frames — a constraint
    ///     easing in, a solver damping — because <see cref="IPoseProcessor.Process" /> is handed a
    ///     pose and not a clock, and a processor that guessed at a fixed timestep would ease at a
    ///     different rate on every machine.
    /// </remarks>
    public float LastDeltaTime { get; private set; }

    /// <summary>The layers, base first.</summary>
    public IReadOnlyList<AnimationLayer> Layers => layers;

    /// <summary>What runs on the pose after the layers are mixed. IK lives here.</summary>
    public IList<IPoseProcessor> PoseProcessors => processors;

    /// <summary>How fast everything plays. One is as authored; zero pauses the character.</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>What to do with the motion baked into the root joint.</summary>
    public RootMotionMode RootMotion { get; set; } = RootMotionMode.Disabled;

    /// <summary>Which joint carries the character through the world.</summary>
    public int RootJoint { get; set; }

    /// <summary>How far the root moved during the last update.</summary>
    /// <remarks>
    ///     Reported whatever <see cref="RootMotion" /> is set to — except
    ///     <see cref="RootMotionMode.Disabled" />, where nothing computes it. A character controller
    ///     reads this, decides how much of it survives a wall, and moves the entity itself.
    /// </remarks>
    public RootMotionDelta LastRootMotion { get; private set; }

    /// <summary>Adds a layer on top of the ones already there.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="machine">The graph it runs.</param>
    /// <returns>The layer, so its weight, mask and blend mode can be set.</returns>
    /// <remarks>
    ///     The first layer added is the base: its weight and mask are ignored, and it is the one
    ///     that owns root motion unless another layer is told to.
    /// </remarks>
    public AnimationLayer AddLayer(string name, AnimationStateMachine machine) {
        var layer = new AnimationLayer(name, machine, Parameters, Scratch) {
            ContributesRootMotion = layers.Count == 0
        };

        layer.States.Constraints = Constraints;

        layers.Add(layer);
        return layer;
    }

    /// <summary>The layer by name, or <see langword="null" />.</summary>
    /// <param name="name">The layer's name.</param>
    /// <returns>The layer, or <see langword="null" />.</returns>
    public AnimationLayer? Layer(string name) {
        foreach (var layer in layers) {
            if (string.Equals(layer.Name, name, StringComparison.Ordinal)) {
                return layer;
            }
        }

        return null;
    }

    /// <summary>Advances every layer and rebuilds the pose.</summary>
    /// <param name="deltaTime">How much time has passed, in seconds.</param>
    public void Update(float deltaTime) {
        Events.Clear();
        Constraints.Clear();

        var step = deltaTime * Speed;

        LastDeltaTime = step;
        var wantsRootMotion = RootMotion is not RootMotionMode.Disabled && RootJoint >= 0;
        var motion = RootMotionDelta.None;
        var built = false;

        for (var index = 0; index < layers.Count; index++) {
            var layer = layers[index];

            if (!layer.Contributes(index)) {
                continue;
            }

            var wantsThisLayersRootMotion = wantsRootMotion && layer.ContributesRootMotion;

            if (!built) {
                // The first layer that contributes writes the pose rather than blending into it.
                // Blending the base layer against whatever was in the buffer would make the result
                // depend on last frame, which is how a character that stops being animated for one
                // frame never fully comes back.
                var delta = layer.States.Evaluate(
                    step,
                    Pose.Bones,
                    Events,
                    index,
                    1f,
                    wantsThisLayersRootMotion
                );

                built = true;

                if (wantsThisLayersRootMotion) {
                    motion = delta;
                }

                continue;
            }

            using var lease = Scratch.Rent();

            var layerMotion = layer.States.Evaluate(
                step,
                lease.Pose,
                Events,
                index,
                MathUtil.Saturate(layer.Weight),
                wantsThisLayersRootMotion
            );

            layer.Apply(Pose.Bones, lease.Pose);

            if (wantsThisLayersRootMotion) {
                motion = layerMotion.Scaled(MathUtil.Saturate(layer.Weight));
            }
        }

        if (!built) {
            Pose.ResetToBindPose();
        }

        if (wantsRootMotion) {
            // Taken out of the pose, because it has been handed to whoever owns the transform.
            // Leaving it in would move the character twice — once through the world and once inside
            // its own model space, which is the sliding-feet-plus-drifting-mesh bug.
            Pose[RootJoint] = Skeleton.BindPose[RootJoint];
        }

        LastRootMotion = motion;

        if (processors.Count > 0) {
            foreach (var processor in processors) {
                processor.Process(this, Pose.Bones, model);
            }
        }

        Parameters.ClearTriggers();
    }

    /// <summary>
    ///     The bone palette for GPU skinning, as of the last update.
    /// </summary>
    /// <param name="destination">One matrix per joint.</param>
    public void ComputeSkinningMatrices(Span<Matrix4x4> destination) =>
        Pose.ComputeSkinningMatrices(destination, model);

    static int FirstRoot(Skeleton skeleton) {
        var parents = skeleton.Parents;

        for (var index = 0; index < parents.Length; index++) {
            if (parents[index] < 0) {
                return index;
            }
        }

        return -1;
    }
}
