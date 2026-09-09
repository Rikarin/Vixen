// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>The anamorphic smear a bright highlight gets, which bloom cannot produce.</summary>
/// <remarks>
///     <para>
///         <b>Not bloom with a direction.</b> Bloom is isotropic because the scatter it models is:
///         light spreading a short way in every direction from where it landed. A streak comes from an
///         aperture that is not round — an anamorphic lens squeezes a wide image onto a normal frame
///         with a cylindrical element, so what diffracts and scatters inside it is stretched along one
///         axis. No radius of an isotropic blur is that shape.
///     </para>
///     <para>
///         <b>Three kinds of pass, and the middle one runs several times.</b> A threshold into a
///         reduced target, then <see cref="BlurPasses" /> separable blurs along
///         <see cref="Direction" />, then a composite that adds the result onto the scene. The blurs
///         ping-pong between two planes of the same size and each one's stride is
///         <c>(2·Samples + 1)</c> times the last, so three passes of four taps reach 729 texels for
///         the cost of 27 samples rather than 729.
///     </para>
///     <para>
///         ⚠ <b>Each pass steps in its <em>source's</em> texel grid, not the frame's.</b> The bright
///         plane is at <see cref="Scale" /> of the frame, so a stride measured in the frame's texels
///         would be a fraction of a texel and every tap would land back where it started — a streak
///         subtly too short and too soft, which no screenshot answers. It is the mistake the bloom
///         chain records having made, and <see cref="LightStreakKeys.TexelSize" /> is set per pass
///         from the plane that pass reads for that reason.
///     </para>
///     <para>
///         ⚠ <b>Before the tonemap, like <see cref="LensFlareRenderer" />.</b> A streak is light
///         arriving at the sensor and has to be able to blow out; added after the curve it is a wash
///         over the picture.
///     </para>
///     <para>
///         ⚠ <b><see cref="Threshold" /> is in the source's units.</b> In a physically lit frame that
///         is cd/m² and nothing is near one, so the default of one would streak the floor — the same
///         argument <c>!Bloom</c>'s threshold makes and the same failure if it is left alone.
///     </para>
/// </remarks>
public sealed class LightStreakRenderer : SceneRenderer, IDisposable, IPostProcessTarget {
    readonly List<FullScreenRenderer> passes = [];
    bool disposed;

    PostProcessOverlay applied;

    /// <inheritdoc />
    /// <remarks>
    ///     Recorded rather than applied, on <see cref="LensFlareRenderer.Apply" />'s terms: a node
    ///     that wrote into its own properties here would lose the authored value the first frame a
    ///     volume reached it.
    /// </remarks>
    public void Apply(in PostProcessOverlay overlay) => applied = overlay;

    /// <summary>The linear HDR colour the streak is built from and added onto.</summary>
    public required string Source { get; init; }

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Streaked";

    /// <summary>The format of the targets it declares.</summary>
    public PixelFormat Format { get; set; } = PixelFormat.Rgba16Float;

    /// <summary>The fraction of the frame the streak is built at.</summary>
    /// <remarks>
    ///     A quarter by default. The smear is wide and smooth by construction, so resolving it finely
    ///     buys nothing a viewer can see and costs four times the bandwidth of every blur pass.
    /// </remarks>
    public float Scale { get; set; } = 0.25f;

    /// <summary>Luminance above which a pixel streaks, in the source's units.</summary>
    /// <remarks>
    ///     ⚠ <b>Photometric, and deliberately not the one every other threshold in this assembly
    ///     defaults to.</b> The renderer works in cd/m² and nothing there is near one, so a threshold
    ///     of one streaks the floor — the smear stops being a shape put where a highlight is and
    ///     becomes a second copy of the whole picture. Measured on the Epic tier fixture: at one it
    ///     moves the frame's average channel by 23.1 of 255, which is a whole-frame shading change
    ///     rather than an effect. Forty thousand is what sample 13's hand-authored frame gives its
    ///     flare.
    /// </remarks>
    public float Threshold { get; set; } = 40_000f;

