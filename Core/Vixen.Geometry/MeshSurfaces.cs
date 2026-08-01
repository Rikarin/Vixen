// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry;

/// <summary>How a face's texture coordinates are worked out from where it is.</summary>
/// <remarks>
///     ⚠ <b><see cref="World" /> is the default and doc 24's P5 says why in one sentence: a block-out
///     box scaled 8×3 must not stretch its texels.</b> Every unwrapper answers "where on this surface
///     am I" and every one of them needs the surface to have been laid out; a block-out has not been
///     laid out and never will be, because it exists to be replaced. What makes proportion readable at
///     a glance is a checker whose squares are a fixed number of <i>metres</i> everywhere in the level,
///     which is the projection that ignores the object entirely.
/// </remarks>
public enum UvProjection : byte {
    /// <summary>From the world position, on whichever axis pair each face most faces.</summary>
    /// <remarks>The default, and the only one whose squares are the same size on two objects of
    ///     different scales.</remarks>
    World,

    /// <summary>The same, in the mesh's own space, so the mapping travels with the object.</summary>
    /// <remarks>What a prop wants: a crate carried across the level keeps its texels where they were,
    ///     which world space does not do.</remarks>
    Box,

    /// <summary>From one axis for every face, whichever way each one points.</summary>
    /// <remarks>A floor, a road, a decal — anything whose mapping should not break at the corner
    ///     where the dominant axis changes.</remarks>
    Planar
}

/// <summary>Doc 24's Surfaces table: what a face is mapped with and how its normals are computed.</summary>
/// <remarks>
///     <para>
///         <b>The arithmetic half of P5, and it is deliberately all of the half that needs no
///         renderer.</b> A projection is a function of a position and a normal; a smoothing group is a
///         rule for averaging; the per-face transform is two-by-two matrix arithmetic. What is not here
///         is the material a face is assigned and the checker it is drawn with, both of which are
///         statements about a viewport.
///     </para>
///     <para>
///         ⚠ <b>Coordinates are per <i>corner</i>, not per position.</b> A cube's corner is three
///         corners in three faces with three different UVs, which is exactly why the drawing structure
///         and the position graph are different graphs — see <see cref="EditMesh" />'s own remarks. A
///         projection that wrote per position could not map two faces of one box differently, which is
///         the whole point of a box projection.
///     </para>
/// </remarks>
public static class MeshSurfaces {
    /// <summary>How many world units one repeat of a texture covers, unless a caller says otherwise.</summary>
    /// <remarks>
    ///     One metre, which is the number a block-out is measured in and the number the grid defaults
    ///     to. A checker at this scale makes "how wide is that corridor" a thing you count rather than
    ///     a thing you measure.
    /// </remarks>
    public const float DefaultScale = 1f;

    /// <summary>The angle below which <see cref="AutoSmooth" /> treats two faces as one surface.</summary>
    /// <remarks>
    ///     Thirty degrees. Above it a cylinder of twelve sides — thirty degrees between neighbours —
    ///     is the first shape that stops being smooth, and below it a bevel of two segments starts
    ///     being smoothed into the faces it was cut from. It is the same number Blender and 3ds Max
    ///     default to, for the same reason.
    /// </remarks>
    public const float DefaultSmoothingAngle = MathF.PI / 6f;

