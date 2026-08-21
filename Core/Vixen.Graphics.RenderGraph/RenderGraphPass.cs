// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.RenderGraph;

/// <summary>One declared pass.</summary>
sealed class GraphPass {
    public required int Index { get; init; }

    public required string Name { get; init; }

    public PassKind Kind { get; set; } = PassKind.Graphics;

    /// <summary>Which queue scheduling put it on. Decided by <c>RenderGraph.Compile</c>.</summary>
    public QueueKind Queue { get; set; } = QueueKind.Graphics;

    /// <summary>Which segment it belongs to, as an index into the schedule.</summary>
    public int Segment { get; set; }

    /// <summary>The transitions it needs before it runs, worked out once at compile time.</summary>
    /// <remarks>
    ///     Planned rather than derived while recording, because a cross-queue handover is two
    ///     barriers and the first of them belongs at the end of a segment that is already closed by
    ///     the time the pass needing it is reached. One walk decides both halves; recording replays
    ///     what the walk decided.
    /// </remarks>
    public List<PlannedBarrier> Barriers { get; } = [];

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
    /// <param name="resolve">
    ///     Where to resolve a multisampled attachment at the end of the pass, or
    ///     <see cref="GraphTexture.None" /> not to resolve it. Naming one makes the store a
    ///     <see cref="StoreAction.Resolve" /> whatever <paramref name="store" /> says, because a
    ///     resolve target that is not resolved into is a texture the pass silently left empty.
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
    ///     <para>
    ///         <b>A resolve target is a write of its own</b>, and the multisampled attachment above it
    ///         usually is not read at all. That is the whole shape of MSAA in a graph: the samples exist
    ///         for the duration of one pass and what survives is the resolve — so the resolve is the
    ///         write the next pass reads, and the multisampled texture is free to be aliased and
    ///         discarded. Declaring the resolve as a write is what makes both true; without it the
    ///         resolve has no producer, every reader of it fails validation, and the pass that filled it
    ///         is culled for writing something nobody wanted.
    ///     </para>
    /// </remarks>
    public void ColourAttachment(
        GraphTexture texture,
        LoadAction load = LoadAction.Clear,
        Color4 clear = default,
        StoreAction? store = null,
        GraphTexture resolve = default
    ) {
        graph.Validate(texture);

        if (load == LoadAction.Load) {
            pass.Uses.Add(new(texture, GraphBuffer.None, ResourceState.ColourTarget, false));
        }

        pass.Uses.Add(new(texture, GraphBuffer.None, ResourceState.ColourTarget, true));

        if (resolve.IsValid) {
            graph.Validate(resolve);
            graph.ValidateResolve(texture, resolve);

            // The layout Vulkan puts a resolve destination in is the colour-attachment one, not the
            // copy-destination one: it is written by the render pass, not by a transfer.
            pass.Uses.Add(new(resolve, GraphBuffer.None, ResourceState.ColourTarget, true));
        }

        pass.Attachments.Add(new(texture, load, store, clear, 0f, 0, false, false, resolve));
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

    ICommandList? commandList;

    internal RenderGraphContext(RenderGraph graph, ICommandList? commandList) {
        this.graph = graph;
        this.commandList = commandList;
    }

    /// <summary>The list to record into.</summary>
    /// <remarks>
    ///     ⚠ <b>Not the same list for the whole frame once the graph schedules onto more than one
    ///     queue.</b> A pass body that captured this and recorded into it later would be recording
    ///     into a list that has been submitted — read it inside the body, every time, which is what
    ///     every pass in the tree already does.
    /// </remarks>
    public ICommandList CommandList =>
        commandList ?? throw new InvalidOperationException(
            "A render-graph context was read outside a pass. The list belongs to the segment being "
            + "recorded, and there is none between segments."
        );

    internal void Retarget(ICommandList list) => commandList = list;

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

    /// <summary>Times a named region inside this pass, one level under the pass's own scope.</summary>
    /// <param name="name">What to call it.</param>
    /// <returns>A token for <see cref="EndScope" />, or null when nothing was opened.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>For the pass that is several dispatches wearing one name, and for nothing else.</b>
    ///         The graph gives every pass a scope without anybody opting in, and that flat list is the
    ///         timeline's shape on purpose — a bar per pass sums to the frame and needs no reading.
    ///         This is the exception it does not cover: a compute pass whose body records four or five
    ///         unrelated dispatches is one bar that can only say <i>that</i> it was expensive, never
    ///         <i>which part</i> was. Sample 13's screen-probe gather is the case that forced it — half
    ///         a frame under one name, with a trace, a resolve, an accumulate and a filter inside.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A reader must sum level zero alone.</b> A nested scope's time is already inside its
    ///         parent's, so adding every scope up double-counts the nesting and reports more GPU time
    ///         than the frame has. <see cref="GpuScope.Level" /> is what tells the two apart.
    ///     </para>
    /// </remarks>
    public int? BeginScope(string name) => graph.Profiler?.Begin(CommandList, name);

    /// <summary>Closes a region opened by <see cref="BeginScope" />.</summary>
    /// <param name="token">What <see cref="BeginScope" /> returned. Null does nothing.</param>
    public void EndScope(int? token) => graph.Profiler?.Close(CommandList, token);
}
