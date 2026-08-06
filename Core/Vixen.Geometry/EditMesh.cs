// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry;

/// <summary>One face: a run of corners, and the group it belongs to.</summary>
/// <param name="Start">Where its corners begin in <see cref="EditMesh.Corners" />.</param>
/// <param name="Count">How many it has. Three is a triangle and four is a quad; more is an n-gon.</param>
/// <param name="Group">Which group of faces it is part of. See <see cref="EditMesh" />.</param>
/// <param name="Smoothing">
///     Which smoothing group it is in, or zero for a face whose edges are all hard.
/// </param>
/// <remarks>
///     ⚠ <b>Two per-face numbers rather than one, and they answer different questions.</b>
///     <paramref name="Group" /> is what a tool selects and what a material is assigned to — doc 24's
///     D2, and Unreal's PolyGroups. <paramref name="Smoothing" /> is whether this face's normals are
///     averaged with its neighbours', which is doc 24's P5 and is a statement about <i>shading</i>: a
///     cylinder's wall wants one smoothing group and one material, and its cap wants the same material
///     and a different smoothing group, or the rim goes soft and the silhouette turns to mush.
/// </remarks>
public readonly record struct MeshFace(int Start, int Count, int Group, int Smoothing = 0);

/// <summary>An edge, as the two shared positions it runs between.</summary>
/// <param name="A">The lower of the two position indices.</param>
/// <param name="B">The higher.</param>
/// <remarks>
///     ⚠ <b>Ordered, so that the edge one face walks forwards and its neighbour walks backwards is one
///     edge.</b> Unordered, a cube would have twenty-four edges where it has twelve, and every one of
///     them would be drawn twice and selectable twice.
/// </remarks>
public readonly record struct MeshEdge(int A, int B) {
    /// <summary>The end that is not the one named.</summary>
    /// <param name="position">One of the two ends.</param>
    /// <returns>The other one.</returns>
    /// <remarks>Walking a loop is "step to the far end and look for the next edge", said once here
    ///     rather than as a ternary in every query that walks one.</remarks>
    public int Other(int position) => position == A ? B : A;

    /// <summary>Whether an edge runs to a position.</summary>
    /// <param name="position">The position.</param>
    /// <returns>Whether either end is it.</returns>
    public bool Touches(int position) => position == A || position == B;
}

/// <summary>An editable mesh: faces over shared positions, with the two graphs kept apart.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's D2, and the shape of it is an argument rather than a preference.</b> This is an
///         indexed face set — faces as n-gon loops of <i>corners</i>, corners over <i>shared
///         positions</i> — with an explicit edge table beside it. It is deliberately <b>not</b> a
///         half-edge structure: a half-edge cannot represent a non-manifold edge, and blockout
///         geometry is non-manifold constantly — a wall meeting a floor in a T, a boolean result, an
///         imported mesh with a stray internal face. A kernel that refuses those refuses the ordinary
///         case.
///     </para>
///     <para>
///         ⚠ <b>The position graph and the shading graph are different graphs, and conflating them is
///         the bug this design exists to prevent.</b> A cube's corner is one <i>position</i> and three
///         <i>corners</i>, each with its own normal, its own texture coordinate and possibly its own
///         material. Vertex snapping, welding, edge loops and "drag this corner" run on positions;
///         normals, texture coordinates, materials and smoothing run per corner. One vertex list
///         either splits smooth shading every time it extrudes or welds texture coordinates every time
///         it merges, and both are discovered late, by an artist, in a mesh that is already in a
///         level.
///     </para>
///     <para>
///         ⚠ <b>Faces carry a group id, which is Unreal's PolyGroups and the reason to have them even
///         in a polygon kernel.</b> A boolean returns triangles, and a face that was one wall before
///         the cut has to still be one wall afterwards or the next extrude acts on a sliver. A group
///         is what a tool selects and what a material is assigned to; every operation propagates it.
///     </para>
///     <para>
///         ⚠ <b>Non-manifoldness is <i>reported</i>, not refused.</b> <see cref="Validate" /> says
///         which edges have more than two faces so that a tool can say "this operation needs a
///         manifold edge" — which is a sentence a designer can act on — instead of producing something
///         wrong or throwing halfway through an edit.
///     </para>
///     <para>
///         <b>Mutable, and cloned rather than rebuilt for undo.</b> Doc 24's D3: a position change
///         records the positions it touched, and a topology change records the whole mesh, because a
///         boolean has no inverse to record and an undo implemented as an inverse operation is a
///         second implementation of every tool that will disagree with the first.
///     </para>
/// </remarks>
public sealed class EditMesh {
    /// <summary>How near two positions must be to count as one when a mesh is built from triangles.</summary>
    /// <remarks>
    ///     ⚠ <b>Relative to the mesh's own size, and it is for floating-point noise rather than for
    ///     cleaning up geometry.</b> A generator's seam is <c>cos 0</c> against <c>cos 2π</c> and
    ///     differs in the last bits; welding by <i>distance</i> is a verb with a settings popover and
    ///     belongs to the user.
    /// </remarks>
    public const float DefaultWeldTolerance = 1e-4f;

