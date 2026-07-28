// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     The dual-filter bloom chain: a downsample pyramid, then an upsample that adds each level back.
/// </summary>
/// <remarks>
///     <para>
///         The first <em>multi-pass</em> effect, and the one worth building early for that reason. A
///         single post pass proves the node; a pyramid proves the graph — nine textures whose
///         lifetimes overlap in a strict pattern, each written by one pass and read by exactly one
///         other, which is precisely the shape transient aliasing exists for.
///     </para>
///     <para>
///         <strong>Mips rather than one wide blur.</strong> Each level halves the resolution, so a
///         bloom spanning a quarter of the screen costs about a third more than its first level alone.
///         A blur wide enough to do that in one pass would be hundreds of taps at full resolution.
///     </para>
///     <para>
///         <strong>The chain is declared, not imported.</strong> Every level lives and dies inside the
///         frame, so the graph owns them — which means the level-4 texture can be the same memory as
///         something else in the frame that had finished with it, and a bloom nothing reads costs no
///         passes at all.
///     </para>
///     <para>
///         <strong>The children are real <see cref="FullScreenRenderer" />s, kept between frames.</strong>
///         Each one owns a pipeline cache and a uniform buffer; rebuilding them every frame would
///         recompile the same pipelines and reallocate the same buffers, which is most of what a post
///         chain could get wrong. What is rebuilt per frame is only what depends on the frame's size.
///     </para>
/// </remarks>
public sealed class BloomRenderer : SceneRenderer, IDisposable {
    readonly List<FullScreenRenderer> passes = [];
    bool disposed;

    /// <summary>The shader to run, in its three modes.</summary>
    public string ShaderName { get; init; } = BloomKeys.ShaderName;

    /// <summary>The name of the texture the chain reads.</summary>
    public required string Source { get; init; }

    /// <summary>The name the chain's result is published under, for whatever composites it.</summary>
    /// <remarks>
    ///     The largest level of the up-chain. The node declares it like every other level, so a
    ///     tonemap pass reads it by name and the graph drops the whole pyramid if nothing does.
    /// </remarks>
    public string Output { get; init; } = "Bloom";

    /// <summary>
    ///     How many levels the pyramid has, the first at half resolution.
    /// </summary>
    /// <remarks>
    ///     Clamped to what the frame can actually halve. Five levels of a 1080p frame ends at 33×18,
    ///     and going further is levels of a handful of texels that cost a pass each and change
    ///     nothing.
    /// </remarks>
    public int Levels { get; set; } = 5;

    /// <summary>The format every level has.</summary>
    /// <remarks>
    ///     Half-float rather than 8-bit, and not for precision at the top of the chain — for the
    ///     bottom. A bloom level holds values well above one, and an 8-bit chain clamps them at the
    ///     first downsample, so a bright highlight blooms exactly as much as a merely white one.
    /// </remarks>
    public PixelFormat Format { get; set; } = PixelFormat.Rgba16Float;

    /// <summary>Luminance above which a pixel contributes.</summary>
    public float Threshold { get; set; } = 1f;

    /// <summary>How soft the threshold is, which is what stops highlights popping in and out.</summary>
    public float Knee { get; set; } = 0.5f;

    /// <summary>The upsample tent's radius in texels, which is how wide the bloom spreads.</summary>
    public float FilterRadius { get; set; } = 1f;

    /// <summary>How much of each level is added on the way up.</summary>
    public float Intensity { get; set; } = 1f;

    /// <summary>Whether the prefilter Karis-averages its taps, which stops highlight flicker.</summary>
    public bool KarisAverage { get; set; } = true;

    /// <summary>Where shader modules come from.</summary>
    public EffectPipelineDescriber? Modules { get; set; }

    /// <summary>Where descriptor sets come from.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>Where the sampler comes from.</summary>
    public SamplerCache? Samplers { get; set; }

