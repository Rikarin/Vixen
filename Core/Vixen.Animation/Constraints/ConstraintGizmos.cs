// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;

namespace Vixen.Animation.Constraints;

/// <summary>What the gizmo pass draws, and how far off counts as bad.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="Tolerance" /> is what turns a number into a colour</b>, and it is per
///         project rather than per goal: two centimetres is a miss on a hand and nothing at all on a
///         hip, but an author scanning a viewport needs one scale to read, not one per goal kind.
///     </para>
///     <para>
///         ⚠ <b><c>default</c> is a quiet style, not the usual one</b>, and the difference cost a
///         round of failing tests. A struct's property initialisers do not run for <c>default</c>, so
///         a zeroed style has every flag off — and a zero tolerance would have graded every goal
///         fully red. The numbers read their zero as "use the usual value"; the flags cannot, so
///         zeroed means chains, shapes and labels off, leaving only the misses. That is a real style
///         somebody would want, which is the only reason it is allowed to be the zero one.
///     </para>
/// </remarks>
public readonly record struct ConstraintGizmoStyle {
    /// <summary>How far off a hand may be before an author would call it wrong, in metres.</summary>
    public const float DefaultTolerance = 0.02f;

    /// <summary>The same question for a rotation, in degrees.</summary>
    public const float DefaultAngularTolerance = 2f;

    /// <summary>How big a frame's axes are drawn, in metres.</summary>
    public const float DefaultSize = 0.06f;

    /// <summary>Everything on, two centimetres and two degrees.</summary>
    public static ConstraintGizmoStyle Default => new();

    /// <summary>
    ///     How far off a position goal may be before the line reads as red, in metres. Zero means
    ///     <see cref="DefaultTolerance" />.
    /// </summary>
    public float Tolerance { get; init; } = DefaultTolerance;

    /// <summary>
    ///     How far off an angular goal may be before the line reads as red, in radians. Zero means
    ///     <see cref="DefaultAngularTolerance" />.
    /// </summary>
    public float AngularTolerance { get; init; } = MathUtil.DegreesToRadians(DefaultAngularTolerance);

    /// <summary>How big the frame axes and effector crosses are, in metres. Zero means the usual.</summary>
    public float Size { get; init; } = DefaultSize;

    /// <summary>Whether the joints a goal is allowed to move are drawn.</summary>
    public bool Chains { get; init; } = true;

    /// <summary>Whether the proxy shape a surface goal lives on is drawn.</summary>
    public bool Shapes { get; init; } = true;

    /// <summary>Whether each goal is labelled with what it is and how far off it is.</summary>
    public bool Readout { get; init; } = true;

    /// <summary>Creates the usual style.</summary>
    public ConstraintGizmoStyle() {
    }

    /// <summary>How big a frame's axes are, with a zeroed style answering the usual size.</summary>
    public float Extent => Size > 0f ? Size : DefaultSize;

    /// <summary>How bad a residual is, in <c>[0, 1]</c>, against whichever tolerance applies.</summary>
    /// <param name="residual">The residual.</param>
    /// <returns>Zero for satisfied, one for a full tolerance out or worse.</returns>
    public float Severity(in ConstraintResidual residual) {
        var limit = residual.Kind is GoalKind.Position or GoalKind.Distance
            ? Tolerance > 0f ? Tolerance : DefaultTolerance
            : AngularTolerance > 0f ? AngularTolerance : MathUtil.DegreesToRadians(DefaultAngularTolerance);

        return MathUtil.Saturate(MathF.Abs(residual.Magnitude) / limit);
    }
}

/// <summary>Draws what a constrained pose is trying to do, and how badly it is failing.</summary>
/// <remarks>
///     <para>
///         <b>An author sees the constraint failing before a build does.</b> That is the whole claim,
///         and everything here serves it: the effector, the place the goal resolved to, the joints the
///         solver was allowed to move, the proxy shape a surface coordinate is anchored to, and — the
///         one that matters — a line from where the effector ended up to where it was wanted, coloured
///         by how far that is.
///     </para>
///     <para>
///         ⚠ <b>It reads <see cref="ConstraintStack.LastSolved" /> rather than re-resolving.</b> A
///         gizmo pass that resolved the frames again would draw a second, subtly different answer —
///         one frame later, against a pose the solver has already moved — and the times that
///         difference is largest are exactly the times somebody has the gizmos on. What is drawn is
///         what happened.
///     </para>
///     <para>
///         ⚠ <b>It draws into <see cref="DebugDraw" />, not into an editor viewport.</b> So it is
///         testable with no window, and a game can switch it on in a debug build to see why a hand is
///         through a wall on a player's machine — which is where these failures actually get reported.
///     </para>
/// </remarks>
public static class ConstraintGizmos {
    static readonly Color4 Chain = new(1f, 0.85f, 0.3f, 0.7f);
    static readonly Color4 Shape = new(0.45f, 0.95f, 0.6f, 0.5f);
    static readonly Color4 Idle = new(0.6f, 0.63f, 0.68f, 0.6f);

