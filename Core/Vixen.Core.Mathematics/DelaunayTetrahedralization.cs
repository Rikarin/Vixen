// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Mathematics;

/// <summary>
///     The Delaunay tetrahedralisation of a set of points, built by Bowyer–Watson over
///     <see cref="ExactPredicates" />.
/// </summary>
/// <remarks>
///     <para>
///         Bowyer–Watson is fifteen lines of idea: to insert a point, delete every cell whose
///         circumsphere contains it, and fill the cavity that leaves by joining the point to each
///         of the cavity's boundary faces. All of the difficulty is in "contains", which is a
///         question about a sign, and which floating point cannot answer. Every predicate here is
///         exact, and every tie a degenerate input produces is broken by
///         <see cref="ExactPredicates.InSphere(Vector3, Vector3, Vector3, Vector3, Vector3, int, int, int, int, int)" />
///         the same way every time — which is what a grid of points needs, since its cells are
///         cospherical eight at a time and no amount of tolerance decides anything about them.
///     </para>
///     <para>
///         <strong>The Delaunay property is what makes this worth building rather than any old
///         tetrahedralisation.</strong> A cell's circumsphere is empty of other points, so the
///         four points a position interpolates between are its natural neighbours rather than
///         whichever four a mesh generator happened to group. For light probes that is the
///         difference between indirect light that changes smoothly as an object walks across a
///         room and light that jumps when it crosses an arbitrary seam.
///     </para>
///     <para>
///         <strong>Construction is enclosed rather than incremental at the hull.</strong> The
///         points are inserted into a tetrahedron large enough to hold them all, and the cells
///         touching its four corners are dropped at the end. That is the textbook arrangement and
///         it has a textbook hazard: an enclosure that is not large enough silently loses cells
///         near the hull. So the result is <em>checked</em> — <see cref="FillsConvexHull" /> is
///         true only when what came out uses every point and its boundary is closed and convex,
///         which for a complex whose cells are all Delaunay is exactly the statement that it is
///         the Delaunay tetrahedralisation of the input. A failed check grows the enclosure and
///         rebuilds.
///     </para>
///     <para>
///         Duplicate positions are merged, and <see cref="Vertices" /> is the merged set — indices
///         into it, not into what the caller passed. Input that has no volume at all (fewer than
///         four points, or all of them on one plane) has no tetrahedralisation, which is reported
///         by <see cref="IsDegenerate" /> rather than by an exception: a floor's worth of probes
///         at one height is a thing an author can legitimately make, and the caller has a
///         fallback for it.
///     </para>
/// </remarks>
public sealed class DelaunayTetrahedralization {
    /// <summary>
    ///     The three vertices of the face opposite each of a cell's four vertices, wound so that
    ///     the opposite vertex is on the face's positive side.
    /// </summary>
    /// <remarks>
    ///     Not an arbitrary tabulation: <see cref="ExactPredicates.Orient3D" /> is antisymmetric,
    ///     so the winding of each row is the one that makes the permutation even. Get a row
    ///     backwards and every orientation test through that face answers the opposite of the
    ///     truth, which corrupts the mesh in a way that only shows up several insertions later.
    /// </remarks>
    static readonly int[] FaceVertices = [
        1, 3, 2,
        0, 2, 3,
        0, 3, 1,
        0, 1, 2
    ];

    /// <summary>
    ///     How far out the enclosing tetrahedron is put, as a multiple of the input's own radius.
    /// </summary>
    /// <remarks>
    ///     Two attempts, six orders of magnitude apart. The first is already far enough that only
    ///     a point layout with an aspect ratio in the millions could defeat it; the second exists
    ///     so that "far enough" is a fact the build establishes rather than one it assumes.
    /// </remarks>
    static readonly float[] Enclosures = [1e6f, 1e12f];

    readonly int[] cellNeighbours;
    readonly int[] cellVertices;
    readonly Vector3[] vertices;

    DelaunayTetrahedralization(Vector3[] vertices, int[] cellVertices, int[] cellNeighbours, bool fillsConvexHull) {
        this.vertices = vertices;
        this.cellVertices = cellVertices;
        this.cellNeighbours = cellNeighbours;
        FillsConvexHull = fillsConvexHull;
    }

