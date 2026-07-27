// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Shaders;

namespace Vixen.Rendering.Compositor;

/// <summary>What a compute node's body is given to bind its own resources with.</summary>
/// <remarks>
///     A callback rather than a descriptor set on the node, because the buffers a compute pass reads
///     are render-graph resources: the handle does not exist until the graph has allocated it, and
///     may be a different one next frame. So the node declares the dependency — which is what orders
///     the passes and places the barrier — and the host, which owns the descriptor pool, writes the
///     set with the handles this hands it.
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
    readonly Dictionary<string, GraphBuffer> resolved = new(StringComparer.Ordinal);

    /// <summary>The compute shader to run.</summary>
    public required string ShaderName { get; init; }

    /// <summary>The permutations selecting which variant of it.</summary>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>Which permutation keys the shader's variants are selected by.</summary>
    public IReadOnlyList<ParameterKey> PermutationKeys { get; set; } = [];

    /// <summary>The names of buffers the dispatch reads.</summary>
    public IList<string> Reads { get; } = [];

    /// <summary>The names of buffers it writes.</summary>
    public IList<string> Writes { get; } = [];

    /// <summary>How many workgroups to run.</summary>
    public Int3 Groups { get; set; } = new(1, 1, 1);

    /// <summary>Where compute pipelines come from. Set before the first frame that builds.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>What binds the pass's own descriptor sets, before the dispatch.</summary>
    public Action<ComputeDispatch>? OnBind { get; init; }

    /// <summary>The buffer a name resolved to this frame, for <see cref="ComputeDispatch" />.</summary>
    internal GraphBuffer Resolved(string name) =>
        resolved.TryGetValue(name, out var buffer)
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

        resolved.Clear();

        foreach (var name in Reads) {
            resolved[name] = frame.Buffer(ToString(), name);
        }

        foreach (var name in Writes) {
            resolved[name] = frame.Buffer(ToString(), name);
        }

        var reads = Reads.Select(name => resolved[name]).ToArray();
        var writes = Writes.Select(name => resolved[name]).ToArray();
        var groups = Groups;

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Kind = PassKind.Compute;

                foreach (var read in reads) {
                    pass.Reads(read);
                }

                foreach (var write in writes) {
                    pass.Writes(write);
                }

                pass.Execute(
                    context => {
                        context.CommandList.BindPipeline(pipeline);
                        OnBind?.Invoke(new(context, this) { Effect = effect });
                        context.CommandList.Dispatch(groups.X, groups.Y, groups.Z);
                    }
                );
            }
        );
    }
}
