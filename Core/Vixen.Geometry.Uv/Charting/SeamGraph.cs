// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv.Charting;

/// <summary>The mesh as a graph a seam is a walk on, with § D4's seven-term cost on every edge.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D4, taken from MeshTailor's representational point directly: <b>seam
///         candidates are paths of existing edges</b>, found by search on the mesh's own graph under an
///         edge cost, never by placing a curve in space and snapping it. There is no snapping stage
///         here, so there are no snapping artefacts — which is the same lesson doc 41 § D4 reached from
///         the other side, that a feature reproduced by construction beats one approximated and then
///         snapped.
///     </para>
///     <para>
///         ⚠ <b>Every quantity here is either dimensionless or a length, and that is load-bearing.</b>
///         The six quality terms are normalized to <c>[0, 1]</c> and the seventh <i>is</i> the length,
///         so <see cref="Cut" /> scales exactly linearly with the model and every comparison made
///         against it is scale-free. An absolute epsilon anywhere in the construction would make the
///         seams a statement about how big the model happened to be — which this repository has been
///         bitten by three times in one day, most recently in <c>EditMesh.Normal</c>, where
///         <c>Vector3.Normalize</c>'s absolute <c>MathUtil.ZeroTolerance</c> of <c>1e-6</c> met a cross
///         product that scales as the <i>square</i> of the model. Nothing below calls it: every
///         direction is divided by its own length in <see langword="double" />, and every tolerance is
///         a fraction of <see cref="Diagonal" />.
///     </para>
///     <para>
///         ⚠ <b>An edge carrying anything other than exactly two faces is not a dual link.</b> A
///         boundary edge has nowhere to go and a non-manifold edge has no consistent answer to "which
///         face is on the other side", so neither joins two faces in the dual graph — which makes both
///         of them a free cut rather than a case the charter has to reason about. A region grown over
///         this graph therefore cannot contain a non-manifold edge that it reached <i>through</i>, and
///         the bowtie <c>ChartMesh</c> refuses cannot be assembled by growth at all.
///     </para>
/// </remarks>
sealed class SeamGraph {
    /// <summary>How many directions the occlusion estimate casts per face.</summary>
    /// <remarks>
    ///     Sixteen is enough to separate "inside a crevice" from "on an outer wall", which is all the
    ///     visibility term is asked to do — it is one of seven and its default weight is 0.75. The
    ///     estimate is per face and computed once for the mesh, so the recursion pays for it once
    ///     however many times it splits.
    /// </remarks>
    const int OcclusionRays = 16;

    /// <summary>How far off the surface a visibility ray starts, as a fraction of the bounds diagonal.</summary>
    /// <remarks>
    ///     ⚠ <b>A fraction, and not a constant.</b> An absolute offset is below the surface on a model
    ///     in millimetres and above the next wall on one in kilometres, and the symptom either way is an
    ///     occlusion term that quietly reads zero.
    /// </remarks>
    const double RayOffset = 1e-4;

    /// <summary>How big the mesh is made before a ray is cast at it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The visibility rays are cast at a rescaled copy of the mesh, and that is a workaround
    ///         for an absolute tolerance one assembly down rather than a modelling choice.</b>
    ///         <see cref="TriangleTree.Raycast" /> rejects a triangle whose Möller–Trumbore determinant
    ///         falls below <c>MathUtil.ZeroTolerance</c> — an absolute <c>1e-6</c> — and that determinant
    ///         is <c>edge · (direction × edge)</c>, which for a unit direction scales as the <b>square</b>
    ///         of the model. So the same mesh at a thousandth of its size has determinants a millionth
    ///         the size, every ray silently misses, the occlusion term reads zero, and <b>the charter
    ///         cuts somewhere else</b>. Measured: five of this corpus's nine shapes charted differently
    ///         at 1/1024 scale, and a power of two is exact everywhere else in the seam cost, so nothing
    ///         but an absolute epsilon could have done it.
    ///     </para>
    ///     <para>
    ///         This is the third time this repository has been bitten by <c>ZeroTolerance</c> meeting a
    ///         cross product — <c>EditMesh.Normal</c> and <c>ManifoldMesh.TriangleNormal</c> were the
    ///         first two. The fix belongs in <c>TriangleTree</c>, whose test should be relative to the
    ///         triangle's own scale; <c>Vixen.Core.Mathematics</c> is owned by other work, so the mesh is
    ///         brought to a fixed size here instead and the rays are cast in that space.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A power of two, so the workaround does not itself break the invariance it exists to
    ///         restore.</b> Scaling by <c>1024/diagonal</c> where the diagonal has itself scaled by a
    ///         power of two leaves every mantissa untouched, so the normalized copy is bit-identical at
    ///         any power-of-two input scale. A round decimal here would have traded one scale dependency
    ///         for a subtler one.
    ///     </para>
    /// </remarks>
    const float RayScale = 1024f;

