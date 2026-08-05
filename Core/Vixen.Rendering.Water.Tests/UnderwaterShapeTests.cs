// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Water;
using Vixen.Water;
using Xunit;

namespace Tests;

/// <summary>
///     Underwater as a post-process volume's shape — [docs/plan/35 § D9], and § B2's consumer.
/// </summary>
/// <remarks>
///     <para>
///         The whole of what § D9 calls "the whole feature": doc 32 built the priority, the blend
///         radius and the optional fields, and water needs a non-box containment test and nothing
///         else. What it does <em>not</em> answer is the waterline, which is a per-pixel mask and is
///         separated deliberately.
///     </para>
///     <para>
///         ⚠ <b>The surface it tests against is the drawn one, waves included.</b> Against the rest
///         height the boundary would be at mean sea level, and a camera sitting in a swell would cross
///         it half a second before and after the water actually reached it.
///     </para>
/// </remarks>
public sealed class UnderwaterShapeTests {
    static WaterZoneState Lake(float surface = 10f, float depth = 8f, float falloff = 2f) {
        var state = new WaterZoneState(
            WaterZone.Default with { Extent = 256f, Resolution = 129, CoarsestTexel = 0f }
        );

        var spline = new Spline(
            Spline.SmoothTangents(
                [
                    new(-40f, surface, -40f),
                    new(40f, surface, -40f),
                    new(40f, surface, 40f),
                    new(-40f, surface, 40f)
                ],
                closed: true,
                tension: 1f
            ),
            closed: true
        );

        var lake = new WaterBody(WaterBodyKind.Lake, spline, defaults: new() { Depth = depth }) {
            SurfaceHeight = surface,
            ShoreFalloff = falloff,
            BedRamp = 4f
        };

        state.SetBodies([lake]);
        state.Update(Vector2.Zero, new FlatWaterGround(surface - depth));

        return state;
    }

    /// <summary>A still sea, so the tests are about the shape and not about a wave.</summary>
    static WaterWaveSpectrum Still => WaterWaveSpectrum.Default with { AmplitudeScale = 0f };

    [Fact]
    public void A_point_under_the_surface_and_inside_the_body_is_inside() {
        var shape = new UnderwaterShape(Lake(), Still, 0f);

        Assert.True(shape.Contains(new(0f, 6f, 0f), out var outside));
        Assert.Equal(0f, outside, 4);
    }

    [Fact]
    public void A_point_above_the_surface_is_outside_by_how_far_above() {
        var shape = new UnderwaterShape(Lake(), Still, 0f);

        Assert.False(shape.Contains(new(0f, 13f, 0f), out var outside));
        Assert.Equal(3f, outside, 1);
    }

    /// <summary>
    ///     A point on dry land is outside however deep it is, which is the whole reason for a shape.
    /// </summary>
    /// <remarks>
    ///     ⚠ A box volume around the lake would grade a rectangle — including the beach beside it and
    ///     the cellar under it — while the inspector looked exactly right. That is
    ///     [§ B2](../../docs/plan/35-water.md#b2-doc-32s-volumes-are-boxes)'s failure case, and it is
    ///     what this replaces.
    /// </remarks>
    [Fact]
    public void A_point_beside_the_lake_is_outside_however_low_it_is() {
        var shape = new UnderwaterShape(Lake(), Still, 0f);

        Assert.False(shape.Contains(new(80f, -50f, 80f), out var outside));
        Assert.True(outside > 0f, "dry ground read as underwater.");
    }

    /// <summary>The fade is a fade, so the grade comes on across the shoreline rather than at it.</summary>
    /// <remarks>
    ///     ⚠ <b>The falloff here is eight metres and the fixture's texels are two, which is the
    ///     constraint rather than a convenience.</b> A shoreline band narrower than a few texels cannot
    ///     be resolved however smooth the arithmetic is — the field is what carries the ramp — so the
    ///     same lake with a two-metre falloff really would read as a cut, and the panel's
    ///     metres-per-texel readout is what an author has to check it against.
    /// </remarks>
    [Fact]
    public void The_shoreline_reads_as_a_fade_rather_than_a_cut() {
        var shape = new UnderwaterShape(Lake(falloff: 8f), Still, 0f);

        var previous = 0f;
        var distinct = 0;

        // Walking out from the middle of the lake, the outside distance rises smoothly rather than
        // stepping from zero to the feather in one texel.
        for (var x = 0f; x <= 48f; x += 0.5f) {
            shape.Contains(new(x, 6f, 0f), out var outside);

            Assert.True(outside >= previous - 1e-4f, $"the fade went backwards at x = {x}.");

            if (outside > 0f && outside < 2f) {
                distinct++;
            }

            previous = outside;
        }

        Assert.True(distinct >= 3, "the shoreline was a cut rather than a ramp.");
    }

    /// <summary>The waves move the boundary, which is why the shape is rebuilt per frame.</summary>
    /// <remarks>
    ///     ⚠ A test against the rest height would put the boundary at mean sea level. A camera at the
    ///     crest height is underwater under a crest and not under a trough, and it is the same camera.
    /// </remarks>
    [Fact]
    public void The_boundary_moves_with_the_waves() {
        var state = Lake();
        var swell = WaterWaveSpectrum.Default with { WindSpeed = 20f, MaximumWavelength = 120f };

        var wet = 0;
        var dry = 0;

        // Just above the rest surface: whether it is underwater depends on the wave passing.
        for (var index = 0; index < 200; index++) {
            var shape = new UnderwaterShape(state, swell, index * 0.05f);

            if (shape.Contains(new(0f, 10.2f, 0f), out _)) {
                wet++;
            } else {
                dry++;
            }
        }

        Assert.True(wet > 0 && dry > 0, $"the boundary never moved: {wet} wet, {dry} dry.");
    }

    /// <summary>A zone with no field answers nothing rather than everything.</summary>
    /// <remarks>
    ///     <c>PostProcessVolumeSystem.Reach</c>'s stated behaviour: a custom volume nothing resolves
    ///     reaches nothing. The alternative — falling back to the box — is the failure above.
    /// </remarks>
    [Fact]
    public void A_zone_that_has_not_rasterised_contains_nothing() {
        var shape = new UnderwaterShape(new WaterZoneState(WaterZone.Default), Still, 0f);

        Assert.False(shape.Contains(Vector3.Zero, out var outside));
        Assert.True(outside > 0f);
    }
}
