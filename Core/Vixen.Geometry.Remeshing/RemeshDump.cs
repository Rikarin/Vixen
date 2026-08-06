// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;

namespace Vixen.Geometry.Remeshing;

/// <summary>A straight run between two points, which is how a field and a feature chain are drawn.</summary>
/// <param name="From">Where it starts.</param>
/// <param name="To">Where it ends.</param>
public readonly record struct RemeshSegment(Vector3 From, Vector3 To);

/// <summary>One triangle of the conditioned surface, and which patch of the layout claimed it.</summary>
/// <param name="Triangle">Its index into <see cref="RemeshDump.Conditioned" />'s faces.</param>
/// <param name="Patch">Which patch, or <c>-1</c> where the partition claimed nothing.</param>
/// <remarks>
///     ⚠ <b>A patch index rather than a colour.</b> docs/plan/41 § D1 asks for the layout "as coloured
///     regions" and a colour is a viewer's decision — a palette baked in here would be one more thing to
///     disagree about, and it would throw away the identity a caller needs to point at a patch in the
///     report. The index is the artefact; colouring it is one modulus away.
/// </remarks>
public readonly record struct RemeshRegion(int Triangle, int Patch);

/// <summary>One arc of the partition, with the integer the quantizer gave it.</summary>
/// <param name="Arc">Its index in the layout.</param>
/// <param name="From">Where the arc starts, in world space.</param>
/// <param name="To">Where it ends.</param>
/// <param name="Quads">How many quads run along it.</param>
/// <param name="Target">How many the density field asked for, before rounding.</param>
/// <param name="IsFeature">Whether every one of its edges is a feature edge.</param>
/// <remarks>
///     ⚠ <b><see cref="Target" /> travels beside <see cref="Quads" /> because their difference is the
///     thing the quantization is <i>for</i>.</b> docs/plan/41 § D7's cost is the squared distance
///     between them summed over arcs, so an arc where they are far apart is where the consistency
///     system had to spend — and reading the integers alone tells you what happened without telling you
///     what it cost.
/// </remarks>
public readonly record struct RemeshArcLabel(
    int Arc,
    Vector3 From,
    Vector3 To,
    int Quads,
    float Target,
    bool IsFeature
);

/// <summary>Each of docs/plan/41 § D1's stages as an inspectable artefact, in memory.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D1 and § R4: when a remesh looks wrong, <i>which stage</i> is the first
///         question, and a monolith cannot answer it.</b> <see cref="RemeshReport" /> says which stage
///         was slow and which one dropped something; this says <i>what each one produced</i> — the
///         conditioned triangles, the field as a line set, the singularities it left, the layout as
///         regions and the quantization as a labelled graph.
///     </para>
///     <para>
///         ⚠ <b>It returns the data and never writes a file, and that is a rule rather than a
///         preference.</b> <c>Core/</c> is under the virtual-path rule: no <c>System.IO.Path</c>, no
///         <c>File</c>. Every field here is a plain array a caller turns into an <c>.obj</c>, a
///         <c>.ply</c>, a gizmo batch or an editor overlay, and the format that suits an editor is not
///         the one that suits a bug report.
///     </para>
///     <para>
///         ⚠ <b>It re-runs the stages rather than reaching into a remesh that already happened, and
///         that costs what a remesh costs.</b> The alternative is a capture hook threaded through
///         <see cref="Remesher" />, which would put a debugging concern in the middle of the pipeline
///         every caller pays for. Determinism is what makes re-running legitimate: § D14's gate is that
///         the same input and settings give the same answer, so the artefacts captured here are the
///         artefacts the remesh had. It stops before extraction, because the extraction's artefact is
///         the mesh <see cref="Remesher.Remesh" /> already returns.
///     </para>
/// </remarks>
/// <example>
///     <code>
/// var dump = RemeshDump.Capture(triangles, new RemeshSettings { TargetQuads = 5000 });
///
/// dump.Conditioned;      // stage ①, as a mesh
/// dump.Features;         // stage ②, as a line set
/// dump.Field;            // stage ③, as a cross per vertex
/// dump.Layout;           // stage ④, one region per conditioned triangle
/// dump.Quantization;     // stage ⑤, one label per arc
///     </code>
/// </example>
public sealed class RemeshDump {
    RemeshDump(
        EditMesh conditioned,
        RemeshSegment[] features,
        RemeshSegment[] field,
        Vector3[] singularities,
        RemeshRegion[] layout,
        RemeshArcLabel[] quantization,
        string[] warnings
    ) {
        Conditioned = conditioned;
        Features = features;
        Field = field;
        Singularities = singularities;
        Layout = layout;
        Quantization = quantization;
        Warnings = warnings;
    }

