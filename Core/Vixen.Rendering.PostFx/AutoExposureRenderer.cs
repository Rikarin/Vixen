// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>
///     The scene reduced to one number, produced on the device and left there for the tonemapper.
/// </summary>
/// <remarks>
///     <para>
///         <b>K2's last downstream item, and the one that needed every part of it.</b>
///         <c>AutoExposure.rvn</c> has been in the library since the post-process set was written and
///         nothing built the chain, because a chain is what it needs: a compute node, a halving
///         sequence of storage images, and a buffer that survives from one frame to the next. The
///         compute node is K2's; the rest is here.
///     </para>
///     <para>
///         <b>Why it cannot be a full-screen pass like every other effect in this assembly.</b> Its
///         output is not an image. It reduces the frame to a single value and leaves it in a buffer,
///         and a fragment stage cannot write a buffer at all — it writes the targets bound to it and
///         nothing else. The alternative is a readback, which costs a stall and a frame of latency
///         for a number the host never looks at.
///     </para>
///     <para>
///         ⚠ <b>The exposure buffer is a compositor <i>import</i>, not a declared resource, and that
///         is the whole of what makes adaptation work.</b> A declared buffer lives for one frame; this
///         one holds the value the eye has adapted to, and a buffer that started at zero every frame
///         would make <c>Adapt</c>'s ease-toward-target ease from nothing each time — an exposure that
///         never converges and a frame that flickers. So the host owns it, it is registered in
///         <see cref="GraphicsCompositor.BufferImports" />, and the graph sees it as memory something
///         outside the frame is responsible for.
///     </para>
///     <para>
///         <b>The chain halves to 1×1 and the last step is the one <c>Adapt</c> reads.</b> Every
///         reduction averages a 2×2 block through a single bilinear tap — a bilinear filter <i>is</i>
///         the average of the four texels it sits between, so the hardware does the reduction for
///         free, which only holds while each step is exactly half the one above it.
///     </para>
///     <para>
///         ⚠ <b>The first step takes the log and the rest do not</b>, which is a permutation rather
///         than a branch: averaging luminance directly lets one specular highlight drag the whole
///         frame's exposure down, and the geometric mean is what "the middle of this scene's
///         brightness" means. Every later step is then a plain average of values that are already
///         logarithmic, and the permutation keeps the colour-space arithmetic out of all of them.
///     </para>
/// </remarks>
public sealed class AutoExposureRenderer : SceneRenderer, IDisposable, IPostProcessTarget {
    /// <summary>How many bytes the exposure buffer holds. One float, and it is the whole point.</summary>
    public const int ExposureSize = sizeof(float);

    /// <summary>How many bins the histogram has, which the shader's clear dispatch fixes at 8×8.</summary>
    public const int BinCount = 64;

    /// <summary>How many bytes the histogram buffer holds.</summary>
    public const int HistogramSize = BinCount * sizeof(uint);

    readonly List<ComputeRenderer> steps = [];

    PostProcessOverlay applied;

    IGraphicsDevice? owner;
    BufferHandle exposure;
    BufferHandle histogram;
    bool seeded;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Recorded rather than applied, so the authored clamps stay what the document set and the
    ///     overlay is laid over them each frame. See <c>IPostProcessTarget</c>. What the meter takes
    ///     from it is the clamp pair — <c>meterMinimumEv</c> and <c>meterMaximumEv</c>, the look
    ///     profile's half of the exposure — and nothing else: the target the meter converges on is
    ///     the scene's, and the compensation is the tonemap's.
    /// </remarks>
    public void Apply(in PostProcessOverlay overlay) => applied = overlay;

    /// <summary>The linear HDR colour it measures.</summary>
    public required string Source { get; init; }

    /// <summary>Where the bilinear sampler comes from.</summary>
    public SamplerCache? Samplers { get; set; }

    /// <summary>Where the compute pipelines come from.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Where the descriptor sets come from.</summary>
    public DescriptorAllocator? Allocator { get; set; }

    /// <summary>The device the exposure buffer belongs to.</summary>
    /// <remarks>
    ///     Separate from <see cref="Pipelines" /> because the buffer outlives a frame and a pipeline
    ///     cache does not have to be the thing that owns it.
    /// </remarks>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>How long the last frame took, for the adaptation rate.</summary>
    /// <remarks>
    ///     ⚠ <b>A rate and not a blend factor.</b> <c>1 - exp(-dt·rate)</c> is the fraction of the way
    ///     to the target a given elapsed time covers, which makes the response frame-rate independent.
    ///     A raw lerp factor per frame adapts twice as fast at 120 Hz as at 60, which people report as
    ///     "the exposure feels wrong on my machine".
    /// </remarks>
    public float DeltaTime { get; set; } = 1f / 60f;

