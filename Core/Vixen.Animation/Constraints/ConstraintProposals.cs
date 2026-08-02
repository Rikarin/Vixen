// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>One joint the proposal pass watches, and the surfaces it might be touching.</summary>
/// <param name="Joint">The joint — a wrist, an ankle.</param>
/// <param name="Chain">The joint at the top of what would move to hold it there.</param>
/// <param name="Offset">Where on that joint the contact point is, in the joint's own space.</param>
public readonly record struct ProposalEffector(int Joint, int Chain, Vector3 Offset);

/// <summary>How close counts, and how long counts.</summary>
/// <param name="Distance">How near a surface a point has to be, in metres.</param>
/// <param name="MinimumSpan">
///     How much of the clip it has to stay there for, as a fraction. Below this it is a hand passing
///     by, not a hand resting.
/// </param>
/// <param name="Slack">
///     How far it may drift while still counting as one contact, in metres — the difference between a
///     hand resting and a hand sliding, and a slide should be one proposal rather than forty.
/// </param>
/// <remarks>
///     ⚠ <b>Spelled out rather than written <c>new()</c></b>, for <c>CurveCompressionSettings</c>'s
///     reason: a positional <c>record struct</c>'s parameterless constructor zeroes its fields.
/// </remarks>
public readonly record struct ProposalSettings(
    float Distance = 0.02f,
    float MinimumSpan = 0.08f,
    float Slack = 0.05f
) {
    /// <summary>Two centimetres, held for eight per cent of the clip, drifting under five.</summary>
    public static ProposalSettings Default => new(0.02f, 0.08f, 0.05f);
}

/// <summary>A constraint the editor thinks the animator meant, for a person to accept or reject.</summary>
/// <param name="Tag">The tag it would add.</param>
/// <param name="Shape">Which proxy shape it noticed.</param>
/// <param name="Closest">How near the effector got, in metres.</param>
/// <param name="Drift">How far it moved across the span, in metres.</param>
/// <param name="Confidence">
///     How sure it is, in <c>[0, 1]</c> — near and still and long is one; far, drifting or brief is
///     nearer zero.
/// </param>
/// <remarks>
///     ⚠ <b>A proposal is never applied silently, and the type exists to make that structural.</b> The
///     pass returns descriptions of what it noticed; adding them is a separate call somebody makes.
///     An editor that marked up a clip on open would be an editor whose output nobody could review,
///     and the failure mode of proximity heuristics is confident nonsense.
/// </remarks>
public readonly record struct ConstraintProposal(
    ConstraintTagRecord Tag,
    Symbol Shape,
    float Closest,
    float Drift,
    float Confidence
);

/// <summary>Watches a clip play against a scene and says where the contacts look like they are.</summary>
/// <remarks>
///     <para>
///         <b>Assisted, not automatic.</b> With a clip and the scene it was authored against, this
///         proposes tags from proximity: this hand is within two centimetres of this shape for this
///         span, so offer a position goal with that lifespan. The author accepts, edits or rejects.
///     </para>
///     <para>
///         ⚠ <b>The authoring scene is authoring-time only.</b> What is baked into the tag is a
///         surface coordinate that resolves from the live game alone; the scene exists so the editor
///         can work out what the animator meant, and is then discarded. A constraint that could not be
///         resolved without it would be a bug, not a feature.
///     </para>
/// </remarks>
public static class ConstraintProposals {
    /// <summary>Looks for contacts.</summary>
    /// <param name="skeleton">The rig the clip is played on.</param>
    /// <param name="clip">The clip, baked against that rig.</param>
    /// <param name="shapes">The body's proxy shapes.</param>
    /// <param name="effectors">Which joints to watch.</param>
    /// <param name="settings">How close and how long counts.</param>
    /// <param name="samples">How many moments of the clip to look at.</param>
    /// <returns>What it noticed, most confident first.</returns>
    /// <remarks>
    ///     ⚠ <b>Contacts against the character's <em>own</em> shapes.</b> A hand on a hip, a hand on a
    ///     belly, two hands meeting — the cases a clip carries on its own. A contact against a prop or
    ///     another actor needs the sequencer's scene to say what was there, which the editor supplies
    ///     by posing those actors into the same shape set before calling this.
    /// </remarks>
    public static IReadOnlyList<ConstraintProposal> Find(
        Skeleton skeleton,
        AnimationClip clip,
        ProxyShapeSet shapes,
        IReadOnlyList<ProposalEffector> effectors,
        ProposalSettings settings,
        int samples = 60
    ) {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(effectors);

        var count = Math.Max(samples, 2);
        var pose = new BoneTransform[skeleton.JointCount];
        var model = new BoneTransform[skeleton.JointCount];
        var posed = new ProxyShapes(shapes);

        List<ConstraintProposal> found = [];

        // One walk per effector rather than one over the whole clip, because a run is a property of
        // one effector against one shape and interleaving them would need a state machine per pair
        // anyway. A clip has a handful of effectors and a hundred samples.
        foreach (var effector in effectors) {
            if ((uint)effector.Joint >= (uint)skeleton.JointCount) {
                continue;
            }

            for (var index = 0; index < shapes.Count; index++) {
                var shape = shapes[index];

                if (shape.Joint == effector.Joint || skeleton.IsDescendantOf(shape.Joint, effector.Joint)) {
                    // A shape hanging off the effector itself travels with it, so it is always within
                    // two centimetres and always would be. Every one of those is a false proposal.
                    continue;
                }

                Walk(skeleton, clip, posed, pose, model, effector, shape, settings, count, found);
            }
        }

        found.Sort(static (left, right) => right.Confidence.CompareTo(left.Confidence));
        return found;
    }

