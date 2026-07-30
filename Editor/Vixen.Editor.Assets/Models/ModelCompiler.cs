// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
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
///         <c>docs/plan/22-virtualized-geometry.md</c></b>, and the whole of the algorithm lives in
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

    /// <summary>
    ///     How many bytes of attributes a page vertex carries beside its quantized position.
    /// </summary>
    /// <remarks>
    ///     A normal as three halves and a texture coordinate as two, which with
    ///     <see cref="MeshletPageBuilder.PositionSize" /> makes a page vertex sixteen bytes — a
    ///     device word boundary per vertex, without padding to reach one.
    /// </remarks>
    public const int PageAttributeStride = 10;

    /// <summary>
    ///     The same, for a skinned mesh: eight more bytes of bone influences after the coordinate.
    /// </summary>
    /// <remarks>
    ///     A skinned page vertex is twenty-four bytes rather than sixteen, and a static one is
    ///     untouched — see <see cref="MeshletPageSet.InfluenceOffset" /> for why this is per mesh
    ///     rather than a fixed layout every mesh pays for.
    /// </remarks>
    public const int SkinnedPageAttributeStride = PageAttributeStride + MeshletPageBuilder.InfluenceSize;

    /// <summary>Where a skinned page vertex's influences begin, in bytes from the vertex.</summary>
    public const int PageInfluenceOffset = MeshletPageBuilder.PositionSize + PageAttributeStride;

    /// <summary>Packs a DAG's geometry into pages, quantized against one grid.</summary>
    /// <param name="mesh">The mesh, as it came out of the importer.</param>
    /// <param name="meshlets">Its DAG, from <see cref="CompileMeshlets" />.</param>
    /// <param name="report">Where to say what happened.</param>
    /// <returns>The pages, or null if they could not be built.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The pages are built here rather than at load, and that is what phase 2 is for.</b>
    ///         Quantizing a mesh means finding its extent, snapping every vertex to a grid and
    ///         packing clusters into slots — work proportional to the whole mesh, which is exactly the
    ///         work streaming exists so a frame never does. A build does it once; a load reads bytes.
    ///     </para>
    ///     <para>
    ///         <b>Attributes are packed even though phase 4 does not read them.</b> The visibility
    ///         buffer needs positions and nothing else, and it would be smaller to ship positions
    ///         alone — but phase 5's resolve fetches the normal and the texture coordinate of the same
    ///         vertex from the same page, and a format that made that a re-import is a format that is
    ///         not the shipping one. See <c>docs/plan/22-virtualized-geometry.md</c> phase 5.
    ///     </para>
    /// </remarks>
    public static MeshletPageSet? CompilePages(
        MeshData mesh,
        MeshletMesh meshlets,
        Action<ImportSeverity, string> report
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(meshlets);
        ArgumentNullException.ThrowIfNull(report);

        if (mesh.IsSkinned && OutOfPalette(mesh) is { } offender) {
            report(
                ImportSeverity.Error,
                $"'{mesh.Name}' weights a vertex to bone {offender}, and a page vertex stores a bone "
                + $"index in one byte — so a skeleton it can page has at most "
                + $"{MeshletPageBuilder.MaxBones} bones. "
                + "The mesh has a cluster hierarchy and no pages."
            );

            return null;
        }

        try {
            return MeshletPageBuilder.Build(
                meshlets,
                mesh.Positions,
                PageAttributes(mesh),
                new() {
                    AttributeStride = mesh.IsSkinned ? SkinnedPageAttributeStride : PageAttributeStride,
                    InfluenceOffset = mesh.IsSkinned ? PageInfluenceOffset : -1
                }
            );
        } catch (ArgumentException failure) {
            // A cluster that does not fit a page, or positions the grid was not built over. Both are
            // build errors about this mesh rather than bugs in the packer, and both leave the mesh
            // with a DAG and no pages — which draws through the classic path and says why.
            report(
                ImportSeverity.Error,
                $"'{mesh.Name}' has a cluster hierarchy that could not be paged, so it has no pages. "
                + failure.Message
            );

            return null;
        }
    }

    /// <summary>
    ///     The per-vertex bytes that ride along with a quantized position, in source-vertex order.
    /// </summary>
    /// <remarks>
    ///     Halves rather than floats, and no octahedral encoding: a half has ten bits of mantissa over
    ///     a unit range, which is finer than any normal map it will be perturbed by, and an
    ///     encode/decode pair is a second place for the resolve and the raster to disagree. A mesh
    ///     missing normals or texture coordinates gets zeros for them rather than a shorter stride,
    ///     because a stride that varied per mesh would have to be carried per mesh.
    /// </remarks>
    static byte[] PageAttributes(MeshData mesh) {
        var stride = mesh.IsSkinned ? SkinnedPageAttributeStride : PageAttributeStride;
        var attributes = new byte[mesh.Positions.Length * stride];

        for (var i = 0; i < mesh.Positions.Length; i++) {
            var normal = i < mesh.Normals.Length ? mesh.Normals[i] : Vector3.Zero;
            var uv = i < mesh.TexCoords.Length ? mesh.TexCoords[i] : Vector2.Zero;
            var at = i * stride;

            Half(attributes.AsSpan(at), normal.X);
            Half(attributes.AsSpan(at + 2), normal.Y);
            Half(attributes.AsSpan(at + 4), normal.Z);
            Half(attributes.AsSpan(at + 6), uv.X);
            Half(attributes.AsSpan(at + 8), uv.Y);

            if (mesh.IsSkinned) {
                Influences(attributes.AsSpan(at + PageAttributeStride), mesh, i);
            }
        }

        return attributes;
    }

    /// <summary>
    ///     One vertex's four bone indices and four weights, a byte each.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Rounded rather than truncated, and that is not fussiness.</b> Truncating four weights
    ///         loses up to four 255ths of the total, all of it in the same direction — so every vertex
    ///         of the mesh shrinks toward the skeleton's origin by a fraction that varies with how
    ///         unlucky its weights are. The shader renormalises, which hides the magnitude of the error
    ///         and not its distribution.
    ///     </para>
    ///     <para>
    ///         A vertex with no weights at all — which a mesh can have, since <c>IsSkinned</c> is a
    ///         property of the mesh — gets the whole of bone zero rather than nothing. Nothing would
    ///         renormalise to the same thing, and saying it here means the shader's fallback is never
    ///         the path a shipped asset takes.
    ///     </para>
    /// </remarks>
    static void Influences(Span<byte> destination, MeshData mesh, int vertex) {
        var at = vertex * 4;

        if (at + 4 > mesh.BoneWeights.Length) {
            destination[..MeshletPageBuilder.InfluenceSize].Clear();
            destination[4] = byte.MaxValue;

            return;
        }

        for (var i = 0; i < 4; i++) {
            var index = at + i < mesh.BoneIndices.Length ? mesh.BoneIndices[at + i] : 0;
            var weight = Math.Clamp(mesh.BoneWeights[at + i], 0f, 1f);

            destination[i] = (byte)Math.Clamp(index, 0, MeshletPageBuilder.MaxBones - 1);
            destination[4 + i] = (byte)MathF.Round(weight * byte.MaxValue);
        }
    }

    /// <summary>The first bone index the page format cannot store, or null if every one fits.</summary>
    static int? OutOfPalette(MeshData mesh) {
        foreach (var index in mesh.BoneIndices) {
            if (index < 0 || index >= MeshletPageBuilder.MaxBones) {
                return index;
            }
        }

        return null;
    }

    static void Half(Span<byte> destination, float value) =>
        BitConverter.TryWriteBytes(destination, BitConverter.HalfToInt16Bits((System.Half)value));

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