    /// <summary>How fast the eye goes light-adapted, in e-folds per second.</summary>
    public float BrightenRate { get; set; } = 3f;

    /// <summary>And dark-adapted, which in a real eye is far slower.</summary>
    public float DarkenRate { get; set; } = 1f;

    /// <summary>The luminance a correctly exposed mid-grey sits at.</summary>
    public float MiddleGrey { get; set; } = 0.18f;

    /// <summary>The lowest exposure the adaptation may settle on.</summary>
    /// <remarks>
    ///     ⚠ <b>EV 17, because the floor is what bounds the brightest scene.</b> See
    ///     <see cref="AutoExposureAsset.MinimumExposure" /> for the account: at the old 0.03 — EV 4.8,
    ///     a dim interior — every photometric daylight scene settled on the clamp rather than on its
    ///     own luminance and tonemapped to flat white.
    /// </remarks>
    public float MinimumExposure { get; set; } = Photometry.ExposureFromEv100(17f);

    /// <summary>And the highest, so a nearly black frame cannot drive it to infinity.</summary>
    public float MaximumExposure { get; set; } = 8f;

    /// <summary>Whether the frame is metered by a histogram rather than by a geometric mean.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The two answer different questions, and this picks which one.</b> A geometric mean
    ///         over the whole frame is a good number for a scene of roughly uniform brightness and a
    ///         bad one for the two cases exposure exists for — a dark room with a bright window, a
    ///         bright street with a dark doorway. The mean sits between the two populations and
    ///         exposes for neither. Discarding the darkest and brightest percentiles throws the window
    ///         and the doorway away and exposes for what is left.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Off by default, and off is what every existing frame already had.</b> Unreal calls
    ///         the two "Histogram" and "Basic", ships both, and defaults to the histogram; this
    ///         defaults to the chain because a document that turned it on without meaning to would get
    ///         a different exposure for reasons it never wrote down.
    ///     </para>
    /// </remarks>
    public bool UseHistogram { get; set; }

    /// <summary>The darkest luminance the histogram resolves, as a base-2 log.</summary>
    public float MinimumLogLuminance { get; set; } = -10f;

    /// <summary>And the brightest.</summary>
    public float MaximumLogLuminance { get; set; } = 20f;

    /// <summary>The fraction of the frame discarded from the dark end before the mean is taken.</summary>
    public float LowPercentile { get; set; } = 0.5f;

    /// <summary>And from the bright end.</summary>
    public float HighPercentile { get; set; } = 0.95f;

    /// <summary>How strongly the centre of the frame counts for more than its edge.</summary>
    /// <remarks>Zero meters evenly; higher narrows the weight toward the middle.</remarks>
    public float MeteringPower { get; set; } = 1f;

    /// <summary>What the chain's first reduction starts at, in texels.</summary>
    /// <remarks>
    ///     ⚠ <b>Not the frame's size.</b> Reducing a 4K frame to 1×1 is eleven dispatches and measures
    ///     nothing a 512-wide version does not: exposure is a property of the whole image, and every
    ///     step after the first is averaging an average. Starting small is the difference between nine
    ///     dispatches and thirteen, every frame, for the same number.
    /// </remarks>
    public int StartSize { get; set; } = 512;

    /// <summary>The buffer the tonemapper reads. Invalid until the first <c>Build</c>.</summary>
    public BufferHandle Exposure => exposure;

    /// <summary>The bins, for a test or an inspector. Invalid until the first histogram build.</summary>
    public BufferHandle Histogram => histogram;

    /// <summary>What the exposure buffer is called in the graph.</summary>
    public string ExposureResource => $"{this}.Exposure";

    /// <summary>And the histogram.</summary>
    /// <remarks>
    ///     ⚠ <b>Imported like the exposure, even though it does not have to survive a frame.</b> It is
    ///     cleared at the start of every chain and read at the end of the same one, so a declared
    ///     buffer would do — except that the graph has no way for a node to declare one. Importing it
    ///     is the shape that exists, and it costs 256 bytes that never alias.
    /// </remarks>
    public string HistogramResource => $"{this}.Histogram";

