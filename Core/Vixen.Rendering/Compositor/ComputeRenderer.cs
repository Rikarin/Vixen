// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Shaders;

namespace Vixen.Rendering.Compositor;

/// <summary>What a compute node's body is given to bind anything its declaration cannot express.</summary>
/// <remarks>
///     The escape hatch, no longer the mechanism. A node with
///     <see cref="ComputeRenderer.Descriptors" /> configured writes its own set out of the resources
///     it declared; this stays for the bindings that are not frame resources at all — a persistent
///     buffer the compositor never hears about, or a second set the node has no way to name.
/// </remarks>
public sealed class ComputeDispatch {
    internal ComputeDispatch(RenderGraphContext context, ComputeRenderer node) {
        Context = context;
        Node = node;
    }

    /// <summary>The command list the dispatch is recorded into.</summary>
    public ICommandList CommandList => Context.CommandList;

    /// <summary>The node being dispatched.</summary>
    public ComputeRenderer Node { get; }

    /// <summary>The effect resolved for this dispatch.</summary>
    public required Effect Effect { get; init; }

    /// <summary>The buffer a name resolved to, for this frame.</summary>
    public BufferHandle Buffer(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Context.Buffer(Node.Resolved(name));
    }

    RenderGraphContext Context { get; }
}

/// <summary>
///     A compute pass: what it reads, what it writes, and how many groups of it to run.
/// </summary>
/// <remarks>
///     <para>
///         The node that makes clustered light culling — and every other compute stage in doc 06's
///         list — a thing the compositor can express. Its whole value over a hand-written dispatch is
///         the two lists: a pass that says it writes the cluster buffer, next to a pass that says it
///         reads it, is a pass the graph orders first and puts a barrier after. That edge is the one
///         nobody maintains correctly by hand, and it is the reason a compute pass in a frame graph
///         is worth more than a compute pass in a function.
///     </para>
///     <para>
///         The effect is resolved through the ordinary <see cref="EffectSystem" />, so a compute
///         shader is permuted, cached and baked exactly like a graphics one — and a shipping build
///         cannot compile one for the same structural reason it cannot compile a vertex shader.
///     </para>
/// </remarks>
public sealed class ComputeRenderer : SceneRenderer {
    readonly Dictionary<string, GraphBuffer> buffers = new(StringComparer.Ordinal);
    readonly Dictionary<string, GraphTexture> textures = new(StringComparer.Ordinal);

    /// <summary>The compute shader to run.</summary>
    public required string ShaderName { get; init; }

    /// <summary>The permutations selecting which variant of it.</summary>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>Which permutation keys the shader's variants are selected by.</summary>
    public IReadOnlyList<ParameterKey> PermutationKeys { get; set; } = [];

    /// <summary>The names of buffers the dispatch reads.</summary>
    public IList<string> BufferReads { get; } = [];

    /// <summary>The names of buffers it writes.</summary>
    public IList<string> BufferWrites { get; } = [];

    /// <summary>The names of textures it samples or reads.</summary>
    public IList<string> Reads { get; } = [];

    /// <summary>The names of textures it writes, as storage images.</summary>
    /// <remarks>
    ///     The half of a compute pass that a bloom chain, a GTAO pass or a mip generator is made of.
    ///     Separate from <see cref="BufferWrites" /> only because the graph tracks the two resource
    ///     kinds separately; the edge either declares is the same one.
    /// </remarks>
    public IList<string> Writes { get; } = [];

    /// <summary>How many workgroups to run.</summary>
    public Int3 Groups { get; set; } = new(1, 1, 1);

    /// <summary>Where a described sampler comes from, for a binding that names one by value.</summary>
    /// <remarks>
    ///     Shared rather than owned, because a sampler is pure state and a device caps how many exist
    ///     — a chain of post passes each making its own reaches that cap on drivers that allow four
    ///     thousand.
    /// </remarks>
    public SamplerCache? Samplers { get; set; }

    /// <summary>Where compute pipelines come from. Set before the first frame that builds.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>The set it writes for itself, out of the resources it declared.</summary>
    /// <remarks>
    ///     Its <see cref="DescriptorBindings.Layout" /> can be left unset and taken from the resolved
    ///     effect's <see cref="Effect.SetLayouts" />, which is where the layout the pipeline was built
    ///     from actually lives — supplying a different one is how a set gets bound to a pipeline it is
    ///     not compatible with.
    /// </remarks>
    public DescriptorBindings Descriptors { get; } = new() { Slot = DescriptorSetSlot.PerMaterial };

    /// <summary>What binds anything the declaration cannot express, before the dispatch.</summary>
    public Action<ComputeDispatch>? OnBind { get; init; }

    /// <summary>The buffer a name resolved to this frame, for <see cref="ComputeDispatch" />.</summary>
    internal GraphBuffer Resolved(string name) =>
        buffers.TryGetValue(name, out var buffer)
            ? buffer
            : throw new CompositorBindingException(ToString(), "buffer", name);

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (Pipelines is null || Groups.X <= 0 || Groups.Y <= 0 || Groups.Z <= 0) {
            return;
        }

        var key = EffectKey.From(ShaderName, Parameters, PermutationKeys);

        if (frame.Effects.Resolve(key) is not { } effect) {
            // Nothing to dispatch and nothing to guess at. A missing compute variant is reported by
            // EffectSystem.Misses like every other, which is what makes "no runtime compilation in a
            // shipping build" a test rather than a hope.
            return;
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid) {
            return;
        }

        buffers.Clear();
        textures.Clear();

        foreach (var name in BufferReads.Concat(BufferWrites)) {
            buffers[name] = frame.Buffer(ToString(), name);
        }

        foreach (var name in Reads.Concat(Writes)) {
            textures[name] = frame.Texture(ToString(), name);
        }

        // The layout the effect was compiled with, unless the host insisted on one of its own. A set
        // is only bindable to a pipeline whose layout it was allocated from, so guessing here would
        // produce something the validation layers reject and a release driver does not.
        if (!Descriptors.Layout.IsValid && (int)Descriptors.Slot < effect.SetLayouts.Length) {
            Descriptors.Layout = effect.SetLayouts[(int)Descriptors.Slot];
        }

        var bound = Descriptors.Resolve(ToString(), textures, buffers, effect, Samplers);
        var bufferReads = BufferReads.Select(name => buffers[name]).ToArray();
        var bufferWrites = BufferWrites.Select(name => buffers[name]).ToArray();
        var textureReads = Reads.Select(name => textures[name]).ToArray();
        var textureWrites = Writes.Select(name => textures[name]).ToArray();
        var groups = Groups;

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Kind = PassKind.Compute;

                foreach (var read in bufferReads) {
                    pass.Reads(read);
                }

                foreach (var write in bufferWrites) {
                    pass.Writes(write);
                }

                foreach (var read in textureReads) {
                    pass.Reads(read);
                }

                foreach (var write in textureWrites) {
                    pass.Writes(write);
                }

                pass.Execute(
                    context => {
                        context.CommandList.BindPipeline(pipeline);
                        bound?.Bind(context);
                        OnBind?.Invoke(new(context, this) { Effect = effect });
                        context.CommandList.Dispatch(groups.X, groups.Y, groups.Z);
                    }
                );
            }
        );
    }
}
