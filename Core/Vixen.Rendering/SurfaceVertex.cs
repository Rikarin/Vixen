// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Rendering;

/// <summary>One vertex of a lit surface, in the layout the shading stages declare.</summary>
/// <remarks>
///     <para>
///         <b>Position, normal, tangent, texture coordinate — in that order, and the order is the
///         format.</b> <c>ForwardPlus.rvn</c>'s vertex stage takes exactly these four and
///         <see cref="Locations" /> carries the numbers its reflection reports. A layout that named a
///         location the stage does not declare binds nothing to that attribute and the stage reads
///         whatever the driver left there, which is a mesh drawn with its normals in another slot, on
///         one driver, silently — see <see cref="VertexLocations" />.
///     </para>
///     <para>
///         <b>Interleaved, and one layout rather than a layout per mesh.</b> Every surface goes through
///         one <see cref="GeometryBuffer" /> so that meshes share a vertex and index buffer and a run of
///         draws can become one command; a buffer has a single stride, so the stride is a decision made
///         once here rather than per asset. <see cref="MeshData" /> is stored the other way round —
///         parallel typed arrays — because a compiler decides what to keep, and this is where that
///         decision has already been made.
///     </para>
///     <para>
///         ⚠ <b>Forty-eight bytes, uncompressed, and that is not the end state.</b> Half-float
///         texture coordinates and an octahedral normal-tangent pair would make it twenty-four, which
///         is the difference between fitting a level's geometry in a cache and not. Doing that needs
///         the shader side to agree, so it is a change to this struct and to <c>ForwardPlus.rvn</c>
///         together — and it is worth nothing until something is drawing at all.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct SurfaceVertex {
    /// <summary>Where it is, in the mesh's own space.</summary>
    public Vector3 Position;

    /// <summary>Which way the surface faces.</summary>
    public Vector3 Normal;

    /// <summary>The tangent, with the bitangent's handedness in <c>w</c>.</summary>
    public Vector4 Tangent;

    /// <summary>Where it samples a texture.</summary>
    public Vector2 TexCoord;

    /// <summary>How many bytes one of these is.</summary>
    public static int SizeInBytes => Marshal.SizeOf<SurfaceVertex>();

    /// <summary>Where each attribute binds, from <c>ForwardPlus</c>'s reflected vertex inputs.</summary>
    /// <remarks>
    ///     ⚠ <b><c>ForwardPlus</c>'s numbers, which is to say one pass's.</b> Correct for the pass
    ///     it was read off and wrong for every other — <c>ShadowCaster</c> declares one stream
    ///     against <c>ForwardPlus</c>'s six, so its <c>position</c> is location 1. Prefer
    ///     <see cref="Schema" />, which carries no numbers at all and takes them from whichever
    ///     effect is being drawn with. This stays for the renderers that are handed locations by a
    ///     caller rather than an effect — see <see cref="VertexLocations" />.
    /// </remarks>
    public static VertexLocations Locations => new(6u, 7u, 8u, 9u);

    /// <summary>These four attributes under the names a Raven vertex stage declares them by.</summary>
    /// <remarks>
    ///     The names are the shaders' parameter names, and they have to be: <see cref="VertexSchema" />
    ///     matches by name, so <c>texcoord</c> here and <c>uv</c> in a stage is an attribute the
    ///     pipeline refuses to bind. Every surface stage in the library — <c>ForwardPlus</c>,
    ///     <c>GBufferPass</c>, <c>ShadowCaster</c>, <c>DepthOnly</c> — spells them this way.
    /// </remarks>
    public static VertexSchema Schema { get; } = new(
        SizeInBytes,
        new("position", VertexFormat.Float32X3, 0),
        new("normal", VertexFormat.Float32X3, 12),
        new("tangent", VertexFormat.Float32X4, 24),
        new("texcoord", VertexFormat.Float32X2, 40)
    );
}
