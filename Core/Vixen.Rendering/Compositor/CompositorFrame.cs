// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Shaders;

namespace Vixen.Rendering.Compositor;

/// <summary>A texture the host owns and lends the frame.</summary>
/// <param name="Texture">The texture.</param>
/// <param name="View">A view of it, which the graph attaches.</param>
/// <param name="Description">What it is, so passes can reason about its size and format.</param>
/// <param name="EntryState">What it is being used as when the frame receives it.</param>
/// <param name="ExitState">What it must be left as.</param>
/// <remarks>
///     <para>
///         Two things are imported rather than declared, and they are imported for opposite reasons.
///         The swapchain image belongs to the presentation engine and has to be handed back in
///         <see cref="ResourceState.Present" />. A cached shadow atlas belongs to the frame
///         <em>before</em> this one — and a cache is by definition not transient, because the graph's
///         pool exists precisely to recycle memory whose lifetime ends inside the frame.
///     </para>
///     <para>
///         Everything else should be declared. A resource the graph owns is one it can alias with
///         another whose lifetime does not overlap, and that is most of what a transient pool is for.
///     </para>
/// </remarks>
public readonly record struct ImportedTexture(
    TextureHandle Texture,
    TextureViewHandle View,
    TextureDescription Description,
    ResourceState EntryState = ResourceState.Undefined,
    ResourceState ExitState = ResourceState.Undefined
);

/// <summary>A buffer the host owns and lends the frame.</summary>
/// <param name="Buffer">The buffer.</param>
/// <param name="Description">What it is.</param>
/// <param name="EntryState">What it is being used as when the frame receives it.</param>
/// <param name="ExitState">What it must be left as.</param>
/// <remarks>
///     A light list and a cluster list are both imports for the same reason a cached shadow atlas is:
///     the host filled them before the frame started, and something outside the graph owns the
///     memory. A buffer whose whole life is inside one frame should be declared instead.
/// </remarks>
public readonly record struct ImportedBuffer(
    BufferHandle Buffer,
    BufferDescription Description,
    ResourceState EntryState = ResourceState.Undefined,
    ResourceState ExitState = ResourceState.Undefined
);

/// <summary>
///     What a compositor node gets while it is declaring passes: the graph, the frame's resources by
///     name, and what a recorded pass will need.
/// </summary>
/// <remarks>
///     <para>
///         The seam that used to be a dictionary of texture handles. A node still refers to a target
///         by <em>name</em> — that has not changed and is what lets one authored document run against
///         a swapchain, an offscreen buffer or a test's scratch texture — but a name now resolves to
///         a <see cref="GraphTexture" />, which the graph may not have allocated yet and may alias
///         with something else once it has.
///     </para>
///     <para>
///         That is the whole point of the move. Declaring a target rather than binding one is what
///         lets the graph size it, place its barriers, decide whether its contents ever have to reach
///         memory, and drop the pass entirely if nothing reads what it wrote.
///     </para>
/// </remarks>
public sealed class CompositorFrame {
    readonly Dictionary<string, Entry> resources = new(StringComparer.Ordinal);
    readonly Dictionary<string, BufferEntry> buffers = new(StringComparer.Ordinal);
    RenderDrawContext? context;

    /// <summary>The graph this frame's passes are declared into.</summary>
    public required RenderGraph Graph { get; init; }

    /// <summary>Where a shader variant comes from.</summary>
    public required EffectSystem Effects { get; init; }

    /// <summary>The device, for a feature that has to create a pipeline it did not have.</summary>
    public IGraphicsDevice? Device { get; init; }

    /// <summary>
    ///     The size a resource declared without one takes, in pixels.
    /// </summary>
    /// <remarks>
    ///     The frame's reference size — the swapchain's, normally. A declaration says
    ///     <c>scale: 0.5</c> and gets half of this, which is how a half-resolution bloom chain is
    ///     authored without the document knowing what window it is in.
    /// </remarks>
    public Int2 Size { get; init; } = new(1, 1);

    /// <summary>The resources this frame has, by name.</summary>
    public IEnumerable<string> Names => resources.Keys;

