// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Tests;

/// <summary>The reduction chain, and the buffer that carries its answer into the next frame.</summary>
/// <remarks>
///     <para>
///         <b>The claims worth making about a pass whose output is a single number.</b> There is no
///         picture to compare and nothing about the frame that would look wrong if it were subtly
///         off — the value goes into a multiply and a wrong exposure looks exactly like a
///         differently-lit scene. So what is asserted is the <i>shape</i>: the chain halves all the
///         way down, the first step is the one that takes the log, the adaptation is one group, and
///         the buffer is an import rather than a declared resource.
///     </para>
///     <para>
///         ⚠ <b>That last one is the assertion that matters most and would be the easiest to lose.</b>
///         A declared buffer lives for one frame. Adaptation eases toward a target from where it
///         already is, so a buffer the graph re-declared each frame would ease from zero every time —
///         an exposure that never converges. Nothing about that is visible in a single frame's
///         command list, which is why it is asserted against the import table directly.
///     </para>
/// </remarks>
public class AutoExposureTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true, FramesInFlight = 2 });
    readonly EffectSystem effects = new();
    readonly DescriptorAllocator allocator;
    readonly SamplerCache samplers;
    readonly EffectPipelineDescriber describer;
    readonly ComputePipelineCache pipelines;
    readonly Dictionary<string, DescriptorSetLayoutHandle> layouts = [];

    public AutoExposureTests() {
        allocator = new(device);
        samplers = new(device);
        describer = new(device);
        pipelines = new(device);

        // Set 2 in the shape the shader's reflection reports, with the indices taken from the
        // generated constants for the reason the chain takes them from there: a binding index is
        // declaration order, so a texture added above another renumbers everything below it.
        Declare(
            AutoExposureKeys.ShaderName,
            new(AutoExposureKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Compute),
            new(AutoExposureKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Compute),
            new(AutoExposureKeys.SourceSamplerBinding, DescriptorKind.Sampler, ShaderStage.Compute),
            new(AutoExposureKeys.ExposureBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
            new(AutoExposureKeys.HistogramBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
            new(AutoExposureKeys.TargetBinding, DescriptorKind.StorageTexture, ShaderStage.Compute),
            new(AutoExposureKeys.AverageBinding, DescriptorKind.StorageTexture, ShaderStage.Compute)
        );

        effects.AddProvider(new AlwaysCompiles(layouts));
    }

    void Declare(string shader, params DescriptorBinding[] bindings) =>
        layouts[shader] = device.CreateDescriptorSetLayout(new(DescriptorSetSlot.PerMaterial, bindings, shader));

    /// <inheritdoc />
    public void Dispose() {
        samplers.Dispose();
        allocator.Dispose();
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A chain from 512 halves nine times, and the adaptation is one more pass.</summary>
    /// <remarks>
    ///     512 → 256 → … → 1 is nine reductions. The count is asserted rather than the sizes alone
    ///     because a chain that stopped one short would leave <c>Adapt</c> reading a 2×2 texture and
    ///     taking one of its four texels as the whole frame's brightness — which is not an error, and
    ///     is a scene whose exposure is decided by a quarter of it.
    /// </remarks>
    [Fact]
    public void The_chain_halves_to_one_texel_and_then_adapts() {
        using var h = Build();
        Frame(h);

        Assert.Equal(10, h.Exposure.PassCount);
        Assert.Equal(10, device.Recorder!.CountOf(RecordedCommandKind.Dispatch));
    }

    /// <summary>Only the first reduction takes the log, and only the last pass adapts.</summary>
    /// <remarks>
    ///     ⚠ Averaging luminance rather than its logarithm lets one specular highlight drag the whole
    ///     frame's exposure down — the arithmetic mean of a distribution with a long bright tail sits
    ///     far above its bulk. The log happens once and every later step averages values that are
    ///     already logarithmic, so a second step that took it again would be averaging the log of a
    ///     log.
    /// </remarks>
    [Fact]
    public void The_first_step_takes_the_log_and_the_last_one_adapts() {
        using var h = Build();
        Frame(h);

        Assert.True(h.Exposure.Steps[0].Parameters.Get(AutoExposureKeys.FirstStep));
        Assert.Equal(0, h.Exposure.Steps[0].Parameters.Get(AutoExposureKeys.Mode));

        for (var step = 1; step < h.Exposure.PassCount - 1; step++) {
            Assert.False(h.Exposure.Steps[step].Parameters.Get(AutoExposureKeys.FirstStep));
            Assert.Equal(0, h.Exposure.Steps[step].Parameters.Get(AutoExposureKeys.Mode));
        }

        var adapt = h.Exposure.Steps[h.Exposure.PassCount - 1];

        Assert.Equal(1, adapt.Parameters.Get(AutoExposureKeys.Mode));

        // One group, because one invocation does the adaptation and the shader sends the other
        // sixty-three home. A dispatch of more would be that many invocations reading and writing one
        // buffer element with no ordering between them.
        Assert.Equal(new Int3(1, 1, 1), adapt.Groups);
    }

    /// <summary>
    ///     ⚠ <b>The exposure buffer is an import, which is what lets it survive the frame.</b>
    /// </summary>
    [Fact]
    public void The_exposure_buffer_is_the_same_one_every_frame() {
        using var h = Build();
        Frame(h);

        var first = h.Exposure.Exposure;

        Assert.True(first.IsValid);

        // ⚠ The same handle after three more frames, which is the whole claim. The buffer is imported
        // into each frame rather than declared in it, so the graph treats the memory as somebody
        // else's and does not alias next frame's exposure over this one's — and the adaptation has
        // something to ease *from*. A declared buffer would come back a different handle, or the same
        // handle holding whatever the aliaser left, and either way the exposure would never converge.
        Frame(h);
        Frame(h);
        Frame(h);

        Assert.Equal(first, h.Exposure.Exposure);

        // And it is not something the compositor declared, which is the other half of the same point.
        Assert.DoesNotContain(h.Compositor.BufferResources, declared => declared.Name == h.Exposure.ExposureResource);
    }

    /// <summary>A smaller start is a shorter chain, and still ends at one texel.</summary>
    [Fact]
    public void A_smaller_start_is_a_shorter_chain() {
        using var h = Build(start: 16);
        Frame(h);

        // 16 → 8 → 4 → 2 → 1 is four reductions, and the adaptation.
        Assert.Equal(5, h.Exposure.PassCount);
    }

    /// <summary>
    ///     ⚠ The histogram is three dispatches whatever the frame's size is, and the chain is not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two meters answer different questions, so they are two shapes rather than one with
    ///         a flag. A halving chain costs a dispatch per level and measures a geometric mean; the
    ///         histogram costs a clear, a build and a resolve however large the frame is, and measures
    ///         a percentile range — which is the number that can ignore a bright window.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The clear is a dispatch of its own and cannot be folded into the build.</b> A
    ///         build invocation cannot clear "its" bin, because a bin belongs to a luminance rather
    ///         than to a pixel and every invocation in the dispatch is racing every other one for all
    ///         of them. Two dispatches would be a histogram accumulating on top of last frame's.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_histogram_is_three_dispatches_and_the_chain_is_a_dispatch_per_level() {
        using var metered = Build(start: 256, histogram: true);
        Frame(metered);

        Assert.Equal(3, metered.Exposure.PassCount);
        Assert.Equal("AutoExposure.Step0", metered.Exposure.Steps[0].Name);

        using var chained = Build(start: 256);
        Frame(chained);

        // 256 → 128 → … → 1 is eight reductions, and the adaptation.
        Assert.Equal(9, chained.Exposure.PassCount);
    }

    /// <summary>
    ///     ⚠ Both meters write one buffer, and it is the same buffer across frames either way.
    /// </summary>
    /// <remarks>
    ///     The histogram's own bins are rebuilt every frame and could be a declared resource; the
    ///     exposure cannot, because the adaptation eases <em>toward</em> a target and a value that
    ///     started at zero every frame would never converge. Switching meters must not change that.
    /// </remarks>
    [Fact]
    public void The_histogram_eases_the_same_buffer_the_chain_does() {
        using var h = Build(histogram: true);

        Frame(h);
        var first = h.Exposure.Exposure;

        Frame(h);
        Frame(h);

        Assert.True(first.IsValid);
        Assert.Equal(first, h.Exposure.Exposure);
        Assert.True(h.Exposure.Histogram.IsValid);
        Assert.NotEqual(first, h.Exposure.Histogram);
    }

    /// <summary>
    ///     The tonemapper reads the buffer where one is named, and the scalar where it is not.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both directions, because the permutation is what makes the change additive.</b> With
    ///     no buffer named, the variant is the one every consumer compiled to before auto-exposure
    ///     existed — no binding declared and none bound. Asserting only the measured case would let a
    ///     regression that always declared the binding pass, and that regression is an incomplete
    ///     descriptor set in every frame that does not measure.
    /// </remarks>
    [Fact]
    public void The_tonemapper_reads_the_buffer_only_when_one_is_named() {
        using var measured = Build(measured: true);
        Frame(measured);

        Assert.True(measured.Tonemap.Pass.Parameters.Get(TonemapKeys.UseExposureBuffer));

        using var authored = Build(measured: false);
        Frame(authored);

        Assert.False(authored.Tonemap.Pass.Parameters.Get(TonemapKeys.UseExposureBuffer));
    }

    /// <summary>
    ///     ⚠ A look's meter clamps arrive as EVs and land on the meter as exposures — inverted,
    ///     because the two units run opposite ways.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The conversion this pins is the one that reads plausibly wrong either way round: a
    ///         <em>high</em> EV is a bright scene and therefore a <em>low</em> exposure, so the
    ///         look's <c>meterMaximumEv</c> must become <c>MinimumExposure</c> and its
    ///         <c>meterMinimumEv</c> the maximum. Swapped, the meter's floor and ceiling cross and
    ///         every frame clamps to one of them — a picture, not an error.
    ///     </para>
    ///     <para>
    ///         Delivered through <c>GraphicsCompositor.Apply</c> rather than straight onto the node,
    ///         because the claim includes the walk: the meter is a compute chain, not a
    ///         <c>PostEffectRenderer</c>, and it must be reached by the same traversal that reaches
    ///         everything else.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_looks_meter_clamps_reach_the_meter_converted_and_leave_with_the_look() {
        using var h = Build();

        var overlay = PostProcessOverlay.None;
        overlay.Add(new() { MeterMinimumEv = 5f, MeterMaximumEv = 14f }, 1f);

        // Two nodes take the overlay: the meter and the tonemap behind it.
        Assert.Equal(2, h.Compositor.Apply(overlay));
        Frame(h);

        var adapt = h.Exposure.Steps[h.Exposure.PassCount - 1];

        // Six places rather than nine: even at full weight the blend is a lerp, and a lerp's
        // last-bit rounding survives the EV round trip.
        Assert.Equal(
            Photometry.ExposureFromEv100(14f),
            adapt.Parameters.Get(AutoExposureKeys.MinimumExposure),
            6
        );

        Assert.Equal(
            Photometry.ExposureFromEv100(5f),
            adapt.Parameters.Get(AutoExposureKeys.MaximumExposure),
            6
        );

        // Unloading the look restores the authored clamps — recorded, never written into.
        h.Compositor.Apply(PostProcessOverlay.None);
        Frame(h);

        adapt = h.Exposure.Steps[h.Exposure.PassCount - 1];

        Assert.Equal(0.03f, adapt.Parameters.Get(AutoExposureKeys.MinimumExposure), 5);
        Assert.Equal(8f, adapt.Parameters.Get(AutoExposureKeys.MaximumExposure), 5);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }

        public required GraphicsCompositor Compositor { get; init; }

        public required RenderGraph Graph { get; init; }

        public required AutoExposureRenderer Exposure { get; init; }

        public required TonemapRenderer Tonemap { get; init; }

        public void Dispose() {
            Exposure.Dispose();
            Tonemap.Dispose();
        }
    }

    Harness Build(int start = 512, bool measured = true, bool histogram = false) {
        var size = new Int2(320, 180);
        var system = new RenderSystem();

        var exposure = new AutoExposureRenderer {
            Name = "AutoExposure",
            Source = "SceneColour",
            Samplers = samplers,
            Pipelines = pipelines,
            Allocator = allocator,
            Device = device,
            StartSize = start,
            UseHistogram = histogram
        };

        var tonemap = new TonemapRenderer {
            Name = "Tonemap",
            Source = "SceneColour",
            Output = "Display",
            ExposureBuffer = measured ? exposure.ExposureResource : "",
            Modules = describer,
            Samplers = samplers,
            Allocator = allocator,
            Device = device
        };

        var compositor = new GraphicsCompositor(system) { FrameSize = size };

        compositor.Imports["SceneColour"] = Colour("SceneColour", size);
        compositor.Imports["Display"] = Colour("Display", size);
        compositor.Game = new SceneRendererSequence { Children = { exposure, tonemap } };

        return new() {
            System = system,
            Compositor = compositor,
            Graph = new(device),
            Exposure = exposure,
            Tonemap = tonemap
        };
    }

    ImportedTexture Colour(string name, Int2 size) {
        var description = new TextureDescription(
            PixelFormat.Rgba16Float,
            size.X,
            size.Y,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: name
        );

        var texture = device.CreateTexture(description);

        return new(texture, device.CreateTextureView(texture), description);
    }

    /// <summary>An effect for whatever is asked for, with the layout its shader declared.</summary>
    /// <remarks>
    ///     Copied rather than shared with <c>BloomTests</c>, which is what <c>PostEffectTests</c> does
    ///     to it as well: it is five lines of stub, and a fixture reaching into another fixture makes
    ///     one test class's private shape part of another's contract.
    /// </remarks>
    static Effect Compiled(EffectKey key, DescriptorSetLayoutHandle layout) =>
        new() {
            Key = key,
            Stages = [new(ShaderStage.Compute, [1, 2, 3, 4], "main")],
            SetLayouts = [default, default, layout, default],
            ConstantBufferSize = 32
        };

    sealed class AlwaysCompiles(Dictionary<string, DescriptorSetLayoutHandle> layouts) : IEffectProvider {
        public Effect? TryGet(EffectKey key) =>
            Compiled(key, layouts.TryGetValue(key.ShaderName, out var layout) ? layout : default);
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
}
