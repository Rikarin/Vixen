// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>
///     Puts a split frame back together: direct light times sun visibility, plus the ambient the
///     shading pass held back, rebuilt from screen planes.
/// </summary>
/// <remarks>
///     <para>
///         The other half of <c>ForwardPlus.SplitOutputs</c>. A split pass writes direct light,
///         emissive and specular ambient to one target and its albedo, normals and <c>f0</c> to three
///         more, precisely so the screen-space passes have something to modulate: this node multiplies
///         an irradiance plane by the albedo, an occlusion plane into that ambient and a
///         sun-visibility channel into the direct term, and adds a traced reflections plane weighted
///         by the surface's own specular reflectance. The formula is one line —
///         <c>direct × sun + albedo × irradiance × occlusion + reflections × reflectance</c> — and
///         <c>AmbientCombine.rvn</c> states every term's stand-in semantics beside it.
///     </para>
///     <para>
///         <strong>Every plane past the first three is optional.</strong> The shader's bindings exist
///         in every variant — a set is written wholly or not at all — so an absent input binds the
///         direct plane as a stand-in and a <c>use…</c> switch at zero makes the term read as "off":
///         occlusion and sun as one, irradiance and reflections as nothing. A document that names no
///         AO node gets unoccluded ambient rather than a frame modulated by whatever the stand-in
///         holds.
///     </para>
///     <para>
///         <strong>Sun visibility multiplies direct and occlusion multiplies ambient</strong> —
///         <see cref="DistanceFieldAoRenderer" />'s own rule, which is why its plane keeps the two in
///         separate channels. Where a shadow map already shadows the sun, the document turns that
///         node's <c>SunShadow</c> off and its channel stays one; the double-application guard is the
///         AO node's knob, not arithmetic here.
///     </para>
/// </remarks>
public sealed class AmbientCombineRenderer() : PostEffectRenderer(
    AmbientCombineKeys.ShaderName,

    // None, on IndirectDiffuse's reasoning: which optional plane is real is a uniform switch, not a
    // permutation — the bindings exist in every variant either way, so keys here would split the
    // cache for variants that compile to the same interface.
    [],
    AmbientCombineKeys.ConstantBufferBinding
) {
    /// <summary>The split pass's first target: direct light, emissive and specular ambient.</summary>
    public required string Direct { get; init; }

    /// <summary>Its second: diffuse albedo, the material's own occlusion in alpha.</summary>
    public required string Albedo { get; init; }

    /// <summary>Its third: world normal in xyz — a zero vector marks a pixel no surface wrote.</summary>
    public required string Normals { get; init; }

    /// <summary>
    ///     Its fourth: specular reflectance at normal incidence — the material's <c>f0</c>. Null
    ///     weighs every surface as a dielectric, which is what this node did before the plane existed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Three channels, and it carries <c>f0</c> rather than metalness because metalness
    ///         cannot be carried.</b> <c>MaterialData</c> keeps a diffuse colour and an <c>f0</c>, and
    ///         the pair is underdetermined; worse, the albedo plane holds <c>base × (1 − metalness)</c>
    ///         and is therefore black at metalness one, so a consumer handed a metalness channel would
    ///         still have no base colour to rebuild a metal's <c>f0</c> from. <c>AmbientCombine.rvn</c>
    ///         has the audit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Naming it is half of one change.</b> With it and <see cref="Reflections" /> both
    ///         named, the shading pass stops writing its own specular ambient
    ///         (<see cref="Lighting" />) and this node adds the traced answer in its place. Naming
    ///         only one of the two leaves the frame exactly as it was, deliberately — see
    ///         <see cref="Configure" />.
    ///     </para>
    /// </remarks>
    public string? Specular { get; set; }

    /// <summary>
    ///     Screen irradiance over π — what <c>!IndirectDiffuse</c> publishes, and what
    ///     <c>!ScreenProbeGather</c>'s upsample publishes. Null contributes no ambient at all.
    /// </summary>
    public string? Irradiance { get; set; }

    /// <summary><c>!DistanceFieldAo</c>'s plane: occlusion in r, sun visibility in g. Null reads both as one.</summary>
    public string? Occlusion { get; set; }

    /// <summary><c>!Ssao</c>'s plane: occlusion in r, a contact-scale term over the field's room-scale one.</summary>
    public string? ContactOcclusion { get; set; }

    /// <summary><c>!Reflections</c>' plane: radiance in rgb, validity in alpha. Null blends none in.</summary>
    /// <remarks>
    ///     ⚠ <b>Validity, and the shader weighs it by the surface's own specular reflectance</b> —
    ///     <c>Ibl.EnvironmentDfg</c> against the normals plane's roughness and the view angle, at a
    ///     dielectric <c>f0</c>. Blending by the plane's alpha alone, which is what this did, is a
    ///     lerp weight of one at every pixel the trace answered: the traced radiance replaced the
    ///     direct term, the rebuilt ambient and so the albedo, and every rough surface in the frame
    ///     became a mirror of whatever the trace saw. Name a <see cref="View" /> — without one the
    ///     weight falls back to normal incidence, which is the modest end of the range.
    /// </remarks>
    public string? Reflections { get; set; }

    /// <summary>
    ///     The scene's lighting, whose <see cref="Lighting.SceneLighting.AmbientSpecular" /> this node owns, or
    ///     null for a host that would rather write it itself.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A compositor node reaching into the frame's lighting, because the question is the
    ///         document's and the answer belongs to the shading pass.</b> Whether a frame has a traced
    ///         reflections plane is a fact about the graph — nothing a material or an environment
    ///         could know — and it decides whether the shading pass keeps its prefiltered specular
    ///         ambient. There is no other object that knows both.
    ///     </para>
    ///     <para>
    ///         Written on every build, which costs an assignment: a document that is rebuilt with the
    ///         reflections node removed has to put the term back.
    ///     </para>
    /// </remarks>
    public Lighting.SceneLighting? Lighting { get; set; }

    /// <summary>A multiplier on the rebuilt ambient alone — the split-mode seat of <c>ambientIntensity</c>.</summary>
    public float Intensity { get; set; } = 1f;

    /// <summary>The full-resolution depth, for the bilateral AO upsample. Null keeps the plain linear read.</summary>
    /// <remarks>
    ///     The AO pair usually arrives at half resolution, and this pass is the one place it meets
    ///     the full-resolution frame — so the upsample lives here rather than in a pass of its own.
    ///     A linear tap of half-res occlusion at a depth edge averages the two surfaces that meet
    ///     there, which reads as occlusion standing a texel or two off the corner that earned it.
    ///     With a depth bound, each AO plane is read as its four nearest texels weighted by
    ///     <c>ScreenProbeUpsample</c>'s plane test instead.
    /// </remarks>
    public string? Depth { get; set; }

    /// <summary>The view whose camera drives the unprojection, or null to set it by hand.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ On <see cref="DistanceFieldAoRenderer.View" />'s terms: with <see cref="Depth" />
    ///         bound and nothing driving the unprojection, every tap's surface is reconstructed at a
    ///         place that exists nowhere, the plane test rejects everything, and the upsample quietly
    ///         falls back to the linear read it was meant to replace.
    ///     </para>
    ///     <para>
    ///         Two consumers now, and the second is <see cref="Reflections" />: the reflection weight
    ///         unprojects this pixel's view ray out of the same matrix, because a dielectric reflects
    ///         four per cent head-on and nearly everything at a grazing angle. Absent a camera the
    ///         shader takes the weight at normal incidence rather than trusting an identity, which is
    ///         a dimmer reflection and never a runaway one.
    ///     </para>
    /// </remarks>
    public RenderView? View { get; set; }

    /// <summary>Clip space back to world space, when no <see cref="View" /> supplies it.</summary>
    public Matrix4x4 InverseViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>How far off a pixel's plane a reduced texel's surface may stand and still count, in metres.</summary>
    public float PlaneTolerance { get; set; } = 0.05f;

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        parameters.Set(AmbientCombineKeys.UseIrradiance, Irradiance is null ? 0f : 1f);
        parameters.Set(AmbientCombineKeys.UseOcclusion, Occlusion is null ? 0f : 1f);
        parameters.Set(AmbientCombineKeys.UseContactOcclusion, ContactOcclusion is null ? 0f : 1f);
        parameters.Set(AmbientCombineKeys.UseSpecular, Specular is null ? 0f : 1f);

        // ⚠ **One condition for both halves, and it is not "is there a reflections plane".** The
        // shader *adds* the traced reflection rather than lerping toward it, which only balances
        // once the shading pass has stopped writing the term the addition replaces — and the pass
        // only stops when the line below tells it to. Split the two and the frame either counts the
        // sky twice or loses every surface's specular ambient.
        //
        // Both required, and a document with reflections but no specular plane therefore draws the
        // frame it drew before: a half-migrated document losing its metals' reflectance is the
        // failure nobody would see, where a document that lost its reflections is one somebody does.
        var replaces = Reflections is not null && Specular is not null;

        parameters.Set(AmbientCombineKeys.UseReflections, replaces ? 1f : 0f);

        if (Lighting is { } lighting) {
            lighting.AmbientSpecular = replaces ? 0f : 1f;
        }

        parameters.Set(AmbientCombineKeys.Intensity, Intensity);

        // The upsample runs when there is a depth to test against and an AO plane worth testing —
        // bilateral taps of the stand-in would be four reads deciding a term the switch discards.
        var bilateral = Depth is not null && (Occlusion is not null || ContactOcclusion is not null);
        parameters.Set(AmbientCombineKeys.UseBilateral, bilateral ? 1f : 0f);
        parameters.Set(AmbientCombineKeys.PlaneTolerance, PlaneTolerance);

        // The document's camera, when one was named — the whole view-projection, on
        // DistanceFieldAoRenderer's terms: the plane test stands in world space.
        if (View is { } camera && Matrix4x4.Invert(camera.ViewProjection, out var unprojection)) {
            InverseViewProjection = unprojection;
        }

        parameters.Set(AmbientCombineKeys.InverseViewProjection, InverseViewProjection);

        // ⚠ And whether that matrix is a camera's, which the reflection weight needs and the plane
        // test does not. The upsample degrades gracefully through an identity — every tap fails the
        // plane test and it falls back to the linear read — but the reflection weight does not: a
        // view direction unprojected through an identity is unrelated to the frame, and the grazing
        // angles it invents are what a *full mirror* looks like. So the shader is told, and with no
        // camera it weighs every surface at normal incidence instead.
        var driven = View is not null || InverseViewProjection != Matrix4x4.Identity;

        parameters.Set(AmbientCombineKeys.UseView, driven ? 1f : 0f);

        // Each AO plane's own texel, measured off the plane the graph actually declared — the
        // upsample has to find that plane's texel centres, and guessing a scale here would break
        // the day a document runs one AO pass at half resolution and the other at full.
        parameters.Set(AmbientCombineKeys.OcclusionTexel, TexelOf(frame, Occlusion));
        parameters.Set(AmbientCombineKeys.ContactTexel, TexelOf(frame, ContactOcclusion));

        // Each resource is declared read once however many bindings it fills: the stand-in below
        // aliases the direct plane into every absent slot, and a pass that declared the same read
        // four times over would be asking the graph a question it already answered.
        HashSet<string> declared = [];

        void Bind(uint binding, string resource) {
            if (declared.Add(resource)) {
                Read(bindings, binding, resource);
            } else {
                bindings.Add(new() { Binding = binding, Kind = DescriptorKind.SampledTexture, Resource = resource });
            }
        }

        Bind(AmbientCombineKeys.DirectBinding, Direct);
        Bind(AmbientCombineKeys.AlbedoBinding, Albedo);
        Bind(AmbientCombineKeys.NormalsBinding, Normals);

        // The direct plane stands in for whatever is absent. Any bound texture would satisfy the
        // set; this one is bound already, and the matching switch above is what actually turns the
        // term off — the stand-in's texels are never part of the answer.
        Bind(AmbientCombineKeys.IrradianceBinding, Irradiance ?? Direct);
        Bind(AmbientCombineKeys.OcclusionBinding, Occlusion ?? Direct);
        Bind(AmbientCombineKeys.ContactOcclusionBinding, ContactOcclusion ?? Direct);
        Bind(AmbientCombineKeys.ReflectionsBinding, Reflections ?? Direct);
        Bind(AmbientCombineKeys.SpecularBinding, Specular ?? Direct);
        Bind(AmbientCombineKeys.DepthBufferBinding, Depth ?? Direct);

        // Point for the planes the split pass wrote at frame resolution — texel for texel — and
        // linear for the ones that may be half resolution, which the AO pair and irradiance are.
        Sample(bindings, AmbientCombineKeys.PointSamplerBinding, Samplers!.PointClamp);
        Sample(bindings, AmbientCombineKeys.LinearSamplerBinding, Samplers!.LinearClamp);
    }

    /// <summary>One texel of the named plane, off the texture the graph actually declared.</summary>
    /// <remarks>
    ///     The frame's own texel where the plane is absent or not yet declared — a harmless answer,
    ///     because both cases are ones the shader's switches keep from being read.
    /// </remarks>
    Vector2 TexelOf(CompositorFrame frame, string? plane) {
        if (plane is null || !frame.Has(plane)) {
            return TexelSize(frame.Size);
        }

        var declared = frame.Graph.DescribeTexture(frame.Texture(ToString(), plane));
        return TexelSize(new(declared.Width, declared.Height));
    }
}
