// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Geometry.Remeshing;
using Vixen.Geometry.Uv;

namespace Vixen.Editor.Blockout;

/// <summary>The retopology section of the blockout settings panel.</summary>
/// <remarks>
///     <para>
///         <b>An editable class beside <see cref="RemeshSettings" />, and doc 36 § P1's last blockout
///         row is why it exists.</b> Nothing in the editor drew the remesher's settings at all: they
///         are a <c>Core/</c> record, and a record is the wrong shape twice over for a panel.
///     </para>
///     <para>
///         ⚠ <b>A class rather than an annotation on the record, and there are three separate reasons
///         — any one of them decisive.</b> The record is <c>init</c>-only, and the inspector's
///         generator emits <c>owner.Property = value</c> for anything with a setter, which for an
///         <c>init</c> property is a compiler error in generated code the author never sees. The
///         record lives in <c>Core/Vixen.Geometry.Remeshing</c>, which cannot reference an editor
///         assembly — <c>ReflectedDescriptor</c>'s own remarks put it as "no runtime type carries
///         <c>[Inspector]</c>, and none should". And a panel needs a stable object to bind to across
///         edits, which a record replaced wholesale by every <c>with</c> expression is not. The
///         import pipeline reached the same conclusion first: see <c>ModelImportEdits</c>.
///     </para>
///     <para>
///         ⚠ <b>Not every member of the record is here, and the absences are deliberate.</b>
///         <see cref="RemeshSettings.Guides" /> and <see cref="RemeshSettings.DensityMask" /> are
///         per-run data a stroke produces rather than dials a person types, and
///         <see cref="RemeshSettings.Symmetry" /> is a plane that belongs to a gizmo rather than to a
///         number field. <see cref="ToRemeshSettings" /> leaves each at the record's own default, so
///         a caller that has one passes it beside these rather than through them.
///     </para>
/// </remarks>
[DataContract("BlockoutRetopologySettings")]
public sealed class BlockoutRetopologySettings {
    /// <summary>Roughly how many quads to spend.</summary>
    [Inspector]
    [Range(50f, 200000f)]
    [Tooltip("Roughly how many quads the result should have. The budget, not a guarantee.")]
    public int TargetQuads { get; set; } = 2000;

    /// <summary>An edge length to hit instead of a quad count, or zero to derive one from the count.</summary>
    [Inspector]
    [Tooltip("An edge length in world units to hit instead of a quad count. Zero derives one from the count.")]
    public float TargetEdgeLength { get; set; }

    /// <summary>How much the quad density follows curvature, 0…1.</summary>
    [Inspector]
    [Range(0f, 1f)]
    [Tooltip("How much denser the mesh gets where it curves. 0 is uniform.")]
    public float Adaptivity { get; set; } = 0.5f;

    /// <summary>The angle, in degrees, above which an edge counts as a hard feature.</summary>
    [Inspector]
    [Range(0f, 180f)]
    [Tooltip("The dihedral angle above which an edge is reproduced exactly rather than smoothed over.")]
    public float FeatureAngle { get; set; } = 35f;

    /// <summary>Whether authored creases are kept.</summary>
    [Inspector]
    public bool KeepCreases { get; set; } = true;

    /// <summary>Whether face-group boundaries are kept.</summary>
    [Inspector]
    public bool KeepGroups { get; set; } = true;

    /// <summary>Whether existing UV seams are kept.</summary>
    [Inspector]
    public bool KeepUvSeams { get; set; }

    /// <summary>Whether an open rim is pinned where it is.</summary>
    [Inspector]
    public bool FreezeBorder { get; set; } = true;

    /// <summary>Whether the source's attribute layers are resampled onto the result.</summary>
    [Inspector]
    public bool TransferAttributes { get; set; } = true;

    /// <summary>Whether an atlas is built out of the patch grids.</summary>
    [Inspector]
    public bool GenerateUvs { get; set; } = true;

    /// <summary>How many iterations the cross-field solve runs.</summary>
    [Inspector]
    [Range(1f, 200f)]
    [Tooltip("How long the direction field is smoothed for. More is smoother loops and slower.")]
    public int FieldIterations { get; set; } = 30;

    /// <summary>What the remesher is actually asked for.</summary>
    /// <param name="guides">Guide curves, if a tool produced any.</param>
    /// <returns>The settings.</returns>
    public RemeshSettings ToRemeshSettings(IReadOnlyList<RemeshGuide>? guides = null) =>
        new() {
            TargetQuads = TargetQuads,
            TargetEdgeLength = TargetEdgeLength,
            Adaptivity = Adaptivity,
            FeatureAngle = FeatureAngle,
            KeepCreases = KeepCreases,
            KeepGroups = KeepGroups,
            KeepUvSeams = KeepUvSeams,
            FreezeBorder = FreezeBorder,
            TransferAttributes = TransferAttributes,
            GenerateUvs = GenerateUvs,
            FieldIterations = FieldIterations,
            Guides = guides ?? []
        };
}

