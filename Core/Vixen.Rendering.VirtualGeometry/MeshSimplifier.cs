// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>What one group's simplification produced.</summary>
/// <param name="Corners">Three source-vertex indices per surviving triangle.</param>
/// <param name="Error">The largest deviation from the original mesh any surviving vertex now carries.</param>
/// <param name="Updates">
///     What each surviving vertex now carries, by welded index, for the caller to fold back in.
///     <b>Returned rather than written</b>: groups of one level are simplified in parallel and two of
///     them share the vertices on the boundary between them, so a simplification that wrote through
///     to the shared array would make the result depend on which thread got there first.
/// </param>
readonly record struct SimplifyResult(int[] Corners, float Error, (int Vertex, float Error)[] Updates);

/// <summary>Collapses edges by quadric error, with a set of edges it may not touch.</summary>
/// <remarks>
///     <para>
///         Garland–Heckbert, with three deviations that all come from what this is for.
///     </para>
///     <para>
///         <b>A vertex collapses onto another vertex, never onto an optimal point.</b> The textbook
///         placement solves the quadric for the position of least error, which is a slightly better
///         surface and a position that existed in no input. That would mean inventing a normal, a
///         texture coordinate and a set of skinning weights for it — and, worse, it would mean a
///         group's locked boundary being <em>nearly</em> where its children left it. Collapsing onto
///         an existing vertex makes the boundary bit-identical, which is what turns crack-freedom
///         from a tolerance into an equality a test can assert.
///     </para>
///     <para>
///         <b>Locked edges are locked, not merely expensive.</b> The usual treatment weights boundary
///         edges heavily and lets the optimiser decide; here a group's outer boundary is shared with
///         geometry somebody else is drawing at a different level of detail, so "expensive" is a
///         crack that costs a lot. Two rules keep it exact: an endpoint of a locked edge is never
///         removed, and no collapse may destroy a triangle that carries one.
///     </para>
///     <para>
///         <b>The link condition is checked rather than assumed.</b> Real meshes are not manifolds —
///         they have fins, T-junctions and triangles that share two edges — and a collapse across one
///         produces geometry that is not a surface at all. The condition is the standard one: the
///         vertices adjacent to both ends of an edge must be exactly the vertices opposite it.
///     </para>
/// </remarks>
static class MeshSimplifier {
    /// <summary>How long a vertex's list of incident triangles may get before it is swept of dead ones.</summary>
    const int CompactionThreshold = 32;

    /// <summary>Simplifies a set of triangles down towards a triangle count.</summary>
    /// <param name="positions">The mesh's positions.</param>
    /// <param name="welded">What <see cref="Topology.Weld" /> produced.</param>
    /// <param name="seam">Which welded vertices carry more than one set of attributes.</param>
    /// <param name="vertexError">
    ///     The deviation each welded vertex has already accumulated. Read here and reported back
    ///     through <see cref="SimplifyResult.Updates" />, so a vertex that survives four
    ///     simplifications reports what all four cost rather than what the last one did.
    /// </param>
    /// <param name="corners">Three source-vertex indices per triangle.</param>
    /// <param name="lockedEdges">The edges that may not move, keyed by <see cref="Topology.Edge" />.</param>
    /// <param name="targetTriangles">How many triangles to aim for.</param>
    /// <returns>The surviving triangles and what they cost.</returns>
    public static SimplifyResult Simplify(
        Vector3[] positions,
        int[] welded,
        bool[] seam,
        float[] vertexError,
        ReadOnlySpan<int> corners,
        HashSet<long> lockedEdges,
        int targetTriangles
    ) {
        var state = new State(positions, welded, seam, vertexError, corners, lockedEdges);
        state.Run(targetTriangles);

        return state.Result();
    }

    /// <summary>A symmetric 4×4 quadric: the sum of squared distances to a set of planes.</summary>
    /// <remarks>
    ///     Ten doubles rather than sixteen floats. Symmetric, so the upper triangle is the whole of
    ///     it; and double, because a quadric is a sum of products of coordinates and accumulates over
    ///     hundreds of collapses — in float, a vertex far from the origin loses the small differences
    ///     that are exactly what the error is made of.
    /// </remarks>
    struct Quadric {
        public double A00;
        public double A01;
        public double A02;
        public double A03;
        public double A11;
        public double A12;
        public double A13;
        public double A22;
        public double A23;
        public double A33;

