// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.RenderGraph;

/// <summary>A frame, declared before it happens.</summary>
/// <remarks>
///     <para>
///         Passes state what they read and write; the graph culls what nothing needs, gives
///         non-overlapping resources the same memory, places the barriers, derives the attachment
///         load and store actions, and then runs the passes that survived.
///     </para>
///     <para>
///         <b>This is not optional garnish</b> ([05](../../docs/plan/05-graphics-rhi.md)). With six
///         backends, hand-maintaining barrier correctness across deferred shading, shadows, SSAO,
///         SSR, TAA, bloom and depth of field is not achievable — and a barrier that is merely
///         <em>too broad</em> costs performance silently while one that is too narrow is a race that
///         reproduces on one vendor's driver and not another's. Hand-written barriers stay available
///         through <see cref="ICommandList.Barrier" /> for the hot paths that want them.
///     </para>
///     <para>
///         The graph is rebuilt every frame and that is deliberate: a frame's passes depend on what
///         is visible, and a cached graph would be a cache invalidated by everything. Rebuilding is
///         cheap because the physical resources are not — <see cref="TransientResourcePool" /> holds
///         those across frames.
///     </para>
/// </remarks>
public sealed class RenderGraph {
    readonly IGraphicsDevice device;
    readonly TransientResourcePool pool;
    readonly bool ownsPool;
    readonly List<GraphResource> resources = [];
    readonly List<GraphPass> passes = [];
    readonly List<BufferBarrier> bufferBarriers = [];
    readonly List<TextureBarrier> textureBarriers = [];

    readonly List<string> warnings = [];
    readonly HashSet<string> seenWarnings = new(StringComparer.Ordinal);

    int generation = 1;
    bool compiled;

    /// <summary>Creates a graph and the pool its transients come from.</summary>
    /// <param name="device">The device.</param>
    public RenderGraph(IGraphicsDevice device) : this(device, new TransientResourcePool(device)) =>
        ownsPool = true;

    /// <summary>Creates a graph over an existing pool.</summary>
    /// <param name="device">The device.</param>
    /// <param name="pool">The pool, which the caller keeps.</param>
    /// <remarks>
    ///     Sharing one pool between graphs is what makes a frame with several graphs — a main view
    ///     and a reflection probe, say — reuse memory between them rather than each holding its own.
    /// </remarks>
    public RenderGraph(IGraphicsDevice device, TransientResourcePool pool) {
        this.device = device;
        this.pool = pool;
    }

    /// <summary>The pool its transient resources come from.</summary>
    public TransientResourcePool Pool => pool;

    /// <summary>How many passes were declared.</summary>
    public int PassCount => passes.Count;

