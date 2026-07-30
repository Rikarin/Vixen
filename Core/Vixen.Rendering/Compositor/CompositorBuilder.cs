// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Rendering.Features;
using Vixen.Shaders;

namespace Vixen.Rendering.Compositor;

/// <summary>A name in a compositor asset that nothing was bound to.</summary>
/// <remarks>
///     Its own type rather than an <see cref="InvalidOperationException" />, because the caller can
///     do something about this one: a missing view or target is a host that has not registered
///     something yet, which an editor reports beside the asset rather than as a crash.
/// </remarks>
public sealed class CompositorBindingException : Exception {
    /// <summary>Creates the exception.</summary>
    /// <param name="node">Which node in the graph referred to the name.</param>
    /// <param name="kind">What kind of thing the name was meant to be.</param>
    /// <param name="name">The name.</param>
    public CompositorBindingException(string node, string kind, string name)
        : base($"Compositor node '{node}' refers to {kind} '{name}', which nothing bound.") {
        Node = node;
        Kind = kind;
        Name = name;
    }

    /// <summary>Creates the exception for a name that resolved to something unusable.</summary>
    /// <param name="node">Which node in the graph referred to the name.</param>
    /// <param name="kind">What kind of thing the name was meant to be.</param>
    /// <param name="name">The name.</param>
    /// <param name="reason">What is wrong with what it found, as a clause completing "which …".</param>
    /// <remarks>
    ///     A resolved name can still be the wrong thing — a buffer a copy names but that was never
    ///     declared as one a copy may touch. The three properties are the same three, so an editor
    ///     reporting this beside the asset points at the same node either way; only the sentence
    ///     differs.
    /// </remarks>
    public CompositorBindingException(string node, string kind, string name, string reason)
        : base($"Compositor node '{node}' refers to {kind} '{name}', which {reason}.") {
        Node = node;
        Kind = kind;
        Name = name;
    }

    /// <inheritdoc />
    public CompositorBindingException() { }

    /// <inheritdoc />
    public CompositorBindingException(string message) : base(message) { }

    /// <inheritdoc />
    public CompositorBindingException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Which node referred to the name.</summary>
    public string Node { get; } = string.Empty;

    /// <summary>What kind of thing it was meant to be.</summary>
    public string Kind { get; } = string.Empty;

    /// <summary>The name.</summary>
    public string Name { get; } = string.Empty;
}

/// <summary>
///     Turns an authored <see cref="GraphicsCompositorAsset" /> into a running compositor.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The asset names resources; the host binds the names.</strong> A texture handle
///         belongs to a device that did not exist when the file was written, and a
///         <see cref="RenderView" /> is built from a camera that moves — neither can be in a
///         document. So the file says <em>which</em> target and <em>which</em> view, and this is
///         where the two meet. One authored compositor then runs against a swapchain, an offscreen
///         buffer or a test's scratch texture without changing a line of it.
///     </para>
///     <para>
///         Stages are the exception and are created here rather than bound, because a stage <em>is</em>
///         its authored settings — a name, a sort mode and the blend and depth state its draws use.
///         There is nothing about a stage that only the host knows.
///     </para>
///     <para>
///         An unbound name throws <see cref="CompositorBindingException" /> naming the node, the kind
///         and the name. Binding what it can and quietly skipping the rest would produce a frame that
///         is missing a pass and reports nothing, which is the failure that takes a day to find.
///     </para>
/// </remarks>
public sealed class CompositorBuilder(RenderSystem system) {
    /// <summary>The schema version this builder understands.</summary>
    public const int SupportedVersion = 2;

