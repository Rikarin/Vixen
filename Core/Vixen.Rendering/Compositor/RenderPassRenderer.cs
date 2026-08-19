// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Shaders;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     A render pass, and the renderers that draw into it.
/// </summary>
/// <remarks>
///     <para>
///         The node that resolves what a stage deliberately does not: a stage is <em>which</em>
///         objects and in what order, a pass is where they land. One stage feeds several passes and
///         one pass draws several stages, so the compositor is where the two meet.
///     </para>
///     <para>
///         <strong>It declares a pass rather than opening one.</strong> Targets are named, and the
///         render graph decides what they are: how big, whether two of them can share memory,
///         whether the contents ever have to reach memory at all, what barriers precede the pass, and
///         whether the pass is worth running. A node that called <c>BeginRenderPass</c> itself would
///         be answering all of those with "I do not know".
///     </para>
///     <para>
///         <strong><see cref="Reads" /> is not optional bookkeeping.</strong> A pass that samples the
///         shadow atlas must say so, because that read is the edge that orders the shadow pass before
///         it and puts a barrier between them — and, if nothing says so, the edge that keeps the
///         shadow pass from being culled for producing something nobody wanted.
///     </para>
///     <para>
///         There is still no separate "clear" renderer: clearing is a load action on an attachment.
///         Issuing it as its own operation costs a tile-based GPU a full extra pass writing a colour
///         the next pass overwrites.
///     </para>
/// </remarks>
public sealed class RenderPassRenderer : SceneRenderer {
    /// <summary>The names of its colour attachments, in the order the shader writes them.</summary>
    public IList<string> ColourTargets { get; } = [];

    /// <summary>The name of its depth attachment, or null for a pass with none.</summary>
    public string? DepthTarget { get; set; }

    /// <summary>The names of textures this pass samples.</summary>
    public IList<string> Reads { get; } = [];

    /// <summary>The names of buffers it reads.</summary>
    /// <remarks>
    ///     Separate from <see cref="Reads" /> because a buffer and a texture are different resources
    ///     in the graph, not because a pass thinks of them differently. A forward pass reading the
    ///     cluster list a compute pass wrote is the case this exists for, and it is the same edge:
    ///     the read orders the two and puts the barrier between them.
    /// </remarks>
    public IList<string> BufferReads { get; } = [];

    /// <summary>What to do with the colour attachments at the start of the pass.</summary>
    public LoadAction Load { get; set; } = LoadAction.Clear;

