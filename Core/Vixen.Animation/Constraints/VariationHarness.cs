// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>How bad a run is allowed to get before it counts as a failure.</summary>
/// <remarks>
///     ⚠ <b>Every one of them off by default, and that is deliberate.</b> A threshold nobody chose is
///     a build that fails for a reason nobody agreed to; a project sets the ones it means and leaves
///     the rest, and <see cref="VariationReport.Judge" /> only looks at the ones that were set.
/// </remarks>
public readonly record struct HarnessThresholds {
    /// <summary>How far a goal may miss by, in metres. Zero means the run is not judged on it.</summary>
    public float Residual { get; init; }

    /// <summary>How far into a surface a contact may sink, in metres. Zero means unjudged.</summary>
    public float Penetration { get; init; }

    /// <summary>
    ///     How hard an effector may change velocity, in metres a second squared. Zero means unjudged.
    /// </summary>
    public float Jerk { get; init; }

    /// <summary>Whether a chain running out of reach is a failure rather than a note.</summary>
    public bool Reach { get; init; }

    /// <summary>Whether a joint hitting the end of its range of motion is a failure.</summary>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Reach" /> because they are different fixes.</b> A straight
    ///     arm that is still short means the contact is somewhere this body cannot get to; a joint at
    ///     its stop means the pose the solver wanted is one this body may not adopt. The first is
    ///     answered by moving the contact, the second by widening the limit or bending elsewhere.
    /// </remarks>
    public bool Limits { get; init; }

    /// <summary>Whether anything at all is judged.</summary>
    public bool Any => Residual > 0f || Penetration > 0f || Jerk > 0f || Reach || Limits;
}

/// <summary>What one goal did across one configuration: the worst of it, and when.</summary>
/// <param name="Variation">Which row of the matrix — an index into <see cref="VariationReport.Cases" />.</param>
/// <param name="Goal">Which column, by the tag's name.</param>
/// <param name="Residual">The worst miss, in the goal kind's own units.</param>
/// <param name="Penetration">How far into the surface the contact sank at worst, in metres.</param>
/// <param name="Jerk">The worst change of effector velocity, in metres a second squared.</param>
/// <param name="Reached">Whether the chain ran out of reach at any point.</param>
/// <param name="Limited">Whether a joint on the chain hit the end of its range of motion.</param>
/// <param name="Ran">Whether the goal resolved at all in this configuration.</param>
/// <param name="At">Where in the clip the worst residual was, in <c>[0, 1]</c>.</param>
/// <remarks>
///     ⚠ <b><see cref="At" /> is what makes the report a tool rather than a verdict.</b> A number
///     saying a clip is wrong somewhere is not actionable; the same number with a configuration and a
///     moment attached drops an author on the frame — which is what the plan means by the worst cell
///     being selectable.
/// </remarks>
public readonly record struct HarnessCell(
    int Variation,
    string Goal,
    float Residual,
    float Penetration,
    float Jerk,
    bool Reached,
    bool Limited,
    bool Ran,
    float At
) {
    /// <summary>Whether this cell breaks any threshold that was set.</summary>
    /// <param name="thresholds">The thresholds.</param>
    /// <returns>Whether it fails.</returns>
    public bool Fails(in HarnessThresholds thresholds) =>
        (thresholds.Residual > 0f && Residual > thresholds.Residual)
        || (thresholds.Penetration > 0f && Penetration > thresholds.Penetration)
        || (thresholds.Jerk > 0f && Jerk > thresholds.Jerk)
        || (thresholds.Reach && Reached)
        || (thresholds.Limits && Limited);

    /// <inheritdoc />
    public override string ToString() =>
        Ran
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{Goal}: {Residual * 100f:0.#} cm, {Penetration * 100f:0.#} cm in, {Jerk:0.#} m/s² at {At:0.##}"
            )
            : $"{Goal}: never resolved";
}

/// <summary>One configuration the harness ran, and what it was.</summary>
/// <param name="Index">Its row in the matrix.</param>
/// <param name="Label">What it was, in words.</param>
/// <param name="Choices">Which value each axis took, in the plan's axis order.</param>
/// <remarks>
///     <see cref="Choices" /> rather than only the label, because the drill-down has to rebuild this
///     exact configuration — a label is for a person and an index per axis is for the editor.
/// </remarks>
public sealed record HarnessCase(int Index, string Label, IReadOnlyList<int> Choices);