    /// <summary>Draws every goal the last solve resolved.</summary>
    /// <param name="draw">Where the lines go.</param>
    /// <param name="stack">The stack that solved them.</param>
    /// <param name="model">The model-space pose, as of after the solve.</param>
    /// <param name="style">What to draw.</param>
    /// <remarks>
    ///     ⚠ <b>Pass the pose from <em>after</em> the solve.</b> The residual is measured against where
    ///     the effector ended up, so drawing it from the pre-solve pose would show a miss that includes
    ///     the correction the solver already made — the line would be longest when the solver was doing
    ///     the most work, which is precisely backwards.
    /// </remarks>
    public static void Draw(
        DebugDraw draw,
        ConstraintStack stack,
        ReadOnlySpan<BoneTransform> model,
        ConstraintGizmoStyle? style = null
    ) {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(stack);

        // ⚠ Nullable rather than `= default`, because a zeroed style is the quiet one and nobody
        // calling this with no arguments means "draw almost nothing".
        var chosen = style ?? ConstraintGizmoStyle.Default;
        var world = stack.WorldTransform;
        var goals = stack.LastSolved;
        var residuals = stack.LastResiduals;

        for (var index = 0; index < goals.Length; index++) {
            One(draw, stack, model, world, goals[index], residuals[index], chosen);
        }
    }

    /// <summary>One line each, for whatever is worth saying about a shape set.</summary>
    /// <param name="draw">Where the lines go.</param>
    /// <param name="shapes">The shapes.</param>
    /// <param name="model">The model-space pose.</param>
    /// <param name="world">Where the character is.</param>
    /// <param name="colour">What colour, or <see langword="null" /> for the usual green.</param>
    /// <remarks>
    ///     Separate from <see cref="Draw" /> because "show me the proxy shapes" is a question somebody
    ///     asks about a body, not about a goal — the shape editor wants all of them and the gizmo pass
    ///     wants the one a goal is anchored to.
    /// </remarks>
    public static void DrawShapes(
        DebugDraw draw,
        ProxyShapes shapes,
        ReadOnlySpan<BoneTransform> model,
        in BoneTransform world,
        Color4? colour = null
    ) {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(shapes);

        var active = shapes.Active;

        for (var index = 0; index < active.Count; index++) {
            if (shapes.TryPose(active[index].Name, model, out var posed)) {
                Outline(draw, posed, world, colour ?? Shape);
            }
        }
    }

    static void One(
        DebugDraw draw,
        ConstraintStack stack,
        ReadOnlySpan<BoneTransform> model,
        in BoneTransform world,
        in ResolvedGoal resolved,
        in ConstraintResidual residual,
        in ConstraintGizmoStyle style
    ) {
        var goal = resolved.Goal;
        var chain = goal.Solved;

        if (goal.Effector < 0 || goal.Effector >= model.Length) {
            return;
        }

        var size = style.Extent;
        var effector = At(world, model[goal.Effector].Translation);

        // The chain first, so the goal's own marks sit on top of it rather than under.
        if (style.Chains && chain.First != chain.Effector) {
            Bones(draw, stack.Skeleton, model, world, chain);
        }

        draw.Cross(effector, size * 0.5f, Idle);

        if (style.Shapes && goal.Goal is SurfaceFrame surface && stack.Shapes is { } shapes
            && shapes.TryPose(surface.Coordinate.Shape, model, out var posed)) {
            Outline(draw, posed, world, Shape);
        }

        var target = Wanted(resolved, model, out var known);

        if (known) {
            var placed = At(world, target);

            draw.Axes(Basis(world, resolved.Frame.Transform), size);

            // ⚠ The miss, and the one line the whole pass exists for. Grey when nothing applied, so a
            // goal that never resolved is visibly different from one that resolved and landed — the
            // two look identical in a residual of zero, and they mean opposite things.
            draw.Line(effector, placed, residual.Ran ? Grade(style.Severity(residual)) : Idle);
        } else {
            draw.Axes(Basis(world, resolved.Frame.Transform), size * 0.6f);
        }

        if (style.Readout) {
            draw.Text(effector + new Vector3(0f, size * 1.6f, 0f), Say(goal, residual, resolved.Weight), Grade(style.Severity(residual)), size * 0.9f);
        }
    }

