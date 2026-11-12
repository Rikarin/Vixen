// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-FileCopyrightText: Copyright (C) 2011 by Morten S. Mikkelsen
// SPDX-License-Identifier: Apache-2.0 AND Zlib
//
// A managed port of mikktspace.c, and so an altered version of it, which is plainly marked here as the
// licence below asks: only triangles are taken, memory is managed, and the code is arranged for C#.
// The original's notice stays with it, unaltered:
//
//   Copyright (C) 2011 by Morten S. Mikkelsen
//
//   This software is provided 'as-is', without any express or implied
//   warranty.  In no event will the authors be held liable for any damages
//   arising from the use of this software.
//
//   Permission is granted to anyone to use this software for any purpose,
//   including commercial applications, and to alter it and redistribute it
//   freely, subject to the following restrictions:
//
//   1. The origin of this software must not be misrepresented; you must not
//      claim that you wrote the original software. If you use this software
//      in a product, an acknowledgment in the product documentation would be
//      appreciated but is not required.
//   2. Altered source versions must be plainly marked as such, and must not be
//      misrepresented as being the original software.
//   3. This notice may not be removed or altered from any source distribution.

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Geometry;

/// <summary>The tangent basis most game engines and bakers agree on, from Morten Mikkelsen's thesis.</summary>
/// <remarks>
///     <para>
///         <b>Why a bake needs this one and not any tangent.</b> A tangent-space normal map only means
///         something against the basis it was baked in, and a renderer rebuilds that basis from the mesh
///         alone. A per-triangle basis is right on a flat face and subtly wrong wherever a smooth low
///         mesh curves, which reads as shading seams along triangle edges. This algorithm is the
///         published answer: it depends on no face order, no vertex order and no degenerate triangle, so
///         a baker and an engine that both run it get the same frame from the same mesh.
///     </para>
///     <para>
///         ⚠ <b>A corner's answer depends on which corners it shares a tangent with</b>, so it is one
///         value per triangle corner and never per indexed vertex. Averaging back onto an existing index
///         list is what the original's header forbids in capitals. A hard edge needs nothing special:
///         two normals on one point are two welded vertices, so the two sides never share a group.
///     </para>
///     <para>
///         ⚠ <b>Texture coordinates are taken in this engine's convention and turned round here.</b>
///         <c>ModelReader</c> delivers <c>v</c> running down the image, and the algorithm has only ever
///         been run elsewhere with <c>v</c> running up. Feeding it <c>1 − v</c> is what makes a stored
///         sign mean the same as in every tool a map is exchanged with, and the bitangent point up the
///         image, which is the green channel glTF and most engines read.
///     </para>
///     <para>
///         ⚠ <b>The order-sensitive parts are ported as they are, on purpose.</b> The weld's partition
///         and the edge sort's pivot sequence look like details, but its passes leave the last
///         run of edges unsorted, so on the odd mesh which triangles count as neighbours depends on them.
///         Tidying them gives a basis that is almost always the same, which is the worst kind of
///         different. <c>MikkTSpaceTests</c> holds this against values from the original compiled.
///     </para>
///     <para>
///         <b>What differs from the original.</b> Quads are not taken: the reader and the baker both
///         triangulate first, which the original's header names as the way to keep a quad's diagonal out
///         of the question, and nothing here has a quad to give it. The fallbacks for a failed allocation
///         are gone, a sort of distinct integers is <see cref="Array.Sort(Array)" />, and the recursive
///         group walk is a loop in the same order, because a pole with thousands of triangles would
///         otherwise be a stack overflow on a baking thread.
///     </para>
/// </remarks>
public static class MikkTSpace {
    const int Cells = 2048;
    const uint SortSeed = 39871946;
    const float SmallestNormal = 1.17549435e-38f;

    /// <summary>The cosine of the original's default threshold of 180 degrees, which splits no group but a reversal.</summary>
    const float ThresholdCosine = -1f;

    const int Degenerate = 1;
    const int GroupWithAny = 4;
    const int OrientPreserving = 8;
    const int Unassigned = -1;

