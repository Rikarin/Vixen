// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.ScreenProbes.Tests;

/// <summary>The nearest pyramid, and the march that skips by it.</summary>
public class ScreenDepthPyramidTests {
    /// <summary>Top-down orthographic: ndc.x = x/4, ndc.y = z/4, device depth = (y+4)/8.</summary>
    static readonly Matrix4x4 Camera = new(
        new Vector4(0.25f, 0f, 0f, 0f),
        new Vector4(0f, 0f, 0.125f, 0f),
        new Vector4(0f, 0.25f, 0f, 0f),
        new Vector4(0f, 0f, 0.5f, 1f)
    );

    [Fact]
    public void TheReductionKeepsTheNearestAndSkySinks() {
        // Reversed depth: the nearest surface is the MAXIMUM, and sky (zero) never wins a cell.
        var pyramid = new ScreenDepthPyramid(new(5, 3));

        Assert.Equal(4, pyramid.Levels);
        Assert.Equal(new Int2(5, 3), pyramid.SizeOf(0));
        Assert.Equal(new Int2(3, 2), pyramid.SizeOf(1));
        Assert.Equal(new Int2(2, 1), pyramid.SizeOf(2));
        Assert.Equal(new Int2(1, 1), pyramid.SizeOf(3));

        var depth = new float[5 * 3];

        depth[(1 * 5) + 1] = 0.75f;
        depth[(0 * 5) + 4] = 0.5f;

        pyramid.Build(depth);

        Assert.Equal(0.75f, pyramid.Nearest(1, new(0, 0)));
        Assert.Equal(0.5f, pyramid.Nearest(1, new(2, 0)));
        Assert.Equal(0f, pyramid.Nearest(1, new(1, 0)));
        Assert.Equal(0.75f, pyramid.Nearest(3, new(0, 0)));
    }

    [Fact]
    public void TheHierarchicalMarchAgreesWithTheNaiveOne() {
        // A floor, a ceiling patch, and a strip of sky — the fixture the naive march defines, run
        // through both marches over a grid of rays: same hits, same pixels, same misses.
        var surface = Screen(out var pyramid);
        var trace = new ScreenSpaceTrace(surface) { ViewProjection = Camera, Steps = 64, Thickness = 0.05f };

        var checked_ = 0;
        var hits = 0;

        for (var x = -3; x <= 3; x++) {
            for (var z = -3; z <= 3; z++) {
                foreach (var direction in Directions()) {
                    var origin = new Vector3(x, 0.01f, z);

                    trace.Pyramid = null;

                    var naive = trace.TryHit(origin, direction, 8f, out var naivePixel);

                    trace.Pyramid = pyramid;

                    var fast = trace.TryHit(origin, direction, 8f, out var fastPixel);

                    // The continuous test can only find MORE than the sampled one — a shell the
                    // fixed steps straddle. On this fixture's thick shells the two must agree, and
                    // where both hit they must name the same pixel.
                    Assert.True(
                        naive == fast || (!naive && fast),
                        $"naive hit and the pyramid missed at {origin} along {direction}"
                    );

                    if (naive && fast) {
                        Assert.Equal(naivePixel, fastPixel);
                        hits++;
                    }

                    checked_++;
                }
            }
        }

        Assert.True(hits > 20, $"only {hits} of {checked_} rays hit — the fixture referees too little");
    }

    [Fact]
    public void TheSkipIsMeasuredNotClaimed() {
        // A big, almost-empty screen: one far corner holds a surface, and a long ray across the
        // sky must cost the pyramid a handful of fetches where the naive march pays per step.
        var surface = new ReconstructedScreenSurface(new(64, 64));
        var pyramid = new ScreenDepthPyramid(new(64, 64));

        surface.Depth[(63 * 64) + 63] = 0.9f;
        pyramid.Build(surface.Depth);

        var trace = new ScreenSpaceTrace(surface) { ViewProjection = Camera, Steps = 64, Thickness = 0.05f };
        var origin = new Vector3(-3.5f, 1f, -3.5f);
        var direction = Vector3.Normalize(new(1f, 0f, 0.5f));

        Assert.False(trace.TryHit(origin, direction, 7f, out _));

        var naive = trace.Samples;

        trace.Pyramid = pyramid;

        Assert.False(trace.TryHit(origin, direction, 7f, out _));

        Assert.True(
            trace.Samples * 4 < naive,
            $"the pyramid fetched {trace.Samples} against the naive {naive} — the skip is not skipping"
        );
    }

    [Fact]
    public void APerspectiveCameraDeclinesToThePlainWalk() {
        // A w that varies along the ray: the pyramid is set and deliberately unused — the sample
        // count is the naive walk's, which is how the decline is observable at all.
        var surface = Screen(out var pyramid);

        var perspective = new Matrix4x4(
            new Vector4(0.25f, 0f, 0f, 0.05f),
            new Vector4(0f, 0f, 0.125f, 0f),
            new Vector4(0f, 0.25f, 0f, 0f),
            new Vector4(0f, 0f, 0.5f, 1f)
        );

        var trace = new ScreenSpaceTrace(surface) {
            ViewProjection = perspective, Steps = 48, Thickness = 0.05f, Pyramid = pyramid
        };

        trace.TryHit(new(-2f, 0.01f, 0f), Vector3.Normalize(new(1f, 0.2f, 0f)), 6f, out _);

        Assert.True(
            trace.Samples >= 40,
            $"{trace.Samples} fetches — the perspective ray went through the pyramid it must decline"
        );
    }

    /// <summary>The device fixtures' screen: floor at 0.5, a ceiling patch at 0.75, a sky strip.</summary>
    static ReconstructedScreenSurface Screen(out ScreenDepthPyramid pyramid) {
        var surface = new ReconstructedScreenSurface(new(8, 8));

        for (var y = 0; y < 8; y++) {
            for (var x = 0; x < 8; x++) {
                var at = (y * 8) + x;

                surface.Depth[at] = x == 7 ? 0f : x is >= 3 and <= 5 && y is >= 2 and <= 5 ? 0.75f : 0.5f;
            }
        }

        pyramid = new(new(8, 8));
        pyramid.Build(surface.Depth);

        return surface;
    }

    static Vector3[] Directions() => [
        Vector3.Normalize(new(1f, 1f, 0.2f)),
        Vector3.Normalize(new(-0.5f, 1f, 0.7f)),
        Vector3.Normalize(new(0.3f, 0.6f, -1f)),
        Vector3.Normalize(new(1f, 0.05f, 0.1f))
    ];
}