        /// <summary>The quadric of one plane, weighted.</summary>
        /// <param name="plane">A unit normal and its distance, as <c>ax + by + cz + d</c>.</param>
        /// <param name="weight">How much it counts — triangle area, so a sliver does not outvote a face.</param>
        /// <returns>The quadric.</returns>
        public static Quadric FromPlane(Vector4 plane, double weight) =>
            new() {
                A00 = weight * plane.X * plane.X,
                A01 = weight * plane.X * plane.Y,
                A02 = weight * plane.X * plane.Z,
                A03 = weight * plane.X * plane.W,
                A11 = weight * plane.Y * plane.Y,
                A12 = weight * plane.Y * plane.Z,
                A13 = weight * plane.Y * plane.W,
                A22 = weight * plane.Z * plane.Z,
                A23 = weight * plane.Z * plane.W,
                A33 = weight * plane.W * plane.W
            };

        /// <summary>Adds another quadric into this one.</summary>
        /// <param name="other">The other.</param>
        public void Add(in Quadric other) {
            A00 += other.A00;
            A01 += other.A01;
            A02 += other.A02;
            A03 += other.A03;
            A11 += other.A11;
            A12 += other.A12;
            A13 += other.A13;
            A22 += other.A22;
            A23 += other.A23;
            A33 += other.A33;
        }

        /// <summary>The squared distance a point is from the planes this quadric was built from.</summary>
        /// <param name="point">The point.</param>
        /// <returns>The sum of weighted squared distances. Never below zero, up to rounding.</returns>
        public readonly double Evaluate(Vector3 point) {
            double x = point.X;
            double y = point.Y;
            double z = point.Z;

            return (A00 * x * x) + (2 * A01 * x * y) + (2 * A02 * x * z) + (2 * A03 * x)
                + (A11 * y * y) + (2 * A12 * y * z) + (2 * A13 * y)
                + (A22 * z * z) + (2 * A23 * z)
                + A33;
        }
    }

    /// <summary>One group's simplification, from its own local view of the mesh.</summary>
    /// <remarks>
    ///     Local indices rather than the mesh's own: a group holds a few thousand triangles out of a
    ///     mesh that may hold millions, and every per-vertex structure here — the quadrics, the
    ///     adjacency sets, the incident triangles — would otherwise be allocated at the size of the
    ///     whole mesh once per group.
    /// </remarks>
    sealed class State {
        readonly Vector3[] positions;
        readonly bool[] seam;
        readonly float[] vertexError;
        readonly HashSet<long> lockedEdges;

        readonly List<int> localToWelded = [];
        readonly Dictionary<int, int> weldedToLocal = [];

        readonly int[] triangles;
        readonly int[] sources;
        readonly int[] initial;
        readonly bool[] alive;

        readonly List<int>[] incident;
        readonly HashSet<int>[] adjacent;
        readonly Quadric[] quadrics;
        readonly bool[] locked;
        readonly bool[] removed;
        readonly int[] version;
        readonly float[] error;

        readonly PriorityQueue<Candidate, (double Cost, int From, int To)> queue = new();

        int liveTriangles;
        float worst;

        public State(
            Vector3[] positions,
            int[] welded,
            bool[] seam,
            float[] vertexError,
            ReadOnlySpan<int> corners,
            HashSet<long> lockedEdges
        ) {
            this.positions = positions;
            this.seam = seam;
            this.vertexError = vertexError;
            this.lockedEdges = lockedEdges;

            triangles = new int[corners.Length];
            sources = new int[corners.Length];
            initial = new int[corners.Length];

            for (var corner = 0; corner < corners.Length; corner++) {
                sources[corner] = corners[corner];
                triangles[corner] = Local(welded[corners[corner]]);
                initial[corner] = triangles[corner];
            }

            var count = localToWelded.Count;
            alive = new bool[corners.Length / 3];
            incident = new List<int>[count];
            adjacent = new HashSet<int>[count];
            quadrics = new Quadric[count];
            locked = new bool[count];
            removed = new bool[count];
            version = new int[count];
            error = new float[count];

            for (var vertex = 0; vertex < count; vertex++) {
                incident[vertex] = [];
                adjacent[vertex] = [];
                error[vertex] = vertexError[localToWelded[vertex]];
            }

            Build();
        }

