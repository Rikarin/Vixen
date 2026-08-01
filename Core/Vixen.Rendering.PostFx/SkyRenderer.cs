// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>
///     The background, drawn as the environment the scene is lit by rather than as a colour.
/// </summary>
/// <remarks>
///     <para>
///         <b>The node kind a document needed before a frame could have a sky at all.</b> Every other
///         full-screen pass reads what an earlier pass wrote, and a <c>sceneTextures:</c> line can
///         hand a graph resource to a shading pass — but the environment cube is neither: it is baked
///         before the frame graph exists and it outlives every frame, so no document could name it
///         and every frame ended up clearing to a colour somebody typed beside a cube somebody baked.
///         Two opinions about one sky, drifting apart at the first retune.
///     </para>
///     <para>
///         <b>What makes it possible is set 0.</b> <c>Sky.rvn</c> declares the cube <c>[PerFrame]</c>,
///         and <see cref="SceneConstants" /> writes that set for whatever effect is about to bind it
///         — under the effect's own name, so declaring <c>environment</c> in the shader is the whole
///         of the wiring. A full-screen pass already binds set 0; nothing new crosses the boundary.
///     </para>
///     <para>
///         ⚠ <b>It writes an existing target and does not declare one.</b> Naming the frame's own HDR
///         colour is what puts the sky <em>behind</em> the scene: this runs first, fills every pixel,
///         and the opaque pass that follows loads rather than clears. A pass that declared a target of
///         its own would need a composite to get it into the frame, which is a second full-screen
///         pass to avoid one clear.
///     </para>
///     <para>
///         <b>No depth, and no depth test.</b> A sky drawn last would need the depth buffer as a read
///         and a discard per pixel; drawn first it costs one write of the target, which on any
///         hardware made this century is cheaper than the alternative and is what every engine that
///         does not have a dedicated far-plane trick does.
///     </para>
/// </remarks>
public sealed class SkyRenderer() : PostEffectRenderer(
    SkyKeys.ShaderName,
    SkyKeys.UsedPermutationKeys,
    SkyKeys.ConstantBufferBinding
) {
    /// <summary>
    ///     The view whose rays the cube is sampled along.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The frame's camera, taken rather than copied.</b> Without it
    ///     <see cref="InverseViewProjection" /> stays at identity and every pixel samples the cube
    ///     along a ray built from a camera at the origin looking down −Z — a sky that is a plausible
    ///     picture of the wrong direction, which is the hardest kind of wrong to notice. A host with
    ///     no view sets the matrix itself.
    /// </remarks>
    public RenderView? View { get; set; }

    /// <summary>Clip back to world, when no <see cref="View" /> supplies it.</summary>
    public Matrix4x4 InverseViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>
    ///     A view of the prefiltered environment cube. Without one this node draws nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The host's, handed over rather than named.</b> The cube is baked before the frame
    ///     graph exists and shared by every frame, so there is no graph resource to point at — see
    ///     <see cref="ResourceBinding.View" />. Which also means the host owes the transition: this
    ///     texture must already be in <c>ShaderRead</c>, because no pass in this frame will move it.
    /// </remarks>
    public TextureViewHandle Environment { get; set; }

    /// <summary>How the cube is sampled. Linear-clamped, normally the frame's shared one.</summary>
    public SamplerHandle EnvironmentSampler { get; set; }

    /// <summary>How many mip levels the chain has, so <see cref="Soften" /> can name one.</summary>
    public float MipCount { get; set; } = 1f;

    /// <summary>Whether to sample the blurred end of the prefiltered chain rather than the sharp one.</summary>
    /// <remarks>
    ///     A weather knob rather than a quality one: on, the sun's aureole becomes a haze. Off is the
    ///     sky the surfaces below it are reflecting.
    /// </remarks>
    public bool Soften { get; set; }

    /// <summary>
    ///     A multiplier on the sampled luminance.
    /// </summary>
    /// <remarks>
    ///     One, and one is the honest default: the background and the ambient come out of the same
    ///     texels, so anything else is a frame whose sky and whose lighting disagree about the
    ///     weather. It exists because a stylised project may want exactly that.
    /// </remarks>
    public float Intensity { get; set; } = 1f;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Nothing is declared. <see cref="PostEffectRenderer.Output" /> names a resource the
    ///     document already has — the frame's HDR colour — and declaring a second texture under that
    ///     name would be a sky the scene is then drawn over the top of, in a target nobody composites.
    /// </remarks>
    protected override void DeclareOutput(CompositorFrame frame, Int2 size) {
        ArgumentNullException.ThrowIfNull(frame);

        if (!frame.Has(Output)) {
            base.DeclareOutput(frame, size);
        }
    }

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(parameters);

        // Read every frame rather than once, because the camera moves. Here rather than in a collect
        // override because this node lives outside the assembly that declares one, and this runs at
        // the same point in the frame — after the views are settled and before the pass is recorded.
        if (View is { } view && Matrix4x4.Invert(view.ViewProjection, out var inverse)) {
            InverseViewProjection = inverse;
        }

        parameters.Set(SkyKeys.Soften, Soften);
        parameters.Set(SkyKeys.InverseViewProjection, InverseViewProjection);
        parameters.Set(SkyKeys.Intensity, Intensity);
        parameters.Set(SkyKeys.EnvironmentMipCount, MipCount);

        // Handed over rather than read out of the graph, which is the whole of what makes this node
        // different from every other full-screen pass — and why it declares no read: there is nothing
        // in this frame to be ordered after.
        if (Environment.IsValid) {
            bindings.Add(
                new() { Binding = SkyKeys.EnvironmentBinding, Kind = DescriptorKind.SampledTexture, View = Environment }
            );
        }

        Sample(bindings, SkyKeys.EnvironmentSamplerBinding, EnvironmentSampler.IsValid
            ? EnvironmentSampler
            : Samplers!.LinearClamp);
    }
}
