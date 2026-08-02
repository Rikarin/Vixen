// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Ik;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>Foot placement, expressed as constraints rather than as its own solver.</summary>
/// <remarks>
///     <para>
///         <b>The same feet, through the stage.</b> <see cref="FootPlacement" /> stays exactly as it
///         is — a game that wants only feet should not have to meet any of this, and it is the
///         cheaper path — but a game that has <em>other</em> constraints needs its feet arbitrating
///         with them rather than fighting them from a separate pass. Two position goals with a region
///         of the ground plane's tolerance, plus an orientation goal per foot, and one arbitration
///         story instead of two.
///     </para>
///     <para>
///         ⚠ <b>The hip drop is not a goal, and that is the one real asymmetry.</b> It is computed
///         <em>from</em> the feet — the deepest shortfall over all of them — so it cannot be one
///         participant among them; a goal that needed every other goal's answer before stating its
///         own is not something a one-pass arbiter can host. It is applied here, before the goals are
///         handed over, exactly as the standalone solver applies it before its two-bone solves. The
///         staged solve that could express it properly is what <see cref="IConstraintArbiter" /> is
///         for.
///     </para>
///     <para>
///         Ground contacts are supplied, not queried, for <see cref="GroundContact" />'s reason: a
///         raycast is a physics-world question asked on the physics thread's terms.
///     </para>
/// </remarks>
public sealed class FootPlacementPreset {
    readonly ConstraintStack stack;
    readonly FootChain[] feet;
    readonly ConstraintHandle[] positions;
    readonly ConstraintHandle[] orientations;
    readonly ProvidedFrame[] frames;
    readonly float[] shortfall;

    /// <summary>Adds foot goals to a stack.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="pelvis">The joint that is lowered to give the legs slack.</param>
    /// <param name="feet">The legs.</param>
    /// <param name="label">What to call the goals, for suppression and querying.</param>
    public FootPlacementPreset(
        ConstraintStack stack,
        int pelvis,
        IEnumerable<FootChain> feet,
        Symbol label = default
    ) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(feet);

        this.stack = stack;
        this.feet = [.. feet];

        Pelvis = pelvis;
        Label = label.IsSome ? label : Symbol.Intern("foot");

        positions = new ConstraintHandle[this.feet.Length];
        orientations = new ConstraintHandle[this.feet.Length];
        frames = new ProvidedFrame[this.feet.Length];
        shortfall = new float[this.feet.Length];

        for (var index = 0; index < this.feet.Length; index++) {
            var leg = this.feet[index];
            var frame = new ProvidedFrame(Symbol.Intern($"{Label}-{index}"));

            frames[index] = frame;

            positions[index] = stack.Add(
                new PositionGoal {
                    Effector = leg.Ankle,
                    Chain = new(leg.Hip, leg.Ankle),
                    Goal = frame,
                    Pole = leg.Pole,
                    Label = Label,
                    Weight = 0f,
                    EaseIn = 0f,
                    EaseOut = 0f
                }
            );

            orientations[index] = stack.Add(
                new OrientationGoal {
                    Effector = leg.Ankle,
                    Chain = new(leg.Hip, leg.Ankle),
                    Goal = frame,
                    Label = Label,
                    Weight = 0f,
                    EaseIn = 0f,
                    EaseOut = 0f
                }
            );
        }
    }

    /// <summary>The joint that is lowered to give the legs slack.</summary>
    public int Pelvis { get; }

    /// <summary>What the goals are called.</summary>
    public Symbol Label { get; }

    /// <summary>How far the hips may be lowered, in metres.</summary>
    public float MaxHipDrop { get; set; } = 0.4f;

    /// <summary>How much of the way to the surface's normal the ankle rolls, in <c>[0, 1]</c>.</summary>
    public float AnkleWeight { get; set; } = 1f;

    /// <summary>How much of the placement to apply at all, in <c>[0, 1]</c>.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>How far the hips were lowered on the last update. Zero or negative.</summary>
    public float HipDrop { get; private set; }

    /// <summary>Sets this frame's contacts and lowers the hips.</summary>
    /// <param name="local">The pose, in local space, written in place.</param>
    /// <param name="model">A model-space buffer of at least the skeleton's joint count.</param>
    /// <param name="contacts">One ground contact per leg, in the order the legs were given.</param>
    /// <returns>How far the hips were lowered, in metres.</returns>
    /// <remarks>
    ///     Called before the stack solves — from the game's own update, or from a processor placed
    ///     ahead of the stack in the list. What it leaves behind is a provided frame per foot and a
    ///     weight per goal, which the stack then arbitrates like anything else.
    /// </remarks>
    public float Update(
        Span<BoneTransform> local,
        Span<BoneTransform> model,
        ReadOnlySpan<GroundContact> contacts
    ) {
        var weight = MathUtil.Saturate(Weight);
        var roll = MathUtil.Saturate(AnkleWeight) * weight;

        HipDrop = 0f;

        for (var index = 0; index < feet.Length; index++) {
            positions[index].Weight = 0f;
            orientations[index].Weight = 0f;
        }

        if (weight <= 0f || feet.Length == 0) {
            return 0f;
        }

        SkeletonPose.ComputeModelSpace(stack.Skeleton, local, model);

        var drop = 0f;

        for (var index = 0; index < feet.Length; index++) {
            var contact = index < contacts.Length ? contacts[index] : default;

            if (!contact.Hit) {
                shortfall[index] = float.NaN;
                continue;
            }

            shortfall[index] = Goal(contact, feet[index].SoleOffset).Y - model[feet[index].Ankle].Translation.Y;
            drop = MathF.Min(drop, shortfall[index]);
        }

        drop = MathF.Max(drop, -MathF.Abs(MaxHipDrop)) * weight;

        if (drop < 0f) {
            var parent = stack.Skeleton.ParentOf(Pelvis);

            var parentRotation = parent < 0
                ? Quaternion.Identity
                : model[parent].Rotation;

            local[Pelvis].Translation += Quaternion.Transform(
                new Vector3(0f, drop, 0f),
                Quaternion.Conjugate(parentRotation)
            );

            SkeletonPose.ComputeModelSpace(stack.Skeleton, local, model);
        }

        HipDrop = drop;

        for (var index = 0; index < feet.Length; index++) {
            if (float.IsNaN(shortfall[index])) {
                continue;
            }

            var leg = feet[index];
            var contact = contacts[index];
            var ankle = model[leg.Ankle];

            var rolled = Quaternion.Concatenate(
                ankle.Rotation,
                Quaternion.Nlerp(
                    Quaternion.Identity,
                    Quaternion.FromToRotation(Quaternion.Transform(Vector3.Up, ankle.Rotation), contact.Normal),
                    roll
                )
            );

            // World space, because that is what a provided frame is. The stack brings it back into
            // model space with the character's own transform, so a caller working in model space
            // leaves the transform at identity and pays nothing for the round trip.
            stack.Bindings.Provide(
                frames[index].Name,
                BoneTransform.Concatenate(
                    new(Goal(contact, leg.SoleOffset), rolled, Vector3.One),
                    stack.WorldTransform
                )
            );

            positions[index].Weight = weight;
            orientations[index].Weight = roll;
        }

        return drop;
    }

    static Vector3 Goal(in GroundContact contact, float soleOffset) {
        var normal = contact.Normal.LengthSquared() > 1e-6f
            ? Vector3.Normalize(contact.Normal)
            : Vector3.Up;

        return contact.Position + (normal * soleOffset);
    }
}