    /// <summary>How many passes survived culling. Meaningless before <see cref="Compile" />.</summary>
    public int SurvivingPassCount {
        get {
            var count = 0;

            foreach (var pass in passes) {
                if (pass.Survives) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>How many virtual resources were declared.</summary>
    public int ResourceCount => resources.Count;

    /// <summary>How many barriers the last <see cref="Execute" /> emitted.</summary>
    /// <remarks>
    ///     Worth watching. A graph whose barrier count grows faster than its pass count is one whose
    ///     passes are ping-ponging a resource between states, and that is invisible from the picture.
    /// </remarks>
    public int BarrierCount { get; private set; }

    /// <summary>Declares a texture the graph will provide.</summary>
    /// <param name="description">What it has to be.</param>
    public GraphTexture CreateTexture(in TextureDescription description) {
        ThrowIfCompiled();
        description.Validate();

        resources.Add(new() {
            Index = resources.Count + 1,
            Name = string.IsNullOrEmpty(description.Name) ? $"texture {resources.Count + 1}" : description.Name,
            IsTexture = true,
            IsImported = false,
            TextureDescription = description
        });

        return new(resources.Count, generation);
    }

    /// <summary>Declares a buffer the graph will provide.</summary>
    /// <param name="description">What it has to be.</param>
    public GraphBuffer CreateBuffer(in BufferDescription description) {
        ThrowIfCompiled();
        description.Validate();

        resources.Add(new() {
            Index = resources.Count + 1,
            Name = string.IsNullOrEmpty(description.Name) ? $"buffer {resources.Count + 1}" : description.Name,
            IsTexture = false,
            IsImported = false,
            BufferDescription = description
        });

        return new(resources.Count, generation);
    }

    /// <summary>Brings a texture the graph does not own into it.</summary>
    /// <param name="handle">The texture.</param>
    /// <param name="view">A view of it, which the graph uses for attachments.</param>
    /// <param name="description">What it is, so that passes can reason about its size and format.</param>
    /// <param name="entryState">What it is being used as when the graph receives it.</param>
    /// <param name="exitState">What it must be left as.</param>
    /// <remarks>
    ///     Two states rather than one, because the graph has to hand it back correctly. A swapchain
    ///     image left in <c>ColourTarget</c> rather than <c>Present</c> is a validation error at
    ///     present time — a frame's worth of code away from the graph that caused it.
    /// </remarks>
    public GraphTexture ImportTexture(
        TextureHandle handle,
        TextureViewHandle view,
        in TextureDescription description,
        ResourceState entryState = ResourceState.Undefined,
        ResourceState exitState = ResourceState.Undefined
    ) {
        ThrowIfCompiled();

        resources.Add(new() {
            Index = resources.Count + 1,
            Name = string.IsNullOrEmpty(description.Name) ? $"imported {resources.Count + 1}" : description.Name,
            IsTexture = true,
            IsImported = true,
            TextureDescription = description,
            ImportedTexture = handle,
            ImportedView = view,
            EntryState = entryState,
            ExitState = exitState
        });

        return new(resources.Count, generation);
    }

    /// <summary>Brings a buffer the graph does not own into it.</summary>
    /// <param name="handle">The buffer.</param>
    /// <param name="description">What it is.</param>
    /// <param name="entryState">What it is being used as when the graph receives it.</param>
    /// <param name="exitState">What it must be left as.</param>
    public GraphBuffer ImportBuffer(
        BufferHandle handle,
        in BufferDescription description,
        ResourceState entryState = ResourceState.Undefined,
        ResourceState exitState = ResourceState.Undefined
    ) {
        ThrowIfCompiled();

        resources.Add(new() {
            Index = resources.Count + 1,
            Name = string.IsNullOrEmpty(description.Name) ? $"imported {resources.Count + 1}" : description.Name,
            IsTexture = false,
            IsImported = true,
            BufferDescription = description,
            ImportedBuffer = handle,
            EntryState = entryState,
            ExitState = exitState
        });

        return new(resources.Count, generation);
    }

    /// <summary>Adds a pass.</summary>
    /// <param name="name">Its name, which reaches the capture and the Graphviz dump.</param>
    /// <param name="setup">What it declares, and what it does.</param>
    /// <exception cref="RenderGraphException">The pass declared nothing the graph could act on.</exception>
    public void AddPass(string name, Action<RenderGraphPassBuilder> setup) {
        ArgumentNullException.ThrowIfNull(setup);
        ThrowIfCompiled();

        var pass = new GraphPass { Index = passes.Count, Name = name };
        setup(new(this, pass));

        if (pass.Body is null) {
            throw new RenderGraphException(
                $"Pass '{name}' declared no work. Call Execute in its setup, or do not add it — a pass "
                + "that declares resources and does nothing still forces them to exist."
            );
        }

        if (pass.Uses.Count == 0 && !pass.HasSideEffect) {
            throw new RenderGraphException(
                $"Pass '{name}' reads and writes nothing and is not marked SideEffect, so the graph has "
                + "no reason to keep it and no way to order it. Declare what it touches, or say it has "
                + "an effect the graph cannot see."
            );
        }

        passes.Add(pass);
    }

    /// <summary>
    ///     Works out what runs, what shares memory, and where the barriers go.
    /// </summary>
    /// <exception cref="RenderGraphException">The graph does not describe a frame that could run.</exception>
    /// <remarks>
    ///     Called automatically by <see cref="Execute" />. Separate because a frame debugger wants to
    ///     compile and inspect without running, and because a validation failure should name the
    ///     declaration that caused it rather than arriving mid-frame.
    /// </remarks>
    public void Compile() {
        if (compiled) {
            return;
        }

        ValidateReads();
        Cull();
        Lint();
        ComputeLifetimes();
        compiled = true;
    }

    /// <summary>Warnings the graph found while compiling — frames that run and quietly waste work.</summary>
    /// <remarks>
    ///     <para>
    ///         Warnings rather than exceptions, because every one of them describes a frame that
    ///         draws: the picture is merely missing something, or paying for something it throws
    ///         away. The list is append-only across <see cref="Reset" />s and each message appears
    ///         once, so a host can log new entries by remembering how many it has already reported.
    ///     </para>
    ///     <para>
    ///         The case this exists for is the discarded write: a pass stores a resource and the
    ///         next pass to touch it clears it, with nothing reading in between. That frame is a
    ///         full raster paid for a result thrown away every time — sample 13 ran its whole
    ///         visibility resolve into a colour the sky pass overwrote, for months, and every
    ///         counter reported success.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<string> Warnings => warnings;

    /// <summary>Finds work the frame pays for and throws away, once per distinct finding.</summary>
    void Lint() {
        var lastWriter = new GraphPass?[resources.Count];
        var consumed = new bool[resources.Count];

        foreach (var pass in passes) {
            if (!pass.Survives) {
                continue;
            }

            foreach (var use in pass.Uses) {
                var index = (use.IsTexture ? use.Texture.Index : use.Buffer.Index) - 1;

                if (!use.IsWrite) {
                    consumed[index] = true;
                    continue;
                }

                // A loaded attachment declares its read before its write, so a genuine
                // read-modify-write arrives here already consumed. What is left is the discard:
                // an earlier pass's stored result, overwritten before anyone looked.
                if (lastWriter[index] is { } writer && !consumed[index] && !ReferenceEquals(writer, pass)) {
                    var message =
                        $"VX2101: '{writer.Name}' writes '{resources[index].Name}' and "
                        + $"'{pass.Name}' overwrites it before anything reads it — the first "
                        + "write is discarded every frame.";

                    if (seenWarnings.Add(message)) {
                        warnings.Add(message);
                    }
                }

                lastWriter[index] = pass;
                consumed[index] = false;
            }
        }
    }

    /// <summary>Where each pass's cost is reported, or <see langword="null" /> to measure nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The frame times itself, rather than forty renderers each remembering to.</b> Every
    ///         pass the graph runs is bracketed by a scope named after it, so a document that adds a
    ///         node gets a bar in the timeline without anybody opting in — which is what Unity's SRP
    ///         does by wrapping each <c>ScriptableRenderPass</c> in a <c>ProfilingSampler</c> and what
    ///         Unreal's RDG does by emitting a draw event per pass. A scheme that needed every
    ///         renderer to call a profiler is the scheme that produced a permanently empty timeline
    ///         here, and doc 13 asks for exactly this: "timestamps around each render-graph pass".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null by default, and that is the toggle.</b> A timestamp pair is a GPU write, and
    ///         on tile-based hardware — MoltenVK is this engine's development target — a query write
    ///         can force a tile resolve and change the timings it is reporting. Off is therefore the
    ///         only honest default; the cost of off is one null check per pass and no device work at
    ///         all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Set it before <see cref="Execute" />, and leave it set.</b> The graph reads it
    ///         once per execution, so attaching one mid-frame times part of a frame and detaching one
    ///         mid-frame leaves a scope open. It survives <see cref="Reset" /> because the graph
    ///         outlives the frame it describes.
    ///     </para>
    /// </remarks>
    public IGpuScopeSink? Profiler { get; set; }

    /// <summary>Runs it.</summary>
    /// <param name="commandList">Where the work is recorded.</param>
    public void Execute(ICommandList commandList) {
        ArgumentNullException.ThrowIfNull(commandList);
        Compile();

        BarrierCount = 0;
        Realise();

        foreach (var resource in resources) {
            resource.CurrentState = resource.IsImported ? resource.EntryState : ResourceState.Undefined;
        }

        var context = new RenderGraphContext(this, commandList);

        // Read once. A sink swapped between passes would open a scope on one and close it on
        // another, and the field is public precisely so a host can change it between frames.
        var sink = Profiler;

        for (var index = 0; index < passes.Count; index++) {
            var pass = passes[index];

            if (!pass.Survives) {
                continue;
            }

            // ⚠ Opened *before* the barriers, not after. A barrier's cost is a stall, it is real GPU
            // time, and it is caused by what the pass about to run declared it needs — so charging it
            // to that pass is both the truthful attribution and the one that makes the scopes sum to
            // the frame. Timing only the body leaves every barrier in the frame unattributed, which
            // reads as "the GPU was idle" rather than "this pass waited for its inputs".
            var scope = sink?.Begin(commandList, pass.Name);

            EmitBarriers(commandList, pass);

            if (pass.HasAttachments) {
                RunWithAttachments(commandList, context, pass, index);
            } else {
                // ⚠ The half of the frame a capture could not name. A backend labels a *render pass*
                // from `RenderPassDescription.Name` — the Vulkan one turns it into a debug group,
                // WebGPU into a pass label — so an attachment pass is already legible and a second
                // group here would only nest its own name inside itself. A pass with no attachments
                // has no description to carry a name, and every compute dispatch in the frame is one:
                // the GPU cull, the clipmap, the surface cache, the probe gather, the exposure
                // reduce. Without this a capture of sample 13 is a wall of anonymous dispatches.
                //
                // ⚠ Guarded on the name being there, and not out of tidiness. A backend may decline
                // to open a group it cannot name — the Vulkan one returns early on an empty string —
                // while its pop is unconditional, so pushing "" and popping it anyway closes a group
                // somebody else opened. That is an unbalanced label stack, and it surfaces as a
                // validation error at submit, a whole frame away from the unnamed pass that caused it.
                var named = !string.IsNullOrEmpty(pass.Name);

                if (named) {
                    commandList.PushDebugGroup(pass.Name);
                }

                context.RenderArea = Int2.Zero;
                pass.Body!(context);

                if (named) {
                    commandList.PopDebugGroup();
                }
            }

            sink?.Close(commandList, scope);

            ReleaseExpired(index);
        }

        RestoreImports(commandList);
    }

    /// <summary>Forgets the frame, keeping the physical resources.</summary>
    /// <remarks>
    ///     Handles from before a reset are invalid, and the generation counter says so rather than
    ///     letting one address whatever took its slot.
    /// </remarks>
    public void Reset() {
        foreach (var resource in resources) {
            if (!resource.IsImported && resource.PoolSlot >= 0) {
                pool.Release(resource.PoolSlot);
            }
        }

        resources.Clear();
        passes.Clear();
        compiled = false;
        generation++;
    }

    /// <summary>The compiled graph as Graphviz DOT.</summary>
    /// <remarks>
    ///     What [05](../../docs/plan/05-graphics-rhi.md) asks for and what a frame debugger renders.
    ///     Culled passes are drawn dashed rather than omitted, because "why did my pass not run" is
    ///     the question this is most often opened to answer.
    /// </remarks>
    public string ToGraphviz() {
        Compile();
        var text = new System.Text.StringBuilder();
        text.AppendLine("digraph RenderGraph {");
        text.AppendLine("  rankdir=LR;");
        text.AppendLine("  node [fontname=\"Helvetica\"];");

        foreach (var pass in passes) {
            var style = pass.Survives ? "solid" : "dashed";
            var colour = pass.Survives ? "black" : "grey";

            text.AppendLine(
                $"  p{pass.Index} [shape=box,style={style},color={colour},label=\"{Escape(pass.Name)}\"];"
            );
        }

        foreach (var resource in resources) {
            var shape = resource.IsImported ? "ellipse" : "oval";
            var style = resource.IsImported ? "bold" : "solid";

            text.AppendLine(
                $"  r{resource.Index} [shape={shape},style={style},label=\"{Escape(resource.Name)}\"];"
            );
        }

        foreach (var pass in passes) {
            foreach (var use in pass.Uses) {
                var resource = use.IsTexture ? use.Texture.Index : use.Buffer.Index;

                text.AppendLine(
                    use.IsWrite
                        ? $"  p{pass.Index} -> r{resource} [label=\"{use.State}\"];"
                        : $"  r{resource} -> p{pass.Index} [label=\"{use.State}\"];"
                );
            }
        }

        text.AppendLine("}");
        return text.ToString();
    }

    /// <summary>Releases the pool, when the graph made one.</summary>
    /// <remarks>
    ///     A graph handed someone else's pool does not destroy it — the caller owns what the caller
    ///     created, which is what lets several graphs share one.
    /// </remarks>
    public void DisposePool() {
        if (ownsPool) {
            pool.Dispose();
        }
    }

    /// <summary>The physical texture a virtual one was given.</summary>
    /// <param name="texture">The virtual texture.</param>
    /// <remarks>
    ///     Valid after <see cref="Execute" /> and until <see cref="Reset" />. What a caller uses to
    ///     hand a graph-produced target to something outside the graph — a readback, a debug view, an
    ///     imported target passed on to a second graph.
    /// </remarks>
    public TextureHandle TextureOf(GraphTexture texture) => Resolve(texture).Texture;

    /// <summary>The default view of the physical texture a virtual one was given.</summary>
    /// <param name="texture">The virtual texture.</param>
    public TextureViewHandle ViewOf(GraphTexture texture) => Resolve(texture).View;

    /// <summary>The physical buffer a virtual one was given.</summary>
    /// <param name="buffer">The virtual buffer.</param>
    public BufferHandle BufferOf(GraphBuffer buffer) => Resolve(buffer).Buffer;

    /// <summary>The description a texture was declared or imported with.</summary>
    /// <param name="texture">The virtual texture.</param>
    /// <remarks>
    ///     For a consumer whose own arithmetic decides viewports inside the texture — a shadow
    ///     atlas node, a tile packer — and which therefore has to be able to *check* the declared
    ///     extent against its own before recording into it. Without this the mismatch is a scissor
    ///     rectangle outside the texture, which rasterizes nothing and says nothing.
    /// </remarks>
    public TextureDescription DescribeTexture(GraphTexture texture) => Resolve(texture).TextureDescription;

    internal GraphResource Resolve(GraphTexture texture) {
        Validate(texture);
        return resources[texture.Index - 1];
    }

    internal GraphResource Resolve(GraphBuffer buffer) {
        Validate(buffer);
        return resources[buffer.Index - 1];
    }

    internal void Validate(GraphTexture texture) {
        if (!texture.IsValid || texture.Index > resources.Count || texture.Generation != generation) {
            throw new RenderGraphException(
                "A texture handle does not belong to this graph. Handles are indices into one build "
                + "and stop meaning anything at Reset()."
            );
        }

        if (!resources[texture.Index - 1].IsTexture) {
            throw new RenderGraphException("A buffer was used where a texture was expected.");
        }
    }

    internal void Validate(GraphBuffer buffer) {
        if (!buffer.IsValid || buffer.Index > resources.Count || buffer.Generation != generation) {
            throw new RenderGraphException(
                "A buffer handle does not belong to this graph. Handles are indices into one build "
                + "and stop meaning anything at Reset()."
            );
        }

        if (resources[buffer.Index - 1].IsTexture) {
            throw new RenderGraphException("A texture was used where a buffer was expected.");
        }
    }

    static string Escape(string value) => value.Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>Refuses a read of something nothing produces.</summary>
    /// <remarks>
    ///     The most valuable thing the graph validates, and the one that is hardest to see by eye: a
    ///     pass reading a transient no earlier pass wrote reads undefined memory, which on most
    ///     drivers is the previous frame's contents and therefore looks almost right.
    /// </remarks>
    void ValidateReads() {
        var written = new bool[resources.Count];

        for (var index = 0; index < resources.Count; index++) {
            written[index] = resources[index].IsImported;
        }

        foreach (var pass in passes) {
            foreach (var use in pass.Uses) {
                var resource = (use.IsTexture ? use.Texture.Index : use.Buffer.Index) - 1;

                if (!use.IsWrite && !written[resource]) {
                    throw new RenderGraphException(
                        $"Pass '{pass.Name}' reads '{resources[resource].Name}', which no earlier pass "
                        + "writes and which was not imported. Passes run in declaration order, so a "
                        + "producer declared later does not count — and the contents it would read are "
                        + "whatever was in that memory last frame."
                    );
                }
            }

            foreach (var use in pass.Uses) {
                if (use.IsWrite) {
                    written[(use.IsTexture ? use.Texture.Index : use.Buffer.Index) - 1] = true;
                }
            }
        }
    }

    /// <summary>Removes passes nothing needs.</summary>
    /// <remarks>
    ///     Worked backwards from what leaves the graph. A pass survives if it has a declared side
    ///     effect, writes an imported resource, or writes something a surviving pass reads — and that
    ///     last clause is why this iterates rather than sweeping once: removing a pass can orphan the
    ///     one that fed it.
    /// </remarks>
    void Cull() {
        foreach (var pass in passes) {
            pass.Survives = pass.HasSideEffect;
            pass.Consumers = 0;

            foreach (var use in pass.Uses) {
                if (!use.IsWrite) {
                    continue;
                }

                var resource = resources[(use.IsTexture ? use.Texture.Index : use.Buffer.Index) - 1];

                if (resource.IsImported) {
                    pass.Survives = true;
                }
            }
        }

        var changed = true;

        while (changed) {
            changed = false;

            for (var index = passes.Count - 1; index >= 0; index--) {
                var pass = passes[index];

                if (pass.Survives) {
                    continue;
                }

                if (IsReadByASurvivor(pass, index)) {
                    pass.Survives = true;
                    changed = true;
                }
            }
        }

        foreach (var pass in passes) {
            if (!pass.Survives) {
                continue;
            }

            foreach (var use in pass.Uses) {
                if (use.IsWrite) {
                    continue;
                }

                var index = (use.IsTexture ? use.Texture.Index : use.Buffer.Index) - 1;

                foreach (var producer in passes) {
                    if (producer.Survives && producer.Index < pass.Index && Writes(producer, index)) {
                        producer.Consumers++;
                    }
                }
            }
        }
    }

    bool IsReadByASurvivor(GraphPass pass, int passIndex) {
        foreach (var use in pass.Uses) {
            if (!use.IsWrite) {
                continue;
            }

            var resource = (use.IsTexture ? use.Texture.Index : use.Buffer.Index) - 1;

            for (var later = passIndex + 1; later < passes.Count; later++) {
                if (!passes[later].Survives) {
                    continue;
                }

                foreach (var laterUse in passes[later].Uses) {
                    var laterResource = (laterUse.IsTexture ? laterUse.Texture.Index : laterUse.Buffer.Index) - 1;

                    if (laterResource == resource && !laterUse.IsWrite) {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    static bool Writes(GraphPass pass, int resourceIndex) {
        foreach (var use in pass.Uses) {
            if (use.IsWrite && (use.IsTexture ? use.Texture.Index : use.Buffer.Index) - 1 == resourceIndex) {
                return true;
            }
        }

        return false;
    }

    void ComputeLifetimes() {
        foreach (var resource in resources) {
            resource.FirstUse = -1;
            resource.LastUse = -1;
            resource.ReadCount = 0;
            resource.IsWritten = false;
        }

        for (var index = 0; index < passes.Count; index++) {
            if (!passes[index].Survives) {
                continue;
            }

            foreach (var use in passes[index].Uses) {
                var resource = resources[(use.IsTexture ? use.Texture.Index : use.Buffer.Index) - 1];

                if (resource.FirstUse < 0) {
                    resource.FirstUse = index;
                }

                resource.LastUse = index;

                if (use.IsWrite) {
                    resource.IsWritten = true;
                } else {
                    resource.ReadCount++;
                }
            }
        }
    }

    /// <summary>Gives every used transient a physical resource.</summary>
    /// <remarks>
    ///     Acquired in first-use order and released at last use, so two resources whose lifetimes do
    ///     not overlap get the same one. A resource no surviving pass touches is not realised at all,
    ///     which is the other half of culling: a pass removed for having no consumers takes its
    ///     targets' memory with it.
    /// </remarks>
    void Realise() {
        var order = new List<GraphResource>();

        foreach (var resource in resources) {
            if (resource.FirstUse >= 0) {
                order.Add(resource);
                continue;
            }

            // ⚠ An import no surviving pass touched still has its handles, and RestoreImports still
            // has to be able to transition it. Culling decides whether the graph *uses* a resource;
            // it does not decide whether the resource exists, and an import exists by definition.
            //
            // Leaving these unassigned crashed the frame rather than drawing it wrong: an imported
            // swapchain whose only writer had been culled reached RestoreImports with a default
            // handle, and the barrier that would have moved it to Present threw "a texture handle
            // referred to nothing" from inside the backend — a null-handle error a frame's worth of
            // stack away from the empty document that caused it.
            if (resource.IsImported) {
                resource.Texture = resource.ImportedTexture;
                resource.View = resource.ImportedView;
                resource.Buffer = resource.ImportedBuffer;
            }
        }

        order.Sort(static (left, right) => left.FirstUse.CompareTo(right.FirstUse));

        foreach (var resource in order) {
            if (resource.IsImported) {
                resource.Texture = resource.ImportedTexture;
                resource.View = resource.ImportedView;
                resource.Buffer = resource.ImportedBuffer;
                continue;
            }

            if (resource.PoolSlot >= 0) {
                continue;
            }

            // Release everything whose last use is behind this one's first, so the slot is available.
            foreach (var earlier in order) {
                if (earlier.PoolSlot >= 0 && !earlier.IsImported && earlier.LastUse < resource.FirstUse) {
                    pool.Release(earlier.PoolSlot);
                    earlier.PoolSlot = -1;
                }
            }

            if (resource.IsTexture) {
                resource.PoolSlot = pool.AcquireTexture(resource.TextureDescription);
                resource.Texture = pool.TextureAt(resource.PoolSlot);
                resource.View = pool.ViewAt(resource.PoolSlot);
            } else {
                resource.PoolSlot = pool.AcquireBuffer(resource.BufferDescription);
                resource.Buffer = pool.BufferAt(resource.PoolSlot);
            }
        }
    }

    void ReleaseExpired(int passIndex) {
        foreach (var resource in resources) {
            if (!resource.IsImported && resource.PoolSlot >= 0 && resource.LastUse == passIndex) {
                pool.Release(resource.PoolSlot);
                resource.PoolSlot = -1;
            }
        }
    }

    /// <summary>Transitions everything a pass is about to touch, in one group.</summary>
    /// <remarks>
    ///     One group and not one per resource: a driver given ten barriers together inserts one
    ///     stall and given them one at a time inserts ten, and the RHI cannot batch them for us
    ///     because by the time it sees the second the first is recorded.
    /// </remarks>
    void EmitBarriers(ICommandList commandList, GraphPass pass) {
        bufferBarriers.Clear();
        textureBarriers.Clear();

        foreach (var use in pass.Uses) {
            var resource = resources[(use.IsTexture ? use.Texture.Index : use.Buffer.Index) - 1];

            // State is tracked per *virtual* resource, not per physical one, and that is deliberate.
            // Where two virtual resources share memory, the second one's first transition therefore
            // says it is coming from Undefined — which is legal from any state and means "the
            // contents may be discarded". That is precisely right for memory being taken over:
            // stating the true previous state would ask the driver to preserve garbage, and on
            // hardware with compressed render targets that costs a decompress for nothing.

            // A write to a resource already in the same state still needs a barrier: two passes
            // writing the same target back to back is a write-after-write hazard, and nothing about
            // the states being equal makes the first write visible to the second.
            if (resource.CurrentState == use.State && !use.IsWrite) {
                continue;
            }

            if (use.IsTexture) {
                textureBarriers.Add(new(resource.Texture, resource.CurrentState, use.State));
            } else {
                bufferBarriers.Add(new(resource.Buffer, resource.CurrentState, use.State));
            }

            resource.CurrentState = use.State;
        }

        if (bufferBarriers.Count == 0 && textureBarriers.Count == 0) {
            return;
        }

        BarrierCount += bufferBarriers.Count + textureBarriers.Count;
        commandList.Barrier(new(bufferBarriers.ToArray(), textureBarriers.ToArray()));
    }

    void RunWithAttachments(
        ICommandList commandList,
        RenderGraphContext context,
        GraphPass pass,
        int passIndex
    ) {
        var colour = new List<ColourAttachment>();
        DepthStencilAttachment? depth = null;
        var area = Int2.Zero;

        foreach (var attachment in pass.Attachments) {
            var resource = resources[attachment.Texture.Index - 1];

            if (area == Int2.Zero) {
                area = new(resource.TextureDescription.Width, resource.TextureDescription.Height);
            }

            var store = attachment.Store ?? DeriveStore(resource, passIndex);

            if (attachment.IsDepth) {
                depth = new(
                    resource.View,
                    attachment.Load,
                    store,
                    attachment.ClearDepth,
                    attachment.Load,
                    store,
                    attachment.ClearStencil,
                    attachment.ReadOnly
                );
            } else {
                colour.Add(new(resource.View, attachment.Load, store, attachment.ClearColour));
            }
        }

        context.RenderArea = area;
        commandList.BeginRenderPass(new(colour.ToArray(), depth, pass.Name));
        pass.Body!(context);
        commandList.EndRenderPass();
    }

    /// <summary>Whether an attachment's contents survive the pass.</summary>
    /// <remarks>
    ///     <para>
    ///         The decision that matters most on tiled hardware, and the one nobody remembers to make
    ///         by hand. A target nothing reads afterwards and that leaves the graph unused never has
    ///         to reach memory at all — the tile is resolved and discarded — and on a mobile GPU that
    ///         is the difference between a bandwidth-bound frame and one that is not.
    ///     </para>
    ///     <para>
    ///         Anything imported is always stored: the graph does not know what the importer will do
    ///         with it, and discarding a swapchain image is a black screen.
    ///     </para>
    /// </remarks>
    static StoreAction DeriveStore(GraphResource resource, int passIndex) =>
        resource.IsImported || resource.LastUse > passIndex ? StoreAction.Store : StoreAction.DontCare;

    /// <summary>Hands imported resources back in the state their owner expects.</summary>
    void RestoreImports(ICommandList commandList) {
        bufferBarriers.Clear();
        textureBarriers.Clear();

        foreach (var resource in resources) {
            if (!resource.IsImported
                || resource.ExitState == ResourceState.Undefined
                || resource.CurrentState == resource.ExitState) {
                continue;
            }

            if (resource.IsTexture) {
                textureBarriers.Add(new(resource.Texture, resource.CurrentState, resource.ExitState));
            } else {
                bufferBarriers.Add(new(resource.Buffer, resource.CurrentState, resource.ExitState));
            }

            resource.CurrentState = resource.ExitState;
        }

        if (bufferBarriers.Count == 0 && textureBarriers.Count == 0) {
            return;
        }

        BarrierCount += bufferBarriers.Count + textureBarriers.Count;
        commandList.Barrier(new(bufferBarriers.ToArray(), textureBarriers.ToArray()));
    }

    void ThrowIfCompiled() {
        if (compiled) {
            throw new RenderGraphException(
                "The graph has been compiled. Declaring into it now would change a frame whose passes "
                + "have already been culled and whose memory has already been assigned. Call Reset() "
                + "and build the next frame."
            );
        }
    }
}

/// <summary>A graph that does not describe a frame that could run.</summary>
/// <remarks>
///     Distinct from <see cref="ArgumentException" /> because it is almost never one argument that is
///     wrong: it is a relationship between two declarations, and the message names both.
/// </remarks>
public sealed class RenderGraphException : Exception {
    /// <summary>Creates one.</summary>
    /// <param name="message">What is wrong, naming the passes or resources involved.</param>
    public RenderGraphException(string message) : base(message) { }

    /// <summary>Creates one.</summary>
    public RenderGraphException() { }

    /// <summary>Creates one.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">What caused it.</param>
    public RenderGraphException(string message, Exception innerException) : base(message, innerException) { }
}
