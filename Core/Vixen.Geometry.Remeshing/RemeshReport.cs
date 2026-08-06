// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Remeshing;

/// <summary>Which of the seven stages a timing, a warning or a refusal belongs to.</summary>
/// <remarks>
///     <b>The stage boundaries are the debugging surface and that is deliberate.</b> docs/plan/41
///     § D1: when a remesh looks wrong, <i>which stage</i> is the first question, and a monolith
///     cannot answer it. Every stage can dump its artefact — the conditioned triangles, the field as
///     a line set, the layout as coloured regions, the quantization as a labelled graph.
/// </remarks>
public enum RemeshStage : byte {
    /// <summary>Weld, orient, de-speck, repair, isotropic pre-remesh.</summary>
    Condition,

    /// <summary>Dihedral angles, creases, groups, seams and guides, chained into polylines.</summary>
    Features,

    /// <summary>The 4-RoSy cross field, its hierarchy, and where the singularities landed.</summary>
    Field,

    /// <summary>Separatrix tracing and the patch decomposition.</summary>
    Layout,

    /// <summary>How many quads each patch side gets, solved as a flow.</summary>
    Quantize,

    /// <summary>Per-patch grids, stitched, relaxed and validated.</summary>
    Extract,

    /// <summary>Normals, coordinates, colours, materials, weights and the baked maps.</summary>
    Transfer
}

/// <summary>How long a stage took and how much it handled.</summary>
/// <param name="Stage">Which stage.</param>
/// <param name="Elapsed">How long it took.</param>
/// <param name="Elements">What it handled — triangles, vertices, patches, depending on the stage.</param>
public readonly record struct RemeshStageTiming(RemeshStage Stage, TimeSpan Elapsed, int Elements);

/// <summary>A vertex whose one-ring does not close into four quarter turns, and by how much.</summary>
/// <param name="Position">Which output position.</param>
/// <param name="Valence">How many quads meet there. Four is regular.</param>
/// <param name="Index">The accumulated rotation in quarter turns: <c>+1</c> at a valence-3, <c>-1</c> at a valence-5.</param>
public readonly record struct Singularity(int Position, int Valence, int Index);

/// <summary>What conditioning changed, so a caller can refuse to continue on it.</summary>
/// <param name="Welded">Positions merged into one.</param>
/// <param name="Reoriented">Faces whose winding was flipped to agree with their neighbours.</param>
/// <param name="Unorientable">Components whose orientation could not be made consistent — flagged, not fixed.</param>
/// <param name="Despecked">Connected components dropped for being too small a fraction of the largest.</param>
/// <param name="Cut">Non-manifold edges split so that each side is manifold.</param>
/// <param name="Filled">Holes closed.</param>
/// <param name="Shrinkwrapped">Whether the voxel escape hatch fired.</param>
/// <param name="Triangles">How many triangles came out.</param>
/// <param name="Mesh">Doc 24's report on the conditioned surface, unchanged.</param>
/// <remarks>
///     <para>
///         <b>Conditioning is a stage with a report, not hygiene.</b> docs/plan/41 § D3 and § B1: a
///         remesher that assumes a clean manifold is a remesher that does not run on the input this
///         library exists for.
///     </para>
///     <para>
///         ⚠ <b>Non-manifold edges are repaired by <i>cutting</i>, not by merging.</b> An edge with
///         three or more faces is split so each side is manifold. Cutting keeps the geometry and
///         costs a seam; merging invents a surface that was never there.
///     </para>
///     <para>
///         ⚠ <b><see cref="Shrinkwrapped" /> being true is worth reading as a warning.</b> It
///         destroys thin features and it is never the default — it is the escape hatch for input so
///         broken that nothing else will run.
///     </para>
/// </remarks>
public readonly record struct ConditioningReport(
    int Welded,
    int Reoriented,
    int Unorientable,
    int Despecked,
    int Cut,
    int Filled,
    bool Shrinkwrapped,
    int Triangles,
    MeshReport Mesh
);

/// <summary>Everything measured about a remesh, which is the answer ZRemesher does not give.</summary>
/// <param name="QuadCount">How many four-sided faces came out.</param>
/// <param name="NonQuadCount">How many did not. ⚠ Must be zero; a non-zero is a bug rather than a setting.</param>
/// <param name="Singularities">Every irregular vertex, with its valence.</param>
/// <param name="SingularitiesOnFeatures">How many landed on a feature polyline. Must be zero.</param>
/// <param name="MaxDeviation">The furthest the output strays from the source, as a fraction of the bounding-box diagonal.</param>
/// <param name="MeanDeviation">The average, on the same scale.</param>
/// <param name="MinScaledJacobian">The worst quad's shape quality. A negative one is inverted.</param>
/// <param name="FeatureReproductionError">The furthest a feature polyline sits from the nearest output edge.</param>
/// <param name="Conditioning">What stage one changed.</param>
/// <param name="Mesh">Doc 24's report on the output, unchanged.</param>
/// <param name="Stages">Time and element counts, per stage.</param>
/// <param name="Warnings">The shrinkwrap fired · N components dropped · a patch collapsed · the budget was not met.</param>
/// <remarks>
///     <para>
///         ⚠ <b>A remesher that cannot tell you it went wrong will be trusted until it embarrasses
///         somebody.</b> docs/plan/41 § Part 4. The report is what makes this usable in an unattended
///         content build, and it is the difference between a build step and a button somebody watches.
///     </para>
///     <para>
///         ⚠ <b>Deviation is a <i>fraction of the diagonal</i> and never an absolute distance.</b>
///         The same lesson doc 24's <c>Vixen.Geometry</c> row records twice — the capsule's
///         degenerate poles and the weld tolerance — because a fixed number is a claim about how big
///         a model is, and the answer has to compare across models.
///     </para>
/// </remarks>
public readonly record struct RemeshReport(
    int QuadCount,
    int NonQuadCount,
    IReadOnlyList<Singularity> Singularities,
    int SingularitiesOnFeatures,
    float MaxDeviation,
    float MeanDeviation,
    float MinScaledJacobian,
    float FeatureReproductionError,
    ConditioningReport Conditioning,
    MeshReport Mesh,
    IReadOnlyList<RemeshStageTiming> Stages,
    IReadOnlyList<string> Warnings
) {
    /// <summary>Whether every face has four sides, which is what keeps doc 24's verbs working.</summary>
    public bool IsAllQuad => NonQuadCount == 0;
}