/// <summary>The matrix: every configuration against every goal.</summary>
/// <remarks>
///     <para>
///         <b>The answer to "is this clip finished".</b> An author with this knows which clips are
///         done. An author without one re-checks everything by hand every time an artist changes a
///         body, which is the failure this whole phase exists to prevent.
///     </para>
///     <para>
///         ⚠ <b>A goal that never resolved is reported and is not a pass.</b> It is the most common
///         way a variation actually breaks — the hand cannot reach the shape on the small body, the
///         binding resolves to nothing — and a report that showed it as zero error would be worse
///         than no report.
///     </para>
/// </remarks>
public sealed class VariationReport {
    readonly HarnessCell[] cells;

    internal VariationReport(
        IReadOnlyList<HarnessCase> cases,
        IReadOnlyList<string> goals,
        HarnessCell[] cells,
        int samples
    ) {
        Cases = cases;
        Goals = goals;
        Samples = samples;

        this.cells = cells;
    }

    /// <summary>The configurations, one per row.</summary>
    public IReadOnlyList<HarnessCase> Cases { get; }

    /// <summary>The goals, one per column, by the tag's name.</summary>
    public IReadOnlyList<string> Goals { get; }

    /// <summary>How many moments of the clip each configuration was watched at.</summary>
    public int Samples { get; }

    /// <summary>Every cell, row by row.</summary>
    public ReadOnlySpan<HarnessCell> Cells => cells;

    /// <summary>One cell.</summary>
    /// <param name="variation">Its row.</param>
    /// <param name="goal">Its column.</param>
    /// <returns>The cell.</returns>
    public HarnessCell this[int variation, int goal] => cells[(variation * Goals.Count) + goal];

    /// <summary>The cell an author should look at first, or <see langword="null" /> if there are none.</summary>
    /// <param name="thresholds">
    ///     What counts as bad, so a miss and a penetration can be compared. Default judges on the
    ///     residual alone.
    /// </param>
    /// <returns>The worst cell.</returns>
    /// <remarks>
    ///     ⚠ <b>A goal that never resolved outranks any amount of error.</b> Two centimetres out is a
    ///     clip that needs a nudge; not resolving at all is a clip that does nothing on that body, and
    ///     ranking them on magnitude would put the second at the bottom of the list with a zero.
    /// </remarks>
    public HarnessCell? Worst(in HarnessThresholds thresholds = default) {
        HarnessCell? worst = null;
        var score = float.NegativeInfinity;

        foreach (var cell in cells) {
            var value = Rank(cell, thresholds);

            if (value > score) {
                score = value;
                worst = cell;
            }
        }

        return worst;
    }

    static float Rank(in HarnessCell cell, in HarnessThresholds thresholds) {
        if (!cell.Ran) {
            return float.MaxValue;
        }

        var residual = thresholds.Residual > 0f ? cell.Residual / thresholds.Residual : cell.Residual;
        var penetration = thresholds.Penetration > 0f ? cell.Penetration / thresholds.Penetration : cell.Penetration;
        var jerk = thresholds.Jerk > 0f ? cell.Jerk / thresholds.Jerk : 0f;

        return MathF.Max(residual, MathF.Max(penetration, jerk));
    }

    /// <summary>Whether the run passes, and what failed if it does not.</summary>
    /// <param name="thresholds">What counts as a failure.</param>
    /// <returns>The verdict.</returns>
    public HarnessVerdict Judge(in HarnessThresholds thresholds) {
        List<HarnessCell> failed = [];

        if (!thresholds.Any) {
            return new(true, failed, "Nothing was judged: no threshold was set.");
        }

        foreach (var cell in cells) {
            if (!cell.Ran || cell.Fails(thresholds)) {
                failed.Add(cell);
            }
        }

        return new(failed.Count == 0, failed, Summarise(failed, thresholds));
    }

    string Summarise(List<HarnessCell> failed, HarnessThresholds thresholds) {
        if (failed.Count == 0) {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Cases.Count} configuration(s) × {Goals.Count} goal(s) over {Samples} samples: all within tolerance."
            );
        }

        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"{failed.Count} of {cells.Length} cell(s) failed:");

        // ⚠ Worst first and capped. A body range of twenty against eight goals can fail in a hundred
        // and sixty places for one cause, and a log that scrolls is a log nobody reads to the end of.
        foreach (var cell in failed.OrderByDescending(entry => Rank(entry, thresholds)).Take(10)) {
            text.Append(CultureInfo.InvariantCulture, $"\n  {Cases[cell.Variation].Label} — {cell}");
        }

        if (failed.Count > 10) {
            text.Append(CultureInfo.InvariantCulture, $"\n  … and {failed.Count - 10} more.");
        }

        return text.ToString();
    }
}

