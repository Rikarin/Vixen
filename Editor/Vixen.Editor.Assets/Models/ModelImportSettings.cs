// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml.Meta;
using Vixen.Geometry.Remeshing;
using Vixen.Geometry.Uv;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.VirtualGeometry;

namespace Vixen.Editor.Assets.Models;

/// <summary>How one model is imported.</summary>
/// <remarks>
///     Each of these answers something the file cannot. Which axis is up, how the material tree is
///     wired and what the LODs should be are all decisions with better homes — the first in the
///     authoring tool, the second in a material asset, the third in the compiler that sees the whole
///     model.
/// </remarks>
[DataContract("ModelImporter")]
public sealed record ModelImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The importer that most needs it, because a model is the asset whose sub-asset names
    ///     the author least controls.</b> A <c>.glb</c> from an asset store arrives with two meshes
    ///     called <c>Cube</c> and no way to fix it short of a round trip through Blender; the import
    ///     names the second one <c>Cube_1</c> so the asset still loads, and this is how it gets a name
    ///     worth reading. A sprite's name comes from the slicer and a video has one track, so neither
    ///     of those importers declares this.
    /// </remarks>
    public List<SubAssetRename> SubAssetNames { get; init; } = [];

    /// <inheritdoc />
    IReadOnlyList<SubAssetRename> IImportSettings.SubAssetNames => SubAssetNames;

    /// <summary>What to multiply every length by.</summary>
    /// <remarks>
    ///     The setting nobody escapes. An FBX out of Max or Maya is in centimetres, a glTF is in
    ///     metres, and a scene mixing the two has one of them a hundred times too big. Applied to
    ///     vertex positions <em>and</em> to node translations, so a model scales exactly once however
    ///     deep its hierarchy is.
    /// </remarks>
    public float Scale { get; init; } = 1f;

    /// <summary>Whether to compute normals for meshes that have none.</summary>
    /// <remarks>
    ///     Only for meshes that have none. Recomputing normals an artist authored would throw away
    ///     the hand-adjusted shading that is most of what makes a hard-surface model read correctly.
    /// </remarks>
    public bool GenerateNormals { get; init; } = true;

    /// <summary>Whether to compute tangents for meshes that have UVs and no tangents.</summary>
    /// <remarks>
    ///     Needs texture coordinates, because a tangent frame is defined by how the UVs run across
    ///     the surface. A mesh with no UVs gets none and no complaint — it has nothing to normal-map.
    /// </remarks>
    public bool GenerateTangents { get; init; } = true;

    /// <summary>Whether to import the animation clips the file carries.</summary>
    /// <remarks>
    ///     On by default and worth turning off per asset. A character exported once per animation
    ///     ships the same skeleton and the same clip in every file, and the duplicates are dead
    ///     weight in the bundle.
    /// </remarks>
    public bool ImportAnimations { get; init; } = true;

    /// <summary>Whether to bake a signed distance field for each of the model's meshes.</summary>
    /// <remarks>
    ///     <para>
    ///         On by default, because <c>docs/plan/19</c> makes the field the substrate the whole
    ///         lighting path stands on — distance-field shadows and occlusion read it, and everything
    ///         above them traces it. A mesh with no field is invisible to all of that.
    ///     </para>
    ///     <para>
    ///         It is the most expensive thing this importer does, by a wide margin: a bake is one
    ///         exact closest-point query and <see cref="DistanceFieldSignRays" /> ray casts for every
    ///         one of <see cref="DistanceFieldResolution" /> cubed samples. Turning it off is one
    ///         setting, and worth it for a project that is not lighting this way — or per asset, for
    ///         a mesh nothing will ever cast off.
    ///     </para>
    /// </remarks>
    public bool GenerateDistanceFields { get; init; } = true;

    /// <summary>How many samples along the longest axis of each field.</summary>
    /// <remarks>
    ///     The quality dial and the cost dial at once — doubling it is eight times the samples. The
    ///     other two axes follow from the mesh's bounds so cells stay near-cubic, which matters most
    ///     for a thin mesh: the thin axis is the one that decides whether the field leaks.
    /// </remarks>
    public int DistanceFieldResolution { get; init; } = 32;

    /// <summary>How many rays each sample casts to decide which side of the surface it is on.</summary>
    /// <remarks>
    ///     The dominant cost, and what makes the sign survive the meshes people actually ship rather
    ///     than only closed ones. Below about sixteen the vote is noisy on concave geometry; above
    ///     about sixty-four it stops changing.
    /// </remarks>
    public int DistanceFieldSignRays { get; init; } = 32;

    /// <summary>How far a field's volume is grown past its mesh, as a fraction of the mesh's size.</summary>
    /// <remarks>
    ///     A field whose bounds are the mesh's own has the surface lying on its boundary, where a
    ///     trilinear sample has nothing on one side. The margin is where a ray approaching the mesh
    ///     slows down before it arrives rather than at it.
    /// </remarks>
    public float DistanceFieldBoundsExpansion { get; init; } = 0.2f;

    /// <summary>Whether to build a cluster hierarchy for each of the model's meshes.</summary>
    /// <remarks>
    ///     <para>
    ///         Phase 1 of <c>docs/plan/22-virtualized-geometry.md</c>: the mesh is partitioned into clusters
    ///         of about <see cref="MeshletTriangles" /> triangles, neighbouring clusters are
    ///         simplified together as groups with their shared boundary locked, and the result is
    ///         split and simplified again until one cluster is left. What comes out is every level of
    ///         detail at once, plus a fallback mesh cut from it at a fixed budget.
    ///     </para>
    ///     <para>
    ///         On by default, and it is the second most expensive thing this importer does. Turning
    ///         it off leaves a mesh that draws through the ordinary path with whatever levels of
    ///         detail were authored, which is the right answer for a mesh that is already a hundred
    ///         triangles.
    ///     </para>
    /// </remarks>
    public bool GenerateMeshlets { get; init; } = true;

    /// <summary>The most triangles one cluster may hold.</summary>
    /// <remarks>
    ///     The unit of culling and of streaming both, since a cluster is accepted or rejected whole
    ///     and paged in whole. A hundred and twenty-eight is what Nanite uses and about where the
    ///     per-cluster overhead stops mattering.
    /// </remarks>
    public int MeshletTriangles { get; init; } = 128;

    /// <summary>The most distinct vertices one cluster may reference.</summary>
    /// <remarks>
    ///     At most 256, because a cluster's triangles index its own vertex list with a byte. A closed
    ///     patch of a hundred and twenty-eight triangles carries about seventy vertices, so this
    ///     rarely binds; where it does, the cluster is split rather than the mesh refused.
    /// </remarks>
    public int MeshletVertices { get; init; } = 128;

    /// <summary>How many clusters are simplified together as a group.</summary>
    /// <remarks>
    ///     The dial that decides how much a level of detail can actually remove. A group's shared
    ///     outer boundary is locked, so a small group has little interior to collapse and a large one
    ///     spans parts of the mesh that have no business being simplified together.
    /// </remarks>
    public int MeshletGroupSize { get; init; } = 16;

    /// <summary>How many triangles the generated fallback mesh may have.</summary>
    /// <remarks>
    ///     A cut through the finished hierarchy at a fixed budget, emitted as an ordinary indexed
    ///     mesh. It is what WebGL2 draws, what the physics cook reads, and what anything the
    ///     virtualized path does not reach falls back to — generated rather than authored, so it
    ///     cannot disagree with the mesh it stands in for.
    /// </remarks>
    public int MeshletFallbackTriangles { get; init; } = 4096;

    /// <summary>Whether every mesh in the model is retopologised into quads on the way in.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41 § D16's importer row, and it is the AI pipeline's hook.</b> A generated
    ///         GLB dropped into the project is four million triangles of marching-cubes noise with no
    ///         UVs; with this on it comes out as <see cref="RetopologyQuads" /> quads with an atlas,
    ///         which is smaller, looks better under a moving light, subdivides and can be rigged.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Off by default, and it stays off.</b> Retopology is destructive in the way that
    ///         matters: an artist's topology is a decision, and an importer that silently replaced it
    ///         would be an importer nobody could use on a hand-modelled asset. It is also the most
    ///         expensive thing this importer can do, by a margin over the distance field.
    ///     </para>
    /// </remarks>
    public bool Retopologize { get; init; }

    /// <summary>How many quads to aim for per mesh.</summary>
    /// <remarks>
    ///     The budget, not a guarantee — docs/plan/41's <c>Remesher.BudgetTolerance</c>: a patch's quad
    ///     count is a product of two side lengths, so a partition of snaky patches overshoots
    ///     quadratically and the report says by how much rather than the layout being scaled to fit.
    /// </remarks>
    public int RetopologyQuads { get; init; } = 5000;

    /// <summary>Zero for uniform squares, one to let curvature decide the density.</summary>
    public float RetopologyAdaptivity { get; init; } = 0.5f;

    /// <summary>The angle, in degrees, above which a shared edge is a hard feature.</summary>
    public float RetopologyFeatureAngle { get; init; } = 35f;

    /// <summary>Whether the source's coordinate seams are features the retopology keeps.</summary>
    /// <remarks>
    ///     docs/plan/41 § D4, so that a retexture-then-remesh round trip does not shred an atlas. It
    ///     needs the source to carry coordinates; a mesh with none has no seams and this does nothing.
    /// </remarks>
    public bool RetopologyKeepUvSeams { get; init; }

    /// <summary>Which axis to mirror across — <c>none</c>, <c>x</c>, <c>y</c> or <c>z</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41 § D11, and an axis rather than a plane on purpose.</b> The exactness the
    ///         section promises — output vertex <i>k</i> and its mirror are exact negations, every
    ///         vertex on the plane has an exactly zero coordinate — holds for an axis through the
    ///         origin, where the snap is a store of zero and the reflection is a sign-bit flip. An
    ///         arbitrary plane in a settings file would be four numbers that usually miss that case and
    ///         silently give a rounded mirror instead.
    ///     </para>
    ///     <para>
    ///         ⚠ It is the model's own space, so a character exported off-centre gets its symmetry
    ///         about the wrong plane. That is the file to fix rather than the setting.
    ///     </para>
    /// </remarks>
    public SymmetryAxis RetopologySymmetry { get; init; } = SymmetryAxis.None;

    /// <summary>Guide curves the retopology's edge flow should follow, as <c>.vxspline</c> asset paths.</summary>
    /// <remarks>
    ///     <b>docs/plan/41 § D10: "ours are an asset, not a paint session".</b> A painted guide dies
    ///     with the mesh it was painted on, so re-generating the source throws the direction away. A
    ///     curve saved beside the mesh survives it — which is why this is a list of paths to
    ///     <c>SplineAsset</c> files rather than a list of polylines pasted into the <c>.meta</c>.
    /// </remarks>
    public List<RetopologyGuideReference> RetopologyGuides { get; init; } = [];

    /// <summary>When to generate texture coordinates for the model's meshes.</summary>
    /// <remarks>
    ///     <b>docs/plan/42 § D13's importer row: "generate when the source has no UVs, or always, or
    ///     never".</b> <see cref="UnwrapMode.WhenMissing" /> is the one worth defaulting to if a
    ///     project ever does — it fixes the generated-mesh case and leaves an artist's atlas alone —
    ///     and it is still off here for the reason <see cref="Retopologize" /> is.
    /// </remarks>
    public UnwrapMode Unwrap { get; init; } = UnwrapMode.Never;

    /// <summary>The atlas resolution the unwrap packs for.</summary>
    /// <remarks>
    ///     ⚠ <b>The packer needs this because the margin is in texels and packing happens in UV
    ///     units</b> — docs/plan/42 § B4 and § D8. A margin expressed as a fraction of UV space is a
    ///     margin that means a different number of texels on every island of every atlas.
    /// </remarks>
    public int UnwrapResolution { get; init; } = 1024;

    /// <summary>How many texels of empty space each island keeps around it.</summary>
    public int UnwrapMargin { get; init; } = 4;

    /// <summary>Texels per world unit to hold across every chart, or zero to fill the atlas instead.</summary>
    /// <remarks>docs/plan/42 § D9: density is a constraint when it is set, and an observation when it is not.</remarks>
    public float UnwrapTexelDensity { get; init; }

    /// <summary>These as the bake wants them.</summary>
    /// <returns>The build settings.</returns>
    public DistanceFieldBuildSettings ToDistanceFieldSettings() =>
        new() {
            Resolution = DistanceFieldResolution,
            SignRayCount = DistanceFieldSignRays,
            BoundsExpansion = DistanceFieldBoundsExpansion
        };

    /// <summary>These as the cluster build wants them.</summary>
    /// <returns>The build settings.</returns>
    public MeshletBuildSettings ToMeshletSettings() =>
        new() {
            MaxTriangles = MeshletTriangles,
            MaxVertices = MeshletVertices,
            GroupSize = MeshletGroupSize,
            FallbackTriangles = MeshletFallbackTriangles
        };

    /// <summary>These as the remesher wants them.</summary>
    /// <param name="guides">The curves the guide references resolved to, or empty.</param>
    /// <returns>The remesh settings.</returns>
    /// <remarks>
    ///     ⚠ <b>The mapper rather than a nullable <c>RemeshSettings</c> on the record, which is what
    ///     docs/plan/41 § D16 asks for.</b> The section was written before anybody opened this file:
    ///     every other expensive stage here is a flat <c>bool</c> plus its dials plus a
    ///     <c>To…Settings()</c>, because a <c>.meta</c> is authored by hand as often as by the
    ///     inspector and a nested record is a second level of indentation for every one of them. The
    ///     house pattern wins; § D16's row is amended rather than followed.
    /// </remarks>
    public RemeshSettings ToRemeshSettings(IReadOnlyList<RemeshGuide>? guides = null) =>
        new() {
            TargetQuads = RetopologyQuads,
            Adaptivity = RetopologyAdaptivity,
            FeatureAngle = RetopologyFeatureAngle,
            KeepUvSeams = RetopologyKeepUvSeams,
            Guides = guides ?? [],
            Symmetry = RetopologySymmetry switch {
                SymmetryAxis.X => new Plane(Vector3.UnitX, 0f),
                SymmetryAxis.Y => new Plane(Vector3.UnitY, 0f),
                SymmetryAxis.Z => new Plane(Vector3.UnitZ, 0f),
                _ => null
            }
        };

    /// <summary>These as the unwrapper's first two stages want them.</summary>
    /// <returns>The unwrap settings.</returns>
    public UvSettings ToUvSettings() => new() { FeatureAngle = RetopologyFeatureAngle };

    /// <summary>These as the packer wants them.</summary>
    /// <returns>The pack settings.</returns>
    public PackSettings ToPackSettings() =>
        new() {
            Resolution = UnwrapResolution,
            Margin = UnwrapMargin,
            TexelDensity = UnwrapTexelDensity
        };
}

