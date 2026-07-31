// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;
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
}