/// <summary>Whether a run passes, for a build to act on.</summary>
/// <param name="Passed">Whether it did.</param>
/// <param name="Failed">The cells that broke a threshold, or that never resolved.</param>
/// <param name="Summary">What to print.</param>
public readonly record struct HarnessVerdict(bool Passed, IReadOnlyList<HarnessCell> Failed, string Summary);

/// <summary>What the harness runs, and against what.</summary>
public sealed class HarnessPlan {
    /// <summary>The clip, in its authored form.</summary>
    /// <remarks>
    ///     ⚠ <b>The authored form and not a baked <see cref="AnimationClip" />.</b> A baked clip's
    ///     constraint goals hold joint <em>indices</em> resolved against one skeleton, so varying the
    ///     body means baking again — and a harness handed a baked clip would silently pose every body
    ///     with the first one's joint numbering.
    /// </remarks>
    public required AnimationClipContent Clip { get; init; }

    /// <summary>The rig as authored, which the variations start from.</summary>
    public required Skeleton Skeleton { get; init; }

    /// <summary>The proxy shapes built against that rig, if the clip's goals need any.</summary>
    public ProxyShapeSet? Shapes { get; init; }

    /// <summary>The priority ladder the clip's tags name, if it names any.</summary>
    public PriorityLadder? Ladder { get; init; }

    /// <summary>What varies. No axes at all is one configuration, which is a legitimate run.</summary>
    public IReadOnlyList<IVariationSource> Variations { get; init; } = [];

    /// <summary>How many moments of the clip to watch.</summary>
    public int Samples { get; init; } = 32;

    /// <summary>What counts as a failure.</summary>
    public HarnessThresholds Thresholds { get; init; }

    /// <summary>How conflicts resolve, or <see langword="null" /> for the shipped arbiter.</summary>
    public IConstraintArbiter? Arbiter { get; init; }

    /// <summary>How chains are moved, or <see langword="null" /> for the shipped solver.</summary>
    public IChainSolver? Solver { get; init; }
}

/// <summary>Plays an interaction across a range of bodies, props and ground, and measures it.</summary>
/// <remarks>
///     <para>
///         <b>The single highest-value tool in doc 34, and the answer to its first risk.</b> The
///         honest cost of constraints is marking up a library of clips, and what makes that cost
///         bearable is not authoring faster but <em>knowing when to stop</em>.
///     </para>
///     <para>
///         Four measurements, because four different things go wrong: a goal misses
///         (<see cref="HarnessCell.Residual" />), a contact sinks into what it was supposed to rest on
///         (<see cref="HarnessCell.Penetration" />), a hand snaps between two frames
///         (<see cref="HarnessCell.Jerk" />), and a limb runs out of arm
///         (<see cref="HarnessCell.Reached" />).
///     </para>
///     <para>
///         ⚠ <b>"Joint limits hit" is <see cref="HarnessCell.Reached" /> and they are not the same
///         thing.</b> The plan asks for joint limits; no skeleton in this engine carries any, which
///         <see cref="DefaultConstraintArbiter" /> already says. What can be measured is whether the
///         chain was fully extended while the goal was still missing — a straight arm reaching for
///         something out of range — which is the failure a limit would have caught in the cases that
///         matter here. Reporting it under the name it actually has beats reporting a limit check that
///         does not exist.
///     </para>
///     <para>
///         ⚠ <b>Nothing here touches a graphics device, a window or the ECS</b>, so a CI machine runs
///         it exactly as an editor does. That is not a happy accident: it is why the measurements are
///         taken from <see cref="ConstraintStack.LastResiduals" /> and the pose rather than from
///         anything rendered.
///     </para>
/// </remarks>
public static class VariationHarness {
    /// <summary>Runs the whole matrix.</summary>
    /// <param name="plan">What to run.</param>
    /// <returns>The report.</returns>
    public static VariationReport Run(HarnessPlan plan) {
        ArgumentNullException.ThrowIfNull(plan);

        var axes = plan.Variations;
        var cases = Enumerate(axes);
        var goals = plan.Clip.Constraints.Select(static tag => tag.Name.Length > 0 ? tag.Name : tag.Effector).ToArray();
        var samples = Math.Max(2, plan.Samples);
        var cells = new HarnessCell[cases.Count * goals.Length];

        for (var row = 0; row < cases.Count; row++) {
            Measure(plan, cases[row], goals, samples, cells.AsSpan(row * goals.Length, goals.Length));
        }

        return new(cases, goals, cells, samples);
    }