/// <summary>The charting-and-flattening section of the blockout settings panel.</summary>
/// <remarks>
///     ⚠ <b><see cref="UvSettings.Decomposition" /> and <see cref="UvSettings.SeamCost" /> are not
///     here.</b> The first is an interface — a plug point for a learned part field, not a number —
///     and the second is a nested record of weights that belongs in a section of its own the first
///     time somebody asks for it. <see cref="ToUvSettings" /> leaves both at the record's defaults,
///     which is the built-in decomposition and the built-in seam cost.
/// </remarks>
[DataContract("BlockoutChartSettings")]
public sealed class BlockoutChartSettings {
    /// <summary>The distortion a chart must come in under, or it is split and tried again.</summary>
    [Inspector]
    [Range(1f, 3f)]
    [Tooltip("One is a perfectly isometric map. Tighten it and charts multiply; loosen it and the texture stretches.")]
    public float DistortionThreshold { get; set; } = 1.15f;

    /// <summary>How deep the split-and-retry recursion may go.</summary>
    [Inspector]
    [Range(0f, 16f)]
    public int MaxDepth { get; set; } = 8;

    /// <summary>The angle, in degrees, above which a shared edge counts as a hard feature.</summary>
    [Inspector]
    [Range(0f, 180f)]
    public float FeatureAngle { get; set; } = 40f;

    /// <summary>Whether face-group boundaries partition first, unconditionally.</summary>
    [Inspector]
    [Tooltip("A material boundary is somewhere the texture already changes, so a seam there costs nothing new.")]
    public bool KeepGroups { get; set; } = true;

    /// <summary>How many iterations the flattener's local–global loop runs, per chart.</summary>
    [Inspector]
    [Range(1f, 256f)]
    public int FlattenIterations { get; set; } = 32;

    /// <summary>How many conjugate-gradient iterations each linear solve is allowed.</summary>
    [Inspector]
    [Range(1f, 512f)]
    public int SolverIterations { get; set; } = 64;

    /// <summary>What the charter and the flattener are actually asked for.</summary>
    /// <returns>The settings.</returns>
    public UvSettings ToUvSettings() =>
        new() {
            DistortionThreshold = DistortionThreshold,
            MaxDepth = MaxDepth,
            FeatureAngle = FeatureAngle,
            KeepGroups = KeepGroups,
            FlattenIterations = FlattenIterations,
            SolverIterations = SolverIterations
        };
}

/// <summary>The packing section of the blockout settings panel.</summary>
/// <remarks>
///     ⚠ <b><see cref="PackSettings.Resolution" /> is <c>required</c> on the record and has a default
///     here.</b> That is not a disagreement: a required member exists so that a caller in code cannot
///     forget the number the margin is counted against, and a panel cannot forget it — it is a field
///     with a value in it from the moment it is drawn.
/// </remarks>
[DataContract("BlockoutPackSettings")]
public sealed class BlockoutPackSettings {
    /// <summary>The atlas's edge length in texels.</summary>
    [Inspector]
    [Range(64f, 8192f)]
    [Tooltip("The atlas's edge length in texels. The margin is counted against it.")]
    public int Resolution { get; set; } = 1024;

    /// <summary>How many texels of empty space separate two islands.</summary>
    [Inspector]
    [Range(0f, 64f)]
    [Tooltip("Four is the usual answer for a 2K atlas: enough for a trilinear tap plus two mip levels.")]
    public int Margin { get; set; } = 4;

    /// <summary>How hard to try.</summary>
    [Inspector]
    public PackQuality Quality { get; set; } = PackQuality.Irregular;

    /// <summary>What to do when it does not fit.</summary>
    [Inspector]
    public PackOverflow Overflow { get; set; } = PackOverflow.Scale;

    /// <summary>How many orientations an island may be tried in, as quarter turns.</summary>
    [Inspector]
    [Range(1f, 16f)]
    [Tooltip("Quarter turns an island may be tried in. One means none.")]
    public int Rotations { get; set; } = 4;

    /// <summary>Texels per world unit every island is scaled to, or zero to keep each island's own scale.</summary>
    [Inspector]
    [Tooltip("Texels per world unit. Zero keeps each island at whatever scale flattening gave it.")]
    public float TexelDensity { get; set; }

    /// <summary>How many islands the expensive core will place before the cheap tail takes over.</summary>
    [Inspector]
    [Range(16f, 16384f)]
    public int CoreLimit { get; set; } = 1024;

    /// <summary>What the packer is actually asked for.</summary>
    /// <returns>The settings.</returns>
    public PackSettings ToPackSettings() =>
        new() {
            Resolution = Resolution,
            Margin = Margin,
            Quality = Quality,
            Overflow = Overflow,
            Rotations = Rotations,
            TexelDensity = TexelDensity,
            CoreLimit = CoreLimit
        };
}
