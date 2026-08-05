// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Shaders;

namespace Vixen.Rendering.Compositor;

/// <summary>Where one of a node's graph resources goes in the descriptor set it binds.</summary>
/// <remarks>
///     <para>
///         The binding index comes from the shader, not from the compositor: Raven decides what
///         <c>binding = 3</c> means. <see cref="Name" /> is how to say that without writing the number
///         down — it resolves against <see cref="Effect.Bindings" />, the plan the reflection always
///         had and the runtime now carries. <see cref="Binding" /> remains for a provider that reports
///         no plan.
///     </para>
///     <para>
///         <see cref="Kind" /> decides where <see cref="Resource" /> is looked up. A texture kind
///         resolves against what the node declared as a texture, a buffer kind against its buffers,
///         and <see cref="DescriptorKind.Sampler" /> against neither — a sampler is not a frame
///         resource, it is device state that outlives every graph.
///     </para>
/// </remarks>
public sealed class ResourceBinding {
    /// <summary>
    ///     The shader's own name for it, resolved against the effect's binding plan.
    /// </summary>
    /// <remarks>
    ///     The way to say where something goes without writing down a number the shader owns. Raven
    ///     assigns a binding index from declaration order within a set, so adding a texture above
    ///     another renumbers everything below it — and a host holding the old number gets a
    ///     validation error at best. Set this and leave <see cref="Binding" /> alone; the explicit
    ///     index stays for a provider whose effects do not report their plan.
    /// </remarks>
    public string? Name { get; init; }

    /// <summary>Its index within the set, when <see cref="Name" /> is not used or not found.</summary>
    public uint Binding { get; init; }

    /// <summary>What it binds, which is also what decides how <see cref="Resource" /> resolves.</summary>
    /// <remarks>Taken from the effect's plan when <see cref="Name" /> resolves against one.</remarks>
    public DescriptorKind Kind { get; init; }

    /// <summary>The graph resource's name, for every kind but a bare sampler.</summary>
    public string Resource { get; init; } = "";

    /// <summary>The sampler, for <see cref="DescriptorKind.Sampler" />.</summary>
    public SamplerHandle Sampler { get; init; }

    /// <summary>
    ///     A view of a texture the <em>host</em> owns, for a binding that is not a graph resource.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The texture counterpart of <see cref="Sampler" />, and it exists for the same
    ///         reason.</b> A sampler is pure state that outlives every frame, so it is handed over as
    ///         a handle rather than named; an environment cube is exactly as long-lived — baked
    ///         before the frame graph exists and shared by every frame — and had no way to reach a
    ///         node at all. <c>SkyRenderer</c> is what needed it: the background is the cube the scene
    ///         is lit by, and no <c>Resource</c> name could point at it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It declares no edge, because there is nothing to order against.</b> A graph
    ///         resource's name is what puts the barrier in and keeps its producer from being culled;
    ///         a host texture has no producer in this frame, so the host owes the transition — once,
    ///         at upload — exactly as it does for the samplers beside it. Wrong for anything a pass
    ///         writes, and the graph cannot tell you so.
    ///     </para>
    ///     <para>
    ///         Consulted only when <see cref="Resource" /> is empty, so a binding that names a graph
    ///         texture is unaffected.
    ///     </para>
    /// </remarks>
    public TextureViewHandle View { get; init; }

    /// <summary>
    ///     A buffer the <em>host</em> owns, for a binding that is not a graph resource.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="View" />'s counterpart for the other resource kind, and it covers the case
    ///         a name cannot: a buffer that reaches its consumer as a <em>handle</em>. The frame's
    ///         light list and its cluster lists travel that way —
    ///         <c>ForwardLightingRenderFeature.Publish</c> and <c>RenderPassRenderer.SceneBuffers</c>
    ///         write handles into the scene's parameters — so a node outside the pass that published
    ///         them has nothing to resolve a <see cref="Resource" /> against.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It declares no edge, on <see cref="View" />'s terms and for a sharper reason.</b>
    ///         The handle was published while last frame executed, so there is nothing in this frame
    ///         to order against — and importing it under a name of one's own would give the graph a
    ///         second resource over memory it already tracks, which is two barrier streams for one
    ///         buffer. <c>TerrainSceneRenderer</c> consumes exactly these two buffers exactly this
    ///         way, through a side-effect pass, and its own remarks record what it costs: a frame that
    ///         has only just started culling is lit without its lamps for one frame.
    ///     </para>
    ///     <para>
    ///         Consulted only when <see cref="Resource" /> is empty, so a binding that names a graph
    ///         buffer is unaffected.
    ///     </para>
    /// </remarks>
    public BufferHandle Buffer { get; init; }

