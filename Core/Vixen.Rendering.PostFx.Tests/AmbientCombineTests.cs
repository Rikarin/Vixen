// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using Vixen.Graphics;
using Vixen.Rendering.PostFx;
using Xunit;
using MathVector4 = Vixen.Core.Mathematics.Vector4;

namespace Tests;

/// <summary>
///     The combine's arithmetic, held against hand pixels — and the placement readback decodes
///     the split frame's formats.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Combine" /> is <c>AmbientCombine.rvn</c>'s fragment restated in C#, term for
///         term, so a change to either side fails here rather than shipping as a picture that is
///         plausibly but differently lit. The pixels are hand-computed: a formula checked against
///         itself proves only that it ran.
///     </para>
///     <para>
///         The decoder half covers <c>ScreenProbeGatherRenderer</c>'s readback: the split frame
///         hands placement its real depth attachment (<c>Depth32Float</c>) and the normals target
///         the split pass writes (<c>Rgba16Float</c>), and both must decode to the same numbers the
///         wide formats carry.
///     </para>
/// </remarks>
public class AmbientCombineTests {
    // --- The formula, as the shader states it --------------------------------

    /// <summary>
    ///     <c>direct × sun + albedo × irradiance × occlusion</c>, reflections lerped over by
    ///     validity — <c>AmbientCombine.rvn</c>'s fragment, term for term.
    /// </summary>
    static Vector4 Combine(
        Vector4 direct,
        Vector4 normals,
        Vector4 albedo,
        Vector3 irradiance,
        Vector2 field,
        float contact,
        Vector4 reflections,
        float useIrradiance = 0f,
        float useOcclusion = 0f,
        float useContactOcclusion = 0f,
        float useReflections = 0f,
        float intensity = 1f
    ) {
        var n = new Vector3(normals.X, normals.Y, normals.Z);

        // A pixel no surface wrote passes through untouched.
        if (Vector3.Dot(n, n) < 0.25f) {
            return direct;
        }

        var incoming = irradiance * useIrradiance;
        var open = albedo.W * Lerp(1f, field.X, useOcclusion) * Lerp(1f, contact, useContactOcclusion);
        var sun = Lerp(1f, field.Y, useOcclusion);

        var color = new Vector3(direct.X, direct.Y, direct.Z) * sun
            + new Vector3(albedo.X, albedo.Y, albedo.Z) * incoming * open * intensity;

        var validity = Math.Clamp(reflections.W, 0f, 1f) * useReflections;
        color = Vector3.Lerp(color, new(reflections.X, reflections.Y, reflections.Z), validity);

        return new(color, direct.W);
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    static readonly Vector4 Surface = new(0f, 0f, 1f, 0.5f);

    /// <summary>With nothing named, the combine is the identity over the direct plane.</summary>
    /// <remarks>
    ///     The stand-in semantics, all four at once: every switch at zero reads occlusion and sun as
    ///     one, irradiance and reflections as nothing — whatever texels the stand-ins hold, which is
    ///     the point, because the stand-ins are the direct plane itself.
    /// </remarks>
    [Fact]
    public void Every_switch_at_zero_is_the_identity() {
        var direct = new Vector4(0.5f, 0.25f, 0.125f, 1f);

        // Garbage in every optional plane, deliberately.
        var result = Combine(direct, Surface, new(0.8f, 0.6f, 0.4f, 0.5f), new(9f), new(9f, 9f), 9f, new(9f));

        Assert.Equal(direct, result);
    }

    /// <summary>A pixel the normals plane never wrote passes through whatever is switched on.</summary>
    [Fact]
    public void A_skyward_pixel_passes_through() {
        var direct = new Vector4(0.5f, 0.25f, 0.125f, 1f);

        var result = Combine(
            direct,
            normals: Vector4.Zero,
            new(0.8f, 0.6f, 0.4f, 1f),
            new(0.2f),
            new(0.5f, 0.5f),
            0.8f,
            new(1f, 0f, 0f, 1f),
            useIrradiance: 1f,
            useOcclusion: 1f,
            useContactOcclusion: 1f,
            useReflections: 1f
        );

        Assert.Equal(direct, result);
    }

    /// <summary>One hand pixel through every term at once.</summary>
    /// <remarks>
    ///     By hand: open = 0.5 × 0.5 × 0.8 = 0.2, sun = 0.5;
    ///     ambient = albedo × irradiance × open × intensity = (0.064, 0.048, 0.064);
    ///     direct × sun = (0.25, 0.125, 0.0625); summed = (0.314, 0.173, 0.1265);
    ///     reflections at validity 0.25 pull a quarter of the way to red.
    /// </remarks>
    [Fact]
    public void One_pixel_through_every_term() {
        var result = Combine(
            new(0.5f, 0.25f, 0.125f, 1f),
            Surface,
            new(0.8f, 0.6f, 0.4f, 0.5f),
            new(0.2f, 0.2f, 0.4f),
            new(0.5f, 0.5f),
            0.8f,
            new(1f, 0f, 0f, 0.25f),
            useIrradiance: 1f,
            useOcclusion: 1f,
            useContactOcclusion: 1f,
            useReflections: 1f,
            intensity: 2f
        );

        Assert.Equal(0.75f * 0.314f + 0.25f, result.X, 1e-5f);
        Assert.Equal(0.75f * 0.173f, result.Y, 1e-5f);
        Assert.Equal(0.75f * 0.1265f, result.Z, 1e-5f);
        Assert.Equal(1f, result.W, 1e-5f);
    }

    /// <summary>Sun visibility multiplies direct only, and occlusion multiplies ambient only.</summary>
    /// <remarks>
    ///     <c>!DistanceFieldAo</c>'s channel rule, asserted from the consuming side: zero the sun and
    ///     direct light vanishes while ambient stands; zero the occlusion instead and the ambient
    ///     vanishes while direct light stands. Pre-combining the two channels would fail both halves.
    /// </remarks>
    [Fact]
    public void Sun_and_occlusion_reach_different_terms() {
        var direct = new Vector4(0.5f, 0.5f, 0.5f, 1f);
        var albedo = new Vector4(1f, 1f, 1f, 1f);
        var irradiance = new Vector3(0.25f);

        var shadowed = Combine(
            direct, Surface, albedo, irradiance, new(1f, 0f), 1f, default, useIrradiance: 1f, useOcclusion: 1f
        );

        var occluded = Combine(
            direct, Surface, albedo, irradiance, new(0f, 1f), 1f, default, useIrradiance: 1f, useOcclusion: 1f
        );

        // Sun at zero: direct vanishes and the ambient quarter stands.
        Assert.Equal(0.25f, shadowed.X, 5);

        // Occlusion at zero: ambient vanishes and the direct half stands.
        Assert.Equal(0.5f, occluded.X, 5);
    }

    // --- The bilateral upsample, as the shader states it ---------------------

    /// <summary>One tap of a reduced plane: what it holds, and where its surface stood.</summary>
    /// <param name="Value">The plane's <c>rg</c> at that texel.</param>
    /// <param name="World">The surface the AO pass measured there.</param>
    /// <param name="Coverage">Its share of the bilinear blend.</param>
    readonly record struct Tap(Vector2 Value, Vector3 World, float Coverage);

    /// <summary>
    ///     <c>AmbientCombine.Upsample</c>'s tap rule, restated: bilinear coverage, the plane test,
    ///     renormalised over what survives, and the linear blend where nothing does.
    /// </summary>
    /// <remarks>
    ///     The placement arithmetic — which four texels a UV lands between — is the shader's and is
    ///     ordinary bilinear addressing. What is worth holding in C# is the part that is not
    ///     ordinary: which taps are allowed to count, and what happens when none are.
    /// </remarks>
    static Vector2 Upsample(
        IReadOnlyList<Tap> taps,
        Vector3 world,
        Vector3 normal,
        float planeTolerance,
        Vector2 linearFallback
    ) {
        var kept = Vector2.Zero;
        var keptWeight = 0f;

        foreach (var tap in taps) {
            if (tap.Coverage <= 0f) {
                continue;
            }

            // A texel whose surface does not lie on this pixel's plane measured a different
            // surface, and blending it in is how half-res occlusion bleeds across a depth edge.
            if (MathF.Abs(Vector3.Dot(world - tap.World, normal)) > planeTolerance) {
                continue;
            }

            kept += tap.Value * tap.Coverage;
            keptWeight += tap.Coverage;
        }

        // Every texel rejected: the plain blend, on the probe upsample's dilation reasoning — a
        // smeared answer beats a hole.
        return keptWeight <= 0f ? linearFallback : kept / keptWeight;
    }

    static readonly Vector3 Up = new(0f, 1f, 0f);

    /// <summary>On one flat surface every tap survives, and the upsample is the bilinear blend.</summary>
    /// <remarks>
    ///     The property that keeps the bilateral path from being a regression away from a depth
    ///     edge: open floor is where most of the frame is, and the filter must not change it.
    /// </remarks>
    [Fact]
    public void Away_from_an_edge_the_upsample_is_the_plain_blend() {
        var taps = new Tap[] {
            new(new(1f, 1f), Vector3.Zero, 0.25f),
            new(new(0.5f, 1f), new(0.1f, 0f, 0f), 0.25f),
            new(new(0.5f, 1f), new(0f, 0f, 0.1f), 0.25f),
            new(new(0f, 1f), new(0.1f, 0f, 0.1f), 0.25f)
        };

        var upsampled = Upsample(taps, Vector3.Zero, Up, 0.05f, new(-1f, -1f));

        // The four texels' mean, and not the fallback — every one of them stands on the floor.
        Assert.Equal(0.5f, upsampled.X, 5);
        Assert.Equal(1f, upsampled.Y, 5);
    }

    /// <summary>
    ///     At a corner the taps that measured the other surface drop out, and the answer is the
    ///     ones that stood where this pixel stands.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is the artefact the upsample exists for. Half-resolution occlusion linearly
    ///     sampled at a wall-floor corner averages the floor's texels with the wall's, so the dark
    ///     contact reads a texel or two away from the crease that earned it — occlusion visibly
    ///     offset from the geometry, with an unoccluded strip left at the corner itself. Weighting
    ///     the taps by the plane test is what pins it back to the surface.
    /// </remarks>
    [Fact]
    public void At_a_depth_edge_the_taps_across_it_drop_out() {
        // Two texels on this pixel's floor, two on a surface half a metre above it.
        var taps = new Tap[] {
            new(new(0.2f, 1f), Vector3.Zero, 0.25f),
            new(new(0.2f, 1f), new(0.1f, 0f, 0f), 0.25f),
            new(new(1f, 1f), new(0f, 0.5f, 0f), 0.25f),
            new(new(1f, 1f), new(0.1f, 0.5f, 0f), 0.25f)
        };

        var upsampled = Upsample(taps, Vector3.Zero, Up, 0.05f, new(-1f, -1f));

        // The floor's own occlusion, undiluted by the two texels that measured something else —
        // where the plain blend would have read 0.6 and lit the corner it should have darkened.
        Assert.Equal(0.2f, upsampled.X, 5);

        var smeared = (0.2f + 0.2f + 1f + 1f) / 4f;
        Assert.True(smeared > upsampled.X, $"the blend ({smeared}) was not brighter than the filtered ({upsampled.X})");
    }

    /// <summary>A pixel every tap rejects takes the linear blend rather than a hole.</summary>
    /// <remarks>
    ///     <c>ScreenProbeUpsample</c>'s dilation rule, and the reason the fallback is not zero: a
    ///     thin surface no reduced texel landed on is common — a railing, a wire — and answering
    ///     "fully occluded" there is a black outline drawn along every one of them.
    /// </remarks>
    [Fact]
    public void A_pixel_no_tap_agrees_with_falls_back_to_the_blend() {
        var taps = new Tap[] {
            new(new(0f, 0f), new(0f, 2f, 0f), 0.25f),
            new(new(0f, 0f), new(0f, 3f, 0f), 0.25f),
            new(new(0f, 0f), new(0f, 4f, 0f), 0.25f),
            new(new(0f, 0f), new(0f, 5f, 0f), 0.25f)
        };

        var upsampled = Upsample(taps, Vector3.Zero, Up, 0.05f, new(0.75f, 1f));

        Assert.Equal(0.75f, upsampled.X, 5);
        Assert.Equal(1f, upsampled.Y, 5);
    }

    /// <summary>A tolerance wide enough to accept everything is the plain blend again.</summary>
    /// <remarks>
    ///     The knob's off end, and the compare composition against the version before this: a
    ///     document that wants the old behaviour writes a tolerance nothing fails.
    /// </remarks>
    [Fact]
    public void A_tolerance_nothing_fails_is_the_blend_it_replaced() {
        var taps = new Tap[] {
            new(new(0.2f, 1f), Vector3.Zero, 0.25f),
            new(new(0.2f, 1f), new(0.1f, 0f, 0f), 0.25f),
            new(new(1f, 1f), new(0f, 0.5f, 0f), 0.25f),
            new(new(1f, 1f), new(0.1f, 0.5f, 0f), 0.25f)
        };

        var upsampled = Upsample(taps, Vector3.Zero, Up, 1000f, new(-1f, -1f));

        Assert.Equal(0.6f, upsampled.X, 5);
    }

    // --- The placement readback decodes the split frame's formats ------------

    [Theory]
    [InlineData(PixelFormat.Rgba32Float, 16)]
    [InlineData(PixelFormat.Rgba16Float, 8)]
    [InlineData(PixelFormat.Rgba8UNorm, 4)]
    [InlineData(PixelFormat.Depth32Float, 4)]
    public void Placement_knows_how_wide_a_readable_texel_is(PixelFormat format, long expected) {
        using var gather = new ScreenProbeGatherRenderer { Depth = "SceneDepth", Normals = "SceneNormals" };

        Assert.Equal(expected, gather.BytesPerPixel(format, "SceneDepth"));
    }

    [Fact]
    public void A_format_placement_cannot_decode_is_refused_by_name() {
        using var gather = new ScreenProbeGatherRenderer { Depth = "SceneDepth", Normals = "SceneNormals" };

        var refusal = Assert.Throws<Vixen.Rendering.Compositor.CompositorBindingException>(
            () => gather.BytesPerPixel(PixelFormat.Rg16Float, "SceneDepth")
        );

        Assert.Contains("Rg16Float", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A depth attachment reads back as itself: one float per texel, no channels.</summary>
    [Fact]
    public void Depth32Float_decodes_one_float_per_texel() {
        float[] values = [0f, 0.25f, 0.5f, 1f];
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);

        var depth = new float[values.Length];
        ScreenProbeGatherRenderer.DecodeDepth(bytes, PixelFormat.Depth32Float, depth);

        Assert.Equal(values, depth);
    }

    /// <summary>Half floats decode by value — the first channel for depth, all four for normals.</summary>
    [Fact]
    public void Rgba16Float_decodes_by_value() {
        Half[] texels = [
            (Half)0.75f, (Half)0f, (Half)0f, (Half)0f,
            (Half)(-0.5f), (Half)0.25f, (Half)1f, (Half)0.125f
        ];

        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes<Half>(texels).ToArray();

        var depth = new float[2];
        ScreenProbeGatherRenderer.DecodeDepth(bytes, PixelFormat.Rgba16Float, depth);

        Assert.Equal([0.75f, -0.5f], depth);

        var normals = new MathVector4[2];
        ScreenProbeGatherRenderer.DecodeNormals(bytes, PixelFormat.Rgba16Float, normals);

        Assert.Equal(new MathVector4(0.75f, 0f, 0f, 0f), normals[0]);

        // Signed values survive, which the unorm path could never say: the split pass stores the
        // shading normal raw, not remapped to 0..1.
        Assert.Equal(new MathVector4(-0.5f, 0.25f, 1f, 0.125f), normals[1]);
    }

    /// <summary>The unorm path still rescales — the byte is the encoding, not the number.</summary>
    [Fact]
    public void Rgba8UNorm_still_decodes_to_unit_range() {
        byte[] bytes = [255, 0, 128, 64];

        var depth = new float[1];
        ScreenProbeGatherRenderer.DecodeDepth(bytes, PixelFormat.Rgba8UNorm, depth);

        Assert.Equal(1f, depth[0]);

        var normals = new MathVector4[1];
        ScreenProbeGatherRenderer.DecodeNormals(bytes, PixelFormat.Rgba8UNorm, normals);

        Assert.Equal(new MathVector4(1f, 0f, 128f / 255f, 64f / 255f), normals[0]);
    }
}