    /// <summary>The distinct input positions, in first-seen order. Cell indices refer to these.</summary>
    public ReadOnlySpan<Vector3> Vertices => vertices;

    /// <summary>Four vertex indices per cell, positively oriented.</summary>
    /// <remarks>
    ///     Positively oriented meaning <see cref="ExactPredicates.Orient3D" /> over the four
    ///     returns <c>+1</c>. The invariant holds for every cell at every moment during
    ///     construction, which is what lets the in-sphere test be called without a sign to correct
    ///     for.
    /// </remarks>
    public ReadOnlySpan<int> CellVertices => cellVertices;

    /// <summary>
    ///     Four neighbour cell indices per cell — the cell across the face opposite each vertex,
    ///     or <c>-1</c> where that face is on the boundary.
    /// </summary>
    public ReadOnlySpan<int> CellNeighbours => cellNeighbours;

    /// <summary>How many cells there are.</summary>
    public int CellCount => cellVertices.Length / 4;

    /// <summary>Whether the input has no volume, and so no tetrahedralisation.</summary>
    /// <remarks>
    ///     True for fewer than four distinct points, and for any number of them lying on a single
    ///     plane or line. <see cref="CellCount" /> is zero in that case and
    ///     <see cref="TryFind(Vector3, out int, out Vector4)" /> always fails, so a caller with a
    ///     fallback needs no special case beyond having one.
    /// </remarks>
    public bool IsDegenerate => cellVertices.Length == 0;

    /// <summary>
    ///     Whether the cells fill the convex hull of the input — the check that says this is the
    ///     Delaunay tetrahedralisation and not merely a Delaunay-looking piece of one.
    /// </summary>
    /// <remarks>
    ///     Every cell that comes out of Bowyer–Watson has an empty circumsphere by construction,
    ///     so the only way to be wrong is to be <em>missing</em> cells. A complex of empty-sphere
    ///     cells whose boundary is convex and which uses every point is the Delaunay
    ///     tetrahedralisation; that is the whole proof, and this property is the half of it a
    ///     computer has to check. False only for a degenerate input, or for an enclosure that
    ///     could not be grown far enough — see the class remarks.
    /// </remarks>
    public bool FillsConvexHull { get; }

    /// <summary>Builds the tetrahedralisation of <paramref name="positions" />.</summary>
    /// <param name="positions">
    ///     The points. Must all be finite; duplicates are merged rather than rejected.
    /// </param>
    /// <exception cref="ArgumentException">A position is not finite.</exception>
    public static DelaunayTetrahedralization Build(ReadOnlySpan<Vector3> positions) {
        var distinct = Deduplicate(positions);

        if (distinct.Length < 4) {
            return new(distinct, [], [], false);
        }

        foreach (var enclosure in Enclosures) {
            var built = new Incremental(distinct, enclosure).Run();

            if (built is not null) {
                return built;
            }
        }

        // Every attempt lost cells, or the input has no volume. Either way there is nothing here
        // to interpolate over and the caller is told so rather than handed a partial mesh.
        return new(distinct, [], [], false);
    }

    /// <summary>
    ///     Finds the cell containing <paramref name="position" /> and the barycentric weights of
    ///     the position within it.
    /// </summary>
    /// <param name="position">Where to look.</param>
    /// <param name="cell">The cell found, or <c>-1</c>.</param>
    /// <param name="weights">
    ///     The weights of the cell's four vertices, in <see cref="CellVertices" /> order. They sum
    ///     to one and are all non-negative.
    /// </param>
    /// <returns>False when the position is outside the hull, or the mesh is degenerate.</returns>
    /// <remarks>
    ///     A walk rather than a search: step to the neighbour across whichever face the position
    ///     is beyond, and stop when it is beyond none of them. The steps are exact orientation
    ///     tests, so the walk cannot wander in circles on a coplanar face the way a floating-point
    ///     one can, and the boundary is where it correctly stops: a face with no neighbour that
    ///     the position is beyond means the position is outside the hull, because the hull is what
    ///     the boundary is.
    /// </remarks>
    public bool TryFind(Vector3 position, out int cell, out Vector4 weights) => TryFind(position, 0, out cell, out weights);

