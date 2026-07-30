// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.RenderGraph;

/// <summary>One declared pass.</summary>
sealed class GraphPass {
    public required int Index { get; init; }

    public required string Name { get; init; }

    public PassKind Kind { get; set; } = PassKind.Graphics;

    public List<ResourceUse> Uses { get; } = [];

    public List<GraphAttachment> Attachments { get; } = [];

    public Action<RenderGraphContext>? Body { get; set; }

    /// <summary>Whether the pass must run even if nothing reads what it writes.</summary>
    public bool HasSideEffect { get; set; }

    /// <summary>Whether culling kept it.</summary>
    public bool Survives { get; set; }

    /// <summary>How many surviving passes read something this one writes.</summary>
    public int Consumers { get; set; }

    public bool HasAttachments => Attachments.Count > 0;
}

/// <summary>What a pass declares about itself, before it runs.</summary>
/// <remarks>
///     <para>
///         Declaration is separated from execution because the graph has to know the whole frame
///         before any of it happens: what nothing reads can be culled, what does not overlap can
///         share memory, and what changes use needs a barrier — and none of those are answerable one
///         pass at a time.
///     </para>
///     <para>
///         The builder is handed to the setup callback and is not valid afterwards. Keeping one and
///         declaring into it later would declare into a graph that has already been compiled, which
///         <see cref="RenderGraph" /> refuses rather than silently ignores.
///     </para>
/// </remarks>
public sealed class RenderGraphPassBuilder {
    readonly RenderGraph graph;
    readonly GraphPass pass;

    internal RenderGraphPassBuilder(RenderGraph graph, GraphPass pass) {
        this.graph = graph;
        this.pass = pass;
    }

    /// <summary>Which queue the pass belongs on.</summary>
    public PassKind Kind {
        get => pass.Kind;
        set => pass.Kind = value;
    }

    /// <summary>States that the pass reads a texture.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="state">What it needs to be in — sampled, by default.</param>
    public void Reads(GraphTexture texture, ResourceState state = ResourceState.ShaderRead) {
        graph.Validate(texture);
        pass.Uses.Add(new(texture, GraphBuffer.None, state, false));
    }

    /// <summary>States that the pass reads a buffer.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="state">What it needs to be in.</param>
    public void Reads(GraphBuffer buffer, ResourceState state = ResourceState.ShaderRead) {
        graph.Validate(buffer);
        pass.Uses.Add(new(GraphTexture.None, buffer, state, false));
    }

    /// <summary>States that the pass writes a texture.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="state">What it needs to be in.</param>
    public void Writes(GraphTexture texture, ResourceState state = ResourceState.ShaderWrite) {
        graph.Validate(texture);
        pass.Uses.Add(new(texture, GraphBuffer.None, state, true));
    }

    /// <summary>States that the pass writes a buffer.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="state">What it needs to be in.</param>
    public void Writes(GraphBuffer buffer, ResourceState state = ResourceState.ShaderWrite) {
        graph.Validate(buffer);
        pass.Uses.Add(new(GraphTexture.None, buffer, state, true));
    }

    /// <summary>Renders into a texture as a colour attachment.</summary>
    /// <param name="texture">What to render into.</param>
    /// <param name="load">What to do with it at the start of the pass.</param>
    /// <param name="clear">What to clear to.</param>
    /// <param name="store">
    ///     What to do at the end, or <see langword="null" /> to let the graph decide from whether
    ///     anything reads it later.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         An attachment is a write, and is declared as one — so culling, lifetimes and barriers all
    ///         see it without the caller having to say it twice.
    ///     </para>
    ///     <para>
    ///         <b>A loaded attachment is a read as well</b>, and the graph has to be told, because two of
    ///         its decisions turn on reads and neither can see a <see cref="LoadAction" />: culling keeps
    ///         a pass alive because a survivor <em>reads</em> what it wrote, and validation requires what
    ///         is read to have a producer. Without the read, a pass that only clears a target is "never
    ///         read" and culled, and the pass that loads it loads undefined memory — which is how the
    ///         visibility buffer's depth arrived as NaNs and every fragment quietly failed the depth
    ///         test.
    ///     </para>
    /// </remarks>
    public void ColourAttachment(
        GraphTexture texture,
        LoadAction load = LoadAction.Clear,
        Color4 clear = default,
        StoreAction? store = null
    ) {
        graph.Validate(texture);

        if (load == LoadAction.Load) {
            pass.Uses.Add(new(texture, GraphBuffer.None, ResourceState.ColourTarget, false));
        }

        pass.Uses.Add(new(texture, GraphBuffer.None, ResourceState.ColourTarget, true));
        pass.Attachments.Add(new(texture, load, store, clear, 0f, 0, false, false));
    }