        /// <summary>The local index of a welded vertex, assigning one if it is new.</summary>
        /// <param name="vertex">The welded vertex.</param>
        /// <returns>Its local index.</returns>
        int Local(int vertex) {
            if (weldedToLocal.TryGetValue(vertex, out var local)) {
                return local;
            }

            local = localToWelded.Count;
            localToWelded.Add(vertex);
            weldedToLocal[vertex] = local;

            return local;
        }

        Vector3 Position(int local) => positions[localToWelded[local]];

        /// <summary>Fills in adjacency, quadrics, locks and the initial set of candidate collapses.</summary>
        void Build() {
            for (var triangle = 0; triangle < alive.Length; triangle++) {
                var a = triangles[triangle * 3];
                var b = triangles[(triangle * 3) + 1];
                var c = triangles[(triangle * 3) + 2];

                // A triangle whose corners are not three distinct points is not geometry — it is what
                // an exporter leaves behind. It carries no plane, no adjacency and no area, and
                // keeping it would put a zero-weight quadric and a pair of phantom edges into every
                // decision below.
                if (a == b || b == c || a == c) {
                    continue;
                }

                alive[triangle] = true;
                liveTriangles++;

                incident[a].Add(triangle);
                incident[b].Add(triangle);
                incident[c].Add(triangle);

                adjacent[a].Add(b);
                adjacent[a].Add(c);
                adjacent[b].Add(a);
                adjacent[b].Add(c);
                adjacent[c].Add(a);
                adjacent[c].Add(b);

                var plane = Plane(a, b, c, out var area);

                if (area <= 0) {
                    continue;
                }

                var quadric = Quadric.FromPlane(plane, area);
                quadrics[a].Add(quadric);
                quadrics[b].Add(quadric);
                quadrics[c].Add(quadric);
            }

            foreach (var key in lockedEdges) {
                LockEnd((int)(key >> 32));
                LockEnd((int)(key & 0xFFFFFFFF));
            }

            // A seam is locked from both directions, and both are needed. It may not be a target,
            // because the survivor would have to be one of the copies and every triangle arriving
            // from the other chart would take that copy's texture coordinate. And it may not be
            // removed either, because welding made the copies one vertex: moving it moves both
            // charts at once, and whichever copy the survivor is, the other chart's triangles land on
            // it. The cost is that a seam does not simplify — a mesh cut into many small charts
            // therefore has a floor on how coarse it can get, which is a property of how it was
            // unwrapped rather than of this build.
            for (var vertex = 0; vertex < locked.Length; vertex++) {
                if (seam[localToWelded[vertex]]) {
                    locked[vertex] = true;
                }
            }

            for (var vertex = 0; vertex < adjacent.Length; vertex++) {
                foreach (var other in adjacent[vertex]) {
                    if (vertex < other) {
                        Offer(vertex, other);
                        Offer(other, vertex);
                    }
                }
            }
        }

        /// <summary>Marks a welded vertex unremovable, if this group holds it at all.</summary>
        /// <param name="vertex">The welded vertex.</param>
        void LockEnd(int vertex) {
            if (weldedToLocal.TryGetValue(vertex, out var local)) {
                locked[local] = true;
            }
        }

        /// <summary>The plane of a triangle, and twice the area to weight it by.</summary>
        /// <param name="a">One corner.</param>
        /// <param name="b">Another.</param>
        /// <param name="c">The third.</param>
        /// <param name="area">Twice the triangle's area.</param>
        /// <returns>The plane, as a unit normal and a distance.</returns>
        Vector4 Plane(int a, int b, int c, out float area) {
            var pa = Position(a);
            var cross = Vector3.Cross(Position(b) - pa, Position(c) - pa);

            area = cross.Length();

            if (area <= 0) {
                return default;
            }

            var normal = cross / area;

            return new(normal.X, normal.Y, normal.Z, -Vector3.Dot(normal, pa));
        }

