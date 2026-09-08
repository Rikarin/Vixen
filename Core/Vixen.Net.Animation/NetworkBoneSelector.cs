// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;

namespace Vixen.Net.Animation;

/// <summary>Where a <see cref="NetworkBoneSelection" /> comes from.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="NetworkBoneSelection.Joints" /> was written in exactly one place in the
///         repository and it was a test</b>
///         (<see href="https://github.com/Rikarin/Vixen/issues/481" />). Both pose systems bail out
///         when it is null, so in any real build the whole networked-pose path was a no-op by
///         construction — correct code, reachable by nothing, and the assembly's README costing out a
///         path no shipped configuration could take.
///     </para>
///     <para>
///         <b>The selection is not replicated, so it has to be a function of content.</b> That is
///         <see cref="NetworkBoneSelection" />'s own rule and it is what makes these static: given the
///         same rig, two peers compute the same array without a message, in the same order, and the
///         wire layout means the same thing at both ends. Anything drawn from run-time state — a
///         camera, a distance, a random subset — would silently disagree.
///     </para>
///     <para>
///         <b>Ordered root-first, because <see cref="NetworkBonePrecision" /> is indexed by slot.</b>
///         A rotation's error compounds down the chain, so the joint nearest the root is the one whose
///         precision everything below it inherits, and a narrowed table is only meaningful if slot 0
///         is the pelvis on every rig in the game. Breadth-first from the root is what makes that true
///         without anybody maintaining a list.
///     </para>
/// </remarks>
public static class NetworkBoneSelector {
    /// <summary>The joints nearest the root, in breadth-first order, capped.</summary>
    /// <param name="skeleton">The rig.</param>
    /// <param name="max">How many to take. Clamped to what one <see cref="NetworkBones" /> holds.</param>
    /// <returns>The joint indices, root first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="skeleton" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="max" /> is not positive.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The default, and it is a defensible one rather than a placeholder.</b> A ragdoll's
    ///         driven bones are the pelvis, the spine, the head and four limbs — eighteen or so, all
    ///         of them within four levels of the root — and the joints a breadth-first walk drops are
    ///         the fingers, the toes and the facial rig, which is exactly the set whose error reaches
    ///         nothing and is watched by nobody.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Breadth-first rather than depth-first, and the difference is the whole point.</b>
    ///         Depth-first with a cap of twenty-four spends the budget walking one arm to the
    ///         fingertips and never reaches the other one. The failure is a character with one animated
    ///         limb, which reads as a broken clip rather than as a selection.
    ///     </para>
    /// </remarks>
    public static int[] Trunk(Skeleton skeleton, int max = NetworkBonesReplicator.MaxBones) {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);

        var limit = Math.Min(max, NetworkBonesReplicator.MaxBones);
        var chosen = new List<int>(Math.Min(limit, skeleton.JointCount));
        var frontier = new List<int>();
        var next = new List<int>();

        for (var joint = 0; joint < skeleton.JointCount; joint++) {
            if (skeleton.ParentOf(joint) < 0) {
                frontier.Add(joint);
            }
        }

        // A rig with no root at all — every parent index pointing at something — cannot be walked, and
        // an empty selection is the honest answer. `Skeleton.TryCreate` refuses cycles, so this is
        // only reachable for a skeleton with no joints.
        while (frontier.Count > 0 && chosen.Count < limit) {
            foreach (var joint in frontier) {
                if (chosen.Count == limit) {
                    break;
                }

                chosen.Add(joint);
            }

            next.Clear();

            foreach (var joint in frontier) {
                for (var child = 0; child < skeleton.JointCount; child++) {
                    if (skeleton.ParentOf(child) == joint) {
                        next.Add(child);
                    }
                }
            }

            (frontier, next) = (next, frontier);
        }

        return [.. chosen];
    }

    /// <summary>The joints a game named, in the order it named them.</summary>
    /// <param name="skeleton">The rig.</param>
    /// <param name="names">The joint names, most important first.</param>
    /// <returns>Their indices. A name the rig does not have is dropped.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A missing name is dropped rather than throwing, and the order is the caller's.</b>
    ///     Dropping is the same decision <c>NetworkBonesCaptureSystem</c> already makes about a joint
    ///     index the rig does not have — a rig re-exported with fewer joints is content changing under
    ///     code, and it presents as a limb that does not move, which gets reported. Throwing at load
    ///     would take the whole character down instead. But the order is deliberately <i>not</i>
    ///     compacted or sorted: a narrowed <see cref="NetworkBonePrecision" /> is indexed by slot, and
    ///     silently renumbering the slots would give every bone somebody else's bit budget.
    /// </remarks>
    public static int[] Named(Skeleton skeleton, IReadOnlyList<string> names) {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(names);

        var chosen = new List<int>(Math.Min(names.Count, NetworkBonesReplicator.MaxBones));

        foreach (var name in names) {
            if (chosen.Count == NetworkBonesReplicator.MaxBones) {
                break;
            }

            var joint = skeleton.IndexOf(name);

            if (joint >= 0) {
                chosen.Add(joint);
            }
        }

        return [.. chosen];
    }
}