    /// <summary>How nearly parallel two faces must be to be grouped together.</summary>
    /// <remarks>
    ///     The cosine of about half a degree. Coarser and a cylinder's side becomes one group; finer
    ///     and a cube's face does not, because the two triangles of a quad are computed from different
    ///     corners and their normals differ in the last bits.
    /// </remarks>
    public const float DefaultCoplanarTolerance = 0.99996f;

    readonly List<Vector3> positions = [];
    readonly List<int> corners = [];
    readonly List<MeshFace> faces = [];

    readonly List<Vector3> normals = [];
    readonly List<Vector2> texCoords = [];

    readonly List<MeshEdge> edges = [];
    readonly List<int> edgeFaces = [];
    readonly List<int> edgeStarts = [];

    // ⚠ The tables the other direction, and they are built with the edge table rather than on demand.
    // Every loop, ring, grow and shrink in `MeshTopology` asks "what meets here", and a query that
    // answered it by walking every face would be linear in the mesh per *step* of a walk that has as
    // many steps as the loop is long — which on a corridor's edge ring is the whole mesh squared.
    readonly Dictionary<MeshEdge, int> edgeIndex = [];
    readonly List<int> positionEdges = [];
    readonly List<int> positionEdgeStarts = [];
    readonly List<int> positionFaces = [];
    readonly List<int> positionFaceStarts = [];

    bool topology = true;

    /// <summary>An empty mesh.</summary>
    public EditMesh() {
    }

    /// <summary>A copy of another one.</summary>
    /// <param name="other">The mesh to copy.</param>
    /// <remarks>
    ///     What an undo entry holds for a topology change. Deep: every list is copied, so the recorded
    ///     mesh cannot be changed by the edit that follows it — which is the mistake
    ///     <c>Vixen.Editor.Core</c>'s randomised do/undo/redo suite exists to catch.
    /// </remarks>
    public EditMesh(EditMesh other) {
        ArgumentNullException.ThrowIfNull(other);

        positions.AddRange(other.positions);
        corners.AddRange(other.corners);
        faces.AddRange(other.faces);
        normals.AddRange(other.normals);
        texCoords.AddRange(other.texCoords);
    }

    /// <summary>The shared positions — the graph a snap, a weld and a drag run on.</summary>
    public ReadOnlySpan<Vector3> Positions => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(positions);

    /// <summary>Every face's corners, in face order: a position index per corner.</summary>
    public ReadOnlySpan<int> Corners => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(corners);

    /// <summary>The faces.</summary>
    public IReadOnlyList<MeshFace> Faces => faces;

    /// <summary>Per-corner normals, or empty when the mesh carries none.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty means absent, which is <c>MeshData</c>'s own rule and is worth keeping.</b> A
    ///     layer of zeroes and a layer that is not there are different things: the first is a mesh
    ///     whose normals are wrong and the second is one whose normals have not been computed.
    /// </remarks>
    public ReadOnlySpan<Vector3> Normals => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(normals);

    /// <summary>Per-corner texture coordinates, or empty.</summary>
    public ReadOnlySpan<Vector2> TexCoords => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(texCoords);

    /// <summary>How many shared positions there are.</summary>
    public int PositionCount => positions.Count;

    /// <summary>How many corners there are, over every face.</summary>
    public int CornerCount => corners.Count;

    /// <summary>How many faces there are.</summary>
    public int FaceCount => faces.Count;

    /// <summary>Whether there is anything in it.</summary>
    public bool IsEmpty => faces.Count == 0;

    /// <summary>The edges, each exactly once, rebuilt when the topology has changed.</summary>
    public IReadOnlyList<MeshEdge> Edges {
        get {
            Rebuild();
            return edges;
        }
    }

    /// <summary>The box round every position.</summary>
    /// <remarks>An empty mesh has an empty box rather than one round the origin, so that a caller
    ///     merging boxes is not given a point at zero to include.</remarks>
    public BoundingBox Bounds {
        get {
            if (positions.Count == 0) {
                return default;
            }

            var low = positions[0];
            var high = positions[0];

            foreach (var position in positions) {
                low = Vector3.Min(low, position);
                high = Vector3.Max(high, position);
            }

            return new BoundingBox(low, high);
        }
    }

    /// <summary>Which faces an edge belongs to.</summary>
    /// <param name="edge">Its index in <see cref="Edges" />.</param>
    /// <returns>The face indices — one for a boundary, two for a manifold edge, more for a seam.</returns>
    public ReadOnlySpan<int> FacesOf(int edge) {
        Rebuild();

        var start = edgeStarts[edge];
        var end = edge + 1 < edgeStarts.Count ? edgeStarts[edge + 1] : edgeFaces.Count;

        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(edgeFaces)[start..end];
    }

    /// <summary>Which edge runs between two positions.</summary>
    /// <param name="a">One end.</param>
    /// <param name="b">The other. Order does not matter.</param>
    /// <returns>Its index in <see cref="Edges" />, or <c>-1</c> when no face joins them.</returns>
    public int EdgeBetween(int a, int b) {
        Rebuild();

        return edgeIndex.GetValueOrDefault(a < b ? new MeshEdge(a, b) : new MeshEdge(b, a), -1);
    }

