// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;

namespace Vixen.Editor.ShaderGraph;

/// <summary>Where a preview's texture becomes a number the interface can draw.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An interface for the reason <c>ThumbnailCache</c>'s is.</b> A number an image command
///         carries is handed out by <c>UiRenderer.RegisterImage</c>, and this assembly has no
///         interface renderer and should not acquire one. The host that has both hands one of these
///         over; without it a preview still compiles and still draws, and the swatch is what shows.
///     </para>
///     <para>
///         ⚠ <b>Unregistering is not optional.</b> A registered view whose texture has been destroyed
///         is a descriptor set pointing at freed memory, which is undefined behaviour rather than an
///         error — see <c>UiRenderer.RegisterImage</c>'s own warning. The renderer calls this before
///         every destroy.
///     </para>
/// </remarks>
public interface IPreviewImages {
    /// <summary>Names a texture, and returns the number to draw it by.</summary>
    /// <param name="view">The view. Owned by the caller, not by the sink.</param>
    /// <returns>The number, or zero if it could not be named.</returns>
    ulong Register(TextureViewHandle view);

    /// <summary>Gives up a number, before its texture is destroyed.</summary>
    /// <param name="image">What <see cref="Register" /> returned.</param>
    void Release(ulong image);
}

/// <summary>
///     One node's expression, compiled and run over a quad, into a target that outlives the edit.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two tiers, and the split is the whole design.</b> Turning a graph into Raven is string
///         work over a handful of nodes and is done every time a preview is asked for —
///         <see cref="ShaderGraphPreview.Compile" />. Turning that text into SPIR-V, a pipeline and a
///         drawn target costs tens of milliseconds and is done only when the <i>text</i> changed. So
///         moving a node, renaming a property, selecting, panning and zooming all emit the source
///         that is already cached and cost nothing; typing a number into a port emits different
///         source and costs one rebuild. That is the answer to "a shader compiled per keystroke is a
///         stall": there is no keystroke that reaches the compiler without changing the expression.
///     </para>
///     <para>
///         ⚠ <b>And rebuilds are rationed as well.</b> <see cref="RebuildsPerUpdate" /> is how many
///         may happen in one frame; the rest wait. Pasting twenty nodes invalidates twenty previews
///         and would otherwise be twenty compilations between two frames — visible as a freeze,
///         during which the editor is doing work for pictures the author has not looked at yet.
///     </para>
///     <para>
///         <b>The renderer owns every target, and there is one per node, not one per edit.</b> A
///         rebuild draws into the texture that is already there — the size never changes — so the
///         number the interface holds stays valid and the picture changes underneath it. A node that
///         is evicted or a renderer that is disposed unregisters the number first and destroys the
///         texture second. <see cref="Created" /> and <see cref="Destroyed" /> are equal after
///         <see cref="Dispose" />, and a test asserts it: a claim about a leak that cannot be
///         measured is one nobody can check.
///     </para>
///     <para>
///         ⚠ <b>Unlit, into a non-sRGB target, and neither is incidental.</b>
///         <see cref="ShaderGraphPreview" /> ends the closure at <c>Master/Unlit</c>, so the fragment
///         writes the node's value straight out with no lighting, no exposure and no tone map, and
///         <see cref="Format" /> is <c>Rgba8UNorm</c> rather than the sRGB form so nothing encodes it
///         on the way in. What a preview shows is the number, which is the only thing that makes a
///         preview worth looking at.
///     </para>
///     <para>
///         ⚠ <b>The quad is in clip space and its texture coordinate follows the engine's
///         convention.</b> Clip <c>y = +1</c> is the top — <c>Core/Vixen.Core.Mathematics/Conventions.md</c>,
///         and the Vulkan backend's negative-height viewport is what implements it — so the corner at
///         <c>y = +1</c> is given <c>texcoord.y = 0</c> and the target's first row is the top of the
///         picture. An interface image command therefore draws it unflipped, which is why
///         <c>NodePreview.FlipVertically</c> is not set. A preview that came out upside down would be
///         perfectly plausible to look at, so <c>ShaderGraphPreviewDeviceTests</c> asserts a corner
///         rather than a histogram.
///     </para>
///     <para>
///         ⚠ <b>A node whose expression needs a resource gets no picture.</b> The preview binds one
///         uniform block holding the two transforms and nothing else — no textures, no samplers — so
///         <c>Texture/Sample 2D</c> is refused rather than drawn as whatever an unbound descriptor
///         reads as. <see cref="Refusals" /> counts them. Binding a material's textures means knowing
///         which material, which is doc 08's material compiler and not a thumbnail's.
///     </para>
/// </remarks>
public sealed class ShaderGraphPreviewRenderer : INodePreviewSource, IDisposable {
    /// <summary>How big a preview is, in pixels.</summary>
    /// <remarks>
    ///     Bigger than <c>NodePreviewLayer.Size</c> draws it, so a zoomed-in canvas does not show a
    ///     soft square, and small enough that a graph full of them is under a megabyte.
    /// </remarks>
    public const int Size = 64;

