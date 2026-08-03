// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>One proxy shape, as something the viewport's gizmo can drag.</summary>
/// <remarks>
///     <para>
///         <b>What "sized with handles" needs.</b> The shapes already draw — <c>DrawShapes</c> puts
///         every one of them in a viewport — and what was missing was a way to take hold of one. This
///         is the same <see cref="IGizmoTarget" /> an entity and a mesh selection go through, so the
///         move, rotate and scale modes, the snapping and the world/parent space toggle all work
///         without any of them knowing what a proxy shape is.
///     </para>
///     <para>
///         ⚠ <b><see cref="Scale" /> writes the extents, because a proxy shape has no scale of its
///         own.</b> Its size <em>is</em> its extents, so a scale handle that wrote a separate field
///         would be a second size that has to be reconciled with the first — and a sphere with a
///         radius of 0.2 and a scale of 3 is a sphere whose radius nobody can read off the panel.
///     </para>
///     <para>
///         ⚠ <b>One undo entry per drag, not one per frame.</b> The gizmo recomputes from mouse-down
///         and writes absolute values every frame it moves; a target that called
///         <see cref="ProxyShapeDocument.Edit" /> from its setters would put sixty entries on the
///         stack for one gesture. The drag mutates a copy and <see cref="Commit" /> records the edit,
///         which is <c>MeshGizmoTarget</c>'s arrangement for the same reason.
///     </para>
/// </remarks>
public sealed class ProxyShapeGizmoTarget : IGizmoTarget {
    readonly ProxyShapeDocument document;
    readonly ProxyShapeRecord started;
    readonly BoneTransform jointToWorld;

    /// <summary>Takes hold of a shape.</summary>
    /// <param name="document">The set it belongs to.</param>
    /// <param name="shape">The shape.</param>
    /// <param name="joint">Where its joint is, in world space.</param>
    public ProxyShapeGizmoTarget(ProxyShapeDocument document, ProxyShapeRecord shape, in BoneTransform joint) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(shape);

        this.document = document;

        started = shape;
        jointToWorld = joint;
        Current = shape;
    }

    /// <summary>What the shape is at this moment of the drag, before it has been recorded.</summary>
    public ProxyShapeRecord Current { get; private set; }

    /// <summary>Where the joint the shape hangs off is, in world space.</summary>
    /// <remarks>
    ///     ⚠ <b>Supplied rather than looked up, and it is a <em>posed</em> joint.</b> A shape is
    ///     placed against the rig as it stands — usually the bind pose in an editor, but a scrubbed
    ///     clip if somebody is checking a contact mid-motion — and a target that recomputed the bind
    ///     pose itself would put the handle somewhere the shape is not.
    /// </remarks>
    public BoneTransform Joint => jointToWorld;

    /// <inheritdoc />
    public Vector3 Position {
        get => jointToWorld.Translation + Quaternion.Transform(Current.Position * jointToWorld.Scale, jointToWorld.Rotation);
        set {
            var local = Quaternion.Transform(value - jointToWorld.Translation, Quaternion.Conjugate(jointToWorld.Rotation));

            Current = Current with { Position = Divide(local, jointToWorld.Scale) };
        }
    }

    /// <inheritdoc />
    public Quaternion Rotation {
        get => Quaternion.Concatenate(Current.Rotation, jointToWorld.Rotation);
        set => Current = Current with {
            Rotation = Quaternion.Normalize(Quaternion.Concatenate(value, Quaternion.Conjugate(jointToWorld.Rotation)))
        };
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Relative to the size the drag started at, which is what makes a scale handle behave.</b>
    ///     The gizmo hands back a factor against mouse-down; reading the extents back as the factor
    ///     would make the second frame of a drag scale the already-scaled shape, and a shape would run
    ///     away under the pointer.
    /// </remarks>
    public Vector3 Scale {
        get => Divide(Current.Extents, started.Extents);
        set {
            var factor = new Vector3(MathF.Abs(value.X), MathF.Abs(value.Y), MathF.Abs(value.Z));

            Current = Current with {
                Extents = started.Extents * factor,
                TopExtents = started.TopExtents * factor
            };
        }
    }

    /// <inheritdoc />
    public Matrix4x4 ParentToWorld =>
        Matrix4x4.Compose(jointToWorld.Scale, jointToWorld.Rotation, jointToWorld.Translation);

    /// <summary>Whether the drag changed anything.</summary>
    public bool IsDirty => Current != started;

    /// <summary>Records the whole drag as one undoable edit.</summary>
    /// <returns>The shape as it now is, so a caller can keep selecting it.</returns>
    public ProxyShapeRecord Commit() =>
        IsDirty ? document.Edit(started, Describe(), _ => Current) : started;

    /// <summary>Which of the three the drag mostly did, for the undo entry's name.</summary>
    /// <remarks>
    ///     A gizmo drag is one of move, rotate or scale, and the stack reads much better for
    ///     "Resize Proxy Shape" than for "Edit Proxy Shape" three times in a row.
    /// </remarks>
    string Describe() =>
        Current.Extents != started.Extents ? "Resize Proxy Shape"
        : Current.Rotation != started.Rotation ? "Turn Proxy Shape"
        : "Move Proxy Shape";

    /// <summary>Where a shape's joint is, given a rig and a pose.</summary>
    /// <param name="skeleton">The rig.</param>
    /// <param name="model">The model-space pose, or empty for the bind pose.</param>
    /// <param name="shape">The shape.</param>
    /// <param name="world">Where the character is.</param>
    /// <returns>The joint, in world space.</returns>
    public static BoneTransform JointOf(
        Skeleton skeleton,
        ReadOnlySpan<BoneTransform> model,
        ProxyShapeRecord shape,
        in BoneTransform world
    ) {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(shape);

        var joint = skeleton.IndexOf(shape.Joint);

        if (joint < 0) {
            // A shape on a joint this rig does not have never poses, so its handle goes where the
            // character is rather than nowhere — a gizmo at the origin is one nobody can find.
            return world;
        }

        if (model.IsEmpty) {
            var bind = new BoneTransform[skeleton.JointCount];

            SkeletonPose.ComputeModelSpace(skeleton, skeleton.BindPose, bind);
            return BoneTransform.Concatenate(bind[joint], world);
        }

        return BoneTransform.Concatenate(model[joint], world);
    }

    static Vector3 Divide(Vector3 value, Vector3 by) =>
        new(
            by.X == 0f ? 0f : value.X / by.X,
            by.Y == 0f ? 0f : value.Y / by.Y,
            by.Z == 0f ? 0f : value.Z / by.Z
        );
}