    /// <summary>Views a node may draw from, by the name the asset uses.</summary>
    public Dictionary<string, RenderView> Views { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     What the post-process nodes need and a document cannot carry.
    /// </summary>
    /// <remarks>
    ///     A device, a shader-module cache, a descriptor allocator and a sampler cache — four things
    ///     that belong to a running renderer rather than to a file, and which every node built here
    ///     gets if they are set. Without them the tree still builds and the post nodes decline to
    ///     draw, which is what an editor loading a document with no device wants.
    /// </remarks>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>Where shader modules come from.</summary>
    public EffectPipelineDescriber? Modules { get; set; }

    /// <summary>Where descriptor sets come from.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>Where samplers come from.</summary>
    public SamplerCache? Samplers { get; set; }

    /// <summary>
    ///     What GPU culling runs on, when a document asks for it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Host-supplied for the reason <see cref="Descriptors" /> and <see cref="Samplers" />
    ///         are: a visibility group holds device memory that outlives the frame, a pyramid holds a
    ///         frame of depth, and neither is a thing a file can create. What the document decides is
    ///         where the two passes go; what a host decides is whether the device has any business
    ///         running them at all.
    ///     </para>
    ///     <para>
    ///         Null builds the nodes anyway and they do nothing, which is what a target with no
    ///         compute wants: one document, and the CPU path where the capability is missing.
    ///     </para>
    /// </remarks>
    public GpuVisibilityGroup? Visibility { get; set; }

    /// <summary>The depth pyramid the <c>HiZ</c> node builds and the culling pass tests against.</summary>
    public HiZPyramid? Occluders { get; set; }

    /// <summary>Where the indirect draw arguments go, for a document that asks for them.</summary>
    public GpuDrawArguments? Arguments { get; set; }

    /// <summary>
    ///     The cluster traversal a <c>ClusterCulling</c> node dispatches, or null.
    /// </summary>
    /// <remarks>
    ///     <see cref="Visibility" />'s counterpart for virtualized geometry, supplied on exactly the
    ///     same terms and for the same reason: it owns device buffers that outlive a frame, which a
    ///     document cannot create. Every one of the five below is independently null, and a node built
    ///     without them is a node that does nothing — which is what a document describing the
    ///     virtualized path says to a project that has no virtualized meshes in it.
    /// </remarks>
    public GpuClusterVisibility? Clusters { get; set; }

    /// <summary>The pool the traversal's pages live in.</summary>
    public MeshletPagePool? Pages { get; set; }

    /// <summary>The draw that fills the visibility buffer.</summary>
    public GpuClusterRaster? Raster { get; set; }

    /// <summary>The binning that sorts its tiles by material.</summary>
    public GpuVisibilityTiles? Tiles { get; set; }

    /// <summary>The per-material shading that consumes those bins.</summary>
    public GpuClusterResolve? Resolve { get; set; }

    /// <summary>The frame's per-frame constants, which the resolve's lighting reads.</summary>
    /// <remarks>
    ///     The same objects a forward draw binds. Since the lighting moved into <c>ClusteredShading</c>
    ///     the resolve declares the same sets a forward draw does, so what fills them is what already
    ///     fills them — see <see cref="VisibilityBufferRenderer.SceneConstants" />.
    /// </remarks>
    public SceneConstants? SceneConstants { get; set; }

    /// <summary>The frame's per-view constants, on the same terms.</summary>
    public ViewConstants? ViewConstants { get; set; }

    /// <summary>
    ///     The per-view block this build created, if the document declared one.
    /// </summary>
    /// <remarks>
    ///     <strong>The caller owns it.</strong> It holds a descriptor set layout and a buffer per view,
    ///     which is the only device state a build produces — created here because the document is what
    ///     describes the block, and disposed by whoever asked for the build, because a builder does not
    ///     outlive anything.
    /// </remarks>
    public ViewConstants? ViewBlock { get; private set; }

    /// <summary>The stages this build created, by name.</summary>
    public Dictionary<string, RenderStage> Stages { get; } = new(StringComparer.Ordinal);

    /// <summary>Node kinds this builder can build beyond the ones it knows itself.</summary>
    /// <remarks>
    ///     <para>
    ///         The seam that makes a compositor document extensible, and the one the effect set needed
    ///         first. <c>Vixen.Rendering.PostFx</c> holds nodes whose assets this assembly must not
    ///         know about — it is downstream, and a builder that named its types would be a cycle — so
    ///         a document naming <c>!Bloom</c> is built by a factory that project registers rather than
    ///         by a case in a switch here.
    ///     </para>
    ///     <para>
    ///         Which means a game's own effect is a node kind on exactly the same terms as a shipped
    ///         one: a <c>[DataContract]</c> asset for the YAML tag, and a factory. Without this, "the
    ///         frame is data" would have stopped at the node kinds this assembly happened to define.
    ///     </para>
    /// </remarks>
    public IList<ISceneRendererFactory> Factories { get; } = [];

    /// <summary>Builds the compositor an asset describes.</summary>
    /// <exception cref="CompositorBindingException">A name was not bound.</exception>
    /// <exception cref="NotSupportedException">The document is a version this cannot read.</exception>
    public GraphicsCompositor Build(GraphicsCompositorAsset asset) {
        ArgumentNullException.ThrowIfNull(asset);

        if (asset.Version != SupportedVersion) {
            throw new NotSupportedException(
                $"This compositor asset is version {asset.Version} and this build reads version "
                + $"{SupportedVersion}. Binding what it understands and dropping the rest would "
                + "produce a frame missing a pass and say nothing about it."
            );
        }

        // Counted per build, so rebuilding from a document that dropped a node does not leave a ring
        // sized for the one that used to be there.
        reductions = 0;
        argumentPasses = 0;

        // Cleared for the same reason, and it matters more: a stale entry here is a host holding a
        // node that is in no tree, filling a buffer no frame copies, with nothing to say why.
        Uploads.Clear();
        Readbacks.Clear();

        ViewBlock = asset.ViewBlock is { } block && Device is not null ? Block(block) : null;

        foreach (var declared in asset.Stages) {
            Stages[declared.Name] = AddStage(declared);
        }

        // Before the tree, because a permutation that changed after a variant resolved would leave
        // already-resolved variants compiled for the old value — and a node that draws is a node that
        // may resolve one. Both features say so in as many words.
        if (asset.GpuDriven is { } driven) {
            EnableGpuDriven(driven);
        }

        var compositor = new GraphicsCompositor(system) { Game = asset.Game is null ? null : Node(asset.Game) };

        foreach (var resource in asset.Resources) {
            compositor.Resources.Add(resource);
        }

        foreach (var buffer in asset.Buffers) {
            compositor.BufferResources.Add(buffer);
        }

        return compositor;
    }

    /// <summary>
    ///     What the document asked for and the device agreed to, after the last build.
    /// </summary>
    /// <remarks>
    ///     ⚠ <strong>Not the same as what was asked.</strong> Every flag in
    ///     <see cref="GpuDrivenAsset" /> is gated on a capability, so a document that asked for all of
    ///     it gets none of it on a target without descriptor indexing — and that is the correct frame
    ///     rather than a degraded one. This is how a host or an editor says which, without repeating
    ///     the capability checks the features already made.
    /// </remarks>
    public GpuDrivenResult GpuDriven { get; private set; }

    /// <summary>
    ///     Turns the frame's material and transform records on where the device takes them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Reached through the features rather than created here</strong>, which is the
    ///         same division everything else in this builder follows: a feature holds device memory
    ///         that outlives the frame, and a document cannot create one. What a document decides is
    ///         whether the frame draws this way; what a host decides is whether the features exist and
    ///         whether the table has a fallback texture to hand out.
    ///     </para>
    ///     <para>
    ///         ⚠ A material feature with no <c>Device</c> cannot answer the capability question and so
    ///         answers no. The device is taken from this builder when the feature has none, because a
    ///         host that set one here and not there meant the same device — and the alternative is a
    ///         document that asks for records and silently gets none.
    ///     </para>
    /// </remarks>
    void EnableGpuDriven(GpuDrivenAsset declared) {
        var materials = false;
        var transforms = false;
        var objects = false;

        foreach (var feature in system.Features) {
            if (feature is not RootRenderFeature root) {
                continue;
            }

            foreach (var subFeature in root.SubFeatures) {
                switch (subFeature) {
                    case MaterialRenderFeature material when declared.MaterialRecords:
                        material.Device ??= Device;
                        materials |= material.EnableRecords(Permutation(declared.Shader, "UseMaterialRecords"));
                        break;

                    case TransformRenderFeature transform when declared.TransformRecords:
                        transform.Device ??= Device;
                        transforms |= transform.EnableRecords(Permutation(declared.Shader, "UseTransformRecords"));
                        break;
                }
            }
        }

        // A second pass, because the answer depends on the first one and sub-features are in the
        // order a host added them. What addresses a per-object record is the draw's instance index,
        // and that holds the object's slot only because the transform path put it there — so a
        // lighting feature asked without transforms would read record zero for every object.
        foreach (var feature in system.Features) {
            if (feature is not RootRenderFeature root) {
                continue;
            }

            foreach (var subFeature in root.SubFeatures) {
                if (subFeature is ForwardLightingRenderFeature lighting && declared.TransformRecords) {
                    lighting.Device ??= Device;
                    objects |= lighting.EnableRecords(Permutation(declared.Shader, "UseObjectRecords"), transforms);
                }
            }
        }

        GpuDriven = new(materials, transforms, objects);
    }

    /// <summary>The shader's own key for a permutation, by the name the generator interns it under.</summary>
    static PermutationKey<bool> Permutation(string shader, string name) =>
        ParameterKeys.NewPermutation(false, $"{shader}.{name}");

    RenderStage AddStage(RenderStageAsset declared) {
        // Reused rather than added twice, so building a second compositor over one render system —
        // an editor reloading the asset — does not exhaust the 64-stage mask with duplicates.
        if (system.FindStage(declared.Name) is { } existing) {
            return Configure(existing, declared);
        }

        return Configure(system.AddStage(new(declared.Name, declared.SortMode)), declared);
    }

    static RenderStage Configure(RenderStage stage, RenderStageAsset declared) {
        stage.Blend = declared.Blend switch {
            BlendPreset.AlphaBlend => BlendState.AlphaBlend,
            BlendPreset.PremultipliedAlpha => BlendState.PremultipliedAlpha,
            BlendPreset.Additive => BlendState.Additive,
            _ => BlendState.Opaque
        };

        stage.DepthStencil = declared.Depth switch {
            DepthPreset.TestOnly => DepthStencilState.TestOnly,
            DepthPreset.Disabled => DepthStencilState.Disabled,
            _ => DepthStencilState.Default
        };

        stage.Rasterizer = new(
            declared.Cull,
            DepthClamp: declared.DepthClamp,
            DepthBias: declared.DepthBias,
            DepthBiasSlope: declared.DepthBiasSlope
        );

        return stage;
    }

    SceneRenderer Node(ISceneRendererAsset declared) =>
        declared switch {
            SequenceAsset sequence => Sequence(sequence),
            RenderPassAsset pass => Pass(pass),
            SingleStageAsset single => Single(single),
            ShadowMapAsset shadows => Cascades(shadows),
            PunctualShadowAsset punctual => Punctual(punctual),
            FullScreenAsset post => FullScreen(post),
            ComputeAsset compute => Compute(compute),
            BufferUploadAsset upload => Upload(upload),
            BufferReadbackAsset readback => Readback(readback),
            HiZAsset pyramid => Reduce(pyramid),
            GpuCullingAsset culling => Culling(culling),
            ClusterCullingAsset clusters => ClusterCulling(clusters),
            VisibilityBufferAsset visibility => VisibilityBuffer(visibility),
            _ => Extension(declared)
        };

    /// <summary>A node kind this assembly does not know, from whoever does.</summary>
    /// <remarks>
    ///     Asked after the built-ins rather than before, so a factory cannot quietly replace a node
    ///     kind the document's schema already defines — and the exception, when nothing answers, still
    ///     names the type nobody could build.
    /// </remarks>
    SceneRenderer Extension(ISceneRendererAsset declared) {
        foreach (var factory in Factories) {
            if (factory.Create(declared, this) is { } node) {
                return node;
            }
        }

        throw new CompositorBindingException(declared.Name, "a node kind", declared.GetType().Name);
    }

    SceneRendererSequence Sequence(SequenceAsset declared) {
        var node = new SceneRendererSequence { Name = declared.Name, Enabled = declared.Enabled };

        foreach (var child in declared.Children) {
            node.Children.Add(Node(child));
        }

        return node;
    }

    RenderPassRenderer Pass(RenderPassAsset declared) {
        var node = new RenderPassRenderer {
            Name = declared.Name,
            Enabled = declared.Enabled,
            SampleCount = declared.SampleCount,
            DepthTarget = declared.DepthTarget
        };

        // Names carried straight through rather than resolved here. A target is a render-graph
        // resource that does not exist until the frame declares it, so binding one at build time
        // would mean binding a texture that is reallocated, aliased or dropped every frame.
        foreach (var target in declared.ColourTargets) {
            node.ColourTargets.Add(target);
        }

        foreach (var read in declared.Reads) {
            node.Reads.Add(read);
        }

        foreach (var read in declared.BufferReads) {
            node.BufferReads.Add(read);
        }

        node.Descriptors.Slot = declared.Slot;
        node.Descriptors.Allocator = Descriptors;
        node.Samplers = Samplers;
        Bind(node.Descriptors, declared.Bindings);

        foreach (var child in declared.Children) {
            node.Children.Add(Node(child));
        }

        return node;
    }

    SingleStageRenderer Single(SingleStageAsset declared) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            View = Bind(Views, declared.Name, "view", declared.View),
            Stage = Stage(declared.Name, declared.Stage),
            Constants = ViewBlock
        };

