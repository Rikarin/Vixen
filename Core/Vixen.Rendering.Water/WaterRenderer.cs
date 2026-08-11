// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Lighting;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Water;

/// <summary>
///     The water pass: absorption and scattering integrated over the depth of water, composited once.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D8](../../docs/plan/35-water.md#d8-the-surface-is-a-pass-between-lighting-and-translucency-and-its-reflections-are-l5s),
///         and what <c>overview § 1.9</c> recorded transmission / refraction as waiting for.</b> It
///         runs after deferred lighting and before ordinary translucency, reads a <em>copy</em> of the
///         scene colour and the depth buffer, and writes back into the scene colour.
///     </para>
///     <para>
///         ⚠ <b>The copy is the point, not fussiness.</b> Sampling a target a pass is also writing is
///         undefined — not slow, not approximate — which is why § B1 called the blocker the copy
///         rather than the pass, and why <c>!Copy</c> exists. Name it in <see cref="Behind" /> and
///         put a <c>!Copy</c> node ahead of this one.
///     </para>
///     <para>
///         <b>Integrated, not blended.</b> Alpha blending gives a surface whose opacity is a number
///         somebody typed; absorption over a path length gives one whose colour <em>and</em> opacity
///         are both consequences of how deep it is — a shallow edge clear because the path is short,
///         going green-blue and then black over metres because the long wavelengths go first, from
///         one coefficient triple rather than a gradient somebody painted.
///     </para>
///     <para>
///         ⚠ <b>The reflection plane is doc 19 § L5's, not SSR's, and that is routing rather than
///         compromise.</b> Unreal's water pass classifies tiles specifically so it can run an indirect
///         SSR draw over them, because SSR is what it has. Vixen's SSR is ⬜ and its traced
///         reflections are ✅ — so a lake reflects a mountain that is <em>off screen</em>, which is the
///         single most common reflection failure in every screen-space implementation. Leave
///         <see cref="Reflections" /> empty and the pass compiles the variant without it.
///     </para>
///     <para>
///         ⚠ <b>Its alpha is the waterline mask, not an opacity.</b> § D9's underwater composite reads
///         it per pixel, because a camera straddling the surface needs two treatments in one frame
///         divided by a curve a post-process volume's single per-frame weight cannot express.
///     </para>
/// </remarks>
public sealed class WaterRenderer : SceneRenderer, IDisposable {
    readonly FullScreenRenderer pass;
    readonly ComputeRenderer classify;
    readonly List<ResourceBinding> bindings = [];

    BufferHandle placeholder;
    bool disposed;

    /// <summary>Creates the node.</summary>
    public WaterRenderer() {
        pass = new() {
            Name = WaterKeys.ShaderName,
            ShaderName = WaterKeys.ShaderName,
            PermutationKeys = WaterKeys.UsedPermutationKeys,
            ConstantBinding = WaterKeys.ConstantBufferBinding
        };

        classify = new() {
            Name = WaterTilesKeys.ShaderName,
            ShaderName = WaterTilesKeys.ShaderName,
            ConstantBinding = WaterTilesKeys.ConstantBufferBinding
        };
    }

    /// <summary>The target it writes — the frame's scene colour.</summary>
    /// <remarks>
    ///     ⚠ <b>Written, not blended into.</b> The pass composites the water over what
    ///     <see cref="Behind" /> holds and produces the finished pixel, so a blend state here would
    ///     mix the result with the very thing it already accounted for.
    /// </remarks>
    public required string Output { get; init; }

    /// <summary>The copy of the frame so far, which is what is behind the water.</summary>
    public required string Behind { get; init; }

    /// <summary>The opaque scene's depth, which says how far away what is behind is.</summary>
    public required string SceneDepth { get; init; }

    /// <summary>The surface plane: device depth in <c>r</c>, coverage in <c>g</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Coverage rather than a shading-model id, and § D8 assumed the id.</b> The doc says to
    ///     classify from the G-buffer's shading-model id "which already exists" — it does not; the
    ///     deferred path is ⬜ and this engine is Forward+. So the surface pass writes a one-channel
    ///     mask, which is exactly what an id comparison would have produced, and when the deferred
    ///     path lands this binding becomes that comparison with nothing else changing.
    /// </remarks>
    public required string Surface { get; init; }

