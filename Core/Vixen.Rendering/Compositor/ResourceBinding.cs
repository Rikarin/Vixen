// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;

namespace Vixen.Rendering.Compositor;

/// <summary>Where one of a node's graph resources goes in the descriptor set it binds.</summary>
/// <remarks>
///     <para>
///         The binding index comes from the shader, not from the compositor: Raven decides what
///         <c>binding = 3</c> means and the host states it here. Reflecting it off the effect would be
///         better and is what <c>Effect</c> will eventually carry per resource — until then this is
///         the seam, and it is a small one because the alternative is the node reaching for a device
///         handle it has no way to have.
///     </para>
///     <para>
///         <see cref="Kind" /> decides where <see cref="Resource" /> is looked up. A texture kind
///         resolves against what the node declared as a texture, a buffer kind against its buffers,
///         and <see cref="DescriptorKind.Sampler" /> against neither — a sampler is not a frame
///         resource, it is device state that outlives every graph.
///     </para>
/// </remarks>
public sealed class ResourceBinding {
    /// <summary>Its index within the set.</summary>
    public required uint Binding { get; init; }

    /// <summary>What it binds, which is also what decides how <see cref="Resource" /> resolves.</summary>
    public required DescriptorKind Kind { get; init; }

    /// <summary>The graph resource's name, for every kind but a bare sampler.</summary>
    public string Resource { get; init; } = "";

    /// <summary>The sampler, for <see cref="DescriptorKind.Sampler" />.</summary>
    public SamplerHandle Sampler { get; init; }

    /// <summary>Where in the buffer the binding starts.</summary>
    public long Offset { get; init; }

    /// <summary>How much of the buffer, or zero for the rest of it.</summary>
    public long Size { get; init; }

    /// <summary>Whether this resolves against a texture.</summary>
    public bool IsTexture => Kind is DescriptorKind.SampledTexture or DescriptorKind.StorageTexture;

    /// <summary>Whether this resolves against a buffer.</summary>
    public bool IsBuffer =>
        Kind is DescriptorKind.UniformBuffer
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
    /// <returns>The resolved set, or null when there is nothing to bind.</returns>
    /// <exception cref="CompositorBindingException">A binding names something undeclared.</exception>
    internal BoundBindings? Resolve(
        string node,
        IReadOnlyDictionary<string, GraphTexture> textures,
        IReadOnlyDictionary<string, GraphBuffer> buffers
    ) {
        if (!IsConfigured) {
            return null;
        }

        var bindings = Bindings.ToArray();
        var resolvedTextures = new GraphTexture[bindings.Length];
        var resolvedBuffers = new GraphBuffer[bindings.Length];

        for (var i = 0; i < bindings.Length; i++) {
            var binding = bindings[i];

            if (binding.IsTexture) {
                resolvedTextures[i] = textures.TryGetValue(binding.Resource, out var texture)
                    ? texture
                    : throw new CompositorBindingException(node, "bound texture", binding.Resource);
            } else if (binding.IsBuffer) {
                resolvedBuffers[i] = buffers.TryGetValue(binding.Resource, out var buffer)
                    ? buffer
                    : throw new CompositorBindingException(node, "bound buffer", binding.Resource);
            }
        }

        return new(bindings, resolvedTextures, resolvedBuffers, Allocator!, Layout, Slot);
    }
}

/// <summary>One frame's resolution of a node's bindings, ready to become a set when the pass runs.</summary>
sealed class BoundBindings(
    ResourceBinding[] bindings,
    GraphTexture[] textures,
    GraphBuffer[] buffers,
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
            var binding = bindings[i];

            writes[i] = binding.Kind switch {
                DescriptorKind.Sampler => DescriptorWrite.SamplerAt(binding.Binding, binding.Sampler),
                _ when binding.IsTexture => new(
                    binding.Binding,
                    binding.Kind,
                    TextureView: context.View(textures[i])
                ),
                _ => new(
                    binding.Binding,
                    binding.Kind,
                    context.Buffer(buffers[i]),
                    binding.Offset,
                    binding.Size
                )
            };
        }

        extra.CopyTo(writes.AsSpan(bindings.Length));
        context.CommandList.BindDescriptorSet(slot, allocator.Allocate(layout, writes));
    }
}