    /// <summary>
    ///     The per-view block, and the descriptor set layout every shader in the frame agrees on.
    /// </summary>
    /// <remarks>
    ///     Members are named by parameter key rather than by offset alone, so a document cannot drift
    ///     from the block a shader reads without the build saying so. An empty list takes
    ///     <see cref="ViewConstants" />'s own default, which is the view-projection and the view
    ///     position — what it writes for every view whether or not anything asked.
    /// </remarks>
    ViewConstants Block(ViewBlockAsset declared) {
        // Touched so that the standard keys are interned before anything looks one up by name.
        _ = ViewConstants.ViewProjection;

        var layout = Device!.CreateDescriptorSetLayout(
            new(declared.Set, [new(declared.Binding, DescriptorKind.UniformBuffer, declared.Stages)], "View")
        );

        var block = new ViewConstants(Device) {
            Descriptors = Descriptors,
            Layout = layout,
            Slot = declared.Set,
            Binding = declared.Binding,
            Size = declared.Size
        };

        if (declared.Members.Length == 0) {
            return block;
        }

        block.Members.Clear();

        foreach (var member in declared.Members) {
            if (!ParameterKeys.TryGet(member.Name, out var key)) {
                // A name nothing interned is a document naming a parameter no shader declares, which
                // would otherwise be a value that silently never arrives.
                throw new CompositorBindingException("viewBlock", "parameter", member.Name);
            }

            block.Members.Add(new(key, member.Offset, member.Size));
        }

        return block;
    }