    /// <summary>Makes one tangent per triangle corner.</summary>
    /// <param name="positions">Three corners per triangle, in order.</param>
    /// <param name="normals">One unit normal per corner.</param>
    /// <param name="texCoords">One coordinate per corner, with <c>v</c> running down the image.</param>
    /// <returns>
    ///     One per corner: a unit tangent, and in <c>W</c> the sign for
    ///     <c>bitangent = sign × cross(normal, tangent)</c>. Empty for no triangles.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The arrays differ in length, or are not whole triangles.</exception>
    public static Vector4[] Generate(Vector3[] positions, Vector3[] normals, Vector2[] texCoords) {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(normals);
        ArgumentNullException.ThrowIfNull(texCoords);

        if (normals.Length != positions.Length || texCoords.Length != positions.Length) {
            throw new ArgumentException("Every corner needs a position, a normal and a coordinate.", nameof(normals));
        }

        if (positions.Length % 3 != 0) {
            throw new ArgumentException("Three corners make a triangle.", nameof(positions));
        }

        return positions.Length == 0 ? [] : new Run(positions, normals, texCoords).Solve();
    }

    static bool NotZero(float value) => MathF.Abs(value) > SmallestNormal;

    static bool NotZero(Vector3 value) => NotZero(value.X) || NotZero(value.Y) || NotZero(value.Z);

    static Vector3 Unit(Vector3 value) => NotZero(value) ? value * (1f / value.Length()) : value;

    static Vector3 Project(Vector3 value, Vector3 normal) => Unit(value - normal * Dot(normal, value));

    static float Dot(Vector3 one, Vector3 two) => one.X * two.X + one.Y * two.Y + one.Z * two.Z;

    /// <summary>What a corner ends up with.</summary>
    struct Space {
        public Vector3 S;
        public bool Orient;
    }

    /// <summary>The triangles sharing one welded vertex, with one orientation.</summary>
    struct Group {
        public int Count;
        public int Offset;
        public int Vertex;
        public bool Orient;
    }

    /// <summary>An edge, as its lower and higher welded corner and its triangle.</summary>
    struct Edge {
        public int Low;
        public int High;
        public int Face;

        public readonly int this[int channel] => channel == 0 ? Low : channel == 1 ? High : Face;
    }

    /// <summary>A corner on its way through the weld.</summary>
    struct WeldVertex {
        public Vector3 Point;
        public int Index;

        public readonly float this[int channel] => channel == 0 ? Point.X : channel == 1 ? Point.Y : Point.Z;
    }

    sealed class Run(Vector3[] positions, Vector3[] normals, Vector2[] texCoords) {
        readonly int total = positions.Length / 3;
        readonly int[] list = new int[positions.Length];
        readonly int[] original = new int[positions.Length / 3];
        readonly int[] flags = new int[positions.Length / 3];
        int good;

        Vector3[] tangentS = [];
        Vector3[] tangentT = [];
        int[] neighbours = [];
        int[] assigned = [];

        Group[] groups = [];
        int[] members = [];
        Space[] spaces = [];

        /// <summary>The original's coordinates: <c>v</c> up, and a third component that is always one.</summary>
        Vector3 Coordinate(int corner) => new(texCoords[corner].X, 1f - texCoords[corner].Y, 1f);

        public Vector4[] Solve() {
            for (var corner = 0; corner < list.Length; corner++) {
                list[corner] = corner;
            }

            for (var triangle = 0; triangle < total; triangle++) {
                original[triangle] = triangle;
            }

            Weld();

            var degenerate = 0;

            for (var triangle = 0; triangle < total; triangle++) {
                var (p0, p1, p2) = (Point(triangle, 0), Point(triangle, 1), Point(triangle, 2));

                if (p0 == p1 || p0 == p2 || p1 == p2) {
                    flags[triangle] |= Degenerate;
                    degenerate++;
                }
            }

            good = total - degenerate;
            MoveDegenerateLast();
            InitialiseTriangles();
            var count = BuildGroups();

            spaces = new Space[total * 3];
            Array.Fill(spaces, new() { S = Vector3.UnitX });

            MakeSpaces(count);
            CopyToDegenerate();

            var tangents = new Vector4[total * 3];

            for (var corner = 0; corner < tangents.Length; corner++) {
                var space = spaces[corner];

                tangents[corner] = new(space.S.X, space.S.Y, space.S.Z, space.Orient ? 1f : -1f);
            }

            return tangents;
        }

        Vector3 Point(int triangle, int corner) => positions[list[triangle * 3 + corner]];

        static int GridCell(float minimum, float maximum, float value) {
            var cell = (int)(Cells * ((value - minimum) / (maximum - minimum)));

            return cell < Cells ? cell >= 0 ? cell : 0 : Cells - 1;
        }

