// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Where the field turns by a quarter of a turn that does not come back.</summary>
/// <param name="Triangle">Which triangle of the conditioned surface it sits in.</param>
/// <param name="Index">The turning in quarter turns: <c>+1</c> becomes a valence-3 in the output, <c>-1</c> a valence-5.</param>
/// <remarks>
///     <para>
///         ⚠ <b>A triangle and not a vertex, and docs/plan/41 § D5 says the opposite.</b> § D5:
///         "Singularities are read off afterwards as the index of each vertex — the accumulated
///         rotation around its one-ring, in quarter turns." That is the formulation for a
///         <i>face</i>-based field, where each cross lives in a triangle and the loop that encircles a
///         vertex is its fan; § D5's own first sentence puts the cross on the vertices, and the loop
///         that encircles one of <i>those</i> is a triangle. Taking the doc literally does not merely
///         mislabel the answer, it breaks the invariant: a one-ring loop encloses the whole fan at a
///         vertex, every triangle is enclosed by three such loops, and the total comes out at three
///         times the Euler characteristic. <c>SingularityTests</c> measures the corrected version at
///         exactly <c>4χ</c> on a sphere, a torus and a two-holed torus.
///     </para>
///     <para>
///         ⚠ <b>Not <see cref="Singularity" />, which is a different thing with a similar name.</b>
///         That one is a vertex of the finished quad mesh with a valence, and it belongs to
///         <see cref="RemeshReport" />; this one is a property of the field, on a surface that is
///         about to be thrown away.
///     </para>
/// </remarks>
readonly record struct FieldSingularity(int Triangle, int Index);

/// <summary>Reading the singularities off the field, and then putting them where an artist would.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D6, which is the single pass most responsible for whether the output
///         looks artist-made.</b> A field minimizing smoothness alone scatters singularity pairs
///         across flat regions, and a remesh with scattered singularities is what artists mean when
///         they say automatic topology looks wrong. Three corrections, in order: cancel adjacent
///         opposite pairs, push singularities off feature lines, attract them to Gaussian curvature.
///     </para>
///     <para>
///         ⚠ <b>It is cheap, and § D6 says so in as many words.</b> Tens or hundreds of singularities
///         rather than millions of vertices — the temptation is to spend the effort on the solver
///         instead, and § Part 6 records that this pass carries the aesthetic load <i>permanently</i>
///         rather than until a learned prior arrives.
///     </para>
///     <para>
///         ⚠ <b>Corrections two and three are one mechanism with two signs, and it is the edge
///         weight.</b> The energy is <c>Σ w·deviation</c>; a heavy edge is one the field may not turn
///         across and a light one is where turning is cheap. Stiffening round a feature repels;
///         releasing at a large angle defect attracts. See <see cref="FieldLevel.Weights" />.
///     </para>
/// </remarks>
static class SingularityPass {
    /// <summary>How many dual steps apart a <c>+¼</c> and a <c>−¼</c> may be and still count as a pair.</summary>
    /// <remarks>§ D6: "within a few edges of each other contribute nothing but noise".</remarks>
    public const int PairRadius = 4;

    /// <summary>How many extra sweeps a cancelled pair's neighbourhood gets.</summary>
    public const int CancelIterations = 24;

    /// <summary>How many level-zero sweeps each of the two stiffness corrections runs.</summary>
    /// <remarks>
    ///     ⚠ No hierarchy here, and that is not an oversight. The global structure was settled by the
    ///     hierarchical solve; these two are local rearrangements of where the turning sits, and a
    ///     coarse level would undo the very placement they exist to make.
    /// </remarks>
    public const int PlacementIterations = 12;

    /// <summary>How much heavier the edges round a feature line are.</summary>
    public const float FeatureStiffness = 6f;

    /// <summary>How far, in rings, the feature stiffening reaches.</summary>
    public const int FeatureRings = 2;