    /// <summary>Maps faces by projecting them.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="faces">Which faces, or <see langword="null" /> for all of them.</param>
    /// <param name="projection">Which projection.</param>
    /// <param name="scale">How many world units one repeat covers.</param>
    /// <param name="toWorld">
    ///     Where the mesh is in the world, for <see cref="UvProjection.World" />. Identity leaves world
    ///     space and the mesh's own space the same thing, which is what a test wants and what an entity
    ///     at the origin is.
    /// </param>
    /// <param name="axis">Which way <see cref="UvProjection.Planar" /> looks. Ignored by the others.</param>
    /// <returns>How many faces were mapped.</returns>
    /// <remarks>
    ///     ⚠ <b>A face that is not named keeps the coordinates it had.</b> Mapping a wall and finding
    ///     the floor remapped is the behaviour that makes a per-face tool useless; the layer is created
    ///     at full size the first time anything writes to it, so the faces nobody touched start at zero
    ///     rather than at whatever was in memory.
    /// </remarks>
    public static int Project(
        EditMesh mesh,
        IReadOnlyCollection<int>? faces,
        UvProjection projection = UvProjection.World,
        float scale = DefaultScale,
        Matrix4x4? toWorld = null,
        Vector3 axis = default
    ) {
        ArgumentNullException.ThrowIfNull(mesh);

        var coordinates = Layer(mesh);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(coordinates);

        var step = MathF.Abs(scale) > 1e-6f ? 1f / scale : 1f;
        var matrix = toWorld ?? Matrix4x4.Identity;
        var world = projection == UvProjection.World;

        var planar = axis.IsZero ? Vector3.UnitY : Vector3.Normalize(axis);
        var mapped = 0;

        foreach (var face in Chosen(mesh, faces)) {
            var entry = mesh.Faces[face];

            var normal = projection == UvProjection.Planar
                ? planar
                : world
                    ? Matrix4x4.TransformDirection(mesh.Normal(face), matrix)
                    : mesh.Normal(face);

            var dominant = Dominant(normal);

            for (var corner = 0; corner < entry.Count; corner++) {
                var position = mesh.Positions[mesh.Corners[entry.Start + corner]];

                if (world) {
                    position = Matrix4x4.TransformPosition(position, matrix);
                }

                span[entry.Start + corner] = Flatten(position, dominant) * step;
            }

            mapped++;
        }

        mesh.SetTexCoords(span);
        return mapped;
    }

    /// <summary>Moves, turns and scales the coordinates of faces, about their own centre.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="faces">Which faces, or <see langword="null" /> for all of them.</param>
    /// <param name="offset">How far to slide them, in repeats.</param>
    /// <param name="rotation">How far to turn them, in radians.</param>
    /// <param name="scale">How much to scale them by. Zero on an axis is read as one.</param>
    /// <returns>How many faces were changed.</returns>
    /// <remarks>
    ///     ⚠ <b>About each face's own centre rather than about the origin of the mapping.</b> Rotating
    ///     a face's texture forty-five degrees about a point somewhere off in UV space moves it out of
    ///     the frame as well as turning it, which is not what anybody dragging a rotate field means —
    ///     and is exactly what a naive matrix multiply does.
    /// </remarks>
    public static int Transform(
        EditMesh mesh,
        IReadOnlyCollection<int>? faces,
        Vector2 offset = default,
        float rotation = 0f,
        Vector2 scale = default
    ) {
        ArgumentNullException.ThrowIfNull(mesh);

        var coordinates = Layer(mesh);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(coordinates);

        var sizeX = MathF.Abs(scale.X) > 1e-6f ? scale.X : 1f;
        var sizeY = MathF.Abs(scale.Y) > 1e-6f ? scale.Y : 1f;

        var cos = MathF.Cos(rotation);
        var sin = MathF.Sin(rotation);

        var changed = 0;

        foreach (var face in Chosen(mesh, faces)) {
            var entry = mesh.Faces[face];
            var centre = Vector2.Zero;

            for (var corner = 0; corner < entry.Count; corner++) {
                centre += span[entry.Start + corner];
            }

            centre /= entry.Count;

            for (var corner = 0; corner < entry.Count; corner++) {
                var local = span[entry.Start + corner] - centre;
                var sized = new Vector2(local.X * sizeX, local.Y * sizeY);

                span[entry.Start + corner] = centre
                    + new Vector2((sized.X * cos) - (sized.Y * sin), (sized.X * sin) + (sized.Y * cos))
                    + offset;
            }

            changed++;
        }

        mesh.SetTexCoords(span);
        return changed;
    }

