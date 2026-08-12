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
public sealed class ComputeRenderer : SceneRenderer, IDisposable {
    readonly Dictionary<string, GraphBuffer> buffers = new(StringComparer.Ordinal);
    readonly Dictionary<string, GraphTexture> textures = new(StringComparer.Ordinal);

    EffectConstants? constants;

    /// <summary>The compute shader to run.</summary>
    public required string ShaderName { get; init; }

    /// <summary>
    ///     The permutations selecting which variant of it, and the values its block is filled from.
    /// </summary>
    /// <remarks>
    ///     Both, from one collection, which is what <see cref="ParameterCollection" /> is for and what
    ///     <see cref="FullScreenRenderer" /> already did. A compute pass that had somewhere to put its
    ///     permutations and nowhere to put its <em>values</em> is a pass whose uniforms a host has to
    ///     bind through <see cref="OnBind" /> — see <see cref="ConstantBinding" />.
    /// </remarks>
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

    /// <summary>The names of storage images it binds and produces nothing in.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>For the storage binding a variant has to fill and never stores to</b>, which is what
    ///         every multi-mode compute shader in the library ends up with: a descriptor set is written
    ///         whole or not at all, so a dispatch that has no use for the image its sibling variant
    ///         writes still has to name one. <c>AutoExposure</c>'s histogram is the case — its clear,
    ///         its build and its resolve all bind <c>target</c> and <c>average</c>, and not one of the
    ///         three stores a texel.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Neither <see cref="Reads" /> nor <see cref="Writes" /> says this, and both say
    ///         something false.</b> <see cref="Writes" /> claims a result, so a run of passes that each
    ///         bind the same image reads to the graph as a frame's work overwritten before anybody
    ///         looked — which is VX2101, correctly reported against a declaration that was wrong.
    ///         <see cref="Reads" /> claims contents <em>and</em> asks for the read-only layout, and a
    ///         storage descriptor is written with <c>General</c>: the image would be bound in a layout
    ///         the dispatch is not allowed to bind it in. So this declares the use the graph needs to
    ///         see — the resource is live here and must arrive in <see cref="ResourceState.ShaderWrite" />
    ///         — and no production at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The image has to be imported.</b> Nothing writes it, and the graph refuses a read
    ///         of a transient no earlier pass produces — rightly, because the contents of one are last
    ///         frame's memory. An import is memory somebody else owns and is answerable without a
    ///         producer, which is the same reason <c>VolumetricFogRenderer</c> owns its shadow
    ///         stand-in rather than declaring one.
    ///     </para>
    /// </remarks>
    public IList<string> Bound { get; } = [];

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

    /// <summary>What fills the compose slots the compilation declares.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same fix <see cref="FullScreenRenderer.Composition" /> carries, and this type
    ///         was missed when that one was made.</b> A compilation is the whole library and every
    ///         compose slot any shader in it declares must be bound — RVN2073 — so a dispatch that has
    ///         no opinion about a material's third surface feature still has to name one.
    ///         <c>MaterialCompiler.PassComposition</c>'s own remarks predict this exact case: "a
    ///         compute pass sharing a package with a shader that declares <c>distanceField</c> is
    ///         refused unless it names a filler for a slot it has never heard of."
    ///     </para>
    ///     <para>
    ///         Without it <em>every</em> <c>!Compute</c> node in every document was refused, and it did
    ///         not fail quietly: the compiler throws from inside
    ///         <c>GraphicsCompositor.Build</c> — half way through a frame — with the whole library's
    ///         unbound slots as the message, naming files the document has never mentioned.
    ///         <c>!AutoExposure</c> is the node that found it, because it is the only compute shader
    ///         in the post-effect library and so the only one a frame reaches through this path.
    ///     </para>
    /// </remarks>
    public ShaderComposition Composition { get; set; } = Materials.MaterialCompiler.PassComposition();

    /// <summary>The set it writes for itself, out of the resources it declared.</summary>
    /// <remarks>
    ///     Its <see cref="DescriptorBindings.Layout" /> can be left unset and taken from the resolved
    ///     effect's <see cref="Effect.SetLayouts" />, which is where the layout the pipeline was built
    ///     from actually lives — supplying a different one is how a set gets bound to a pipeline it is
    ///     not compatible with.
    /// </remarks>
    public DescriptorBindings Descriptors { get; } = new() { Slot = DescriptorSetSlot.PerMaterial };

