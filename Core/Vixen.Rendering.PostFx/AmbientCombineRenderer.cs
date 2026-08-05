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
///         emissive and specular ambient to one target and its albedo and normals to two more,
///         precisely so the screen-space passes have something to modulate: this node multiplies an
///         irradiance plane by the albedo, an occlusion plane into that ambient and a sun-visibility
///         channel into the direct term, and blends a traced reflections plane over the result by its
///         own validity. The formula is one line —
///         <c>direct × sun + albedo × irradiance × occlusion</c>, reflections lerped over — and
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
    ///     Screen irradiance over π — what <c>!IndirectDiffuse</c> publishes, and what
    ///     <c>!ScreenProbeGather</c>'s upsample publishes. Null contributes no ambient at all.
    /// </summary>
    public string? Irradiance { get; set; }

    /// <summary><c>!DistanceFieldAo</c>'s plane: occlusion in r, sun visibility in g. Null reads both as one.</summary>
    public string? Occlusion { get; set; }

    /// <summary><c>!Ssao</c>'s plane: occlusion in r, a contact-scale term over the field's room-scale one.</summary>
    public string? ContactOcclusion { get; set; }

    /// <summary><c>!Reflections</c>' plane: radiance in rgb, validity in alpha. Null blends none in.</summary>
    public string? Reflections { get; set; }

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

    /// <summary>The view whose camera drives the plane test's unprojection, or null to set it by hand.</summary>
    /// <remarks>
    ///     ⚠ On <see cref="DistanceFieldAoRenderer.View" />'s terms: with <see cref="Depth" /> bound
    ///     and nothing driving the unprojection, every tap's surface is reconstructed at a place
    ///     that exists nowhere, the plane test rejects everything, and the upsample quietly falls
    ///     back to the linear read it was meant to replace.
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
        parameters.Set(AmbientCombineKeys.UseReflections, Reflections is null ? 0f : 1f);
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
