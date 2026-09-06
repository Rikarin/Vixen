// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
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

/// <summary>
///     The bloom chain — the first post effect that is more than one pass.
/// </summary>
/// <remarks>
///     <para>
///         A single full-screen pass proves the node. A pyramid proves the graph: nine textures whose
///         lifetimes overlap in a strict pattern, each written by one pass and read by exactly one
///         other. Every one of them is declared rather than imported, so the whole chain is the
///         graph's to allocate, to alias, and to drop.
///     </para>
///     <para>
///         The arithmetic asserted here is the arithmetic that is invisible when it is wrong. A texel
///         size taken from the target instead of the source makes a bloom that is subtly too soft;
///         reading the wrong level on the way up makes one that is subtly too tight. Neither throws,
///         and neither is something a screenshot answers.
///     </para>
/// </remarks>
public class BloomTests : IDisposable {
    // The shader's own keys rather than strings interned here, so a rename in Bloom.rvn breaks the
    // build rather than quietly making these assertions about a parameter nothing reads.
    static readonly ParameterKey<Vector2> TexelSize = BloomKeys.TexelSize;
    static readonly PermutationKey<int> Mode = BloomKeys.Mode;

    readonly NullDevice device = new(new() { Record = true, FramesInFlight = 2 });
    readonly EffectSystem effects = new();
    readonly DescriptorAllocator allocator;
    readonly SamplerCache samplers;
    readonly EffectPipelineDescriber describer;
    readonly Dictionary<string, DescriptorSetLayoutHandle> layouts = [];