    /// <summary>Renders into a texture as the depth-stencil attachment.</summary>
    /// <param name="texture">What to render into.</param>
    /// <param name="load">What to do with depth at the start of the pass.</param>
    /// <param name="clearDepth">
    ///     What to clear depth to. <c>0</c> is <em>far</em> under the engine's reversed-Z convention;
    ///     clearing to 1 here is the classic mistake and depth-tests the whole scene away.
    /// </param>
    /// <param name="clearStencil">What to clear stencil to.</param>
    /// <param name="readOnly">
    ///     Whether the pass only tests depth. A read-only depth attachment is a read, not a write —
    ///     which is what lets a shader sample the same buffer the pass is testing against.
    /// </param>
    /// <param name="store">What to do at the end, or <see langword="null" /> to let the graph decide.</param>
    public void DepthAttachment(
        GraphTexture texture,
        LoadAction load = LoadAction.Clear,
        float clearDepth = 0f,
        byte clearStencil = 0,
        bool readOnly = false,
        StoreAction? store = null
    ) {
        graph.Validate(texture);

        // Loading is reading — see ColourAttachment. A depth test against loaded contents is the
        // clearest case there is: the whole point of loading is that an earlier pass's depth decides
        // this one's fragments, and culling that earlier pass replaces its answer with whatever was
        // in the memory.
        if (load == LoadAction.Load && !readOnly) {
            pass.Uses.Add(new(texture, GraphBuffer.None, ResourceState.DepthStencilWrite, false));
        }

        pass.Uses.Add(new(
            texture,
            GraphBuffer.None,
            readOnly ? ResourceState.DepthStencilRead : ResourceState.DepthStencilWrite,
            !readOnly
        ));

        pass.Attachments.Add(new(texture, load, store, default, clearDepth, clearStencil, true, readOnly));
    }

    /// <summary>States that the pass matters even if nothing reads what it writes.</summary>
    /// <remarks>
    ///     The escape hatch culling needs. A pass whose only effect is outside the graph — a readback
    ///     into a buffer the caller keeps, a timestamp query, a debug overlay written straight to a
    ///     swapchain image — has no consumer the graph can see, and would otherwise be removed for
    ///     being useless.
    /// </remarks>
    public void SideEffect() => pass.HasSideEffect = true;

    /// <summary>What the pass does when it runs.</summary>
    /// <param name="body">The work.</param>
    public void Execute(Action<RenderGraphContext> body) => pass.Body = body;
}

/// <summary>What a pass gets when it runs.</summary>
/// <remarks>
///     The command list is already inside the render pass, if the pass declared attachments, and the
///     barriers it needs have already been placed. What is left is the drawing.
/// </remarks>
public sealed class RenderGraphContext {
    readonly RenderGraph graph;

    internal RenderGraphContext(RenderGraph graph, ICommandList commandList) {
        this.graph = graph;
        CommandList = commandList;
    }

    /// <summary>The list to record into.</summary>
    public ICommandList CommandList { get; }

    /// <summary>The size of the pass's attachments, in pixels.</summary>
    /// <remarks><see cref="Int2.Zero" /> for a pass with no attachments.</remarks>
    public Int2 RenderArea { get; internal set; }

    /// <summary>The physical texture behind a virtual one.</summary>
    /// <param name="texture">The virtual texture.</param>
    public TextureHandle Texture(GraphTexture texture) => graph.Resolve(texture).Texture;

    /// <summary>The default view of a virtual texture.</summary>
    /// <param name="texture">The virtual texture.</param>
    public TextureViewHandle View(GraphTexture texture) => graph.Resolve(texture).View;

    /// <summary>The physical buffer behind a virtual one.</summary>
    /// <param name="buffer">The virtual buffer.</param>
    public BufferHandle Buffer(GraphBuffer buffer) => graph.Resolve(buffer).Buffer;
}