        /// <summary>Puts a collapse on the queue with what it currently costs.</summary>
        /// <param name="from">The vertex that would be removed.</param>
        /// <param name="to">The vertex that would survive.</param>
        void Offer(int from, int to) {
            if (locked[from] || seam[localToWelded[to]]) {
                return;
            }

            var quadric = quadrics[from];
            quadric.Add(quadrics[to]);

            var cost = Math.Max(quadric.Evaluate(Position(to)), 0);

            // The direction is part of the key, not only the pair. Two entries with one priority pop
            // in whatever order the heap happens to hold them, and a build has to produce the same
            // DAG twice — so no two entries are allowed to share a priority.
            queue.Enqueue(new(from, to, version[from], version[to]), (cost, from, to));
        }

        /// <summary>Collapses until the triangle count is low enough or nothing legal is left.</summary>
        /// <param name="targetTriangles">How many triangles to aim for.</param>
        public void Run(int targetTriangles) {
            while (liveTriangles > targetTriangles && queue.TryDequeue(out var candidate, out var priority)) {
                if (removed[candidate.From] || removed[candidate.To]) {
                    continue;
                }

                if (version[candidate.From] != candidate.FromVersion || version[candidate.To] != candidate.ToVersion) {
                    continue;
                }

                if (!IsLegal(candidate.From, candidate.To)) {
                    continue;
                }

                Collapse(candidate.From, candidate.To, priority.Cost);
            }
        }

        /// <summary>Whether a collapse leaves a surface, and leaves the locked edges alone.</summary>
        /// <param name="from">The vertex that would be removed.</param>
        /// <param name="to">The vertex that would survive.</param>
        /// <returns>Whether it may happen.</returns>
        bool IsLegal(int from, int to) {
            if (locked[from] || seam[localToWelded[to]] || !adjacent[from].Contains(to)) {
                return false;
            }

            var shared = 0;

            foreach (var triangle in incident[from]) {
                if (!alive[triangle]) {
                    continue;
                }

                if (!Contains(triangle, to)) {
                    continue;
                }

                shared++;

                // The triangle disappears in this collapse. If it is the one holding a locked edge
                // up, the edge goes with it — the crack the lock exists to prevent, arriving from
                // the one direction locking the endpoints does not cover.
                if (CarriesLockedEdge(triangle)) {
                    return false;
                }
            }

            if (shared == 0) {
                return false;
            }

            // The link condition. Anything adjacent to both ends that is not opposite the edge means
            // the two ends are joined by a path that is not a face, and closing it folds the surface
            // onto itself.
            var common = 0;

            foreach (var neighbour in adjacent[from]) {
                if (adjacent[to].Contains(neighbour)) {
                    common++;
                }
            }

            if (common != shared) {
                return false;
            }

            return !WouldFlip(from, to);
        }

        /// <summary>Whether any triangle would turn inside out.</summary>
        /// <param name="from">The vertex that would be removed.</param>
        /// <param name="to">The vertex that would survive.</param>
        /// <returns>Whether a surviving triangle would face the other way.</returns>
        /// <remarks>
        ///     The quadric knows nothing about orientation: a vertex pulled through the surface lands
        ///     the same distance from the same planes as one pulled onto it, at half the cost of
        ///     leaving it where it was. Without this a simplification of anything thin — a leaf, a
        ///     sheet of cloth, a wall — inverts triangles rather than removing them.
        /// </remarks>
        bool WouldFlip(int from, int to) {
            var destination = Position(to);

            foreach (var triangle in incident[from]) {
                if (!alive[triangle] || Contains(triangle, to)) {
                    continue;
                }

                var a = triangles[triangle * 3];
                var b = triangles[(triangle * 3) + 1];
                var c = triangles[(triangle * 3) + 2];

                var before = Vector3.Cross(Position(b) - Position(a), Position(c) - Position(a));

                var pa = a == from ? destination : Position(a);
                var pb = b == from ? destination : Position(b);
                var pc = c == from ? destination : Position(c);

                var after = Vector3.Cross(pb - pa, pc - pa);

                if (after.LengthSquared() <= 0 || Vector3.Dot(before, after) <= 0) {
                    return true;
                }
            }

            return false;
        }

