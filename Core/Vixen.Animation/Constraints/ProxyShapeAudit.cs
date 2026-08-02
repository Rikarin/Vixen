// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>What is worth telling somebody about a shape set before they ship it.</summary>
/// <remarks>
///     <para>
///         Separate from <see cref="ShapeVocabulary" />'s check, which asks whether a set says the
///         right words. This asks whether it is any <em>good</em>: a shape that never moves is a
///         shape somebody attached to the wrong joint, two shapes deeply inside each other are a
///         contact that will resolve to the wrong one, and a name in one body's set and missing from
///         another's is the failure that makes a clip work on one character and not another.
///     </para>
///     <para>
///         ⚠ <b>None of these is an error and all of them are worth reading.</b> A shape that never
///         moves is legitimate on a prop; two shapes overlapping is legitimate at a shoulder. What is
///         wanted is a list an author scans once, not a build that refuses.
///     </para>
/// </remarks>
public static class ProxyShapeAudit {
    /// <summary>Looks for the three things worth saying about a set.</summary>
    /// <param name="set">The shapes.</param>
    /// <param name="skeleton">The rig they hang off.</param>
    /// <param name="motion">
    ///     Clips to play while watching, or empty. Without them, "never moves" cannot be answered and
    ///     is not attempted.
    /// </param>
    /// <param name="samples">How many moments of each clip to look at.</param>
    /// <returns>What it found.</returns>
    public static IReadOnlyList<ShapeValidation> Audit(
        ProxyShapeSet set,
        Skeleton skeleton,
        IReadOnlyList<AnimationClip>? motion = null,
        int samples = 24
    ) {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(skeleton);

        List<ShapeValidation> found = [];

        Still(set, skeleton, motion, samples, found);
        Overlapping(set, skeleton, found);

        return found;
    }

    /// <summary>Names one set has and another does not, in both directions.</summary>
    /// <param name="left">One set.</param>
    /// <param name="right">The other.</param>
    /// <returns>What it found.</returns>
    /// <remarks>
    ///     ⚠ <b>The most important of the three, and the one an author cannot notice alone.</b> A clip
    ///     naming <c>left-palm</c> works on the body that has one and silently does nothing on the body
    ///     that calls it <c>palm-l</c> — and nothing about either set, read on its own, says so.
    /// </remarks>
    public static IReadOnlyList<ShapeValidation> Compare(ProxyShapeSet left, ProxyShapeSet right) {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        List<ShapeValidation> found = [];

        Missing(left, right, found);
        Missing(right, left, found);

        return found;
    }

    static void Missing(ProxyShapeSet have, ProxyShapeSet want, List<ShapeValidation> found) {
        foreach (var shape in have.Shapes) {
            if (want.IndexOf(shape.Name) >= 0) {
                continue;
            }

            found.Add(
                new(
                    shape.Name,
                    $"'{have.Name}' has a shape called '{shape.Name}' and '{want.Name}' does not. A clip with a "
                    + $"contact there works on one of these bodies and silently does nothing on the other."
                )
            );
        }
    }

    static void Still(
        ProxyShapeSet set,
        Skeleton skeleton,
        IReadOnlyList<AnimationClip>? motion,
        int samples,
        List<ShapeValidation> found
    ) {
        if (motion is null || motion.Count == 0) {
            return;
        }

        var pose = new BoneTransform[skeleton.JointCount];
        var model = new BoneTransform[skeleton.JointCount];
        var shapes = new ProxyShapes(set);
        var moved = new bool[set.Count];
        var first = new Vector3[set.Count];
        var seen = false;

        foreach (var clip in motion) {
            if (clip.Skeleton != skeleton) {
                continue;
            }

            for (var index = 0; index < Math.Max(samples, 2); index++) {
                clip.Sample(index / (float)(Math.Max(samples, 2) - 1) * clip.Duration, pose);
                SkeletonPose.ComputeModelSpace(skeleton, pose, model);
                shapes.Invalidate();

                for (var at = 0; at < set.Count; at++) {
                    if (!shapes.TryPose(set[at].Name, model, out var placed)) {
                        continue;
                    }

                    if (!seen) {
                        first[at] = placed.Transform.Translation;
                        continue;
                    }

                    moved[at] |= (placed.Transform.Translation - first[at]).LengthSquared() > 1e-6f;
                }

                seen = true;
            }
        }

        if (!seen) {
            return;
        }

        for (var at = 0; at < set.Count; at++) {
            if (moved[at]) {
                continue;
            }

            found.Add(
                new(
                    set[at].Name,
                    $"'{set[at].Name}' never moves across the clips it was checked against. That is right for a "
                    + $"prop and usually means a shape was attached to the wrong joint — it hangs off "
                    + $"'{skeleton.NameOf(set[at].Joint)}'."
                )
            );
        }
    }

    /// <summary>
    ///     Pairs whose enclosing spheres are deeply inside each other and which are not on the same
    ///     limb.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Only <em>adjacent</em> joints are exempt, and "related" was the wrong word for it.</b> A
    ///     shoulder overlaps an upper arm by design and always will, so a shape and its parent's shape
    ///     say nothing. Exempting every ancestor exempts almost everything — a hand is a descendant of
    ///     the spine, so a hand buried in the belly would never be reported, which is the single most
    ///     useful thing this pass could say.
    /// </remarks>
    static void Overlapping(ProxyShapeSet set, Skeleton skeleton, List<ShapeValidation> found) {
        var pose = new SkeletonPose(skeleton);
        var model = new BoneTransform[skeleton.JointCount];
        var shapes = new ProxyShapes(set);

        pose.ComputeModelSpace(model);

        for (var left = 0; left < set.Count; left++) {
            if (!shapes.TryPose(set[left].Name, model, out var one)) {
                continue;
            }

            for (var right = left + 1; right < set.Count; right++) {
                if (Adjacent(skeleton, set[left].Joint, set[right].Joint)
                    || !shapes.TryPose(set[right].Name, model, out var other)) {
                    continue;
                }

                var reach = Radius(one) + Radius(other);
                var apart = (one.Transform.Translation - other.Transform.Translation).Length();
                var into = reach - apart;

                if (into <= MathF.Min(Radius(one), Radius(other))) {
                    continue;
                }

                found.Add(
                    new(
                        set[left].Name,
                        $"'{set[left].Name}' and '{set[right].Name}' sit {into:0.###} m inside one another in the "
                        + $"bind pose, and they are not on adjacent joints ('{skeleton.NameOf(set[left].Joint)}' and "
                        + $"'{skeleton.NameOf(set[right].Joint)}'). A contact near the seam resolves to whichever "
                        + "one the author did not mean."
                    )
                );
            }
        }
    }

    static bool Adjacent(Skeleton skeleton, int left, int right) =>
        left == right
        || skeleton.ParentOf(left) == right
        || skeleton.ParentOf(right) == left
        || skeleton.ParentOf(left) == skeleton.ParentOf(right);

    static float Radius(in ProxyShapePose posed) {
        var extents = Vector3.Max(posed.Dimensions.Extents, posed.Dimensions.TopExtents) * posed.Transform.Scale;
        return MathF.Max(MathF.Max(extents.X, extents.Y), extents.Z);
    }
}