    /// <summary>Which edges meet at a position.</summary>
    /// <param name="position">Its index in <see cref="Positions" />.</param>
    /// <returns>The edge indices. How many there are is the position's valence.</returns>
    /// <remarks>
    ///     ⚠ <b>Valence is what an edge loop walk stops on, so this is the query the whole of
    ///     <c>MeshTopology</c> rests on.</b> A loop runs through positions where four edges meet and
    ///     ends where a different number do — which is why a loop round a cylinder's cap closes and a
    ///     loop that reaches a pole stops there.
    /// </remarks>
    public ReadOnlySpan<int> EdgesAt(int position) {
        Rebuild();

        return Run(positionEdges, positionEdgeStarts, position);
    }

    /// <summary>Which faces meet at a position.</summary>
    /// <param name="position">Its index in <see cref="Positions" />.</param>
    /// <returns>The face indices, one per corner of that face at the position.</returns>
    /// <remarks>A face appears once, unless it visits the position twice — which a face made by a
    ///     boolean's cap legitimately can, and which a caller collecting into a set never notices.</remarks>
    public ReadOnlySpan<int> FacesAt(int position) {
        Rebuild();

        return Run(positionFaces, positionFaceStarts, position);
    }

    /// <summary>The position indices a face runs through.</summary>
    /// <param name="face">Its index in <see cref="Faces" />.</param>
    /// <returns>Its corners, in winding order.</returns>
    public ReadOnlySpan<int> CornersOf(int face) {
        var entry = faces[face];

        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(corners)
            .Slice(entry.Start, entry.Count);
    }

    /// <summary>Adds a shared position.</summary>
    /// <param name="position">Where.</param>
    /// <returns>Its index.</returns>
    public int AddPosition(Vector3 position) {
        positions.Add(position);
        return positions.Count - 1;
    }

    /// <summary>Adds a face over positions that are already in the mesh.</summary>
    /// <param name="loop">Its corners, as position indices, in winding order.</param>
    /// <param name="group">Which group it belongs to.</param>
    /// <param name="smoothing">Which smoothing group, or zero for hard edges.</param>
    /// <returns>Its index.</returns>
    /// <exception cref="ArgumentException">Fewer than three corners, or one names no position.</exception>
    /// <remarks>
    ///     ⚠ <b>Refused rather than repaired.</b> A face of two corners is a line and a corner naming
    ///     a position that is not there is a mesh that will fail somewhere further along, in an
    ///     operation that did not create it. Everything this kernel <i>reports</i> rather than refusing
    ///     — a non-manifold edge, an inconsistent winding — is something a designer can legitimately
    ///     make; neither of these is.
    /// </remarks>
    public int AddFace(ReadOnlySpan<int> loop, int group = 0, int smoothing = 0) {
        if (loop.Length < 3) {
            throw new ArgumentException($"A face wants three corners or more and was given {loop.Length}.", nameof(loop));
        }

        foreach (var index in loop) {
            if ((uint) index >= (uint) positions.Count) {
                throw new ArgumentException($"Corner {index} names no position.", nameof(loop));
            }
        }

        var face = new MeshFace(corners.Count, loop.Length, group, smoothing);

        faces.Add(face);

        foreach (var index in loop) {
            corners.Add(index);
        }

        // Layers stay parallel to the corners, so a mesh that carries one gets defaults for the new
        // face rather than an array that is short by four and throws on the next read.
        if (normals.Count > 0) {
            normals.AddRange(Enumerable.Repeat(Vector3.Zero, loop.Length));
        }

        if (texCoords.Count > 0) {
            texCoords.AddRange(Enumerable.Repeat(Vector2.Zero, loop.Length));
        }

        topology = true;
        return faces.Count - 1;
    }

    /// <summary>Empties the face table, and optionally the positions with it.</summary>
    /// <param name="keepPositions">Whether the shared positions stay.</param>
    /// <remarks>
    ///     ⚠ <b>Keeping the positions is what every operation in <see cref="MeshOperations" /> does,
    ///     and it is not an optimisation.</b> A position index is what a selection holds, what an undo
    ///     entry records and what a drag in flight is writing to — doc 24's D3 turns on one meaning the
    ///     same thing from one frame to the next. Rebuilding the face table over the same positions is
    ///     what lets an extrude renumber the faces without renumbering the corners a designer is
    ///     dragging.
    /// </remarks>
    public void Clear(bool keepPositions = false) {
        faces.Clear();
        corners.Clear();
        normals.Clear();
        texCoords.Clear();

        if (!keepPositions) {
            positions.Clear();
        }

        topology = true;
    }

    /// <summary>Moves one shared position.</summary>
    /// <param name="index">Which.</param>
    /// <param name="position">Where to.</param>
    /// <remarks>
    ///     ⚠ <b>Not a topology change, which is why the edge table survives it.</b> Doc 24's D3 turns
    ///     on this distinction: a position change records the positions it touched and merges with the
    ///     drag before it, and a topology change records the whole mesh.
    /// </remarks>
    public void MovePosition(int index, Vector3 position) => positions[index] = position;