    /// <summary>How much lighter an edge at a full quarter-turn of angle defect is.</summary>
    public const float CurvatureRelease = 1.5f;

    /// <summary>Every triangle whose index is not zero, ascending.</summary>
    /// <param name="mesh">The surface.</param>
    /// <param name="field">The field on it.</param>
    /// <returns>The singularities.</returns>
    public static List<FieldSingularity> Extract(ManifoldMesh mesh, CrossField field) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(field);

        var found = new List<FieldSingularity>();

        for (var triangle = 0; triangle < mesh.TriangleCount; triangle++) {
            var index = IndexOf(mesh, field, triangle);

            if (index != 0) {
                found.Add(new(triangle, index));
            }
        }

        return found;
    }

    /// <summary>The turning of the field round one triangle, in quarter turns.</summary>
    /// <param name="mesh">The surface.</param>
    /// <param name="field">The field.</param>
    /// <param name="triangle">Which triangle.</param>
    /// <returns><c>-1</c>, <c>0</c> or <c>+1</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         The three vertex representatives are rotated into the triangle's own plane — one flat
    ///         chart — and read as angles in one basis, so the three differences telescope to exactly
    ///         zero. What is left is the period jumps: each difference is split into a multiple of a
    ///         quarter turn and a residual in <c>[−π/4, π/4]</c>, and because the differences sum to
    ///         nothing, the jumps are determined by the residuals alone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The rotation into the plane is what carries the curvature, and it is why this sums
    ///         to the Euler characteristic rather than to zero.</b> The same edge is flattened into two
    ///         different planes by its two triangles, and the difference between those two flattenings
    ///         is the dihedral angle — so the residuals do not cancel across an edge, and what
    ///         survives summing over the whole surface is <c>4χ</c>. Replace the rotation with a
    ///         projection and the identity is gone; see <see cref="CrossField.Transport" />.
    ///     </para>
    /// </remarks>
    public static int IndexOf(ManifoldMesh mesh, CrossField field, int triangle) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(field);

        var normal = mesh.TriangleNormal(triangle);

        if (normal.LengthSquared() <= 0f) {
            return 0;
        }

        var corners = mesh.Corners(triangle);
        var tangent = ScaleSafe.Flatten(mesh.Positions[corners[1]] - mesh.Positions[corners[0]], normal);

        if (tangent.LengthSquared() <= 0f) {
            return 0;
        }

        var bitangent = Vector3.Cross(normal, tangent);

        Span<float> angles = stackalloc float[3];

        for (var corner = 0; corner < 3; corner++) {
            var vertex = corners[corner];
            var direction = CrossField.Transport(field.Direction(vertex), mesh.VertexNormal(vertex), normal);

            if (direction.LengthSquared() <= 0f) {
                return 0;
            }

            angles[corner] = MathF.Atan2(Vector3.Dot(direction, bitangent), Vector3.Dot(direction, tangent));
        }

        var quarter = MathF.PI * 0.5f;
        var jumps = 0;

        for (var corner = 0; corner < 3; corner++) {
            var delta = angles[(corner + 1) % 3] - angles[corner];

            jumps += (int) MathF.Round(delta / quarter, MidpointRounding.AwayFromZero);
        }

        // ⚠ The period jumps are summed as integers rather than the residuals as floats, and the two
        // are the same number only in exact arithmetic. Three angles in one basis telescope to zero,
        // so the residuals determine the jumps — but going through them means three `atan2` results
        // and three subtractions of float error before a rounding that decides an integer.
        return -jumps;
    }

    /// <summary>Whether a singularity sits on the interior of a feature polyline.</summary>
    /// <param name="mesh">The surface.</param>
    /// <param name="features">The feature graph.</param>
    /// <param name="triangle">Which triangle.</param>
    /// <returns>Whether any of its three edges is a feature edge whose ends are not both corners.</returns>
    /// <remarks>
    ///     ⚠ <b>The interior of a chain, and never a corner, and a cube is the proof that the
    ///     difference is not pedantry.</b> § D6 step two says a singularity on a hard <i>edge</i> is a
    ///     visible pinch and the exit criterion is zero on features; § D6 step three says the right
    ///     place for one is where the surface is not developable and names "the corner of a box". On
    ///     <c>MeshShapes</c>' box every vertex is a feature corner and every edge is a feature edge, and
    ///     the eight quarter turns χ = 2 demands have nowhere else to be. Reading step two as "off
    ///     every feature vertex" makes a cube unsatisfiable; reading it as "off the runs between
    ///     corners" makes it exactly the topology an artist would draw.
    /// </remarks>
    public static bool OnFeatureLine(ManifoldMesh mesh, FeatureGraph features, int triangle) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(features);

        for (var side = 0; side < 3; side++) {
            var half = (triangle * 3) + side;

            if (!features.IsFeatureEdge(half)) {
                continue;
            }

            var from = mesh.Triangles[half];
            var to = mesh.Triangles[ManifoldMesh.Next(half)];

            if (!features.IsCorner(from) || !features.IsCorner(to)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs § D6's three corrections, in order.</summary>
    /// <param name="mesh">The surface.</param>
    /// <param name="settings">Adaptivity, guides and the rest.</param>
    /// <param name="features">The feature graph.</param>
    /// <param name="curvature">The curvature field, for the angle defect.</param>
    /// <param name="field">The solved field. Not modified.</param>
    /// <param name="cancelled">How many opposite pairs were removed.</param>
    /// <returns>The corrected field.</returns>
    public static CrossField Place(
        ManifoldMesh mesh,
        RemeshSettings settings,
        FeatureGraph features,
        CurvatureField curvature,
        CrossField field,
        out int cancelled
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(curvature);
        ArgumentNullException.ThrowIfNull(field);

        cancelled = 0;

        if (mesh.VertexCount == 0 || mesh.TriangleCount == 0) {
            return field;
        }

        var working = field.Directions.ToArray();

        // One — cancel adjacent opposite pairs, on the field as it stands and with the weights it
        // was solved under.
        var plain = Level(mesh, settings, features, curvature, null, working);

        cancelled = Cancel(mesh, plain, working);

        // Two — push off feature lines, by making the edges round them expensive to turn across.
        Correct(mesh, settings, features, curvature, working, Repulsion(mesh, features));

        // Three — attract to Gaussian curvature, by making the edges at a large angle defect cheap.
        // The repulsion stays in the product: a fingertip that is also a crease is still a crease.
        Correct(mesh, settings, features, curvature, working, Combined(mesh, features, curvature));

        return new(working);
    }

    /// <summary>One weighted correction, kept only if it improved the two things § D6 cares about.</summary>
    /// <remarks>
    ///     ⚠ <b>Every one of the three corrections is reverted when it does not help, and that is what
    ///     makes this pass safe to run unattended.</b> § D6 describes three rearrangements of where the
    ///     turning sits, and a rearrangement is a bet: releasing the edges at a large angle defect
    ///     lets a singularity move onto a fingertip, and it also lets a perfectly quiet region acquire
    ///     one. The score is lexicographic — first how many singularities sit on a feature line, then
    ///     how much turning there is in total — and the field is put back unless it went down or
    ///     stayed level on both. So <c>Place</c> is monotone: the field it returns is never worse than
    ///     the field it was given, on either measure.
    /// </remarks>
    static void Correct(
        ManifoldMesh mesh,
        RemeshSettings settings,
        FeatureGraph features,
        CurvatureField curvature,
        Vector3[] working,
        float[] stiffness
    ) {
        var saved = working.ToArray();
        var before = Score(mesh, features, new(working));

        Sweep(Level(mesh, settings, features, curvature, stiffness, working), PlacementIterations);

        var after = Score(mesh, features, new(working));

        if (after.OnFeature > before.OnFeature
            || (after.OnFeature == before.OnFeature && after.Turning > before.Turning)) {
            saved.CopyTo(working, 0);
        }
    }

    /// <summary>How many singularities sit on a feature line, and how much turning there is at all.</summary>
    static (int OnFeature, int Turning) Score(ManifoldMesh mesh, FeatureGraph features, CrossField field) {
        var onFeature = 0;
        var turning = 0;

        for (var triangle = 0; triangle < mesh.TriangleCount; triangle++) {
            var index = IndexOf(mesh, field, triangle);

            if (index == 0) {
                continue;
            }

            turning += Math.Abs(index);

            if (OnFeatureLine(mesh, features, triangle)) {
                onFeature++;
            }
        }

        return (onFeature, turning);
    }

    /// <summary>A level-zero graph over the mesh, with a chosen stiffness and the field already in it.</summary>
    static FieldLevel Level(
        ManifoldMesh mesh,
        RemeshSettings settings,
        FeatureGraph features,
        CurvatureField curvature,
        float[]? stiffness,
        Vector3[] directions
    ) => CrossFieldSolver.Base(mesh, settings, features, curvature, stiffness, directions);

    static void Sweep(FieldLevel level, int iterations) {
        for (var iteration = 0; iteration < iterations; iteration++) {
            for (var colour = 0; colour < level.Colours; colour++) {
                for (var at = level.ColourStarts[colour]; at < level.ColourStarts[colour + 1]; at++) {
                    level.Relax(level.ColourOrder[at]);
                }
            }
        }
    }

    /// <summary>Heavier edges within a few rings of a feature line — § D6's repulsion.</summary>
    static float[] Repulsion(ManifoldMesh mesh, FeatureGraph features) {
        var stiffness = new float[mesh.VertexCount];

        Array.Fill(stiffness, 1f);

        var depth = new int[mesh.VertexCount];

        Array.Fill(depth, int.MaxValue);

        var queue = new Queue<int>();

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
            // ⚠ Corners excluded, for the reason `OnFeatureLine` states: a corner is where a
            // singularity belongs, so stiffening round one pushes it away from the only place it can
            // legitimately go.
            if (features.IsFeatureVertex(vertex) && !features.IsCorner(vertex)) {
                depth[vertex] = 0;
                queue.Enqueue(vertex);
            }
        }

        while (queue.Count > 0) {
            var vertex = queue.Dequeue();

            if (depth[vertex] >= FeatureRings) {
                continue;
            }

            foreach (var neighbour in mesh.Ring(vertex)) {
                if (depth[neighbour] <= depth[vertex] + 1) {
                    continue;
                }

                depth[neighbour] = depth[vertex] + 1;
                queue.Enqueue(neighbour);
            }
        }

        for (var vertex = 0; vertex < stiffness.Length; vertex++) {
            if (depth[vertex] <= FeatureRings) {
                // Falling off with distance rather than a step, so the boundary of the stiffened
                // region is not itself a cheap ring the turning migrates into.
                stiffness[vertex] = 1f + ((FeatureStiffness - 1f) * (1f - ((float) depth[vertex] / (FeatureRings + 1))));
            }
        }

        return stiffness;
    }

    /// <summary>The repulsion times the release — § D6's second and third corrections at once.</summary>
    static float[] Combined(ManifoldMesh mesh, FeatureGraph features, CurvatureField curvature) {
        var stiffness = Repulsion(mesh, features);
        var quarter = MathF.PI * 0.5f;

        for (var vertex = 0; vertex < stiffness.Length; vertex++) {
            // ⚠ The angle defect and not the curvature magnitude, which is § D6's own wording: a
            // cylinder is curved and perfectly developable, so nothing about it should attract a
            // singularity. |K| against a quarter turn is the dimensionless measure of how far from
            // developable a vertex is, and it is already scale-free — an angle has no units.
            var defect = MathF.Min(1f, MathF.Abs(curvature.AngleDefect(vertex)) / quarter);

            stiffness[vertex] /= 1f + (CurvatureRelease * defect);
        }

        return stiffness;
    }

    /// <summary>Finds opposite pairs a few dual steps apart and re-smooths their neighbourhood away.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Reverted when it does not work, which is what makes § D6's "strictly better" a
    ///         statement rather than a hope.</b> Extra iterations on a patch can trade one pair for a
    ///         worse one somewhere else <i>in the same patch</i>, so checking only the two triangles
    ///         that were aimed at would report a cancellation that moved the problem three triangles
    ///         over. The measure is the total absolute index over every triangle the patch touches,
    ///         before and after, and the change is kept only when it went down.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Ascending over the singularity list, and the partner search breaks ties by index.</b>
    ///         Pairing by "nearest first" over the whole set would need a sort by a float distance, and
    ///         two equal distances would then be ordered by whatever the sort did with them.
    ///     </para>
    /// </remarks>
    static int Cancel(ManifoldMesh mesh, FieldLevel level, Vector3[] working) {
        var field = new CrossField(working);
        var found = Extract(mesh, field);

        if (found.Count < 2) {
            return 0;
        }

        var paired = new bool[found.Count];
        var cancelled = 0;

        for (var one = 0; one < found.Count; one++) {
            if (paired[one]) {
                continue;
            }

            var partner = Nearest(mesh, found, paired, one);

            if (partner < 0) {
                continue;
            }

            var (region, interior, touched) = Region(mesh, found[one].Triangle, found[partner].Triangle);
            var saved = new Vector3[region.Count];

            for (var at = 0; at < region.Count; at++) {
                saved[at] = working[region[at]];
            }

            var before = Turning(mesh, field, touched);

            // ⚠ The patch is re-seeded from its own rim before it is swept, and without that this
            // whole correction is a no-op. The field arrived here from a solve that had already
            // converged, so running the same operator over the same neighbourhood again reproduces
            // the same answer to the bit — the pair is a local minimum, not an accident. Flooding the
            // interior with the rim's direction throws the pair away and lets the smoothing decide
            // whether it comes back; if it was necessary it does, and the revert below keeps it.
            Reseed(mesh, level, interior, working);

            for (var iteration = 0; iteration < CancelIterations; iteration++) {
                foreach (var vertex in interior) {
                    level.Relax(vertex);
                }
            }

            if (Turning(mesh, field, touched) < before) {
                paired[one] = true;
                paired[partner] = true;
                cancelled++;

                continue;
            }

            for (var at = 0; at < region.Count; at++) {
                working[region[at]] = saved[at];
            }
        }

        return cancelled;
    }

    /// <summary>The total absolute index over a set of triangles, summed in index order.</summary>
    static int Turning(ManifoldMesh mesh, CrossField field, List<int> triangles) {
        var total = 0;

        foreach (var triangle in triangles) {
            total += Math.Abs(IndexOf(mesh, field, triangle));
        }

        return total;
    }

    /// <summary>The nearest unpaired singularity of the opposite sign, within <see cref="PairRadius" />.</summary>
    static int Nearest(ManifoldMesh mesh, List<FieldSingularity> found, bool[] paired, int from) {
        var reach = new Dictionary<int, int> { [found[from].Triangle] = 0 };
        var queue = new Queue<int>();

        queue.Enqueue(found[from].Triangle);

        while (queue.Count > 0) {
            var triangle = queue.Dequeue();
            var depth = reach[triangle];

            if (depth >= PairRadius) {
                continue;
            }

            for (var side = 0; side < 3; side++) {
                var other = mesh.Adjacent(triangle, side);

                if (other >= 0 && reach.TryAdd(other, depth + 1)) {
                    queue.Enqueue(other);
                }
            }
        }

        var best = -1;
        var bestDepth = int.MaxValue;

        // Ascending over the singularity list, so the nearest of two equally near partners is the
        // one with the lower index — which is a tie-break and not a preference.
        for (var to = 0; to < found.Count; to++) {
            if (to == from || paired[to] || found[to].Index != -found[from].Index) {
                continue;
            }

            if (reach.TryGetValue(found[to].Triangle, out var depth) && depth < bestDepth) {
                bestDepth = depth;
                best = to;
            }
        }

        return best;
    }

    /// <summary>Floods a patch's interior with its rim's direction, so the sweep starts from nothing.</summary>
    static void Reseed(ManifoldMesh mesh, FieldLevel level, List<int> interior, Vector3[] working) {
        var inside = new HashSet<int>(interior);
        var queue = new Queue<int>();
        var seeded = new HashSet<int>();

        // Ascending, so the flood front is entered in index order and two runs seed the same vertex
        // from the same neighbour.
        foreach (var vertex in interior) {
            foreach (var neighbour in mesh.Ring(vertex)) {
                if (!inside.Contains(neighbour) && seeded.Add(vertex)) {
                    working[vertex] = CrossField.Transport(
                        working[neighbour],
                        mesh.VertexNormal(neighbour),
                        mesh.VertexNormal(vertex)
                    );

                    queue.Enqueue(vertex);

                    break;
                }
            }
        }

        while (queue.Count > 0) {
            var vertex = queue.Dequeue();

            foreach (var neighbour in mesh.Ring(vertex)) {
                if (!inside.Contains(neighbour) || !seeded.Add(neighbour)) {
                    continue;
                }

                working[neighbour] = CrossField.Transport(
                    working[vertex],
                    mesh.VertexNormal(vertex),
                    mesh.VertexNormal(neighbour)
                );

                queue.Enqueue(neighbour);
            }
        }

        // A hard-constrained vertex is pinned whatever the flood said.
        foreach (var vertex in interior) {
            if (level.Hard[vertex]) {
                working[vertex] = level.HardDirection[vertex];
            }
        }
    }

    /// <summary>The patch, the part of it the sweep may move, and every triangle the two decide.</summary>
    /// <remarks>
    ///     ⚠ Three sets and not one. The <i>patch</i> is what is saved and restored; its
    ///     <i>interior</i> is what moves, because a rim held fixed is what stops a local correction
    ///     from being a global one; and the <i>triangles</i> are strictly more than the patch's own,
    ///     since a triangle one step outside still has a corner inside and its index changes when the
    ///     patch does. Measuring only the patch's own triangles is how a cancellation "succeeds" by
    ///     pushing a singularity over the patch's edge.
    /// </remarks>
    static (List<int> Patch, List<int> Interior, List<int> Triangles) Region(ManifoldMesh mesh, int one, int two) {
        var vertices = new HashSet<int>();
        var reach = new Dictionary<int, int> { [one] = 0, [two] = 0 };
        var queue = new Queue<int>();

        queue.Enqueue(one);
        queue.Enqueue(two);

        while (queue.Count > 0) {
            var triangle = queue.Dequeue();

            foreach (var corner in mesh.Corners(triangle)) {
                vertices.Add(corner);
            }

            if (reach[triangle] >= PairRadius) {
                continue;
            }

            for (var side = 0; side < 3; side++) {
                var other = mesh.Adjacent(triangle, side);

                if (other >= 0 && reach.TryAdd(other, reach[triangle] + 1)) {
                    queue.Enqueue(other);
                }
            }
        }

        var patch = new List<int>(vertices);

        patch.Sort();

        var interior = new List<int>();
        var triangles = new HashSet<int>();

        foreach (var vertex in patch) {
            var rim = false;

            foreach (var neighbour in mesh.Ring(vertex)) {
                if (!vertices.Contains(neighbour)) {
                    rim = true;

                    break;
                }
            }

            if (!rim) {
                interior.Add(vertex);
            }

            foreach (var half in mesh.Outgoing(vertex)) {
                triangles.Add(half / 3);
            }
        }

        var affected = new List<int>(triangles);

        affected.Sort();

        return (patch, interior, affected);
    }
}
