// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>A polyline the edge flow should follow, authored once and reused after the source changes.</summary>
/// <param name="Points">The curve, in the mesh's own space.</param>
/// <param name="Strength">How hard the field is pulled toward it, in <c>[0, 1]</c>.</param>
/// <remarks>
///     ⚠ <b>Ours are an asset rather than a paint session, and that is the difference worth having.</b>
///     docs/plan/41 § D10: a painted guide dies with the mesh it was painted on, so re-generating the
///     source throws the direction away. A curve saved beside the mesh survives it.
/// </remarks>
public readonly record struct RemeshGuide(IReadOnlyList<Vector3> Points, float Strength);

/// <summary>What conditioning is allowed to do to the input before anything else runs.</summary>
/// <remarks>
///     Every tolerance here is relative to the mesh's bounding box and never absolute — a fixed
///     epsilon is a claim about how big a model is, which is the lesson doc 24 records twice.
/// </remarks>
public sealed record ConditioningSettings {
    /// <summary>How near two positions may be and still be one, as a fraction of the bounding-box diagonal.</summary>
    public float WeldTolerance { get; init; } = 1e-5f;

    /// <summary>Components below this fraction of the largest component's surface area are dropped.</summary>
    /// <remarks>
    ///     The default drops an SDF's hallucinated debris and keeps a character's separate eyeball,
    ///     which is the whole of what this number has to get right.
    /// </remarks>
    public float SpeckArea { get; init; } = 1e-3f;

    /// <summary>Whether holes below <see cref="HoleSize" /> are closed.</summary>
    /// <remarks>
    ///     ⚠ Off by default, and deliberately: a hole in the input is very often a hole in the
    ///     subject, and filling one silently is worse than leaving it.
    /// </remarks>
    public bool FillHoles { get; init; }

    /// <summary>The largest hole that will be filled, as a fraction of the bounding-box diagonal.</summary>
    public float HoleSize { get; init; } = 0.05f;

    /// <summary>How many split/collapse/flip/relax rounds the isotropic pre-remesh runs.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the step that makes marching-cubes soup workable.</b> A curvature estimate
    ///     on a staircase mesh is a curvature estimate of the staircase; the pre-remesh is what turns
    ///     per-cell noise into a surface the field can read. The relaxation is reprojected onto the
    ///     original surface each round, so the shape does not drift.
    /// </remarks>
    public int PreRemeshIterations { get; init; } = 5;

    /// <summary>Whether to sample a signed field and re-extract, for input nothing else will run on.</summary>
    /// <remarks>
    ///     ⚠ <b>It destroys thin features and it is never the default.</b> The winding-number field
    ///     means self-intersecting and open input still has an inside, which is the only reason this
    ///     is available at all — and the report says loudly when it fired.
    /// </remarks>
    public bool Shrinkwrap { get; init; }
}

/// <summary>What the output should be, and what it should keep.</summary>
/// <remarks>
///     <para>
///         The authoring surface docs/plan/41 § D10 maps onto ZRemesher's, plus the four rows it has
///         no equivalent for: attribute transfer, baking, coordinates, and a quality report.
///     </para>
///     <para>
///         ⚠ <b><see cref="Adaptivity" />, <see cref="DensityMask" /> and the tightening near
///         features are one mechanism rather than three.</b> docs/plan/41 § D9: they all write into a
///         single per-vertex target edge length, consumed by the pre-remesh, the quantization and the
///         extraction alike. Three separate multipliers applied at three different stages is how a
///         remesher acquires settings that only work in certain combinations.
///     </para>
/// </remarks>
public sealed record RemeshSettings {
    /// <summary>How many quads to aim for. Zero means take the target from <see cref="TargetEdgeLength" /> instead.</summary>
    public int TargetQuads { get; init; } = 5000;

    /// <summary>The edge length to aim for, in world units. Zero means derive it from <see cref="TargetQuads" />.</summary>
    /// <remarks>One implies the other through the surface area; giving both is an error rather than a preference.</remarks>
    public float TargetEdgeLength { get; init; }