    /// <summary>
    ///     Which colour attachments keep what is in them, whatever <see cref="Load" /> says.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Because a pass's attachments are not all the same kind of thing.</b> A shading pass
    ///         that accumulates into a colour a sky pass already filled has to <em>load</em> that one,
    ///         and the normals it writes beside it have no earlier producer at all — loading those is
    ///         a read of memory nobody wrote, which the graph refuses by name and a driver would
    ///         silently hand last frame's contents.
    ///     </para>
    ///     <para>
    ///         Named rather than indexed, so a target added above one of these does not silently move
    ///         the exception onto its neighbour.
    ///     </para>
    /// </remarks>
    public ISet<string> Loaded { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>What to clear them to.</summary>
    public Color4 ClearColour { get; set; }

    /// <summary>What to do with depth at the start of the pass.</summary>
    public LoadAction DepthLoad { get; set; } = LoadAction.Clear;

    /// <summary>
    ///     What to clear depth to. Zero is <em>far</em> under the engine's reversed-Z convention.
    /// </summary>
    public float ClearDepth { get; set; }

    /// <summary>Whether the pass only tests depth, which lets a shader sample it at the same time.</summary>
    public bool ReadOnlyDepth { get; set; }

    /// <summary>How many samples its attachments have.</summary>
    public int SampleCount { get; set; } = 1;

    /// <summary>
    ///     Where each multisampled colour attachment's samples are resolved to, as the target's name
    ///     against the single-sampled resource that receives them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The end of MSAA's plumbing, and the one link that was missing.</b>
    ///         <see cref="SampleCount" /> already reached the pipeline through
    ///         <see cref="RenderOutput" /> and <c>RenderResourceAsset.SampleCount</c> already reached
    ///         the texture, and <c>ColourAttachment.ResolveView</c> is honoured by both backends — but
    ///         nothing between the two ever named a pair, so a frame could draw 4× correctly and end
    ///         with the samples in a texture no later pass can read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A multisampled texture is not sampleable and its resolve is not an attachment.</b>
    ///         The target keeps <c>ColourTarget</c> usage; the resolve is what carries <c>Sampled</c>
    ///         and what the rest of the frame reads by name. Pointing a post chain at the target
    ///         instead is a validation error, and forgetting this map entirely is no error at all.
    ///     </para>
    ///     <para>
    ///         By name rather than by index, on <see cref="Loaded" />'s terms: a target added above
    ///         one of these must not move somebody else's resolve onto its neighbour.
    ///     </para>
    /// </remarks>
    public IDictionary<string, string> ResolveTargets { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The viewport to set, or null for the whole target.</summary>
    public Viewport? Viewport { get; set; }

    /// <summary>The set the pass binds once, before anything under it draws.</summary>
    /// <remarks>
    ///     Where a frame's shared reads belong: the shadow atlas, the cluster list, the depth buffer a
    ///     post pass samples. Declaring the read was only ever half of it — the read orders the passes
    ///     and places the barrier, and this is what actually puts the resource in front of a shader.
    ///     <see cref="DescriptorBindings.Slot" /> should be <see cref="DescriptorSetSlot.PerView" />
    ///     or lower for a pass, so that the materials drawing into it rebind set 2 and 3 without
    ///     disturbing it.
    /// </remarks>
    public DescriptorBindings Descriptors { get; } = new() { Slot = DescriptorSetSlot.PerView };

    /// <summary>Where a described sampler comes from, for a binding that names one by value.</summary>
    /// <remarks>
    ///     Shared rather than owned, because a sampler is pure state and a device caps how many exist
    ///     — a chain of post passes each making its own reaches that cap on drivers that allow four
    ///     thousand.
    /// </remarks>
    public SamplerCache? Samplers { get; set; }

    /// <summary>
    ///     Frame textures this pass hands to the scene's set, as the shader's name for the binding
    ///     against the frame's name for the resource.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         How the shadow atlas reaches a shading pass. The atlas is a resource the graph owns and
    ///         may not have allocated yet, so its handle exists only inside the pass — which makes the
    ///         <em>consuming</em> pass the one entitled to publish it. A producer that published its
    ///         own output would be handing over a handle whose read barrier nobody declared.
    ///     </para>
    ///     <para>
    ///         <strong>Publishing implies reading.</strong> A name here does not also have to appear in
    ///         <see cref="Reads" />: saying a shader will sample it is saying the pass reads it, and
    ///         the two lists disagreeing is exactly the edge the graph would then be missing.
    ///     </para>
    /// </remarks>
    public IDictionary<string, string> SceneTextures { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Frame buffers it hands to the scene's set, on the same terms.</summary>
    /// <remarks>
    ///     The cluster list, which a compute pass wrote and this pass's fragments index into. Separate
    ///     from <see cref="SceneTextures" /> because the graph tracks the two resource kinds
    ///     separately, which is the same reason <see cref="BufferReads" /> is separate from
    ///     <see cref="Reads" />.
    /// </remarks>
    public IDictionary<string, string> SceneBuffers { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Which pass's names the published resources are qualified by.</summary>
    public string ShaderName { get; set; } = "ForwardPlus";

    /// <summary>How many colour attachments a pass writing the ambient split declares.</summary>
    /// <remarks>
    ///     Location 0 is direct light and emissive, 1 albedo with the material's occlusion in alpha,
    ///     2 the world normal with roughness in alpha, 3 the surface's <c>f0</c> — the order
    ///     <c>ForwardPlus.rvn</c> declares its output struct in, which is the order a document's
    ///     <c>colourTargets</c> has to name them in. Four is therefore not a threshold: it is the
    ///     count of that struct's members, and a pass with any other number is not a split pass.
    /// </remarks>
    public const int SplitTargetCount = 4;

    /// <summary>Whether a pass with this many colour attachments is shading into the split.</summary>
    /// <param name="targets">How many colour attachments the pass declares.</param>
    /// <remarks>
    ///     The mesh forward path's answer to the question <c>TerrainSceneRenderer.DetectMode</c> asks
    ///     as <c>frame.Has(Albedo) &amp;&amp; frame.Has(Normals) &amp;&amp; frame.Has(Specular)</c> and
    ///     <see cref="VisibilityBufferRenderer" /> asks of its three target names: is the split there
    ///     to be written. Asked of the attachment count rather than of resource names because a pass
    ///     is where the answer is actually enforced — a variant writing location 3 into a pass that
    ///     declares one attachment is a pipeline the device refuses, and one writing location 0 into
    ///     a pass that declares four leaves three planes at the clear.
    /// </remarks>
    public static bool SplitsOutputs(int targets) => targets >= SplitTargetCount;

    /// <summary>
    ///     The shader permutation that has to carry <see cref="SplitsOutputs" />, for the pass named.
    /// </summary>
    /// <param name="shaderName">The shading pass, as <see cref="ShaderName" /> names it.</param>
    /// <returns>The key, interned under <c>&lt;pass&gt;.SplitOutputs</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         <strong>The other half of a split frame, and the half nothing in the engine set.</strong>
    ///         A document that declares the four planes and an <c>!AmbientCombine</c> to read them has
    ///         said everything a document can say; whether <c>ForwardPlus</c> <em>writes</em> them is a
    ///         permutation, and a permutation belongs to the material layer that resolves variants.
    ///         Until this existed the two halves were joined by hand in two sample projects, and a
    ///         frame that declared the split without them rendered pixel-identically to one that never
    ///         split at all — the single-target variant writes location 0, the other three planes stay
    ///         at the clear, and <c>AmbientCombine.rvn</c> reads a zero-length normal as sky and hands
    ///         the direct target straight back.
    ///     </para>
    ///     <para>
    ///         Given to <c>MaterialRenderFeature.SetPermutation</c> by <c>CompositorBuilder</c>, on
    ///         <see cref="ShadowMapRenderer.CascadeCountKey" />'s exact terms and for its reason: the
    ///         value is a fact about the frame rather than about an object or a material, and it has
    ///         to be set before the first <c>Prepare</c>, because a variant is cached by its effect
    ///         key and a value published after one resolved leaves it compiled for the old one.
    ///     </para>
    /// </remarks>
    public static PermutationKey<bool> SplitOutputsKey(string shaderName) {
        ArgumentException.ThrowIfNullOrEmpty(shaderName);

        return ParameterKeys.NewPermutation(false, $"{shaderName}.SplitOutputs");
    }

    /// <summary>
    ///     The frame's set for whatever draws in this pass, or null for a pass that binds none.
    /// </summary>
    /// <remarks>
    ///     On the pass rather than on a child, which is the opposite of where the per-view block sits
    ///     and follows from what the two sets are: a pass draws several views and each brings its own
    ///     set 1, while set 0 belongs to <em>the pass</em> — its shadow atlas, its cluster list, its
    ///     environment. Handed to the context rather than bound here for the usual reason: no set can
    ///     precede the first pipeline, so a feature is what decides when.
    /// </remarks>
    public SceneConstants? SceneConstants { get; set; }

    /// <summary>What draws into this pass.</summary>
    public IList<SceneRenderer> Children { get; } = [];

    /// <inheritdoc />
    public override IReadOnlyList<SceneRenderer> Nested => [.. Children];

    /// <inheritdoc />
    protected internal override void Collect(GraphicsCompositor compositor) {
        foreach (var child in Children) {
            if (child.Enabled) {
                child.Collect(compositor);
            }
        }
    }

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        var colours = new GraphTexture[ColourTargets.Count];
        var formats = new PixelFormat[ColourTargets.Count];
        var resolves = new GraphTexture[ColourTargets.Count];

        for (var i = 0; i < ColourTargets.Count; i++) {
            colours[i] = frame.Texture(ToString(), ColourTargets[i]);
            formats[i] = frame.FormatOf(ToString(), ColourTargets[i]);

            resolves[i] = ResolveTargets.TryGetValue(ColourTargets[i], out var into)
                ? frame.Texture(ToString(), into)
                : GraphTexture.None;
        }

        var depth = DepthTarget is { Length: > 0 } name ? frame.Texture(ToString(), name) : GraphTexture.None;
        var depthFormat = depth.IsValid ? frame.FormatOf(ToString(), DepthTarget!) : PixelFormat.Undefined;
        var output = new RenderOutput(formats, depthFormat, SampleCount);
        // Published resources are read resources, resolved once with the declared ones. A name in
        // only one of the two lists is the edge the graph would be missing, so there is one list.
        var textures = Reads.Concat(SceneTextures.Values)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(read => read, read => frame.Texture(ToString(), read), StringComparer.Ordinal);

        var buffers = BufferReads.Concat(SceneBuffers.Values)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(read => read, read => frame.Buffer(ToString(), read), StringComparer.Ordinal);

        var bound = Descriptors.Resolve(ToString(), textures, buffers, samplers: Samplers);
        var sampled = textures.Values.ToArray();
        var consumed = buffers.Values.ToArray();

        var publishedTextures = SceneTextures
            .Select(entry => (Key: ParameterKeys.New<TextureViewHandle>($"{ShaderName}.{entry.Key}"), Texture: textures[entry.Value]))
            .ToArray();

        var publishedBuffers = SceneBuffers
            .Select(entry => (Key: ParameterKeys.New<BufferHandle>($"{ShaderName}.{entry.Key}"), Buffer: buffers[entry.Value]))
            .ToArray();

        frame.Graph.AddPass(
            ToString(),
            pass => {
                for (var i = 0; i < colours.Length; i++) {
                    pass.ColourAttachment(
                        colours[i],
                        Loaded.Contains(ColourTargets[i]) ? LoadAction.Load : Load,
                        ClearColour,
                        resolve: resolves[i]
                    );
                }

                if (depth.IsValid) {
                    pass.DepthAttachment(depth, DepthLoad, ClearDepth, readOnly: ReadOnlyDepth);
                }

                foreach (var read in sampled) {
                    pass.Reads(read);
                }

                foreach (var read in consumed) {
                    pass.Reads(read);
                }

                pass.Execute(
                    graphContext => {
                        var context = frame.Context(graphContext.CommandList);
                        var previous = context.Output;
                        var previousScene = context.SceneConstants;

                        context.Output = output;
                        context.SceneConstants = SceneConstants;

                        bound?.Bind(graphContext);

                        // Here rather than at declaration time, because a virtual resource has no
                        // handle until the graph has placed it — and the set that reads them is bound
                        // by the mesh feature below, after the first pipeline.
                        if (SceneConstants is { } scene) {
                            foreach (var (key, texture) in publishedTextures) {
                                scene.Parameters.Set(key, graphContext.View(texture));
                            }

                            foreach (var (key, buffer) in publishedBuffers) {
                                scene.Parameters.Set(key, graphContext.Buffer(buffer));
                            }
                        }

                        if (Viewport is { } viewport) {
                            graphContext.CommandList.SetViewport(viewport);

                            graphContext.CommandList.SetScissor(
                                new((int)viewport.X, (int)viewport.Y, (int)viewport.Width, (int)viewport.Height)
                            );
                        }

                        foreach (var child in Children) {
                            if (child.Enabled) {
                                child.Record(compositor, context);
                            }
                        }

                        context.Output = previous;
                        context.SceneConstants = previousScene;
                    }
                );
            }
        );
    }
}