/// <summary>Which plane through the origin a retopology mirrors across.</summary>
/// <remarks>
///     An axis rather than a plane, because docs/plan/41 § D11's exactness is a property of the
///     axis-aligned case: the snap is a store of zero and the reflection is a sign-bit flip, and both
///     are exact for every float. See <see cref="ModelImportSettings.RetopologySymmetry" />.
/// </remarks>
public enum SymmetryAxis {
    /// <summary>No symmetry. The whole mesh is solved.</summary>
    None,

    /// <summary>The <c>YZ</c> plane — the one a character is symmetric about.</summary>
    X,

    /// <summary>The <c>XZ</c> plane.</summary>
    Y,

    /// <summary>The <c>XY</c> plane.</summary>
    Z
}

/// <summary>When an import generates texture coordinates.</summary>
public enum UnwrapMode {
    /// <summary>Never. Whatever the file carries is what the mesh has.</summary>
    Never,

    /// <summary>Only for meshes that arrived without any, which is the generated-mesh case.</summary>
    WhenMissing,

    /// <summary>Always, replacing whatever the file carried.</summary>
    Always
}

/// <summary>One guide curve a retopology should follow, named by the asset that holds it.</summary>
/// <param name="Spline">The project-relative path of a <c>.vxspline</c>, e.g. <c>Curves/spine.vxspline</c>.</param>
/// <param name="Strength">How hard the field is pulled toward it, in <c>[0, 1]</c>.</param>
/// <remarks>
///     ⚠ <b>A path rather than the curve itself, and docs/plan/41 § D10 is why.</b> A guide that lived
///     in the <c>.meta</c> would be a guide that belongs to one import of one file; a guide that is an
///     asset can be authored once on a curve, shared between the three meshes it applies to, and reused
///     after the source has been regenerated — which is the case the whole AI pipeline consists of.
/// </remarks>
public readonly record struct RetopologyGuideReference(string Spline, float Strength = 1f);
