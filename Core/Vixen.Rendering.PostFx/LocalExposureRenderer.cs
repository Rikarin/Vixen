// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>One exposure per region of the frame rather than one for the whole of it.</summary>
/// <remarks>
///     <para>
///         <b>The thing a single exposure cannot do.</b> A frame with a sunlit window and an unlit
///         interior has ten or twelve stops between the two and a tone curve has about six to spend,
///         so whatever the meter picks, one of them is white or black. That is what a camera does and
///         what an eye does not, because an eye adapts locally. Unreal 5 ships this and calls it Local
///         Exposure; it is the one effect on their list with no Unity counterpart at all.
///     </para>
///     <para>
///         <b>Two passes, because the base has to exist before it can be applied.</b> The first blurs
///         the log luminance bilaterally into a quarter-resolution target; the second reads that back
///         at full resolution and re-composes. A fragment stage cannot write a texture it is also
///         reading, so this is a chain rather than a pass — the same shape <see cref="BloomRenderer" />
///         has and for the same reason.
///     </para>
///     <para>
///         ⚠ <b>Quarter resolution for the base, and that is a decision rather than a saving.</b> The
///         base is the <em>large-scale</em> brightness of a region by definition; computing it at full
///         resolution would let it follow detail, which is exactly what it must not do. It also makes
///         the bilateral cross reach four times as far across the image for the same tap count.
///     </para>
///     <para>
///         ⚠ <b>Before the tonemap.</b> It moves radiance around before the curve shapes it, which is
///         the point — the whole purpose is to bring highlights into the range the curve can spend.
///         After the curve there is nothing left to recover.
///     </para>
/// </remarks>
public sealed class LocalExposureRenderer : SceneRenderer, IDisposable, IPostProcessTarget {
    /// <summary>How big the stand-in buffer is, in bytes.</summary>
    /// <remarks>
    ///     One <c>float</c>, which is what <c>LocalExposure.rvn</c> declares <c>exposureBuffer</c> as.
    ///     See <see cref="TonemapRenderer" /> for why a variant that never reads it still needs one.
    /// </remarks>
    const int StandInSize = 4;

    readonly List<FullScreenRenderer> passes = [];
    bool disposed;

    PostProcessOverlay applied;

    BufferHandle standIn;
    IGraphicsDevice? owner;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Recorded rather than applied. The authored properties stay exactly as the document set
    ///     them and the overlay is laid over them each time the node configures itself — a node that
    ///     wrote into its own properties here would lose the authored value the first frame a volume
    ///     reached it, and walking back out would restore the volume's numbers rather than the
    ///     document's.
    /// </remarks>
    public void Apply(in PostProcessOverlay overlay) => applied = overlay;


