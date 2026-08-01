// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry;

/// <summary>Collision shapes worked out from a block-out mesh.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's P7: "box per convex piece, or the mesh itself".</b> A block-out is what a level
///         is walked around in long before it is what it looks like, and a designer who has to author
///         collision by hand for geometry they will throw away next week does not author it at all.
///     </para>
///     <para>
///         ⚠ <b>A box per connected <i>shell</i> rather than per convex piece, and the difference is
///         stated rather than glossed.</b> Convex decomposition is a research problem with a
///         well-known set of approximate answers, none of them cheap; a shell is what
///         <see cref="EditMesh" /> can already tell you exactly. For a block-out the two mostly
///         coincide, because a block-out is made of boxes joined into rooms — and where they do not,
///         the box is bigger than the geometry, which is the safe direction to be wrong in for
///         something you walk on and the wrong direction for something you shoot through. Which of the
///         two matters is what <see cref="Triangles" /> is for.
///     </para>
///     <para>
///         ⚠ <b>Nothing here builds a <c>ShapeDescription</c>, and that is doc 24's D1 rather than an
///         omission.</b> A hull, a mesh or a compound is registered <i>by its data</i> with a live
///         physics world and the description holds the resulting index — see
///         <c>ShapeDescription</c>'s own remarks — so what a geometry kernel can produce is the data
///         and what turns it into a shape is the host that has a world.
///     </para>
/// </remarks>
public static class MeshCollision {
    /// <summary>Which shell each face belongs to, and how many there are.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="into">A shell number per face. Cleared first.</param>
    /// <returns>How many shells there are.</returns>
    /// <remarks>
    ///     A union-find over faces joined by shared edges, which is the same walk
    ///     <see cref="MeshTopology.Shell" /> does from one face — this one does all of them at once,
    ///     because a collision bake asks about every piece rather than about the one under the pointer.
    /// </remarks>
    public static int Shells(EditMesh mesh, List<int> into) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        var parent = new int[mesh.FaceCount];

        for (var face = 0; face < parent.Length; face++) {
            parent[face] = face;
        }

        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            var sharing = mesh.FacesOf(edge);

            for (var index = 1; index < sharing.Length; index++) {
                var left = Find(parent, sharing[0]);
                var right = Find(parent, sharing[index]);

                if (left != right) {
                    parent[left] = right;
                }
            }
        }

        Dictionary<int, int> numbers = [];

        for (var face = 0; face < parent.Length; face++) {
            var root = Find(parent, face);

            if (!numbers.TryGetValue(root, out var number)) {
                number = numbers.Count;
                numbers[root] = number;
            }

            into.Add(number);
        }

        return numbers.Count;
    }

    /// <summary>A box round each of the mesh's shells.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="into">The boxes, one per shell. Cleared first.</param>
    /// <returns>How many boxes there are.</returns>
    /// <remarks>
    ///     ⚠ <b>Axis-aligned in the mesh's own space, which is not the world's.</b> A wall rotated
    ///     forty-five degrees on its entity has an axis-aligned box in its own space and an oriented
    ///     one in the level, which is right — and is why the boxes come back in the mesh's space and
    ///     the entity's transform is left to the caller.
    /// </remarks>
    public static int Boxes(EditMesh mesh, List<BoundingBox> into) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        List<int> shells = [];

        var count = Shells(mesh, shells);

        if (count == 0) {
            return 0;
        }

        var low = new Vector3[count];
        var high = new Vector3[count];
        var seen = new bool[count];

        for (var face = 0; face < mesh.FaceCount; face++) {
            var shell = shells[face];
            var entry = mesh.Faces[face];

            for (var corner = 0; corner < entry.Count; corner++) {
                var point = mesh.Positions[mesh.Corners[entry.Start + corner]];

                if (!seen[shell]) {
                    low[shell] = point;
                    high[shell] = point;
                    seen[shell] = true;

                    continue;
                }

                low[shell] = Vector3.Min(low[shell], point);
                high[shell] = Vector3.Max(high[shell], point);
            }
        }

        for (var shell = 0; shell < count; shell++) {
            if (seen[shell]) {
                into.Add(new(low[shell], high[shell]));
            }
        }

        return into.Count;
    }

    /// <summary>The mesh as a triangle soup, for the collision that has to be exact.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>The positions and the triangle indices into them.</returns>
    /// <remarks>
    ///     ⚠ <b>Shared positions rather than one vertex per corner, unlike the trip to a renderer.</b>
    ///     A collider has no normals and no coordinates to split on, so the welded graph is exactly
    ///     what it wants — and it is a third the vertices, which matters for a mesh collider more than
    ///     for anything else in the engine.
    /// </remarks>
    public static (Vector3[] Positions, int[] Indices) Triangles(EditMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        return ([.. mesh.Positions], mesh.Triangulate());
    }

    static int Find(int[] parent, int face) {
        while (parent[face] != face) {
            parent[face] = parent[parent[face]];
            face = parent[face];
        }

        return face;
    }
}