    /// <summary>The device its pipelines and uniform buffers are created on.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>Which binding the source texture occupies.</summary>
    /// <remarks>
    ///     Defaulted from the shader rather than written down here, and that is the point of the
    ///     generated constants: Raven assigns a binding index from declaration order within a set, so
    ///     adding a texture above another in <c>Bloom.rvn</c> renumbers everything below it. A host
    ///     holding the old number gets a validation error at best and the wrong texture at worst, and
    ///     nothing would have told it. These four were all wrong when they were guessed.
    /// </remarks>
    public uint SourceBinding { get; set; } = BloomKeys.SourceBinding;

    /// <summary>Which binding the sampler occupies.</summary>
    public uint SamplerBinding { get; set; } = BloomKeys.SourceSamplerBinding;

    /// <summary>Which binding the larger mip occupies, for the upsample mode.</summary>
    public uint PreviousBinding { get; set; } = BloomKeys.PreviousBinding;

    /// <summary>Where the uniform block goes.</summary>
    public uint ConstantBinding { get; set; } = BloomKeys.ConstantBufferBinding;

    /// <summary>How many passes the last build declared.</summary>
    public int PassCount { get; private set; }

    /// <summary>The chain's passes, the first <see cref="PassCount" /> of which are this frame's.</summary>
    /// <remarks>
    ///     The list keeps whatever a longer chain once needed, so a frame that lowered
    ///     <see cref="Levels" /> does not throw away pipelines it is about to want again.
    /// </remarks>
    public IReadOnlyList<FullScreenRenderer> Passes => passes;

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        PassCount = 0;

        if (Modules is null || Samplers is null || Levels < 1) {
            return;
        }

        var levels = LevelsFor(frame.Size);
        var sizes = new Int2[levels];

        for (var i = 0; i < levels; i++) {
            sizes[i] = Halve(frame.Size, i + 1);
            Declare(frame, Down(i), sizes[i]);
        }

        // The up-chain is one shorter than the down-chain: the smallest level is already its own
        // upsample source, so there is nothing to add into it.
        for (var i = 0; i < levels - 1; i++) {
            Declare(frame, Up(i, levels), sizes[i]);
        }

        if (levels == 1) {
            // One level is the whole chain, so the prefilter's target *is* the result — and with no
            // up-chain to declare, nothing would otherwise be published under Output at all. Found by
            // a golden fixture reaching for a single-level bloom; a legitimate low-quality setting
            // that produced a frame naming a resource nobody had declared.
            frame.Add(Output, frame.Texture(ToString(), Down(0)), Format);
        }

        var index = 0;

        // Prefilter: full-resolution source into the first half-resolution level, keeping what is
        // above the threshold. The one pass that reads outside the pyramid.
        Configure(index++, PrefilterMode, Source, null, Down(0), frame.Size);

        for (var i = 1; i < levels; i++) {
            Configure(index++, DownsampleMode, Down(i - 1), null, Down(i), sizes[i - 1]);
        }

        // Downwards through the source levels but upwards through the pyramid: level i reads the
        // level below it — already combined — and adds the down-chain level beside it.
        for (var i = levels - 2; i >= 0; i--) {
            var source = i == levels - 2 ? Down(levels - 1) : Up(i + 1, levels);
            Configure(index++, UpsampleMode, source, Down(i), Up(i, levels), sizes[i + 1]);
        }

        PassCount = index;

