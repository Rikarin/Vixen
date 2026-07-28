// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Tests;

/// <summary>
///     The full-screen pass, and the two caches every post effect needs behind it.
/// </summary>
/// <remarks>
///     <para>
///         Everything else in the compositor draws objects. A post effect has none, which is why a
///         node that draws three vertices was the last thing between the compositor and doc 06's
///         fifteen post-process entries.
///     </para>
///     <para>
///         The pass here is shaped like <c>Library/PostFx/Tonemap.rvn</c> — one source texture, one
///         sampler, one uniform block — because tonemap is the pass every frame ends with and the
///         smallest thing that exercises all three mechanisms at once.
///     </para>
/// </remarks>
public class PostProcessTests : IDisposable {
    // The shader's own keys, generated from Library/PostFx/Tonemap.reflect.json. Naming them here
    // rather than interning the strings is what makes this a test of the pass against the shader it
    // actually runs: the offsets below are still the fixture's, but the keys are not.
    static readonly ParameterKey<float> Exposure = TonemapKeys.Exposure;
    static readonly ParameterKey<float> WhitePoint = TonemapKeys.WhitePoint;
    static readonly PermutationKey<int> Operator = TonemapKeys.Operator;

    readonly NullDevice device = new(new() { Record = true, FramesInFlight = 2 });
    readonly EffectSystem effects = new();
    readonly DescriptorAllocator allocator;
    readonly SamplerCache samplers;
    readonly DescriptorSetLayoutHandle layout;