    /// <summary>Adds a resource under a name.</summary>
    public void Add(string name, GraphTexture texture, PixelFormat format) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        resources[name] = new(texture, format, null);
    }

    /// <summary>Adds a resource under a name, keeping what it was declared or imported as.</summary>
    /// <param name="name">What nodes refer to it by.</param>
    /// <param name="texture">The graph resource.</param>
    /// <param name="description">What it was declared as.</param>
    /// <remarks>
    ///     ⚠ The overload to reach for whenever the extent is not the frame's. A pass that dispatches
    ///     over a volume needs to know how big the volume is, and the alternative — the node carrying
    ///     its own copy of the numbers the document declared — is two derivations of one quantity,
    ///     which drifts into a dispatch that covers part of a texture and leaves the rest at whatever
    ///     the last frame put there. <see cref="BufferEntry" /> carries a description for the
    ///     same reason.
    /// </remarks>
    public void Add(string name, GraphTexture texture, in TextureDescription description) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        resources[name] = new(texture, description.Format, description);
    }

    /// <summary>The resource a node named, or a refusal naming both.</summary>
    /// <exception cref="CompositorBindingException">Nothing was declared or imported under that name.</exception>
    public GraphTexture Texture(string node, string name) =>
        resources.TryGetValue(name, out var entry)
            ? entry.Texture
            : throw new CompositorBindingException(node, "target", name);

    /// <summary>The format of the resource a node named.</summary>
    /// <exception cref="CompositorBindingException">Nothing was declared or imported under that name.</exception>
    public PixelFormat FormatOf(string node, string name) =>
        resources.TryGetValue(name, out var entry)
            ? entry.Format
            : throw new CompositorBindingException(node, "target", name);

    /// <summary>What the resource a node named was declared as, or null for one added without one.</summary>
    /// <exception cref="CompositorBindingException">Nothing was declared or imported under that name.</exception>
    /// <remarks>
    ///     Null rather than a fabricated description, on <c>ParameterKey.DefaultValue</c>'s terms:
    ///     "nobody recorded one" and "it is 1×1×1" are different answers, and a dispatch sized from
    ///     the second covers one froxel.
    /// </remarks>
    public TextureDescription? DescriptionOf(string node, string name) =>
        resources.TryGetValue(name, out var entry)
            ? entry.Description
            : throw new CompositorBindingException(node, "target", name);

    /// <summary>Whether a name resolves to a texture.</summary>
    public bool Has(string name) => resources.ContainsKey(name);

    /// <summary>Adds a buffer under a name.</summary>
    /// <param name="name">What nodes refer to it by.</param>
    /// <param name="buffer">The graph resource.</param>
    /// <param name="description">
    ///     What it was declared or imported as, which is the only place its size and usage survive.
    /// </param>
    /// <remarks>
    ///     The description is carried rather than dropped because a node that <em>copies</em> a buffer
    ///     needs both halves of it: how many bytes there are to move, and whether the buffer was
    ///     declared as something a copy may touch at all. A <see cref="GraphBuffer" /> is an index into
    ///     one build of the graph and says neither.
    /// </remarks>
    public void Add(string name, GraphBuffer buffer, in BufferDescription description) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        buffers[name] = new(buffer, description);
    }

    /// <summary>The buffer a node named, or a refusal naming both.</summary>
    /// <exception cref="CompositorBindingException">Nothing was declared or imported under that name.</exception>
    public GraphBuffer Buffer(string node, string name) =>
        buffers.TryGetValue(name, out var buffer)
            ? buffer.Buffer
            : throw new CompositorBindingException(node, "buffer", name);

    /// <summary>What the buffer a node named was declared or imported as.</summary>
    /// <exception cref="CompositorBindingException">Nothing was declared or imported under that name.</exception>
    public BufferDescription DescribeBuffer(string node, string name) =>
        buffers.TryGetValue(name, out var buffer)
            ? buffer.Description
            : throw new CompositorBindingException(node, "buffer", name);

    /// <summary>Whether a name resolves to a buffer.</summary>
    public bool HasBuffer(string name) => buffers.ContainsKey(name);

    /// <summary>
    ///     The draw context for a pass body, which every pass in a frame shares.
    /// </summary>
    /// <remarks>
    ///     One per frame rather than one per pass. The graph hands every pass the same command list
    ///     and runs them in order, so a shared context is correct — and a context per pass would be
    ///     an allocation for every pass in every frame to hold three fields that do not change.
    /// </remarks>
    internal RenderDrawContext Context(ICommandList commandList) =>
        context ??= new(commandList, Effects) { Device = Device };

    readonly record struct Entry(GraphTexture Texture, PixelFormat Format, TextureDescription? Description);

    readonly record struct BufferEntry(GraphBuffer Buffer, BufferDescription Description);
}