    /// <summary><see cref="TryFind(Vector3, out int, out Vector4)" />, starting the walk from a hint.</summary>
    /// <param name="position">Where to look.</param>
    /// <param name="hint">
    ///     A cell to start from — the one a nearby position was found in, usually. Out-of-range
    ///     values are ignored rather than rejected, so a stale hint costs a longer walk and
    ///     nothing else.
    /// </param>
    /// <param name="cell">The cell found, or <c>-1</c>.</param>
    /// <param name="weights">The weights of the cell's four vertices.</param>
    /// <returns>False when the position is outside the hull, or the mesh is degenerate.</returns>
    public bool TryFind(Vector3 position, int hint, out int cell, out Vector4 weights) {
        cell = -1;
        weights = default;

        var count = CellCount;
        if (count == 0) {
            return false;
        }

        var current = (uint)hint < (uint)count ? hint : 0;
        var previous = -1;

        for (var step = 0; step <= count; step++) {
            var moved = false;

            for (var face = 0; face < 4; face++) {
                if (OrientToFace(current, face, position) >= 0) {
                    continue;
                }

                // The position is beyond this face. A face with nothing beyond it is the hull,
                // and being beyond the hull is the answer rather than a step. This test has to
                // come before the one below it: on a single-cell mesh every face is a boundary,
                // and "no neighbour" and "the neighbour we came from" are both -1.
                var neighbour = cellNeighbours[(current * 4) + face];

                if (neighbour < 0) {
                    return false;
                }

                if (neighbour == previous) {
                    continue;
                }

                previous = current;
                current = neighbour;
                moved = true;
                break;
            }

            if (!moved) {
                cell = current;
                weights = Barycentric(current, position);

                return true;
            }
        }

        return false;
    }

    /// <summary>Which side of a cell's face a position is on, as the face's own winding sees it.</summary>
    int OrientToFace(int cell, int face, Vector3 position) {
        var slot = face * 3;

        return ExactPredicates.Orient3D(
            vertices[cellVertices[(cell * 4) + FaceVertices[slot]]],
            vertices[cellVertices[(cell * 4) + FaceVertices[slot + 1]]],
            vertices[cellVertices[(cell * 4) + FaceVertices[slot + 2]]],
            position
        );
    }

    /// <summary>The barycentric weights of a position inside a cell.</summary>
    /// <remarks>
    ///     Four signed volumes over the whole, which is what barycentric coordinates are. Computed
    ///     in <see langword="double" /> and not exactly: a weight is an interpolation coefficient
    ///     rather than a decision, so a rounding error in it moves the answer by a rounding error.
    ///     The <em>cell</em> was chosen exactly, which is the part that had a cliff in it.
    /// </remarks>
    Vector4 Barycentric(int cell, Vector3 position) {
        var a = vertices[cellVertices[cell * 4]];
        var b = vertices[cellVertices[(cell * 4) + 1]];
        var c = vertices[cellVertices[(cell * 4) + 2]];
        var d = vertices[cellVertices[(cell * 4) + 3]];

        var whole = SignedVolume(a, b, c, d);
        if (whole == 0d) {
            return new(0.25f, 0.25f, 0.25f, 0.25f);
        }

        var wa = SignedVolume(position, b, c, d) / whole;
        var wb = SignedVolume(a, position, c, d) / whole;
        var wc = SignedVolume(a, b, position, d) / whole;

        // The fourth from the other three rather than from its own determinant, so that the four
        // sum to exactly one and a shading term built from them cannot gain or lose energy.
        return new((float)wa, (float)wb, (float)wc, (float)(1d - wa - wb - wc));
    }

    static double SignedVolume(Vector3 a, Vector3 b, Vector3 c, Vector3 d) {
        double adx = a.X - (double)d.X, ady = a.Y - (double)d.Y, adz = a.Z - (double)d.Z;
        double bdx = b.X - (double)d.X, bdy = b.Y - (double)d.Y, bdz = b.Z - (double)d.Z;
        double cdx = c.X - (double)d.X, cdy = c.Y - (double)d.Y, cdz = c.Z - (double)d.Z;

        return (adx * ((bdy * cdz) - (bdz * cdy)))
            + (bdx * ((cdy * adz) - (cdz * ady)))
            + (cdx * ((ady * bdz) - (adz * bdy)));
    }

