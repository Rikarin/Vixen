// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>
///     SMAA 1x: the antialiasing that finds the whole edge before it blends anything.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="FxaaRenderer" /> reads one pixel's neighbourhood, guesses which way the edge
///         runs and blends along the guess — which is why it softens an albedo map's detail as
///         readily as a silhouette. This walks the edge instead: a run of edge texels bounded by two
///         crossing edges is a silhouette whose sub-pixel position is known, and the coverage that
///         follows is looked up rather than estimated. The cost is three passes and two intermediate
///         targets where FXAA is one pass and none.
///     </para>
///     <para>
///         <strong>Three passes, because each needs the whole of the one before it.</strong> The
///         weight pass walks up to sixteen texels along an edge, so it cannot run until every edge in
///         the frame has been found; the blend pass reads its neighbours' weights, so it cannot run
///         until every weight has been written. That is the same shape <see cref="BloomRenderer" />
///         has and the reason both are <see cref="SceneRenderer" />s rather than
///         <see cref="PostEffectRenderer" />s, which is one pass by construction.
///     </para>
///     <para>
///         ⚠ <strong>The coverage table is generated, imported and uploaded once.</strong> The
///         reference distribution ships it as a 179 KB byte array in a header; it is an analytic
///         function, so <see cref="SmaaAreaTexture" /> computes it instead. It is imported rather
///         than declared for <see cref="TemporalAntialiasingRenderer" />'s reason — a transient is
///         dead at the end of the frame, and a table regenerated every frame is a table uploaded
///         every frame — and the copy is a transfer pass of the node's own, because a copy into a
///         texture cannot be recorded inside a render pass and everything a graph pass with
///         attachments executes is inside one.
///     </para>
///     <para>
///         <strong>Every pass binds every texture, whatever its mode.</strong> A descriptor set is
///         written whole or not at all: a set that fell one binding short refuses every draw in the
///         pass while the draw count still reports fine. The edge pass has no weights to read and the
///         blend pass has no table, so both bind <see cref="Source" /> in those slots — already in
///         the sampled layout, which makes it the cheapest valid stand-in, and never the resource the
///         pass writes, which would be a cycle in the graph.
///     </para>
///     <para>
///         ⚠ <strong>Diagonal pattern detection is not implemented.</strong> See
///         <c>Smaa.rvn</c>'s header: silhouettes near 45° fall through to the orthogonal path, which
///         is the reference's own <c>SMAA_DISABLE_DIAG_DETECTION</c> build rather than an
///         approximation of one.
///     </para>
/// </remarks>
public sealed class SmaaRenderer : SceneRenderer, IDisposable {
    readonly FullScreenRenderer[] passes;

    TextureHandle areaTexture;
    TextureViewHandle areaView;
    BufferHandle areaStaging;
    TextureDescription areaDescription;
    bool uploaded;
    bool disposed;

    /// <summary>Creates the three passes.</summary>
    public SmaaRenderer() =>
        passes = [Create("Edges"), Create("Weights"), Create("Blend")];

    /// <summary>The texture it antialiases.</summary>
    public required string Source { get; init; }

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "PostFx";

    /// <summary>The format of the target it declares.</summary>
    /// <remarks>
    ///     <see cref="PostEffectRenderer.Format" />'s reason: a post chain runs in linear HDR, and an
    ///     effect writing an eight-bit target part way through clips every highlight the tonemap was
    ///     going to bring back. A frame that puts this after the tonemap — which is where
    ///     <c>StandardFrame</c> puts it, for the reason FXAA is there — sets it to the display format
    ///     instead.
    /// </remarks>
    public PixelFormat Format { get; set; } = PixelFormat.Rgba16Float;

    /// <summary>The relative local contrast a boundary needs before it is an edge, 0 to 1.</summary>
    /// <remarks>
    ///     Relative, which is what makes one number right on both sides of the tonemap. A tenth is
    ///     the reference's default and means "a tenth of the brightest thing nearby"; a twentieth
    ///     finds more edges and spends more of the frame's time in the weight pass's walk.
    /// </remarks>
    public float EdgeThreshold { get; set; } = 0.1f;

    /// <summary>How much steeper a nearby contrast may be before this edge is discarded.</summary>
    /// <remarks>
    ///     The local contrast adaptation, and what keeps the filter off a soft gradient: a ramp has a
    ///     small difference everywhere and a silhouette has one that dominates its neighbours.
    /// </remarks>
    public float ContrastAdaptation { get; set; } = 2f;

    /// <summary>The luminance below which the frame is treated as flat, in its own units.</summary>
    /// <remarks>
    ///     The floor under the relative threshold. Dividing by the neighbourhood's brightest is
    ///     scale-invariant, which is the point, and in a black neighbourhood it is also a divider
    ///     with unbounded gain — so a value a millionth of a nit would become a full-contrast edge.
    /// </remarks>
    public float LumaFloor { get; set; } = 0.0001f;