    /// <summary>How long a field cross's arms are, as a fraction of the bounding box's diagonal.</summary>
    /// <remarks>
    ///     ⚠ <b>A fraction and never a length</b>, which is the failure this repository has been bitten
    ///     by three times: a fixed number is a claim about how big a model is, and a millimetre-wide
    ///     part and a kilometre-wide one have to draw the same picture.
    /// </remarks>
    public const float CrossArm = 0.004f;

    /// <summary>Stage ① — the triangles conditioning produced, as a mesh.</summary>
    public EditMesh Conditioned { get; }

    /// <summary>Stage ② — every feature polyline, one segment per edge of every chain.</summary>
    public IReadOnlyList<RemeshSegment> Features { get; }

    /// <summary>Stage ③ — the 4-RoSy field, two crossed segments per vertex that has a tangent plane.</summary>
    /// <remarks>
    ///     ⚠ <b>A cross rather than an arrow, because a 4-RoSy field has no arrow.</b> The
    ///     representative direction and its quarter turn are the same field; drawing only the
    ///     representative invents a sign the solver never had, and the discontinuity that draws is a
    ///     rendering artefact somebody will spend an afternoon chasing as a solver bug.
    /// </remarks>
    public IReadOnlyList<RemeshSegment> Field { get; }

    /// <summary>Stage ③ — where the field's singularities landed, as triangle centroids.</summary>
    public IReadOnlyList<Vector3> Singularities { get; }

    /// <summary>Stage ④ — one entry per conditioned triangle, naming the patch that claimed it.</summary>
    public IReadOnlyList<RemeshRegion> Layout { get; }

    /// <summary>Stage ⑤ — one label per arc: where it runs, what it got and what it wanted.</summary>
    public IReadOnlyList<RemeshArcLabel> Quantization { get; }

    /// <summary>What the stages complained about, in the order they complained.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Runs stages ① to ⑤ and keeps what each one produced.</summary>
    /// <param name="source">The input, exactly as <see cref="Remesher.Remesh" /> would take it.</param>
    /// <param name="settings">The settings, exactly as <see cref="Remesher.Remesh" /> would take them.</param>
    /// <param name="scheduler">Workers for the field solve, or <see langword="null" /> for the calling thread.</param>
    /// <returns>The artefacts. A stage that refused leaves its own empty and says so in <see cref="Warnings" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> or <paramref name="settings" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A refusal is empty artefacts and a warning, never an exception</b> — the same contract
    ///     <see cref="Remesher.Remesh" /> holds to, and for the same reason: the input this library
    ///     exists for is the input that refuses, and a debugging facility that throws on it is a
    ///     debugging facility that is unavailable exactly when it is wanted.
    /// </remarks>
    public static RemeshDump Capture(EditMesh source, RemeshSettings settings, JobScheduler? scheduler = null) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        var warnings = new List<string>();
        var soup = TriangleSoup.From(source);
        var mesh = MeshConditioner.Condition(source, settings.Conditioning, out _, BaseLength(soup, settings));

        if (mesh.TriangleCount == 0) {
            warnings.Add("Conditioning left no triangles at all.");

            return new(new(), [], [], [], [], [], [.. warnings]);
        }

        var conditioned = mesh.ToEditMesh();
        var features = FeatureDetector.Detect(mesh, settings, FeatureCurves.All(source, settings));
        var curvature = CurvatureField.Build(mesh);
        var solved = CrossFieldSolver.Solve(mesh, settings, features, curvature, scheduler);
        var field = SingularityPass.Place(mesh, settings, features, curvature, solved, out _);
        var extracted = SingularityPass.Extract(mesh, field);
        var density = DensityField.Build(mesh, settings, features, curvature);
        var layout = PatchLayout.Build(mesh, field, features, density, extracted);

        warnings.AddRange(layout.Warnings);