    /// <summary>Stretches each face's coordinates so that it exactly covers one repeat.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="faces">Which faces, or <see langword="null" /> for all of them.</param>
    /// <returns>How many faces were fitted.</returns>
    /// <remarks>
    ///     ⚠ <b>Per face, and so it does break the mapping across a seam.</b> That is what it is for:
    ///     "put the whole of this sign on this wall" is one face's worth of intent, and the faces
    ///     around it should not move. A face with no extent on an axis is left alone on that axis
    ///     rather than divided by zero.
    /// </remarks>
    public static int Fit(EditMesh mesh, IReadOnlyCollection<int>? faces) {
        ArgumentNullException.ThrowIfNull(mesh);

        var coordinates = Layer(mesh);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(coordinates);
        var fitted = 0;

        foreach (var face in Chosen(mesh, faces)) {
            var entry = mesh.Faces[face];

            var low = span[entry.Start];
            var high = low;

            for (var corner = 1; corner < entry.Count; corner++) {
                low = Vector2.Min(low, span[entry.Start + corner]);
                high = Vector2.Max(high, span[entry.Start + corner]);
            }

            var extent = high - low;
            var sizeX = extent.X > 1e-6f ? 1f / extent.X : 1f;
            var sizeY = extent.Y > 1e-6f ? 1f / extent.Y : 1f;

            for (var corner = 0; corner < entry.Count; corner++) {
                var local = span[entry.Start + corner] - low;

                span[entry.Start + corner] = new(local.X * sizeX, local.Y * sizeY);
            }

            fitted++;
        }

        mesh.SetTexCoords(span);
        return fitted;
    }

    /// <summary>Puts faces in a smoothing group, or takes them out of one.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="faces">Which faces, or <see langword="null" /> for all of them.</param>
    /// <param name="group">Which smoothing group, or zero to make every edge round them hard.</param>
    /// <returns>How many faces were changed.</returns>
    /// <remarks>Doc 24's own words: a hard edge is the <i>absence</i> of a smoothing group, which is
    ///     why zero is a value rather than a sentinel nobody may use.</remarks>
    public static int Smooth(EditMesh mesh, IReadOnlyCollection<int>? faces, int group = 1) {
        ArgumentNullException.ThrowIfNull(mesh);

        var changed = 0;

        foreach (var face in Chosen(mesh, faces)) {
            if (mesh.Faces[face].Smoothing == group) {
                continue;
            }

            mesh.SetSmoothing(face, group);
            changed++;
        }

        return changed;
    }

    /// <summary>Groups faces by how sharply they meet, so that curves are smooth and corners are not.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="angle">How far two faces may turn and still be one surface, in radians.</param>
    /// <returns>How many smoothing groups it made.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>What everybody actually reaches for, and what a generator's output wants the moment
    ///         it has a curve in it.</b> A cylinder made editable comes out faceted because
    ///         <c>EditMeshes.ToMeshData</c> has nothing but the face normals to go on; one call to this
    ///         gives its wall a smoothing group, leaves the rims where its caps meet it hard, and the
    ///         silhouette stops being a polygon.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A union-find over faces across shared edges, not a per-edge flag.</b> Smoothing has
    ///         to be transitive round a cylinder — every neighbour is within the angle, so the whole
    ///         wall is one surface — and a flag per edge would make a corner between two smooth faces
    ///         depend on which of them the normal was computed from first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Groups are numbered from one, because zero means hard.</b> A face with no
    ///         neighbours inside the angle keeps zero rather than getting a group of its own — the two
    ///         are the same picture, and the second would fill a saved mesh with a group per face.
    ///     </para>
    /// </remarks>
    public static int AutoSmooth(EditMesh mesh, float angle = DefaultSmoothingAngle) {
        ArgumentNullException.ThrowIfNull(mesh);

        var parent = new int[mesh.FaceCount];
        var joined = new bool[mesh.FaceCount];

        for (var face = 0; face < parent.Length; face++) {
            parent[face] = face;
        }

        var limit = MathF.Cos(Math.Clamp(angle, 0f, MathF.PI));

        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            var sharing = mesh.FacesOf(edge);

            // ⚠ Exactly two, which is not pedantry: an edge with three faces on it is a
            // non-manifold join, and averaging normals across one produces a shading seam that
            // moves depending on which pair was compared. `EditMesh.Validate` is what reports them.
            if (sharing.Length != 2) {
                continue;
            }

            if (Vector3.Dot(mesh.Normal(sharing[0]), mesh.Normal(sharing[1])) < limit) {
                continue;
            }

            var left = Find(parent, sharing[0]);
            var right = Find(parent, sharing[1]);

            if (left == right) {
                continue;
            }

            parent[left] = right;

            joined[sharing[0]] = true;
            joined[sharing[1]] = true;
        }

        Dictionary<int, int> numbers = [];

        for (var face = 0; face < parent.Length; face++) {
            if (!joined[face]) {
                mesh.SetSmoothing(face, 0);
                continue;
            }

            var root = Find(parent, face);

            if (!numbers.TryGetValue(root, out var number)) {
                number = numbers.Count + 1;
                numbers[root] = number;
            }

            mesh.SetSmoothing(face, number);
        }

