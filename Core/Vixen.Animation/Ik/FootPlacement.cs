// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation.Ik;

/// <summary>What the ground is doing under one foot.</summary>
/// <param name="Hit">Whether anything was found. A foot over a hole is left where the animation put it.</param>
/// <param name="Position">Where the ground is, in model space.</param>
/// <param name="Normal">Which way it faces, in model space.</param>
/// <remarks>
///     <b>Supplied, not queried.</b> This assembly does not reference <c>Vixen.Physics</c> and is not
///     going to: a raycast is a physics-world question, it has to be asked on the physics thread's
///     terms, and a character on a moving platform or a networked client wants to ask it in a way
///     only the game knows. What animation owns is what to do with the answer.
/// </remarks>
public readonly record struct GroundContact(bool Hit, Vector3 Position, Vector3 Normal);

/// <summary>One leg, as the foot solver sees it.</summary>
/// <param name="Hip">The joint at the top of the leg.</param>
/// <param name="Knee">The joint in the middle.</param>
/// <param name="Ankle">The joint the foot hangs off.</param>
/// <param name="Pole">
///     A model-space point the knee bends towards, usually well in front of the character.
/// </param>
/// <param name="SoleOffset">
///     How far the ankle sits above the sole, along the ground's normal. The one measurement that
///     has to come from the rig rather than from the animation.
/// </param>
public readonly record struct FootChain(int Hip, int Knee, int Ankle, Vector3 Pole, float SoleOffset);

/// <summary>
///     Plants feet on ground the animation did not know about: drop the hips until the lowest foot
///     can reach, then solve each leg to its own contact and roll the ankle onto the slope.
/// </summary>
/// <remarks>
///     <para>
///         <b>The hips move first, and that is the whole of why this is not just two two-bone
///         solves.</b> A character standing on a slope has one foot lower than the other by more
///         than a leg can stretch; solving each leg on its own straightens the low one and leaves
///         the character floating with a locked knee. Lowering the pelvis by the deepest foot's
///         shortfall gives both legs slack, and both then solve with a bend.
///     </para>
///     <para>
///         <b>Only ever down.</b> Raising the hips to meet a foot that is above the animated pose
///         would lift the character off the ground it is standing on — the animation already put the
///         supporting foot where it should be, and the other foot's contact is the one that has to
///         give. So the offset is the minimum over the feet, clamped at zero above and at
///         <see cref="MaxHipDrop" /> below.
///     </para>
///     <para>
///         <b>Ankle roll is a rotation towards the surface normal, weighted.</b> Full alignment on a
///         steep slope puts the toe through the ground; the weight is what an artist tunes, and the
///         default of 1 is right for the gentle slopes this is mostly used on.
///     </para>
/// </remarks>
public sealed class FootPlacement {
    readonly FootChain[] feet;
    readonly float[] shortfall;

    /// <summary>Creates a solver for a character's legs.</summary>
    /// <param name="pelvis">
    ///     The joint the hips hang off — the one that is moved to give the legs slack. Usually the
    ///     skeleton's root or the joint just below it.
    /// </param>
    /// <param name="feet">The legs. Two, in practice, but nothing here says so.</param>
    public FootPlacement(int pelvis, IEnumerable<FootChain> feet) {
        ArgumentNullException.ThrowIfNull(feet);

        Pelvis = pelvis;
        this.feet = [.. feet];
        shortfall = new float[this.feet.Length];
    }

    /// <summary>The joint that is moved to give the legs slack.</summary>
    public int Pelvis { get; }

    /// <summary>The legs.</summary>
    public ReadOnlySpan<FootChain> Feet => feet;

    /// <summary>How far the hips may be lowered, in metres.</summary>
    public float MaxHipDrop { get; set; } = 0.4f;

    /// <summary>How much of the way to the surface's normal the ankle rolls, in <c>[0, 1]</c>.</summary>
    public float AnkleWeight { get; set; } = 1f;

    /// <summary>How much of the placement to apply at all, in <c>[0, 1]</c>.</summary>
    /// <remarks>
    ///     Faded out while a character is airborne, and faded in over a few frames on landing. Foot
    ///     placement on a character whose feet are not on the ground is a character reaching for a
    ///     floor it left.
    /// </remarks>
    public float Weight { get; set; } = 1f;

    /// <summary>Plants the feet.</summary>
    /// <param name="skeleton">The skeleton the pose belongs to.</param>
    /// <param name="local">The pose, in local space, written in place.</param>
    /// <param name="model">A model-space buffer of at least the skeleton's joint count.</param>
    /// <param name="contacts">One ground contact per leg, in the order the legs were given.</param>
    /// <returns>How far the hips were lowered, in metres. Zero or negative.</returns>
    public float Solve(
        Skeleton skeleton,
        Span<BoneTransform> local,
        Span<BoneTransform> model,
        ReadOnlySpan<GroundContact> contacts
    ) {
        ArgumentNullException.ThrowIfNull(skeleton);

        var weight = MathUtil.Saturate(Weight);

        if (weight <= 0f || feet.Length == 0) {
            return 0f;
        }

        SkeletonPose.ComputeModelSpace(skeleton, local, model);

        var drop = 0f;

        for (var index = 0; index < feet.Length; index++) {
            var contact = index < contacts.Length ? contacts[index] : default;

            if (!contact.Hit) {
                shortfall[index] = float.NaN;
                continue;
            }

            var goal = Goal(contact, feet[index].SoleOffset);
            shortfall[index] = goal.Y - model[feet[index].Ankle].Translation.Y;
            drop = MathF.Min(drop, shortfall[index]);
        }

        drop = MathF.Max(drop, -MathF.Abs(MaxHipDrop)) * weight;

        if (drop < 0f) {
            // In the pelvis's own local space, because that is where the pose is written. The hips
            // move straight down in model space, so the offset has to be expressed against
            // whatever the pelvis's parent has done.
            var parent = skeleton.ParentOf(Pelvis);

            var parentRotation = parent < 0
                ? Quaternion.Identity
                : model[parent].Rotation;

            local[Pelvis].Translation += Quaternion.Transform(
                new Vector3(0f, drop, 0f),
                Quaternion.Conjugate(parentRotation)
            );
        }

        for (var index = 0; index < feet.Length; index++) {
            if (float.IsNaN(shortfall[index])) {
                continue;
            }

            var leg = feet[index];
            var contact = contacts[index];
            var goal = Goal(contact, leg.SoleOffset);

            var ankle = model[leg.Ankle];
            var rolled = Quaternion.Concatenate(
                ankle.Rotation,
                Quaternion.Nlerp(
                    Quaternion.Identity,
                    Quaternion.FromToRotation(Quaternion.Transform(Vector3.Up, ankle.Rotation), contact.Normal),
                    MathUtil.Saturate(AnkleWeight) * weight
                )
            );

            TwoBoneIk.Solve(
                skeleton,
                local,
                model,
                new(
                    leg.Hip,
                    leg.Knee,
                    leg.Ankle,
                    goal,
                    leg.Pole,
                    rolled,
                    weight,
                    MathUtil.Saturate(AnkleWeight) * weight
                )
            );
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