        /// <summary>Points every corner with the same position, normal and coordinate at one of them.</summary>
        void Weld() {
            var (lowX, lowY, lowZ) = (positions[0].X, positions[0].Y, positions[0].Z);
            var (highX, highY, highZ) = (lowX, lowY, lowZ);

            for (var at = 1; at < list.Length; at++) {
                var point = positions[list[at]];

                if (lowX > point.X) {
                    lowX = point.X;
                } else if (highX < point.X) {
                    highX = point.X;
                }

                if (lowY > point.Y) {
                    lowY = point.Y;
                } else if (highY < point.Y) {
                    highY = point.Y;
                }

                if (lowZ > point.Z) {
                    lowZ = point.Z;
                } else if (highZ < point.Z) {
                    highZ = point.Z;
                }
            }

            var (sizeX, sizeY, sizeZ) = (highX - lowX, highY - lowY, highZ - lowZ);
            var axis = sizeY > sizeX && sizeY > sizeZ ? 1 : sizeZ > sizeX ? 2 : 0;
            var (minimum, maximum) = axis == 0 ? (lowX, highX) : axis == 1 ? (lowY, highY) : (lowZ, highZ);

            var counts = new int[Cells];
            var filled = new int[Cells];
            var offsets = new int[Cells];
            var table = new int[list.Length];

            float Along(Vector3 point) => axis == 0 ? point.X : axis == 1 ? point.Y : point.Z;

            foreach (var corner in list) {
                counts[GridCell(minimum, maximum, Along(positions[corner]))]++;
            }

            for (var cell = 1; cell < Cells; cell++) {
                offsets[cell] = offsets[cell - 1] + counts[cell - 1];
            }

            for (var at = 0; at < list.Length; at++) {
                var cell = GridCell(minimum, maximum, Along(positions[list[at]]));

                table[offsets[cell] + filled[cell]++] = at;
            }

            var scratch = new WeldVertex[counts.Max()];

            for (var cell = 0; cell < Cells; cell++) {
                var entries = counts[cell];

                if (entries < 2) {
                    continue;
                }

                for (var entry = 0; entry < entries; entry++) {
                    var at = table[offsets[cell] + entry];

                    scratch[entry] = new() { Point = positions[list[at]], Index = at };
                }

                Merge(scratch, 0, entries - 1);
            }
        }

        void Merge(WeldVertex[] vertices, int left, int right) {
            var (low, high) = (vertices[left].Point, vertices[left].Point);

            for (var at = left + 1; at <= right; at++) {
                low = Lesser(low, vertices[at].Point);
                high = Greater(high, vertices[at].Point);
            }

            var size = high - low;
            var axis = size.Y > size.X && size.Y > size.Z ? 1 : size.Z > size.X ? 2 : 0;
            var (from, to) = axis == 0 ? (low.X, high.X) : axis == 1 ? (low.Y, high.Y) : (low.Z, high.Z);
            var separator = 0.5f * (to + from);

            if (!float.IsFinite(separator)) {
                return;
            }

            if (separator >= to || separator <= from) {
                CompleteWeld(vertices, left, right);

                return;
            }

            var (near, far) = (left, right);

            while (near < far) {
                var (nearReady, farReady) = (false, false);

                while (!nearReady && near < far) {
                    nearReady = !(vertices[near][axis] < separator);

                    if (!nearReady) {
                        near++;
                    }
                }

                while (!farReady && near < far) {
                    farReady = vertices[far][axis] < separator;

                    if (!farReady) {
                        far--;
                    }
                }

                if (nearReady && farReady) {
                    (vertices[near], vertices[far]) = (vertices[far], vertices[near]);
                    near++;
                    far--;
                }
            }

            if (near == far) {
                if (vertices[far][axis] < separator) {
                    near++;
                } else {
                    far--;
                }
            }

            if (left < far) {
                Merge(vertices, left, far);
            }

            if (near < right) {
                Merge(vertices, near, right);
            }
        }

        static Vector3 Lesser(Vector3 one, Vector3 two) =>
            new(one.X > two.X ? two.X : one.X, one.Y > two.Y ? two.Y : one.Y, one.Z > two.Z ? two.Z : one.Z);