    /// <summary>
    ///     Which binding this shader's own uniform block occupies, or null for one with none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The half of a compute pass that had no way in. A node could declare the buffers and
    ///         textures it read and wrote, and the values beside them — a camera, a count, a
    ///         threshold — had to be bound through <see cref="OnBind" />, which means a host building
    ///         a buffer, filling it, and writing a descriptor by hand. <c>ClusterCulling.rvn</c> is
    ///         the case that made it visible: it takes the camera's half-angle tangents, its planes,
    ///         its view matrix and a light count, and none of them could be written.
    ///     </para>
    ///     <para>
    ///         Filled from <see cref="Parameters" /> at the offsets the effect's plan gives, at build
    ///         time rather than in the pass body — writing a host-visible buffer inside a command list
    ///         is a map and a copy between two dispatches.
    ///     </para>
    ///     <para>
    ///         It rides in the set <see cref="Descriptors" /> writes, so that set has to be configured
    ///         — an allocator, and at least one binding. That is not a limitation a compute pass can
    ///         run into: one that binds no buffer and no storage image has nowhere to put its result,
    ///         so it is a pass with no output rather than a pass this cannot serve.
    ///     </para>
    /// </remarks>
    public uint? ConstantBinding { get; set; }

    /// <summary>The block as it was last filled, for a test or an inspector.</summary>
    /// <remarks>
    ///     What the GPU was given, which is the only way to check that a value landed at the offset
    ///     the shader's plan said — a device that took the bytes cannot be asked what they were.
    /// </remarks>
    public ReadOnlySpan<byte> Constants => constants is { } filled ? filled.Bytes : default;

    /// <summary>How many times the block has actually gone to the GPU.</summary>
    /// <remarks>
    ///     For the test that a pass whose values did not change is not re-uploading them, which is
    ///     the whole reason <see cref="ParameterCollection.Version" /> exists.
    /// </remarks>
    public int UploadCount => constants?.UploadCount ?? 0;

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

        var key = EffectKey.From(ShaderName, Parameters, PermutationKeys, Composition);

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

        foreach (var name in Reads.Concat(Writes).Concat(Bound)) {
            textures[name] = frame.Texture(ToString(), name);
        }

        // The layout the effect was compiled with, unless the host insisted on one of its own. A set
        // is only bindable to a pipeline whose layout it was allocated from, so guessing here would
        // produce something the validation layers reject and a release driver does not.
        if (!Descriptors.Layout.IsValid && (int)Descriptors.Slot < effect.SetLayouts.Length) {
            Descriptors.Layout = effect.SetLayouts[(int)Descriptors.Slot];
        }

        var bound = Descriptors.Resolve(ToString(), textures, buffers, effect, Samplers);

        // Filled here rather than in the pass body: the values are the host's, and writing a
        // host-visible buffer inside a command list is a map and a copy between two dispatches.
        constants ??= frame.Device is { } device ? new(device, $"{this}.Constants") : null;
        var hasConstants = ConstantBinding is not null && constants?.Update(effect, Parameters) == true;

        // At the block's own offset, not at zero: the buffer holds one region per frame in flight, so
        // changing a value does not overwrite what an unfinished frame is reading.
        var extra = hasConstants
            ? new[] {
                DescriptorWrite.Uniform(
                    ConstantBinding!.Value,
                    constants!.Buffer,
                    constants.Offset,
                    constants.Size
                )
            }
            : [];

        var bufferReads = BufferReads.Select(name => buffers[name]).ToArray();
        var bufferWrites = BufferWrites.Select(name => buffers[name]).ToArray();
        var textureReads = Reads.Select(name => textures[name]).ToArray();
        var textureWrites = Writes.Select(name => textures[name]).ToArray();
        var textureBound = Bound.Select(name => textures[name]).ToArray();
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

                // A use and not a production — see Bound. The state is the one a storage descriptor
                // is written with, so the barrier that lands the image in General is placed and the
                // graph is told nothing about contents that do not exist.
                foreach (var bound in textureBound) {
                    pass.Reads(bound, ResourceState.ShaderWrite);
                }

                pass.Execute(
                    context => {
                        context.CommandList.BindPipeline(pipeline);
                        bound?.Bind(context, extra);
                        OnBind?.Invoke(new(context, this) { Effect = effect });
                        context.CommandList.Dispatch(groups.X, groups.Y, groups.Z);
                    }
                );
            }
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        constants?.Dispose();
        constants = null;
    }
}