    public BloomTests() {
        allocator = new(device);
        samplers = new(device);
        describer = new(device);

        // Set 2 of each shader, in the shape its reflection reports — indices from the generated
        // constants for the reason the chain itself takes them from there. A layout per shader rather
        // than one for both: they agree on neither what binding 2 is nor what binding 3 is, so one
        // shared layout is a set the composite writes a texture into where the bloom declared a
        // sampler. No device would report that, and now the Null backend does.
        Declare(
            BloomKeys.ShaderName,
            new(BloomKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(BloomKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(BloomKeys.PreviousBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(BloomKeys.SourceSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
        );

        Declare(
            TonemapKeys.ShaderName,
            new(TonemapKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(TonemapKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(TonemapKeys.LutBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(TonemapKeys.SourceSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
            new(TonemapKeys.LutSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
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

    /// <summary>
    ///     ⚠ A chain that declines publishes an <c>Output</c> nobody wrote, and says so.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The nine passes are the node's own rather than nodes a document named, so they are not
    ///         in <see cref="SceneRenderer.Nested" /> and <see cref="GraphicsCompositor.Degradations" />
    ///         cannot reach them — the chain carries the answer out by hand. Without it the only
    ///         evidence is <see cref="BloomRenderer.PassCount" /> sitting at zero, which is a number a
    ///         host has to already suspect something to go and read.
    ///     </para>
    ///     <para>
    ///         Asserted through the compositor's walk rather than the node's property, because the
    ///         walk is what a host reads and the forwarding is the part that could be dropped.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_chain_with_no_modules_says_what_its_output_holds_instead() {
        using var h = Build(composite: false);

        h.Bloom.Modules = null;
        Frame(h);

        Assert.Equal(0, h.Bloom.PassCount);

        var (node, reason) = Assert.Single(Degradations(h));

        Assert.Equal("Bloom", node);
        Assert.Contains("no Modules", reason, StringComparison.Ordinal);
        Assert.Contains("Output holds whatever the graph last aliased", reason, StringComparison.Ordinal);
    }

    /// <summary>A pyramid of no levels is a legitimate setting and a silent one.</summary>
    /// <remarks>
    ///     <see cref="BloomRenderer.Levels" /> comes from a quality tier, so this is a value a
    ///     document can carry rather than a programming error — and the reason quotes it, because
    ///     "Levels is 0" is the sentence that sends somebody to the tier table.
    /// </remarks>
    [Fact]
    public void A_chain_of_no_levels_quotes_the_setting() {
        using var h = Build(composite: false);

        h.Bloom.Levels = 0;
        Frame(h);

        var (_, reason) = Assert.Single(Degradations(h));

        Assert.Contains("Levels is 0", reason, StringComparison.Ordinal);
    }

    /// <summary>And a chain handed its modules back leaves the report the same frame.</summary>
    [Fact]
    public void A_chain_that_is_handed_its_modules_back_leaves_the_report() {
        using var h = Build(composite: false);
        var modules = h.Bloom.Modules;

        h.Bloom.Modules = null;
        Frame(h);
        Assert.Single(Degradations(h));

        h.Bloom.Modules = modules;
        Frame(h);

        Assert.Empty(Degradations(h));
    }

    /// <summary>The frame's whole list, which is what a host actually reads.</summary>
    static List<(string Node, string Reason)> Degradations(Harness h) {
        List<(string, string)> found = [];
        var reported = h.Compositor.Degradations(found);

        Assert.Equal(found.Count, reported);
        return found;
    }

    /// <summary>
    ///     A chain of <c>n</c> levels is one prefilter, <c>n−1</c> downsamples and <c>n−1</c> upsamples.
    /// </summary>
    /// <remarks>
    ///     The up-chain is one shorter than the down-chain, and that is not an off-by-one: the
    ///     smallest level is already its own upsample source, so there is nothing to add into it.
    /// </remarks>
    [Fact]
    public void The_chain_is_one_prefilter_and_two_passes_per_level_after_it() {
        using var h = Build();
        Frame(h);

        Assert.Equal(9, h.Bloom.PassCount);

        // Nine chain passes and the composite that reads the result.
        Assert.Equal(10, device.Recorder!.CountOf(RecordedCommandKind.Draw));
    }

    /// <summary>
    ///     Each pass steps in its <em>source's</em> texel grid, not its target's.
    /// </summary>
    /// <remarks>
    ///     Both filters offset their taps by a texel of what they are reading. Taking it from the
    ///     target would make the downsample's taps land half a texel apart and the upsample's tent
    ///     twice as wide as it should be — a bloom nobody can point at and everybody can see.
    /// </remarks>
    [Fact]
    public void Every_pass_steps_in_its_source_s_texel_grid() {
        using var h = Build();
        Frame(h);

        // The prefilter reads the full-resolution scene; the first downsample reads level 0, which is
        // half of it; the second reads level 1, a quarter.
        Assert.Equal(new Vector2(1f / 320f, 1f / 180f), h.Bloom.Passes[0].Parameters.Get(TexelSize));
        Assert.Equal(new Vector2(1f / 160f, 1f / 90f), h.Bloom.Passes[1].Parameters.Get(TexelSize));
        Assert.Equal(new Vector2(1f / 80f, 1f / 45f), h.Bloom.Passes[2].Parameters.Get(TexelSize));
    }

    /// <summary>The four modes are four variants of one shader.</summary>
    /// <remarks>
    ///     A permutation rather than a branch, because they read different bindings — the upsample has
    ///     a second texture the others do not — and a runtime branch would keep it bound in every pass
    ///     of the chain.
    /// </remarks>
    [Fact]
    public void The_four_modes_are_four_variants() {
        using var h = Build();
        Frame(h);

        Assert.Equal(0, h.Bloom.Passes[0].Parameters.Get(Mode));
        Assert.Equal(1, h.Bloom.Passes[1].Parameters.Get(Mode));
        Assert.Equal(2, h.Bloom.Passes[2].Parameters.Get(Mode));
        Assert.Equal(3, h.Bloom.Passes[h.Bloom.PassCount - 1].Parameters.Get(Mode));

        // Four bloom variants plus the composite's own shader.
        Assert.Equal(5, effects.Count);
    }

    /// <summary>
    ///     Exactly one pass Karis-averages, and it is the first downsample.
    /// </summary>
    /// <remarks>
    ///     The assertion the mode split exists for. The weight is what stops a highlight in a single
    ///     texel flickering as it moves, it only means anything on a pass that averages several taps of
    ///     the least-filtered level, and until <c>Mode</c> distinguished that pass from the rest of the
    ///     down-chain the shader's own condition — <c>Mode == 0</c>, the prefilter, which never calls
    ///     <c>Tap()</c> — could never be true. A chain that stopped selecting the mode would go back to
    ///     never weighting anything, and nothing else here would notice.
    /// </remarks>
    [Fact]
    public void Only_the_first_downsample_karis_averages() {
        using var h = Build();
        Frame(h);

        var weighting = h.Bloom.Passes.Take(h.Bloom.PassCount)
            .Where(pass => pass.Parameters.Get(Mode) == 1)
            .ToList();

        Assert.Single(weighting);
        Assert.Same(h.Bloom.Passes[1], weighting[0]);
    }

    /// <summary>An upsample reads the level below it and the one beside it.</summary>
    /// <remarks>
    ///     Two reads rather than one, and both are declared: the graph is what puts a barrier between
    ///     the pass that wrote each and the pass that samples it, and a chain that declared only its
    ///     main input would read the level beside it before it had been written.
    /// </remarks>
    [Fact]
    public void An_upsample_declares_both_of_the_levels_it_combines() {
        using var h = Build();
        Frame(h);

        var upsamples = h.Bloom.Passes.Take(h.Bloom.PassCount).Where(p => p.Parameters.Get(Mode) == 3).ToList();

        Assert.Equal(4, upsamples.Count);
        Assert.All(upsamples, pass => Assert.Equal(2, pass.Reads.Count));
        Assert.All(h.Bloom.Passes.Take(5), pass => Assert.Single(pass.Reads));
    }

    /// <summary>
    ///     A bloom nothing reads costs no passes at all.
    /// </summary>
    /// <remarks>
    ///     What declaring the pyramid rather than importing it buys. Nine passes and nine textures
    ///     disappear because one node stopped naming the result — which is the same rule that drops a
    ///     cull nobody consumes.
    /// </remarks>
    [Fact]
    public void A_bloom_nothing_reads_costs_nothing() {
        using var h = Build(composite: false);
        Frame(h);

        Assert.Equal(9, h.Bloom.PassCount);
        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.Draw));
    }

    /// <summary>
    ///     The chain stops where the frame stops halving.
    /// </summary>
    /// <remarks>
    ///     Eight levels of a 64×36 frame would end at a level a quarter of a texel across. Levels of a
    ///     handful of texels cost a pass each and contribute nothing, and a level of zero texels is a
    ///     texture a driver refuses to create.
    /// </remarks>
    [Fact]
    public void The_chain_is_clamped_to_what_the_frame_can_halve() {
        using var h = Build(size: new(64, 36));
        h.Bloom.Levels = 8;

        Frame(h);

        // 36 halves to 18, 9, 4, 2 — four levels, so seven passes.
        Assert.Equal(7, h.Bloom.PassCount);
    }

    /// <summary>Every pass compiles its pipeline once and keeps it across frames.</summary>
    /// <remarks>
    ///     The chain rebuilds its declarations every frame, because the texel sizes depend on a frame
    ///     size that can change. What must not be rebuilt with them is the nine pipelines and the nine
    ///     uniform buffers, which is why the child nodes outlive the frame that configured them.
    /// </remarks>
    [Fact]
    public void The_chain_compiles_its_pipelines_once() {
        using var h = Build();

        for (var i = 0; i < 4; i++) {
            Frame(h);
        }

        Assert.All(h.Bloom.Passes.Take(h.Bloom.PassCount), pass => Assert.Equal(1, pass.PipelineCount));

        // The texel sizes did not change either, so nothing re-uploaded after the first frame.
        Assert.All(h.Bloom.Passes.Take(h.Bloom.PassCount), pass => Assert.Equal(1, pass.UploadCount));
    }

    /// <summary>
    ///     A single-level chain still publishes its result.
    /// </summary>
    /// <remarks>
    ///     One level means no up-chain, and the up-chain is what normally declares the output — so
    ///     until this was fixed a <c>Levels = 1</c> bloom declared everything except the name the
    ///     rest of the frame reads it by, and the pass that read it failed by name. A legitimate
    ///     low-quality setting, and the shortest chain is exactly the one nobody tries.
    /// </remarks>
    [Fact]
    public void A_single_level_chain_still_publishes_its_result() {
        using var h = Build();
        h.Bloom.Levels = 1;

        Frame(h);

        Assert.Equal(1, h.Bloom.PassCount);
        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.Draw));
    }

    /// <summary>The result is published under one name, whatever the chain's depth.</summary>
    [Fact]
    public void The_result_is_the_top_of_the_up_chain() {
        using var h = Build();
        Frame(h);

        Assert.Equal("Bloom", h.Bloom.Output);
        Assert.Contains("Bloom", h.Bloom.Passes[h.Bloom.PassCount - 1].ColourTargets);
    }

    // --- The fixture --------------------------------------------------------

    static Effect Compiled(EffectKey key, DescriptorSetLayoutHandle layout) =>
        new() {
            Key = key,
            Stages = [
                new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
            ],
            SetLayouts = [default, default, layout, default],
            ConstantBufferSize = 32
        };

    sealed class AlwaysCompiles(Dictionary<string, DescriptorSetLayoutHandle> layouts) : IEffectProvider {
        public Effect? TryGet(EffectKey key) =>
            Compiled(key, layouts.TryGetValue(key.ShaderName, out var layout) ? layout : default);
    }

    /// <summary>A look's threshold and knee land on the prefilter, and leave with the look.</summary>
    /// <remarks>
    ///     The pair is what a look actually authors about bloom's shape — the intensity lives on the
    ///     tonemap that composites the pyramid — and the knee is the field this change added to the
    ///     overlay, so this is its end-to-end: settings, fold, node, parameter.
    /// </remarks>
    [Fact]
    public void A_looks_threshold_and_knee_reach_the_prefilter() {
        using var h = Build();

        var overlay = PostProcessOverlay.None;
        overlay.Add(new() { BloomThreshold = 2f, BloomKnee = 0.1f }, 1f);

        h.Bloom.Apply(overlay);
        Frame(h);

        Assert.Equal(2f, h.Bloom.Passes[0].Parameters.Get(BloomKeys.Threshold), 5);
        Assert.Equal(0.1f, h.Bloom.Passes[0].Parameters.Get(BloomKeys.Knee), 5);

        h.Bloom.Apply(PostProcessOverlay.None);
        Frame(h);

        Assert.Equal(1f, h.Bloom.Passes[0].Parameters.Get(BloomKeys.Threshold), 5);
        Assert.Equal(0.5f, h.Bloom.Passes[0].Parameters.Get(BloomKeys.Knee), 5);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required BloomRenderer Bloom { get; init; }
        public FullScreenRenderer? Composite { get; init; }

        public void Dispose() {
            Bloom.Dispose();
            Composite?.Dispose();
            Graph.DisposePool();
            System.Dispose();
        }
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

    Harness Build(bool composite = true, Int2 size = default) {
        size = size == default ? new(320, 180) : size;

        var system = new RenderSystem();

        var bloom = new BloomRenderer {
            Name = "Bloom",
            Source = "SceneColour",
            Output = "Bloom",
            Modules = describer,
            Descriptors = allocator,
            Samplers = samplers,
            Device = device
        };

        var compositor = new GraphicsCompositor(system) { FrameSize = size };

        compositor.Imports["SceneColour"] = Colour("SceneColour", size);

        if (!composite) {
            compositor.Game = bloom;
            return new() { System = system, Compositor = compositor, Graph = new(device), Bloom = bloom };
        }

        var tonemap = new FullScreenRenderer {
            Name = "Composite",
            ShaderName = TonemapKeys.ShaderName,
            Modules = describer,
            Device = device
        };

        tonemap.ColourTargets.Add("Display");
        tonemap.Reads.Add("Bloom");
        tonemap.Descriptors.Allocator = allocator;

        tonemap.Descriptors.Bindings.Add(
            new() { Binding = TonemapKeys.SourceBinding, Kind = DescriptorKind.SampledTexture, Resource = "Bloom" }
        );

        compositor.Imports["Display"] = Colour("Display", size);
        compositor.Game = new SceneRendererSequence { Children = { bloom, tonemap } };

        return new() {
            System = system,
            Compositor = compositor,
            Graph = new(device),
            Bloom = bloom,
            Composite = tonemap
        };
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