        static Vector3 Greater(Vector3 one, Vector3 two) =>
            new(one.X < two.X ? two.X : one.X, one.Y < two.Y ? two.Y : one.Y, one.Z < two.Z ? two.Z : one.Z);

        void CompleteWeld(WeldVertex[] vertices, int left, int right) {
            for (var at = left; at <= right; at++) {
                var slot = vertices[at].Index;
                var corner = list[slot];

                for (var earlier = left; earlier < at; earlier++) {
                    var other = list[vertices[earlier].Index];

                    if (positions[corner] == positions[other]
                        && normals[corner] == normals[other]
                        && Coordinate(corner) == Coordinate(other)) {
                        list[slot] = other;

                        break;
                    }
                }
            }
        }

        /// <summary>Moves degenerate triangles behind the good ones, leaving the good ones in order.</summary>
        void MoveDegenerateLast() {
            var search = 1;
            var at = 0;
            var finding = true;

            while (at < good && finding) {
                if ((flags[at] & Degenerate) == 0) {
                    if (search < at + 2) {
                        search = at + 2;
                    }
                } else {
                    var justDegenerate = true;

                    while (justDegenerate && search < total) {
                        if ((flags[search] & Degenerate) == 0) {
                            justDegenerate = false;
                        } else {
                            search++;
                        }
                    }

                    var other = search;

                    search++;

                    if (justDegenerate) {
                        finding = false;
                    } else {
                        for (var corner = 0; corner < 3; corner++) {
                            (list[at * 3 + corner], list[other * 3 + corner]) =
                                (list[other * 3 + corner], list[at * 3 + corner]);
                        }

                        (flags[at], flags[other]) = (flags[other], flags[at]);
                        (original[at], original[other]) = (original[other], original[at]);
                    }
                }

                if (finding) {
                    at++;
                }
            }
        }

        void InitialiseTriangles() {
            tangentS = new Vector3[good];
            tangentT = new Vector3[good];
            neighbours = new int[good * 3];
            assigned = new int[good * 3];
            Array.Fill(neighbours, -1);
            Array.Fill(assigned, Unassigned);

            for (var triangle = 0; triangle < good; triangle++) {
                flags[triangle] |= GroupWithAny;

                var (c0, c1, c2) = (list[triangle * 3], list[triangle * 3 + 1], list[triangle * 3 + 2]);
                var (t0, t1, t2) = (Coordinate(c0), Coordinate(c1), Coordinate(c2));
                var (t21x, t21y) = (t1.X - t0.X, t1.Y - t0.Y);
                var (t31x, t31y) = (t2.X - t0.X, t2.Y - t0.Y);
                var d1 = positions[c1] - positions[c0];
                var d2 = positions[c2] - positions[c0];
                var area = t21x * t31y - t21y * t31x;
                var s = d1 * t31y - d2 * t21y;
                var t = d1 * -t31x + d2 * t21x;

                flags[triangle] |= area > 0f ? OrientPreserving : 0;

                if (!NotZero(area)) {
                    continue;
                }

                var absolute = MathF.Abs(area);
                var (lengthS, lengthT) = (s.Length(), t.Length());
                var sign = (flags[triangle] & OrientPreserving) == 0 ? -1f : 1f;

                if (NotZero(lengthS)) {
                    tangentS[triangle] = s * (sign / lengthS);
                }

                if (NotZero(lengthT)) {
                    tangentT[triangle] = t * (sign / lengthT);
                }

                if (NotZero(lengthS / absolute) && NotZero(lengthT / absolute)) {
                    flags[triangle] &= ~GroupWithAny;
                }
            }

            BuildNeighbours();
        }

        void BuildNeighbours() {
            var edges = new Edge[good * 3];

            for (var face = 0; face < good; face++) {
                for (var corner = 0; corner < 3; corner++) {
                    var one = list[face * 3 + corner];
                    var two = list[face * 3 + (corner < 2 ? corner + 1 : 0)];

                    edges[face * 3 + corner] = new() {
                        Low = one < two ? one : two, High = !(one < two) ? one : two, Face = face
                    };
                }
            }

            SortEdges(edges, 0, edges.Length - 1, 0, SortSeed);

            // ⚠ As in the original, neither of the next two passes sorts the run that ends the array.
            var start = 0;

            for (var at = 1; at < edges.Length; at++) {
                if (edges[start].Low != edges[at].Low) {
                    SortEdges(edges, start, at - 1, 1, SortSeed);
                    start = at;
                }
            }

            start = 0;

            for (var at = 1; at < edges.Length; at++) {
                if (edges[start].Low != edges[at].Low || edges[start].High != edges[at].High) {
                    SortEdges(edges, start, at - 1, 2, SortSeed);
                    start = at;
                }
            }

            for (var at = 0; at < edges.Length; at++) {
                PairUp(edges, at);
            }
        }