    /// <summary>The surface's world normal in <c>xyz</c> and its foam in <c>a</c>.</summary>
    public required string Normal { get; init; }

    /// <summary>Doc 19 § L5's plane — radiance in <c>rgb</c>, validity in <c>a</c>. Optional.</summary>
    public string Reflections { get; init; } = string.Empty;

    /// <summary>The view the camera matrices are derived from, or null to set them by hand.</summary>
    /// <remarks>
    ///     A document has no other way to reach a camera. Without one the pass reconstructs against an
    ///     identity matrix and a camera at the origin — water that is correct for a view nobody is
    ///     looking through, which is <c>SkyRenderer.View</c>'s arrangement and its reason.
    /// </remarks>
    public RenderView? View { get; set; }

    /// <summary>Clip back to world, when no <see cref="View" /> supplies it.</summary>
    public Matrix4x4 InverseViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>Where the camera is, which both distances are measured from.</summary>
    public Vector3 CameraPosition { get; set; }

    /// <summary>How much light the medium scatters out of a ray per metre, per channel.</summary>
    /// <remarks>
    ///     Higher is murkier <em>and</em> brighter: a glacial lake has high blue-green scattering and
    ///     near-zero absorption, which is why it glows.
    /// </remarks>
    public Vector3 Scattering { get; set; } = new(0.03f, 0.06f, 0.09f);

    /// <summary>How much it absorbs per metre, per channel.</summary>
    /// <remarks>
    ///     ⚠ Red goes about thirty times faster than blue, and that is physics rather than an artistic
    ///     choice — it is why everything is blue at ten metres and why a photograph taken with a flash
    ///     down there is not.
    /// </remarks>
    public Vector3 Absorption { get; set; } = new(0.35f, 0.06f, 0.02f);

    /// <summary>Henyey–Greenstein anisotropy. Water is forward-scattering, around 0.7.</summary>
    public float PhaseG { get; set; } = 0.7f;

    /// <summary>§ D8's scale on what is behind the water.</summary>
    /// <remarks>
    ///     Dims what shows through without touching what the volume adds, which is how an author
    ///     darkens a murky river without also darkening its own scattering. One is the physical
    ///     answer.
    /// </remarks>
    public Vector3 BehindScale { get; set; } = Vector3.One;

    /// <summary>The radiance the volume scatters.</summary>
    public Vector3 SunColour { get; set; } = new(1f, 0.96f, 0.9f);

    /// <summary>Which way the light travels.</summary>
    public Vector3 SunDirection { get; set; } = new(0f, -1f, 0f);

    /// <summary>What arrives from the whole sky — the term that makes water blue rather than dark.</summary>
    /// <remarks>
    ///     ⚠ <b>Not decoration, and it was found by measurement rather than reasoned about.</b> A
    ///     phase function is normalised over the sphere, so a single directional light contributes
    ///     almost nothing outside its forward peak — a sea with the sun behind the viewer integrates
    ///     to black, which reads as a hole in the world. The image fixture measured the pass at ten
    ///     depths and watched every channel go to zero; the arithmetic had been right about the term
    ///     it had and silent about the one it did not.
    /// </remarks>
    public Vector3 SkyColour { get; set; } = new(0.35f, 0.45f, 0.6f);

    /// <summary>Water against air.</summary>
    /// <remarks>
    ///     ⚠ Not a base colour. This is what makes a lake a mirror at a grazing angle and nearly
    ///     invisible looking straight down; a material that raises it to tint the water has made it
    ///     metal.
    /// </remarks>
    public float SurfaceF0 { get; set; } = 0.02f;

    /// <summary>What foam is, where the surface plane's alpha says there is some.</summary>
    public Vector3 FoamColour { get; set; } = new(0.9f, 0.93f, 0.95f);