    /// <summary>Puts a face in a group.</summary>
    /// <param name="face">Which.</param>
    /// <param name="group">Which group.</param>
    public void SetGroup(int face, int group) {
        var entry = faces[face];

        faces[face] = entry with { Group = group };
    }

    /// <summary>Puts a face in a smoothing group.</summary>
    /// <param name="face">Which.</param>
    /// <param name="smoothing">Which smoothing group, or zero for hard edges all round it.</param>
    /// <remarks>
    ///     ⚠ <b>Not a topology change, so the edge table survives it.</b> Smoothing decides how the
    ///     corner normals are computed when the mesh is handed to a renderer — see
    ///     <c>EditMeshes.ToMeshData</c> — and changes nothing about which faces meet where.
    /// </remarks>
    public void SetSmoothing(int face, int smoothing) {
        var entry = faces[face];

        faces[face] = entry with { Smoothing = smoothing };
    }

    /// <summary>Gives the mesh a per-corner normal layer, sized to its corners.</summary>
    /// <param name="values">One per corner.</param>
    /// <exception cref="ArgumentException">The wrong number of them.</exception>
    public void SetNormals(ReadOnlySpan<Vector3> values) {
        Layer(values, normals, nameof(values));
    }

    /// <summary>Ditto for texture coordinates.</summary>
    /// <param name="values">One per corner.</param>
    public void SetTexCoords(ReadOnlySpan<Vector2> values) {
        Layer(values, texCoords, nameof(values));
    }

    /// <summary>Turns the faces into triangles.</summary>
    /// <returns>Three position indices per triangle.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Ear clipping, which is what doc 24's P3 said would replace the fan.</b> A fan from
    ///         each face's first corner is exact for a convex face and wrong for a concave one, and
    ///         the verbs that make concave n-gons are here now: an inset over an L-shaped floor, a
    ///         dissolve that merges two faces round a corner, a bridge between loops of different
    ///         shapes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every face still produces exactly <c>Count − 2</c> triangles, whatever route it
    ///         took.</b> That is what lets <see cref="Regroup" />, <c>MeshElements</c> and
    ///         <c>EditMeshes.ToMeshData</c> walk the faces and the triangles together without a second
    ///         table — and it is why a face ear clipping cannot resolve falls back to the fan rather
    ///         than emitting fewer.
    ///     </para>
    /// </remarks>
    public int[] Triangulate() {
        var total = 0;

        foreach (var face in faces) {
            total += Math.Max(face.Count - 2, 0);
        }

        var indices = new int[total * 3];
        var at = 0;

        for (var face = 0; face < faces.Count; face++) {
            Triangulate(face, indices, ref at);
        }

        return indices;
    }

    /// <summary>Ear-clips one face into the run of triangles it owns.</summary>
    /// <remarks>
    ///     ⚠ <b>In the face's own plane, found from its normal, because ear clipping is planar.</b>
    ///     An n-gon that somebody has dragged a corner of is not exactly planar; projecting onto the
    ///     two axes most nearly in its plane is what makes the containment test meaningful, and using
    ///     the world axes instead is what makes a vertical wall triangulate as a line.
    /// </remarks>
    void Triangulate(int face, int[] into, ref int at) {
        var entry = faces[face];

        if (entry.Count < 3) {
            return;
        }

        if (entry.Count == 3) {
            into[at++] = corners[entry.Start];
            into[at++] = corners[entry.Start + 1];
            into[at++] = corners[entry.Start + 2];

            return;
        }

        var normal = Normal(face);

        if (normal.LengthSquared() <= 0f) {
            Fan(entry, into, ref at);
            return;
        }

        // Two axes spanning the face's plane, taken from whichever world axis is least parallel to
        // its normal so the cross product cannot collapse.
        var guide = MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var across = Vector3.Normalize(Vector3.Cross(normal, guide));
        var along = Vector3.Cross(normal, across);

        var loop = new List<int>(entry.Count);
        var flat = new List<Vector2>(entry.Count);

        for (var corner = 0; corner < entry.Count; corner++) {
            var index = corners[entry.Start + corner];
            var position = positions[index];

            loop.Add(index);
            flat.Add(new(Vector3.Dot(position, across), Vector3.Dot(position, along)));
        }

        var written = at;

        // Every pass either clips an ear — which shortens the loop — or falls back and returns, so
        // this terminates without a counter guarding it.
        while (loop.Count > 3) {
            var clipped = false;

            for (var corner = 0; corner < loop.Count; corner++) {
                var previous = (corner + loop.Count - 1) % loop.Count;
                var next = (corner + 1) % loop.Count;

                if (!IsEar(flat, previous, corner, next)) {
                    continue;
                }

                into[at++] = loop[previous];
                into[at++] = loop[corner];
                into[at++] = loop[next];

                loop.RemoveAt(corner);
                flat.RemoveAt(corner);

                clipped = true;
                break;
            }

            if (clipped) {
                continue;
            }

            // ⚠ A self-intersecting loop, which no triangulation of it is right for. The fan is
            // wrong too and it is wrong *predictably* — and the alternative, emitting fewer triangles
            // than the face owns, breaks the one-to-one walk every other reader relies on.
            at = written;
            Fan(entry, into, ref at);

            return;
        }

        for (var corner = 1; corner + 1 < loop.Count; corner++) {
            into[at++] = loop[0];
            into[at++] = loop[corner];
            into[at++] = loop[corner + 1];
        }
    }

