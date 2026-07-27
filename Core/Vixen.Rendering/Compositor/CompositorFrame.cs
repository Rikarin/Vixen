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
        resources[name] = new(texture, format);
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

    /// <summary>Whether a name resolves to anything.</summary>
    public bool Has(string name) => resources.ContainsKey(name);

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

    readonly record struct Entry(GraphTexture Texture, PixelFormat Format);
}