    /// <summary>The configuration one row of a report was, rebuilt.</summary>
    /// <param name="plan">The plan it came from.</param>
    /// <param name="row">The row.</param>
    /// <returns>The subject.</returns>
    /// <remarks>
    ///     The other half of the drill-down: the report says which row and which moment, and this
    ///     turns the row back into the body, the prop and the ground it was — so an editor can put
    ///     that on screen rather than describing it.
    /// </remarks>
    public static HarnessSubject Rebuild(HarnessPlan plan, HarnessCase row) {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(row);

        var subject = new HarnessSubject { Skeleton = plan.Skeleton, Shapes = plan.Shapes };

        for (var axis = 0; axis < plan.Variations.Count; axis++) {
            plan.Variations[axis].Apply(row.Choices[axis], subject);
        }

        return subject;
    }

    /// <summary>Every combination of every axis, in a fixed order.</summary>
    static List<HarnessCase> Enumerate(IReadOnlyList<IVariationSource> axes) {
        List<HarnessCase> cases = [];

        var choices = new int[axes.Count];
        var total = 1;

        foreach (var axis in axes) {
            total *= Math.Max(1, axis.Count);
        }

        for (var index = 0; index < total; index++) {
            var remainder = index;

            // Last axis varies fastest, so a report read top to bottom groups by the first axis —
            // which is the body, which is how somebody reads it.
            for (var axis = axes.Count - 1; axis >= 0; axis--) {
                var count = Math.Max(1, axes[axis].Count);

                choices[axis] = remainder % count;
                remainder /= count;
            }

            var label = new StringBuilder();

            for (var axis = 0; axis < axes.Count; axis++) {
                if (label.Length > 0) {
                    label.Append(" · ");
                }

                label.Append(axes[axis].Label(choices[axis]));
            }

            cases.Add(new(index, label.Length > 0 ? label.ToString() : "as authored", choices.ToArray()));
        }

        return cases;
    }

    static void Measure(
        HarnessPlan plan,
        HarnessCase row,
        string[] goals,
        int samples,
        Span<HarnessCell> into
    ) {
        var subject = Rebuild(plan, row);
        var skeleton = subject.Skeleton;
        var clip = plan.Clip.Bake(skeleton, plan.Ladder);
        var track = clip.Constraints;

        var stack = new ConstraintStack(skeleton, plan.Arbiter, plan.Solver) {
            Shapes = subject.Shapes is null ? null : new ProxyShapes(subject.Shapes)
        };

        foreach (var (slot, where) in subject.Slots) {
            stack.Bindings.Set(slot, new TransformBinding { Transform = where });
        }

        var tags = new ConstraintTagBuffer();
        stack.Tags = tags;

        var pose = new SkeletonPose(skeleton);
        var model = new BoneTransform[skeleton.JointCount];
        var walk = new Playback(goals.Length, samples);

        var delta = MathF.Max(clip.Duration / samples, 1e-4f);

        // ⚠ One loop of warm-up before anything is recorded. Every tag eases in, and a tag ramping up
        // on the first frame of the first loop is doing exactly what it was authored to do — measuring
        // that ramp would flag every clip ever marked up. The state that has to settle is the stack's,
        // so the warm-up runs the same solve and throws the answers away.
        for (var pass = 0; pass < 2; pass++) {
            for (var sample = 0; sample < samples; sample++) {
                var phase = sample / (float)samples;

                // ⚠ From the bind pose every sample, not from the last one. A harness that let the
                // previous frame's correction accumulate would measure its own drift.
                pose.ResetToBindPose();
                clip.Sample(phase * clip.Duration, pose.Bones);

                tags.Clear();
                tags.Collect(track, phase, 1f);

                stack.Solve(pose.Bones, delta);
                SkeletonPose.ComputeModelSpace(skeleton, pose.Bones, model);

                if (pass == 1) {
                    walk.Record(stack, track, pose.Bones, model, subject, sample, phase, delta);
                }
            }
        }

        walk.Reduce(row.Index, goals, into);
    }

