// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>
///     One indexed draw: the buffers to bind and the range to draw from them.
/// </summary>
/// <remarks>
///     <para>
///         A value type in a <see cref="RenderDataHolder" /> array, so a mesh feature reads one per
///         object with no indirection. That is why it holds <em>handles</em> rather than a reference
///         to a mesh asset: the arrays are native memory, and what a draw call needs is the handle
///         anyway.
///     </para>
///     <para>
///         One submesh, not a list. A mesh with three materials is three render objects sharing one
///         pair of buffers, which is what lets each sort into its own place — a single object with a
///         list inside it would have to pick one sort key for three different pipelines, and would
///         be drawn at the wrong depth for two of them.
///     </para>
/// </remarks>
public struct MeshDraw {
    /// <summary>The vertex buffer, bound to slot 0.</summary>
    public BufferHandle VertexBuffer;

    /// <summary>The index buffer. Invalid for a non-indexed draw.</summary>
    public BufferHandle IndexBuffer;

    /// <summary>Whether the indices are 16- or 32-bit.</summary>
    public IndexFormat IndexFormat;

    /// <summary>How many indices — or vertices, when there is no index buffer — to draw.</summary>
    public int Count;

    /// <summary>The first index into the index buffer.</summary>
    public int FirstIndex;

    /// <summary>Added to every index before the vertex is fetched.</summary>
    public int VertexOffset;

    /// <summary>How many instances. One for an ordinary draw.</summary>
    public int InstanceCount;

    /// <summary>Which vertex layout the buffer uses, as an index into the describer's table.</summary>
    /// <remarks>
    ///     An index rather than the layout itself, because a layout is a property of the vertex
    ///     <em>format</em> and a project has a handful of those — static, skinned, UI, particle. Two
    ///     meshes of the same format then share a pipeline instead of each compiling their own, which
    ///     is why this is part of <see cref="PipelineKey" />.
    /// </remarks>
    public int VertexLayout;

    /// <summary>Whether this draw has anything to draw.</summary>
    public readonly bool IsDrawable => Count > 0 && VertexBuffer.IsValid;

    /// <summary>Whether it goes through the index buffer.</summary>
    public readonly bool IsIndexed => IndexBuffer.IsValid;
}

/// <summary>
///     What a surface is drawn with: a shader, its parameters, and the descriptors they resolved to.
/// </summary>
/// <remarks>
///     <para>
///         A reference type, and the one place in the frame path that is. Everything per-object is a
///         value in an array; a material is shared by many objects and is the unit a user edits, so
///         objects hold an index into a list of these rather than a copy.
///     </para>
///     <para>
///         The <see cref="EffectKey" /> is not stored: it is derived from
///         <see cref="Parameters" /> and the effect's own <c>UsedPermutationKeys</c> whenever the
///         permutation version moves. Storing it would mean two things that must agree, and the one
///         that goes stale is the one that decides which shader runs.
///     </para>
/// </remarks>
public sealed class Material(string shaderName) {
    /// <summary>The shader this material is a variant of.</summary>
    public string ShaderName { get; } = shaderName;

    /// <summary>The values and permutations set on it.</summary>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>Which implementation fills each of the shader's <c>compose</c> slots.</summary>
    /// <remarks>
    ///     <para>
    ///         The material's features, as the compiler resolved them: a metal-roughness surface
    ///         under a clear coat is a composition, not a set of values. Empty for a material whose
    ///         shader declares no slots, which is every post-process effect and the depth-only pass.
    ///     </para>
    ///     <para>
    ///         Set once rather than settable, because it selects which shader exists. A material that
    ///         changed its features after an object had resolved a variant would keep drawing with
    ///         the shader compiled for the old ones — where a value written into the parameter
    ///         collection takes effect on the next frame. Changing a material's features means
    ///         building another material, which is also what the editor's undo stack wants.
    ///     </para>
    /// </remarks>
    public ShaderComposition Composition { get; init; }

    /// <summary>The descriptor set holding this material's resources.</summary>
    /// <remarks>
    ///     Built by whatever owns the material's textures rather than here. A material that has none
    ///     leaves it invalid and nothing is bound, which is what an untextured debug material wants.
    /// </remarks>
    public DescriptorSetHandle Descriptors { get; set; }

    /// <inheritdoc />
    public override string ToString() => ShaderName;
}
