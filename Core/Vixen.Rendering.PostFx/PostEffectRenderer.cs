// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;

namespace Vixen.Rendering.PostFx;

/// <summary>
///     One post-process effect: a shader, the textures it reads, and the target it writes.
/// </summary>
/// <remarks>
///     <para>
///         Every effect in this project is one of these, and almost none of them is more. The work a
///         post pass does — three vertices, one draw, a descriptor set of source textures and a
///         uniform block — is <see cref="FullScreenRenderer" />'s already; what an effect adds is
///         which shader, which permutations, which textures go on which bindings, and its own
///         parameters. So this holds the plumbing and a subclass answers four questions.
///     </para>
///     <para>
///         <strong>The node declares its own output.</strong> An effect that read a target somebody
///         else had to remember to declare would be an effect you cannot drop into a chain — and the
///         graph's whole argument is that a pass nothing reads costs nothing, which only works if the
///         resource it writes is the graph's to alias and drop.
///     </para>
///     <para>
///         <strong>The pass is kept between frames.</strong> It owns a pipeline cache and a uniform
///         buffer, so rebuilding it every frame would recompile the same pipeline and reallocate the
///         same buffer — most of what a post chain can get wrong, and invisible until a profile.
///     </para>
/// </remarks>
public abstract class PostEffectRenderer : SceneRenderer, IDisposable {
    readonly FullScreenRenderer pass;
    bool disposed;

    /// <summary>Creates the node for one shader.</summary>
    /// <param name="shaderName">The shader, from <c>Raven/Library/PostFx</c>.</param>
    /// <param name="permutationKeys">
    ///     The keys its variants are selected by — the generated <c>UsedPermutationKeys</c>. A key
    ///     the shader does not branch on must not be here, or the cache splits for variants that
    ///     compile to the same bytes.
    /// </param>
    /// <param name="constantBinding">Where its uniform block goes, from the generated constants.</param>
    protected PostEffectRenderer(
        string shaderName,
        IReadOnlyList<ParameterKey> permutationKeys,
        uint constantBinding
    ) {
        ArgumentException.ThrowIfNullOrEmpty(shaderName);
        ArgumentNullException.ThrowIfNull(permutationKeys);

        pass = new() {
            // The pass carries the shader's name rather than the node's, because it is what a
            // RenderDoc capture and the graph's pass list are read by — and a node's own name is set
            // by whoever built the chain, after this.
            Name = shaderName,
            ShaderName = shaderName,
            PermutationKeys = permutationKeys,
            ConstantBinding = constantBinding
        };
    }

    /// <summary>The name this effect publishes its result under.</summary>
    public string Output { get; init; } = "PostFx";

    /// <summary>The format of the target it declares.</summary>
    /// <remarks>
    ///     Floating point by default, because a post chain runs in linear HDR: an effect writing an
    ///     eight-bit target part way through the chain clips every highlight the tonemap was going to
    ///     bring back, which reads as a blown-out frame rather than as a format.
    /// </remarks>
    public PixelFormat Format { get; set; } = PixelFormat.Rgba16Float;

    /// <summary>Where shader modules come from. Set before the first frame that builds.</summary>
    public EffectPipelineDescriber? Modules { get; set; }

    /// <summary>Where samplers come from, shared so a chain does not exhaust the device's supply.</summary>
    public SamplerCache? Samplers { get; set; }

    /// <summary>Where the pass's descriptor sets come from.</summary>
    public DescriptorAllocator? Allocator { get; set; }

    /// <summary>The device its pipelines and its uniform block are created on.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>The pass this effect is, for a host that wants to look at what it did.</summary>
    public FullScreenRenderer Pass => pass;

    /// <summary>The frame's set 0, for an effect whose shader declares anything per-frame.</summary>
    /// <remarks>
    ///     Null for almost every one of these — see <see cref="FullScreenRenderer.SceneConstants" />
    ///     for the exception and for why nothing else in the path would bind it.
    /// </remarks>
    public SceneConstants? SceneConstants {
        get => pass.SceneConstants;
        set => pass.SceneConstants = value;
    }