        for (var i = 0; i < index; i++) {
            passes[i].Build(compositor, frame);
        }
    }

    /// <summary>Sets up one pass of the chain, creating its node the first time.</summary>
    /// <remarks>
    ///     A slot whose mode changed — which only happens when <see cref="Levels" /> does — is
    ///     replaced rather than relabelled, because <see cref="SceneRenderer.Name" /> is what a
    ///     RenderDoc capture and the graph's pass list are read by, and a pass labelled "Down3" doing
    ///     an upsample is worse than no label.
    /// </remarks>
    void Configure(int index, int mode, string source, string? previous, string target, Int2 sourceSize) {
        while (passes.Count <= index) {
            passes.Add(Create(mode, passes.Count));
        }

        if (!string.Equals(passes[index].Name, NameFor(mode, index), StringComparison.Ordinal)) {
            passes[index].Dispose();
            passes[index] = Create(mode, index);
        }

        var pass = passes[index];

        pass.Modules = Modules;
        pass.Device = Device;
        pass.Descriptors.Allocator = Descriptors;
        pass.Parameters.Set(BloomKeys.Mode, mode);
        pass.Parameters.Set(BloomKeys.KarisAverage, KarisAverage);

        // One texel of the *source*, not of the target. Both filters step in the source's texel grid,
        // and using the target's would make the downsample's taps land half a texel apart and the
        // upsample's tent twice as wide as it should be — a bloom that is subtly too soft, which is
        // exactly the kind of wrong nobody can point at.
        pass.Parameters.Set(BloomKeys.TexelSize, new Vector2(1f / Math.Max(sourceSize.X, 1), 1f / Math.Max(sourceSize.Y, 1)));
        pass.Parameters.Set(BloomKeys.Threshold, Threshold);
        pass.Parameters.Set(BloomKeys.Knee, Knee);
        pass.Parameters.Set(BloomKeys.FilterRadius, FilterRadius);
        pass.Parameters.Set(BloomKeys.Intensity, Intensity);

        pass.ColourTargets.Clear();
        pass.ColourTargets.Add(target);

        pass.Reads.Clear();
        pass.Reads.Add(source);

        pass.Descriptors.Bindings.Clear();

        pass.Descriptors.Bindings.Add(
            new() { Binding = SourceBinding, Kind = DescriptorKind.SampledTexture, Resource = source }
        );

        pass.Descriptors.Bindings.Add(
            new() { Binding = SamplerBinding, Kind = DescriptorKind.Sampler, Sampler = Samplers!.LinearClamp }
        );

        if (previous is not null) {
            pass.Reads.Add(previous);

            pass.Descriptors.Bindings.Add(
                new() { Binding = PreviousBinding, Kind = DescriptorKind.SampledTexture, Resource = previous }
            );
        }
    }

    void Declare(CompositorFrame frame, string name, Int2 size) {
        if (frame.Has(name)) {
            return;
        }

        frame.Add(
            name,
            frame.Graph.CreateTexture(
                new(Format, size.X, size.Y, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: name)
            ),
            Format
        );
    }

    /// <summary>How many levels the frame can actually be halved into.</summary>
    int LevelsFor(Int2 size) {
        var levels = Levels;

        while (levels > 1 && (Halve(size, levels).X < 2 || Halve(size, levels).Y < 2)) {
            levels--;
        }

        return levels;
    }

    static Int2 Halve(Int2 size, int times) =>
        new(Math.Max(size.X >> times, 1), Math.Max(size.Y >> times, 1));

    string Down(int level) => $"{this}.Down{level}";

    string Up(int level, int levels) => level == 0 ? Output : $"{this}.Up{level}";

    FullScreenRenderer Create(int mode, int index) =>
        new() {
            Name = NameFor(mode, index),
            ShaderName = ShaderName,
            PermutationKeys = [BloomKeys.Mode, BloomKeys.KarisAverage],
            ConstantBinding = ConstantBinding
        };

    string NameFor(int mode, int index) => $"{this}.{Describe(mode)}{index}";

    static string Describe(int mode) => mode switch {
        PrefilterMode => "Prefilter",
        DownsampleMode => "Down",
        _ => "Up"
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

        passes.Clear();
    }

    /// <summary>The three values <c>Bloom.rvn</c>'s <c>Mode</c> permutation takes.</summary>
    /// <remarks>
    ///     Named here rather than generated, because a permutation's <em>meaning</em> is prose in the
    ///     shader — "0 prefilter, 1 downsample, 2 upsample-and-combine" — and nothing in the
    ///     reflection carries it. The key and its default are generated; what each number means is
    ///     the one thing still copied by hand.
    /// </remarks>
    const int PrefilterMode = 0;

    const int DownsampleMode = 1;

    const int UpsampleMode = 2;
}