    ShadowMapRenderer Cascades(ShadowMapAsset declared) =>
        new ShadowMapRenderer {
            Name = declared.Name,
            Enabled = declared.Enabled,
            CasterStage = Stage(declared.Name, declared.Stage),
            Atlas = declared.Atlas,
            Constants = ViewBlock,
            CascadeCount = declared.CascadeCount,
            Resolution = declared.Resolution,
            ShadowDistance = declared.ShadowDistance,
            SplitLambda = declared.SplitLambda,
            Extrusion = declared.Extrusion
        };

    PunctualShadowRenderer Punctual(PunctualShadowAsset declared) =>
        new PunctualShadowRenderer {
            Name = declared.Name,
            Enabled = declared.Enabled,
            CasterStage = Stage(declared.Name, declared.Stage),
            Atlas = declared.Atlas,
            Resolution = declared.Resolution,
            TilesPerSide = declared.TilesPerSide
        };

    FullScreenRenderer FullScreen(FullScreenAsset declared) {
        var node = new FullScreenRenderer {
            Name = declared.Name,
            Enabled = declared.Enabled,
            ShaderName = declared.Shader,
            Blend = Blend(declared.Blend),
            Load = declared.Load,
            ConstantBinding = declared.ConstantBinding,
            Modules = Modules,
            Device = Device,
            Samplers = Samplers,
            Descriptors = { Slot = declared.Slot, Allocator = Descriptors }
        };

        foreach (var target in declared.ColourTargets) {
            node.ColourTargets.Add(target);
        }

        foreach (var read in declared.Reads) {
            node.Reads.Add(read);
        }

        foreach (var read in declared.BufferReads) {
            node.BufferReads.Add(read);
        }

        Bind(node.Descriptors, declared.Bindings);
        return node;
    }

