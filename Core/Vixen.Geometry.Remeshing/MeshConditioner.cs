// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Stage one: seven steps, in order, each one reporting what it changed.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D3, and § B1 is why it is a stage rather than hygiene.</b> Every
///         image-to-3D and text-to-3D generator in current use reconstructs a signed field and
///         extracts a surface with marching cubes, and what comes out is staircase noise at the voxel
///         frequency, self-intersections, near-degenerate slivers, floating debris the field
///         hallucinated, and non-manifold edges where two sheets meet at a cell boundary. A remesher
///         that assumes a clean manifold is a remesher that does not run on the input this library
///         exists for.
///     </para>
///     <para>
///         ⚠ <b>Every tolerance here is a fraction of the bounding-box diagonal and none is a
///         distance.</b> Doc 24 records the same lesson twice — <c>EditMesh.DefaultWeldTolerance</c>'s
///         own remarks, and the capsule's poles in doc 24 § P1, where an absolute epsilon declared
///         sixty-four real triangles degenerate and "a primitive built at a tenth of the scale would
///         have had all of them declared so". A fixed number is a claim about how big a model is.
///     </para>
///     <para>
///         ⚠ <b>Orientation propagates only across manifold edges; connectivity counts every
///         shared edge.</b> Those are two different graphs over the same faces and using one for both
///         is a bug in either direction. A wall meeting a floor in a T — doc 24's <i>ordinary</i>
///         blockout case — has an edge with three faces, and no assignment of windings makes three
///         faces pairwise opposed along it, so a flood fill that crossed it would declare an
///         perfectly ordinary blockout unorientable. Meanwhile a de-speck that would not cross it
///         would see the wall and the floor as two components and quietly delete the smaller.
///     </para>
/// </remarks>
static class MeshConditioner {
    /// <summary>How many rounds the non-manifold repair will iterate before giving up.</summary>
    /// <remarks>One round resolves every case that occurs in practice. The cap is for the exotic
    ///     one where two faces of a cut edge are still joined at both ends through other edges.</remarks>
    public const int RepairRounds = 8;

    /// <summary>How large a cross product's own rounding error is, against the two edges it multiplied.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A relative bound is not scale-free if it sits below what the arithmetic can
    ///         resolve, and that is a different mistake from an absolute one but it fails the same
    ///         way.</b> <c>b - a</c> is a difference of coordinates of order the diagonal, so it
    ///         carries an absolute error of order <c>ε × diagonal</c> whatever the edge's own length
    ///         is — and a cross product of two such differences therefore carries an error of order
    ///         <c>ε × diagonal × reach</c>, where <c>reach</c> is the longer of them. Single
    ///         precision's <c>ε</c> is <c>1.19e-7</c>, so a triangle that is <i>exactly</i> degenerate
    ///         does not compute an area of zero: it computes a power of two somewhere in that band.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured, and the reason the constant is what it is.</b> On a flight of stairs
    ///         driven through a copy of itself, one exactly-degenerate face computed an area of
    ///         <c>0</c> at unit scale and at a thousand times, and <c>2^-42</c> at a
    ///         thousandth — because <c>0.001f</c> is not a binary fraction and perturbs every
    ///         coordinate by an ulp. Against the old bound of <c>1e-9 × diagonal²</c> that is
    ///         <i>1.35 times over</i>, so the face was dropped at two scales and kept at the third,
    ///         and a closed surface came out with a triangular hole in it. The residues seen were
    ///         <c>0.5ε</c> to <c>1.1ε</c> of <c>diagonal × reach</c>; this is eight times the larger
    ///         of them.
    ///     </para>
    ///     <para>
    ///         And it cannot reject a triangle anybody wanted: <c>area ≤ diagonal × reach × 1e-6</c>
    ///         is the same statement as <c>height ≤ 2e-6 × diagonal</c>, which is a fifth of the
    ///         default <see cref="ConditioningSettings.WeldTolerance" />. A face this step now drops
    ///         and did not before is thinner than the distance at which the weld above it calls two
    ///         positions the same place.
    ///     </para>
    /// </remarks>
    public const float CrossNoise = 1e-6f;