    /// <summary>Whether foam is blended over the result at all.</summary>
    public bool Foam { get; set; } = true;

    /// <summary>
    ///     Whether the pass runs over the screen tiles that have water in them rather than over the
    ///     screen — § D8's tile classification.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>§ D8 keeps this "for its actual reason: the water pass is expensive per pixel and
    ///         covers a small fraction of most frames".</b> On, a compute pass classifies the coverage
    ///         mask into one flag per 8×8 tile and this node draws two triangles per tile with one
    ///         instance each; a tile with no water collapses in the vertex stage and costs no fragment
    ///         at all. Off, it is one triangle over the screen and every pixel of the frame runs the
    ///         fragment shader — most of them only to hand back what was already behind.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It needs <see cref="Pipelines" />, and it is off without one</b> rather than
    ///         throwing: a host that has not given the node a compute cache gets the untiled pass, which
    ///         is the same picture, on <c>!ScreenProbeGather</c>'s terms.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it needs <see cref="Output" /> to already hold what <see cref="Behind" /> is a
    ///         copy of, which is what makes the two paths the same picture.</b> The untiled pass writes
    ///         every pixel of the frame — the scene colour back, with a zero mask in alpha — and the
    ///         tiled one leaves a dry tile exactly as it found it. In a document that is free: § B1's
    ///         <c>!Copy</c> is what filled <see cref="Behind" /> from this very target, so "as it found
    ///         it" and "what is behind" are the same bytes. Wired by hand against a target holding
    ///         something else, the dry pixels keep that something else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What a dry tile no longer writes is the alpha mask</b>, and § D9's composite does
    ///         not read it — <c>Underwater.rvn</c> reads the surface plane's own coverage, for the
    ///         reason its remarks give. The pass's alpha remains the mask everywhere the pass ran.
    ///     </para>
    ///     <para>
    ///         Off by default here and on for a document — see <c>WaterAsset.Tiled</c>. A node somebody
    ///         wired by hand has no <c>!Copy</c> guaranteeing the precondition above; a document has.
    ///     </para>
    /// </remarks>
    public bool Tiled { get; set; }

    /// <summary>Where the classification's compute pipeline comes from. Null turns tiling off.</summary>
    public ComputePipelineCache? Pipelines {
        get => classify.Pipelines;
        set => classify.Pipelines = value;
    }

    /// <summary>How many tiles covered the target the last time this built, or zero when untiled.</summary>
    public Int2 TileCount { get; private set; }

    /// <summary>Where shader modules come from. Set before the first frame that builds.</summary>
    public EffectPipelineDescriber? Modules {
        get => pass.Modules;
        set => pass.Modules = value;
    }

    /// <summary>Where samplers come from, shared so a frame does not exhaust the device's supply.</summary>
    public SamplerCache? Samplers { get; set; }

    /// <summary>Where the pass's descriptor sets come from.</summary>
    public DescriptorAllocator? Allocator {
        get => pass.Descriptors.Allocator;
        set => pass.Descriptors.Allocator = value;
    }

    /// <summary>The device its pipelines and its uniform block are created on.</summary>
    public IGraphicsDevice? Device {
        get => pass.Device;
        set => pass.Device = value;
    }

    /// <summary>The pass this node is, for a host that wants to look at what it did.</summary>
    public FullScreenRenderer Pass => pass;

    /// <summary>And the classification dispatch ahead of it, which runs only when <see cref="Tiled" />.</summary>
    public ComputeRenderer Classification => classify;

    /// <summary>How many frames have declared the pass.</summary>
    public int BuildCount { get; private set; }