    // How many nodes of each kind this build has placed, which is how deep their descriptor rings
    // have to be. A set may not be rewritten while a submitted command buffer references it, and two
    // nodes of the same kind in one document are two rewrites before a single submission — which
    // sizing a ring to frames in flight alone does not cover. Counted rather than inferred from
    // whether a late culling node exists, because a document may reduce twice without one and would
    // otherwise get a ring one deep for two builds.
    int reductions;
    int argumentPasses;

    /// <summary>The depth reduction, over the pyramid the host supplied.</summary>
    HiZRenderer Reduce(HiZAsset declared) {
        reductions++;

        if (Occluders is { } pyramid) {
            pyramid.BuildsPerFrame = reductions;
        }

        return new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Depth = declared.Depth,
            Pyramid = Occluders
        };
    }

    /// <summary>
    ///     The culling dispatch, and everything the document's answer implies about the frame.
    /// </summary>
    /// <remarks>
    ///     The one node that reaches past its own pass, because what it decides is not a pass: the
    ///     group is what <see cref="RenderSystem.Visibility" /> has to become for the frame to be
    ///     culled on the device at all, and the arguments are what every feature that draws from them
    ///     has to be given. Both are assignments a host would otherwise have to remember to make in
    ///     the same breath as placing the node — and forgetting either is a frame that culls on the
    ///     CPU, or draws everything, with nothing to say why.
    /// </remarks>
    GpuCullingRenderer Culling(GpuCullingAsset declared) {
        var late = declared.Phase == CullPhase.Late;

        var node = new GpuCullingRenderer {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Phase = declared.Phase,
            Visibility = Visibility,
            Arguments = declared.IndirectDraws ? Arguments : null
        };

        // Asked for here and answered by the buffer, which checks the device: `Compact` is what a
        // document requested and `IsCompacted` is what it got, and a draw loop reads the second.
        if (node.Arguments is not null) {
            node.Arguments.Compact = declared.Compact;
        }

        if (Visibility is { } group) {
            // A late node's own readBack is not read. Declaring a late phase *is* declaring the
            // in-frame path — the two dispatches have to straddle a set of draws, and the readback
            // path submits and waits before any of them are recorded — so the node that asks for it
            // says so, rather than a document being able to ask for two things that cannot both hold.
            // Nodes are built in document order and a late node can only follow a main one, so this
            // assignment is the later of the two.
            group.ReadBack = !late && declared.ReadBack;
            group.TwoPhase = late;
            group.Occluders = Occluders;
            system.Visibility = group;
        }

        if (node.Arguments is { } arguments) {
            arguments.DispatchesPerFrame = ++argumentPasses;
        }

        foreach (var feature in system.Features) {
            if (feature is IDrawArgumentSource source) {
                source.Arguments = node.Arguments;
            }
        }

        return node;
    }

    ClusterCullingRenderer ClusterCulling(ClusterCullingAsset declared) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Visibility = Clusters,
            Pages = Pages,
            Raster = Raster
        };

    VisibilityBufferRenderer VisibilityBuffer(VisibilityBufferAsset declared) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Output = declared.Output,
            Depth = declared.Depth,
            Colour = declared.Colour,
            ViewIndex = declared.ViewIndex,
            Raster = Raster,
            Tiles = Tiles,
            Resolve = Resolve,
            SceneConstants = SceneConstants,
            ViewConstants = ViewConstants
        };

    ComputeRenderer Compute(ComputeAsset declared) {
        var node = new ComputeRenderer {
            Name = declared.Name,
            Enabled = declared.Enabled,
            ShaderName = declared.Shader,
            Groups = new(declared.GroupsX, declared.GroupsY, declared.GroupsZ),
            Pipelines = Device is null ? null : new(Device),
            Samplers = Samplers,
            Descriptors = { Slot = declared.Slot, Allocator = Descriptors }
        };

        foreach (var read in declared.Reads) {
            node.Reads.Add(read);
        }

        foreach (var write in declared.Writes) {
            node.Writes.Add(write);
        }

        foreach (var read in declared.BufferReads) {
            node.BufferReads.Add(read);
        }

        foreach (var write in declared.BufferWrites) {
            node.BufferWrites.Add(write);
        }

        Bind(node.Descriptors, declared.Bindings);
        return node;
    }

    /// <summary>
    ///     The upload nodes this build placed, by name, so a host can give them their bytes.
    /// </summary>
    /// <remarks>
    ///     Tracked rather than left in the tree because that is the only thing a document cannot say
    ///     about an upload: it can place the copy and name what it fills, and the contents are this
    ///     frame's. Walking the tree to find a node by name would work and would be a second way to
    ///     refer to something the builder already has in its hand.
    /// </remarks>
    public Dictionary<string, BufferUploadRenderer> Uploads { get; } = new(StringComparer.Ordinal);

    /// <summary>The readback nodes this build placed, by name, so a host can take their bytes.</summary>
    /// <remarks>
    ///     <strong>The caller owns them.</strong> Each holds a readback buffer per frame in flight,
    ///     which — like <see cref="ViewBlock" /> — is device state a build produced and a builder does
    ///     not outlive. The same is true of <see cref="Uploads" /> and their staging.
    /// </remarks>
    public Dictionary<string, BufferReadbackRenderer> Readbacks { get; } = new(StringComparer.Ordinal);

    BufferUploadRenderer Upload(BufferUploadAsset declared) =>
        Uploads[declared.Name] = new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Buffer = declared.Buffer,
            Offset = declared.Offset
        };

    BufferReadbackRenderer Readback(BufferReadbackAsset declared) =>
        Readbacks[declared.Name] = new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Buffer = declared.Buffer,
            Offset = declared.Offset,
            Size = declared.Size,
            Latency = declared.Latency
        };

    /// <summary>Copies a document's bindings onto a node, translating its sampler presets.</summary>
    static void Bind(DescriptorBindings bindings, ResourceBindingAsset[] declared) {
        foreach (var binding in declared) {
            bindings.Bindings.Add(
                new() {
                    Name = binding.Name,
                    Binding = binding.Binding,
                    Kind = binding.Kind,
                    Resource = binding.Resource,
                    Offset = binding.Offset,
                    Size = binding.Size,
                    Sampled = binding.Sampler switch {
                        SamplerPreset.LinearClamp => SamplerDescription.LinearClamp,
                        SamplerPreset.PointClamp => SamplerDescription.PointClamp,
                        SamplerPreset.LinearRepeat => SamplerDescription.LinearRepeat,
                        SamplerPreset.Shadow => SamplerDescription.Shadow,
                        _ => null
                    }
                }
            );
        }
    }

    static BlendState Blend(BlendPreset preset) =>
        preset switch {
            BlendPreset.AlphaBlend => BlendState.AlphaBlend,
            BlendPreset.PremultipliedAlpha => BlendState.PremultipliedAlpha,
            BlendPreset.Additive => BlendState.Additive,
            _ => BlendState.Opaque
        };

    RenderStage Stage(string node, string name) =>
        Stages.TryGetValue(name, out var stage)
            ? stage
            : throw new CompositorBindingException(node, "stage", name);

    static TValue Bind<TValue>(Dictionary<string, TValue> bindings, string node, string kind, string name) =>
        bindings.TryGetValue(name, out var value)
            ? value
            : throw new CompositorBindingException(node, kind, name);
}

/// <summary>What a build's GPU-driven request actually turned on.</summary>
/// <param name="MaterialRecords">Whether materials became records of one buffer.</param>
/// <param name="TransformRecords">Whether world matrices left the command buffer.</param>
/// <param name="ObjectRecords">Whether the per-object scalars did too.</param>
/// <remarks>
///     Both false is the ordinary answer on most targets and is not a failure — see
///     <see cref="GpuDrivenAsset" />. What it is useful for is telling a "the device said no" frame
///     apart from a "nobody asked" one, which otherwise look identical from outside.
/// </remarks>
public readonly record struct GpuDrivenResult(bool MaterialRecords, bool TransformRecords, bool ObjectRecords);
