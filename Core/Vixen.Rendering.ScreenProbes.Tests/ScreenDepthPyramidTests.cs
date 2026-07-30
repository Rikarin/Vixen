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

    /// <summary>The same camera with a perspective w — clip w is 1 + x/20, off one wherever x is.</summary>
    static readonly Matrix4x4 Perspective = new(
        new Vector4(0.25f, 0f, 0f, 0.05f),
        new Vector4(0f, 0f, 0.125f, 0f),
        new Vector4(0f, 0.25f, 0f, 0f),
        new Vector4(0f, 0f, 0.5f, 1f)
    );

    /// <summary>A camera whose arithmetic a closed form can do by hand: looking down +Z from the
    ///     origin, ndc.x = x/z, ndc.y = y/z, device depth = 1/z — reversed, near plane at one.</summary>
    static readonly Matrix4x4 Forward = new(
        new Vector4(1f, 0f, 0f, 0f),
        new Vector4(0f, 1f, 0f, 0f),
        new Vector4(0f, 0f, 0f, 1f),
        new Vector4(0f, 0f, 1f, 0f)
    );

    [Fact]
    public void TheReductionKeepsTheNearestAndSkySinks() {
        // Reversed depth: the nearest surface is the MAXIMUM, and sky (zero) never wins a cell.
        // Sizes halve the way device mips halve — flooring — which is the whole reason the CPU
        // pyramid and the kernel's mip chain can hold the same texels.
        var pyramid = new ScreenDepthPyramid(new(8, 3));

        Assert.Equal(4, pyramid.Levels);
        Assert.Equal(new Int2(8, 3), pyramid.SizeOf(0));
        Assert.Equal(new Int2(4, 1), pyramid.SizeOf(1));
        Assert.Equal(new Int2(2, 1), pyramid.SizeOf(2));
        Assert.Equal(new Int2(1, 1), pyramid.SizeOf(3));

        var depth = new float[8 * 3];

        depth[(1 * 8) + 1] = 0.75f;
        depth[(0 * 8) + 6] = 0.5f;

        pyramid.Build(depth);

        Assert.Equal(0.75f, pyramid.Nearest(1, new(0, 0)));
        Assert.Equal(0f, pyramid.Nearest(1, new(1, 0)));
        Assert.Equal(0.5f, pyramid.Nearest(1, new(2, 0)));

        // The clamped three-by-three ring: the trailing column a two-by-two block would drop shows
        // up in the last cell too, so no surface can hide from the reduction at an odd edge.
        Assert.Equal(0.5f, pyramid.Nearest(1, new(3, 0)));

        Assert.Equal(0.5f, pyramid.Nearest(2, new(1, 0)));
        Assert.Equal(0.75f, pyramid.Nearest(3, new(0, 0)));
    }

    [Fact]
    public void TheHierarchicalMarchAgreesWithTheNaiveOne() {
        // A floor, a ceiling patch, and a strip of sky — the fixture the naive march defines, run
        // through both marches over a grid of rays: same hits, same pixels, same misses.
        var surface = Screen(out var pyramid);
        var trace = new ScreenSpaceTrace(surface) { ViewProjection = Camera, Steps = 64, Thickness = 0.05f };

        AssertAgreement(trace, pyramid, exactPixels: true);
    }

    [Fact]
    public void APerspectiveRayMarchesThePyramidAndAgreesToo() {
        // The march that used to decline: screen position and device depth are both linear in the
        // projected segment's parameter whatever the camera, so the same DDA runs — and the same
        // grid of rays must find the same surfaces the fixed steps find. Pixels may differ by one:
        // the continuous test resolves a shell crossing in the first texel it touches, where the
        // fixed steps name whichever texel their first in-shell sample happened to land in.
        var surface = Screen(out var pyramid);
        var trace = new ScreenSpaceTrace(surface) { ViewProjection = Perspective, Steps = 64, Thickness = 0.05f };

        AssertAgreement(trace, pyramid, exactPixels: false);
    }

    [Fact]
    public void TheSkipIsMeasuredNotClaimed() {
        Skip(Camera, new(-3.5f, 1f, -3.5f));
    }

    [Fact]
    public void ThePerspectiveSkipIsMeasuredToo() {
        // The whole point of the perspective form: the ray that once fell back to paying per step
        // now skips the empty screen at the coarse levels. The origin sits where the perspective
        // divide still projects it onto the viewport.
        Skip(Perspective, new(-3.2f, 1f, -3.2f));
    }

    [Fact]
    public void AnEndpointBehindTheCameraDeclinesToTheWalk() {
        // A ray leaving through the camera's plane has no projected segment to march — the pyramid
        // is set and deliberately unused, observable as the naive walk's own fetch count.
        var surface = new ReconstructedScreenSurface(new(8, 8));

        surface.Depth.Fill(0.25f);

        var pyramid = new ScreenDepthPyramid(new(8, 8));

        pyramid.Build(surface.Depth);

        var trace = new ScreenSpaceTrace(surface) { ViewProjection = Forward, Steps = 32, Thickness = 0.02f };
        var origin = new Vector3(0f, 0f, 3f);
        var direction = new Vector3(0f, 0f, -1f);

        Assert.False(trace.TryHit(origin, direction, 5f, out _));

        var naive = trace.Samples;

        trace.Pyramid = pyramid;

        Assert.False(trace.TryHit(origin, direction, 5f, out _));
        Assert.True(naive > 0, "the walk never sampled — the fixture referees nothing");
        Assert.Equal(naive, trace.Samples);
    }

    [Fact]
    public void TheLinearShellPassesBehindWhatTheDeviceShellCannot() {
        // One wall at view depth four (device 0.25), one ray flying past two-tenths behind it. In
        // device units the shell under `Forward` is 0.02 deep, which is view depth four to 4.35 —
        // the ray is "inside" a wall it passes a fifth of a unit behind. In view units the shell is
        // what it says: a tenth, and the ray passes. The same fixture through both marches.
        var surface = new ReconstructedScreenSurface(new(8, 8));

        surface.Depth.Fill(0.25f);

        var pyramid = new ScreenDepthPyramid(new(8, 8));

        pyramid.Build(surface.Depth);

        // View depth 4.28 falling to 4.18 across the screen: always behind the wall, never within
        // a tenth of it.
        var origin = new Vector3(-2.5f, 0f, 4.28f);
        var direction = Vector3.Normalize(new(1f, 0f, -0.02f));

        foreach (var withPyramid in new[] { false, true }) {
            var trace = new ScreenSpaceTrace(surface) {
                ViewProjection = Forward,
                Steps = 64,
                Thickness = 0.02f,
                Pyramid = withPyramid ? pyramid : null
            };

            Assert.True(trace.TryHit(origin, direction, 5f, out _), $"device shell missed, pyramid {withPyramid}");

            trace.LinearThickness = 0.1f;

            Assert.False(trace.TryHit(origin, direction, 5f, out _), $"linear shell hit, pyramid {withPyramid}");

            // And the linear shell still stops a ray that genuinely crosses it: straight down the
            // axis, view depth two to twenty-two, through the wall at four.
            Assert.True(
                trace.TryHit(new(0f, 0f, 2f), new(0f, 0f, 1f), 20f, out var pixel),
                $"the crossing ray missed, pyramid {withPyramid}"
            );

            Assert.Equal(new Int2(4, 4), pixel);
        }
    }

    /// <summary>Both marches over one camera's grid of rays: naive implies fast, same pixels.</summary>
    static void AssertAgreement(ScreenSpaceTrace trace, ScreenDepthPyramid pyramid, bool exactPixels) {
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
                        var apart = Math.Max(
                            Math.Abs(naivePixel.X - fastPixel.X),
                            Math.Abs(naivePixel.Y - fastPixel.Y)
                        );

                        Assert.True(
                            exactPixels ? naivePixel == fastPixel : apart <= 1,
                            $"naive {naivePixel} fast {fastPixel} at {origin} along {direction}"
                        );
                        hits++;
                    }

                    checked_++;
                }
            }
        }

        Assert.True(hits > 20, $"only {hits} of {checked_} rays hit — the fixture referees too little");
    }

    /// <summary>A big, almost-empty screen: a long ray across the sky must cost the pyramid a
    ///     handful of fetches where the naive march pays per step.</summary>
    static void Skip(Matrix4x4 camera, Vector3 origin) {
        var surface = new ReconstructedScreenSurface(new(64, 64));
        var pyramid = new ScreenDepthPyramid(new(64, 64));

        surface.Depth[(63 * 64) + 63] = 0.9f;
        pyramid.Build(surface.Depth);

        var trace = new ScreenSpaceTrace(surface) { ViewProjection = camera, Steps = 64, Thickness = 0.05f };
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