    /// <summary>What a preview target is, and what its pipeline is built for.</summary>
    public const PixelFormat Format = PixelFormat.Rgba8UNorm;

    readonly IGraphicsDevice device;
    readonly NodeTypeRegistry registry;
    readonly IPreviewImages? images;
    readonly EffectLoader loader;

    /// <summary>
    ///     What each previewed node has, keyed by the graph as well as the node.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The graph is part of the key and leaving it out is a real bug.</b> A
    ///     <see cref="NodeId" /> is unique within one graph and every graph starts numbering at one,
    ///     so two shader graphs open in two tabs both have a <c>#1</c> — and an editor keyed on the
    ///     node alone would show one tab's picture under the other tab's node.
    /// </remarks>
    readonly Dictionary<(NodeGraphModel Graph, NodeId Node), Entry> entries = [];
    readonly Dictionary<NodeGraphModel, Watched> watched = [];
    readonly List<(NodeGraphModel Graph, NodeId Node)> recent = [];
    readonly List<(NodeGraphModel Graph, NodeId Node)> dirty = [];

    bool disposed;

    /// <summary>Builds a renderer on a device.</summary>
    /// <param name="device">Where the targets and the pipelines live.</param>
    /// <param name="registry">The node types the graphs are edited against.</param>
    /// <param name="images">
    ///     What turns a target into a number the interface draws, or <see langword="null" /> for a
    ///     renderer whose pictures nobody shows — which is what a test has and what a headless editor
    ///     has.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> or <paramref name="registry" /> is null.</exception>
    public ShaderGraphPreviewRenderer(IGraphicsDevice device, NodeTypeRegistry registry, IPreviewImages? images = null) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(registry);

        this.device = device;
        this.registry = registry;
        this.images = images;