    /// <summary>
    ///     The sampler to use, described rather than handed over.
    /// </summary>
    /// <remarks>
    ///     A <see cref="SamplerDescription" /> is data — twelve fields and no device in it — so this
    ///     is the form a compositor document can carry where <see cref="Sampler" /> cannot. Resolved
    ///     through the frame's <see cref="SamplerCache" />, which is also what stops a chain of post
    ///     passes creating one sampler each.
    /// </remarks>
    public SamplerDescription? Sampled { get; init; }

    /// <summary>This binding as the effect's plan describes it, or as it was written down.</summary>
    internal (uint Binding, DescriptorKind Kind) Resolve(Effect? effect) =>
        Name is { Length: > 0 } name && effect?.BindingOf(name) is { } found
            ? (found.Binding, found.Kind)
            : (Binding, Kind);

    /// <summary>Where in the buffer the binding starts.</summary>
    public long Offset { get; init; }

    /// <summary>How much of the buffer, or zero for the rest of it.</summary>
    public long Size { get; init; }

    /// <summary>Whether a kind resolves against a texture.</summary>
    public static bool IsTexture(DescriptorKind kind) =>
        kind is DescriptorKind.SampledTexture or DescriptorKind.StorageTexture;

    /// <summary>Whether a kind resolves against a buffer.</summary>
    public static bool IsBuffer(DescriptorKind kind) =>
        kind is DescriptorKind.UniformBuffer
            or DescriptorKind.StorageBuffer
            or DescriptorKind.DynamicUniformBuffer
            or DescriptorKind.DynamicStorageBuffer;
}

/// <summary>
///     The descriptor set a compositor node writes for itself, out of the graph resources it declared.
/// </summary>
/// <remarks>
///     <para>
///         What replaces the callback a compute node used to bind through. The obstacle was never
///         the API — it was that a graph resource has no handle until the graph has compiled, so a
///         node could not own a set the way a material owns one. A per-frame allocator is exactly the
///         missing lifetime: the node declares what it wants bound, the set is written after the
///         graph resolves and recycled once the GPU is done with it.
///     </para>
///     <para>
///         <strong>A binding may only name a resource the node also declared.</strong> Resolving
///         against the frame at large would compile and would silently drop the edge that orders the
///         producer first and puts the barrier in — a pass would sample a texture nothing had
///         transitioned, which reads as corruption on a tiler and as nothing at all on a desktop
///         driver until it is a customer's machine. So the resolution goes through the node's own
///         read lists, and a binding that names anything else throws while the frame is being built.
///     </para>
/// </remarks>
public sealed class DescriptorBindings {
    /// <summary>What the set contains.</summary>
    public IList<ResourceBinding> Bindings { get; } = [];

    /// <summary>Which of the four conventional sets this is.</summary>
    /// <remarks>
    ///     Per-material by default only because that is what an unmarked Raven binding is. A pass
    ///     binding the shadow atlas and the cluster list for everything it draws wants
    ///     <see cref="DescriptorSetSlot.PerView" />, so that the materials underneath it can rebind
    ///     set 2 without disturbing it.
    /// </remarks>
    public DescriptorSetSlot Slot { get; set; } = DescriptorSetSlot.PerMaterial;

    /// <summary>The set's shape. A node with no layout binds nothing.</summary>
    public DescriptorSetLayoutHandle Layout { get; set; }

    /// <summary>Where the sets come from. A node with no allocator binds nothing.</summary>
    public DescriptorAllocator? Allocator { get; set; }

    /// <summary>Whether there is enough here to write a set at all.</summary>
    public bool IsConfigured => Allocator is not null && Layout.IsValid && Bindings.Count > 0;