    /// <summary>Zero for uniform squares, one to let curvature decide the density.</summary>
    /// <remarks>
    ///     ⚠ The curvature term is weighted by anisotropy — |κ₁ − κ₂| against the model's scale —
    ///     rather than by curvature itself. On a sphere the two are equal, the weight is zero, and
    ///     the field is free to be smooth, which is the correct answer and the one a naive curvature
    ///     alignment gets wrong by chasing noise.
    /// </remarks>
    public float Adaptivity { get; init; } = 0.5f;

    /// <summary>The angle, in degrees, above which a shared edge is a hard feature.</summary>
    public float FeatureAngle { get; init; } = 35f;

    /// <summary>Whether explicit creases on the input are features.</summary>
    public bool KeepCreases { get; init; } = true;

    /// <summary>Whether face-group boundaries are features.</summary>
    public bool KeepGroups { get; init; } = true;

    /// <summary>Whether existing texture-coordinate seams are features.</summary>
    /// <remarks>So that a retexture-then-remesh round trip does not shred an atlas.</remarks>
    public bool KeepUvSeams { get; init; }

    /// <summary>Whether open boundaries are pinned where they are.</summary>
    public bool FreezeBorder { get; init; } = true;

    /// <summary>Curves the edge flow should follow.</summary>
    public IReadOnlyList<RemeshGuide> Guides { get; init; } = [];

    /// <summary>A per-position density multiplier on the source, or empty for none.</summary>
    public IReadOnlyList<float> DensityMask { get; init; } = [];

    /// <summary>The plane to mirror across, or null for no symmetry.</summary>
    /// <remarks>
    ///     ⚠ <b>Ours is exact.</b> docs/plan/41 § D11: the field and layout are solved on one half
    ///     with the plane as a constraint, the result is mirrored, and vertices on the plane are
    ///     snapped to it rather than welded by tolerance — because a tolerance-welded seam is how a
    ///     mirrored mesh acquires a one-vertex crack that only shows up under subdivision. Output
    ///     vertex <i>k</i> and its mirror are exact negations to the last bit.
    /// </remarks>
    public Plane? Symmetry { get; init; }

    /// <summary>What conditioning may do first.</summary>
    public ConditioningSettings Conditioning { get; init; } = new();

    /// <summary>Whether the source's normals, coordinates, colours, materials and weights are carried across.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the output is not a downgrade, it is unusable.</b> docs/plan/41 § D12:
    ///     no normals worth having, no materials, and — for a character — no skinning weights, which
    ///     makes it not a character.
    /// </remarks>
    public bool TransferAttributes { get; init; } = true;

    /// <summary>What stage seven may carry, and what the target can hold.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="TransferSettings.KeepTexCoords" /> is overruled by
    ///     <see cref="GenerateUvs" /> and never the other way round.</b> Both write the same layer,
    ///     and a remesh that regenerated the atlas and then overwrote it with the source's old
    ///     coordinates would be indistinguishable from one where the atlas stage failed.
    /// </remarks>
    public TransferSettings Transfer { get; init; } = new();

    /// <summary>Whether an atlas is generated from the patch layout.</summary>
    public bool GenerateUvs { get; init; } = true;

    /// <summary>Where the atlas comes out, when <see cref="GenerateUvs" /> asks for one.</summary>
    public AtlasSettings Atlas { get; init; } = new();

    /// <summary>How many iterations the cross-field solver runs at each level of its hierarchy.</summary>
    /// <remarks>
    ///     ⚠ <b>A fixed count, not a convergence tolerance and not a wall clock.</b> docs/plan/41
    ///     § D14: byte-identical output at any thread count is a gate, and a stopping rule that reads
    ///     a residual is a floating-point comparison that can land differently on two machines.
    /// </remarks>
    public int FieldIterations { get; init; } = 30;
}