    /// <summary>The format the edge mask and the weights are kept in.</summary>
    /// <remarks>
    ///     Eight bits per channel, which is all either holds: the mask is two booleans and a weight
    ///     is a coverage that never exceeds a half. The reference keeps the mask in two channels
    ///     rather than four; four here because a two-channel colour target is one more format for a
    ///     backend to support and the saving is a hundred kilobytes at 1080p.
    /// </remarks>
    public PixelFormat MaskFormat { get; set; } = PixelFormat.Rgba8UNorm;

    /// <summary>Where shader modules come from.</summary>
    public EffectPipelineDescriber? Modules { get; set; }

    /// <summary>Where the passes' descriptor sets come from.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>Where samplers come from.</summary>
    public SamplerCache? Samplers { get; set; }

    /// <summary>The device its pipelines, its table and its uniform blocks are created on.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>The three passes, in the order they run.</summary>
    public IReadOnlyList<FullScreenRenderer> Passes => passes;

    /// <summary>Whether the coverage table has been copied to the device.</summary>
    /// <remarks>
    ///     ⚠ <b>Set by the transfer pass's body, not by the build that declared it.</b> A build that
    ///     is never executed uploads nothing, and a node that had already marked itself uploaded
    ///     would then import a table full of whatever that memory held — which reads as an
    ///     antialiasing pass that blends pixels in the wrong direction rather than as a missing copy.
    /// </remarks>
    public bool Uploaded => uploaded;

    /// <summary>What the edge mask is published under.</summary>
    public string EdgesName => $"{this}.Edges";

    /// <summary>What the blending weights are published under.</summary>
    public string WeightsName => $"{this}.Weights";

