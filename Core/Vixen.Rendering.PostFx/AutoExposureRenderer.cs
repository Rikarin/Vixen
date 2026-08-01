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
public sealed class AutoExposureRenderer : SceneRenderer, IDisposable {
    /// <summary>How many bytes the exposure buffer holds. One float, and it is the whole point.</summary>
    public const int ExposureSize = sizeof(float);

    readonly List<ComputeRenderer> steps = [];

    IGraphicsDevice? owner;
    BufferHandle exposure;
    bool seeded;

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
    public float MinimumExposure { get; set; } = 0.03f;

    /// <summary>And the highest, so a nearly black frame cannot drive it to infinity.</summary>
    public float MaximumExposure { get; set; } = 8f;

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

    /// <summary>What the exposure buffer is called in the graph.</summary>
    public string ExposureResource => $"{this}.Exposure";

    /// <summary>How the buffer is described to the graph and to whoever copies it.</summary>
    BufferDescription Description =>
        new(ExposureSize, BufferUsage.Storage | BufferUsage.CopySource, MemoryAccess.DeviceLocal, ExposureResource);

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

        var sizes = Chain();
        var index = 0;

        for (var level = 0; level < sizes.Count; level++) {
            Declare(frame, Level(level), sizes[level]);
        }

        for (var level = 0; level < sizes.Count; level++) {
            Reduce(index++, level, level == 0 ? Source : Level(level - 1), Level(level), sizes[level]);
        }

        Adapt(index++, Level(sizes.Count - 1));

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
    }

    /// <summary>The scalars every step carries, whether or not its mode reads them.</summary>
    void Bind(ComputeRenderer node) {
        node.Parameters.Set(AutoExposureKeys.DeltaTime, DeltaTime);
        node.Parameters.Set(AutoExposureKeys.BrightenRate, BrightenRate);
        node.Parameters.Set(AutoExposureKeys.DarkenRate, DarkenRate);
        node.Parameters.Set(AutoExposureKeys.MiddleGrey, MiddleGrey);
        node.Parameters.Set(AutoExposureKeys.MinimumExposure, MinimumExposure);
        node.Parameters.Set(AutoExposureKeys.MaximumExposure, MaximumExposure);
    }

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

        seeded = true;
    }

    void Release() {
        if (owner is { } device && exposure.IsValid) {
            device.Destroy(exposure);
        }

        exposure = default;
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