    /// <summary>The linear HDR colour it re-exposes.</summary>
    public required string Source { get; init; }

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "LocallyExposed";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; set; } = PixelFormat.Rgba16Float;

    /// <summary>How many taps the bilateral blur uses along each axis of its cross.</summary>
    public int Taps { get; set; } = 6;

    /// <summary>How far the bilateral gather reaches, in texels of the reduced image.</summary>
    public float BlurRadius { get; set; } = 6f;

    /// <summary>How different two luminances have to be, in stops, before a tap stops counting.</summary>
    /// <remarks>
    ///     ⚠ The one number that decides whether the result has halos. Large, and a window bleeds
    ///     across its frame and the wall beside it is compressed as though it were bright; small, and
    ///     the base follows every edge and there is no large-scale brightness left to compress.
    /// </remarks>
    public float EdgeRange { get; set; } = 1.5f;

    /// <summary>How much the highlights are brought down. 0 is off.</summary>
    public float HighlightContrast { get; set; } = 0.6f;

    /// <summary>And how much the shadows are brought up.</summary>
    public float ShadowContrast { get; set; } = 0.4f;

    /// <summary>A ceiling on how far any pixel may move, in stops.</summary>
    public float MaximumStops { get; set; } = 4f;

    /// <summary>The exposure the frame will be graded at, which is what the compression pivots around.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not a constant, and getting it wrong darkens or brightens the whole picture.</b>
    ///         The pivot is the log luminance that stays exactly where it is; everything above it comes
    ///         down and everything below comes up. That has to be wherever the meter has decided middle
    ///         grey is, or the effect is a global exposure change wearing a local one's clothes.
    ///     </para>
    ///     <para>
    ///         Taken from <see cref="View" />'s lens when there is one, and otherwise from here — and
    ///         <see cref="ExposureBuffer" /> beats both, which is the case that actually occurs. It is
    ///         the same number <c>!Tonemap</c> resolves, and a document that gives the two different
    ///         values gets a frame that is locally right and globally wrong.
    ///     </para>
    /// </remarks>
    public float Ev100 { get; set; } = 12f;

    /// <summary>
    ///     The buffer <c>AutoExposure</c> left this frame's measured exposure in, or empty for an
    ///     authored one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not optional in practice.</b> <c>post.localExposure</c> is gated on the meter, so
    ///         every frame the Standard Frame has ever run this node in is a metered one — and a
    ///         metered frame's exposure is produced on the device and never read back. A pivot
    ///         resolved on the host is therefore not "close enough" there; it is a number from a
    ///         different frame's arithmetic entirely.
    ///     </para>
    ///     <para>
    ///         <see cref="TonemapRenderer.ExposureBuffer" />'s counterpart, naming the same resource,
    ///         and the two have to agree: this pass moves radiance around and the tonemap shapes what
    ///         is left, so a pivot the curve does not share is a local correction applied about the
    ///         wrong middle.
    ///     </para>
    /// </remarks>
    public string ExposureBuffer { get; init; } = "";

    /// <summary>The view whose lens supplies the exposure value, or null for <see cref="Ev100" />.</summary>
    public RenderView? View { get; set; }

    /// <summary>Where shader modules come from.</summary>
    public EffectPipelineDescriber? Modules { get; set; }

    /// <summary>Where descriptor sets come from.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>Where the samplers come from.</summary>
    public SamplerCache? Samplers { get; set; }

    /// <summary>The device its pipelines and uniform buffers are created on.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>How many passes the last build declared.</summary>
    public int PassCount { get; private set; }

    /// <summary>The chain's passes, for a test or an inspector.</summary>
    public IReadOnlyList<FullScreenRenderer> Passes => passes;

    /// <summary>The linear exposure the pivot is derived from, when no buffer measures one.</summary>
    /// <remarks>
    ///     <para>
    ///         The lens's where <see cref="View" /> has one, which is the order
    ///         <see cref="TonemapRenderer" /> resolves in, and <see cref="Ev100" />'s otherwise.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The exposure, not the pivot — the shader turns one into the other now, and that
    ///         is a correction rather than a tidy-up.</b> This used to hand over
    ///         <c>log2(Photometry.MiddleGrey / exposure)</c>, and
    ///         <see cref="Photometry.MiddleGrey" /> is 1.2: ISO 2720's calibration constant, the
    ///         factor in <c>exposure = 1 / (1.2 · 2^EV)</c>. The scene luminance that renders as
    ///         middle grey is <c>0.18 / exposure</c> — the reflectance, not the constant — so the
    ///         pivot sat 2.74 stops above where it belonged, which in a photometric frame is above
    ///         nearly every texel in it. Everything below the pivot is lifted by
    ///         <see cref="ShadowContrast" />, so what the effect actually did was brighten the whole
    ///         picture: a wash, on the two tiers that switch it on.
    ///     </para>
    /// </remarks>
    public float Exposure {
        get {
            var ev100 = View?.Camera?.Lens is { HasLens: true } lens ? lens.Ev100 : Ev100;

            return MathF.Max(Photometry.ExposureFromEv100(ev100), 1e-9f);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The two passes are not in <see cref="SceneRenderer.Nested" />, so their answer is
    ///     carried out by hand.</b> See <see cref="BloomRenderer" /> for the shape.
    /// </remarks>
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        Degrade(DeclareChain(compositor, frame));
    }

    string? DeclareChain(GraphicsCompositor compositor, CompositorFrame frame) {
        PassCount = 0;

        if (Modules is null) {
            return "no Modules, so neither the blur nor the apply was declared and Output holds "
                + "whatever the graph last aliased into it — the frame is ungraded, not unlit";
        }

        if (Samplers is null) {
            return "no Samplers, so neither the blur nor the apply was declared and Output holds "
                + "whatever the graph last aliased into it — the frame is ungraded, not unlit";
        }

        var reduced = new Int2(Math.Max(frame.Size.X / 4, 1), Math.Max(frame.Size.Y / 4, 1));

        Declare(frame, BaseResource, reduced, PixelFormat.R16Float);
        Declare(frame, Output, frame.Size, Format);

        Configure(0, 0, Source, BaseResource, BaseResource, frame.Size);
        Configure(1, 1, Source, BaseResource, Output, frame.Size);

        PassCount = 2;

        string? reason = null;

        for (var index = 0; index < PassCount; index++) {
            BuildChild(passes[index], compositor, frame);
            reason ??= passes[index].Degraded;
        }

        return reason;
    }

    /// <summary>Sets up one pass of the chain, creating its node the first time.</summary>
    /// <remarks>
    ///     ⚠ <b>Both passes bind both textures, and the blur binds its own target as its base.</b> A
    ///     descriptor set is written whole or not at all, and <c>LocalExposure.rvn</c> declares
    ///     <c>baseLuminance</c> in both variants — a binding is in a shader's plan because it was
    ///     declared, not because a variant reads it. The blur never samples it; the graph is told so
    ///     by which resources the pass reads.
    /// </remarks>
    void Configure(int index, int mode, string source, string blurred, string target, Int2 size) {
        while (passes.Count <= index) {
            passes.Add(Create(passes.Count));
        }

        var pass = passes[index];

        pass.Modules = Modules;
        pass.Device = Device;
        pass.Descriptors.Allocator = Descriptors;

        pass.Parameters.Set(LocalExposureKeys.Mode, mode);
        pass.Parameters.Set(LocalExposureKeys.Taps, Math.Max(Taps, 1));
        pass.Parameters.Set(LocalExposureKeys.BlurRadius, BlurRadius);
        pass.Parameters.Set(LocalExposureKeys.EdgeRange, EdgeRange);
        pass.Parameters.Set(
            LocalExposureKeys.HighlightContrast,
            applied.LocalHighlightContrast?.Over(HighlightContrast) ?? HighlightContrast
        );

        pass.Parameters.Set(
            LocalExposureKeys.ShadowContrast,
            applied.LocalShadowContrast?.Over(ShadowContrast) ?? ShadowContrast
        );
        pass.Parameters.Set(LocalExposureKeys.MaximumStops, MaximumStops);

        var measured = !string.IsNullOrEmpty(ExposureBuffer);

        pass.Parameters.Set(LocalExposureKeys.UseExposureBuffer, measured);
        pass.Parameters.Set(LocalExposureKeys.Exposure, Exposure);

        // ⚠ The *blur's* texel size is the reduced target's and the apply's is the full frame's, and
        // the blur is what reads it. Handing the apply pass the reduced size would do nothing, since
        // it takes no offset taps — but handing the blur the full size would make its radius a
        // quarter of what was asked for, which is a base that follows detail.
        var texel = mode == 0
            ? new Vector2(4f / Math.Max(size.X, 1), 4f / Math.Max(size.Y, 1))
            : new Vector2(1f / Math.Max(size.X, 1), 1f / Math.Max(size.Y, 1));

        pass.Parameters.Set(LocalExposureKeys.TexelSize, texel);

        pass.ColourTargets.Clear();
        pass.ColourTargets.Add(target);

        pass.Reads.Clear();
        pass.Reads.Add(source);

        if (mode == 1) {
            pass.Reads.Add(blurred);
        }

        // ⚠ Emptied, unlike before. `Configure` re-declares the read every frame, so a list nothing
        // clears grows by one entry per frame for the life of the node — and both passes resolve it.
        pass.BufferReads.Clear();

        // ⚠ Both passes, not only the one that reads it. The two modes are one shader, so both
        // variants declare the binding — and a set is written whole or not at all. `Blur` never calls
        // `Pivot`, exactly as it never samples `baseLuminance`.
        if (measured) {
            pass.BufferReads.Add(ExposureBuffer);
        }

        pass.Descriptors.Bindings.Clear();

        pass.Descriptors.Bindings.Add(new() {
            Binding = LocalExposureKeys.SourceBinding, Kind = DescriptorKind.SampledTexture, Resource = source
        });

        pass.Descriptors.Bindings.Add(new() {
            Binding = LocalExposureKeys.SourceSamplerBinding,
            Kind = DescriptorKind.Sampler,
            Sampler = Samplers!.LinearClamp
        });

        // The blur points this at its own source rather than at its target, which nothing reads —
        // pointing it at the target would be a pass declaring a read of what it writes.
        pass.Descriptors.Bindings.Add(new() {
            Binding = LocalExposureKeys.BaseLuminanceBinding,
            Kind = DescriptorKind.SampledTexture,
            Resource = mode == 1 ? blurred : source
        });

        pass.Descriptors.Bindings.Add(new() {
            Binding = LocalExposureKeys.BaseSamplerBinding,
            Kind = DescriptorKind.Sampler,
            Sampler = Samplers!.LinearClamp
        });

        // The meter's buffer where there is one, and a stand-in where there is not — `TonemapRenderer`
        // verbatim, and for the identical reason: the permutation folds the read and leaves the
        // binding, so an unmetered frame that skipped this would draw with a descriptor nothing wrote.
        if (measured) {
            pass.Descriptors.Bindings.Add(new() {
                Binding = LocalExposureKeys.ExposureBufferBinding,
                Kind = DescriptorKind.StorageBuffer,
                Resource = ExposureBuffer
            });
        } else if (StandIn() is { IsValid: true } spare) {
            pass.Descriptors.Bindings.Add(new() {
                Binding = LocalExposureKeys.ExposureBufferBinding,
                Kind = DescriptorKind.StorageBuffer,
                Buffer = spare
            });
        }
    }

    /// <summary>The buffer a frame with no meter puts in the slot, created once per device.</summary>
    BufferHandle StandIn() {
        if (Device is not { } device) {
            return default;
        }

        if (standIn.IsValid && ReferenceEquals(owner, device)) {
            return standIn;
        }

        ReleaseStandIn();

        owner = device;

        standIn = device.CreateBuffer(
            new(StandInSize, BufferUsage.Storage, MemoryAccess.DeviceLocal, $"{this}.ExposureStandIn")
        );

        return standIn;
    }

    void ReleaseStandIn() {
        if (owner is { } device && standIn.IsValid) {
            device.Destroy(standIn);
        }

        standIn = default;
        owner = null;
    }

    FullScreenRenderer Create(int index) =>
        new() {
            Name = $"{this}.{(index == 0 ? "Base" : "Apply")}",
            ShaderName = LocalExposureKeys.ShaderName,
            PermutationKeys = LocalExposureKeys.UsedPermutationKeys,
            ConstantBinding = LocalExposureKeys.ConstantBufferBinding
        };

    string BaseResource => $"{this}.Base";

    static void Declare(CompositorFrame frame, string name, Int2 size, PixelFormat format) {
        if (frame.Has(name)) {
            return;
        }

        frame.Add(
            name,
            frame.Graph.CreateTexture(new(format, size.X, size.Y, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: name)),
            format
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        ReleaseStandIn();

        foreach (var pass in passes) {
            pass.Dispose();
        }

        passes.Clear();
    }
}