        return numbers.Count;
    }

    /// <summary>One normal per corner, honouring the smoothing groups.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>A normal per corner, in face order.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>What turns a smoothing group into a picture.</b> A corner in a face with no smoothing
    ///         group takes the face's own normal, which is flat shading; a corner in one takes the
    ///         area-weighted average of every face at that <i>position</i> that is in the same group,
    ///         which is smooth shading that stops at the group boundary.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Weighted by area rather than counted.</b> A cylinder cap's fan puts a great many
    ///         tiny triangles at the pole and one big quad beside it; an unweighted average there is a
    ///         normal dominated by the tessellation rather than by the shape, which is the classic
    ///         pinched highlight at the top of a sphere.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Faces at a position, not across an edge.</b> Two shells that happen to touch at a
    ///         corner are not one surface, and neither are two faces of the same group that meet only
    ///         there — but a position is what the mesh actually shares, and asking the question any
    ///         other way needs a walk per corner rather than a lookup.
    ///     </para>
    /// </remarks>
    public static Vector3[] Normals(EditMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var normals = new Vector3[mesh.CornerCount];
        var facing = new Vector3[mesh.FaceCount];

        for (var face = 0; face < mesh.FaceCount; face++) {
            facing[face] = mesh.Normal(face) * mesh.Area(face);
        }

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];
            var flat = mesh.Normal(face);

            for (var corner = 0; corner < entry.Count; corner++) {
                var index = entry.Start + corner;

                if (entry.Smoothing == 0) {
                    normals[index] = flat;
                    continue;
                }

                var total = Vector3.Zero;

                foreach (var neighbour in mesh.FacesAt(mesh.Corners[index])) {
                    if (mesh.Faces[neighbour].Smoothing == entry.Smoothing) {
                        total += facing[neighbour];
                    }
                }

                normals[index] = total.LengthSquared() > 0f ? Vector3.Normalize(total) : flat;
            }
        }

        return normals;
    }

    static int Find(int[] parent, int face) {
        while (parent[face] != face) {
            parent[face] = parent[parent[face]];
            face = parent[face];
        }

        return face;
    }

    static IEnumerable<int> Chosen(EditMesh mesh, IReadOnlyCollection<int>? faces) {
        if (faces is null) {
            return Enumerable.Range(0, mesh.FaceCount);
        }

        return faces.Where(face => (uint) face < (uint) mesh.FaceCount).Distinct();
    }

    /// <summary>The mesh's coordinate layer, made at full size if it has none.</summary>
    static List<Vector2> Layer(EditMesh mesh) {
        var coordinates = new List<Vector2>(mesh.CornerCount);
        var existing = mesh.TexCoords;

        for (var corner = 0; corner < mesh.CornerCount; corner++) {
            coordinates.Add(corner < existing.Length ? existing[corner] : Vector2.Zero);
        }

        return coordinates;
    }

    /// <summary>Which axis a normal most points along, signed.</summary>
    /// <returns>1, 2 or 3 for X, Y or Z, negated for the other direction.</returns>
    static int Dominant(Vector3 normal) {
        var x = MathF.Abs(normal.X);
        var y = MathF.Abs(normal.Y);
        var z = MathF.Abs(normal.Z);

        if (y >= x && y >= z) {
            return normal.Y < 0f ? -2 : 2;
        }

        return x >= z ? normal.X < 0f ? -1 : 1 : normal.Z < 0f ? -3 : 3;
    }

    /// <summary>A position dropped onto the axis pair a dominant axis names.</summary>
    /// <remarks>
    ///     ⚠ <b>One of the two axes is negated on three of the six faces, and that is what keeps the
    ///     texture the right way round.</b> Dropping a coordinate is a projection through the surface
    ///     as well as onto it, so the two sides of a box come out mirrored unless one of them is
    ///     flipped — which reads as text on a wall being backwards on the far side of it.
    /// </remarks>
    static Vector2 Flatten(Vector3 position, int dominant) =>
        dominant switch {
            1 => new(-position.Z, position.Y),
            -1 => new(position.Z, position.Y),
            2 => new(position.X, -position.Z),
            -2 => new(position.X, position.Z),
            3 => new(position.X, position.Y),
            _ => new(-position.X, position.Y)
        };
}
