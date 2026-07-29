// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.DistanceFields;

/// <summary>One baked field, placed in a world.</summary>
/// <remarks>
///     <para>
///         <b>Position, rotation and one scale — not a matrix, and that is the point.</b> A distance
///         field survives being moved and turned, and it survives being scaled by one number: rotate
///         a distance and it is the same distance, scale everything by <i>s</i> and every distance
///         scales by <i>s</i> too. It does <b>not</b> survive a non-uniform scale. Squash a sphere's
///         field along one axis and what comes out is not the ellipsoid's distance function — it
///         over-reports along the squashed axis and under-reports across it, and a tracer reading it
///         steps straight through the surface. Taking a <see cref="Matrix4x4" /> here would accept
///         that silently, so the type refuses to be able to express it.
///     </para>
///     <para>
///         A mesh that genuinely needs a non-uniform scale needs its own bake at that scale. That is
///         what every engine that ships this does, and it is why a field is a property of a mesh
///         asset rather than of a renderer.
///     </para>
/// </remarks>
/// <param name="Field">The baked field, in its own space.</param>
/// <param name="Position">Where its origin sits in the world.</param>
/// <param name="Rotation">How it is turned. Must be normalised.</param>
/// <param name="Scale">How much bigger it is than it was baked. Must be positive.</param>
public readonly record struct DistanceFieldInstance(
    MeshDistanceField Field,
    Vector3 Position,
    Quaternion Rotation,
    float Scale
) {
    /// <summary>The world-space box the field covers, which is what a query rejects against.</summary>
    /// <remarks>
    ///     The rotated box's own axis-aligned bound, so it is larger than the field for anything not
    ///     turned by a right angle. That slack costs a query that gets inside it and finds nothing
    ///     useful; the alternative is testing an oriented box per instance per sample, which costs
    ///     more than it saves at these counts.
    /// </remarks>
    public BoundingBox WorldBounds {
        get {
            Span<Vector3> corners = stackalloc Vector3[8];
            Field.Bounds.GetCorners(corners);

            var bounds = BoundingBox.Empty;

            foreach (var corner in corners) {
                bounds = BoundingBox.Merge(bounds, ToWorld(corner));
            }

            return bounds;
        }
    }

    /// <summary>Places a field without turning or resizing it.</summary>
    /// <param name="field">The baked field.</param>
    /// <param name="position">Where its origin sits.</param>
    /// <returns>The instance.</returns>
    public static DistanceFieldInstance At(MeshDistanceField field, Vector3 position) =>
        new(field, position, Quaternion.Identity, 1f);

    /// <summary>The signed distance at a world-space point.</summary>
    /// <param name="world">The point.</param>
    /// <returns>The distance, negative inside, in world units.</returns>
    /// <remarks>
    ///     The scale divides going in and multiplies coming out. Forgetting the second half is the
    ///     classic error and it is invisible at a scale of one, which is the scale everything is
    ///     tested at unless a test says otherwise.
    /// </remarks>
    public float Sample(Vector3 world) => Field.Sample(ToLocal(world)) * Scale;

    /// <summary>Throws if the instance cannot be sampled.</summary>
    /// <exception cref="InvalidOperationException">It cannot.</exception>
    public void Validate() {
        if (Field is null) {
            throw new InvalidOperationException("An instance of no field samples nothing.");
        }

        if (Scale <= 0) {
            throw new InvalidOperationException($"A scale of {Scale} is not a scale.");
        }

        Field.Validate();
    }

    /// <summary>Takes a point from the world into the field's own space.</summary>
    /// <param name="world">The point.</param>
    /// <returns>The same point, in field space.</returns>
    internal Vector3 ToLocal(Vector3 world) =>
        Quaternion.Transform(world - Position, Quaternion.Conjugate(Rotation)) / Scale;

    /// <summary>Takes a point from the field's own space into the world.</summary>
    /// <param name="local">The point.</param>
    /// <returns>The same point, in world space.</returns>
    internal Vector3 ToWorld(Vector3 local) =>
        Quaternion.Transform(local * Scale, Rotation) + Position;
}