        /// <summary>How far a point is from the triangles that meet at a vertex.</summary>
        /// <param name="point">The point.</param>
        /// <param name="vertex">The vertex whose surviving triangles to measure against.</param>
        /// <returns>The distance to the nearest of them.</returns>
        /// <remarks>
        ///     The one ring rather than the whole group. A collapse is local — the only triangles
        ///     that changed are the ones that met at the vertex it removed, and they are the ones
        ///     that now stand where it was. Measuring against everything would cost the group's
        ///     triangle count per collapse and would answer with the same number.
        /// </remarks>
        float Distance(Vector3 point, int vertex) {
            var nearest = float.MaxValue;

            foreach (var triangle in incident[vertex]) {
                if (!alive[triangle]) {
                    continue;
                }

                nearest = MathF.Min(
                    nearest,
                    DistanceSquared(
                        point,
                        Position(triangles[triangle * 3]),
                        Position(triangles[(triangle * 3) + 1]),
                        Position(triangles[(triangle * 3) + 2])
                    )
                );
            }

            return nearest == float.MaxValue ? 0f : MathF.Sqrt(nearest);
        }

        /// <summary>The squared distance from a point to a triangle.</summary>
        /// <param name="point">The point.</param>
        /// <param name="a">One corner.</param>
        /// <param name="b">Another.</param>
        /// <param name="c">The third.</param>
        /// <returns>The squared distance.</returns>
        /// <remarks>
        ///     Ericson's region test: which of the triangle's seven Voronoi regions the point is in,
        ///     decided by six dot products, and then the distance to that feature. The obvious
        ///     alternative — project onto the plane and clamp the barycentrics — is wrong outside the
        ///     triangle, where the nearest point is on an edge and the clamped projection is not.
        /// </remarks>
        static float DistanceSquared(Vector3 point, Vector3 a, Vector3 b, Vector3 c) {
            var ab = b - a;
            var ac = c - a;
            var ap = point - a;

            var d1 = Vector3.Dot(ab, ap);
            var d2 = Vector3.Dot(ac, ap);

            if (d1 <= 0 && d2 <= 0) {
                return (point - a).LengthSquared();
            }

            var bp = point - b;
            var d3 = Vector3.Dot(ab, bp);
            var d4 = Vector3.Dot(ac, bp);

            if (d3 >= 0 && d4 <= d3) {
                return (point - b).LengthSquared();
            }

            var cp = point - c;
            var d5 = Vector3.Dot(ab, cp);
            var d6 = Vector3.Dot(ac, cp);

            if (d6 >= 0 && d5 <= d6) {
                return (point - c).LengthSquared();
            }

            var vc = (d1 * d4) - (d3 * d2);

            if (vc <= 0 && d1 >= 0 && d3 <= 0) {
                return (point - (a + (ab * (d1 / (d1 - d3))))).LengthSquared();
            }

            var vb = (d5 * d2) - (d1 * d6);

            if (vb <= 0 && d2 >= 0 && d6 <= 0) {
                return (point - (a + (ac * (d2 / (d2 - d6))))).LengthSquared();
            }

            var va = (d3 * d6) - (d5 * d4);

            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) {
                return (point - (b + ((c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)))))).LengthSquared();
            }

            var denominator = 1f / (va + vb + vc);

            return (point - (a + (ab * (vb * denominator)) + (ac * (vc * denominator)))).LengthSquared();
        }

        /// <summary>Whether a triangle has a corner at a vertex.</summary>
        /// <param name="triangle">The triangle.</param>
        /// <param name="vertex">The vertex.</param>
        /// <returns>Whether it does.</returns>
        bool Contains(int triangle, int vertex) =>
            triangles[triangle * 3] == vertex
            || triangles[(triangle * 3) + 1] == vertex
            || triangles[(triangle * 3) + 2] == vertex;

        /// <summary>Whether any of a triangle's edges is one that may not move.</summary>
        /// <param name="triangle">The triangle.</param>
        /// <returns>Whether it carries a locked edge.</returns>
        bool CarriesLockedEdge(int triangle) {
            for (var corner = 0; corner < 3; corner++) {
                var a = localToWelded[triangles[(triangle * 3) + corner]];
                var b = localToWelded[triangles[(triangle * 3) + ((corner + 1) % 3)]];

                if (a != b && lockedEdges.Contains(Topology.Edge(a, b))) {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Performs a collapse and offers everything it touched back to the queue.</summary>
        /// <param name="from">The vertex being removed.</param>
        /// <param name="to">The vertex surviving.</param>
        /// <param name="cost">What the quadric said it would cost.</param>
        void Collapse(int from, int to, double cost) {
            foreach (var triangle in incident[from]) {
                if (!alive[triangle]) {
                    continue;
                }

                if (Contains(triangle, to)) {
                    alive[triangle] = false;
                    liveTriangles--;

                    continue;
                }

                for (var corner = 0; corner < 3; corner++) {
                    if (triangles[(triangle * 3) + corner] == from) {
                        triangles[(triangle * 3) + corner] = to;
                    }
                }

                incident[to].Add(triangle);
            }

            foreach (var neighbour in adjacent[from]) {
                adjacent[neighbour].Remove(from);

                if (neighbour == to) {
                    continue;
                }

                adjacent[neighbour].Add(to);
                adjacent[to].Add(neighbour);
            }

            adjacent[from].Clear();
            adjacent[to].Remove(to);
            incident[from].Clear();

            // A triangle is left in the incident lists of the vertices it no longer touches, because
            // taking it out of all three costs a search per collapse. That is fine until a vertex has
            // absorbed a hundred collapses and every one of the four things below that walk its list
            // is walking mostly rubbish — which is a quadratic hiding inside an offline build. A live
            // one ring is a dozen triangles, so a list several times that has earned a sweep.
            if (incident[to].Count > CompactionThreshold) {
                incident[to].RemoveAll(triangle => !alive[triangle] || !Contains(triangle, to));
            }
            quadrics[to].Add(quadrics[from]);
            removed[from] = true;

            // What the collapse actually cost, measured rather than inferred. The quadric is what
            // chose this collapse and it is the wrong number to report: it is the distance to the
            // *planes* of the triangles that met at the vertex, which on a smooth surface is a small
            // fraction of the distance to the surface itself — about a third of it on a sphere.
            // Reporting that would mean a cut chosen for a one-pixel budget that pops by three.
            //
            // The point that was here is now this far from the surface that replaced it, and it
            // stood for everything within error[from] of itself, so everything it stood for is now
            // within the sum. That is a bound rather than an estimate, and it is tight enough to be
            // useful because each level's own step dominates the ones below it.
            var introduced = Distance(Position(from), to);
            var accumulated = Math.Max(error[to], error[from] + introduced);

            error[to] = accumulated;
            worst = Math.Max(worst, accumulated);

            // Only these two. A neighbour's own quadric did not change, so its other candidates are
            // still costed correctly — invalidating them would throw away collapses nothing is wrong
            // with, and the simplification would stall well above its target. Whether they are still
            // *legal* is a different question, and that one is asked when they surface.
            version[from]++;
            version[to]++;

            foreach (var neighbour in adjacent[to]) {
                Offer(neighbour, to);
                Offer(to, neighbour);
            }
        }

        /// <summary>The surviving triangles, back in the mesh's own vertex indices.</summary>
        /// <returns>The result.</returns>
        /// <remarks>
        ///     A corner that never moved keeps the <em>source</em> index it arrived with, which is
        ///     what preserves an attribute seam: the two copies of a seam vertex stay two copies, and
        ///     each triangle keeps the one it was authored against. A corner that did move takes the
        ///     survivor's representative, which is unambiguous because a collapse onto a seam vertex
        ///     is refused.
        /// </remarks>
        public SimplifyResult Result() {
            var corners = new List<int>(liveTriangles * 3);
            var updates = new List<(int Vertex, float Error)>();

            for (var vertex = 0; vertex < error.Length; vertex++) {
                if (!removed[vertex] && error[vertex] > 0) {
                    updates.Add((localToWelded[vertex], error[vertex]));
                }
            }

            for (var triangle = 0; triangle < alive.Length; triangle++) {
                if (!alive[triangle]) {
                    continue;
                }

                for (var corner = 0; corner < 3; corner++) {
                    var index = (triangle * 3) + corner;

                    corners.Add(
                        triangles[index] == initial[index] ? sources[index] : localToWelded[triangles[index]]
                    );
                }
            }

            return new([.. corners], worst, [.. updates]);
        }

        /// <summary>A collapse waiting to be judged, and what the mesh looked like when it was offered.</summary>
        /// <param name="From">The vertex that would be removed.</param>
        /// <param name="To">The vertex that would survive.</param>
        /// <param name="FromVersion">What <see cref="From" />'s version was.</param>
        /// <param name="ToVersion">What <see cref="To" />'s version was.</param>
        readonly record struct Candidate(int From, int To, int FromVersion, int ToVersion);
    }
}
