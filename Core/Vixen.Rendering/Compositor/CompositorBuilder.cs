// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
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

        ViewBlock = asset.ViewBlock is { } block && Device is not null ? Block(block) : null;

        foreach (var declared in asset.Stages) {
            Stages[declared.Name] = AddStage(declared);
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
            BloomAsset bloom => Bloom(bloom),
            ComputeAsset compute => Compute(compute),
            _ => throw new CompositorBindingException(
                declared.Name,
                "a node kind",
                declared.GetType().Name
            )
        };

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

        var stages = declared.Stages switch {
            ShaderStages.Vertex => ShaderStage.Vertex,
            ShaderStages.Pixel => ShaderStage.Fragment,
            _ => ShaderStage.Vertex | ShaderStage.Fragment
        };

        var layout = Device!.CreateDescriptorSetLayout(
            new(declared.Set, [new(declared.Binding, DescriptorKind.UniformBuffer, stages)], "View")
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

    BloomRenderer Bloom(BloomAsset declared) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            ShaderName = declared.Shader,
            Source = declared.Source,
            Output = declared.Output,
            Levels = declared.Levels,
            Format = declared.Format,
            Threshold = declared.Threshold,
            Knee = declared.Knee,
            FilterRadius = declared.FilterRadius,
            Intensity = declared.Intensity,
            Modules = Modules,
            Device = Device,
            Descriptors = Descriptors,
            Samplers = Samplers
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
