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
    readonly List<FullScreenRenderer> passes = [];
    bool disposed;

    PostProcessOverlay applied;

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
    ///         Taken from <see cref="View" />'s lens when there is one, and otherwise from
    ///         <see cref="Ev100" />. It is the same number <c>!Tonemap</c> resolves, and a document
    ///         that gives the two different values gets a frame that is locally right and globally
    ///         wrong.
    ///     </para>
    /// </remarks>
    public float Ev100 { get; set; } = 12f;

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

    /// <summary>The log-2 luminance the compression pivots around.</summary>
    /// <remarks>
    ///     <c>middleGrey / exposure</c> is the scene luminance that comes out as middle grey, and its
    ///     log is where the pivot goes. At EV 12 that is about 4.6 cd/m², whose log-2 is 2.2 — a long
    ///     way from the −2.5 a display-referred pipeline would use, which is the whole reason this is
    ///     computed rather than typed.
    /// </remarks>
    public float Pivot {
        get {
            var ev100 = View?.Camera?.Lens is { HasLens: true } lens ? lens.Ev100 : Ev100;
            var exposure = MathF.Max(Photometry.ExposureFromEv100(ev100), 1e-9f);

            return MathF.Log2(MathF.Max(Photometry.MiddleGrey / exposure, 1e-9f));
        }
    }

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        PassCount = 0;

        if (Modules is null || Samplers is null) {
            return;
        }

        var reduced = new Int2(Math.Max(frame.Size.X / 4, 1), Math.Max(frame.Size.Y / 4, 1));

        Declare(frame, BaseResource, reduced, PixelFormat.R16Float);
        Declare(frame, Output, frame.Size, Format);

        Configure(0, 0, Source, BaseResource, BaseResource, frame.Size);
        Configure(1, 1, Source, BaseResource, Output, frame.Size);

        PassCount = 2;

        for (var index = 0; index < PassCount; index++) {
            BuildChild(passes[index], compositor, frame);
        }
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
        pass.Parameters.Set(LocalExposureKeys.Pivot, Pivot);

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

        foreach (var pass in passes) {
            pass.Dispose();
        }

        passes.Clear();
    }
}
