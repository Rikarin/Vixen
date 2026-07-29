// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Compositor nodes binding their own graph resources.
/// </summary>
/// <remarks>
///     <para>
///         Declaring a read was always only half of it. The declaration is what orders the producing
///         pass first and puts the barrier in; it does not put anything in front of a shader. Until
///         there was a per-frame allocator there was nowhere for the other half to live — a graph
///         resource has no handle until the graph compiles, so a node could not own a set the way a
///         material owns one.
///     </para>
///     <para>
///         The check on all of it is the last test: a binding may only name something the node itself
///         declared. Resolving against the frame at large would compile, and would sample a texture
///         nothing had transitioned.
///     </para>
/// </remarks>
public class DescriptorBindingTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true, FramesInFlight = 2 });
    readonly EffectSystem effects = new();
    readonly DescriptorAllocator allocator;
    readonly DescriptorSetLayoutHandle computeLayout;
    readonly DescriptorSetLayoutHandle viewLayout;

    public DescriptorBindingTests() {
        allocator = new(device);

        computeLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(0, DescriptorKind.StorageBuffer, ShaderStage.Compute),
                    new(1, DescriptorKind.StorageBuffer, ShaderStage.Compute)
                ],
                "Cull"
            )
        );

        // The sampler is declared even though most of the passes below bind only the buffer: a write
        // to a binding the layout never declared is one no shader could read, and the backend rejects
        // it rather than letting the described-sampler test pass on a set with nowhere to put it.
        viewLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerView,
                [
                    new(0, DescriptorKind.StorageBuffer, ShaderStage.Fragment),
                    new(1, DescriptorKind.Sampler, ShaderStage.Fragment)
                ],
                "View"
            )
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        allocator.Dispose();
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The fixture --------------------------------------------------------

    sealed class AlwaysCompiles(ImmutableArray<DescriptorSetLayoutHandle> layouts) : IEffectProvider {
        public Effect? TryGet(EffectKey key) =>
            new() {
                Key = key,
                Stages = key.ShaderName.Contains("Cull", StringComparison.Ordinal)
                    ? [new(ShaderStage.Compute, [1, 2, 3, 4], "main")]
                    : [
                        new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                        new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
                    ],
                SetLayouts = layouts,

                // What a provider backed by Raven's reflection would report, and what lets a caller
                // name a resource instead of numbering it.
                Bindings = [
                    new("lights", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.StorageBuffer),
                    new("clusters", DescriptorSetSlot.PerMaterial, 1, DescriptorKind.StorageBuffer)
                ]
            };
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required RenderStage Opaque { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required BufferHandle Vertices { get; init; }

        public void Dispose() {
            Graph.DisposePool();
            System.Dispose();
        }
    }

    ImportedBuffer Storage(string name) {
        var description = new BufferDescription(4096, BufferUsage.Storage, MemoryAccess.DeviceLocal, name);
        return new(device.CreateBuffer(description), description);
    }

    ImportedTexture Colour(string name) {
        var description = new TextureDescription(
            PixelFormat.Rgba16Float,
            256,
            256,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: name
        );

        var texture = device.CreateTexture(description);
        return new(texture, device.CreateTextureView(texture), description);
    }

    /// <summary>A compositor with the resources every test here binds, and nothing running yet.</summary>
    Harness Build() {
        var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };

        meshes.Add(materials);
        system.AddFeature(meshes);
        effects.AddProvider(new AlwaysCompiles([default, default, computeLayout, default]));

        var compositor = new GraphicsCompositor(system) { FrameSize = new(256, 256) };

        compositor.Imports["SceneColour"] = Colour("SceneColour");
        compositor.Imports["Overlay"] = Colour("Overlay");
        compositor.BufferImports["SceneLights"] = Storage("SceneLights");
        compositor.BufferResources.Add(new() { Name = "Clusters", Size = 4096, Usage = BufferUsage.Storage });

        return new() {
            System = system,
            Compositor = compositor,
            Graph = new(device),
            Opaque = opaque,
            Meshes = meshes,
            Materials = materials,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    ComputeRenderer Cull() {
        var node = new ComputeRenderer {
            Name = "Cull",
            ShaderName = "ClusterCull",
            Pipelines = new(device),
            Groups = new(4, 3, 6)
        };

        node.BufferReads.Add("SceneLights");
        node.BufferWrites.Add("Clusters");
        node.Descriptors.Allocator = allocator;

        node.Descriptors.Bindings.Add(
            new() { Binding = 0, Kind = DescriptorKind.StorageBuffer, Resource = "SceneLights" }
        );

        node.Descriptors.Bindings.Add(
            new() { Binding = 1, Kind = DescriptorKind.StorageBuffer, Resource = "Clusters" }
        );

        return node;
    }

    static void AddMesh(Harness h) {
        var id = h.System.Objects.Add(
            new() { Bounds = new(new Vector3(0f, 0f, -10f), 1f), Stages = h.Opaque.Mask, FeatureIndex = h.Meshes.Index }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, new("Lit"));
    }

    void Frame(Harness h) {
        var list = device.BeginCommandList();

        allocator.BeginFrame();
        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);
        h.Graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    // --- The tests ----------------------------------------------------------

    /// <summary>
    ///     A compute node binds what it declared, between the pipeline and the dispatch.
    /// </summary>
    /// <remarks>
    ///     What used to require the host to supply a callback, and therefore to know which graph
    ///     resource the node had resolved. The node knows; it just had no lifetime to put a set in.
    /// </remarks>
    [Fact]
    public void A_compute_node_binds_the_buffers_it_declared() {
        using var h = Build();
        var cull = Cull();

        var reader = new RenderPassRenderer { Name = "Forward" };
        reader.ColourTargets.Add("SceneColour");
        reader.BufferReads.Add("Clusters");

        h.Compositor.Game = new SceneRendererSequence { Children = { cull, reader } };

        Frame(h);

        var stream = device.Recorder!.Commands.ToList();
        var pipeline = stream.FindIndex(command => command.Kind == RecordedCommandKind.BindPipeline);
        var bind = stream.FindIndex(command => command.Kind == RecordedCommandKind.BindDescriptorSet);
        var dispatch = stream.FindIndex(command => command.Kind == RecordedCommandKind.Dispatch);

        Assert.True(dispatch > 0, "nothing dispatched");
        Assert.True(bind > pipeline, "the set was bound before the pipeline it belongs to");
        Assert.True(dispatch > bind, "the dispatch ran without its resources");
        Assert.Equal((long)DescriptorSetSlot.PerMaterial, stream[bind].A);
        Assert.Equal(1, allocator.WriteCount);
    }

    /// <summary>
    ///     The layout comes from the effect the pipeline was built from.
    /// </summary>
    /// <remarks>
    ///     <see cref="Effect.SetLayouts" /> has existed unused since the effect system was written, and
    ///     this is what it is for. A set is only bindable to a pipeline whose layout it was allocated
    ///     from, so letting the node take one from anywhere else is how a frame ends up with a set the
    ///     validation layers reject and a release driver mis-binds in silence.
    /// </remarks>
    [Fact]
    public void The_layout_comes_from_the_effect_when_the_host_supplies_none() {
        using var h = Build();
        var cull = Cull();

        var reader = new RenderPassRenderer { Name = "Forward" };
        reader.ColourTargets.Add("SceneColour");
        reader.BufferReads.Add("Clusters");

        h.Compositor.Game = new SceneRendererSequence { Children = { cull, reader } };

        Assert.False(cull.Descriptors.Layout.IsValid);

        Frame(h);

        Assert.Equal(computeLayout, cull.Descriptors.Layout);
    }

    /// <summary>A node with no allocator declares its dependencies and binds nothing.</summary>
    /// <remarks>
    ///     Which keeps the two halves independent: the ordering and the barriers are the graph's, and
    ///     a host that would rather bind resources its own way loses none of them.
    /// </remarks>
    [Fact]
    public void A_node_with_no_allocator_binds_nothing() {
        using var h = Build();
        var cull = Cull();
        cull.Descriptors.Allocator = null;

        var reader = new RenderPassRenderer { Name = "Forward" };
        reader.ColourTargets.Add("SceneColour");
        reader.BufferReads.Add("Clusters");

        h.Compositor.Game = new SceneRendererSequence { Children = { cull, reader } };

        Frame(h);

        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.Dispatch));
        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.BindDescriptorSet));
    }

    /// <summary>
    ///     A pass binds its reads once, before anything under it draws.
    /// </summary>
    /// <remarks>
    ///     Per-view rather than per-draw, so the materials drawing into the pass rebind sets 2 and 3
    ///     without disturbing it. That ordering is the whole reason the four slots are ordered by how
    ///     often they change.
    /// </remarks>
    [Fact]
    public void A_pass_binds_its_reads_before_its_children_draw() {
        using var h = Build();
        AddMesh(h);

        var shading = new RenderPassRenderer { Name = "Forward" };
        shading.ColourTargets.Add("SceneColour");
        shading.BufferReads.Add("SceneLights");
        shading.Descriptors.Allocator = allocator;
        shading.Descriptors.Layout = viewLayout;

        shading.Descriptors.Bindings.Add(
            new() { Binding = 0, Kind = DescriptorKind.StorageBuffer, Resource = "SceneLights" }
        );

        shading.Children.Add(new SingleStageRenderer { View = View(), Stage = h.Opaque });
        h.Compositor.Game = shading;

        Frame(h);

        var stream = device.Recorder!.Commands.ToList();
        var bind = stream.FindIndex(command => command.Kind == RecordedCommandKind.BindDescriptorSet);
        var draw = stream.FindIndex(command => command.Kind == RecordedCommandKind.Draw);

        Assert.True(draw > 0, "nothing was drawn");
        Assert.True(bind >= 0 && bind < draw, "the pass's set was not bound before its children drew");
        Assert.Equal((long)DescriptorSetSlot.PerView, stream[bind].A);
    }

    /// <summary>
    ///     Two passes reading the same thing share one set.
    /// </summary>
    /// <remarks>
    ///     The reason the allocator is content-addressed rather than a plain ring. Every pass in a
    ///     shading chain reads the same shadow atlas and the same light list, so a set per pass is a
    ///     set per pass for no reason at all.
    /// </remarks>
    [Fact]
    public void Two_passes_reading_the_same_thing_share_one_set() {
        using var h = Build();

        h.Compositor.Game = new SceneRendererSequence {
            Children = { Reader("First", "SceneColour"), Reader("Second", "Overlay") }
        };

        Frame(h);

        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.BindDescriptorSet));
        Assert.Equal(1, allocator.WriteCount);
        Assert.Equal(1, allocator.ReuseCount);
    }

    /// <summary>
    ///     A binding may only name something the node itself declared.
    /// </summary>
    /// <remarks>
    ///     The check on everything above. Resolving against the frame at large would compile and would
    ///     drop the edge that orders the producer first and places the barrier — so a pass would sample
    ///     a resource nothing had transitioned, which is corruption on a tiler and nothing at all on a
    ///     desktop driver until it is somebody else's machine.
    /// </remarks>
    [Fact]
    public void A_binding_naming_something_the_node_never_declared_is_refused() {
        using var h = Build();
        var cull = Cull();

        // Imported, resolvable, and never declared by this node.
        cull.Descriptors.Bindings.Add(
            new() { Binding = 2, Kind = DescriptorKind.StorageBuffer, Resource = "Undeclared" }
        );

        h.Compositor.BufferImports["Undeclared"] = Storage("Undeclared");

        var reader = new RenderPassRenderer { Name = "Forward" };
        reader.ColourTargets.Add("SceneColour");
        reader.BufferReads.Add("Clusters");

        h.Compositor.Game = new SceneRendererSequence { Children = { cull, reader } };

        var thrown = Assert.Throws<CompositorBindingException>(() => Frame(h));

        Assert.Equal("bound buffer", thrown.Kind);
        Assert.Equal("Undeclared", thrown.Name);
    }

    /// <summary>
    ///     A binding can name what the shader calls it instead of writing down an index.
    /// </summary>
    /// <remarks>
    ///     The seam that had been open since bindings existed. A binding index is Raven's decision —
    ///     assigned from declaration order within a set — so a host that wrote one down was recording
    ///     a number it could not see and would not be told about when a resource was added above it.
    ///     Generated constants close it for code that can reference generated code, and close nothing
    ///     for a compositor document or a shader loaded from a bundle. The effect's own plan is what
    ///     both of those need.
    /// </remarks>
    [Fact]
    public void A_binding_can_name_what_the_shader_calls_it() {
        using var h = Build();
        var cull = Cull();

        cull.Descriptors.Bindings.Clear();

        // No index anywhere: the shader says where these go.
        cull.Descriptors.Bindings.Add(new() { Name = "lights", Resource = "SceneLights" });
        cull.Descriptors.Bindings.Add(new() { Name = "clusters", Resource = "Clusters" });

        var reader = new RenderPassRenderer { Name = "Forward" };
        reader.ColourTargets.Add("SceneColour");
        reader.BufferReads.Add("Clusters");

        h.Compositor.Game = new SceneRendererSequence { Children = { cull, reader } };

        Frame(h);

        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BindDescriptorSet));
        Assert.Equal(1, allocator.WriteCount);
    }

    /// <summary>
    ///     A name the effect does not know falls back to the index that was written down.
    /// </summary>
    /// <remarks>
    ///     Because a provider may report no plan — a test fake, a host supplying effects of its own —
    ///     and a renderer that stopped binding anything the moment reflection was absent would be
    ///     worse than one that took the host at its word.
    /// </remarks>
    [Fact]
    public void An_unknown_name_falls_back_to_the_index() {
        using var h = Build();
        var cull = Cull();

        cull.Descriptors.Bindings.Clear();

        cull.Descriptors.Bindings.Add(
            new() {
                Name = "nothing the shader declares",
                Binding = 0,
                Kind = DescriptorKind.StorageBuffer,
                Resource = "SceneLights"
            }
        );

        var reader = new RenderPassRenderer { Name = "Forward" };
        reader.ColourTargets.Add("SceneColour");
        reader.BufferReads.Add("Clusters");

        h.Compositor.Game = new SceneRendererSequence { Children = { cull, reader } };

        Frame(h);

        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BindDescriptorSet));
    }

    /// <summary>
    ///     A sampler can be described rather than handed over, which is what an asset can carry.
    /// </summary>
    /// <remarks>
    ///     A <see cref="SamplerDescription" /> is twelve fields and no device, so it survives being
    ///     written in a document where a handle cannot. Resolved through the shared cache, which is
    ///     also what stops a chain of post passes creating one sampler each.
    /// </remarks>
    [Fact]
    public void A_sampler_can_be_described_rather_than_handed_over() {
        using var h = Build();
        using var samplers = new SamplerCache(device);

        // Its own layout rather than the shared `viewLayout`, which declares binding 0 and nothing
        // else: this is the one test that binds a second binding, and a set written at a binding its
        // layout never declared is refused by every backend — the Null one included, now that it
        // holds a write against the layout it was allocated from.
        var layout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerView,
                [
                    new(0, DescriptorKind.StorageBuffer, ShaderStage.Fragment),
                    new(1, DescriptorKind.Sampler, ShaderStage.Fragment)
                ],
                "ViewWithSampler"
            )
        );

        var pass = new RenderPassRenderer { Name = "Forward", Samplers = samplers };
        pass.ColourTargets.Add("SceneColour");
        pass.BufferReads.Add("SceneLights");
        pass.Descriptors.Allocator = allocator;
        pass.Descriptors.Layout = layout;

        pass.Descriptors.Bindings.Add(
            new() { Binding = 0, Kind = DescriptorKind.StorageBuffer, Resource = "SceneLights" }
        );

        pass.Descriptors.Bindings.Add(
            new() { Binding = 1, Kind = DescriptorKind.Sampler, Sampled = SamplerDescription.LinearClamp }
        );

        h.Compositor.Game = pass;

        Frame(h);

        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BindDescriptorSet));
        Assert.Equal(1, samplers.Count);
    }

    RenderPassRenderer Reader(string name, string target) {
        var pass = new RenderPassRenderer { Name = name };
        pass.ColourTargets.Add(target);
        pass.BufferReads.Add("SceneLights");
        pass.Descriptors.Allocator = allocator;
        pass.Descriptors.Layout = viewLayout;

        pass.Descriptors.Bindings.Add(
            new() { Binding = 0, Kind = DescriptorKind.StorageBuffer, Resource = "SceneLights" }
        );

        return pass;
    }

    static RenderView View() {
        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        return new("camera") { Position = Vector3.Zero, Frustum = new(view * projection) };
    }
}
