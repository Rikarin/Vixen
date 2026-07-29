// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>Turning a <see cref="MeshData" /> into the vertices a <see cref="GeometryBuffer" /> holds.</summary>
/// <remarks>
///     <para>
///         The one place that goes from the shape a compiler produces — parallel arrays, some of them
///         empty — to the interleaved <see cref="SurfaceVertex" /> the shading stages read. It is a pure
///         function of the mesh, which is what lets every question about it be a test with no device in
///         it.
///     </para>
///     <para>
///         ⚠ <b>A missing attribute is filled in rather than refused, and each one is filled in
///         differently because each absence means something different.</b> An empty array says the file
///         did not have it — see <see cref="MeshData" /> on why that is not the same as an array of
///         zeros — and a mesh with no tangents is the ordinary case rather than a broken one:
///         <see cref="MeshPrimitives" /> produces none at all.
///     </para>
/// </remarks>
public static class SurfaceGeometry {
    /// <summary>How many vertices packing a mesh produces.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>Its vertex count, which is its position count.</returns>
    /// <remarks>
    ///     Positions and nothing else, because a mesh whose other arrays disagree in length is packed to
    ///     its positions with the rest filled in — see <see cref="Pack" />. Anything else would make the
    ///     size depend on which attribute happened to be longest.
    /// </remarks>
    public static int VertexCount(MeshData mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        return mesh.Positions.Length;
    }

    /// <summary>Writes a mesh's vertices into a span, interleaved.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="into">Where to write them. At least <see cref="VertexCount" /> long.</param>
    /// <exception cref="ArgumentException"><paramref name="into" /> is too short.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every attribute is read past its own length as a fill rather than as an error.</b> A
    ///         mesh with normals for half its vertices is a broken import and a mesh with none is a
    ///         primitive; the two arrive here identically, and there is no length at which refusing helps
    ///         somebody more than drawing does. What each fill is:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b>Normal → +Y.</b> Not zero, because a zero normal makes every lighting term zero and
    ///             the surface renders black — which reads as a broken renderer rather than as missing
    ///             data. Up is wrong and visible.
    ///         </item>
    ///         <item>
    ///             <b>Tangent → any vector perpendicular to the normal, handedness +1.</b> The common
    ///             case, since a primitive has none. A zero tangent would make a normal-mapped material
    ///             sample a degenerate basis, and normal mapping is exactly what a tangent is for.
    ///         </item>
    ///         <item>
    ///             <b>Texture coordinate → zero.</b> Every vertex sampling one texel is a flat colour,
    ///             which is both harmless and obviously untextured.
    ///         </item>
    ///     </list>
    /// </remarks>
    public static void Pack(MeshData mesh, Span<SurfaceVertex> into) {
        ArgumentNullException.ThrowIfNull(mesh);

        var count = mesh.Positions.Length;

        if (into.Length < count) {
            throw new ArgumentException(
                $"'{mesh.Name}' has {count} vertices and the span is {into.Length}.",
                nameof(into)
            );
        }

        for (var index = 0; index < count; index++) {
            var normal = index < mesh.Normals.Length ? mesh.Normals[index] : Vector3.UnitY;

            if (normal.LengthSquared() < 1e-12f) {
                normal = Vector3.UnitY;
            } else {
                normal = Vector3.Normalize(normal);
            }

            into[index] = new() {
                Position = mesh.Positions[index],
                Normal = normal,
                Tangent = index < mesh.Tangents.Length ? mesh.Tangents[index] : Perpendicular(normal),
                TexCoord = index < mesh.TexCoords.Length ? mesh.TexCoords[index] : Vector2.Zero
            };
        }
    }

    /// <summary>A mesh's vertices, allocated.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>The vertices.</returns>
    /// <remarks>What a test and a one-off want. A residency cache packs into a rented buffer instead.</remarks>
    public static SurfaceVertex[] Packed(MeshData mesh) {
        var vertices = new SurfaceVertex[VertexCount(mesh)];
        Pack(mesh, vertices);

        return vertices;
    }

    /// <summary>Some unit vector at right angles to a unit vector, with a handedness of +1.</summary>
    /// <param name="normal">The normal, already normalised.</param>
    /// <returns>The tangent.</returns>
    /// <remarks>
    ///     ⚠ <b>Crossed against whichever axis the normal is least aligned with.</b> Crossing against a
    ///     fixed axis gives the zero vector for the surface that happens to face along it — the top of
    ///     every box, if the axis is up — and that is the one degenerate case a fill exists to avoid.
    /// </remarks>
    public static Vector4 Perpendicular(Vector3 normal) {
        var axis = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var tangent = Vector3.Normalize(Vector3.Cross(axis, normal));

        return new(tangent.X, tangent.Y, tangent.Z, 1f);
    }

    /// <summary>A mesh's bounding sphere, in its own space.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>The sphere the culling loop tests.</returns>
    /// <remarks>
    ///     ⚠ <b>From <see cref="MeshData.Bounds" /> when it has one, and from the positions when it does
    ///     not.</b> The box is what the importer wrote and is the cheap answer; a mesh whose box is
    ///     empty — one built at run time, one from an importer that did not fill it in — would otherwise
    ///     cull at the origin with zero radius, which is an object that disappears rather than one that
    ///     is drawn slightly too often.
    /// </remarks>
    public static BoundingSphere BoundsOf(MeshData mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var box = mesh.Bounds;

        if (box.Minimum != box.Maximum) {
            return BoundingSphere.FromBox(box);
        }

        return mesh.Positions.Length == 0 ? default : BoundingSphere.FromPoints(mesh.Positions);
    }
}
