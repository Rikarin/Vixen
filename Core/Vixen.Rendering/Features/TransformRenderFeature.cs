// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Rendering.Features;

/// <summary>
///     Where each object is, and getting that to the shader.
/// </summary>
/// <remarks>
///     <para>
///         A sub-feature rather than a field on <see cref="RenderObject" />, and it is the clearest
///         case for why: a UI quad in screen space and a particle billboard built in the vertex
///         shader both have bounds and both need culling, and neither has a world matrix. Putting
///         one on every object would make them carry sixty-four bytes to say nothing.
///     </para>
///     <para>
///         <strong>Push constants, not a uniform buffer.</strong> The transform is the smallest and
///         most per-draw thing a frame has, which is exactly what push constants are: they travel in
///         the command buffer with no descriptor, no allocation from an upload ring and no offset to
///         track. A <c>mat4</c> is 64 bytes against Vulkan's guaranteed 128, so it fits everywhere
///         with room to spare — and Raven warns at <c>RVN3007</c> if a shader's block exceeds that,
///         so the two sides agree about the budget.
///     </para>
///     <para>
///         The matrix is sent as-is: the engine stores row-major with the translation in
///         <c>M41..M43</c>, the shader reads the same bytes as <c>ColMajor</c>, and that makes its
///         matrix the transpose <c>mul(v, M)</c> wants. Transposing here would compute the wrong
///         transform more expensively — see docs/plan/07 § E.
///     </para>
/// </remarks>
public sealed class TransformRenderFeature : SubRenderFeature, IDrawSubFeature {
    /// <inheritdoc />
    public override string Name => "Transform";

    /// <summary>Each object's object-to-world matrix.</summary>
    public RenderDataKey<Matrix4x4> World { get; private set; }

    /// <summary>Which stages the transform is pushed to. Vertex only, by default.</summary>
    public ShaderStage Stages { get; set; } = ShaderStage.Vertex;

    /// <summary>Where in the push-constant block the transform goes.</summary>
    public int Offset { get; set; }

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) =>
        World = system.Objects.Data.Register<Matrix4x4>();

    /// <inheritdoc />
    public void Draw(RenderSystem system, RenderDrawContext context, in RenderNode node) {
        var world = system.Objects.Data.Data(World)[node.Object.Index];

        // The struct's bytes directly. Sequential layout with sixteen floats in M11..M44 order is
        // what the shader reads, so there is nothing to serialise — see the class remarks.
        context.CommandList.PushConstants(
            Stages,
            Offset,
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref world, 1))
        );
    }
}