    /// <summary>The size the effect's own target is, given the frame's.</summary>
    /// <remarks>
    ///     The frame's by default. An effect that runs at half resolution — ambient occlusion is the
    ///     usual one — overrides this, and the graph sizes its target accordingly.
    /// </remarks>
    protected virtual Int2 TargetSize(Int2 frame) => frame;

    /// <summary>Fills in this frame's parameters and bindings.</summary>
    /// <param name="frame">The frame being built, for its size.</param>
    /// <param name="parameters">The pass's parameters, to set.</param>
    /// <param name="bindings">The pass's bindings, already cleared.</param>
    protected abstract void Configure(CompositorFrame frame, ParameterCollection parameters, IList<ResourceBinding> bindings);

    /// <summary>Adds a sampled texture to the pass, and records that it is read.</summary>
    /// <remarks>
    ///     The two halves have to happen together: the binding is what the shader reads it through,
    ///     and the read is what orders this pass after whatever wrote it and keeps that producer from
    ///     being culled. One without the other is either a validation error or a race.
    /// </remarks>
    protected void Read(IList<ResourceBinding> bindings, uint binding, string resource) {
        ArgumentNullException.ThrowIfNull(bindings);

        if (string.IsNullOrEmpty(resource)) {
            return;
        }

        pass.Reads.Add(resource);
        bindings.Add(new() { Binding = binding, Kind = DescriptorKind.SampledTexture, Resource = resource });
    }

    /// <summary>Adds a sampler to the pass.</summary>
    protected static void Sample(IList<ResourceBinding> bindings, uint binding, SamplerHandle sampler) {
        ArgumentNullException.ThrowIfNull(bindings);
        bindings.Add(new() { Binding = binding, Kind = DescriptorKind.Sampler, Sampler = sampler });
    }

    /// <summary>Declares the resource this effect writes.</summary>
    /// <remarks>
    ///     Transient, because that is what a post-process target is: written by this pass, read by the
    ///     next, dead by the end of the frame — which is exactly the lifetime the graph can alias and,
    ///     if nothing reads it, skip. An effect whose output has to <em>survive</em> the frame
    ///     overrides this and imports instead; <see cref="TemporalAntialiasingRenderer" /> is the one
    ///     that does, because its output is the next frame's history.
    /// </remarks>
    protected virtual void DeclareOutput(CompositorFrame frame, Int2 size) {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Has(Output)) {
            return;
        }

        frame.Add(
            Output,
            frame.Graph.CreateTexture(
                new(Format, size.X, size.Y, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: Output)
            ),
            Format
        );
    }

    /// <summary>One texel of a texture of the given size, which most of these shaders want.</summary>
    protected static Vector2 TexelSize(Int2 size) =>
        new(1f / Math.Max(size.X, 1), 1f / Math.Max(size.Y, 1));

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (Modules is null || Samplers is null) {
            return;
        }

        DeclareOutput(frame, TargetSize(frame.Size));

        pass.Modules = Modules;
        pass.Device = Device;
        pass.Samplers = Samplers;
        pass.Descriptors.Allocator = Allocator;

        pass.ColourTargets.Clear();
        pass.ColourTargets.Add(Output);

        pass.Reads.Clear();
        pass.Descriptors.Bindings.Clear();

        Configure(frame, pass.Parameters, pass.Descriptors.Bindings);

        BuildChild(pass, compositor, frame);
    }

    /// <inheritdoc />
    public void Dispose() {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the pass's pipelines and its uniform buffer.</summary>
    protected virtual void Dispose(bool disposing) {
        if (disposed) {
            return;
        }

        if (disposing) {
            pass.Dispose();
        }

        disposed = true;
    }
}