    static void Walk(
        Skeleton skeleton,
        AnimationClip clip,
        ProxyShapes shapes,
        BoneTransform[] pose,
        BoneTransform[] model,
        in ProposalEffector effector,
        ProxyShape shape,
        in ProposalSettings settings,
        int samples,
        List<ConstraintProposal> found
    ) {
        var open = -1;
        var closest = float.MaxValue;
        var first = SurfacePoint.Side;
        var last = SurfacePoint.Side;
        var start = Vector3.Zero;
        var drift = 0f;

        for (var index = 0; index < samples; index++) {
            var phase = index / (float)(samples - 1);

            clip.Sample(phase * clip.Duration, pose);
            SkeletonPose.ComputeModelSpace(skeleton, pose, model);
            shapes.Invalidate();

            var touching = false;
            var here = Vector3.Zero;
            var where = SurfacePoint.Side;
            var gap = float.MaxValue;

            if (shapes.TryPose(shape.Name, model, out var placed)) {
                here = model[effector.Joint].Translation
                    + Quaternion.Transform(effector.Offset, model[effector.Joint].Rotation);

                where = ShapeGeometry.Project(placed.Shape.Kind, placed.Dimensions, placed.ToShape(here), out var residual);
                gap = (residual * placed.Transform.Scale).Length();
                touching = gap <= settings.Distance;
            }

            if (touching) {
                if (open < 0) {
                    open = index;
                    closest = gap;
                    first = where;
                    start = here;
                    drift = 0f;
                }

                closest = MathF.Min(closest, gap);
                drift = MathF.Max(drift, (here - start).Length());
                last = where;

                continue;
            }

            Close(shape, effector, settings, samples, open, index, closest, drift, first, last, found);
            open = -1;
        }

        Close(shape, effector, settings, samples, open, samples, closest, drift, first, last, found);
    }

    static void Close(
        ProxyShape shape,
        in ProposalEffector effector,
        in ProposalSettings settings,
        int samples,
        int open,
        int end,
        float closest,
        float drift,
        SurfacePoint first,
        SurfacePoint last,
        List<ConstraintProposal> found
    ) {
        if (open < 0) {
            return;
        }

        var begin = open / (float)(samples - 1);
        var finish = (end - 1) / (float)(samples - 1);
        var span = finish - begin;

        if (span < settings.MinimumSpan) {
            // A hand passing by, not a hand resting. Proposing it is how an author learns to ignore
            // the whole feature.
            return;
        }

        // The middle of the run, so a contact that drifted a little is proposed where it was most of
        // the time rather than where it happened to start.
        var point = new SurfacePoint(
            first.Face,
            MathUtil.Lerp(first.U, last.U, 0.5f),
            MathUtil.Lerp(first.V, last.V, 0.5f)
        );

        found.Add(
            new(
                new ConstraintTagRecord {
                    Name = $"{shape.Name} · {effector.Joint}",
                    Kind = GoalKind.Position,
                    Effector = string.Empty,
                    Begin = begin,
                    End = finish,

                    // A tenth of the span at each end, because a contact that snaps on is the first
                    // thing an author has to fix by hand and the default should not create work.
                    EaseIn = span * 0.1f,
                    EaseOut = span * 0.1f,
                    EffectorOffset = effector.Offset,
                    Goal = new() {
                        Kind = ConstraintFrameKind.Surface,
                        Shape = shape.Name.ToString(),
                        Origin = OriginSource.Surface,
                        Face = point.Face,
                        U = point.U,
                        V = point.V,
                        Scale = ScaleSource.Model
                    }
                },
                shape.Name,
                closest,
                drift,
                Confidence(settings, span, closest, drift)
            )
        );
    }

    /// <summary>Near, still and long is one. Far, drifting or brief is nearer zero.</summary>
    /// <remarks>
    ///     A product rather than a sum, so any one of the three being bad is enough to sink it — which
    ///     is the behaviour wanted from a heuristic whose failure mode is confident nonsense.
    /// </remarks>
    static float Confidence(in ProposalSettings settings, float span, float closest, float drift) {
        var near = 1f - MathUtil.Saturate(closest / MathF.Max(settings.Distance, 1e-4f));
        var still = 1f - MathUtil.Saturate(drift / MathF.Max(settings.Slack, 1e-4f));
        var held = MathUtil.Saturate(span / MathF.Max(settings.MinimumSpan * 4f, 1e-4f));

        return MathUtil.Saturate(((near * 0.5f) + 0.5f) * ((still * 0.5f) + 0.5f) * held);
    }
}