    /// <summary>Where the goal wanted the effector to be, in model space, where that is a place.</summary>
    /// <remarks>
    ///     An orientation goal wants a rotation and a distance goal wants a separation, so neither has
    ///     a point to draw a line to; both are drawn at their frame instead. Saying so rather than
    ///     drawing a line to the frame origin matters — that line would look like a position goal
    ///     missing by a metre.
    /// </remarks>
    static Vector3 Wanted(in ResolvedGoal resolved, ReadOnlySpan<BoneTransform> model, out bool known) {
        known = true;

        switch (resolved.Goal) {
            case PositionGoal position when position.Mode is GoalMode.Absolute: {
                var joint = model[position.Effector];
                var at = joint.Translation + Quaternion.Transform(position.EffectorOffset * joint.Scale, joint.Rotation);

                return position.Nearest(resolved.Frame, at);
            }

            case PositionGoal position: {
                var joint = model[position.Effector];
                var at = joint.Translation + Quaternion.Transform(position.EffectorOffset * joint.Scale, joint.Rotation);

                return at + resolved.Frame.DirectionToModel(position.Offset);
            }

            case AimGoal aim: {
                var joint = model[aim.Effector];
                var origin = joint.Translation + Quaternion.Transform(aim.Origin * joint.Scale, joint.Rotation);

                return aim.Target(resolved.Frame, origin);
            }

            default:
                known = false;
                return resolved.Frame.Origin;
        }
    }

    static void Bones(
        DebugDraw draw,
        Skeleton skeleton,
        ReadOnlySpan<BoneTransform> model,
        in BoneTransform world,
        ChainSpec chain
    ) {
        // Walked from the effector upwards, because that is the direction the parent links point and
        // the only one that terminates: a chain whose first joint is not an ancestor of its effector
        // is a mis-authored chain, and this stops at the root rather than looping.
        for (var joint = chain.Effector; joint > 0 && joint != chain.First;) {
            var parent = skeleton.ParentOf(joint);

            if (parent < 0 || parent >= model.Length) {
                break;
            }

            draw.Line(At(world, model[joint].Translation), At(world, model[parent].Translation), Chain);
            joint = parent;
        }
    }

    /// <summary>Draws a posed proxy shape at whatever fidelity its kind deserves.</summary>
    static void Outline(DebugDraw draw, in ProxyShapePose posed, in BoneTransform world, Color4 colour) {
        var at = posed.Transform;
        var extents = posed.Dimensions.Extents * at.Scale;
        var centre = At(world, at.Translation);
        var up = Quaternion.Transform(Vector3.Up, Quaternion.Concatenate(at.Rotation, world.Rotation));

        switch (posed.Shape.Kind) {
            case ShapeKind.Sphere:
                draw.Sphere(new BoundingSphere(centre, MathF.Max(extents.X, MathF.Max(extents.Y, extents.Z))), colour);
                break;

            case ShapeKind.Capsule or ShapeKind.TaperedCapsule or ShapeKind.Cylinder:
                draw.Capsule(centre - (up * extents.Y), centre + (up * extents.Y), extents.X, colour);
                break;

            case ShapeKind.Cone:
                draw.Cone(centre + (up * extents.Y), -up, extents.X, colour);
                break;

            default:
                draw.Box(
                    new BoundingBox(-extents, extents),
                    Matrix4x4.Compose(Vector3.One, Quaternion.Concatenate(at.Rotation, world.Rotation), centre),
                    colour
                );

                break;
        }
    }

    static string Say(ConstraintGoal goal, in ConstraintResidual residual, float weight) {
        var name = goal.Label == default ? goal.Kind.ToString() : goal.Label.ToString();

        if (!residual.Ran) {
            return $"{name}: unresolved";
        }

        var off = residual.Kind is GoalKind.Position or GoalKind.Distance
            ? string.Create(CultureInfo.InvariantCulture, $"{residual.Magnitude * 100f:0.#} cm")
            : string.Create(CultureInfo.InvariantCulture, $"{MathUtil.RadiansToDegrees(residual.Magnitude):0.#}°");

        return string.Create(CultureInfo.InvariantCulture, $"{name}: {off} at {weight:0.##}");
    }

    /// <summary>Green through amber to red. The one thing an author reads without stopping to think.</summary>
    static Color4 Grade(float severity) =>
        new(
            MathUtil.Lerp(0.25f, 0.95f, severity),
            MathUtil.Lerp(0.85f, 0.25f, severity),
            0.3f,
            1f
        );

    static Vector3 At(in BoneTransform world, Vector3 model) =>
        world.Translation + Quaternion.Transform(model * world.Scale, world.Rotation);

    static Matrix4x4 Basis(in BoneTransform world, in BoneTransform frame) =>
        Matrix4x4.Compose(
            Vector3.One,
            Quaternion.Concatenate(frame.Rotation, world.Rotation),
            At(world, frame.Translation)
        );
}
