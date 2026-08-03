// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Cameras;

namespace Vixen.Animation.Constraints;

/// <summary>A camera as a constraint solve sees it: where it is, and what it sees.</summary>
/// <param name="Transform">Where it is and which way it looks.</param>
/// <param name="Lens">Its field of view.</param>
/// <param name="Aspect">Width over height.</param>
public readonly record struct CameraView(BoneTransform Transform, CameraLens Lens, float Aspect);

/// <summary>The body a shot is composed against.</summary>
/// <remarks>
///     A <c>ref struct</c> for <see cref="ConstraintContext" />'s reason: it carries a pose, and
///     nothing may hold one past the solve that produced it.
/// </remarks>
public readonly ref struct CameraSubject {
    /// <summary>The subject's skeleton.</summary>
    public required Skeleton Skeleton { get; init; }

    /// <summary>Its pose, in model space.</summary>
    public required ReadOnlySpan<BoneTransform> Model { get; init; }

    /// <summary>Where it is in the world.</summary>
    public BoneTransform WorldTransform { get; init; }

    /// <summary>Its proxy shapes, for a coordinate on one.</summary>
    public ProxyShapes? Shapes { get; init; }
}

/// <summary>A place in the picture, resolved to where the camera would have to be to put it there.</summary>
/// <param name="Subject">Where the thing being framed is — a joint, or a coordinate on a shape.</param>
/// <param name="Screen">
///     Where it should land, in normalised device coordinates: <c>(0, 0)</c> the centre, <c>±1</c> the
///     edges.
/// </param>
/// <param name="Region">
///     How far off that it may be and still count — a dead zone, in the same units.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>An <see cref="IConstraintFrame" /> that answers a question about the camera rather
///         than about the subject, and that inversion is the whole trick.</b> Every other frame says
///         "the goal is here". This one says "for the subject to be <em>there</em> in the picture, the
///         camera would have to be here" — which turns a framing constraint into an ordinary position
///         goal on a rigid body, and lets it average, weight, prioritise and arbitrate with the world
///         volume that bounds where the camera may go.
///     </para>
///     <para>
///         <b>It slides rather than dollies.</b> The camera is placed at the subject's current depth,
///         so a goal that wants a head in the upper third moves the camera sideways and up, not
///         backwards. A shot that should also change its distance says so with a second goal.
///     </para>
///     <para>
///         <b>Two cases an authored shot cannot cover.</b> Framing survives body variation — a shot
///         composed against one character puts a taller one's head out of frame, and holding the
///         <em>composition</em> rather than the transform is what keeps the picture. And the camera
///         keeps out of the geometry, because a region goal in world space bounds where it may go.
///     </para>
/// </remarks>
public sealed record ScreenFrame(IConstraintFrame Subject, Vector2 Screen, Vector2 Region) : IConstraintFrame {
    /// <summary>A place in the picture, with no dead zone.</summary>
    /// <param name="subject">Where the thing being framed is.</param>
    /// <param name="screen">Where it should land.</param>
    public ScreenFrame(IConstraintFrame subject, Vector2 screen) : this(subject, screen, Vector2.Zero) {
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Fails when there is no camera in the context — a screen frame on a pose solve is a category
    ///     error rather than a misconfiguration — and when the subject is behind the lens, where the
    ///     question has no answer.
    /// </remarks>
    public bool TryResolve(in ConstraintContext context, out Frame frame) {
        ArgumentNullException.ThrowIfNull(Subject);

        frame = default;

        if (context.View is not { } view || !Subject.TryResolve(context, out var subject)) {
            return false;
        }

        // The subject resolves in the character's model space; the camera lives in the world.
        var point = BoneTransform.Concatenate(
            new BoneTransform(subject.Origin, subject.Rotation, Vector3.One),
            context.WorldTransform
        ).Translation;

        var camera = view.Transform;

        if (!CameraFraming.Project(point, camera.Translation, camera.Rotation, view.Lens, view.Aspect, out var at, out var depth)) {
            return false;
        }

        var wanted = new Vector2(
            Nearest(at.X, Screen.X, MathF.Abs(Region.X)),
            Nearest(at.Y, Screen.Y, MathF.Abs(Region.Y))
        );

        var extents = CameraFraming.Extents(view.Lens, view.Aspect);

        // Where the subject would have to sit in view space to land at `wanted`, at the depth it is
        // actually at — and therefore where the camera has to be for that to be true.
        var offset = view.Lens.Orthographic
            ? new Vector3(wanted.X * extents.X, wanted.Y * extents.Y, -depth)
            : new Vector3(wanted.X * depth * extents.X, wanted.Y * depth * extents.Y, -depth);

        frame = new(
            new BoneTransform(point - Quaternion.Transform(offset, camera.Rotation), camera.Rotation, Vector3.One)
        );

        return true;
    }

    /// <summary>The nearest acceptable place, which inside a dead zone is where it already is.</summary>
    static float Nearest(float at, float wanted, float slack) =>
        slack <= 0f ? wanted : MathUtil.Clamp(at, wanted - slack, wanted + slack);
}

/// <summary>What a camera solve did.</summary>
/// <param name="View">Where the camera ended up.</param>
/// <param name="Moved">How far it had to move, in metres.</param>
/// <param name="Turned">How far it had to turn, in radians.</param>
/// <param name="Applied">How many goals contributed.</param>
public readonly record struct CameraCorrection(CameraView View, float Moved, float Turned, int Applied);

/// <summary>Every goal a camera is under, solved as one rigid body after its shot.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This extends the virtual-camera system and does not compete with it.</b> A shot still
///         decides where the camera is and what it looks at; the director still picks and blends. What
///         happens here is a <em>correction applied to the shot's output</em>, in exactly the
///         relationship the pose solve has to the blended pose — which is also why goals labelled
///         <see cref="ConstraintLabels.Camera" /> are solved here and excluded everywhere else. If the
///         composer already frames a subject adequately, nothing here is needed; the case for it is
///         precisely the case where the subject's <em>size</em> is what changed.
///     </para>
///     <para>
///         A camera has a transform and no skeleton, which makes it the same problem as a character's
///         root placement — and <see cref="RigidBodySolver" /> is the same code for both.
///     </para>
/// </remarks>
public sealed class CameraConstraints {
    readonly List<ConstraintHandle> handles = [];
    readonly ConstraintStack owner;

