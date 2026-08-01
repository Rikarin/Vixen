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

    Harness Build(int start = 512, bool measured = true) {
        var size = new Int2(320, 180);
        var system = new RenderSystem();

        var exposure = new AutoExposureRenderer {
            Name = "AutoExposure",
            Source = "SceneColour",
            Samplers = samplers,
            Pipelines = pipelines,
            Allocator = allocator,
            Device = device,
            StartSize = start
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