    /// <summary>
    ///     Takes <see cref="SunDirection" />, <see cref="SunColour" /> and <see cref="SkyColour" />
    ///     from the frame's own lighting.
    /// </summary>
    /// <param name="lighting">The scene's sun and sky.</param>
    /// <returns>Whether there was a sun to read. False leaves all three where they were.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lighting" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>These three are radiances in the frame's own units, and that is the whole reason
    ///         this method exists rather than a document naming three colours.</b> Everything else in a
    ///         lit frame is physical — a dusk sun is twenty thousand and a sky is thousands — while a
    ///         hand-authored <c>sunColour: 1.0 0.72 0.42</c> is a <em>tint</em>, four decades below the
    ///         scene it is composited into. The volume then integrates correctly to a number the
    ///         exposure resolves as black, and a lake that is four decades too dim is pixel-for-pixel
    ///         the same picture as a water pass that never ran. That is task #119: the fold, the patch
    ///         selection, the info texture, the surface planes and this pass were all correct, and the
    ///         frame had no water in it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The sky term is the environment's mean radiance and not its <c>L00</c>.</b> An SH
    ///         projection of a uniform environment of radiance <c>L</c> has <c>L00 = L·Y₀·4π</c>, so
    ///         handing <c>L00</c> straight over is three and a half times too much sky — which is a
    ///         lake that glows, and is exactly the kind of error that hides inside a number nobody has
    ///         a second source for.
    ///     </para>
    ///     <para>
    ///         Per frame rather than at wire-up, because a level whose sun moves is a lake whose
    ///         scattering peak moves with it. It costs three assignments.
    ///     </para>
    /// </remarks>
    public bool LightFrom(SceneLighting lighting) {
        ArgumentNullException.ThrowIfNull(lighting);

        if (lighting.Sun?.Sun is not { } sun) {
            return false;
        }

        SunDirection = sun.Direction;
        SunColour = sun.Radiance;

        if (lighting.Environment is { } sky) {
            // Y₀ · 4π = 3.5449, so the mean radiance is the coefficient over that — which is the same
            // as multiplying by Y₀. `Intensity` is the artistic scale the ambient term is multiplied
            // by everywhere else, so the water reads the sky the rest of the frame is lit by.
            SkyColour = sky.Irradiance.L00 * (0.282095f * sky.Intensity);
        }

        return true;
    }

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);

        if (string.Equals(Behind, Output, StringComparison.Ordinal)) {
            throw new CompositorBindingException(
                ToString(),
                "target",
                Behind,
                "is both what the water reads and what it writes. Sampling a target a pass is also "
                + "writing is undefined — not slow, not approximate — so the read has to come from a "
                + "copy. Put a !Copy node ahead of this one and name its destination here. See "
                + "docs/plan/35 § B1, which calls the copy the blocker rather than the pass"
            );
        }

        if (Samplers is null || pass.Device is null) {
            // Nothing to run against. A document naming !Water in a host that has not wired the
            // renderer up should cost a frame with no water, not an exception — the same terms
            // !ScreenProbeGather and !SurfaceCache are built on.
            return;
        }

        pass.Samplers = Samplers;
        pass.ColourTargets.Clear();
        pass.Reads.Clear();
        pass.BufferReads.Clear();
        pass.Descriptors.Bindings.Clear();
        bindings.Clear();

        pass.ColourTargets.Add(Output);

        var tiles = Tile(compositor, frame);

        if (View is { } view) {
            CameraPosition = view.Position;

            if (Matrix4x4.Invert(view.ViewProjection, out var inverse)) {
                InverseViewProjection = inverse;
            }
        }

        pass.Parameters.Set(WaterKeys.Reflections, Reflections.Length > 0);
        pass.Parameters.Set(WaterKeys.Foam, Foam);
        pass.Parameters.Set(WaterKeys.Tiled, tiles.Tiled);
        pass.Parameters.Set(WaterKeys.TileCount, tiles.Count);
        pass.Parameters.Set(WaterKeys.TargetSize, tiles.Target);
        pass.Parameters.Set(WaterKeys.InverseViewProjection, InverseViewProjection);
        pass.Parameters.Set(WaterKeys.CameraPosition, CameraPosition);
        pass.Parameters.Set(WaterKeys.Scattering, Scattering);
        pass.Parameters.Set(WaterKeys.Absorption, Absorption);
        pass.Parameters.Set(WaterKeys.PhaseG, PhaseG);
        pass.Parameters.Set(WaterKeys.BehindScale, BehindScale);
        pass.Parameters.Set(WaterKeys.SunColour, SunColour);
        pass.Parameters.Set(WaterKeys.SunDirection, SunDirection);
        pass.Parameters.Set(WaterKeys.SkyColour, SkyColour);
        pass.Parameters.Set(WaterKeys.SurfaceF0, SurfaceF0);
        pass.Parameters.Set(WaterKeys.FoamColour, FoamColour);

        Read(WaterKeys.SceneColourCopyBinding, Behind);
        Read(WaterKeys.SceneDepthBinding, SceneDepth);
        Read(WaterKeys.WaterSurfaceBinding, Surface);
        Read(WaterKeys.WaterNormalBinding, Normal);

        // ⚠ Bound even when the permutation is off, because a set is written wholly or not at all —
        // the same rule !AmbientCombine's stand-in planes follow. What switches the term off is the
        // permutation; what would happen without a binding is a descriptor the driver refuses.
        Read(WaterKeys.ReflectionPlaneBinding, Reflections.Length > 0 ? Reflections : Behind);

        // The tile flags, on exactly those terms: declared and bound whether or not anything filled
        // them, because the untiled variant's set has the slot in it too.
        pass.BufferReads.Add(tiles.Buffer);

        bindings.Add(
            new() {
                Binding = WaterKeys.WaterTilesBinding,
                Kind = DescriptorKind.StorageBuffer,
                Resource = tiles.Buffer
            }
        );

        bindings.Add(
            new() {
                Binding = WaterKeys.PointSamplerBinding,
                Kind = DescriptorKind.Sampler,
                Sampler = Samplers.PointClamp
            }
        );

        bindings.Add(
            new() {
                Binding = WaterKeys.LinearSamplerBinding,
                Kind = DescriptorKind.Sampler,
                Sampler = Samplers.LinearClamp
            }
        );

        foreach (var binding in bindings) {
            pass.Descriptors.Bindings.Add(binding);
        }

        // ⚠ Two triangles per tile and one instance per tile, against a target that is *loaded*. The
        // load is not tidiness: a pass covering part of the screen with LoadAction.DontCare leaves the
        // pixels no instance covered holding whatever the allocator handed over, which on most drivers
        // is the previous frame and reads as smearing rather than as an uninitialised target.
        pass.Vertices = tiles.Tiled ? WaterTiles.VerticesPerTile : 3;
        pass.Instances = tiles.Tiled ? WaterTiles.Total(tiles.Count) : 1;
        pass.Load = tiles.Tiled ? LoadAction.Load : LoadAction.DontCare;

        BuildCount++;

        // The classification first, because the draw reads what it wrote. The graph would order them
        // from the buffer either way; declaring them the other way round would be a reader looking for
        // a producer that has not declared itself yet.
        if (tiles.Tiled) {
            BuildChild(classify, compositor, frame);
        }

        BuildChild(pass, compositor, frame);
    }

    /// <summary>Sizes the tiling, declares its buffer, and points the classifier at both.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The buffer is declared whether or not the classification runs</b>, and it is one
    ///         word wide when it does not. A descriptor set is written wholly or not at all, so the
    ///         untiled variant binds this slot as well — see <c>Water.rvn</c>'s <c>waterTiles</c> — and
    ///         a graph resource is the only thing <see cref="DescriptorBindings" /> can resolve a
    ///         buffer binding against.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sized from the target's own description rather than from the frame's size</b>, on
    ///         <see cref="CompositorFrame.DescriptionOf" />'s own terms: a scaled resource is not the
    ///         frame's extent, and a tiling derived from the wrong
    ///         one covers part of the target and leaves the rest to whatever the last frame put there.
    ///     </para>
    /// </remarks>
    Tiling Tile(GraphicsCompositor compositor, CompositorFrame frame) {
        var described = frame.DescriptionOf(ToString(), Output);
        var target = described is { } size ? new Int2(size.Width, size.Height) : compositor.FrameSize;
        var tiled = Tiled && Pipelines is not null && target is { X: > 0, Y: > 0 };
        var count = tiled ? WaterTiles.CountFor(target) : default;
        var name = $"{this}.Tiles";

        TileCount = count;

        if (!frame.HasBuffer(name)) {
            var description = new BufferDescription(
                WaterTiles.Bytes(count),
                BufferUsage.Storage,
                tiled ? MemoryAccess.DeviceLocal : MemoryAccess.HostUpload,
                name
            );

            // ⚠ Created when something fills it and *imported* when nothing does, and the difference is
            // a graph rule rather than a preference: a pass that reads a transient no earlier pass
            // wrote is refused by name — "the contents it would read are whatever was in that memory
            // last frame" — and the untiled variant binds this slot without ever indexing it. An
            // imported buffer is one whose contents are the host's business, which is exactly the
            // claim being made.
            frame.Add(
                name,
                tiled
                    ? frame.Graph.CreateBuffer(description)
                    : frame.Graph.ImportBuffer(Placeholder(description), description, ResourceState.ShaderRead, ResourceState.ShaderRead),
                description
            );
        }

        if (!tiled) {
            return new(false, default, target, name);
        }

        classify.Groups = new(count.X, count.Y, 1);
        classify.Samplers = Samplers;
        classify.Descriptors.Allocator = pass.Descriptors.Allocator;
        classify.Parameters.Set(WaterTilesKeys.TileCount, count);
        classify.Parameters.Set(WaterTilesKeys.TargetSize, target);

        classify.Reads.Clear();
        classify.BufferWrites.Clear();
        classify.Descriptors.Bindings.Clear();

        classify.Reads.Add(Surface);
        classify.BufferWrites.Add(name);

        classify.Descriptors.Bindings.Add(
            new() {
                Binding = WaterTilesKeys.WaterSurfaceBinding,
                Kind = DescriptorKind.SampledTexture,
                Resource = Surface
            }
        );

        classify.Descriptors.Bindings.Add(
            new() {
                Binding = WaterTilesKeys.PointSamplerBinding,
                Kind = DescriptorKind.Sampler,
                Sampler = Samplers!.PointClamp
            }
        );

        classify.Descriptors.Bindings.Add(
            new() { Binding = WaterTilesKeys.TilesBinding, Kind = DescriptorKind.StorageBuffer, Resource = name }
        );

        return new(true, count, target, name);
    }

    /// <summary>The one word the untiled variant binds and never reads, made once.</summary>
    /// <remarks>
    ///     ⚠ <b>Zeroed, and that is not decoration either.</b> "Nothing reads it" is true of the variant
    ///     compiled today; a buffer of undefined memory is one whose first reader — a permutation
    ///     somebody adds, a driver that reorders — finds tiles claimed at random. Four bytes to make the
    ///     answer "no tiles" instead of "whatever was there".
    /// </remarks>
    BufferHandle Placeholder(in BufferDescription description) {
        if (placeholder.IsValid) {
            return placeholder;
        }

        placeholder = pass.Device!.CreateBuffer(description);
        pass.Device.Write(placeholder, 0, new byte[description.Size]);

        return placeholder;
    }

    /// <summary>What one frame's tiling came to.</summary>
    readonly record struct Tiling(bool Tiled, Int2 Count, Int2 Target, string Buffer);

    /// <summary>Adds a sampled texture to the pass, and records that it is read.</summary>
    /// <remarks>
    ///     The two halves have to happen together: the binding is what the shader reads it through,
    ///     and the read is what orders this pass after whatever wrote it and keeps that producer from
    ///     being culled. One without the other is either a validation error or a race.
    /// </remarks>
    void Read(uint binding, string resource) {
        if (string.IsNullOrEmpty(resource)) {
            return;
        }

        pass.Reads.Add(resource);
        bindings.Add(new() { Binding = binding, Kind = DescriptorKind.SampledTexture, Resource = resource });
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (placeholder.IsValid) {
            pass.Device?.Destroy(placeholder);
            placeholder = default;
        }

        pass.Dispose();
        classify.Dispose();
    }
}