    /// <summary>How the buffer is described to the graph and to whoever copies it.</summary>
    BufferDescription Description =>
        new(ExposureSize, BufferUsage.Storage | BufferUsage.CopySource, MemoryAccess.DeviceLocal, ExposureResource);

    BufferDescription HistogramDescription =>
        new(HistogramSize, BufferUsage.Storage, MemoryAccess.DeviceLocal, HistogramResource);

    /// <summary>How many dispatches the last build produced — the reductions plus the adaptation.</summary>
    public int PassCount { get; private set; }

    /// <summary>The chain's nodes, the first <see cref="PassCount" /> of which are this frame's.</summary>
    public IReadOnlyList<ComputeRenderer> Steps => steps;

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        PassCount = 0;

        // ⚠ No effect system here, and that is `ComputeRenderer`'s doing rather than an omission: it
        // resolves its variant through `frame.Effects` and takes its set layout from the effect it
        // got. A property mirroring one would be a second place for the same answer to come from.
        if (Samplers is null || Pipelines is null || Allocator is null || Device is null) {
            return;
        }

        Acquire(Device);

        // ⚠ Imported into the *frame* rather than registered on the compositor, which is the same
        // thing `TemporalAntialiasingRenderer` does with its history and for the same two reasons.
        // The compositor's own import table is folded into the frame before any node builds, so a
        // node adding to it during its build is a frame too late — the first frame refers to a buffer
        // nothing bound, and every frame after that works, which is the worst shape a bug can have.
        // And an import is what tells the graph this memory belongs to somebody else, so it does not
        // alias next frame's exposure over this one's.
        frame.Add(
            ExposureResource,
            frame.Graph.ImportBuffer(
                exposure,
                Description,
                ResourceState.ShaderWrite,
                ResourceState.ShaderWrite
            ),
            Description
        );

        frame.Add(
            HistogramResource,
            frame.Graph.ImportBuffer(
                histogram,
                HistogramDescription,
                ResourceState.ShaderWrite,
                ResourceState.ShaderWrite
            ),
            HistogramDescription
        );

        var sizes = Chain();
        var index = 0;

        // Declared whichever meter runs, because the histogram's three modes still *bind* `target`
        // and `average` — a set is written whole or not at all, and there is no variant in which
        // those two bindings fold away. One 256² image and its chain is what that costs; the passes
        // that would fill it are the part that is skipped.
        for (var level = 0; level < sizes.Count; level++) {
            Declare(frame, Level(level), sizes[level]);
        }

        if (UseHistogram) {
            // Three dispatches instead of ten, and the middle one is the only one that touches the
            // frame. ⚠ The clear is its own dispatch rather than the first thing the build does: a
            // build invocation cannot clear "its" bin, because a bin belongs to a luminance rather
            // than to a pixel and every invocation is racing every other for all of them. A dispatch
            // boundary is the only ordering a device offers between the last write and the first read.
            Meter(index++, 2, "Clear", sizes[0], new(1, 1, 1));
            Meter(index++, 3, "Build", sizes[0], new(Groups(sizes[0].X), Groups(sizes[0].Y), 1));
            Meter(index++, 4, "Resolve", sizes[0], new(1, 1, 1));
        } else {
            for (var level = 0; level < sizes.Count; level++) {
                Reduce(index++, level, level == 0 ? Source : Level(level - 1), Level(level), sizes[level]);
            }

            Adapt(index++, Level(sizes.Count - 1));
        }

        PassCount = index;