    /// <summary>Resolves every binding's name against what a node declared, for this frame.</summary>
    /// <param name="node">The node, for the exception message.</param>
    /// <param name="textures">The textures it declared, by name.</param>
    /// <param name="buffers">The buffers it declared, by name.</param>
    /// <param name="effect">
    ///     The variant being drawn with, whose plan a binding's <see cref="ResourceBinding.Name" />
    ///     resolves against. Null for a node that has no effect of its own — a render pass — whose
    ///     bindings therefore have to carry their indices.
    /// </param>
    /// <param name="samplers">Where a described sampler comes from.</param>
    /// <returns>The resolved set, or null when there is nothing to bind.</returns>
    /// <exception cref="CompositorBindingException">A binding names something undeclared.</exception>
    internal BoundBindings? Resolve(
        string node,
        IReadOnlyDictionary<string, GraphTexture> textures,
        IReadOnlyDictionary<string, GraphBuffer> buffers,
        Effect? effect = null,
        SamplerCache? samplers = null
    ) {
        if (!IsConfigured) {
            return null;
        }

        var bindings = Bindings.ToArray();
        var places = new (uint Binding, DescriptorKind Kind)[bindings.Length];
        var resolvedTextures = new GraphTexture[bindings.Length];
        var resolvedBuffers = new GraphBuffer[bindings.Length];
        var resolvedSamplers = new SamplerHandle[bindings.Length];
        var resolvedViews = new TextureViewHandle[bindings.Length];
        var resolvedHandles = new BufferHandle[bindings.Length];

        for (var i = 0; i < bindings.Length; i++) {
            var binding = bindings[i];
            places[i] = binding.Resolve(effect);
            resolvedSamplers[i] = binding.Sampler;

            if (binding.Sampled is { } description) {
                resolvedSamplers[i] = samplers is not null
                    ? samplers.GetOrCreate(description)
                    : throw new CompositorBindingException(node, "sampler", description.Name);
            }

            // A view the node handed over is not resolved against the graph at all — see
            // ResourceBinding.View. Named first because a binding with both would otherwise resolve
            // the name and silently ignore the handle.
            if (binding.Resource.Length == 0 && binding.View.IsValid) {
                resolvedViews[i] = binding.View;
            } else if (binding.Resource.Length == 0 && binding.Buffer.IsValid) {
                resolvedHandles[i] = binding.Buffer;
            } else if (ResourceBinding.IsTexture(places[i].Kind)) {
                resolvedTextures[i] = textures.TryGetValue(binding.Resource, out var texture)
                    ? texture
                    : throw new CompositorBindingException(node, "bound texture", binding.Resource);
            } else if (ResourceBinding.IsBuffer(places[i].Kind)) {
                resolvedBuffers[i] = buffers.TryGetValue(binding.Resource, out var buffer)
                    ? buffer
                    : throw new CompositorBindingException(node, "bound buffer", binding.Resource);
            }
        }

        return new(
            bindings,
            places,
            resolvedTextures,
            resolvedBuffers,
            resolvedSamplers,
            resolvedViews,
            resolvedHandles,
            Allocator!,
            Layout,
            Slot
        );
    }
}

/// <summary>One frame's resolution of a node's bindings, ready to become a set when the pass runs.</summary>
sealed class BoundBindings(
    ResourceBinding[] bindings,
    (uint Binding, DescriptorKind Kind)[] places,
    GraphTexture[] textures,
    GraphBuffer[] buffers,
    SamplerHandle[] samplers,
    TextureViewHandle[] views,
    BufferHandle[] handles,
    DescriptorAllocator allocator,
    DescriptorSetLayoutHandle layout,
    DescriptorSetSlot slot
) {
    DescriptorWrite[] writes = new DescriptorWrite[bindings.Length];

    /// <summary>Writes the set and binds it.</summary>
    /// <param name="context">The running pass, which is the first moment the handles exist.</param>
    /// <param name="extra">
    ///     Writes the node supplies itself, appended after the resolved ones. For a binding whose
    ///     resource is not a graph resource at all — a node's own uniform block — which has no name to
    ///     resolve and no edge to declare.
    /// </param>
    public void Bind(RenderGraphContext context, ReadOnlySpan<DescriptorWrite> extra = default) {
        if (writes.Length != bindings.Length + extra.Length) {
            writes = new DescriptorWrite[bindings.Length + extra.Length];
        }

        for (var i = 0; i < bindings.Length; i++) {
            var (index, kind) = places[i];

            writes[i] = kind switch {
                DescriptorKind.Sampler => DescriptorWrite.SamplerAt(index, samplers[i]),

                // The host's own view or buffer where the node handed one over, and the graph's
                // otherwise. Checked before the kind, because each pair of arms is one kind.
                _ when views[i].IsValid => new(index, kind, TextureView: views[i]),
                _ when handles[i].IsValid => new(index, kind, handles[i], bindings[i].Offset, bindings[i].Size),
                _ when ResourceBinding.IsTexture(kind) => new(index, kind, TextureView: context.View(textures[i])),
                _ => new(index, kind, context.Buffer(buffers[i]), bindings[i].Offset, bindings[i].Size)
            };
        }

        extra.CopyTo(writes.AsSpan(bindings.Length));
        context.CommandList.BindDescriptorSet(slot, allocator.Allocate(layout, writes));
    }
}