    /// <summary>The old triangulation, kept as what a face nothing else can resolve falls back to.</summary>
    void Fan(MeshFace entry, int[] into, ref int at) {
        for (var corner = 1; corner + 1 < entry.Count; corner++) {
            into[at++] = corners[entry.Start];
            into[at++] = corners[entry.Start + corner];
            into[at++] = corners[entry.Start + corner + 1];
        }
    }

    /// <summary>Whether a corner of a flattened loop is an ear: convex, and containing nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every question here is a sign, and a sign is the one thing a <c>float</c>
    ///         expression compared against zero cannot be trusted to answer.</b> This read
    ///         <c>((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X)) &lt;= 0f</c> until doc 41's
    ///         determinism work gave the repository a two-dimensional orientation predicate. The
    ///         error is in the subtractions rather than in the products, so no input-scaled epsilon
    ///         rescues it: three points on <c>y = 3x</c> whose coordinates are all integers a
    ///         <c>float</c> holds exactly evaluate to <c>+16</c> in one argument order and
    ///         <c>-67108864</c> in another, so the naive form is not even antisymmetric.
    ///     </para>
    ///     <para>
    ///         What that buys is not a different triangulation of an ordinary face — it is the
    ///         guarantee that a reflex corner is never cut as an ear, which is how ear clipping
    ///         produces overlapping triangles on the near-degenerate n-gons a boolean makes when a cut
    ///         passes almost exactly through a vertex. <c>MeshBoolean</c> already refuses to classify
    ///         a point against a plane with a tolerance, for the same reason and on doc 24 § D5's
    ///         argument; this is that decision applied to the other predicate the kernel asks.
    ///     </para>
    ///     <para>
    ///         The cost is confined to n-gons: a face of three corners returns before reaching here,
    ///         so a triangle mesh pays nothing, and the predicate's filtered path is a handful of
    ///         <c>double</c> operations when the answer is not close.
    ///     </para>
    /// </remarks>
    static bool IsEar(List<Vector2> flat, int previous, int corner, int next) {
        var a = flat[previous];
        var b = flat[corner];
        var c = flat[next];

        // Anticlockwise in the projected plane, which is what the face's own normal makes it. A
        // reflex corner has the opposite sign and is never an ear, and a collinear one has no area
        // to cut.
        if (ExactPredicates.Orient2D(a, b, c) <= 0) {
            return false;
        }

        for (var index = 0; index < flat.Count; index++) {
            if (index == previous || index == corner || index == next) {
                continue;
            }

            var point = flat[index];

            if (ExactPredicates.Orient2D(a, b, point) >= 0
                && ExactPredicates.Orient2D(b, c, point) >= 0
                && ExactPredicates.Orient2D(c, a, point) >= 0) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds a mesh from a triangle soup, welding its positions and grouping its faces.</summary>
    /// <param name="source">The positions, one per drawing vertex.</param>
    /// <param name="indices">Three per triangle.</param>
    /// <param name="weld">How near two positions may be and still be one, as a fraction of the bounds.</param>
    /// <param name="coplanar">How nearly parallel two faces must be to share a group.</param>
    /// <returns>The mesh.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>This is the door into the kernel and it takes arrays rather than a <c>MeshData</c>.</b>
    ///         Doc 24's D1: a geometry kernel that depended on the render assembly to describe a
    ///         triangle would be backwards. The six-line copy from <c>MeshData</c> lives in the editor
    ///         beside the code that uploads it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Welding is not optional and the reason is D2's.</b> A drawing vertex is a corner:
    ///         a cube's eight corners arrive as twenty-four entries because each carries three normals
    ///         and three texture coordinates. Without welding the position graph would have
    ///         twenty-four members, its twelve edges would not exist, and every one of them that did
    ///         would be a boundary.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Groups are coplanar connected components, which is what makes a cube's side one
    ///         wall.</b> A triangle soup has no faces bigger than a triangle, so the diagonal across a
    ///         cube's side is a real edge and the two halves are two faces. The group is what says they
    ///         are one thing — Unreal's PolyGroups exactly — and it is what an extrude will act on
    ///         instead of the triangulation.
    ///     </para>
    /// </remarks>
    public static EditMesh FromTriangles(
        ReadOnlySpan<Vector3> source,
        ReadOnlySpan<int> indices,
        float weld = DefaultWeldTolerance,
        float coplanar = DefaultCoplanarTolerance
    ) {
        var mesh = new EditMesh();
        var remap = Weld(source, weld, mesh.positions);

        Span<int> loop = stackalloc int[3];

        for (var index = 0; index + 2 < indices.Length; index += 3) {
            loop[0] = remap[indices[index]];
            loop[1] = remap[indices[index + 1]];
            loop[2] = remap[indices[index + 2]];

            // ⚠ A triangle whose corners collapsed onto one another during the weld is dropped rather
            // than kept as a face with two equal corners. It has no area, no normal and no edge; a
            // face table containing one is a table every operation has to test for.
            if (loop[0] != loop[1] && loop[1] != loop[2] && loop[2] != loop[0]) {
                mesh.AddFace(loop);
            }
        }

        mesh.Regroup(coplanar);
        return mesh;
    }

    /// <summary>Puts every face into a group with the coplanar faces it is connected to.</summary>
    /// <param name="coplanar">How nearly parallel two faces must be to share a group.</param>
    /// <remarks>
    ///     Connected <i>and</i> coplanar: two parallel walls facing the same way are two groups because
    ///     no edge joins them, and the two triangles of a cube's side are one because an edge does.
    /// </remarks>
    public void Regroup(float coplanar = DefaultCoplanarTolerance) {
        Rebuild();

        var parent = new int[faces.Count];
        var facing = new Vector3[faces.Count];

        for (var face = 0; face < faces.Count; face++) {
            parent[face] = face;
            facing[face] = Normal(face);
        }

        for (var edge = 0; edge < edges.Count; edge++) {
            var touching = FacesOf(edge);

            for (var first = 0; first < touching.Length; first++) {
                for (var second = first + 1; second < touching.Length; second++) {
                    if (Vector3.Dot(facing[touching[first]], facing[touching[second]]) >= coplanar) {
                        Union(touching[first], touching[second]);
                    }
                }
            }
        }

        // ⚠ Numbered from zero in face order rather than by the root's index. A group id is written to
        // a file and shown in an inspector; ids that jumped from 0 to 7 to 23 because that is where
        // the union-find happened to root them would be a mesh nobody could read.
        var seen = new Dictionary<int, int>();

        for (var face = 0; face < faces.Count; face++) {
            var root = Find(face);

            if (!seen.TryGetValue(root, out var group)) {
                group = seen.Count;
                seen[root] = group;
            }

            faces[face] = faces[face] with { Group = group };
        }

        int Find(int face) {
            while (parent[face] != face) {
                parent[face] = parent[parent[face]];
                face = parent[face];
            }

            return face;
        }

        void Union(int first, int second) {
            var a = Find(first);
            var b = Find(second);

            if (a != b) {
                parent[Math.Max(a, b)] = Math.Min(a, b);
            }
        }
    }

    /// <summary>A face's normal, from its winding.</summary>
    /// <param name="face">Which.</param>
    /// <returns>The unit normal, or zero for a face with no area.</returns>
    /// <remarks>
    ///     ⚠ <b>Newell's method rather than the cross product of the first three corners.</b> An n-gon
    ///     is not exactly planar once anybody has dragged a corner of it, and three corners of a
    ///     five-sided face can be very nearly collinear — which gives a normal that is noise. Newell's
    ///     is the area-weighted answer over the whole loop and degrades to the cross product for a
    ///     triangle.
    /// </remarks>
    public Vector3 Normal(int face) {
        var entry = faces[face];
        var total = Vector3.Zero;

        for (var corner = 0; corner < entry.Count; corner++) {
            var current = positions[corners[entry.Start + corner]];
            var next = positions[corners[entry.Start + ((corner + 1) % entry.Count)]];

            total += new Vector3(
                (current.Y - next.Y) * (current.Z + next.Z),
                (current.Z - next.Z) * (current.X + next.X),
                (current.X - next.X) * (current.Y + next.Y)
            );
        }

        var lengthSquared = total.LengthSquared();

        // ⚠ Against zero rather than against an absolute epsilon, and the capsule is what found it.
        // The Newell sum is twice the face's area, so a fixed tolerance is a statement about how big a
        // face has to be — and the triangles round a hemisphere's pole are genuinely small. A
        // primitive built at a tenth of the scale would have had every face declared degenerate.
        //
        // ⚠ And the division is written out rather than delegated to `Vector3.Normalize`, because that
        // method carries exactly the absolute epsilon this line exists to avoid: it returns zero below
        // `MathUtil.ZeroTolerance` of length, which is 1e-6. The comment above was therefore true of
        // this line and false of the method, and the two together made a face's normal depend on how
        // big the model was. Found by docs/plan/42 § U2's scale gate — a flat strip whose quads span a
        // hundredth of a unit triangulates by ear clipping at one scale and falls back to a fan at a
        // thousandth of it, because `Triangulate` reads a zero normal as "no plane to clip in". Two
        // different triangulations of one surface is two different unwraps of it.
        return lengthSquared > 0f ? total * (1f / MathF.Sqrt(lengthSquared)) : Vector3.Zero;
    }

    /// <summary>A face's area.</summary>
    /// <param name="face">Which.</param>
    /// <returns>The area, in world units squared.</returns>
    /// <remarks>Half the length of Newell's sum, which is the same number the normal is derived
    ///     from — so a face with no area is exactly a face with no normal.</remarks>
    public float Area(int face) {
        var entry = faces[face];
        var total = Vector3.Zero;

        for (var corner = 0; corner < entry.Count; corner++) {
            var current = positions[corners[entry.Start + corner]];
            var next = positions[corners[entry.Start + ((corner + 1) % entry.Count)]];

            total += new Vector3(
                (current.Y - next.Y) * (current.Z + next.Z),
                (current.Z - next.Z) * (current.X + next.X),
                (current.X - next.X) * (current.Y + next.Y)
            );
        }

        return total.Length() * 0.5f;
    }

    /// <summary>Everything that is true of the mesh that a tool might need to know.</summary>
    /// <returns>The report.</returns>
    /// <remarks>
    ///     ⚠ <b>The highest-value thing in doc 24's testing table, and it has to be written first.</b>
    ///     A mesh operation that corrupts the edge table produces geometry that looks correct and
    ///     fails three operations later, in a mesh a designer has spent an hour on, with no way to
    ///     attribute it. Every operation asserting the whole structure afterwards turns that into a
    ///     failing test in the commit that caused it.
    /// </remarks>
    public MeshReport Validate() {
        Rebuild();

        List<int> nonManifold = [];
        List<int> boundary = [];
        List<int> reversed = [];
        List<int> degenerate = [];

        for (var edge = 0; edge < edges.Count; edge++) {
            var touching = FacesOf(edge);

            switch (touching.Length) {
                case 1:
                    boundary.Add(edge);
                    break;

                case 2 when !Opposed(edges[edge], touching[0], touching[1]):
                    // ⚠ Two faces walking one edge the same way are two faces facing opposite ways.
                    // Reported rather than repaired: which of the two is the wrong one is a question
                    // about what the designer meant, and "flip normals" is a verb they run.
                    reversed.Add(edge);
                    break;

                case > 2:
                    nonManifold.Add(edge);
                    break;

                default:
                    break;
            }
        }

        // ⚠ Relative to the mesh's own size, which is the same lesson the weld tolerance teaches and
        // which the capsule taught again here. An absolute epsilon is a statement about how big a face
        // has to be to count, and the triangles round a hemisphere's pole are genuinely small — a
        // primitive built at a tenth of the scale would have had all of them declared degenerate.
        var extent = Bounds;
        var diagonal = (extent.Maximum - extent.Minimum).LengthSquared();
        var sliver = diagonal * 1e-9f;

        for (var face = 0; face < faces.Count; face++) {
            if (Area(face) <= sliver) {
                degenerate.Add(face);
            }
        }

        var used = new bool[positions.Count];

        foreach (var corner in corners) {
            used[corner] = true;
        }

        var orphans = 0;

        foreach (var flag in used) {
            if (!flag) {
                orphans++;
            }
        }

        return new MeshReport(nonManifold, boundary, reversed, degenerate, orphans);
    }

    /// <summary>Rebuilds the edge table if anything has changed the topology.</summary>
    /// <remarks>
    ///     ⚠ <b>Lazy, and a position move does not invalidate it.</b> Dragging a corner changes where
    ///     the geometry is and not what is joined to what, and a table rebuilt per frame of a drag is
    ///     the whole cost of the drag.
    /// </remarks>
    void Rebuild() {
        if (!topology) {
            return;
        }

        topology = false;

        edges.Clear();
        edgeFaces.Clear();
        edgeStarts.Clear();
        edgeIndex.Clear();

        var byPair = new Dictionary<MeshEdge, List<int>>();
        var order = new List<MeshEdge>();

        for (var face = 0; face < faces.Count; face++) {
            var entry = faces[face];

            for (var corner = 0; corner < entry.Count; corner++) {
                var from = corners[entry.Start + corner];
                var to = corners[entry.Start + ((corner + 1) % entry.Count)];

                if (from == to) {
                    continue;
                }

                var edge = from < to ? new MeshEdge(from, to) : new MeshEdge(to, from);

                if (!byPair.TryGetValue(edge, out var touching)) {
                    byPair[edge] = touching = [];
                    order.Add(edge);
                }

                touching.Add(face);
            }
        }

        // A list beside the dictionary, because the order edges are discovered in is the order a test,
        // a drawn overlay and a saved file all read them in — and a hash set's order is the hash's.
        foreach (var edge in order) {
            edgeIndex[edge] = edges.Count;
            edgeStarts.Add(edgeFaces.Count);
            edges.Add(edge);
            edgeFaces.AddRange(byPair[edge]);
        }

        Incidence();
    }

    /// <summary>Fills the position-to-edge and position-to-face runs, from the tables just built.</summary>
    /// <remarks>
    ///     ⚠ <b>Counting sort into flat runs rather than a list per position.</b> A mesh of ten
    ///     thousand positions is ten thousand small lists otherwise, allocated on every topology
    ///     change — which for a loop cut is once per cut and for an extrude is once per drag frame.
    ///     Two passes over each table and two integer arrays is the same answer with no allocation
    ///     per position.
    /// </remarks>
    void Incidence() {
        Counted(positionEdgeStarts, positionEdges, edges.Count * 2, Edge);
        Counted(positionFaceStarts, positionFaces, corners.Count, Corner);

        (int Position, int Value) Edge(int at) =>
            (at % 2 == 0 ? edges[at / 2].A : edges[at / 2].B, at / 2);

        (int Position, int Value) Corner(int at) {
            // Which face a corner belongs to, by binary search over the face table rather than by a
            // second array: the faces are in corner order, so the face owning a corner is the last one
            // whose run starts at or before it.
            var low = 0;
            var high = faces.Count - 1;

            while (low < high) {
                var middle = (low + high + 1) / 2;

                if (faces[middle].Start <= at) {
                    low = middle;
                } else {
                    high = middle - 1;
                }
            }

            return (corners[at], low);
        }

        void Counted(List<int> starts, List<int> values, int total, Func<int, (int Position, int Value)> entry) {
            starts.Clear();
            values.Clear();

            for (var index = 0; index <= positions.Count; index++) {
                starts.Add(0);
            }

            for (var at = 0; at < total; at++) {
                starts[entry(at).Position + 1]++;
            }

            for (var index = 1; index < starts.Count; index++) {
                starts[index] += starts[index - 1];
            }

            for (var index = 0; index < total; index++) {
                values.Add(0);
            }

            // ⚠ A cursor per position, taken from the starts and advanced — and the starts are left
            // where they are. Advancing the starts themselves is the shorter version of this loop and
            // it destroys the table it is filling.
            var cursor = new int[positions.Count];

            for (var at = 0; at < total; at++) {
                var (position, value) = entry(at);

                values[starts[position] + cursor[position]] = value;
                cursor[position]++;
            }
        }
    }

    /// <summary>One position's run out of a flat table.</summary>
    static ReadOnlySpan<int> Run(List<int> values, List<int> starts, int position) {
        if ((uint) position + 1 >= (uint) starts.Count) {
            return default;
        }

        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values)[starts[position]..starts[position + 1]];
    }

    /// <summary>Whether two faces walk an edge in opposite directions, which is consistent winding.</summary>
    bool Opposed(MeshEdge edge, int first, int second) => Forwards(edge, first) != Forwards(edge, second);

    /// <summary>Whether a face walks an edge from A to B rather than from B to A.</summary>
    bool Forwards(MeshEdge edge, int face) {
        var entry = faces[face];

        for (var corner = 0; corner < entry.Count; corner++) {
            var from = corners[entry.Start + corner];
            var to = corners[entry.Start + ((corner + 1) % entry.Count)];

            if (from == edge.A && to == edge.B) {
                return true;
            }

            if (from == edge.B && to == edge.A) {
                return false;
            }
        }

        return false;
    }

    void Layer<T>(ReadOnlySpan<T> values, List<T> into, string name) {
        if (values.Length != corners.Count) {
            throw new ArgumentException(
                $"A corner layer wants {corners.Count} values and was given {values.Length}.",
                name
            );
        }

        into.Clear();

        foreach (var value in values) {
            into.Add(value);
        }
    }

    /// <summary>Collapses coincident positions and says where each original one went.</summary>
    /// <remarks>
    ///     ⚠ <b>A grid of cells the size of the tolerance, and the twenty-seven around each one are
    ///     searched.</b> Hashing the rounded position alone is one line shorter and wrong in exactly
    ///     the case this exists for: two positions a hair apart either side of a cell boundary land in
    ///     different buckets and are never compared, so the seam it was written to close stays open on
    ///     whichever meshes happen to straddle one.
    /// </remarks>
    static int[] Weld(ReadOnlySpan<Vector3> source, float tolerance, List<Vector3> into) {
        var remap = new int[source.Length];

        if (source.Length == 0) {
            return remap;
        }

        var low = source[0];
        var high = source[0];

        foreach (var position in source) {
            low = Vector3.Min(low, position);
            high = Vector3.Max(high, position);
        }

        var epsilon = tolerance <= 0f ? 0f : (high - low).Length() * tolerance;

        if (epsilon <= 0f) {
            var exact = new Dictionary<Vector3, int>(source.Length);

            for (var index = 0; index < source.Length; index++) {
                if (!exact.TryGetValue(source[index], out var at)) {
                    at = into.Count;
                    into.Add(source[index]);
                    exact[source[index]] = at;
                }

                remap[index] = at;
            }

            return remap;
        }

        var cells = new Dictionary<(long X, long Y, long Z), List<int>>();
        var squared = epsilon * epsilon;

        for (var index = 0; index < source.Length; index++) {
            var position = source[index];
            var cell = Cell(position, epsilon);
            var at = -1;

            for (var x = -1; at < 0 && x <= 1; x++) {
                for (var y = -1; at < 0 && y <= 1; y++) {
                    for (var z = -1; at < 0 && z <= 1; z++) {
                        if (!cells.TryGetValue((cell.X + x, cell.Y + y, cell.Z + z), out var bucket)) {
                            continue;
                        }

                        foreach (var candidate in bucket) {
                            if (Vector3.DistanceSquared(into[candidate], position) <= squared) {
                                at = candidate;
                                break;
                            }
                        }
                    }
                }
            }

            if (at < 0) {
                at = into.Count;
                into.Add(position);

                if (!cells.TryGetValue(cell, out var bucket)) {
                    cells[cell] = bucket = [];
                }

                bucket.Add(at);
            }

            remap[index] = at;
        }

        return remap;
    }

    static (long X, long Y, long Z) Cell(Vector3 position, float size) =>
        ((long) MathF.Floor(position.X / size), (long) MathF.Floor(position.Y / size),
            (long) MathF.Floor(position.Z / size));
}