    /// <summary>One configuration's samples, kept until the whole clip has been walked.</summary>
    /// <remarks>
    ///     ⚠ <b>Kept rather than folded in as it goes, because the residual cannot be judged until the
    ///     run is over.</b> A goal is worth measuring when it is being asked for in full, and how much
    ///     "in full" is depends on the rest of the clip: a tag at <c>MaxWeight</c> 0.4, or one sharing
    ///     a chain with another, is never at 1.0 and is not therefore failing. Folding the worst
    ///     residual in per sample would score every clip on its own crossfades.
    /// </remarks>
    sealed class Playback(int goals, int samples) {
        readonly float[] applied = new float[goals * samples];
        readonly float[] missed = new float[goals * samples];
        readonly float[] sunk = new float[goals * samples];
        readonly float[] phases = new float[samples];
        readonly Vector3[] previous = new Vector3[goals];
        readonly Vector3[] velocity = new Vector3[goals];
        readonly bool[] seen = new bool[goals];
        readonly bool[] reached = new bool[goals];
        readonly bool[] limited = new bool[goals];
        readonly float[] jerk = new float[goals];

        public void Record(
            ConstraintStack stack,
            ConstraintTrack? track,
            ReadOnlySpan<BoneTransform> local,
            ReadOnlySpan<BoneTransform> model,
            HarnessSubject subject,
            int sample,
            float phase,
            float delta
        ) {
            phases[sample] = phase;

            if (track is null) {
                return;
            }

            var solved = stack.LastSolved;
            var residuals = stack.LastResiduals;

            for (var column = 0; column < goals && column < track.Count; column++) {
                var goal = track[column].Goal;
                var found = -1;

                for (var index = 0; index < solved.Length; index++) {
                    if (ReferenceEquals(solved[index].Goal, goal)) {
                        found = index;
                        break;
                    }
                }

                if (found < 0) {
                    continue;
                }

                var residual = residuals[found];
                var slot = (column * samples) + sample;

                applied[slot] = residual.Applied;
                missed[slot] = MathF.Abs(residual.Magnitude);
                sunk[slot] = Sunk(stack, subject, goal, model);
                reached[column] |= Exhausted(stack, goal, model, residual);
                limited[column] |= AtItsStop(stack, goal, local);

                // The jerk: how hard the effector changed velocity between this sample and the last.
                // A hand that snaps shows here and nowhere else — the residual is small on both sides
                // of a snap, which is exactly what makes a snap invisible in a residual plot.
                var effector = goal.Effector >= 0 && goal.Effector < model.Length
                    ? model[goal.Effector].Translation
                    : Vector3.Zero;

                if (seen[column]) {
                    var now = (effector - previous[column]) / delta;

                    jerk[column] = MathF.Max(jerk[column], (now - velocity[column]).Length() / delta);
                    velocity[column] = now;
                }

                previous[column] = effector;
                seen[column] = true;
            }
        }

        /// <summary>Turns the samples into one cell per goal.</summary>
        public void Reduce(int variation, string[] names, Span<HarnessCell> into) {
            for (var column = 0; column < goals; column++) {
                var peak = 0f;

                for (var sample = 0; sample < samples; sample++) {
                    peak = MathF.Max(peak, applied[(column * samples) + sample]);
                }

                if (peak <= 0f) {
                    into[column] = new(variation, names[column], 0f, 0f, 0f, false, false, false, 0f);
                    continue;
                }

                // Within five percent of the most this goal was ever asked for. Below that it is
                // ramping, being shared, or suppressed, and a miss is what it was told to do.
                var floor = peak * 0.95f;
                var worst = 0f;
                var at = 0f;
                var penetration = 0f;

                for (var sample = 0; sample < samples; sample++) {
                    var slot = (column * samples) + sample;

                    penetration = MathF.Max(penetration, sunk[slot]);

                    if (applied[slot] < floor || missed[slot] <= worst) {
                        continue;
                    }

                    worst = missed[slot];
                    at = phases[sample];
                }

                into[column] = new(
                    variation,
                    names[column],
                    worst,
                    penetration,
                    jerk[column],
                    reached[column],
                    limited[column],
                    true,
                    at
                );
            }
        }
    }

