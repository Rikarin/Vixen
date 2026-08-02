// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>What a bright source does to the rest of the frame, rather than to its own pixels.</summary>
/// <remarks>
///     <para>
///         <b>Not bloom with a different name.</b> Bloom is light scattered a short way from where it
///         landed. A flare is light that reflected off the back of one lens element and the front of
///         another, so it lands somewhere else entirely — and where it lands is not arbitrary. A ghost
///         of a highlight sits on the line from that highlight through the centre of the frame, at a
///         distance set by which pair of elements it bounced between, which is why the whole effect is
///         one vector and a list of scale factors.
///     </para>
///     <para>
///         <b>Two passes.</b> A threshold into a quarter-resolution target, then a gather that adds
///         the ghosts, the halo and the starburst onto the scene. A full-resolution gather over a
///         dozen ghosts is a dozen dependent reads per pixel of an image that is mostly black.
///     </para>
///     <para>
///         ⚠ <b>Before the tonemap and before <c>!Bloom</c>.</b> A flare is light arriving at the
///         sensor, so it has to be able to blow out. Added after the curve it could only ever be a
///         wash over the picture, and a highlight already at white would gain a ghost brighter than
///         itself.
///     </para>
///     <para>
///         ⚠ <b><see cref="Threshold" /> is in the source's units.</b> In a physically lit frame that
///         is cd/m² and nothing is near one, so the usual value of one would flare the floor. The same
///         argument <c>!Bloom</c>'s threshold makes, and the same failure if it is left at the default.
///     </para>
/// </remarks>
public sealed class LensFlareRenderer : SceneRenderer, IDisposable, IPostProcessTarget {
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


    /// <summary>The linear HDR colour the flare is built from and added onto.</summary>
    public required string Source { get; init; }

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Flared";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; set; } = PixelFormat.Rgba16Float;

    /// <summary>Luminance above which a pixel contributes to a flare.</summary>
    public float Threshold { get; set; } = 1f;

    /// <summary>How many ghosts are traced along the centre vector.</summary>
    public int Ghosts { get; set; } = 5;

    /// <summary>How far apart consecutive ghosts are, as a fraction of the vector to the centre.</summary>
    public float GhostSpacing { get; set; } = 0.35f;

    /// <summary>How bright the ghosts are.</summary>
    public float GhostIntensity { get; set; } = 0.06f;

    /// <summary>Whether the halo ring is drawn.</summary>
    public bool UseHalo { get; set; } = true;

    /// <summary>How far out the halo ring sits, in UV.</summary>
    public float HaloRadius { get; set; } = 0.45f;

    /// <summary>How thick it is.</summary>
    public float HaloThickness { get; set; } = 0.2f;

    /// <summary>How bright it is.</summary>
    public float HaloIntensity { get; set; } = 0.04f;

    /// <summary>How far the three channels are sampled apart, in UV. Zero is no fringing.</summary>
    public float ChromaticOffset { get; set; } = 0.006f;

    /// <summary>Whether the diaphragm's diffraction spikes are drawn.</summary>
    public bool UseStarburst { get; set; }

    /// <summary>How many diffraction spikes.</summary>
    /// <remarks>
    ///     Taken from the view's lens when it has one, because the spikes <em>are</em> the diaphragm —
    ///     the same blade count that shapes the bokeh in <c>!DepthOfField</c>. A number typed here and
    ///     a different one on the camera would be one lens with two diaphragms.
    /// </remarks>
    public float StarburstBlades { get; set; } = 6f;

    /// <summary>How strongly the spikes modulate the ghosts.</summary>
    public float StarburstIntensity { get; set; } = 0.5f;

    /// <summary>A rotation on the spike pattern, in radians.</summary>
    public float StarburstAngle { get; set; }

    /// <summary>A tint on the whole flare, for a lens with a coloured coating.</summary>
    public Vector3 Tint { get; set; } = Vector3.One;

    /// <summary>The view whose lens supplies the blade count, or null for <see cref="StarburstBlades" />.</summary>
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

    /// <summary>How many blades the spikes are drawn for.</summary>
    public float Blades =>
        View?.Camera?.Lens is { HasLens: true, BladeCount: >= 3 } lens ? lens.BladeCount : StarburstBlades;

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        PassCount = 0;

        if (Modules is null || Samplers is null) {
            return;
        }