        for (var step = 0; step < index; step++) {
            BuildChild(steps[step], compositor, frame);
        }
    }

    /// <summary>The sizes of the chain, halving from <see cref="StartSize" /> to 1×1.</summary>
    /// <remarks>
    ///     Square, because what is being measured is a scalar and the aspect ratio of the intermediate
    ///     does not enter into it — a non-square chain would need two counts and answer the same.
    /// </remarks>
    List<Int2> Chain() {
        List<Int2> sizes = [];

        for (var size = Math.Max(StartSize, 1); size > 1; size >>= 1) {
            sizes.Add(new(size >> 1, size >> 1));
        }

        if (sizes.Count == 0) {
            sizes.Add(new(1, 1));
        }

        return sizes;
    }

    void Reduce(int index, int level, string source, string target, Int2 size) {
        var node = At(index, $"Reduce{level}");

        node.Parameters.Set(AutoExposureKeys.Mode, 0);
        node.Parameters.Set(AutoExposureKeys.FirstStep, level == 0);

        // Eight by eight, which is what the shader declares; the tail invocations test themselves out
        // against the target's dimensions, because storing outside a storage image is undefined in
        // both targets — a corrupted neighbour on one driver and a device loss on another.
        node.Groups = new(Groups(size.X), Groups(size.Y), 1);

        node.Reads.Clear();
        node.Writes.Clear();
        node.BufferWrites.Clear();
        node.Descriptors.Bindings.Clear();

        node.Reads.Add(source);
        node.Writes.Add(target);

        // ⚠ Declared as a read even though a reduction never reads it, because it is *bound* in every
        // step — see the `average` binding below for why — and a node that binds a resource the graph
        // has not been told about is refused. The edge it creates is the one that was wanted anyway:
        // every reduction before the adaptation that writes it.
        node.BufferReads.Add(ExposureResource);

        // ⚠ And the histogram, which the chain never touches. A node binds a buffer only if it also
        // declared it here — `ComputeRenderer` builds the resolve table from BufferReads and
        // BufferWrites and nothing else — and the binding is not optional, because a descriptor set
        // is written whole or not at all.
        node.BufferReads.Add(HistogramResource);

        Bind(node);

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.SourceBinding, Kind = DescriptorKind.SampledTexture, Resource = source
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.SourceSamplerBinding, Kind = DescriptorKind.Sampler, Sampled = SamplerDescription.LinearClamp
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.TargetBinding, Kind = DescriptorKind.StorageTexture, Resource = target
        });

        // ⚠ Bound in every step even though only the adaptation writes it, because an incomplete set
        // is not bound at all — the fault `WorldRenderer` records as a five-set layout with four sets
        // bound. `average` takes the same texture as `target` here, which nothing reads: the reduce
        // path does not touch it.
        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.AverageBinding, Kind = DescriptorKind.StorageTexture, Resource = target
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.ExposureBinding, Kind = DescriptorKind.StorageBuffer, Resource = ExposureResource
        });

        // ⚠ Bound by the reduction and the adaptation too, which never touch it. `histogram` is a
        // binding of the shader's one set whichever mode a variant is, and an incomplete set is not
        // bound at all — so a chain that skipped it would lose the exposure buffer with it and every
        // dispatch would be refused.
        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.HistogramBinding, Kind = DescriptorKind.StorageBuffer, Resource = HistogramResource
        });
    }

    /// <summary>One of the histogram's three dispatches.</summary>
    /// <remarks>
    ///     <para>
    ///         All three bind the same set, and every binding in it, because a descriptor set is
    ///         written whole or not at all — the fault <c>WorldRenderer</c> records as a five-set
    ///         layout with four sets bound. The clear and the resolve read no texture and bind one
    ///         anyway; the build writes no image and binds two.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The build reads the scene at the chain's first size rather than at the frame's.</b>
    ///         A histogram is a distribution, and a distribution sampled at 256² has the same shape as
    ///         one sampled at 4K — with a sixty-fourth of the atomics contending for sixty-four bins,
    ///         which is where the cost of this actually is. It also keeps the quantised weight from
    ///         overflowing a bin.
    ///     </para>
    /// </remarks>
    void Meter(int index, int mode, string name, Int2 size, Int3 groups) {
        var node = At(index, name);

        node.Parameters.Set(AutoExposureKeys.Mode, mode);
        node.Parameters.Set(AutoExposureKeys.FirstStep, false);
        node.Groups = groups;

        node.Reads.Clear();
        node.Writes.Clear();
        node.BufferReads.Clear();
        node.BufferWrites.Clear();
        node.Descriptors.Bindings.Clear();

        node.Reads.Add(Source);
        node.Writes.Add(Level(0));

        // ⚠ <b>Each step declares what it actually does to the bins, and the resolve's is a read.</b>
        // Declaring the write on all three did order them — a write-after-write is still a barrier —
        // but a barrier from one write to the next carries no read access, so the resolve's loads
        // over the bins were never made visible to it. It worked on the drivers it was tried on and
        // was one cache policy away from metering a frame off stale bins. It also left the graph
        // seeing three writers and no reader at all, which is what VX2101 reported every frame and
        // what entitles the graph to reorder or cull the two that feed the answer.
        //
        // The build is a genuine read-modify-write: `atomicAdd` accumulates onto what the clear
        // zeroed, so it declares both, read first — the shape the graph's lint already understands.
        if (mode != 2) {
            node.BufferReads.Add(HistogramResource);
        }

        if (mode != 4) {
            node.BufferWrites.Add(HistogramResource);
        }

        if (mode == 4) {
            node.BufferWrites.Add(ExposureResource);
        } else {
            node.BufferReads.Add(ExposureResource);
        }

        Bind(node);

        node.Parameters.Set(AutoExposureKeys.MinimumLogLuminance, MinimumLogLuminance);
        node.Parameters.Set(AutoExposureKeys.MaximumLogLuminance, MaximumLogLuminance);
        node.Parameters.Set(AutoExposureKeys.LowPercentile, LowPercentile);
        node.Parameters.Set(AutoExposureKeys.HighPercentile, Math.Max(HighPercentile, LowPercentile));
        node.Parameters.Set(AutoExposureKeys.MeteringPower, MeteringPower);

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.SourceBinding, Kind = DescriptorKind.SampledTexture, Resource = Source
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.SourceSamplerBinding, Kind = DescriptorKind.Sampler, Sampled = SamplerDescription.LinearClamp
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.TargetBinding, Kind = DescriptorKind.StorageTexture, Resource = Level(0)
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.AverageBinding, Kind = DescriptorKind.StorageTexture, Resource = Level(0)
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.ExposureBinding, Kind = DescriptorKind.StorageBuffer, Resource = ExposureResource
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.HistogramBinding, Kind = DescriptorKind.StorageBuffer, Resource = HistogramResource
        });

        _ = size;
    }

    void Adapt(int index, string average) {
        var node = At(index, "Adapt");

        node.Parameters.Set(AutoExposureKeys.Mode, 1);
        node.Parameters.Set(AutoExposureKeys.FirstStep, false);

        // One group, and the shader sends every invocation but the first home: sixty-four invocations
        // reading and writing one buffer element with no ordering between them is the alternative.
        node.Groups = new(1, 1, 1);

        node.Reads.Clear();
        node.Writes.Clear();
        node.BufferWrites.Clear();
        node.Descriptors.Bindings.Clear();

        node.Reads.Add(average);
        node.Writes.Add(average);
        node.BufferWrites.Add(ExposureResource);

        // ⚠ And the histogram, which the chain never touches. A node binds a buffer only if it also
        // declared it here — `ComputeRenderer` builds the resolve table from BufferReads and
        // BufferWrites and nothing else — and the binding is not optional, because a descriptor set
        // is written whole or not at all.
        node.BufferReads.Add(HistogramResource);

        Bind(node);

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.SourceBinding, Kind = DescriptorKind.SampledTexture, Resource = average
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.SourceSamplerBinding, Kind = DescriptorKind.Sampler, Sampled = SamplerDescription.LinearClamp
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.TargetBinding, Kind = DescriptorKind.StorageTexture, Resource = average
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.AverageBinding, Kind = DescriptorKind.StorageTexture, Resource = average
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.ExposureBinding, Kind = DescriptorKind.StorageBuffer, Resource = ExposureResource
        });

        // ⚠ Bound by the reduction and the adaptation too, which never touch it. `histogram` is a
        // binding of the shader's one set whichever mode a variant is, and an incomplete set is not
        // bound at all — so a chain that skipped it would lose the exposure buffer with it and every
        // dispatch would be refused.
        node.Descriptors.Bindings.Add(new() {
            Binding = AutoExposureKeys.HistogramBinding, Kind = DescriptorKind.StorageBuffer, Resource = HistogramResource
        });
    }

    /// <summary>The scalars every step carries, whether or not its mode reads them.</summary>
    void Bind(ComputeRenderer node) {
        node.Parameters.Set(AutoExposureKeys.DeltaTime, DeltaTime);
        node.Parameters.Set(AutoExposureKeys.BrightenRate, BrightenRate);
        node.Parameters.Set(AutoExposureKeys.DarkenRate, DarkenRate);
        node.Parameters.Set(AutoExposureKeys.MiddleGrey, MiddleGrey);
        node.Parameters.Set(AutoExposureKeys.MinimumExposure, MinimumExposureFor());
        node.Parameters.Set(AutoExposureKeys.MaximumExposure, MaximumExposureFor());
    }

    /// <summary>The exposure floor, with the overlay's clamp laid over the authored one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The relation between the overlay's EVs and these knobs is reciprocal, and the
    ///         conversion happens here — the only place that knows both units.</b> The look authors
    ///         its clamps as EVs, the space a light meter and the rest of the look think in; this
    ///         node's knobs are linear multipliers, where a <em>high</em> EV is a <em>low</em>
    ///         exposure. So <c>meterMaximumEv</c> — "never expose for brighter than noon" — lands on
    ///         <see cref="MinimumExposure" />, and <c>meterMinimumEv</c> on
    ///         <see cref="MaximumExposure" />. Authoring the look in these raw units instead would
    ///         push that inversion into every <c>.vxlook</c> ever written.
    ///     </para>
    ///     <para>
    ///         The blend runs in EV for the tonemap's reason: stops are where exposure lerps
    ///         honestly, and the authored clamp converts, blends and converts back.
    ///     </para>
    /// </remarks>
    float MinimumExposureFor() =>
        applied.MeterMaximumEv is { } brightest
            ? Photometry.ExposureFromEv100(brightest.Over(Photometry.Ev100FromExposure(MinimumExposure)))
            : MinimumExposure;

    /// <summary>And the ceiling — the overlay's <c>meterMinimumEv</c>, converted. See above.</summary>
    float MaximumExposureFor() =>
        applied.MeterMinimumEv is { } darkest
            ? Photometry.ExposureFromEv100(darkest.Over(Photometry.Ev100FromExposure(MaximumExposure)))
            : MaximumExposure;

    ComputeRenderer At(int index, string name) {
        while (steps.Count <= index) {
            // ⚠ The name is set here and never again: `SceneRenderer.Name` is init-only, which is
            // deliberate — a node's name is what a frame graph's edges are recorded against, and a
            // node that could be renamed mid-frame would be a pass whose dependencies point at a
            // node nobody can find.
            steps.Add(new() {
                Name = $"{this}.Step{steps.Count}",
                ShaderName = AutoExposureKeys.ShaderName,
                PermutationKeys = AutoExposureKeys.UsedPermutationKeys,
                ConstantBinding = AutoExposureKeys.ConstantBufferBinding
            });
        }

        var node = steps[index];

        node.Samplers = Samplers;
        node.Pipelines = Pipelines;
        node.Descriptors.Allocator = Allocator;

        return node;
    }

    static void Declare(CompositorFrame frame, string name, Int2 size) {
        if (frame.Has(name)) {
            return;
        }

        frame.Add(
            name,
            frame.Graph.CreateTexture(new(
                PixelFormat.R32Float,
                size.X,
                size.Y,
                TextureUsage.Storage | TextureUsage.Sampled,
                Name: name
            )),
            PixelFormat.R32Float
        );
    }

    /// <summary>Creates the exposure buffer, once, and seeds it so the first frame is not black.</summary>
    /// <remarks>
    ///     ⚠ <b>Seeded to one rather than left zero.</b> A zeroed buffer is an exposure of zero, and
    ///     the adaptation eases <i>toward</i> its target — so the first several frames of a session
    ///     would be black and then fade in, which reads as a broken renderer rather than as an eye
    ///     adjusting. One is the neutral multiplier every authored exposure starts at.
    /// </remarks>
    void Acquire(IGraphicsDevice device) {
        if (seeded && ReferenceEquals(owner, device)) {
            return;
        }

        Release();

        owner = device;

        exposure = device.CreateBuffer(new(
            ExposureSize,
            BufferUsage.Storage | BufferUsage.CopyDestination | BufferUsage.CopySource,
            MemoryAccess.DeviceLocal,
            ExposureResource
        ));

        histogram = device.CreateBuffer(new(
            HistogramSize,
            BufferUsage.Storage | BufferUsage.CopyDestination | BufferUsage.CopySource,
            MemoryAccess.DeviceLocal,
            HistogramResource
        ));

        seeded = true;
    }

    void Release() {
        if (owner is { } device) {
            if (exposure.IsValid) {
                device.Destroy(exposure);
            }

            if (histogram.IsValid) {
                device.Destroy(histogram);
            }
        }

        exposure = default;
        histogram = default;
        owner = null;
        seeded = false;
    }

    /// <inheritdoc />
    public void Dispose() {
        foreach (var step in steps) {
            step.Dispose();
        }

        steps.Clear();
        Release();
    }

    /// <summary>How many eight-wide groups cover a run of texels.</summary>
    static int Groups(int extent) => Math.Max(1, (extent + 7) / 8);

    string Level(int index) => $"{this}.Level{index}";
}