        var quantization = layout.IsUsable ? Quantizer.Solve(layout) : null;

        if (quantization is not null) {
            warnings.AddRange(quantization.Warnings);
        } else {
            warnings.Add("The layout stage refused: no usable patch decomposition, so there is nothing to quantize.");
        }

        return new(
            conditioned,
            FeatureLines(mesh, features),
            FieldCrosses(mesh, field),
            SingularityPoints(mesh, extracted),
            Regions(mesh, layout),
            Labels(mesh, layout, quantization),
            [.. warnings]
        );
    }

    /// <summary>§ D9's base length, off the source's own area — the same formula stage ① is given.</summary>
    /// <remarks>
    ///     ⚠ Repeated from <see cref="Remesher" /> rather than shared, because sharing it would mean a
    ///     hook in the pipeline whose only caller is this file. The formula is one line and the comment
    ///     that matters — a quad of side <c>L</c> covers <c>L²</c> — is in both places.
    /// </remarks>
    static float BaseLength(TriangleSoup soup, RemeshSettings settings) {
        if (settings.TargetEdgeLength > 0f) {
            return settings.TargetEdgeLength;
        }

        var area = 0f;

        for (var triangle = 0; triangle < soup.TriangleCount; triangle++) {
            area += soup.Area(triangle);
        }

        return area > 0f ? MathF.Sqrt(area / Math.Max(settings.TargetQuads, 1)) : 0f;
    }

    static RemeshSegment[] FeatureLines(ManifoldMesh mesh, FeatureGraph features) {
        var segments = new List<RemeshSegment>();

        foreach (var polyline in features.Polylines) {
            for (var step = 0; step + 1 < polyline.Vertices.Length; step++) {
                segments.Add(
                    new(mesh.Positions[polyline.Vertices[step]], mesh.Positions[polyline.Vertices[step + 1]])
                );
            }
        }

        return [.. segments];
    }

    static RemeshSegment[] FieldCrosses(ManifoldMesh mesh, CrossField field) {
        var arm = CrossArm * mesh.Diagonal;
        var segments = new List<RemeshSegment>(field.Count * 2);

        for (var vertex = 0; vertex < field.Count; vertex++) {
            var direction = field.Direction(vertex);

            if (direction.LengthSquared() <= 0f) {
                continue;
            }

            var origin = mesh.Positions[vertex];
            var across = Vector3.Cross(mesh.VertexNormal(vertex), direction);

            segments.Add(new(origin - (arm * direction), origin + (arm * direction)));

            if (across.LengthSquared() > 0f) {
                segments.Add(new(origin - (arm * across), origin + (arm * across)));
            }
        }

        return [.. segments];
    }

    static Vector3[] SingularityPoints(ManifoldMesh mesh, List<FieldSingularity> extracted) {
        var points = new Vector3[extracted.Count];

        for (var index = 0; index < extracted.Count; index++) {
            var corners = mesh.Corners(extracted[index].Triangle);

            points[index] = (mesh.Positions[corners[0]] + mesh.Positions[corners[1]] + mesh.Positions[corners[2]])
                / 3f;
        }

        return points;
    }

    static RemeshRegion[] Regions(ManifoldMesh mesh, PatchLayout layout) {
        var regions = new RemeshRegion[mesh.TriangleCount];

        for (var triangle = 0; triangle < regions.Length; triangle++) {
            regions[triangle] = new(triangle, -1);
        }

        for (var patch = 0; patch < layout.Patches.Count; patch++) {
            foreach (var triangle in layout.Patches[patch].Triangles) {
                regions[triangle] = new(triangle, patch);
            }
        }

        return regions;
    }

    static RemeshArcLabel[] Labels(ManifoldMesh mesh, PatchLayout layout, Quantization? quantization) {
        var labels = new RemeshArcLabel[layout.Arcs.Count];

        for (var arc = 0; arc < labels.Length; arc++) {
            var chain = layout.Arcs[arc];

            labels[arc] = new(
                arc,
                mesh.Positions[chain.Vertices[0]],
                mesh.Positions[chain.Vertices[^1]],
                quantization is not null ? quantization.Counts[arc] : 0,
                chain.Target,
                chain.IsFeature
            );
        }

        return labels;
    }
}
