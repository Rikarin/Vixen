// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
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
    readonly List<ResourceBinding> bindings = [];

    bool disposed;

    /// <summary>Creates the node.</summary>
    public WaterRenderer() {
        pass = new() {
            Name = WaterKeys.ShaderName,
            ShaderName = WaterKeys.ShaderName,
            PermutationKeys = WaterKeys.UsedPermutationKeys,
            ConstantBinding = WaterKeys.ConstantBufferBinding
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

    /// <summary>How many frames have declared the pass.</summary>
    public int BuildCount { get; private set; }

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

        if (View is { } view) {
            CameraPosition = view.Position;

            if (Matrix4x4.Invert(view.ViewProjection, out var inverse)) {
                InverseViewProjection = inverse;
            }
        }

        pass.Parameters.Set(WaterKeys.Reflections, Reflections.Length > 0);
        pass.Parameters.Set(WaterKeys.Foam, Foam);
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

        BuildCount++;
        BuildChild(pass, compositor, frame);
    }

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
        pass.Dispose();
    }
}