    /// <summary>Runs the seven steps and builds the view the rest of the pipeline works on.</summary>
    /// <param name="source">The input. Read, never modified.</param>
    /// <param name="settings">What conditioning is allowed to do.</param>
    /// <param name="report">What it did.</param>
    /// <param name="targetEdgeLength">
    ///     The length the isotropic pre-remesh aims for. Zero means the input's own mean edge length.
    /// </param>
    /// <returns>The conditioned surface.</returns>
    /// <remarks>
    ///     ⚠ <b><paramref name="targetEdgeLength" /> is a parameter rather than a
    ///     <see cref="ConditioningSettings" /> field, and that is a gap in the contract rather than a
    ///     choice.</b> Step six is defined as "isotropic pre-remesh <i>to a target edge length</i>"
    ///     and the only place a length lives is <see cref="RemeshSettings.TargetEdgeLength" />, which
    ///     conditioning is not handed. The pipeline passes the one it derived; a caller conditioning
    ///     on its own passes zero and gets the mesh's own mean, which is the scale-free answer.
    /// </remarks>
    public static ManifoldMesh Condition(
        EditMesh source,
        ConditioningSettings settings,
        out ConditioningReport report,
        float targetEdgeLength = 0f
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        var soup = TriangleSoup.From(source);

        soup.Compact();

        // Taken once, before anything moves, so that every step's threshold is a fraction of the
        // same length — a diagonal re-measured after a de-speck would make the hole threshold depend
        // on whether a speck happened to be far away.
        var diagonal = soup.Diagonal;

        var welded = Weld(soup, settings.WeldTolerance, diagonal);
        var first = Orient(soup);
        var reoriented = first.Reoriented;
        var unorientable = first.Unorientable;
        var despecked = Despeck(soup, settings.SpeckArea);
        var cut = Repair(soup);
        var filled = settings.FillHoles ? Fill(soup, settings.HoleSize * diagonal) : 0;

        // ⚠ Built before the pre-remesh and never rebuilt: the reference the relaxation reprojects
        // onto is the surface as it stood when step six began. Reprojecting onto the previous
        // iteration compounds the drift instead of correcting it.
        var reference = SurfaceProjector.Build([.. soup.Positions], [.. soup.Triangles]);
        var target = IsotropicRemesh.Run(soup, reference, targetEdgeLength, settings.PreRemeshIterations);

        var shrinkwrapped = false;

        if (settings.Shrinkwrap) {
            shrinkwrapped = VoxelShrinkwrap.Run(soup, target);

            if (shrinkwrapped) {
                // The extraction is a fresh surface with fresh defects — one vertex per cell pinches
                // wherever a cell's inside corners are diagonally opposite — so the cheap half of
                // conditioning runs again over it. Not the pre-remesh: a second one would relax the
                // extraction toward a reference it no longer resembles.
                //
                // ⚠ De-specked *after* the repair here, where the first pass does it before. Cutting
                // a pinch detaches whatever was hanging off it, so the crumbs a dual extraction
                // leaves do not exist until the repair has run — and running the de-speck first
                // would leave every one of them in the output.
                welded += Weld(soup, settings.WeldTolerance, soup.Diagonal);

                var again = Orient(soup);

                reoriented += again.Reoriented;
                unorientable += again.Unorientable;
                cut += Repair(soup);
                despecked += Despeck(soup, settings.SpeckArea);
            }
        }

        var view = ManifoldMesh.Build(soup);

        report = new(
            welded,
            reoriented,
            unorientable,
            despecked,
            cut,
            filled,
            shrinkwrapped,
            view.TriangleCount,
            view.ToEditMesh().Validate()
        );

        return view;
    }