    public PostProcessTests() {
        allocator = new(device);
        samplers = new(device);

        layout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(1, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(2, DescriptorKind.UniformBuffer, ShaderStage.Fragment)
                ],
                "Tonemap"
            )
        );

        effects.AddProvider(new AlwaysCompiles(layout));
    }

    /// <inheritdoc />
    public void Dispose() {
        samplers.Dispose();
        allocator.Dispose();
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The sampler cache --------------------------------------------------

    /// <summary>Two descriptions that are equal are one sampler.</summary>
    /// <remarks>
    ///     A sampler is pure state, so this is not merely an optimisation: Vulkan caps how many a
    ///     device will create, and a post chain that made one per pass would reach it on a driver
    ///     that allows four thousand.
    /// </remarks>
    [Fact]
    public void Equal_sampler_descriptions_are_one_sampler() {
        using var cache = new SamplerCache(device);

        Assert.Equal(cache.LinearClamp, cache.GetOrCreate(SamplerDescription.LinearClamp));
        Assert.NotEqual(cache.LinearClamp, cache.PointClamp);
        Assert.Equal(2, cache.Count);
    }

    /// <summary>Disposing gives every sampler back.</summary>
    [Fact]
    public void The_sampler_cache_returns_what_it_took() {
        var before = device.LiveResourceCount;
        var cache = new SamplerCache(device);

        _ = cache.LinearClamp;
        _ = cache.PointClamp;

        Assert.True(device.LiveResourceCount > before);
        cache.Dispose();
        Assert.Equal(before, device.LiveResourceCount);
    }

    // --- The uniform block --------------------------------------------------

    /// <summary>A value lands at the offset the effect's reflection gave it.</summary>
    [Fact]
    public void A_parameter_is_written_at_its_own_offset() {
        using var constants = new EffectConstants(device);
        var parameters = new ParameterCollection();

        parameters.Set(Exposure, 2.5f);

        Assert.True(constants.Update(Compiled(EffectKey.From("Tonemap", parameters, [])), parameters));
        Assert.Equal(2.5f, MemoryMarshal.Read<float>(constants.Bytes[..4]));
    }

    /// <summary>
    ///     A parameter nobody set gets the value the shader declared, not zero.
    /// </summary>
    /// <remarks>
    ///     The reason <see cref="ParameterKey.DefaultBytes" /> exists. <c>var whitePoint: float = 4f</c>
    ///     arriving as zero is not a subtle error — it is a divide that produces a white frame — and
    ///     nothing anywhere would report it.
    /// </remarks>
    [Fact]
    public void An_unset_parameter_takes_the_value_the_shader_declared() {
        using var constants = new EffectConstants(device);
        var parameters = new ParameterCollection();

        parameters.Set(Exposure, 1f);

        Assert.True(constants.Update(Compiled(EffectKey.From("Tonemap", parameters, [])), parameters));
        Assert.Equal(4f, MemoryMarshal.Read<float>(constants.Bytes.Slice(4, 4)));
    }

    /// <summary>Nothing goes to the GPU while nothing has changed.</summary>
    [Fact]
    public void The_block_is_uploaded_once_until_a_value_changes() {
        using var constants = new EffectConstants(device);
        var parameters = new ParameterCollection();
        var effect = Compiled(EffectKey.From("Tonemap", parameters, []));

        parameters.Set(Exposure, 1f);
        constants.Update(effect, parameters);
        constants.Update(effect, parameters);
        constants.Update(effect, parameters);

        Assert.Equal(1, constants.UploadCount);

        parameters.Set(Exposure, 2f);
        constants.Update(effect, parameters);

        Assert.Equal(2, constants.UploadCount);
    }

    /// <summary>A different variant is a different layout, so it re-uploads even unchanged.</summary>
    /// <remarks>
    ///     The version alone would not catch it: the values did not change, the block they go into
    ///     did. Two variants of one shader can put the same parameter at different offsets.
    /// </remarks>
    [Fact]
    public void A_different_effect_re_uploads_even_when_no_value_changed() {
        using var constants = new EffectConstants(device);
        var parameters = new ParameterCollection();

        parameters.Set(Exposure, 1f);
        constants.Update(Compiled(EffectKey.From("Tonemap", parameters, [])), parameters);
        constants.Update(Compiled(EffectKey.From("Tonemap", parameters, [])), parameters);

        Assert.Equal(2, constants.UploadCount);
    }

    /// <summary>
    ///     A changed block goes to a different region, so an unfinished frame keeps what it had.
    /// </summary>
    /// <remarks>
    ///     Rewriting the same bytes is a race nothing reports: a uniform read half from one frame's
    ///     values and half from the next is a value that was never set anywhere. The block moves only
    ///     when it changes, which is what keeps the ring free in the common case of a post pass whose
    ///     parameters are the same every frame.
    /// </remarks>
    [Fact]
    public void A_changed_block_moves_to_another_region() {
        using var constants = new EffectConstants(device);
        var parameters = new ParameterCollection();
        var effect = Compiled(EffectKey.From("Tonemap", parameters, []));

        parameters.Set(Exposure, 1f);
        constants.Update(effect, parameters);
        var first = constants.Offset;

        // Unchanged: the same region, because nothing was rewritten.
        constants.Update(effect, parameters);
        Assert.Equal(first, constants.Offset);

        parameters.Set(Exposure, 2f);
        constants.Update(effect, parameters);

        Assert.NotEqual(first, constants.Offset);
        Assert.Equal(0, constants.Offset % constants.Alignment);
    }

    // --- The pass -----------------------------------------------------------

    /// <summary>
    ///     A full-screen pass is three vertices and no vertex buffer at all.
    /// </summary>
    /// <remarks>
    ///     The claim the node exists to make. The triangle comes out of <c>SV_VertexID</c>, so there
    ///     is nothing to allocate, nothing to bind, and no quad's diagonal seam across the middle of
    ///     the screen.
    /// </remarks>
    [Fact]
    public void A_full_screen_pass_draws_three_vertices_and_binds_nothing_to_draw_them_from() {
        using var h = Build();
        Frame(h);

        var draw = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.Draw));

        Assert.Equal(3, draw.A);
        Assert.Equal(1, draw.B);
        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.BindVertexBuffer));
        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.BindIndexBuffer));
    }

    /// <summary>Its source, its sampler and its block are one set, bound once.</summary>
    [Fact]
    public void It_binds_its_source_its_sampler_and_its_block_in_one_set() {
        using var h = Build();
        Frame(h);

        var bind = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BindDescriptorSet));

        Assert.Equal((long)DescriptorSetSlot.PerMaterial, bind.A);
        Assert.Equal(1, allocator.WriteCount);
    }

    /// <summary>The layout is the effect's, which is the only one its pipeline is compatible with.</summary>
    [Fact]
    public void The_layout_comes_from_the_effect() {
        using var h = Build();
        Frame(h);

        Assert.Equal(layout, h.Pass.Descriptors.Layout);
    }

    /// <summary>The pipeline is compiled once and reused for every later frame.</summary>
    /// <remarks>
    ///     A post chain rebuilds its nodes' declarations every frame, and a node that recompiled with
    ///     them would put a driver's shader compile inside the frame — the classic stutter that
    ///     profiling blames on whatever ran next.
    /// </remarks>
    [Fact]
    public void The_pipeline_survives_the_frame_that_built_it() {
        using var h = Build();

        for (var i = 0; i < 5; i++) {
            Frame(h);
        }

        Assert.Equal(1, h.Pass.PipelineCount);
        Assert.Equal(5, device.Recorder!.CountOf(RecordedCommandKind.Draw));
    }

    /// <summary>A different variant is a different pipeline.</summary>
    [Fact]
    public void A_permutation_change_compiles_a_second_pipeline() {
        using var h = Build();
        Frame(h);

        h.Pass.Parameters.Set(Operator, 2);
        Frame(h);

        Assert.Equal(2, h.Pass.PipelineCount);
    }

    /// <summary>A pass whose target nothing needs is dropped, like any other.</summary>
    /// <remarks>
    ///     Which is what makes the node a graph pass rather than a function that happens to draw:
    ///     writing to a declared texture nobody reads costs nothing at all.
    /// </remarks>
    [Fact]
    public void A_full_screen_pass_writing_to_nothing_anyone_reads_is_culled() {
        using var h = Build(importTarget: false);
        Frame(h);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.Draw));
    }

    /// <summary>Without a shader it declares nothing rather than throwing.</summary>
    [Fact]
    public void A_pass_whose_effect_is_missing_declares_nothing() {
        using var h = Build();
        var empty = new EffectSystem();

        h.Graph.Reset();
        h.Compositor.Build(h.Graph, empty, device);

        Assert.Single(empty.Misses);
    }

    // --- The fixture --------------------------------------------------------

    /// <summary>
    ///     One source texture, one sampler and a two-float block — the shape of a tonemap pass.
    /// </summary>
    /// <remarks>
    ///     The offsets are the ones a std140 block of two scalars has. Written out rather than derived
    ///     so that a test asserting a value landed at byte 4 is asserting against a stated layout
    ///     rather than against whatever the code did.
    /// </remarks>
    static Effect Compiled(EffectKey key, DescriptorSetLayoutHandle layout = default) =>
        new() {
            Key = key,
            Stages = [
                new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
            ],
            SetLayouts = layout.IsValid ? [default, default, layout, default] : [],
            ConstantBufferSize = 8,
            Parameters = [new(Exposure, 0, 4), new(WhitePoint, 4, 4)]
        };

    sealed class AlwaysCompiles(DescriptorSetLayoutHandle layout) : IEffectProvider {
        public Effect? TryGet(EffectKey key) => Compiled(key, layout);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required FullScreenRenderer Pass { get; init; }

        public void Dispose() {
            Pass.Dispose();
            Graph.DisposePool();
            System.Dispose();
        }
    }

    ImportedTexture Colour(string name) {
        var description = new TextureDescription(
            PixelFormat.Rgba16Float,
            320,
            180,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: name
        );

        var texture = device.CreateTexture(description);
        return new(texture, device.CreateTextureView(texture), description);
    }

    Harness Build(bool importTarget = true) {
        var system = new RenderSystem();
        var describer = new EffectPipelineDescriber(device);

        var pass = new FullScreenRenderer {
            Name = "Tonemap",
            ShaderName = "Tonemap",
            PermutationKeys = [Operator],
            ConstantBinding = 2,
            Modules = describer,
            Device = device
        };

        pass.ColourTargets.Add("Display");
        pass.Reads.Add("SceneColour");
        pass.Descriptors.Allocator = allocator;

        pass.Descriptors.Bindings.Add(
            new() { Binding = 0, Kind = DescriptorKind.SampledTexture, Resource = "SceneColour" }
        );

        pass.Descriptors.Bindings.Add(
            new() { Binding = 1, Kind = DescriptorKind.Sampler, Sampler = samplers.LinearClamp }
        );

        pass.Parameters.Set(Exposure, 1.2f);

        var compositor = new GraphicsCompositor(system) { FrameSize = new(320, 180), Game = pass };

        compositor.Imports["SceneColour"] = Colour("SceneColour");

        if (importTarget) {
            // The frame's final target is imported, so the pass writing it survives culling. A
            // declared target nothing reads is exactly what the culling test below relies on.
            compositor.Imports["Display"] = Colour("Display");
        } else {
            compositor.Resources.Add(
                new() {
                    Name = "Display",
                    Format = PixelFormat.Rgba16Float,
                    Usage = TextureUsage.ColourTarget | TextureUsage.Sampled
                }
            );
        }

        return new() { System = system, Compositor = compositor, Graph = new(device), Pass = pass };
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
