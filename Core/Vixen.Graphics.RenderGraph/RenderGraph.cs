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

    readonly List<RenderGraphSegment> segments = [];

    int generation = 1;
    bool compiled;
    int handovers;

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

    /// <summary>How many barriers the last <see cref="Execute(ICommandList)" /> emitted.</summary>
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
    ///     Called automatically by <see cref="Execute(ICommandList)" />. Separate because a frame debugger wants to
    ///     compile and inspect without running, and because a validation failure should name the
    ///     declaration that caused it rather than arriving mid-frame.
    /// </remarks>
    public void Compile() {
        if (compiled) {
            return;
        }

        ValidateReads();
        ValidateSampleCounts();
        Cull();
        Lint();
        ComputeLifetimes();
        BuildSegments();
        PlanBarriers();
        Schedule = new([.. segments], QueuesOfPasses(), handovers);
        compiled = true;
    }

    /// <summary>Whether the frame is spread over the device's queues, or all on the graphics one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="QueueScheduling.Single" /> by default, and it is not a placeholder.</b>
    ///         A second queue only pays when the work on it genuinely overlaps, and the RHI has no
    ///         primitive yet that lets one submission wait on another without draining a whole queue
    ///         (<see cref="SerialisedQueues" />) — so <see cref="QueueScheduling.Async" /> today buys
    ///         correctness coverage rather than frame time.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Turning it on is a claim every pass in the frame has to be able to keep.</b>
    ///         <see cref="PassKind" /> was carried and never read, which means no renderer's
    ///         declaration of it has ever been checked against what its body records. A pass that says
    ///         <see cref="PassKind.Compute" /> and draws is a frame that stops working; a pass that
    ///         says it and dispatches is fine. That is why this is opt-in per graph rather than a
    ///         default that arrives with an upgrade.
    ///     </para>
    ///     <para>
    ///         Read by <see cref="Compile" />. Changing it after the graph has compiled changes
    ///         nothing until the next <see cref="Reset" />.
    ///     </para>
    /// </remarks>
    public QueueScheduling Scheduling { get; set; } = QueueScheduling.Single;

    /// <summary>Which queue each pass runs on. Meaningless before <see cref="Compile" />.</summary>
    public RenderGraphSchedule? Schedule { get; private set; }

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
    ///         ⚠ <b>Set it before <see cref="Execute(ICommandList)" />, and leave it set.</b> The graph reads it
    ///         once per execution, so attaching one mid-frame times part of a frame and detaching one
    ///         mid-frame leaves a scope open. It survives <see cref="Reset" /> because the graph
    ///         outlives the frame it describes.
    ///     </para>
    /// </remarks>
    public IGpuScopeSink? Profiler { get; set; }

    /// <summary>Runs it, all on one queue.</summary>
    /// <param name="commandList">Where the work is recorded.</param>
    /// <exception cref="RenderGraphException">
    ///     The schedule uses more than one queue, which one list cannot express.
    /// </exception>
    public void Execute(ICommandList commandList) {
        ArgumentNullException.ThrowIfNull(commandList);
        Compile();

        if (Schedule!.IsMultiQueue) {
            throw new RenderGraphException(
                "The graph scheduled passes onto more than one queue and was handed one command list. "
                + "A list belongs to one queue, so a frame on two of them needs one list each — call "
                + "Execute(IRenderGraphQueues), or leave Scheduling at Single."
            );
        }

        BarrierCount = 0;
        Realise();

        var context = new RenderGraphContext(this, commandList);
        RunSegment(segments[0], commandList, context);
    }

    /// <summary>Runs it, one command list per segment of the schedule.</summary>
    /// <param name="queues">Where the lists come from and where they go.</param>
    /// <exception cref="ArgumentNullException"><paramref name="queues" /> is null.</exception>
    /// <exception cref="RenderGraphException">A list arrived belonging to the wrong queue.</exception>
    /// <remarks>
    ///     <para>
    ///         Valid whatever <see cref="Scheduling" /> says and whatever the device can do: a
    ///         single-queue schedule is one segment, so this records one list and submits it, which is
    ///         what <see cref="Execute(ICommandList)" /> does except that the submission is not the
    ///         caller's any more.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The graph submits here, and <see cref="Execute(ICommandList)" /> does not.</b> That
    ///         asymmetry is the whole reason for the second overload — a frame whose lists the caller
    ///         submitted afterwards could not put anything between two of them, and everything
    ///         interesting about a second queue is what goes between.
    ///     </para>
    /// </remarks>
    public void Execute(IRenderGraphQueues queues) {
        ArgumentNullException.ThrowIfNull(queues);
        Compile();

        BarrierCount = 0;
        Realise();

        var context = new RenderGraphContext(this, null);

        foreach (var segment in segments) {
            var list = queues.Begin(segment);

            if (list is null) {
                throw new RenderGraphException($"No command list was given for {segment.Name}.");
            }

            if (list.Kind != segment.Queue) {
                throw new RenderGraphException(
                    $"{segment.Name} was given a {list.Kind} command list. A list may only be submitted "
                    + "to the queue it was opened for, and the barriers in this segment name the queue "
                    + "the segment was scheduled onto."
                );
            }

            context.Retarget(list);
            RunSegment(segment, list, context);
            queues.Submit(segment, list);
        }
    }

    void RunSegment(RenderGraphSegment segment, ICommandList commandList, RenderGraphContext context) {
        // Read once. A sink swapped between passes would open a scope on one and close it on
        // another, and the field is public precisely so a host can change it between frames.
        var sink = Profiler;

        for (var index = segment.FirstPass; index <= segment.LastPass; index++) {
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

            EmitBarriers(commandList, pass.Barriers);

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
                // ⚠ Guarded on the name being there, and no longer for safety's sake. It used to be:
                // the Vulkan backend returned early on an empty string while its pop was
                // unconditional, so pushing "" and popping it anyway closed a group somebody else
                // opened, and every pass after it in the capture hung under a label it had nothing to
                // do with. That asymmetry is fixed where it lived — `VulkanCommandList.UnnamedGroup`
                // opens a placeholder and the pop refuses to underflow — so this guard is now only
                // taste: a nameless group is a level in the tree that carries no information.
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

        // The release halves of this segment's handovers, and — on the last one — the transitions
        // that put imports back the way their owner expects. Outside the profiler's scopes on
        // purpose: they belong to the segment, not to the pass that happened to be last in it.
        EmitBarriers(commandList, segment.Tail);
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
        segments.Clear();
        Schedule = null;
        handovers = 0;
        compiled = false;
        generation++;
    }

    /// <summary>The compiled graph as Graphviz DOT.</summary>
    /// <remarks>
    ///     <para>
    ///         What [05](../../docs/plan/05-graphics-rhi.md) asks for and what a frame debugger
    ///         renders. Culled passes are drawn dashed rather than omitted, because "why did my pass
    ///         not run" is the question this is most often opened to answer.
    ///     </para>
    ///     <para>
    ///         A multi-queue schedule is drawn as a box per segment, with the wait edges between them
    ///         dashed and bold. That picture answers the second question this gets opened for — "did
    ///         anything actually overlap" — which the numbers cannot: two segments on two queues with
    ///         an edge between them are two segments that run one after the other, and the shape of
    ///         that is obvious in a drawing and invisible in a list.
    ///     </para>
    /// </remarks>
    public string ToGraphviz() {
        Compile();
        var text = new System.Text.StringBuilder();
        text.AppendLine("digraph RenderGraph {");
        text.AppendLine("  rankdir=LR;");
        text.AppendLine("  node [fontname=\"Helvetica\"];");

        if (Schedule!.IsMultiQueue) {
            foreach (var segment in segments) {
                text.AppendLine($"  subgraph cluster_s{segment.Index} {{");
                text.AppendLine($"    label=\"{Escape(segment.Name)}\";");
                text.AppendLine($"    style={(segment.Queue == QueueKind.Graphics ? "solid" : "dashed")};");

                for (var index = segment.FirstPass; index <= segment.LastPass; index++) {
                    if (passes[index].Survives) {
                        text.AppendLine($"    p{index};");
                    }
                }

                text.AppendLine("  }");
            }
        }

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

        foreach (var segment in segments) {
            foreach (var producer in segment.WaitsOn) {
                var from = LastSurvivor(producer);
                var to = FirstSurvivor(segment);

                if (from >= 0 && to >= 0) {
                    text.AppendLine($"  p{from} -> p{to} [style=dashed,penwidth=2,label=\"waits\"];");
                }
            }
        }

        text.AppendLine("}");
        return text.ToString();
    }

    int FirstSurvivor(RenderGraphSegment segment) {
        for (var index = segment.FirstPass; index <= segment.LastPass; index++) {
            if (passes[index].Survives) {
                return index;
            }
        }

        return -1;
    }

    int LastSurvivor(RenderGraphSegment segment) {
        for (var index = segment.LastPass; index >= segment.FirstPass; index--) {
            if (passes[index].Survives) {
                return index;
            }
        }

        return -1;
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
    ///     Valid after <see cref="Execute(ICommandList)" /> and until <see cref="Reset" />. What a caller uses to
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

    /// <summary>Refuses a pass whose attachments disagree about how many samples they have.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The mistake the resolve pair makes easy.</b> A frame turning MSAA on has to raise the
    ///         sample count on the pass <em>and</em> on every texture it attaches, and the depth one is
    ///         the easy one to forget: a document says <c>sampleCount: 4</c> on its colour target,
    ///         leaves its depth target at one, and gets a validation-layer message about a framebuffer
    ///         rather than about the line it left out.
    ///     </para>
    ///     <para>
    ///         An exception rather than a warning, on <see cref="ValidateReads" />'s terms and not
    ///         <see cref="Lint" />'s: this is not a frame that draws something imperfect, it is a frame
    ///         no backend will begin.
    ///     </para>
    /// </remarks>
    void ValidateSampleCounts() {
        foreach (var pass in passes) {
            if (pass.Attachments.Count < 2) {
                continue;
            }

            var first = resources[pass.Attachments[0].Texture.Index - 1];

            foreach (var attachment in pass.Attachments) {
                var resource = resources[attachment.Texture.Index - 1];

                if (resource.TextureDescription.SampleCount == first.TextureDescription.SampleCount) {
                    continue;
                }

                throw new RenderGraphException(
                    $"Pass '{pass.Name}' attaches '{first.Name}' at "
                    + $"{first.TextureDescription.SampleCount}× and '{resource.Name}' at "
                    + $"{resource.TextureDescription.SampleCount}×. Every attachment of one pass has "
                    + "the same sample count — raising it on the colour targets and leaving the depth "
                    + "target behind is the usual way here."
                );
            }
        }
    }

    /// <summary>Refuses a resolve pair a backend would reject or silently mis-resolve.</summary>
    /// <remarks>
    ///     <para>
    ///         All three conditions are ones a validation layer will report and a release driver will
    ///         not, and the third is the one worth the check: a resolve between two <em>differently
    ///         sized</em> attachments is undefined rather than a scale, so it reads as a picture that
    ///         is subtly cropped rather than as an error.
    ///     </para>
    ///     <para>
    ///         Checked here, at declaration, rather than at execution — the caller who named the pair
    ///         is on the stack, and by execution the only thing left to say is which pass it was.
    ///     </para>
    /// </remarks>
    internal void ValidateResolve(GraphTexture texture, GraphTexture resolve) {
        var source = resources[texture.Index - 1];
        var destination = resources[resolve.Index - 1];

        if (source.TextureDescription.SampleCount <= 1) {
            throw new RenderGraphException(
                $"'{source.Name}' has one sample and was given '{destination.Name}' to resolve into. "
                + "Only a multisampled attachment resolves; a single-sampled one is already its own "
                + "answer."
            );
        }

        if (destination.TextureDescription.SampleCount != 1) {
            throw new RenderGraphException(
                $"'{destination.Name}' has {destination.TextureDescription.SampleCount} samples and is "
                + $"the resolve target of '{source.Name}'. A resolve target is where the samples stop."
            );
        }

        if (destination.TextureDescription.Format != source.TextureDescription.Format) {
            throw new RenderGraphException(
                $"'{source.Name}' is {source.TextureDescription.Format} and resolves into "
                + $"'{destination.Name}', which is {destination.TextureDescription.Format}. A resolve "
                + "averages samples; it does not convert."
            );
        }

        if (destination.TextureDescription.Width != source.TextureDescription.Width
            || destination.TextureDescription.Height != source.TextureDescription.Height) {
            throw new RenderGraphException(
                $"'{source.Name}' is {source.TextureDescription.Width}×{source.TextureDescription.Height} "
                + $"and resolves into '{destination.Name}', which is "
                + $"{destination.TextureDescription.Width}×{destination.TextureDescription.Height}. A "
                + "resolve is per texel, not a blit."
            );
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
        var aliasing = !Schedule!.IsMultiQueue;
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

            // ⚠ No aliasing once the frame is on two queues. "Their lifetimes do not overlap" means
            // "no pass between them touches either", and that is a statement about pass order — which
            // stops being a statement about *time* the moment two queues run at once. Two transients
            // sharing memory across a queue boundary is a corruption bug that needs the two queues to
            // be genuinely concurrent to show up, which is to say it appears on the user's discrete
            // card and never in CI. Memory is the cheaper thing to spend.
            if (!aliasing) {
                resource.PoolSlot = Acquire(resource);
                continue;
            }

            // Release everything whose last use is behind this one's first, so the slot is available.
            foreach (var earlier in order) {
                if (earlier.PoolSlot >= 0 && !earlier.IsImported && earlier.LastUse < resource.FirstUse) {
                    pool.Release(earlier.PoolSlot);
                    earlier.PoolSlot = -1;
                }
            }

            resource.PoolSlot = Acquire(resource);
        }
    }

    /// <summary>Takes a pool slot for a transient and fills in the handles it was given.</summary>
    int Acquire(GraphResource resource) {
        if (resource.IsTexture) {
            var slot = pool.AcquireTexture(resource.TextureDescription);
            resource.Texture = pool.TextureAt(slot);
            resource.View = pool.ViewAt(slot);
            return slot;
        }

        var buffer = pool.AcquireBuffer(resource.BufferDescription);
        resource.Buffer = pool.BufferAt(buffer);
        return buffer;
    }

    void ReleaseExpired(int passIndex) {
        foreach (var resource in resources) {
            if (!resource.IsImported && resource.PoolSlot >= 0 && resource.LastUse == passIndex) {
                pool.Release(resource.PoolSlot);
                resource.PoolSlot = -1;
            }
        }
    }

    /// <summary>Records a planned group of transitions, in one call.</summary>
    /// <remarks>
    ///     One group and not one per resource: a driver given ten barriers together inserts one
    ///     stall and given them one at a time inserts ten, and the RHI cannot batch them for us
    ///     because by the time it sees the second the first is recorded.
    /// </remarks>
    void EmitBarriers(ICommandList commandList, List<PlannedBarrier> planned) {
        if (planned.Count == 0) {
            return;
        }

        bufferBarriers.Clear();
        textureBarriers.Clear();

        foreach (var barrier in planned) {
            var resource = resources[barrier.Resource];

            if (resource.IsTexture) {
                textureBarriers.Add(
                    new(resource.Texture, barrier.Before, barrier.After, 0, 0, 0, 0, barrier.From, barrier.To)
                );
            } else {
                bufferBarriers.Add(new(resource.Buffer, barrier.Before, barrier.After, barrier.From, barrier.To));
            }
        }

        BarrierCount += bufferBarriers.Count + textureBarriers.Count;
        commandList.Barrier(new(bufferBarriers.ToArray(), textureBarriers.ToArray()));
    }

    /// <summary>Assigns every surviving pass a queue and cuts the frame into segments.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Only <see cref="PassKind.Compute" /> leaves the graphics queue, and only where the
    ///         device has a compute family of its own.</b> A pass declaring attachments stays put
    ///         whatever it says its kind is — attachments are a draw, and the RHI already refuses a
    ///         render pass on a compute list — and so does <see cref="PassKind.Transfer" />, which is
    ///         the one kind whose queue can do <em>less</em> than the graphics queue: a Vulkan
    ///         transfer family accepts copies and nothing else, and no pass in this tree has ever had
    ///         its body checked against that, because until now nothing read the declaration at all.
    ///         Hoisting those is a separate piece of work with an audit in it.
    ///     </para>
    ///     <para>
    ///         Segments are cut where the queue changes and nowhere else. Passes are never reordered:
    ///         a schedule is a partition of the declaration order, which is what lets the two ways of
    ///         running one frame be compared call for call.
    ///     </para>
    /// </remarks>
    void BuildSegments() {
        segments.Clear();

        var hoisting = Scheduling == QueueScheduling.Async && device.Features.HasAsyncCompute;
        GraphPass? first = null;

        foreach (var pass in passes) {
            pass.Queue = hoisting && pass.Survives && pass.Kind == PassKind.Compute && !pass.HasAttachments
                ? QueueKind.Compute
                : QueueKind.Graphics;

            first ??= pass.Survives ? pass : null;
        }

        if (first is null) {
            // A frame with nothing in it still needs somewhere for the import restores to go.
            segments.Add(new(0, QueueKind.Graphics, 0, -1));
            return;
        }

        var imported = false;

        foreach (var resource in resources) {
            if (resource.IsImported) {
                imported = true;
                break;
            }
        }

        // ⚠ An import arrives owned by the graphics queue and has to be *released* from it before
        // another queue may read it — so a frame whose first pass is a compute one needs a graphics
        // list in front of it to record that release on. Without this the release lands at the end of
        // the very segment that acquires, which is a handover recorded entirely on the destination
        // queue: legal to record, checked by nothing, and the acquiring pass reads whatever the
        // memory held. Empty lists at the two ends are a cheap price, and neither is added unless
        // there is an import to hand over.
        if (imported && first.Queue != QueueKind.Graphics) {
            segments.Add(new(0, QueueKind.Graphics, first.Index, first.Index - 1));
        }

        RenderGraphSegment? open = null;

        foreach (var pass in passes) {
            if (!pass.Survives) {
                continue;
            }

            if (open is null || open.Queue != pass.Queue) {
                open = new(segments.Count, pass.Queue, pass.Index, pass.Index);
                segments.Add(open);
            } else {
                open.LastPass = pass.Index;
            }

            pass.Segment = open.Index;
        }

        // The same argument at the other end: an import has to be handed *back* to graphics, and the
        // frame's last segment is whatever the last pass happened to be on. Next frame starts by
        // assuming graphics owns it, which is the assumption the restore exists to keep true.
        if (imported && open!.Queue != QueueKind.Graphics) {
            segments.Add(new(segments.Count, QueueKind.Graphics, passes.Count, passes.Count - 1));
        }
    }

    /// <summary>
    ///     Decides every barrier in the frame, including both halves of every queue handover.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Planned rather than derived while recording, because a handover is two barriers and
    ///         they are not adjacent.</b> The release belongs at the end of the segment that owns the
    ///         resource; the acquire belongs in front of the pass that wants it — and by the time
    ///         recording reaches that pass, the releasing segment's list has been submitted. One walk
    ///         decides both, from the same states, which is also what makes them identical: a release
    ///         and an acquire that disagree about the layout are undefined rather than an error.
    ///     </para>
    ///     <para>
    ///         State is tracked per <em>virtual</em> resource, not per physical one, and that is
    ///         deliberate. Where two virtual resources share memory, the second one's first
    ///         transition says it is coming from <see cref="ResourceState.Undefined" /> — legal from
    ///         any state, and meaning the contents may be discarded. That is precisely right for
    ///         memory being taken over: stating the true previous state would ask the driver to
    ///         preserve garbage, and on hardware with compressed render targets that costs a
    ///         decompress for nothing. It is also why coming <em>from</em> Undefined needs no
    ///         handover — nobody is claiming the contents survive.
    ///     </para>
    /// </remarks>
    void PlanBarriers() {
        handovers = 0;

        foreach (var resource in resources) {
            resource.CurrentState = resource.IsImported ? resource.EntryState : ResourceState.Undefined;
            resource.CurrentQueue = QueueKind.Graphics;
            resource.CurrentSegment = 0;
            resource.LastWriteSegment = -1;
            resource.ReaderSegments.Clear();
        }

        foreach (var segment in segments) {
            segment.Tail.Clear();
            segment.Waits.Clear();
        }

        foreach (var pass in passes) {
            pass.Barriers.Clear();

            if (!pass.Survives) {
                continue;
            }

            var segment = segments[pass.Segment];

            foreach (var use in pass.Uses) {
                var index = (use.IsTexture ? use.Texture.Index : use.Buffer.Index) - 1;
                var resource = resources[index];

                // Every cross-queue hazard, whether or not it also needs a barrier. A barrier orders
                // one queue against itself; two queues need an edge, and read-after-write,
                // write-after-read and write-after-write are all of them.
                if (resource.LastWriteSegment >= 0) {
                    AddWait(segment, segments[resource.LastWriteSegment]);
                }

                if (use.IsWrite) {
                    foreach (var reader in resource.ReaderSegments) {
                        AddWait(segment, segments[reader]);
                    }
                }

                var crosses = resource.CurrentQueue != pass.Queue;

                // A write to a resource already in the same state still needs a barrier: two passes
                // writing the same target back to back is a write-after-write hazard, and nothing
                // about the states being equal makes the first write visible to the second.
                if (crosses || resource.CurrentState != use.State || use.IsWrite) {
                    var transfers = crosses && resource.CurrentState != ResourceState.Undefined;
                    var from = transfers ? resource.CurrentQueue : pass.Queue;
                    var barrier = new PlannedBarrier(index, resource.CurrentState, use.State, from, pass.Queue);
                    pass.Barriers.Add(barrier);

                    if (transfers) {
                        segments[resource.CurrentSegment].Tail.Add(barrier);
                        handovers++;
                        AddWait(segment, segments[resource.CurrentSegment]);
                    }

                    resource.CurrentState = use.State;
                }

                resource.CurrentQueue = pass.Queue;
                resource.CurrentSegment = pass.Segment;

                if (use.IsWrite) {
                    resource.LastWriteSegment = pass.Segment;
                    resource.ReaderSegments.Clear();
                } else if (!resource.ReaderSegments.Contains(pass.Segment)) {
                    resource.ReaderSegments.Add(pass.Segment);
                }
            }
        }

        PlanRestores();
        ReduceWaits();
    }

    /// <summary>Hands imported resources back in the state — and on the queue — their owner expects.</summary>
    /// <remarks>
    ///     The queue half is as load-bearing as the state half and less obvious. Ownership starts each
    ///     frame on graphics because that is where the caller's own work is; an import left on the
    ///     compute queue makes next frame's first barrier a lie, and a lie about ownership reads as
    ///     the resource's contents being whatever the memory held.
    /// </remarks>
    void PlanRestores() {
        var last = segments[^1];

        for (var index = 0; index < resources.Count; index++) {
            var resource = resources[index];

            if (!resource.IsImported) {
                continue;
            }

            var wantsState = resource.ExitState != ResourceState.Undefined
                && resource.CurrentState != resource.ExitState;

            var wantsQueue = resource.CurrentQueue != QueueKind.Graphics;

            if (!wantsState && !wantsQueue) {
                continue;
            }

            var after = resource.ExitState != ResourceState.Undefined
                ? resource.ExitState
                : resource.CurrentState;

            var transfers = wantsQueue && resource.CurrentState != ResourceState.Undefined;
            var from = transfers ? resource.CurrentQueue : QueueKind.Graphics;
            var barrier = new PlannedBarrier(index, resource.CurrentState, after, from, QueueKind.Graphics);
            last.Tail.Add(barrier);

            if (transfers) {
                segments[resource.CurrentSegment].Tail.Add(barrier);
                handovers++;
                AddWait(last, segments[resource.CurrentSegment]);
            }

            resource.CurrentState = after;
            resource.CurrentQueue = QueueKind.Graphics;
        }
    }

    static void AddWait(RenderGraphSegment consumer, RenderGraphSegment producer) {
        // Same queue needs no edge: two lists submitted to one queue run in the order they were
        // submitted, and asking for a wait there would be asking the device to order something it
        // cannot reorder.
        if (producer.Index >= consumer.Index || producer.Queue == consumer.Queue) {
            return;
        }

        if (!consumer.Waits.Contains(producer)) {
            consumer.Waits.Add(producer);
        }
    }

    /// <summary>Drops wait edges that another wait edge already implies.</summary>
    /// <remarks>
    ///     Computed against the unreduced graph and applied afterwards, because a transitive reduction
    ///     read while it is being written gives a different answer depending on the order the segments
    ///     happen to be in.
    /// </remarks>
    void ReduceWaits() {
        var original = new Dictionary<RenderGraphSegment, RenderGraphSegment[]>();

        foreach (var segment in segments) {
            original[segment] = [.. segment.Waits];
        }

        foreach (var segment in segments) {
            var waits = original[segment];

            if (waits.Length < 2) {
                continue;
            }

            segment.Waits.Clear();

            foreach (var producer in waits) {
                var implied = false;

                foreach (var other in waits) {
                    if (!ReferenceEquals(other, producer) && Reaches(other, producer, original)) {
                        implied = true;
                        break;
                    }
                }

                if (!implied) {
                    segment.Waits.Add(producer);
                }
            }
        }
    }

    /// <summary>Whether waiting for one segment already waits for another.</summary>
    static bool Reaches(
        RenderGraphSegment from,
        RenderGraphSegment target,
        Dictionary<RenderGraphSegment, RenderGraphSegment[]> waits
    ) {
        if (target.Index >= from.Index) {
            return false;
        }

        // Everything earlier on the same queue is finished by the time this one is, without an edge.
        if (target.Queue == from.Queue) {
            return true;
        }

        foreach (var producer in waits[from]) {
            if (ReferenceEquals(producer, target) || Reaches(producer, target, waits)) {
                return true;
            }
        }

        return false;
    }

    QueueKind[] QueuesOfPasses() {
        var queues = new QueueKind[passes.Count];

        foreach (var pass in passes) {
            queues[pass.Index] = pass.Queue;
        }

        return queues;
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
            } else if (attachment.Resolve.IsValid) {
                // The store is the resolve, whatever the derivation said. `DeriveStore` answers "does
                // anything read this later", and for a multisampled attachment the answer is almost
                // always no — what the next pass reads is the resolve target beside it. Letting that
                // derivation win would store `DontCare` and resolve nothing, which is the one failure
                // shape MSAA has: a correctly multisampled pass whose result never leaves the tile.
                colour.Add(
                    new(
                        resource.View,
                        attachment.Load,
                        StoreAction.Resolve,
                        attachment.ClearColour,
                        resources[attachment.Resolve.Index - 1].View
                    )
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