        void PairUp(Edge[] edges, int at) {
            var (low, high, face) = (edges[at].Low, edges[at].High, edges[at].Face);
            var (fromA, toA, numberA) = EdgeOf(face, low, high);

            if (neighbours[face * 3 + numberA] != -1) {
                return;
            }

            for (var other = at + 1;
                 other < edges.Length && edges[other].Low == low && edges[other].High == high;
                 other++) {
                var next = edges[other].Face;
                var (toB, fromB, numberB) = EdgeOf(next, low, high);

                if (fromA == fromB && toA == toB && neighbours[next * 3 + numberB] == -1) {
                    neighbours[face * 3 + numberA] = next;
                    neighbours[next * 3 + numberB] = face;

                    return;
                }
            }
        }

        /// <summary>A triangle's edge between two welded corners, in the triangle's own direction.</summary>
        (int From, int To, int Number) EdgeOf(int face, int low, int high) {
            var (c0, c1, c2) = (list[face * 3], list[face * 3 + 1], list[face * 3 + 2]);

            if (c0 == low || c0 == high) {
                return c1 == low || c1 == high ? (c0, c1, 0) : (c2, c0, 2);
            }

            return (c1, c2, 1);
        }

        /// <summary>The original's quicksort, whose pivots decide the order of equal keys.</summary>
        static void SortEdges(Edge[] edges, int left, int right, int channel, uint seed) {
            var elements = right - left + 1;

            if (elements < 2) {
                return;
            }

            if (elements == 2) {
                if (edges[left][channel] > edges[right][channel]) {
                    (edges[left], edges[right]) = (edges[right], edges[left]);
                }

                return;
            }

            var turn = seed & 31;

            turn = (seed << (int)turn) | (seed >> (int)(32 - turn));
            seed = seed + turn + 3;

            var (near, far) = (left, right);
            var pivot = edges[(int)(seed % (uint)elements) + near][channel];

            do {
                while (edges[near][channel] < pivot) {
                    near++;
                }

                while (edges[far][channel] > pivot) {
                    far--;
                }

                if (near <= far) {
                    (edges[near], edges[far]) = (edges[far], edges[near]);
                    near++;
                    far--;
                }
            } while (near <= far);

            if (left < far) {
                SortEdges(edges, left, far, channel, seed);
            }

            if (near < right) {
                SortEdges(edges, near, right, channel, seed);
            }
        }

        int BuildGroups() {
            groups = new Group[good * 3];
            members = new int[good * 3];

            var count = 0;
            var offset = 0;

            for (var face = 0; face < good; face++) {
                for (var corner = 0; corner < 3; corner++) {
                    if ((flags[face] & GroupWithAny) != 0 || assigned[face * 3 + corner] != Unassigned) {
                        continue;
                    }

                    var group = count++;

                    assigned[face * 3 + corner] = group;
                    groups[group] = new() {
                        Count = 1,
                        Offset = offset,
                        Vertex = list[face * 3 + corner],
                        Orient = (flags[face] & OrientPreserving) != 0
                    };
                    members[offset] = face;

                    Spread(group, neighbours[face * 3 + corner], neighbours[face * 3 + (corner > 0 ? corner - 1 : 2)]);
                    offset += groups[group].Count;
                }
            }

            return count;
        }

        /// <summary>The original's recursion as a walk in the same order, left before right.</summary>
        void Spread(int group, int left, int right) {
            Stack<int> pending = [];

            pending.Push(right);
            pending.Push(left);

            while (pending.TryPop(out var face)) {
                if (face >= 0 && Join(group, face, out var corner)) {
                    pending.Push(neighbours[face * 3 + (corner > 0 ? corner - 1 : 2)]);
                    pending.Push(neighbours[face * 3 + corner]);
                }
            }
        }

