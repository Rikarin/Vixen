// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Uv;

/// <summary>What a seam costs to run somewhere, which is where <i>"where would an artist cut"</i> lives.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D4. Every weight multiplies a term that is already normalized to
///         <c>[0, 1]</c>, so the numbers compare with each other and a caller can zero one out
///         without rebalancing the rest.
///     </para>
///     <para>
///         ⚠ <b><see cref="Length" /> is the term everything else is traded against.</b> Raise it
///         and the cutter takes the short way round a feature it should have followed; drop it and
///         seams wander through flat regions collecting a fractional saving each time.
///     </para>
/// </remarks>
public sealed record SeamCost {
    /// <summary>Prefers a seam sitting in a crease that folds inward, where a discontinuity does not read.</summary>
    public float Concavity { get; init; } = 1f;

    /// <summary>Prefers a seam that is occluded, estimated by ambient occlusion over the surface.</summary>
    public float Visibility { get; init; } = 0.75f;

    /// <summary>Prefers a seam following a hard edge, where a normal-map discontinuity is invisible anyway.</summary>
    public float Feature { get; init; } = 1.25f;

    /// <summary>Prefers a seam running where the texture already changes.</summary>
    public float Material { get; init; } = 1.5f;

    /// <summary>Prefers a seam on the mirror plane, so the two halves' seams agree exactly.</summary>
    public float Symmetry { get; init; } = 0.5f;

    /// <summary>Prefers a short seam. The term the others are traded against.</summary>
    public float Length { get; init; } = 1f;

    /// <summary>Prefers a seam that was already there, when re-unwrapping a mesh that had coordinates.</summary>
    public float Existing { get; init; } = 2f;
}

/// <summary>Everything the charter and the flattener need.</summary>
/// <remarks>
///     ⚠ <b>Chart count is an outcome of a quality target rather than a knob, and that inversion is
///     the design.</b> docs/plan/42 § D3: growing regions until a stretch bound trips, with nothing
///     that ever puts two back together, is exactly why the established tools fragment. Here the
///     recursion splits only what fails <see cref="DistortionThreshold" />, and then a merge-back
///     pass puts adjacent charts together again wherever their union still passes.
/// </remarks>
public sealed record UvSettings {
    /// <summary>The distortion a chart must come in under, or it is split and tried again.</summary>
    /// <remarks>
    ///     One is a perfectly isometric map. The usual answer is a little above it — tighten this and
    ///     charts multiply, loosen it and the texture stretches.
    /// </remarks>
    public float DistortionThreshold { get; init; } = 1.15f;

    /// <summary>How deep the split-and-retry recursion may go before it accepts what it has.</summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>What a seam costs to run somewhere.</summary>
    public SeamCost SeamCost { get; init; } = new();

    /// <summary>How a region that failed its bound is broken up, or <c>null</c> for the built-in one.</summary>
    /// <remarks>
    ///     ⚠ <b>The default path never calls one, and that is deliberate.</b> docs/plan/42 § D3 keeps
    ///     PartUV's recursion and replaces its learned top with a classical concavity-driven split, so
    ///     leaving this null selects an approximate convex decomposition over the dual graph that owes
    ///     nobody anything. The hook exists so that a learned part field can be dropped in behind it
    ///     under § D14's rules — <i>it proposes and never decides</i>, because whatever it returns is
    ///     still flattened, still measured, and still has to pass <see cref="DistortionThreshold" />.
    /// </remarks>
    public IChartDecomposition? Decomposition { get; init; }

    /// <summary>The angle, in degrees, above which a shared edge counts as a hard feature.</summary>
    public float FeatureAngle { get; init; } = 40f;

    /// <summary>Whether face-group boundaries partition first, unconditionally.</summary>
    /// <remarks>
    ///     <para>
    ///         They do by default: a material boundary is somewhere the texture already changes, so a
    ///         seam there costs nothing that has not already been paid.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"Face group" means one somebody assigned, and the charter checks — this setting is
    ///         read together with <see cref="EditMesh.GroupSource" /> and never on its own.</b> A mesh
    ///         built by <see cref="EditMesh.FromTriangles" /> carries the coplanarity guess instead, and
    ///         on any faceted surface that is one group per triangle: measured on a 25 439-triangle
    ///         image-to-3D mesh, honouring it produced 24 197 charts, because the count was decided
    ///         before charting began and every recursion and merge-back afterwards was working inside a
    ///         single triangle. Turning this off is not the fix for that — it would also throw away the
    ///         real material boundaries on the meshes that have them.
    ///     </para>
    /// </remarks>
    public bool KeepGroups { get; init; } = true;

    /// <summary>How many iterations the flattener's local–global loop runs, per chart.</summary>
    /// <remarks>
    ///     ⚠ <b>A count and not a tolerance, and that is a determinism decision rather than a
    ///     performance one.</b> A residual test is a floating-point comparison whose outcome can
    ///     differ across platforms, which is precisely the class of bug the gate exists to prevent.
    /// </remarks>
    public int FlattenIterations { get; init; } = 32;

    /// <summary>How many conjugate-gradient iterations each linear solve is allowed.</summary>
    /// <remarks>
    ///     Small on purpose: consecutive solves in a local–global loop are close, so a warm start
    ///     converges in very few steps. The same fixed-budget argument as
    ///     <see cref="FlattenIterations" /> applies, for the same reason.
    /// </remarks>
    public int SolverIterations { get; init; } = 64;
}
