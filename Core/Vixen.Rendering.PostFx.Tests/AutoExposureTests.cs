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

    /// <summary>Neither meter leaves the graph anything to complain about.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>VX2101, twice, on every launch of sample 13.</b> The histogram's three dispatches
    ///         each declared a write of the image its <c>target</c> and <c>average</c> bindings point
    ///         at, and not one of the three modes stores a texel into it — so the graph saw three
    ///         producers with no reader between them and said so, correctly, about a declaration that
    ///         was wrong. Nothing was being discarded: the meter's data path is the histogram buffer,
    ///         which is declared read and written by the passes that do each.
    ///     </para>
    ///     <para>
    ///         Asserted over the whole warning list rather than by matching the code, because the next
    ///         wrong declaration will not be this one. Both meters, because the reduction chain is
    ///         where a genuine discarded write would be — nine steps handing an image along, and
    ///         swapping two of them is a level that goes nowhere.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Neither_meter_leaves_the_graph_a_warning() {
        using var metered = Build(histogram: true);

        Frame(metered);
        Frame(metered);

        Assert.Empty(metered.Graph.Warnings);

        using var chained = Build();

        Frame(chained);
        Frame(chained);

        Assert.Empty(chained.Graph.Warnings);
    }

    /// <summary>A step rebuilt every frame declares the same things, not one more set of them.</summary>
    /// <remarks>
    ///     ⚠ The chain's nodes are kept and reconfigured rather than rebuilt, and its buffer reads
    ///     were appended to a list nothing emptied — so the tenth minute of a session declared
    ///     thousands of copies of the same two names. Invisible in the picture, because a duplicate
    ///     read asks for a state the resource is already in, and an unbounded list on a hot path
    ///     either way.
    /// </remarks>
    [Fact]
    public void A_rebuilt_step_declares_the_same_resources_it_did_last_frame() {
        using var h = Build(start: 16);

        Frame(h);

        var declared = h.Exposure.Steps
            .Take(h.Exposure.PassCount)
            .Select(step => (step.Reads.Count, step.Writes.Count, step.Bound.Count, step.BufferReads.Count, step.BufferWrites.Count))
            .ToArray();

        Frame(h);
        Frame(h);
        Frame(h);

        Assert.Equal(
            declared,
            h.Exposure.Steps
                .Take(h.Exposure.PassCount)
                .Select(step => (step.Reads.Count, step.Writes.Count, step.Bound.Count, step.BufferReads.Count, step.BufferWrites.Count))
                .ToArray()
        );
    }

    /// <summary>
    ///     ⚠ The first frame lands on what it metered; every frame after it eases at the authored rate.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>What a launch looked like without this.</b> A fresh device-local buffer holds no
    ///         exposure — the claim that one was seeded to <c>1</c> was in this class's remarks and in
    ///         none of its code — so the adaptation eased from zero toward its target and took about
    ///         five time constants to arrive. At sample 13's <c>darkenRate</c> of 0.6 that is eight and
    ///         a half seconds of a black screen slowly lighting up, which reads as a broken renderer
    ///         rather than as an eye adjusting.
    ///     </para>
    ///     <para>
    ///         The blend is <c>1 - exp(-dt·rate)</c> and saturates, so the fix is the elapsed time and
    ///         not a second code path through the adaptation: one frame told the scene has been there
    ///         for hours arrives at the target exactly, and the rates, the clamps and the value it
    ///         converges on are all untouched.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_frame_that_has_no_previous_exposure_lands_on_its_target() {
        using var h = Build(histogram: true);

        h.Exposure.DeltaTime = 1f / 60f;
        Frame(h);

        var first = h.Exposure.Steps[h.Exposure.PassCount - 1].Parameters.Get(AutoExposureKeys.DeltaTime);

        // Enough that the blend is one to a float at any rate anybody would author — a tenth of an
        // e-fold per second still arrives.
        Assert.True(1f - MathF.Exp(-first * 0.1f) >= 1f, $"a blend of {1f - MathF.Exp(-first * 0.1f)} does not settle");

        Frame(h);

        Assert.Equal(1f / 60f, h.Exposure.Steps[h.Exposure.PassCount - 1].Parameters.Get(AutoExposureKeys.DeltaTime), 6);
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

        // The node's own defaults, named rather than transcribed: the floor is EV 17 because it is
        // the bound on the *brightest* scene and has to reach daylight.
        Assert.Equal(Photometry.ExposureFromEv100(17f), adapt.Parameters.Get(AutoExposureKeys.MinimumExposure), 9);
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
