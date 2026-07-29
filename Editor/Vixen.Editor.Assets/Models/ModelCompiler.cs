// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Rendering;
using Vixen.Rendering.VirtualGeometry;

namespace Vixen.Editor.Assets.Models;

/// <summary>The decisions about a model that need the whole model, taken once, at build time.</summary>
/// <remarks>
///     <para>
///         Doc 08's <c>ModelCompiler</c>. It exists because a mesh cannot answer any of these
///         questions about itself: what the vertex layout should be, how the indices should be
///         ordered for the cache, which levels of detail there should be, and — first of them, and
///         what is built here — what the cluster DAG is.
///     </para>
///     <para>
///         <b>Meshlet generation is phase 1 of
///         <c>docs/virtualized-geometry.md</c></b>, and the whole of the algorithm lives in
///         <c>Vixen.Rendering.VirtualGeometry</c> rather than here, for the reason the distance-field
///         bake lives in <c>Vixen.Rendering.DistanceFields</c>: a <see cref="MeshletMesh" /> is what
///         this writes and what a player deserialises, so both halves have to be talking about one
///         type — and the algorithm is then testable against spheres and grids with no import
///         context anywhere near it.
///     </para>
///     <para>
///         <b>What this adds is the refusal.</b> Improvement 5 of the plan: DAG validity is a build
///         error rather than a property the builder is careful about. A mesh whose DAG does not
///         validate produces no meshlets and a message naming the group, because the alternative is
///         an asset that ships and cracks at one distance on one mesh — and nothing about that
///         failure points back at the build that caused it.
///     </para>
/// </remarks>
public static class ModelCompiler {
    /// <summary>Builds one mesh's cluster DAG, or refuses it.</summary>
    /// <param name="mesh">The mesh, as it came out of the importer.</param>
    /// <param name="settings">How big clusters and groups are.</param>
    /// <param name="report">Where to say what happened.</param>
    /// <returns>The DAG, or null if the mesh has no triangles or its DAG did not validate.</returns>
    /// <exception cref="ArgumentNullException">The mesh or the report is null.</exception>
    public static MeshletMesh? CompileMeshlets(
        MeshData mesh,
        MeshletBuildSettings settings,
        Action<ImportSeverity, string> report
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(report);

        if (mesh.TriangleCount == 0) {
            return null;
        }

        var input = ToBuildInput(mesh);
        var built = MeshletBuilder.Build(input, settings);
        var problems = MeshletValidator.Validate(built, input);

        if (problems.Count == 0) {
            return built;
        }

        // Every problem, and then it fails once — the habit `SceneImporter` set, for the same reason:
        // a build that stopped at the first one would make fixing a mesh a sequence of builds.
        report(
            ImportSeverity.Error,
            $"'{mesh.Name}' produced a cluster hierarchy that is not crack-free, so it has none. "
            + string.Join(" ", problems)
        );

        return null;
    }

    /// <summary>A mesh as the DAG builder wants it.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>The builder's input.</returns>
    /// <remarks>
    ///     A copy of the references and not of the arrays. The builder reads them and writes none of
    ///     them, which is worth being able to say out loud: a compile that quietly rewrote the mesh
    ///     it was handed would make the order the importer does its work in matter.
    /// </remarks>
    static MeshletBuildInput ToBuildInput(MeshData mesh) =>
        new() {
            Positions = mesh.Positions,
            Indices = mesh.Indices,
            Normals = mesh.Normals,
            Tangents = mesh.Tangents,
            TexCoords = mesh.TexCoords,
            BoneIndices = mesh.BoneIndices,
            BoneWeights = mesh.BoneWeights,
            MaterialIndex = mesh.MaterialIndex
        };
}
