// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Engine.Transforms;

/// <summary>
///     The user-facing view of an entity's transform: reads the world matrix, writes the local one.
/// </summary>
/// <remarks>
///     <para>
///         A <c>ref struct</c> holding a world and an entity and nothing else, so it costs nothing to
///         make and cannot outlive the frame it was made in — which is the point. It is a
///         <em>view</em>, not a component: every read goes to the chunk and every write goes to the
///         chunk, so two façades over the same entity can never disagree.
///     </para>
///     <para>
///         Setting a world-space property writes the local one with the parent's inverse applied, the
///         same semantics Unity has. It uses the parent's <see cref="WorldTransform" /> as it stands
///         <i>now</i>, which is what the last <c>TransformSystem</c> pass produced — so a child moved
///         in the same frame as its parent lands where the parent was at the start of the frame, and
///         is corrected by the next pass. That is Unity's behaviour too, and it is the reason
///         hierarchy-sensitive code belongs in <c>SystemPhase.LateUpdate</c>.
///     </para>
/// </remarks>
public readonly ref struct Transform {
    readonly World world;
    readonly Entity entity;

    /// <summary>Views an entity's transform.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    public Transform(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);
        this.world = world;
        this.entity = entity;
    }

    /// <summary>The entity being viewed.</summary>
    public Entity Entity => entity;

    /// <summary>Position relative to the parent.</summary>
    public Vector3 LocalPosition {
        get => world.Read<LocalTransform>(entity).Position;
        set => world.Get<LocalTransform>(entity).Position = value;
    }

    /// <summary>Rotation relative to the parent.</summary>
    public Quaternion LocalRotation {
        get => world.Read<LocalTransform>(entity).Rotation;
        set => world.Get<LocalTransform>(entity).Rotation = value;
    }

    /// <summary>Scale relative to the parent.</summary>
    public Vector3 LocalScale {
        get => world.Read<LocalTransform>(entity).Scale;
        set => world.Get<LocalTransform>(entity).Scale = value;
    }

    /// <summary>The local-to-world matrix, as of the last transform pass.</summary>
    public Matrix4x4 LocalToWorld => world.Read<WorldTransform>(entity).Value;

    /// <summary>Position in world space.</summary>
    public Vector3 Position {
        get => world.Read<WorldTransform>(entity).Value.Translation;
        set => LocalPosition = ToParentSpace(value);
    }

    /// <summary>Rotation in world space.</summary>
    public Quaternion Rotation {
        get => Matrix4x4.Decompose(LocalToWorld, out _, out var rotation, out _) ? rotation : LocalRotation;

        set {
            var parent = Hierarchy.ParentOf(world, entity);

            LocalRotation = parent.IsNull
                ? value
                : Quaternion.Conjugate(ParentRotation(parent)) * value;
        }
    }

    /// <summary>
    ///     Scale in world space, which is a lie whenever a parent's rotation and scale disagree —
    ///     hence the name, and hence there being no setter.
    /// </summary>
    public Vector3 LossyScale =>
        Matrix4x4.Decompose(LocalToWorld, out var scale, out _, out _) ? scale : LocalScale;

    /// <summary>The direction the entity faces: its local -Z, in world space.</summary>
    public Vector3 Forward => Vector3.Normalize(TransformDirection(Vector3.Forward));

    /// <summary>The entity's local +X, in world space.</summary>
    public Vector3 Right => Vector3.Normalize(TransformDirection(Vector3.UnitX));

    /// <summary>The entity's local +Y, in world space.</summary>
    public Vector3 Up => Vector3.Normalize(TransformDirection(Vector3.UnitY));

    /// <summary>The entity's parent, or <see cref="Entity.Null" />.</summary>
    public Entity Parent => Hierarchy.ParentOf(world, entity);

    /// <summary>Takes a point from this entity's space into world space.</summary>
    /// <param name="point">The point.</param>
    /// <returns>Where it is in the world.</returns>
    public Vector3 TransformPoint(Vector3 point) => Matrix4x4.TransformPosition(point, LocalToWorld);

    /// <summary>Takes a direction from this entity's space into world space, ignoring translation.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>Where it points in the world.</returns>
    public Vector3 TransformDirection(Vector3 direction) => Matrix4x4.TransformDirection(direction, LocalToWorld);

    /// <summary>Takes a point from world space into this entity's space.</summary>
    /// <param name="point">The point.</param>
    /// <returns>Where it is relative to this entity, or the point itself if the matrix is singular.</returns>
    public Vector3 InverseTransformPoint(Vector3 point) =>
        Matrix4x4.Invert(LocalToWorld, out var inverse) ? Matrix4x4.TransformPosition(point, inverse) : point;

    /// <summary>Turns the entity to face a world-space point.</summary>
    /// <param name="target">What to look at.</param>
    /// <param name="up">Which way is up. Defaults to world +Y.</param>
    public void LookAt(Vector3 target, Vector3 up = default) {
        var direction = target - Position;

        if (direction.LengthSquared() <= float.Epsilon) {
            return;
        }

        Rotation = LookRotation(Vector3.Normalize(direction), up == default ? Vector3.UnitY : up);
    }

    /// <summary>Moves the entity by a world-space offset.</summary>
    /// <param name="offset">How far.</param>
    public void Translate(Vector3 offset) => Position += offset;

    /// <summary>Renders the entity and its world position.</summary>
    /// <returns>The transform in text.</returns>
    public override string ToString() => $"{entity} at {Position}";

    /// <summary>The rotation that takes local -Z onto a direction, with a chosen up.</summary>
    /// <param name="forward">Where to face. Already normalised.</param>
    /// <param name="up">Which way is up. Need not be perpendicular.</param>
    /// <returns>The rotation.</returns>
    /// <remarks>
    ///     Built here rather than in <c>Vixen.Core.Mathematics</c> because it encodes a convention
    ///     the mathematics library deliberately does not have one of: that an entity faces its local
    ///     -Z. That is an <em>engine</em> decision (see Conventions.md § Handedness), and the
    ///     basis below is what it means — local +Z is the backward axis, so it is the negated
    ///     direction, and the other two follow right-handed.
    /// </remarks>
    public static Quaternion LookRotation(Vector3 forward, Vector3 up) {
        var backward = -forward;
        var right = Vector3.Cross(up, backward);

        if (right.LengthSquared() <= 1e-12f) {
            // Looking straight along the up axis. Any roll is as good as any other, so pick one
            // rather than producing a matrix with a zero row.
            right = Vector3.Cross(Vector3.UnitZ, backward);

            if (right.LengthSquared() <= 1e-12f) {
                right = Vector3.UnitX;
            }
        }

        right = Vector3.Normalize(right);
        var trueUp = Vector3.Cross(backward, right);

        // Row-vector convention: row i is where local axis i lands.
        var basis = new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            trueUp.X, trueUp.Y, trueUp.Z, 0f,
            backward.X, backward.Y, backward.Z, 0f,
            0f, 0f, 0f, 1f
        );

        return Matrix4x4.Decompose(basis, out _, out var rotation, out _) ? rotation : Quaternion.Identity;
    }

    Vector3 ToParentSpace(Vector3 worldPoint) {
        var parent = Hierarchy.ParentOf(world, entity);

        if (parent.IsNull) {
            return worldPoint;
        }

        return Matrix4x4.Invert(world.Read<WorldTransform>(parent).Value, out var inverse)
            ? Matrix4x4.TransformPosition(worldPoint, inverse)
            : worldPoint;
    }

    Quaternion ParentRotation(Entity parent) =>
        Matrix4x4.Decompose(world.Read<WorldTransform>(parent).Value, out _, out var rotation, out _)
            ? rotation
            : Quaternion.Identity;
}
