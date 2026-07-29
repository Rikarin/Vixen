// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>The mesh a cluster DAG is built over.</summary>
/// <remarks>
///     <para>
///         The same parallel arrays a mesh is imported as, and deliberately not that type: this
///         library sits below the render system, so a build can be checked without a device, a render
///         feature or a material anywhere in the reference graph.
///     </para>
///     <para>
///         <b>The attributes are here to find seams, not to be simplified.</b> Two vertices at one
///         position with different UVs are a seam, and a seam is locked — collapsing across one would
///         have to decide which of the two texture coordinates the survivor keeps, and either answer
///         smears a texture across the join. Everything the build does with normals, tangents, UVs
///         and weights is that comparison.
///     </para>
/// </remarks>
public sealed record MeshletBuildInput {
    /// <summary>One position per vertex.</summary>
    public Vector3[] Positions { get; init; } = [];

    /// <summary>Three vertex indices per triangle.</summary>
    public int[] Indices { get; init; } = [];

    /// <summary>One normal per vertex, or empty.</summary>
    public Vector3[] Normals { get; init; } = [];

    /// <summary>One tangent per vertex, or empty.</summary>
    public Vector4[] Tangents { get; init; } = [];

    /// <summary>One texture coordinate per vertex, or empty.</summary>
    public Vector2[] TexCoords { get; init; } = [];

    /// <summary>Four joint indices per vertex, or empty.</summary>
    public int[] BoneIndices { get; init; } = [];

    /// <summary>Four weights per vertex, or empty.</summary>
    public float[] BoneWeights { get; init; } = [];

    /// <summary>Which of the model's materials every cluster is drawn with.</summary>
    /// <remarks>
    ///     One per mesh rather than one per triangle, because an import already splits a model into a
    ///     mesh per material. A group that spanned two materials would simplify across the join and
    ///     produce a cluster that is neither.
    /// </remarks>
    public int MaterialIndex { get; init; }

    /// <summary>How many vertices there are.</summary>
    public int VertexCount => Positions.Length;

    /// <summary>How many triangles there are.</summary>
    public int TriangleCount => Indices.Length / 3;

    /// <summary>Whether it carries skinning weights.</summary>
    public bool IsSkinned => BoneWeights.Length > 0;

    /// <summary>Refuses a mesh a DAG cannot be built over.</summary>
    /// <exception cref="ArgumentException">
    ///     The indices are not whole triangles, an index is out of range, or an attribute array is
    ///     present with the wrong length.
    /// </exception>
    public void Validate() {
        if (Indices.Length % 3 != 0) {
            throw new ArgumentException("The indices are not whole triangles.", nameof(Indices));
        }

        foreach (var index in Indices) {
            if (index < 0 || index >= Positions.Length) {
                throw new ArgumentException($"Index {index} is outside the {Positions.Length} vertices.", nameof(Indices));
            }
        }

        Expect(Normals.Length, 1, nameof(Normals));
        Expect(Tangents.Length, 1, nameof(Tangents));
        Expect(TexCoords.Length, 1, nameof(TexCoords));
        Expect(BoneIndices.Length, 4, nameof(BoneIndices));
        Expect(BoneWeights.Length, 4, nameof(BoneWeights));
    }

    /// <summary>Refuses an attribute array that is present and the wrong length.</summary>
    /// <param name="length">How long it is.</param>
    /// <param name="stride">How many entries it has per vertex.</param>
    /// <param name="name">What it is called.</param>
    /// <exception cref="ArgumentException">It is present and does not match the vertex count.</exception>
    /// <remarks>
    ///     Empty is not an error and is not the same as absent-and-wrong: an attribute the file did
    ///     not have is an empty array, which is exactly what a mesh with no tangents looks like.
    /// </remarks>
    void Expect(int length, int stride, string name) {
        if (length != 0 && length != Positions.Length * stride) {
            throw new ArgumentException(
                $"{name} has {length} entries for {Positions.Length} vertices at {stride} each.",
                name
            );
        }
    }
}