    static Vector3[] Deduplicate(ReadOnlySpan<Vector3> positions) {
        var seen = new Dictionary<Vector3, int>(positions.Length);
        var distinct = new List<Vector3>(positions.Length);

        foreach (var position in positions) {
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) {
                throw new ArgumentException($"Position {position} is not finite.", nameof(positions));
            }

            if (seen.TryAdd(position, distinct.Count)) {
                distinct.Add(position);
            }
        }

        return [.. distinct];
    }

    /// <summary>One incremental construction, over one enclosure size.</summary>
    /// <remarks>
    ///     A class rather than a method because the cell arrays, the free list and the scratch
    ///     buffers are shared by five steps that all mutate them, and threading nine
    ///     <c>ref</c> parameters through would hide which of them is the state.
    /// </remarks>
    sealed class Incremental {
        readonly List<int> boundary = [];
        readonly Stack<int> cavity = new();
        readonly List<int> cavityCells = [];
        readonly Dictionary<long, int> edges = [];
        readonly Stack<int> freed = new();
        readonly int inputCount;
        readonly Vector3[] points;

        bool[] alive;
        int cellCount;
        int lastCell;
        int[] neighbours;
        int[] vertices;

        public Incremental(Vector3[] input, float enclosure) {
            inputCount = input.Length;
            points = new Vector3[input.Length + 4];
            input.CopyTo(points, 0);
            Enclose(input, enclosure, points.AsSpan(input.Length));

            // A Delaunay tetrahedralisation of n points has O(n²) cells in the worst case and
            // about 7n in every case anybody meets. Start near the second and double from there.
            var capacity = (input.Length * 8) + 64;
            vertices = new int[capacity * 4];
            neighbours = new int[capacity * 4];
            alive = new bool[capacity];
        }

        /// <summary>Runs the construction, or returns null if the enclosure was not big enough.</summary>
        public DelaunayTetrahedralization? Run() {
            AddCell(inputCount, inputCount + 1, inputCount + 2, inputCount + 3);
            neighbours.AsSpan(0, 4).Fill(-1);

            for (var point = 0; point < inputCount; point++) {
                Insert(point);
            }

            return Extract();
        }

        /// <summary>Four corners far enough out that every input point is well inside them.</summary>
        /// <remarks>
        ///     <para>
        ///         A regular tetrahedron on the alternate corners of a cube, which is positively
        ///         oriented in that order and needs no square roots to write down. Its inradius is
        ///         the half-extent over √3, so a factor of one is already a containment; the factor
        ///         actually used is six orders of magnitude more than that, because the corners
        ///         only have to be outside the circumsphere of every cell the real points form,
        ///         and a nearly flat cell's circumsphere is very much larger than the points that
        ///         made it.
        ///     </para>
        ///     <para>
        ///         Being far away costs nothing here, which is the point of exact predicates: the
        ///         reason the textbook warns against an oversized enclosure is that it destroys a
        ///         floating-point in-sphere test, and there is no floating-point in-sphere test.
        ///     </para>
        /// </remarks>
        static void Enclose(ReadOnlySpan<Vector3> input, float scale, Span<Vector3> corners) {
            var minimum = input[0];
            var maximum = input[0];

            foreach (var point in input) {
                minimum = Vector3.Min(minimum, point);
                maximum = Vector3.Max(maximum, point);
            }

            var centre = (minimum + maximum) * 0.5f;
            var extent = Vector3.Distance(minimum, maximum) * 0.5f;
            var reach = MathF.Max(extent, 1e-6f) * scale;

            corners[0] = centre + (new Vector3(1f, 1f, 1f) * reach);
            corners[1] = centre + (new Vector3(1f, -1f, -1f) * reach);
            corners[2] = centre + (new Vector3(-1f, 1f, -1f) * reach);
            corners[3] = centre + (new Vector3(-1f, -1f, 1f) * reach);
        }

        void Insert(int point) {
            var position = points[point];
            var start = Locate(position);

            cavity.Clear();
            cavityCells.Clear();
            boundary.Clear();
            edges.Clear();

            cavity.Push(start);
            cavityCells.Add(start);
            alive[start] = false;

            while (cavity.Count > 0) {
                var cell = cavity.Pop();

                for (var face = 0; face < 4; face++) {
                    var neighbour = neighbours[(cell * 4) + face];

                    if (neighbour >= 0 && !alive[neighbour]) {
                        continue;
                    }

                    if (neighbour >= 0 && InCircumsphere(neighbour, point)) {
                        alive[neighbour] = false;
                        cavityCells.Add(neighbour);
                        cavity.Push(neighbour);

                        continue;
                    }

                    // The face is on the cavity's boundary. Record what it is made of now, while
                    // the cell that holds it still exists: the cells are about to be recycled.
                    var slot = face * 3;
                    boundary.Add(vertices[(cell * 4) + FaceVertices[slot]]);
                    boundary.Add(vertices[(cell * 4) + FaceVertices[slot + 1]]);
                    boundary.Add(vertices[(cell * 4) + FaceVertices[slot + 2]]);
                    boundary.Add(neighbour);
                    boundary.Add(neighbour < 0 ? -1 : FaceTowards(neighbour, cell));
                }
            }

            foreach (var cell in cavityCells) {
                Free(cell);
            }

            for (var face = 0; face < boundary.Count; face += 5) {
                var v0 = boundary[face];
                var v1 = boundary[face + 1];
                var v2 = boundary[face + 2];
                var outside = boundary[face + 3];
                var outsideFace = boundary[face + 4];

                // (v0, v1, v2) is wound so that the cavity's interior — and so the new point,
                // which the cavity is star-shaped around — is on its positive side. The new cell
                // is therefore positively oriented without anything having to check.
                var cell = AddCell(v0, v1, v2, point);

                neighbours[(cell * 4) + 3] = outside;

                if (outside >= 0) {
                    neighbours[(outside * 4) + outsideFace] = cell;
                }

                // The three remaining faces each contain the new point and one edge of the
                // boundary face, and every boundary edge is shared by exactly two boundary faces.
                // Matching on the edge is therefore the whole of the adjacency repair.
                LinkAcrossEdge(v1, v2, cell, 0);
                LinkAcrossEdge(v0, v2, cell, 1);
                LinkAcrossEdge(v0, v1, cell, 2);
            }
        }

        void LinkAcrossEdge(int from, int to, int cell, int face) {
            var key = from < to ? ((long)from << 32) | (uint)to : ((long)to << 32) | (uint)from;

            if (edges.Remove(key, out var other)) {
                neighbours[(cell * 4) + face] = other >> 2;
                neighbours[other] = cell;
            } else {
                edges[key] = (cell * 4) + face;
                neighbours[(cell * 4) + face] = -1;
            }
        }

        /// <summary>Which of <paramref name="cell" />'s faces looks at <paramref name="towards" />.</summary>
        int FaceTowards(int cell, int towards) {
            for (var face = 0; face < 4; face++) {
                if (neighbours[(cell * 4) + face] == towards) {
                    return face;
                }
            }

            return -1;
        }

        bool InCircumsphere(int cell, int point) =>
            ExactPredicates.InSphere(
                points[vertices[cell * 4]],
                points[vertices[(cell * 4) + 1]],
                points[vertices[(cell * 4) + 2]],
                points[vertices[(cell * 4) + 3]],
                points[point],
                vertices[cell * 4],
                vertices[(cell * 4) + 1],
                vertices[(cell * 4) + 2],
                vertices[(cell * 4) + 3],
                point
            ) > 0;

        /// <summary>The cell containing a position, found by walking towards it.</summary>
        /// <remarks>
        ///     The walk never leaves the mesh, because every point being inserted is strictly
        ///     inside the enclosing tetrahedron and the enclosure's own four faces therefore never
        ///     fail. The step limit and the scan behind it are not expected to run: a visibility
        ///     walk in a Delaunay triangulation terminates, and with exact orientations the
        ///     triangulation really is Delaunay. They are there because "not expected to" is not
        ///     the same as "cannot", and an infinite loop inside a content build is the worst way
        ///     to find out which one it was.
        /// </remarks>
        int Locate(Vector3 position) {
            var current = alive[lastCell] ? lastCell : FirstAlive();
            var previous = -1;

            for (var step = 0; step <= cellCount; step++) {
                var moved = false;

                for (var face = 0; face < 4; face++) {
                    var neighbour = neighbours[(current * 4) + face];

                    if (neighbour < 0 || neighbour == previous) {
                        continue;
                    }

                    if (Orient(current, face, position) < 0) {
                        previous = current;
                        current = neighbour;
                        moved = true;

                        break;
                    }
                }

                if (!moved) {
                    if (Contains(current, position)) {
                        return current;
                    }

                    break;
                }
            }

            for (var cell = 0; cell < cellCount; cell++) {
                if (alive[cell] && Contains(cell, position)) {
                    return cell;
                }
            }

            throw new InvalidOperationException(
                $"No cell contains {position}, which cannot happen inside the enclosing tetrahedron."
            );
        }

        int FirstAlive() {
            for (var cell = 0; cell < cellCount; cell++) {
                if (alive[cell]) {
                    return cell;
                }
            }

            return 0;
        }

        bool Contains(int cell, Vector3 position) {
            for (var face = 0; face < 4; face++) {
                if (Orient(cell, face, position) < 0) {
                    return false;
                }
            }

            return true;
        }

        int Orient(int cell, int face, Vector3 position) {
            var slot = face * 3;

            return ExactPredicates.Orient3D(
                points[vertices[(cell * 4) + FaceVertices[slot]]],
                points[vertices[(cell * 4) + FaceVertices[slot + 1]]],
                points[vertices[(cell * 4) + FaceVertices[slot + 2]]],
                position
            );
        }

        int AddCell(int v0, int v1, int v2, int v3) {
            var cell = Allocate();

            vertices[cell * 4] = v0;
            vertices[(cell * 4) + 1] = v1;
            vertices[(cell * 4) + 2] = v2;
            vertices[(cell * 4) + 3] = v3;
            lastCell = cell;

            return cell;
        }

        /// <summary>A recycled cell if one is going spare, and a new one otherwise.</summary>
        /// <remarks>
        ///     Recycling matters more than it looks: every insertion frees the cavity and
        ///     immediately allocates one cell per boundary face, so without a free list the arrays
        ///     would grow by the whole cavity on every point and a build over a few thousand
        ///     probes would spend its time in memory rather than in geometry.
        /// </remarks>
        int Allocate() {
            if (freed.TryPop(out var recycled)) {
                alive[recycled] = true;

                return recycled;
            }

            if (cellCount == alive.Length) {
                Grow();
            }

            alive[cellCount] = true;

            return cellCount++;
        }

        void Grow() {
            Array.Resize(ref vertices, vertices.Length * 2);
            Array.Resize(ref neighbours, neighbours.Length * 2);
            Array.Resize(ref alive, alive.Length * 2);
        }

        void Free(int cell) {
            alive[cell] = false;
            freed.Push(cell);
        }

        /// <summary>Drops the enclosure, renumbers what is left, and checks that it is complete.</summary>
        DelaunayTetrahedralization? Extract() {
            var remap = new int[cellCount];
            var kept = 0;

            for (var cell = 0; cell < cellCount; cell++) {
                remap[cell] = -1;

                if (!alive[cell]) {
                    continue;
                }

                if (vertices[cell * 4] >= inputCount
                    || vertices[(cell * 4) + 1] >= inputCount
                    || vertices[(cell * 4) + 2] >= inputCount
                    || vertices[(cell * 4) + 3] >= inputCount) {
                    continue;
                }

                remap[cell] = kept++;
            }

            if (kept == 0) {
                return null;
            }

            var keptVertices = new int[kept * 4];
            var keptNeighbours = new int[kept * 4];

            for (var cell = 0; cell < cellCount; cell++) {
                if (remap[cell] < 0) {
                    continue;
                }

                for (var slot = 0; slot < 4; slot++) {
                    keptVertices[(remap[cell] * 4) + slot] = vertices[(cell * 4) + slot];

                    var neighbour = neighbours[(cell * 4) + slot];
                    keptNeighbours[(remap[cell] * 4) + slot] = neighbour < 0 ? -1 : remap[neighbour];
                }
            }

            var result = new DelaunayTetrahedralization(
                points.AsSpan(0, inputCount).ToArray(),
                keptVertices,
                keptNeighbours,
                true
            );

            return IsComplete(result) ? result : null;
        }

        /// <summary>Whether the cells use every point and their boundary is the convex hull.</summary>
        /// <remarks>
        ///     <para>
        ///         Losing cells near the hull is the one failure an enclosure that is not big
        ///         enough produces, and it is invisible from the inside: everything that is present
        ///         is still Delaunay. What it does leave is a dent — a boundary that is not convex.
        ///     </para>
        ///     <para>
        ///         So the check is local rather than global. A closed polyhedral surface that turns
        ///         the same way at every edge is convex, which is a theorem and also six lines: pair
        ///         the boundary faces across their shared edges, and ask of each pair that neither
        ///         face has the other's far corner outside it. That is <c>O(faces)</c>, where asking
        ///         every point about every face would be <c>O(faces × points)</c> — on a grid, where
        ///         both the faces and the points are in the thousands and every one of those tests
        ///         lands on the exact path because the answers keep being zero, the difference is
        ///         the difference between a bake and a hang.
        ///     </para>
        ///     <para>
        ///         An unpaired edge means the boundary is not closed, and a second boundary
        ///         component — a void inside the mesh — turns the wrong way at every one of its
        ///         edges, since its faces look into the void. Both fail here without needing a case
        ///         of their own.
        ///     </para>
        /// </remarks>
        static bool IsComplete(DelaunayTetrahedralization mesh) {
            var used = new bool[mesh.vertices.Length];

            foreach (var vertex in mesh.cellVertices) {
                used[vertex] = true;
            }

            foreach (var isUsed in used) {
                if (!isUsed) {
                    return false;
                }
            }

            var faces = new List<int>();

            for (var cell = 0; cell < mesh.CellCount; cell++) {
                // The invariant the in-sphere test was called under all the way through
                // construction, checked once at the end where it is cheap. A cell that came out
                // flat or inside-out means the cavity was not star-shaped around the point it was
                // dug for, and everything built on it afterwards asked the in-sphere test a
                // question with the sign reversed.
                if (ExactPredicates.Orient3D(
                        mesh.vertices[mesh.cellVertices[cell * 4]],
                        mesh.vertices[mesh.cellVertices[(cell * 4) + 1]],
                        mesh.vertices[mesh.cellVertices[(cell * 4) + 2]],
                        mesh.vertices[mesh.cellVertices[(cell * 4) + 3]]
                    )
                    != 1) {
                    return false;
                }

                for (var face = 0; face < 4; face++) {
                    if (mesh.cellNeighbours[(cell * 4) + face] >= 0) {
                        continue;
                    }

                    var slot = face * 3;
                    faces.Add(mesh.cellVertices[(cell * 4) + FaceVertices[slot]]);
                    faces.Add(mesh.cellVertices[(cell * 4) + FaceVertices[slot + 1]]);
                    faces.Add(mesh.cellVertices[(cell * 4) + FaceVertices[slot + 2]]);
                }
            }

            // Each entry is a face still looking for the one across an edge, together with which
            // of its own corners is not on that edge — the corner the two of them have to agree
            // about.
            var pending = new Dictionary<long, int>();

            for (var face = 0; face * 3 < faces.Count; face++) {
                for (var corner = 0; corner < 3; corner++) {
                    var from = faces[(face * 3) + ((corner + 1) % 3)];
                    var to = faces[(face * 3) + ((corner + 2) % 3)];
                    var key = from < to ? ((long)from << 32) | (uint)to : ((long)to << 32) | (uint)from;

                    if (!pending.Remove(key, out var packed)) {
                        pending[key] = (face << 2) | corner;

                        continue;
                    }

                    if (!KeepsCornerInside(mesh, faces, face, packed >> 2, packed & 3)
                        || !KeepsCornerInside(mesh, faces, packed >> 2, face, corner)) {
                        return false;
                    }
                }
            }

            return pending.Count == 0;
        }

        /// <summary>Whether one boundary face's far corner stays on the inner side of another's plane.</summary>
        static bool KeepsCornerInside(
            DelaunayTetrahedralization mesh,
            List<int> faces,
            int face,
            int other,
            int corner
        ) =>
            ExactPredicates.Orient3D(
                mesh.vertices[faces[face * 3]],
                mesh.vertices[faces[(face * 3) + 1]],
                mesh.vertices[faces[(face * 3) + 2]],
                mesh.vertices[faces[(other * 3) + corner]]
            ) >= 0;
    }
}