        loader = new EffectLoader(device);
    }

    /// <summary>How many nodes keep a target before the least recently asked for loses one.</summary>
    /// <remarks>
    ///     A ceiling for <c>ThumbnailCache</c>'s reason. A graph of four hundred nodes scrolled past
    ///     once would otherwise hold four hundred targets, four hundred pipelines and four hundred
    ///     descriptor sets — and the canvas only ever asks about what is on screen.
    /// </remarks>
    public int Capacity { get; init; } = 64;

    /// <summary>How many previews may be rebuilt in one <see cref="Update" />.</summary>
    public int RebuildsPerUpdate { get; set; } = 2;

    /// <summary>How many times a graph has been turned into Raven — the cheap tier.</summary>
    public int Emissions { get; private set; }

    /// <summary>How many times Raven has been compiled and a pipeline built — the expensive tier.</summary>
    /// <remarks>
    ///     ⚠ <b>The number the throttling story is made of.</b> "An edit that does not change the
    ///     expression costs no compilation" is a claim about this counter and nothing else, which is
    ///     why it is public.
    /// </remarks>
    public int Compilations { get; private set; }

    /// <summary>How many previews have been drawn.</summary>
    public int Draws { get; private set; }

    /// <summary>How many targets have been created.</summary>
    public int Created { get; private set; }

    /// <summary>How many targets have been destroyed.</summary>
    public int Destroyed { get; private set; }

    /// <summary>How many nodes were refused a picture, for a resource or a compile that failed.</summary>
    public int Refusals { get; private set; }

    /// <summary>How many previews are held.</summary>
    public int Live => entries.Count;

    /// <summary>How many are waiting to be rebuilt.</summary>
    public int Pending => dirty.Count;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>It never compiles and never draws.</b> This is called from the canvas's draw, once per
    ///     visible node, which is no place to record commands on a device — so what it does is emit,
    ///     compare and answer with whatever picture already exists. The work is queued and
    ///     <see cref="Update" /> does it.
    ///     <para>
    ///         A node whose expression has changed keeps showing the old picture until the rebuild
    ///         lands, rather than showing nothing: a preview that blinked empty on every edit would be
    ///         worse than one that is a frame behind.
    ///     </para>
    /// </remarks>
    public bool TryGet(
        NodeGraphModel graph,
        GraphNode node,
        NodeTypeDefinition definition,
        out NodePreview preview
    ) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);

        preview = default;

        if (disposed) {
            return false;
        }

        var revision = Watch(graph);
        var key = (graph, node.Id);

        if (entries.TryGetValue(key, out var entry) && entry.Revision == revision) {
            // ⚠ The first of three gates, and the only one that runs sixty times a second. Nothing
            // about this graph has changed since the source was emitted, so there is nothing to
            // emit — every `NodeGraphCommand` calls `NodeGraphModel.Touch`, which is what moves the
            // revision on. Without it, a canvas of fifty nodes walks fifty closures and builds fifty
            // strings on every frame in which nobody did anything.
            Touch(key);

            return Answer(entry, out preview);
        }

        var compilation = ShaderGraphPreview.Compile(graph, node.Id, registry);

        Emissions++;

        if (compilation.Artefact is not { } source) {
            return false;
        }

        Touch(key);

        if (entry is not null) {
            entry.Revision = revision;

            // The second gate: the graph changed and this node's expression did not. Moving a node,
            // renaming a property, editing a value on a node this one does not depend on — all of
            // them arrive here and none of them costs a compilation.
            if (!string.Equals(entry.Source, source.Source, StringComparison.Ordinal)) {
                entry.Source = source.Source;

                if (!dirty.Contains(key)) {
                    dirty.Add(key);
                }
            }
        } else {
            entries[key] = new Entry { Source = source.Source, Revision = revision };
            dirty.Add(key);
            Evict();

            return false;
        }

        return Answer(entry, out preview);
    }

    /// <summary>What a node's preview is, once it is known there is nothing to recompute.</summary>
    static bool Answer(Entry entry, out NodePreview preview) {
        preview = default;

        if (entry.Image == 0) {
            return false;
        }

        preview = new NodePreview(new Color4(1f, 1f, 1f, 1f), Image: entry.Image);

        return true;
    }

    /// <summary>Rebuilds what has been invalidated, up to <see cref="RebuildsPerUpdate" />.</summary>
    /// <returns>How many were rebuilt.</returns>
    /// <exception cref="ObjectDisposedException">The renderer has been disposed.</exception>
    /// <remarks>
    ///     ⚠ <b>Called between <c>BeginFrame</c> and <c>EndFrame</c>, on the thread that owns the
    ///     device</b>, like every other queue this editor drains. It records and submits a command
    ///     list of its own rather than taking one, so a caller does not have to find a point in the
    ///     frame where it is safe to be outside a render pass — and the submit is ordered before the
    ///     interface's own, which is what makes the target readable in the same frame it was drawn.
    /// </remarks>
    public int Update() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (dirty.Count == 0) {
            return 0;
        }

        var taken = Math.Min(RebuildsPerUpdate, dirty.Count);
        List<(NodeGraphModel Graph, NodeId Node)> built = [];

        for (var index = 0; index < taken; index++) {
            var key = dirty[index];

            if (entries.TryGetValue(key, out var entry) && Build(key.Node, entry)) {
                built.Add(key);
            }
        }

        dirty.RemoveRange(0, taken);

        if (built.Count == 0) {
            return 0;
        }

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "shader graph previews")) {
            foreach (var key in built) {
                Draw(commands, entries[key]);
            }

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        return built.Count;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The device is idled first.</b> A target may be the one the frame in flight is
    ///     sampling, and destroying it underneath that frame is a use-after-free the validation layer
    ///     reports somewhere else entirely — the same rule <c>ThumbnailSurface</c> defers for and the
    ///     same one <c>EditorHost</c> follows before a pane goes.
    /// </remarks>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        device.WaitIdle();

        foreach (var entry in entries.Values) {
            Destroy(entry);
        }

        foreach (var (graph, state) in watched) {
            graph.Changed -= state.Handler;
        }

        watched.Clear();
        entries.Clear();
        recent.Clear();
        dirty.Clear();
    }

    /// <summary>Why one node has no picture, or null when it has one or was never asked about.</summary>
    /// <param name="graph">The graph it is in.</param>
    /// <param name="node">The node.</param>
    /// <returns>The refusal.</returns>
    public string? RefusalFor(NodeGraphModel graph, NodeId node) =>
        entries.TryGetValue((graph, node), out var entry) ? entry.Refusal : null;

    /// <summary>The target one node's picture is in, for a caller that reads it back.</summary>
    /// <param name="graph">The graph it is in.</param>
    /// <param name="node">The node.</param>
    /// <remarks>
    ///     ⚠ <b>Internal, and it exists for the test that asserts the picture is a picture.</b> A
    ///     preview that renders nothing looks exactly like a preview that is not implemented, so
    ///     something has to be able to read the bytes; the texture carries
    ///     <see cref="TextureUsage.CopySource" /> for no other reason. It is left in
    ///     <see cref="ResourceState.ShaderRead" />, so a reader barriers from there.
    /// </remarks>
    internal TextureHandle TextureOf(NodeGraphModel graph, NodeId node) =>
        entries.TryGetValue((graph, node), out var entry) ? entry.Texture : default;

    /// <summary>Compiles one preview's Raven and makes everything the draw needs.</summary>
    bool Build(NodeId id, Entry entry) {
        Release(entry);

        Compilations++;

        Effect effect;

        try {
            var data = Compile(entry.Source);

            if (data is null) {
                Refusals++;

                return false;
            }

            effect = loader.Load(data);
        } catch (Exception failure) when (failure is ShaderCompilationException or ArgumentException or IOException) {
            // ⚠ A refusal rather than a throw. A half-wired graph emits Raven that does not type
            // check every few seconds while somebody is building one, and an editor that fell over
            // when a preview did not compile would be an editor nobody could author a graph in.
            entry.Refusal = failure.Message;
            Refusals++;

            return false;
        }

        // The one resource this binds is the uniform block holding the two transforms. Anything else
        // — a texture, a sampler — is a material's and a preview has no material.
        foreach (var binding in effect.Bindings) {
            if (binding.Kind is not (DescriptorKind.UniformBuffer or DescriptorKind.DynamicUniformBuffer)) {
                entry.Refusal =
                    $"'{binding.Name}' is a {binding.Kind} and a preview binds no resources, so there is "
                    + "nothing this node could be shown against.";
                Refusals++;

                return false;
            }
        }

        entry.Refusal = null;

        Ensure(id, entry);

        var block = effect.BlockOf(DescriptorSetSlot.PerMaterial);

        if (block.Exists) {
            entry.Constants = device.CreateBuffer(
                new(block.Size, BufferUsage.Uniform, MemoryAccess.HostUpload, "shader graph preview constants")
            );

            device.Write(entry.Constants, 0, Transforms(block));

            entry.Descriptors = device.CreateDescriptorSet(
                effect.SetLayouts[(int)DescriptorSetSlot.PerMaterial],
                "shader graph preview"
            );

            device.UpdateDescriptorSet(entry.Descriptors, [DescriptorWrite.Uniform(block.Binding, entry.Constants)]);
        }

        var (vertices, layout) = Geometry(effect);

        if (vertices.Length > 0) {
            entry.Vertices = device.CreateBuffer(
                new(
                    vertices.Length * sizeof(float),
                    BufferUsage.Vertex,
                    MemoryAccess.HostUpload,
                    "shader graph preview quad"
                )
            );

            device.Write(entry.Vertices, 0, MemoryMarshal.AsBytes<float>(vertices));
        }

        entry.Vertex = device.CreateShader(ShaderStage.Vertex, Bytecode(effect, ShaderStage.Vertex), "preview vertex");
        entry.Fragment = device.CreateShader(ShaderStage.Fragment, Bytecode(effect, ShaderStage.Fragment), "preview fragment");

        entry.Pipeline = device.CreateGraphicsPipeline(
            new(
                entry.Vertex,
                entry.Fragment,
                effect.Layout,
                [new ColourTargetState(Format, BlendState.Opaque)],
                layout.Length > 0 ? [new VertexBufferLayout(layout.Stride, layout.Elements)] : [],
                // Two-sided: a quad whose winding disagrees with the rasterizer draws nothing at all,
                // and that failure is indistinguishable from a preview nobody implemented.
                Rasterizer: RasterizerState.TwoSided,
                DepthStencil: DepthStencilState.Disabled,
                Name: "shader graph preview"
            )
        );

        return true;
    }

    /// <summary>Records one preview's pass.</summary>
    void Draw(ICommandList commands, Entry entry) {
        commands.Barrier(new BarrierGroup([], [new TextureBarrier(entry.Texture, entry.State, ResourceState.ColourTarget)]));

        commands.BeginRenderPass(
            new RenderPassDescription(
                [new ColourAttachment(entry.View, LoadAction.Clear, StoreAction.Store, new Color4(0f, 0f, 0f, 1f))],
                name: "shader graph preview"
            )
        );

        commands.SetViewport(new Viewport(0f, 0f, Size, Size));
        commands.SetScissor(new ScissorRect(0, 0, Size, Size));
        commands.BindPipeline(entry.Pipeline);

        if (entry.Descriptors.IsValid) {
            commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, entry.Descriptors);
        }

        if (entry.Vertices.IsValid) {
            commands.BindVertexBuffer(0, entry.Vertices);
        }

        commands.Draw(Quad.Length);
        commands.EndRenderPass();

        commands.Barrier(
            new BarrierGroup([], [new TextureBarrier(entry.Texture, ResourceState.ColourTarget, ResourceState.ShaderRead)])
        );

        entry.State = ResourceState.ShaderRead;
        Draws++;
    }

    /// <summary>The target for a node, made once and drawn into for ever after.</summary>
    void Ensure(NodeId id, Entry entry) {
        if (entry.Texture.IsValid) {
            return;
        }

        entry.Texture = device.CreateTexture(
            new(
                Format,
                Size,
                Size,
                TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource,
                Name: $"shader graph preview {id}"
            )
        );

        entry.View = device.CreateTextureView(entry.Texture);
        entry.State = ResourceState.Undefined;
        entry.Image = images?.Register(entry.View) ?? 0;

        Created++;
    }

    /// <summary>Compiles one preview's Raven, in memory.</summary>
    /// <remarks>
    ///     One tree, no references and no composition: a graph's output imports nothing, so the
    ///     compilation is the generated file and no library needs to be on the path.
    /// </remarks>
    static EffectData? Compile(string source) =>
        RavenEffectCompiler.FromSources([(ShaderGraphPreview.Name + ".rvn", source)])
            .TryGet(EffectKey.Of(ShaderGraphPreview.Name));

    static ReadOnlySpan<byte> Bytecode(Effect effect, ShaderStage stage) {
        foreach (var compiled in effect.Stages) {
            if (compiled.Stage == stage) {
                return compiled.Bytecode.AsSpan();
            }
        }

        return [];
    }

    /// <summary>Identity for the two transforms every graph declares, and zero for anything else.</summary>
    /// <remarks>
    ///     ⚠ <b>Identity rather than a camera.</b> The quad's corners are already in clip space, so
    ///     the fixed vertex stage's <c>worldViewProjection * float4(position, 1f)</c> has to be the
    ///     identity for them to land where they were put — and <c>world</c> likewise, so a graph
    ///     reading a world position previews the quad's own coordinates rather than a translated
    ///     copy. A block left at zero would project every corner to the origin and draw nothing,
    ///     which is the failure this method exists to make impossible.
    /// </remarks>
    static byte[] Transforms(EffectBlock block) {
        var bytes = new byte[block.Size];

        foreach (var parameter in block.Members) {
            if (parameter.Size < 64 || !IsTransform(parameter.Key.Name)) {
                continue;
            }

            var identity = Matrix4x4.Identity;

            MemoryMarshal.Write(bytes.AsSpan(parameter.Offset, 64), in identity);
        }

        return bytes;
    }

    /// <summary>Whether a reflected parameter is one of the two transforms every graph declares.</summary>
    /// <remarks>
    ///     ⚠ <b>The last dotted segment, compared exactly.</b> Raven's reflection qualifies a
    ///     parameter by the block it is in, so the name arrives as something like
    ///     <c>Preview.world</c> — and a suffix test would also match an authored property called
    ///     <c>underworld</c>, which would then be handed the identity matrix.
    /// </remarks>
    static bool IsTransform(string name) {
        var bare = name.AsSpan()[(name.LastIndexOf('.') + 1)..];

        return bare.SequenceEqual("worldViewProjection") || bare.SequenceEqual("world");
    }

    /// <summary>The quad, interleaved for exactly the attributes this variant's vertex stage reads.</summary>
    /// <remarks>
    ///     ⚠ <b>Built from the reflection rather than from a fixed struct.</b> A graph that never
    ///     reads a normal has no <c>normal</c> parameter on its vertex entry point, so a buffer laid
    ///     out for one would put the texture coordinate where the shader is looking for a position.
    ///     What each attribute holds is <see cref="Corner" />'s.
    /// </remarks>
    static (float[] Vertices, Layout Layout) Geometry(Effect effect) {
        List<VertexElement> elements = [];
        List<(int Lanes, Func<Corner, Vector4> Of)> readers = [];
        var offset = 0;

        foreach (var input in effect.VertexInputs.OrderBy(input => input.Location)) {
            var lanes = LanesOf(input.Kind);

            if (lanes == 0) {
                continue;
            }

            elements.Add(new((uint)input.Location, FormatOf(lanes), offset));
            readers.Add((lanes, ReaderOf(input.Name)));
            offset += lanes * sizeof(float);
        }

        var stride = offset;
        var floats = new List<float>(Quad.Length * Math.Max(1, stride / sizeof(float)));

        foreach (var corner in Quad) {
            foreach (var (lanes, of) in readers) {
                var value = of(corner);

                for (var lane = 0; lane < lanes; lane++) {
                    floats.Add(lane switch { 0 => value.X, 1 => value.Y, 2 => value.Z, _ => value.W });
                }
            }
        }

        return ([.. floats], new Layout(stride, [.. elements]));
    }

    static int LanesOf(ShaderValueKind kind) => kind switch {
        ShaderValueKind.Float => 1,
        ShaderValueKind.Float2 => 2,
        ShaderValueKind.Float3 => 3,
        ShaderValueKind.Float4 => 4,
        _ => 0
    };

    static VertexFormat FormatOf(int lanes) => lanes switch {
        1 => VertexFormat.Float32,
        2 => VertexFormat.Float32X2,
        3 => VertexFormat.Float32X3,
        _ => VertexFormat.Float32X4
    };

    /// <summary>What one named vertex attribute holds, corner by corner.</summary>
    static Func<Corner, Vector4> ReaderOf(string name) => name switch {
        "texcoord" => corner => new Vector4(corner.Uv.X, corner.Uv.Y, 0f, 0f),
        // Facing the viewer, so a graph reading a world normal previews a flat surface rather than
        // one whose normal is zero — which normalises to a NaN and shades as nothing.
        "normal" => _ => new Vector4(0f, 0f, 1f, 0f),
        "colour" => _ => new Vector4(1f, 1f, 1f, 1f),
        _ => corner => new Vector4(corner.Position.X, corner.Position.Y, 0f, 1f)
    };

    /// <summary>This graph's revision, subscribing to it the first time it is seen.</summary>
    /// <remarks>
    ///     ⚠ <b>A counter rather than a flag, and per graph rather than per node.</b> A graph has no
    ///     per-node change notification and inventing one would mean teaching every command which
    ///     nodes it touched; what it has is <c>Changed</c>, raised by every
    ///     <c>NodeGraphCommand</c>. So the counter says "something happened" and the source comparison
    ///     says whether it happened to <i>this</i> node.
    ///     <para>
    ///         The handler is remembered so <see cref="Dispose" /> can take it off. A renderer left
    ///         subscribed to a document's graph is a renderer the document keeps alive, and its
    ///         targets with it.
    ///     </para>
    /// </remarks>
    int Watch(NodeGraphModel graph) {
        if (watched.TryGetValue(graph, out var watching)) {
            return watching.Revision;
        }

        var state = new Watched();

        state.Handler = _ => state.Revision++;
        graph.Changed += state.Handler;
        watched[graph] = state;

        return state.Revision;
    }

    void Touch((NodeGraphModel Graph, NodeId Node) key) {
        recent.Remove(key);
        recent.Add(key);
    }

    void Evict() {
        while (recent.Count > Math.Max(1, Capacity)) {
            var oldest = recent[0];

            recent.RemoveAt(0);
            dirty.Remove(oldest);

            if (entries.Remove(oldest, out var entry)) {
                Destroy(entry);
            }
        }
    }

    /// <summary>Destroys what a rebuild replaces, keeping the target and its number.</summary>
    void Release(Entry entry) {
        if (entry.Pipeline.IsValid) {
            device.Destroy(entry.Pipeline);
            entry.Pipeline = default;
        }

        if (entry.Vertex.IsValid) {
            device.Destroy(entry.Vertex);
            entry.Vertex = default;
        }

        if (entry.Fragment.IsValid) {
            device.Destroy(entry.Fragment);
            entry.Fragment = default;
        }

        if (entry.Descriptors.IsValid) {
            device.Destroy(entry.Descriptors);
            entry.Descriptors = default;
        }

        if (entry.Constants.IsValid) {
            device.Destroy(entry.Constants);
            entry.Constants = default;
        }

        if (entry.Vertices.IsValid) {
            device.Destroy(entry.Vertices);
            entry.Vertices = default;
        }
    }

    void Destroy(Entry entry) {
        Release(entry);

        if (!entry.Texture.IsValid) {
            return;
        }

        // ⚠ The number goes before the texture does, or the interface holds a descriptor naming freed
        // memory and the next frame that draws it is undefined rather than wrong.
        if (entry.Image != 0) {
            images?.Release(entry.Image);
            entry.Image = 0;
        }

        device.Destroy(entry.View);
        device.Destroy(entry.Texture);

        entry.Texture = default;
        entry.View = default;

        Destroyed++;
    }

    /// <summary>One corner of the quad: where it is in clip space, and what it samples.</summary>
    /// <remarks>
    ///     ⚠ <b><c>y = +1</c> is the top and takes <c>v = 0</c>.</b> The engine's clip space is
    ///     Y-up — the Vulkan backend expresses it with a negative-height viewport — so the row a
    ///     texture is read from first corresponds to the corner at <c>+1</c>. Getting this backwards
    ///     produces a preview that is perfectly plausible and upside down.
    /// </remarks>
    readonly record struct Corner(Vector2 Position, Vector2 Uv);

    static readonly Corner[] Quad = [
        new(new Vector2(-1f, 1f), new Vector2(0f, 0f)),
        new(new Vector2(-1f, -1f), new Vector2(0f, 1f)),
        new(new Vector2(1f, 1f), new Vector2(1f, 0f)),
        new(new Vector2(1f, 1f), new Vector2(1f, 0f)),
        new(new Vector2(-1f, -1f), new Vector2(0f, 1f)),
        new(new Vector2(1f, -1f), new Vector2(1f, 1f))
    ];

    readonly record struct Layout(int Stride, VertexElement[] Elements) {
        public int Length => Elements.Length;
    }

    /// <summary>One graph's revision, and the handler that moves it.</summary>
    sealed class Watched {
        public int Revision = 1;
        public Action<NodeGraphModel> Handler = _ => { };
    }

    sealed class Entry {
        public required string Source { get; set; }

        /// <summary>The graph revision this source was emitted at.</summary>
        public int Revision { get; set; }

        /// <summary>Why this node has no picture, or null when it has one or has not been built.</summary>
        public string? Refusal { get; set; }

        public TextureHandle Texture;
        public TextureViewHandle View;
        public ResourceState State = ResourceState.Undefined;
        public ulong Image;

        public PipelineHandle Pipeline;
        public ShaderHandle Vertex;
        public ShaderHandle Fragment;
        public BufferHandle Constants;
        public BufferHandle Vertices;
        public DescriptorSetHandle Descriptors;
    }
}