    /// <summary>How near a reflected position must land to count as a mirror, over the diagonal.</summary>
    const double MirrorTolerance = 1e-3;

    /// <summary>What fraction of positions must have a mirror before a plane counts as one.</summary>
    const double MirrorAgreement = 0.98;

    /// <summary>The smallest a barrier can be, so that a plain geodesic survives every weight at zero.</summary>
    /// <remarks>
    ///     ⚠ <b>A zero that would otherwise mean "off".</b> With every <see cref="SeamCost" /> weight at
    ///     zero the barrier metric would be identically zero, every face would sit at distance zero from
    ///     every seed, and the bisection would return whatever the tie-break said rather than a
    ///     bisection. The floor makes the degenerate setting fall back to a pure geodesic split, which
    ///     is a defensible answer to "cut this in half and tell me nothing about where".
    /// </remarks>
    const double BarrierFloor = 1d;

    /// <summary>The mesh this was built over.</summary>
    public required EditMesh Mesh { get; init; }

    /// <summary>How many faces it has.</summary>
    public required int FaceCount { get; init; }

    /// <summary>Each face's centroid.</summary>
    public required Vector3[] Centroids { get; init; }

    /// <summary>Each face's unit normal, or zero for a face with no area.</summary>
    public required Vector3[] Normals { get; init; }

    /// <summary>Each face's area, in world units squared.</summary>
    public required double[] Areas { get; init; }

    /// <summary>The whole mesh's area, which is what a normalized seam length is divided by.</summary>
    public required double SurfaceArea { get; init; }

    /// <summary>The bounding box's diagonal — the length every relative tolerance is a fraction of.</summary>
    public required double Diagonal { get; init; }

    /// <summary>Each face's dual neighbours, as a run into <see cref="Links" />.</summary>
    public required int[] LinkStart { get; init; }

    /// <summary>The neighbouring face of each dual link, ascending within each run.</summary>
    public required int[] Links { get; init; }

    /// <summary>Which mesh edge each dual link crosses, parallel to <see cref="Links" />.</summary>
    public required int[] LinkEdges { get; init; }

    /// <summary>Each mesh edge's length.</summary>
    public required double[] EdgeLengths { get; init; }

    /// <summary>What cutting each mesh edge costs. Lower is a better seam.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>length · (w_L + Σ w_i · (1 − t_i))</c> over the six quality terms. An edge with every
    ///         desirable property costs its length times <see cref="SeamCost.Length" /> alone; an edge
    ///         with none costs its length times every weight there is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That is what makes <see cref="SeamCost.Length" /> the term everything else is traded
    ///         against, in the arithmetic rather than only in the documentation.</b> Raise it and the
    ///         quality terms shrink relative to it, so a shortest-path search takes the short way round
    ///         a feature it should have followed; drop it to zero and length stops being paid for at
    ///         all, so a seam wanders through flat regions collecting a fractional saving each time.
    ///     </para>
    /// </remarks>
    public required double[] Cut { get; init; }

    /// <summary>How strongly a region growth avoids crossing each mesh edge.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>|c₂ − c₁| · (floor + Σ w_i · t_i)</c> — the mirror of <see cref="Cut" />. A good seam is
    ///         a <i>barrier</i> to growth, so two regions grown from opposite ends of a part meet along
    ///         one, which is the Seamster family's whole idea: route the cut where a discontinuity does
    ///         not read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The step is the distance between the two <i>centroids</i>, and using the shared
    ///         edge's length instead is a bug that reads as plausible right up until it is measured.</b>
    ///         An edge's length is a <i>cut capacity</i> — how much it costs to sever — and a traversal
    ///         metric wants a <i>distance</i>. On a surface of revolution the two are close to opposite:
    ///         a narrow waist has short circumferential edges, so crossing it looks <b>cheap</b> under
    ///         edge length while being the longest way round under any real geodesic. Measured on this
    ///         corpus's dumbbell, that inversion put the cut lengthwise down the model instead of round
    ///         its waist, and 15 % of the seam landed where 100 % belonged.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="SeamCost.Length" /> is deliberately absent from this one.</b> Preferring a
    ///         <i>short</i> seam says nothing about whether a given edge is a wall — it is a property of
    ///         a whole path — so it is paid for where whole paths are compared, which is
    ///         <see cref="Cut" />-scored candidate selection, and not here.
    ///     </para>
    /// </remarks>
    public required double[] Barrier { get; init; }