        bool Join(int group, int face, out int corner) {
            var vertex = groups[group].Vertex;
            var orient = groups[group].Orient;

            corner = list[face * 3] == vertex ? 0 : list[face * 3 + 1] == vertex ? 1 : 2;

            if (assigned[face * 3 + corner] != Unassigned) {
                return false;
            }

            // The only dependence on order in the algorithm, as the original says: a triangle with no
            // tangent of its own takes the orientation of the first group to reach it.
            if ((flags[face] & GroupWithAny) != 0
                && assigned[face * 3] == Unassigned
                && assigned[face * 3 + 1] == Unassigned
                && assigned[face * 3 + 2] == Unassigned) {
                flags[face] &= ~OrientPreserving;
                flags[face] |= orient ? OrientPreserving : 0;
            }

            if ((flags[face] & OrientPreserving) != 0 != orient) {
                return false;
            }

            members[groups[group].Offset + groups[group].Count] = face;
            groups[group].Count++;
            assigned[face * 3 + corner] = group;

            return true;
        }

        void MakeSpaces(int count) {
            List<int[]> subgroups = [];
            List<Vector3> found = [];
            List<int> joined = [];

            for (var group = 0; group < count; group++) {
                subgroups.Clear();
                found.Clear();

                var (offset, size) = (groups[group].Offset, groups[group].Count);

                for (var member = 0; member < size; member++) {
                    var face = members[offset + member];
                    var corner = assigned[face * 3] == group ? 0 : assigned[face * 3 + 1] == group ? 1 : 2;
                    var normal = normals[list[face * 3 + corner]];
                    var s = Project(tangentS[face], normal);
                    var t = Project(tangentT[face], normal);

                    joined.Clear();

                    for (var other = 0; other < size; other++) {
                        var candidate = members[offset + other];
                        var any = ((flags[face] | flags[candidate]) & GroupWithAny) != 0;

                        if (any
                            || face == candidate
                            || (Dot(s, Project(tangentS[candidate], normal)) > ThresholdCosine
                                && Dot(t, Project(tangentT[candidate], normal)) > ThresholdCosine)) {
                            joined.Add(candidate);
                        }
                    }

                    joined.Sort();

                    var index = Find(subgroups, joined);

                    if (index < 0) {
                        index = subgroups.Count;
                        subgroups.Add([..joined]);
                        found.Add(Evaluate(joined, groups[group].Vertex));
                    }

                    spaces[original[face] * 3 + corner] = new() { S = found[index], Orient = groups[group].Orient };
                }
            }
        }

        static int Find(List<int[]> subgroups, List<int> joined) {
            var wanted = CollectionsMarshal.AsSpan(joined);

            for (var index = 0; index < subgroups.Count; index++) {
                if (subgroups[index].AsSpan().SequenceEqual(wanted)) {
                    return index;
                }
            }

            return -1;
        }

        /// <summary>The tangent of a subgroup: its triangles' tangents, each weighted by its angle at the vertex.</summary>
        Vector3 Evaluate(List<int> faces, int vertex) {
            var sum = Vector3.Zero;

            foreach (var face in faces) {
                if ((flags[face] & GroupWithAny) != 0) {
                    continue;
                }

                var corner = list[face * 3] == vertex ? 0 : list[face * 3 + 1] == vertex ? 1 : 2;
                var index = list[face * 3 + corner];
                var normal = normals[index];
                var next = list[face * 3 + (corner < 2 ? corner + 1 : 0)];
                var previous = list[face * 3 + (corner > 0 ? corner - 1 : 2)];
                var one = Project(positions[previous] - positions[index], normal);
                var two = Project(positions[next] - positions[index], normal);
                var angle = (float)Math.Acos(Math.Clamp(Dot(one, two), -1f, 1f));

                sum += Project(tangentS[face], normal) * angle;
            }

            return Unit(sum);
        }

        /// <summary>Gives each corner of a degenerate triangle the space of a good corner welded to it.</summary>
        void CopyToDegenerate() {
            if (good == total) {
                return;
            }

            Dictionary<int, int> first = [];

            for (var at = 0; at < good * 3; at++) {
                first.TryAdd(list[at], at);
            }

            for (var triangle = good; triangle < total; triangle++) {
                for (var corner = 0; corner < 3; corner++) {
                    if (first.TryGetValue(list[triangle * 3 + corner], out var at)) {
                        spaces[original[triangle] * 3 + corner] = spaces[original[at / 3] * 3 + at % 3];
                    }
                }
            }
        }
    }
}