        var reduced = new Int2(Math.Max(frame.Size.X / 4, 1), Math.Max(frame.Size.Y / 4, 1));

        Declare(frame, BrightResource, reduced, Format);
        Declare(frame, Output, frame.Size, Format);

        Configure(0, 0, Source, BrightResource, BrightResource);
        Configure(1, 1, Source, BrightResource, Output);

        PassCount = 2;

        for (var index = 0; index < PassCount; index++) {
            BuildChild(passes[index], compositor, frame);
        }
    }

    void Configure(int index, int mode, string source, string bright, string target) {
        while (passes.Count <= index) {
            passes.Add(Create(passes.Count));
        }

        var pass = passes[index];

        pass.Modules = Modules;
        pass.Device = Device;
        pass.Descriptors.Allocator = Descriptors;

        pass.Parameters.Set(LensFlareKeys.Mode, mode);
        pass.Parameters.Set(LensFlareKeys.Ghosts, Math.Clamp(Ghosts, 0, 16));
        pass.Parameters.Set(LensFlareKeys.UseHalo, UseHalo);
        pass.Parameters.Set(LensFlareKeys.UseStarburst, UseStarburst);
        pass.Parameters.Set(LensFlareKeys.Threshold, Threshold);
        pass.Parameters.Set(LensFlareKeys.GhostSpacing, GhostSpacing);
        // ⚠ The ghosts take the volume's opinion and the halo and starburst do not. How much a
        // bright source spills is a property of the place — a dusty cellar flares more than a clean
        // corridor — while the ring's radius and the number of spikes are the lens's character, which
        // belongs to the document and to the camera's blade count.
        pass.Parameters.Set(
            LensFlareKeys.GhostIntensity,
            applied.FlareIntensity?.Over(GhostIntensity) ?? GhostIntensity
        );
        pass.Parameters.Set(LensFlareKeys.HaloRadius, HaloRadius);
        pass.Parameters.Set(LensFlareKeys.HaloThickness, HaloThickness);
        pass.Parameters.Set(LensFlareKeys.HaloIntensity, HaloIntensity);
        pass.Parameters.Set(LensFlareKeys.ChromaticOffset, ChromaticOffset);
        pass.Parameters.Set(LensFlareKeys.StarburstBlades, Blades);
        pass.Parameters.Set(LensFlareKeys.StarburstIntensity, StarburstIntensity);
        pass.Parameters.Set(LensFlareKeys.StarburstAngle, StarburstAngle);
        pass.Parameters.Set(LensFlareKeys.Tint, Tint);

        pass.ColourTargets.Clear();
        pass.ColourTargets.Add(target);

        pass.Reads.Clear();
        pass.Reads.Add(source);

        if (mode == 1) {
            pass.Reads.Add(bright);
        }

        pass.Descriptors.Bindings.Clear();

        pass.Descriptors.Bindings.Add(new() {
            Binding = LensFlareKeys.SourceBinding, Kind = DescriptorKind.SampledTexture, Resource = source
        });

        pass.Descriptors.Bindings.Add(new() {
            Binding = LensFlareKeys.SourceSamplerBinding,
            Kind = DescriptorKind.Sampler,
            Sampler = Samplers!.LinearClamp
        });

        // ⚠ The threshold pass binds its own source here, which it never samples. A binding is in a
        // shader's plan because it was declared and not because a variant reads it, and a set short
        // one entry is not bound at all — so leaving it out would refuse the pass rather than skip a
        // texture. Pointing it at the target instead would be a pass declaring a read of what it
        // writes, which the graph refuses by name.
        pass.Descriptors.Bindings.Add(new() {
            Binding = LensFlareKeys.BrightBinding,
            Kind = DescriptorKind.SampledTexture,
            Resource = mode == 1 ? bright : source
        });

        pass.Descriptors.Bindings.Add(new() {
            Binding = LensFlareKeys.BrightSamplerBinding,
            Kind = DescriptorKind.Sampler,
            Sampler = Samplers!.LinearClamp
        });
    }

    FullScreenRenderer Create(int index) =>
        new() {
            Name = $"{this}.{(index == 0 ? "Bright" : "Ghosts")}",
            ShaderName = LensFlareKeys.ShaderName,
            PermutationKeys = LensFlareKeys.UsedPermutationKeys,
            ConstantBinding = LensFlareKeys.ConstantBufferBinding
        };

    string BrightResource => $"{this}.Bright";

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