    /// <summary>The lower-numbered face of each mesh edge's dual link, or <c>-1</c> when it has none.</summary>
    public required int[] EdgeFaceA { get; init; }

    /// <summary>The higher-numbered one.</summary>
    public required int[] EdgeFaceB { get; init; }

    /// <summary>Builds the graph, which is everything the charter needs that is a function of the mesh alone.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="settings">Where the seam weights and the feature angle come from.</param>
    /// <returns>The graph.</returns>
    /// <remarks>
    ///     ⚠ <b>Once per mesh, never per region.</b> The occlusion estimate casts
    ///     <see cref="OcclusionRays" /> rays per face against a tree over the whole mesh, and the
    ///     recursion evaluates a region many times — so building this inside the loop would multiply the
    ///     one genuinely expensive part of charting by the depth of the recursion, to arrive at exactly
    ///     the same numbers.
    /// </remarks>
    public static SeamGraph Build(EditMesh mesh, UvSettings settings) {
        var faces = mesh.FaceCount;
        var centroids = new Vector3[faces];
        var normals = new Vector3[faces];
        var areas = new double[faces];
        var surface = 0d;

        for (var face = 0; face < faces; face++) {
            var loop = mesh.CornersOf(face);

            double cx = 0d, cy = 0d, cz = 0d;
            double nx = 0d, ny = 0d, nz = 0d;

            for (var corner = 0; corner < loop.Length; corner++) {
                var a = mesh.Positions[loop[corner]];
                var b = mesh.Positions[loop[(corner + 1) % loop.Length]];

                cx += a.X;
                cy += a.Y;
                cz += a.Z;

                // Newell's sum, which is the only face normal that is correct for a non-planar n-gon
                // and is what EditMesh's own normal uses.
                nx += (a.Y - (double)b.Y) * (a.Z + (double)b.Z);
                ny += (a.Z - (double)b.Z) * (a.X + (double)b.X);
                nz += (a.X - (double)b.X) * (a.Y + (double)b.Y);
            }

            centroids[face] = new((float)(cx / loop.Length), (float)(cy / loop.Length), (float)(cz / loop.Length));

            var length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));

            areas[face] = 0.5d * length;
            surface += areas[face];