    ResolvedGoal[] resolved = [];

    /// <summary>Creates a set of camera goals.</summary>
    public CameraConstraints() {
        // The handle machinery belongs to a stack, and a camera's goals want the same handles — the
        // same Add, the same weight, the same ease-out on release. A private stack over a one-joint
        // skeleton is cheaper than a second handle type that behaves almost the same way.
        owner = new(Skeleton.Create(new() { Name = "Camera", Joints = [new() { Name = "Camera", Parent = -1 }] }));
        Bindings = owner.Bindings;
    }

    /// <summary>Who the other parties are — the entities a shot frames.</summary>
    public ConstraintBindings Bindings { get; }

    /// <summary>The goals, in the order they were added.</summary>
    public IReadOnlyList<ConstraintHandle> Goals => handles;

    /// <summary>Adds a goal.</summary>
    /// <param name="goal">What it asks for.</param>
    /// <returns>The handle. Dispose it to drop the goal.</returns>
    /// <remarks>
    ///     A goal here needs no effector: the camera is the effector. <c>Effector = 0</c> is the
    ///     conventional way to say so.
    /// </remarks>
    public ConstraintHandle Add(ConstraintGoal goal) {
        ArgumentNullException.ThrowIfNull(goal);

        var handle = new ConstraintHandle(owner, goal);
        handles.Add(handle);

        return handle;
    }

    /// <summary>Corrects a shot.</summary>
    /// <param name="shot">Where the director put the camera.</param>
    /// <param name="subject">The body it is composed against.</param>
    /// <returns>Where it should actually be.</returns>
    /// <remarks>
    ///     ⚠ <b>Called after the director has picked and blended, and before the matrix is built.</b>
    ///     Any earlier and the correction is to a shot that is about to be replaced; any later and the
    ///     frame has already been rendered from the uncorrected one.
    /// </remarks>
    public CameraCorrection Solve(in CameraView shot, in CameraSubject subject) {
        if (resolved.Length < handles.Count) {
            resolved = new ResolvedGoal[Math.Max(8, handles.Count)];
        }

        var context = new ConstraintContext {
            Skeleton = subject.Skeleton,
            Model = subject.Model,
            Bindings = Bindings,
            WorldTransform = subject.WorldTransform,
            Shapes = subject.Shapes,
            View = shot
        };

        var count = 0;

        foreach (var handle in handles) {
            if (handle.Released || handle.Weight <= 0f) {
                continue;
            }

            var goal = handle.Goal;
            var frame = Frame.Identity;

            // A subject that has left the scene, or a screen frame on a camera that cannot see it.
            // Dropping the goal leaves the shot as the director composed it, which is the right
            // fallback: an uncorrected shot is a shot somebody authored.
            if (goal.Goal is not null && !goal.Goal.TryResolve(context, out frame)) {
                handle.Residual = default;
                continue;
            }

            resolved[count++] = new(goal, frame, MathUtil.Saturate(goal.Weight) * MathUtil.Saturate(goal.MaxWeight));
        }

        if (count == 0) {
            return new(shot, 0f, 0f, 0);
        }

        var placed = RigidBodySolver.Solve(shot.Transform, resolved.AsSpan(0, count), out var moved, out var turned);

        return new(shot with { Transform = placed }, moved, turned, count);
    }
}