    /// <summary>How far into the shape a surface contact sank, in metres, or zero.</summary>
    /// <remarks>
    ///     ⚠ <b>Only a surface goal can be asked this, and that is the whole of the honest answer.</b>
    ///     Interpenetration between two arbitrary proxy shapes is a collision query this engine's
    ///     animation side does not have — but a contact goal already names the shape it is supposed to
    ///     rest <em>on</em>, and "the hand is a centimetre inside the table" is the case an author
    ///     actually needs told about.
    /// </remarks>
    static float Sunk(
        ConstraintStack stack,
        HarnessSubject subject,
        ConstraintGoal goal,
        ReadOnlySpan<BoneTransform> model
    ) {
        if (subject.Shapes is null || stack.Shapes is not { } shapes
            || goal.Goal is not SurfaceFrame surface
            || goal is not PositionGoal position
            || !shapes.TryPose(surface.Coordinate.Shape, model, out var posed)
            || goal.Effector < 0 || goal.Effector >= model.Length) {
            return 0f;
        }

        var joint = model[goal.Effector];
        var at = joint.Translation + Quaternion.Transform(position.EffectorOffset * joint.Scale, joint.Rotation);

        ShapeGeometry.Project(posed.Shape.Kind, posed.Dimensions, posed.ToShape(at), out var residual);

        // The residual's first component is along the surface normal, so a negative one is a point
        // under the surface. Anything at or above it is a contact or a gap, and neither is a failure.
        return MathF.Max(0f, -residual.X);
    }

    /// <summary>Whether any joint the goal may move is sitting at the end of its range.</summary>
    /// <remarks>
    ///     ⚠ <b>Measured on the pose the solve left, which is already clamped.</b> The arbiter cuts an
    ///     illegal rotation before anybody sees it, so "the limit was hit" cannot be detected by
    ///     looking for an illegal pose — there are none. What is detectable is a joint sitting exactly
    ///     at its bound, which is what a clamp produces and what free motion almost never does.
    /// </remarks>
    static bool AtItsStop(ConstraintStack stack, ConstraintGoal goal, ReadOnlySpan<BoneTransform> pose) {
        if (!stack.Skeleton.HasLimits) {
            return false;
        }

        var chain = goal.Solved;
        var bind = stack.Skeleton.BindPose;

        for (var joint = chain.Effector; joint >= 0; joint = joint == chain.First ? -1 : stack.Skeleton.ParentOf(joint)) {
            if ((uint)joint >= (uint)pose.Length) {
                break;
            }

            var limit = stack.Skeleton.LimitOf(joint);

            if (limit.IsFree) {
                continue;
            }

            // Asking the limit to clamp what is already there: if it takes nothing off, the joint is
            // inside its range; if it does, the pose was written by something that does not clamp.
            // Either way the interesting case is the joint that is *exactly* at the bound, which
            // shows up as a clamp of a hair more than nothing.
            limit.Clamp(Nudged(pose[joint].Rotation, bind[joint].Rotation), bind[joint].Rotation, out var cut);

            if (cut) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The same rotation, a thousandth further from the bind pose.</summary>
    /// <remarks>
    ///     A joint at its stop is one where going any further would be clamped, and that is a question
    ///     with no answer unless something tries. A thousandth of a radian is under a twentieth of a
    ///     degree — far below anything an author authored, and far above the float noise a
    ///     decomposition leaves.
    /// </remarks>
    static Quaternion Nudged(Quaternion local, Quaternion bind) {
        var delta = Quaternion.Concatenate(Quaternion.Conjugate(bind), local);

        return Quaternion.Concatenate(bind, AimGoal.ScaleRotation(delta, 1.001f));
    }

    /// <summary>Whether the chain is straight and the goal is still missing.</summary>
    static bool Exhausted(
        ConstraintStack stack,
        ConstraintGoal goal,
        ReadOnlySpan<BoneTransform> model,
        in ConstraintResidual residual
    ) {
        var chain = goal.Solved;

        if (chain.First == chain.Effector || MathF.Abs(residual.Magnitude) <= 1e-3f) {
            return false;
        }

        var span = 0f;
        var joint = chain.Effector;

        while (joint > 0 && joint != chain.First) {
            var parent = stack.Skeleton.ParentOf(joint);

            if (parent < 0 || parent >= model.Length) {
                return false;
            }

            span += (model[joint].Translation - model[parent].Translation).Length();
            joint = parent;
        }

        var straight = (model[chain.Effector].Translation - model[chain.First].Translation).Length();

        // Within a millimetre of the sum of its own bones is a chain with nothing left to give.
        return span - straight <= 1e-3f;
    }
}