            // ⚠ Divided by its own length rather than handed to Vector3.Normalize, whose absolute
            // MathUtil.ZeroTolerance of 1e-6 is a length being compared against a quantity that scales
            // as the square of the model. docs/plan/42 § Part 5's U2 records the same fix in EditMesh.
            normals[face] = length > 0d
                ? new((float)(nx / length), (float)(ny / length), (float)(nz / length))
                : Vector3.Zero;
        }

        var bounds = mesh.Bounds;
        var span = bounds.Maximum - bounds.Minimum;
        var diagonal = Math.Sqrt(
            ((double)span.X * span.X) + ((double)span.Y * span.Y) + ((double)span.Z * span.Z)
        );

        if (!(diagonal > 0d)) {
            diagonal = 1d;
        }

        var exposure = Exposure(mesh, centroids, normals, bounds, diagonal);
        var mirrors = Mirrors(mesh, bounds, diagonal);

        var edges = mesh.Edges;
        var lengths = new double[edges.Count];
        var cut = new double[edges.Count];
        var barrier = new double[edges.Count];
        var faceA = new int[edges.Count];
        var faceB = new int[edges.Count];
        var degree = new int[faces + 1];

        var cost = settings.SeamCost;
        var featureAngle = Math.Max(1e-3d, settings.FeatureAngle) * Math.PI / 180d;

        for (var edge = 0; edge < edges.Count; edge++) {
            var ends = edges[edge];
            var a = mesh.Positions[ends.A];
            var b = mesh.Positions[ends.B];

            double dx = b.X - (double)a.X, dy = b.Y - (double)a.Y, dz = b.Z - (double)a.Z;

            lengths[edge] = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

            var incident = mesh.FacesOf(edge);

            faceA[edge] = -1;
            faceB[edge] = -1;

            if (incident.Length != 2 || incident[0] == incident[1]) {
                // Not a dual link. It is already a cut, so it costs nothing to keep cutting it and it
                // is no barrier to a growth that can never cross it.
                continue;
            }

            var left = Math.Min(incident[0], incident[1]);
            var right = Math.Max(incident[0], incident[1]);

            faceA[edge] = left;
            faceB[edge] = right;
            degree[left + 1]++;
            degree[right + 1]++;

            var quality = Terms(
                mesh,
                centroids,
                normals,
                exposure,
                mirrors,
                bounds,
                diagonal,
                featureAngle,
                edge,
                ends,
                left,
                right
            );

            var missing = (cost.Concavity * (1d - quality.Concavity))
                + (cost.Visibility * (1d - quality.Visibility))
                + (cost.Feature * (1d - quality.Feature))
                + (cost.Material * (1d - quality.Material))
                + (cost.Symmetry * (1d - quality.Symmetry))
                + (cost.Existing * (1d - quality.Existing));

            var present = (cost.Concavity * quality.Concavity)
                + (cost.Visibility * quality.Visibility)
                + (cost.Feature * quality.Feature)
                + (cost.Material * quality.Material)
                + (cost.Symmetry * quality.Symmetry)
                + (cost.Existing * quality.Existing);

            cut[edge] = lengths[edge] * (Math.Max(0d, cost.Length) + Math.Max(0d, missing));
            barrier[edge] = quality.Reach * (BarrierFloor + Math.Max(0d, present));
        }

        for (var face = 0; face < faces; face++) {
            degree[face + 1] += degree[face];
        }

        var links = new int[degree[faces]];
        var linkEdges = new int[degree[faces]];
        var filled = new int[faces];

        for (var edge = 0; edge < edges.Count; edge++) {
            if (faceA[edge] < 0) {
                continue;
            }

            var left = faceA[edge];
            var right = faceB[edge];

            links[degree[left] + filled[left]] = right;
            linkEdges[degree[left] + filled[left]] = edge;
            filled[left]++;

            links[degree[right] + filled[right]] = left;
            linkEdges[degree[right] + filled[right]] = edge;
            filled[right]++;
        }

        // Ascending neighbour order within each run, so that every walk over this graph visits it in
        // an order that is a function of the mesh rather than of the edge table's construction.
        for (var face = 0; face < faces; face++) {
            var start = degree[face];
            var count = degree[face + 1] - start;

            for (var i = 1; i < count; i++) {
                var neighbour = links[start + i];
                var through = linkEdges[start + i];
                var slot = i - 1;

                while (slot >= 0 && links[start + slot] > neighbour) {
                    links[start + slot + 1] = links[start + slot];
                    linkEdges[start + slot + 1] = linkEdges[start + slot];
                    slot--;
                }

                links[start + slot + 1] = neighbour;
                linkEdges[start + slot + 1] = through;
            }
        }

        return new() {
            Mesh = mesh,
            FaceCount = faces,
            Centroids = centroids,
            Normals = normals,
            Areas = areas,
            SurfaceArea = surface,
            Diagonal = diagonal,
            LinkStart = degree,
            Links = links,
            LinkEdges = linkEdges,
            EdgeLengths = lengths,
            Cut = cut,
            Barrier = barrier,
            EdgeFaceA = faceA,
            EdgeFaceB = faceB
        };
    }

    /// <summary>The six dimensionless terms for one edge, and the geodesic step across it.</summary>
    readonly record struct Quality(
        double Concavity,
        double Visibility,
        double Feature,
        double Material,
        double Symmetry,
        double Existing,
        double Reach
    );

    /// <summary>Measures § D4's six quality terms on one edge.</summary>
    /// <remarks>
    ///     ⚠ <b>Concavity is read from the neighbour's centroid rather than from the sign of a cross
    ///     product.</b> A crease's sign depends on which way round the shared edge is walked, and the
    ///     edge table stores it one way for both faces — so a cross-product sign answers a question
    ///     about the edge table. Whether the neighbour's centroid sits in front of this face's outward
    ///     plane is a question about the surface, and it is the test the approximate-convex-decomposition
    ///     literature § D3 names uses.
    /// </remarks>
    static Quality Terms(
        EditMesh mesh,
        Vector3[] centroids,
        Vector3[] normals,
        double[] exposure,
        bool[] mirrors,
        BoundingBox bounds,
        double diagonal,
        double featureAngle,
        int edge,
        MeshEdge ends,
        int left,
        int right
    ) {
        var nl = normals[left];
        var nr = normals[right];

        var dot = Math.Clamp(((double)nl.X * nr.X) + ((double)nl.Y * nr.Y) + ((double)nl.Z * nr.Z), -1d, 1d);
        var angle = Math.Acos(dot);

        var toward = centroids[right] - centroids[left];
        var reach = Math.Sqrt(
            ((double)toward.X * toward.X) + ((double)toward.Y * toward.Y) + ((double)toward.Z * toward.Z)
        );

        var lean = reach > 0d
            ? (((double)nl.X * toward.X) + ((double)nl.Y * toward.Y) + ((double)nl.Z * toward.Z)) / reach
            : 0d;

        // Positive when the neighbour is in front of this face's outward plane, which is a valley.
        var concavity = Math.Clamp(lean, 0d, 1d);
        var feature = Math.Clamp(angle / featureAngle, 0d, 1d);
        var visibility = 1d - (0.5d * (exposure[left] + exposure[right]));
        var material = mesh.Faces[left].Group != mesh.Faces[right].Group ? 1d : 0d;
        var symmetry = OnMirror(mesh, mirrors, bounds, diagonal, ends.A) && OnMirror(mesh, mirrors, bounds, diagonal, ends.B)
            ? 1d
            : 0d;

        return new(
            concavity,
            visibility,
            feature,
            material,
            symmetry,
            Existing(mesh, edge, ends, left, right),
            reach
        );
    }

    /// <summary>Whether the mesh's own coordinates already disagree across an edge.</summary>
    /// <remarks>
    ///     docs/plan/42 § D4's last term, and it is the one that makes re-unwrapping a mesh that had
    ///     coordinates cheap: a seam that was already there costs nothing new, because the discontinuity
    ///     is already in the asset and an artist has already looked at it.
    /// </remarks>
    static double Existing(EditMesh mesh, int edge, MeshEdge ends, int left, int right) {
        if (mesh.TexCoords.Length != mesh.CornerCount) {
            return 0d;
        }

        return Splits(mesh, ends.A, left, right) || Splits(mesh, ends.B, left, right) ? 1d : 0d;
    }

    /// <summary>Whether two faces carry different coordinates at one shared position.</summary>
    static bool Splits(EditMesh mesh, int position, int left, int right) {
        var one = CoordinateAt(mesh, left, position);
        var other = CoordinateAt(mesh, right, position);

        if (one is null || other is null) {
            return false;
        }

        return one.Value != other.Value;
    }

    /// <summary>The coordinate a face carries at a position, or null when it does not reach it.</summary>
    static Vector2? CoordinateAt(EditMesh mesh, int face, int position) {
        var entry = mesh.Faces[face];
        var loop = mesh.CornersOf(face);

        for (var corner = 0; corner < loop.Length; corner++) {
            if (loop[corner] == position) {
                return mesh.TexCoords[entry.Start + corner];
            }
        }

        return null;
    }

    /// <summary>How much of the sky each face can see, as a fraction in <c>[0, 1]</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/42 § D4's visibility term, which is Seamster's inconspicuousness: a seam that
    ///         nobody can see costs nothing to have. It is estimated by ambient occlusion over the
    ///         surface, which is <see cref="TriangleTree.Raycast" />'s job.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An open patch reads as fully exposed and that is the right answer, not a failure.</b>
    ///         Nothing occludes a hemisphere from outside, so the term contributes nothing and the other
    ///         six decide — which is what should happen on a surface with no crevices to hide a seam in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The shape diameter function § D3 names beside concavity is <i>not</i> here, and the
    ///         reason is a missing return value rather than a judgement.</b> An SDF needs the
    ///         <i>distance</i> to the surface an inward ray strikes, and <c>TriangleTree.Raycast</c>
    ///         reports whether it hit and which side it hit, not how far — and
    ///         <c>Vixen.Core.Mathematics</c> is owned by other work this phase may not touch. Occlusion
    ///         and concavity carry the decomposition in its place; a thickness term is the upgrade a
    ///         distance-returning raycast would justify.
    ///     </para>
    /// </remarks>
    static double[] Exposure(
        EditMesh mesh,
        Vector3[] centroids,
        Vector3[] normals,
        BoundingBox bounds,
        double diagonal
    ) {
        var exposure = new double[mesh.FaceCount];
        var triangles = mesh.Triangulate();

        if (triangles.Length == 0) {
            Array.Fill(exposure, 1d);

            return exposure;
        }

        // The rescaled copy the rays are actually cast at — see RayScale for why there is one.
        var centre = (bounds.Minimum + bounds.Maximum) * 0.5f;
        var factor = RayScale / (float)diagonal;
        var scaled = new Vector3[mesh.PositionCount];

        for (var position = 0; position < scaled.Length; position++) {
            scaled[position] = (mesh.Positions[position] - centre) * factor;
        }

        var tree = new TriangleTree(scaled, triangles);
        var offset = (float)(RayScale * RayOffset);

        for (var face = 0; face < mesh.FaceCount; face++) {
            var normal = normals[face];

            if (normal == Vector3.Zero) {
                exposure[face] = 1d;

                continue;
            }

            // A frame from the axis the normal leans on least, so the cross product it is built from is
            // never near zero — its magnitude is at least 1/√3 for any unit normal. No epsilon needed,
            // which is the point of choosing the axis rather than fixing one.
            var away = Math.Abs(normal.X) <= Math.Abs(normal.Y) && Math.Abs(normal.X) <= Math.Abs(normal.Z)
                ? new Vector3(1f, 0f, 0f)
                : Math.Abs(normal.Y) <= Math.Abs(normal.Z)
                    ? new Vector3(0f, 1f, 0f)
                    : new Vector3(0f, 0f, 1f);

            var tangent = Unit(Vector3.Cross(normal, away));
            var bitangent = Unit(Vector3.Cross(normal, tangent));
            var origin = ((centroids[face] - centre) * factor) + (normal * offset);
            var escaped = 0;

            for (var ray = 0; ray < OcclusionRays; ray++) {
                var sample = Hemisphere[ray];
                var direction = (tangent * sample.X) + (bitangent * sample.Y) + (normal * sample.Z);

                if (!tree.Raycast(origin, direction, out _)) {
                    escaped++;
                }
            }

            exposure[face] = escaped / (double)OcclusionRays;
        }

        return exposure;
    }

    /// <summary>A vector divided by its own length, in <see langword="double" />, or zero.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>Vector3.Normalize</c>, and that is the third time in this repository.</b> It
    ///     gives up below an absolute <c>MathUtil.ZeroTolerance</c> of <c>1e-6</c>, which is a length
    ///     being asked about a quantity that scales as the square of the model — so the same mesh in
    ///     metres and in millimetres gets two different answers, and one of them is silently zero.
    /// </remarks>
    static Vector3 Unit(Vector3 value) {
        var length = Math.Sqrt(
            ((double)value.X * value.X) + ((double)value.Y * value.Y) + ((double)value.Z * value.Z)
        );

        return length > 0d
            ? new((float)(value.X / length), (float)(value.Y / length), (float)(value.Z / length))
            : Vector3.Zero;
    }

    /// <summary>Which of the three axis planes through the bounding box's centre the mesh mirrors in.</summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/42 § D4's symmetry term, and § D10's reason for wanting it: a seam on the mirror
    ///         plane makes the two halves' seams agree exactly, which is what lets the halves later be
    ///         stacked on one region of texture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The three world axes and not a search for an arbitrary plane.</b> Doc 41 § D11's
    ///         exact symmetry — where a mirrored vertex is an exact negation — is what makes detection
    ///         reliable, and an authored asset that is symmetric is very nearly always symmetric about a
    ///         world axis. Hunting a general plane is a fitting problem whose failure mode is a plane
    ///         that is <i>nearly</i> right, and a nearly-right mirror term is worse than none.
    ///     </para>
    /// </remarks>
    static bool[] Mirrors(EditMesh mesh, BoundingBox bounds, double diagonal) {
        var mirrors = new bool[3];

        if (mesh.PositionCount == 0) {
            return mirrors;
        }

        var cell = diagonal * MirrorTolerance;
        var buckets = new Dictionary<(long, long, long), List<int>>();

        for (var position = 0; position < mesh.PositionCount; position++) {
            var key = Cell(mesh.Positions[position], cell);

            if (!buckets.TryGetValue(key, out var slot)) {
                buckets[key] = slot = [];
            }

            slot.Add(position);
        }

        var centre = (bounds.Minimum + bounds.Maximum) * 0.5f;

        for (var axis = 0; axis < 3; axis++) {
            var matched = 0;

            for (var position = 0; position < mesh.PositionCount; position++) {
                if (HasMirror(mesh, buckets, cell, centre, axis, mesh.Positions[position])) {
                    matched++;
                }
            }

            mirrors[axis] = matched >= (int)Math.Ceiling(MirrorAgreement * mesh.PositionCount);
        }

        return mirrors;
    }

    static bool HasMirror(
        EditMesh mesh,
        Dictionary<(long, long, long), List<int>> buckets,
        double cell,
        Vector3 centre,
        int axis,
        Vector3 position
    ) {
        var reflected = Reflect(centre, axis, position);
        var key = Cell(reflected, cell);

        for (var dx = -1; dx <= 1; dx++) {
            for (var dy = -1; dy <= 1; dy++) {
                for (var dz = -1; dz <= 1; dz++) {
                    if (!buckets.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var slot)) {
                        continue;
                    }

                    foreach (var candidate in slot) {
                        if (Vector3.Distance(mesh.Positions[candidate], reflected) <= cell) {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    static Vector3 Reflect(Vector3 centre, int axis, Vector3 position) =>
        axis switch {
            0 => new((2f * centre.X) - position.X, position.Y, position.Z),
            1 => new(position.X, (2f * centre.Y) - position.Y, position.Z),
            _ => new(position.X, position.Y, (2f * centre.Z) - position.Z)
        };

    static (long, long, long) Cell(Vector3 position, double cell) =>
        ((long)Math.Floor(position.X / cell), (long)Math.Floor(position.Y / cell), (long)Math.Floor(position.Z / cell));

    /// <summary>Whether a position lies on any detected mirror plane.</summary>
    static bool OnMirror(EditMesh mesh, bool[] mirrors, BoundingBox bounds, double diagonal, int position) {
        var point = mesh.Positions[position];
        var centre = (bounds.Minimum + bounds.Maximum) * 0.5f;
        var tolerance = diagonal * MirrorTolerance;

        for (var axis = 0; axis < 3; axis++) {
            if (!mirrors[axis]) {
                continue;
            }

            var offset = axis switch {
                0 => Math.Abs(point.X - (double)centre.X),
                1 => Math.Abs(point.Y - (double)centre.Y),
                _ => Math.Abs(point.Z - (double)centre.Z)
            };

            if (offset <= tolerance) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sixteen cosine-weighted directions in the tangent frame's <c>+z</c> hemisphere.</summary>
    /// <remarks>
    ///     A Hammersley set rather than anything random: the visibility term has to be a function of the
    ///     mesh alone, and docs/plan/42 § B6 rules out a random restart everywhere in this library for
    ///     the same reason it rules out a metaheuristic packer.
    /// </remarks>
    static readonly Vector3[] Hemisphere = BuildHemisphere();

    static Vector3[] BuildHemisphere() {
        var directions = new Vector3[OcclusionRays];

        for (var index = 0; index < OcclusionRays; index++) {
            var u = (index + 0.5d) / OcclusionRays;
            var v = Radical(index);

            var radius = Math.Sqrt(u);
            var angle = 2d * Math.PI * v;

            directions[index] = new(
                (float)(radius * Math.Cos(angle)),
                (float)(radius * Math.Sin(angle)),
                (float)Math.Sqrt(Math.Max(0d, 1d - u))
            );
        }

        return directions;
    }

    /// <summary>The van der Corput radical inverse in base two.</summary>
    static double Radical(int index) {
        var bits = (uint)index;

        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);

        return bits * 2.3283064365386963e-10d;
    }
}