    /// <summary>What the coverage table is published under.</summary>
    public string AreaName => $"{this}.Area";

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The three passes are not in <see cref="SceneRenderer.Nested" />, so their answer is
    ///     carried out by hand</b> — <see cref="BloomRenderer" />'s arrangement and its reason. A
    ///     chain that declines leaves <see cref="Output" /> holding whatever the graph last aliased
    ///     into it.
    /// </remarks>
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        Degrade(Declare(compositor, frame));
    }

    string? Declare(GraphicsCompositor compositor, CompositorFrame frame) {
        if (Modules is null) {
            return "no Modules, so no pass of the SMAA chain was declared and Output holds whatever "
                + "the graph last aliased into it";
        }

        if (Samplers is null) {
            return "no Samplers, so no pass of the SMAA chain was declared and Output holds whatever "
                + "the graph last aliased into it";
        }

        if ((Device ?? frame.Device) is not { } device) {
            return "no Device, so the coverage table could not be created and no pass of the SMAA "
                + "chain was declared — Output holds whatever the graph last aliased into it";
        }

        var size = frame.Size;

        Declare(frame, EdgesName, size, MaskFormat);
        Declare(frame, WeightsName, size, MaskFormat);
        Declare(frame, Output, size, Format);
        Table(frame, device);

        // The edge pass has nothing to read on the weight and table slots and the blend pass nothing
        // on the edge and table ones. Both bind the source there rather than leaving a hole in the
        // set — and never their own target, which would be a cycle.
        Configure(0, EdgeMode, size, Source, Source, Source, EdgesName);
        Configure(1, WeightMode, size, Source, EdgesName, Source, WeightsName);
        Configure(2, BlendMode, size, Source, Source, WeightsName, Output);

        string? reason = null;

        foreach (var pass in passes) {
            BuildChild(pass, compositor, frame);

            // The first that declined, not the last: a chain fails at its head, and reporting the
            // blend would name the pass furthest from the cause.
            reason ??= pass.Degraded;
        }

        return reason;
    }

    /// <summary>Sets up one of the three passes.</summary>
    void Configure(int index, int mode, Int2 size, string source, string edges, string weights, string target) {
        var pass = passes[index];

        pass.Modules = Modules;
        pass.Device = Device;
        pass.Descriptors.Allocator = Descriptors;

        pass.Parameters.Set(SmaaKeys.Mode, mode);
        pass.Parameters.Set(SmaaKeys.TexelSize, new Vector2(1f / Math.Max(size.X, 1), 1f / Math.Max(size.Y, 1)));
        pass.Parameters.Set(SmaaKeys.EdgeThreshold, EdgeThreshold);
        pass.Parameters.Set(SmaaKeys.ContrastAdaptation, ContrastAdaptation);
        pass.Parameters.Set(SmaaKeys.LumaFloor, LumaFloor);

        // The table's geometry, from the generator rather than from a number written twice. A shader
        // that disagreed with it by one texel would read the neighbouring pattern's coverage, which
        // is a blend in the right place of the wrong amount.
        pass.Parameters.Set(SmaaKeys.AreaTexelSize, new Vector2(1f / SmaaAreaTexture.Side));
        pass.Parameters.Set(SmaaKeys.AreaMaxDistance, SmaaAreaTexture.MaxDistance);

        pass.ColourTargets.Clear();
        pass.ColourTargets.Add(target);

        pass.Reads.Clear();
        pass.Descriptors.Bindings.Clear();

        Read(pass, SmaaKeys.SourceBinding, source);
        Read(pass, SmaaKeys.EdgesBinding, edges);
        Read(pass, SmaaKeys.WeightsBinding, weights);
        Read(pass, SmaaKeys.AreaBinding, AreaName);

        // Linear for the two things read between texels — the blend's offset tap, and the table,
        // whose distance axis is sampled at a square root. Point for everything else: every
        // neighbourhood read in the weight pass is at an exact texel, and a linear tap there averages
        // two texels of a mask into a value that means nothing.
        pass.Descriptors.Bindings.Add(
            new() {
                Binding = SmaaKeys.LinearSamplerBinding,
                Kind = DescriptorKind.Sampler,
                Sampler = Samplers!.LinearClamp
            }
        );

        pass.Descriptors.Bindings.Add(
            new() {
                Binding = SmaaKeys.PointSamplerBinding,
                Kind = DescriptorKind.Sampler,
                Sampler = Samplers!.PointClamp
            }
        );
    }

    static void Read(FullScreenRenderer pass, uint binding, string resource) {
        // Recorded as a read *and* bound, because the two cannot be separated: the binding is what
        // the shader samples it through, and the read is what orders this pass after whatever wrote
        // the texture. A resource named twice is added twice to Reads, which the graph folds.
        pass.Reads.Add(resource);
        pass.Descriptors.Bindings.Add(
            new() { Binding = binding, Kind = DescriptorKind.SampledTexture, Resource = resource }
        );
    }

    static void Declare(CompositorFrame frame, string name, Int2 size, PixelFormat format) {
        if (frame.Has(name)) {
            return;
        }

        frame.Add(
            name,
            frame.Graph.CreateTexture(
                new(format, size.X, size.Y, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: name)
            ),
            format
        );
    }

    /// <summary>Imports the coverage table, and declares the copy that fills it the first time.</summary>
    void Table(CompositorFrame frame, IGraphicsDevice device) {
        Allocate(device);

        var table = frame.Graph.ImportTexture(
            areaTexture,
            areaView,
            areaDescription,
            uploaded ? ResourceState.ShaderRead : ResourceState.Undefined,
            ResourceState.ShaderRead
        );

        frame.Add(AreaName, table, areaDescription.Format);

        if (uploaded) {
            return;
        }

        var staging = areaStaging;

        frame.Graph.AddPass(
            $"{this}.Table",
            builder => {
                // No attachments, so the graph runs the body outside a render pass — which is the
                // only place a copy into a texture may be recorded.
                builder.Kind = PassKind.Transfer;
                builder.Writes(table, ResourceState.CopyDestination);

                // Nothing in *this* frame need read the table for the copy to matter: every frame
                // after this one reads it, and they are not this graph's to see.
                builder.SideEffect();

                builder.Execute(
                    context => {
                        context.CommandList.CopyBufferToTexture(
                            staging,
                            0,
                            new(context.Texture(table)),
                            new(SmaaAreaTexture.Side, SmaaAreaTexture.Side, 1)
                        );

                        uploaded = true;
                    }
                );
            }
        );
    }

    /// <summary>Creates the table and its staging buffer, once.</summary>
    void Allocate(IGraphicsDevice device) {
        if (areaTexture.IsValid) {
            return;
        }

        areaDescription = new(
            PixelFormat.Rg8UNorm,
            SmaaAreaTexture.Side,
            SmaaAreaTexture.Side,
            TextureUsage.Sampled | TextureUsage.CopyDestination,
            Name: AreaName
        );

        areaTexture = device.CreateTexture(areaDescription);
        areaView = device.CreateTextureView(areaTexture);

        var texels = SmaaAreaTexture.Generate();

        areaStaging = device.CreateBuffer(
            new(texels.Length, BufferUsage.CopySource, MemoryAccess.HostUpload, $"{AreaName} staging")
        );

        device.Write(areaStaging, 0, texels);
        uploaded = false;
    }

    static FullScreenRenderer Create(string label) =>
        new() {
            Name = $"Smaa.{label}",
            ShaderName = SmaaKeys.ShaderName,
            PermutationKeys = SmaaKeys.UsedPermutationKeys,
            ConstantBinding = SmaaKeys.ConstantBufferBinding
        };

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var pass in passes) {
            pass.Dispose();
        }

        if (Device is not { } device) {
            return;
        }

        if (areaView.IsValid) {
            device.Destroy(areaView);
        }

        if (areaTexture.IsValid) {
            device.Destroy(areaTexture);
        }

        if (areaStaging.IsValid) {
            device.Destroy(areaStaging);
        }

        areaView = default;
        areaTexture = default;
        areaStaging = default;
    }

    /// <summary>Find the luminance edges.</summary>
    const int EdgeMode = 0;

    /// <summary>Walk each edge, and turn what is at its ends into a coverage.</summary>
    const int WeightMode = 1;

    /// <summary>One bilinear tap per pixel, placed by the weight.</summary>
    const int BlendMode = 2;
}