    /// <summary>How many blur passes run, each reaching further than the last.</summary>
    /// <remarks>
    ///     Three of four taps reach 729 texels of the reduced plane. A fourth buys a streak longer
    ///     than any frame is wide, and a first pass alone is a nine-texel smudge.
    /// </remarks>
    public int BlurPasses { get; set; } = 3;

    /// <summary>How many taps each side of the centre one blur pass takes.</summary>
    public int Samples { get; set; } = 4;

    /// <summary>The axis the smear runs along, in UV. Normalised before it reaches the shader.</summary>
    public Vector2 Direction { get; set; } = new(1f, 0f);

    /// <summary>How fast a tap fades per texel of separation. Below one, or the streak never ends.</summary>
    public float Attenuation { get; set; } = 0.94f;

    /// <summary>How bright the finished streak is added back.</summary>
    public float Intensity { get; set; } = 0.6f;

    /// <summary>A tint on the streak, for the blue a coated anamorphic element is known for.</summary>
    public Vector3 Tint { get; set; } = Vector3.One;

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

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ The chain is not in <see cref="SceneRenderer.Nested" />, so its answer is carried out by
    ///     hand — see <see cref="BloomRenderer" /> and <see cref="LensFlareRenderer" /> for the same
    ///     shape and the same reason: a chain that declines publishes an <see cref="Output" /> nobody
    ///     wrote.
    /// </remarks>
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        Degrade(DeclareChain(compositor, frame));
    }

    string? DeclareChain(GraphicsCompositor compositor, CompositorFrame frame) {
        PassCount = 0;

        if (Modules is null) {
            return "no Modules, so no streak pass was declared and Output holds whatever the graph "
                + "last aliased into it";
        }

        if (Samplers is null) {
            return "no Samplers, so no streak pass was declared and Output holds whatever the graph "
                + "last aliased into it";
        }

        var blurs = Math.Clamp(BlurPasses, 1, 8);
        var taps = Math.Clamp(Samples, 1, 8);
        var scale = Math.Clamp(Scale, 0.05f, 1f);

        var reduced = new Int2(
            Math.Max((int)(frame.Size.X * scale), 1),
            Math.Max((int)(frame.Size.Y * scale), 1)
        );

        // ⚠ The texel of the *reduced* plane, which is what every blur pass steps in. Derived from
        // the extent that was actually declared rather than from `scale`, because the extent is an
        // integer and a 129-texel frame at a quarter is 32 and not 32.25.
        var texel = new Vector2(1f / reduced.X, 1f / reduced.Y);

        Declare(frame, PingResource, reduced, Format);
        Declare(frame, PongResource, reduced, Format);
        Declare(frame, Output, frame.Size, Format);

        // The threshold writes Ping; blur `i` reads what the pass before it wrote and writes the
        // other plane, so after `blurs` of them the streak is in Ping when the count is even.
        Configure(0, 0, taps, Source, Source, PingResource, texel, 1f);

        var read = PingResource;
        var write = PongResource;
        var stride = 1f;

        for (var index = 0; index < blurs; index++) {
            Configure(index + 1, 1, taps, Source, read, write, texel, stride);

            (read, write) = (write, read);
            stride *= (taps * 2) + 1;
        }

        Configure(blurs + 1, 2, taps, Source, read, Output, texel, stride);

        PassCount = blurs + 2;

        string? reason = null;

        for (var index = 0; index < PassCount; index++) {
            BuildChild(passes[index], compositor, frame);
            reason ??= passes[index].Degraded;
        }

        return reason;
    }

    void Configure(
        int index,
        int mode,
        int taps,
        string source,
        string streak,
        string target,
        Vector2 texel,
        float stride
    ) {
        while (passes.Count <= index) {
            passes.Add(Create(passes.Count));
        }

        var pass = passes[index];

        pass.Modules = Modules;
        pass.Device = Device;
        pass.Descriptors.Allocator = Descriptors;

        pass.Parameters.Set(LightStreakKeys.Mode, mode);
        pass.Parameters.Set(LightStreakKeys.Samples, taps);
        pass.Parameters.Set(LightStreakKeys.TexelSize, texel);
        pass.Parameters.Set(LightStreakKeys.Direction, Axis);
        pass.Parameters.Set(LightStreakKeys.Stride, stride);
        pass.Parameters.Set(LightStreakKeys.Attenuation, Attenuation);
        pass.Parameters.Set(LightStreakKeys.Threshold, Threshold);

        // ⚠ The volume's opinion reaches the intensity and nothing else, on the flare's terms: how
        // much a bright source spills is a property of the place, while the axis and the taper are
        // the lens's character and belong to the document.
        pass.Parameters.Set(
            LightStreakKeys.Intensity,
            applied.FlareIntensity?.Over(Intensity) ?? Intensity
        );

        pass.Parameters.Set(LightStreakKeys.Tint, Tint);

        pass.ColourTargets.Clear();
        pass.ColourTargets.Add(target);

        pass.Reads.Clear();

        // ⚠ Every pass declares a read of the scene, including the blurs, whose variant never samples
        // it. A read is what makes a binding resolvable — the graph refuses a node that binds a
        // texture nothing declared, by name — and the binding is there because the *shader* declares
        // it and not because this variant uses it. Leaving it out to save the graph a lifetime
        // refuses the pass instead: "refers to bound texture, which nothing bound".
        pass.Reads.Add(source);

        if (mode != 0) {
            pass.Reads.Add(streak);
        }

        pass.Descriptors.Bindings.Clear();

        pass.Descriptors.Bindings.Add(new() {
            Binding = LightStreakKeys.SourceBinding,
            Kind = DescriptorKind.SampledTexture,
            Resource = source
        });

        pass.Descriptors.Bindings.Add(new() {
            Binding = LightStreakKeys.SourceSamplerBinding,
            Kind = DescriptorKind.Sampler,
            Sampler = Samplers!.LinearClamp
        });

        // ⚠ The threshold pass binds a streak plane it never samples, and it must. A binding is in a
        // shader's plan because it was declared and not because a variant reads it, and a set short
        // one entry is not bound at all — so leaving it out refuses the pass rather than skipping a
        // texture. It is pointed at the scene rather than at the plane this pass writes, because a
        // pass declaring a read of its own target is what the graph refuses by name.
        pass.Descriptors.Bindings.Add(new() {
            Binding = LightStreakKeys.StreakBinding,
            Kind = DescriptorKind.SampledTexture,
            Resource = mode == 0 ? source : streak
        });

        pass.Descriptors.Bindings.Add(new() {
            Binding = LightStreakKeys.StreakSamplerBinding,
            Kind = DescriptorKind.Sampler,
            Sampler = Samplers!.LinearClamp
        });
    }

    /// <summary>The axis, normalised, or the horizontal one when it was authored as zero.</summary>
    /// <remarks>
    ///     ⚠ Zero is what an unset <see cref="Vector2" /> field is, and a zero axis makes every tap
    ///     land on the centre texel — a chain of seventeen samples of one pixel, which comes back as
    ///     the input and reads as "the streak is subtle". Horizontal is what an anamorphic lens does,
    ///     so it is also the right answer to fall back to.
    /// </remarks>
    Vector2 Axis =>
        Direction.LengthSquared() > 1e-8f ? Vector2.Normalize(Direction) : new Vector2(1f, 0f);

    FullScreenRenderer Create(int index) =>
        new() {
            Name = $"{this}.{index}",
            ShaderName = LightStreakKeys.ShaderName,
            PermutationKeys = LightStreakKeys.UsedPermutationKeys,
            ConstantBinding = LightStreakKeys.ConstantBufferBinding
        };

    string PingResource => $"{this}.StreakA";

    string PongResource => $"{this}.StreakB";

    static void Declare(CompositorFrame frame, string name, Int2 size, PixelFormat format) {
        if (frame.Has(name)) {
            return;
        }

        frame.Add(
            name,
            frame.Graph.CreateTexture(
                new(format, size.X, size.Y, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: name)
            ),
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
