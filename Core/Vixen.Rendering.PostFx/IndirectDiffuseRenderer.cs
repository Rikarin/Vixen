// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>The indirect diffuse a surface receives, read out of the irradiance field.</summary>
/// <remarks>
///     <para>
///         <strong>The consumer half of <c>docs/plan/19</c> § L2.</strong> Everything under it — the
///         bricks, the borders, the dilation, the refinement, the filler — exists to answer one
///         question per shaded pixel, and this is where the question gets asked.
///     </para>
///     <para>
///         <strong>A screen-space pass rather than a term inside the shading models, for now.</strong>
///         Adding a compose slot to <c>ForwardPlus</c> and <c>Deferred</c> would put an unbound slot in
///         every material in every project until each named a filler; this reads the same field through
///         the same protocol with none of that reach. What comes out is a buffer a shading pass
///         multiplies by albedo, which is what a payload stored over π is for.
///     </para>
///     <para>
///         <strong>It binds no part of the field itself.</strong> The pool, the index volume and their
///         samplers live in the frame's set 0, put there by <see cref="IrradianceFieldRenderer" />,
///         because the field belongs to the frame rather than to this pass. What this owns is the
///         depth, the normals and its own intensity. The pair has to agree on one thing and only one:
///         the compose-slot prefix those names are written under.
///     </para>
///     <para>
///         <strong>Irradiance in <c>rgb</c>, sun visibility in <c>a</c>.</strong> Both come out of the
///         same probe and the same two fetches, so keeping them apart costs nothing — and combining
///         them would cost the thing that matters, because a shading pass multiplies the first into the
///         ambient term and the second into the direct one.
///     </para>
/// </remarks>
public sealed class IndirectDiffuseRenderer() : PostEffectRenderer(
    IndirectDiffuseKeys.ShaderName,

    // None, and the generator emits no list to name because of it. This shader branches on nothing:
    // what it reads is decided by which shader fills the slot, which is a composition rather than a
    // permutation — so a key here would split the cache for variants that compile to the same bytes.
    [],
    IndirectDiffuseKeys.ConstantBufferBinding
) {
    /// <summary>The slot the shader declares for the field.</summary>
    const string Slot = "irradiance";

    /// <summary>The depth it reconstructs world positions from.</summary>
    public required string Depth { get; init; }

    /// <summary>The normals it asks the probe about.</summary>
    public required string Normals { get; init; }

    /// <summary>The shader behind the field slot — what the lookup actually reads.</summary>
    /// <remarks>
    ///     <c>NoIrradiance</c> by default, which answers no indirect light and a fully lit sun. Those
    ///     are two different right answers rather than one convenient zero: a field that is absent
    ///     contributes nothing to the ambient term, and it does not shadow the sun either — answering
    ///     zero there would put every surface in the world into shadow.
    /// </remarks>
    public string Source { get; set; } = MaterialCompiler.EmptyIrradianceShader;

    /// <summary>Clip space back to world space, for turning a depth into a position.</summary>
    /// <remarks>
    ///     World rather than view, and it has to be: the field is in world space and does not move with
    ///     the camera.
    /// </remarks>
    public Matrix4x4 InverseViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>A multiplier on the result.</summary>
    /// <remarks>
    ///     Physically it should be one. It exists because artists ask for it, and because a value that
    ///     is not one is then visible in a capture rather than folded into the field, where it would
    ///     quietly corrupt what the next bounce reads.
    /// </remarks>
    public float Intensity { get; set; } = 1f;

    /// <summary>What fraction of the frame it runs at.</summary>
    /// <remarks>
    ///     Full, unlike <see cref="DistanceFieldAoRenderer" />. The signal is as low-frequency as that
    ///     one, but the cost is not: this is two point fetches and four filtered ones per pixel rather
    ///     than a march, and halving the resolution buys little while adding an upsample that has to
    ///     respect depth discontinuities to be worth anything.
    /// </remarks>
    public float Scale { get; set; } = 1f;

    /// <inheritdoc />
    protected override Int2 TargetSize(Int2 frame) =>
        new(
            Math.Max((int)MathF.Ceiling(frame.X * Scale), 1),
            Math.Max((int)MathF.Ceiling(frame.Y * Scale), 1)
        );

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        // Without this the slot is unbound, the compiler refuses the variant, and the pass draws
        // nothing while looking exactly like a pass nobody scheduled.
        Pass.Composition = MaterialCompiler.PassComposition(Slot, Source);

        parameters.Set(IndirectDiffuseKeys.InverseViewProjection, InverseViewProjection);
        parameters.Set(IndirectDiffuseKeys.Intensity, Intensity);

        Read(bindings, IndirectDiffuseKeys.DepthBufferBinding, Depth);
        Read(bindings, IndirectDiffuseKeys.NormalBufferBinding, Normals);

        // Point, and it has to be: a bilinear tap of a depth buffer is the average of two depths,
        // which is a surface that exists nowhere — and here it would ask the field about a position
        // between two surfaces, which is where the leaks are.
        Sample(bindings, IndirectDiffuseKeys.PointSamplerBinding, Samplers!.PointClamp);
    }
}