    /// <summary>Step one — collapses positions nearer than a fraction of the diagonal into one.</summary>
    /// <param name="soup">The mesh, rewritten.</param>
    /// <param name="tolerance">How near, as a fraction of <paramref name="diagonal" />.</param>
    /// <param name="diagonal">The bounding box's diagonal.</param>
    /// <returns>How many positions were merged away.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A grid of cells the size of the tolerance, and the twenty-seven round each one are
    ///         searched.</b> Hashing the rounded position alone is one line shorter and wrong in
    ///         exactly the case this exists for — two positions a hair apart either side of a cell
    ///         boundary land in different buckets and are never compared. The same structure
    ///         <c>EditMesh.Weld</c> uses, for the same reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Triangles that collapse to a line, and exact duplicates, go with them, and the
    ///         report has no field that counts them.</b> A triangle with two equal corners has no
    ///         area, no normal and no edge; a triangle listed twice makes every one of its three edges
    ///         non-manifold, so step four would cut them apart into a doubled sheet that the field
    ///         would then be solved on twice. Both are removed here. The difference shows in
    ///         <see cref="ConditioningReport.Triangles" /> and nowhere else, which is the same
    ///         silence <c>EditMesh.FromTriangles</c> keeps about the triangles its own weld collapses.
    ///     </para>
    /// </remarks>
    public static int Weld(TriangleSoup soup, float tolerance, float diagonal) {
        ArgumentNullException.ThrowIfNull(soup);

        var before = soup.Positions.Count;
        var epsilon = tolerance > 0f ? diagonal * tolerance : 0f;
        var remap = new int[before];

        if (epsilon > 0f) {
            var cells = new Dictionary<(long X, long Y, long Z), List<int>>();
            var kept = new List<Vector3>(before);
            var squared = epsilon * epsilon;

            for (var index = 0; index < before; index++) {
                var position = soup.Positions[index];
                var cell = Cell(position, epsilon);
                var at = -1;

                for (var x = -1; at < 0 && x <= 1; x++) {
                    for (var y = -1; at < 0 && y <= 1; y++) {
                        for (var z = -1; at < 0 && z <= 1; z++) {
                            if (!cells.TryGetValue((cell.X + x, cell.Y + y, cell.Z + z), out var bucket)) {
                                continue;
                            }

                            foreach (var candidate in bucket) {
                                if (Vector3.DistanceSquared(kept[candidate], position) <= squared) {
                                    at = candidate;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (at < 0) {
                    at = kept.Count;
                    kept.Add(position);

                    if (!cells.TryGetValue(cell, out var bucket)) {
                        cells[cell] = bucket = [];
                    }

                    bucket.Add(at);
                }

                remap[index] = at;
            }

            soup.Positions.Clear();
            soup.Positions.AddRange(kept);
        } else {
            var exact = new Dictionary<Vector3, int>(before);
            var kept = new List<Vector3>(before);

            for (var index = 0; index < before; index++) {
                var position = soup.Positions[index];

                if (!exact.TryGetValue(position, out var at)) {
                    at = kept.Count;
                    kept.Add(position);
                    exact[position] = at;
                }

                remap[index] = at;
            }

            soup.Positions.Clear();
            soup.Positions.AddRange(kept);
        }

        var triangles = soup.Triangles.ToArray();
        var groups = soup.Groups.ToArray();

        soup.Triangles.Clear();
        soup.Groups.Clear();

        // The same relative sliver bound `EditMesh.Validate` uses, so that a mesh this step passed
        // reports no degenerate faces rather than nearly none. It is a floor and not the whole bound
        // — see `CrossNoise` for the term that dominates it on anything larger than a thousandth of
        // the diagonal, and for the closed-surface-with-a-hole that term exists to stop.
        var sliver = diagonal * diagonal * 1e-9f;
        var noise = diagonal * CrossNoise;
        var seen = new HashSet<(int A, int B, int C)>();

        for (var triangle = 0; triangle * 3 < triangles.Length; triangle++) {
            var a = remap[triangles[(triangle * 3) + 0]];
            var b = remap[triangles[(triangle * 3) + 1]];
            var c = remap[triangles[(triangle * 3) + 2]];

            if (a == b || b == c || c == a) {
                continue;
            }

            var one = soup.Positions[b] - soup.Positions[a];
            var two = soup.Positions[c] - soup.Positions[a];
            var area = Vector3.Cross(one, two).Length() * 0.5f;
            var reach = MathF.Max(one.Length(), two.Length());

            if (area <= MathF.Max(sliver, reach * noise)) {
                continue;
            }

            // Sorted, so that a face and the same face wound the other way are one duplicate rather
            // than two faces that happen to share three vertices.
            var low = Math.Min(a, Math.Min(b, c));
            var high = Math.Max(a, Math.Max(b, c));

            if (!seen.Add((low, a + b + c - low - high, high))) {
                continue;
            }

            soup.Add(a, b, c, groups[triangle]);
        }

        soup.Compact();
        return before - soup.Positions.Count;
    }

    /// <summary>Step two — flood fills a consistent winding over the face graph.</summary>
    /// <param name="soup">The mesh, rewritten.</param>
    /// <returns>How many faces were flipped, and how many components could not be made consistent.</returns>
    /// <remarks>
    ///     ⚠ <b>Unorientable components are flagged, not fixed.</b> A Möbius-like sheet has no
    ///     consistent winding — that is a fact about the surface and not a mistake in the file — so
    ///     the flood fill keeps the best-effort assignment it reached, which is consistent everywhere
    ///     except across one seam, and the component is counted into
    ///     <see cref="ConditioningReport.Unorientable" />. Repairing it would mean cutting the seam,
    ///     which is a decision about the model that belongs to whoever made it.
    /// </remarks>
    public static (int Reoriented, int Unorientable) Orient(TriangleSoup soup) {
        ArgumentNullException.ThrowIfNull(soup);

        var count = soup.TriangleCount;
        var faces = Incidence(soup);
        var visited = new bool[count];
        var reoriented = 0;
        var unorientable = 0;
        var stack = new Stack<int>();

        for (var seed = 0; seed < count; seed++) {
            if (visited[seed]) {
                continue;
            }

            var broken = false;

            visited[seed] = true;
            stack.Push(seed);

            while (stack.Count > 0) {
                var face = stack.Pop();

                for (var side = 0; side < 3; side++) {
                    var from = soup.Triangles[(face * 3) + side];
                    var to = soup.Triangles[(face * 3) + ((side + 1) % 3)];

                    if (!faces.TryGetValue(Key(from, to), out var sharing) || sharing.Count != 2) {
                        // A boundary, or a non-manifold edge. Neither carries an orientation across:
                        // three faces on one edge cannot be pairwise opposed, and treating that as a
                        // contradiction would call every T-junction blockout unorientable.
                        continue;
                    }

                    var other = sharing[0] == face ? sharing[1] : sharing[0];

                    if (other == face) {
                        continue;
                    }

                    var forwards = Walks(soup, other, from, to);

                    if (visited[other]) {
                        if (forwards) {
                            broken = true;
                        }

                        continue;
                    }

                    if (forwards) {
                        (soup.Triangles[(other * 3) + 1], soup.Triangles[(other * 3) + 2]) =
                            (soup.Triangles[(other * 3) + 2], soup.Triangles[(other * 3) + 1]);

                        reoriented++;
                    }

                    visited[other] = true;
                    stack.Push(other);
                }
            }

            if (broken) {
                unorientable++;
            }
        }

        return (reoriented, unorientable);
    }

    /// <summary>Step three — drops components below a fraction of the largest component's area.</summary>
    /// <param name="soup">The mesh, rewritten.</param>
    /// <param name="fraction">The threshold, as a fraction of the largest component's surface area.</param>
    /// <returns>How many components were dropped.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Area, not triangle count, and the difference is the whole of what the default has
    ///         to get right.</b> A generator's hallucinated debris is a handful of triangles covering
    ///         a millionth of the model; a character's separate eyeball is a well-tessellated sphere
    ///         covering a fair fraction of a percent. By triangle count the eyeball can be the smaller
    ///         of the two, and by area it never is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Connectivity here counts every shared edge, including non-manifold ones.</b> See
    ///         this type's own remarks: the wall and the floor of a T-junction are one component even
    ///         though no winding joins them, and a de-speck that thought otherwise would delete
    ///         whichever of the two was smaller.
    ///     </para>
    /// </remarks>
    public static int Despeck(TriangleSoup soup, float fraction) {
        ArgumentNullException.ThrowIfNull(soup);

        var count = soup.TriangleCount;

        if (count == 0 || fraction <= 0f) {
            return 0;
        }

        var parent = new int[count];

        for (var face = 0; face < count; face++) {
            parent[face] = face;
        }

        foreach (var sharing in Incidence(soup).Values) {
            for (var index = 1; index < sharing.Count; index++) {
                var a = Find(parent, sharing[0]);
                var b = Find(parent, sharing[index]);

                if (a != b) {
                    parent[Math.Max(a, b)] = Math.Min(a, b);
                }
            }
        }

        var areas = new Dictionary<int, float>();

        for (var face = 0; face < count; face++) {
            var root = Find(parent, face);

            areas[root] = areas.GetValueOrDefault(root) + soup.Area(face);
        }

        var largest = 0f;

        foreach (var area in areas.Values) {
            largest = MathF.Max(largest, area);
        }

        if (largest <= 0f) {
            return 0;
        }

        var bound = largest * fraction;
        var dropped = 0;

        foreach (var area in areas.Values) {
            if (area < bound) {
                dropped++;
            }
        }

        if (dropped == 0) {
            return 0;
        }

        var triangles = soup.Triangles.ToArray();
        var groups = soup.Groups.ToArray();

        soup.Triangles.Clear();
        soup.Groups.Clear();

        for (var face = 0; face < count; face++) {
            if (areas[Find(parent, face)] >= bound) {
                soup.Add(
                    triangles[(face * 3) + 0],
                    triangles[(face * 3) + 1],
                    triangles[(face * 3) + 2],
                    groups[face]
                );
            }
        }

        soup.Compact();
        return dropped;
    }

    /// <summary>Step four — splits every edge with three or more faces so that each side is manifold.</summary>
    /// <param name="soup">The mesh, rewritten.</param>
    /// <returns>How many non-manifold edges were cut.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Cut, never merge.</b> docs/plan/41 § D3 step 4: cutting keeps the geometry and
    ///         costs a seam; merging invents a surface that was never there. The observable difference
    ///         is that the triangle count is unchanged afterwards — nothing is deleted, positions are
    ///         duplicated — and a repair that reduced it did something else.
    ///     </para>
    ///     <para>
    ///         <b>One algorithm for both defects.</b> The corners at a vertex are grouped into fans by
    ///         the manifold edges that join them, and each fan gets its own copy of the vertex. A
    ///         non-manifold <i>edge</i> falls out because a cut edge joins nothing; a non-manifold
    ///         <i>vertex</i> — the bowtie where two cones meet at a point, which a marching-cubes
    ///         extraction produces at every ambiguous cell — falls out of the same grouping without a
    ///         second pass, and it is a defect <see cref="MeshReport" /> does not name at all.
    ///     </para>
    /// </remarks>
    public static int Repair(TriangleSoup soup) {
        ArgumentNullException.ThrowIfNull(soup);

        var cut = 0;

        for (var round = 0; round < RepairRounds; round++) {
            var faces = Incidence(soup);
            var count = soup.TriangleCount;
            var broken = 0;

            foreach (var sharing in faces.Values) {
                if (sharing.Count > 2) {
                    broken++;
                }
            }

            // The corners at each vertex, as a union-find over half-edges.
            var parent = new int[count * 3];

            for (var half = 0; half < parent.Length; half++) {
                parent[half] = half;
            }

            // Which outgoing half-edges use each (vertex, neighbour) pair — at most two of them on a
            // manifold edge, which is the case that joins two corners into one fan.
            var fans = new Dictionary<(int Vertex, int Neighbour), List<int>>();

            for (var half = 0; half < count * 3; half++) {
                var vertex = soup.Triangles[half];

                foreach (var neighbour in (int[]) [
                    soup.Triangles[ManifoldMesh.Next(half)],
                    soup.Triangles[ManifoldMesh.Previous(half)]
                ]) {
                    var key = (vertex, neighbour);

                    if (!fans.TryGetValue(key, out var list)) {
                        fans[key] = list = [];
                    }

                    list.Add(half);
                }
            }

            var split = false;

            foreach (var ((vertex, neighbour), corners) in fans) {
                if (corners.Count != 2) {
                    continue;
                }

                // Only across an edge that is manifold in the mesh as a whole. An edge with three
                // faces has three corners at this vertex, so it never reaches here — but an edge with
                // three faces where one of them visits the vertex twice would, and this is the check
                // that keeps the two questions separate.
                if (!faces.TryGetValue(Key(vertex, neighbour), out var sharing) || sharing.Count != 2) {
                    continue;
                }

                Union(parent, corners[0], corners[1]);
            }

            var copies = new Dictionary<int, int>();
            var claimed = new bool[soup.Positions.Count];

            for (var half = 0; half < count * 3; half++) {
                var root = Find(parent, half);

                if (!copies.TryGetValue(root, out var index)) {
                    var vertex = soup.Triangles[half];

                    // The first fan at a vertex keeps its position index; every one after it gets a
                    // duplicate at the same place, which is the seam the cut costs and the reason the
                    // triangle count does not move.
                    if (claimed[vertex]) {
                        index = soup.Positions.Count;
                        soup.Positions.Add(soup.Positions[vertex]);
                        split = true;
                    } else {
                        index = vertex;
                        claimed[vertex] = true;
                    }

                    copies[root] = index;
                }

                soup.Triangles[half] = index;
            }

            cut += broken;

            if (broken == 0 && !split) {
                break;
            }
        }

        soup.Compact();
        return cut;
    }

    /// <summary>Step five — closes boundary loops smaller than a fraction of the diagonal.</summary>
    /// <param name="soup">The mesh, rewritten.</param>
    /// <param name="size">The largest hole to close, as a length.</param>
    /// <returns>How many holes were filled.</returns>
    /// <remarks>
    ///     ⚠ <b>Off by default, and deliberately: a hole in the input is very often a hole in the
    ///     subject.</b> <see cref="ConditioningSettings.FillHoles" /> defaults to false. A mouth, a
    ///     sleeve and a pipe's bore are all boundary loops, and closing one silently is worse than
    ///     leaving it — the remesh will happily lay quads over it and nobody will look at the report.
    /// </remarks>
    public static int Fill(TriangleSoup soup, float size) {
        ArgumentNullException.ThrowIfNull(soup);

        var faces = Incidence(soup);

        // The hole walks each boundary edge the opposite way from the single face on it, so the
        // patch that closes it is wound consistently with what is already there.
        var next = new Dictionary<int, int>();

        foreach (var (key, sharing) in faces) {
            if (sharing.Count != 1) {
                continue;
            }

            var face = sharing[0];

            for (var side = 0; side < 3; side++) {
                var from = soup.Triangles[(face * 3) + side];
                var to = soup.Triangles[(face * 3) + ((side + 1) % 3)];

                if (Key(from, to) == key) {
                    next.TryAdd(to, from);
                    break;
                }
            }
        }

        var filled = 0;
        var visited = new HashSet<int>();

        foreach (var start in next.Keys) {
            if (!visited.Add(start)) {
                continue;
            }

            List<int> loop = [start];
            var at = start;
            var closed = false;

            while (next.TryGetValue(at, out var step)) {
                if (step == start) {
                    closed = true;
                    break;
                }

                at = step;

                if (!visited.Add(at)) {
                    // The chain ran into a boundary vertex it has already used, which is a boundary
                    // that branches. Abandoned rather than guessed at — a fan over a figure of eight
                    // is a surface nobody asked for.
                    break;
                }

                loop.Add(at);
            }

            if (!closed || loop.Count < 3) {
                continue;
            }

            var low = soup.Positions[loop[0]];
            var high = low;

            foreach (var vertex in loop) {
                low = Vector3.Min(low, soup.Positions[vertex]);
                high = Vector3.Max(high, soup.Positions[vertex]);
            }

            if ((high - low).Length() > size) {
                continue;
            }

            var centre = Vector3.Zero;

            foreach (var vertex in loop) {
                centre += soup.Positions[vertex];
            }

            var hub = soup.Positions.Count;

            soup.Positions.Add(centre / loop.Count);

            for (var index = 0; index < loop.Count; index++) {
                soup.Add(hub, loop[index], loop[(index + 1) % loop.Count], 0);
            }

            filled++;
        }

        return filled;
    }

    /// <summary>Which faces sit on each undirected edge.</summary>
    static Dictionary<long, List<int>> Incidence(TriangleSoup soup) {
        var faces = new Dictionary<long, List<int>>(soup.Triangles.Count);

        for (var face = 0; face < soup.TriangleCount; face++) {
            for (var side = 0; side < 3; side++) {
                var from = soup.Triangles[(face * 3) + side];
                var to = soup.Triangles[(face * 3) + ((side + 1) % 3)];

                if (from == to) {
                    continue;
                }

                var key = Key(from, to);

                if (!faces.TryGetValue(key, out var sharing)) {
                    faces[key] = sharing = [];
                }

                sharing.Add(face);
            }
        }

        return faces;
    }

    /// <summary>Whether a face walks an edge from one named end to the other rather than back.</summary>
    static bool Walks(TriangleSoup soup, int face, int from, int to) {
        for (var side = 0; side < 3; side++) {
            if (soup.Triangles[(face * 3) + side] == from
                && soup.Triangles[(face * 3) + ((side + 1) % 3)] == to) {
                return true;
            }
        }

        return false;
    }

    static int Find(int[] parent, int index) {
        while (parent[index] != index) {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    static void Union(int[] parent, int first, int second) {
        var a = Find(parent, first);
        var b = Find(parent, second);

        if (a != b) {
            parent[Math.Max(a, b)] = Math.Min(a, b);
        }
    }

    /// <summary>An unordered vertex pair as one key.</summary>
    static long Key(int a, int b) =>
        a < b ? ((long) a << 32) | (uint) b : ((long) b << 32) | (uint) a;

    static (long X, long Y, long Z) Cell(Vector3 position, float size) => (
        (long) MathF.Floor(position.X / size),
        (long) MathF.Floor(position.Y / size),
        (long) MathF.Floor(position.Z / size)
    );
}
