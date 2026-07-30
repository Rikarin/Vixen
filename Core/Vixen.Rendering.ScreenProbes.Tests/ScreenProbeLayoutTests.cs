// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.ScreenProbes.Tests;

/// <summary>Where the probes stand and how a pixel reads the four around it.</summary>
public class ScreenProbeLayoutTests {
    [Fact]
    public void TheGridCoversTheViewport() {
        var layout = new ScreenProbeLayout(new(64, 48));

        Assert.Equal(new Int2(4, 3), layout.GridSize);
        Assert.Equal(12, layout.ProbeCount);
        Assert.Equal(new Int2(32, 24), layout.AtlasSize);

        // A partial tile still gets a probe — the pixels in it have to read something.
        Assert.Equal(new Int2(3, 2), new ScreenProbeLayout(new(33, 17)).GridSize);
    }

    [Fact]
    public void AnchorsAreTileCentresInsideTheViewport() {
        var layout = new ScreenProbeLayout(new(64, 48));

        Assert.Equal(new Int2(8, 8), layout.Anchor(new(0, 0)));
        Assert.Equal(new Int2(24, 40), layout.Anchor(new(1, 2)));

        // A partial tile's centre can fall outside the viewport, and a probe has to stand on a pixel
        // that exists.
        var partial = new ScreenProbeLayout(new(33, 17));

        Assert.Equal(new Int2(32, 16), partial.Anchor(new(2, 1)));
    }

    [Fact]
    public void EveryProbeOwnsItsOwnPatchOfTheAtlas() {
        var layout = new ScreenProbeLayout(new(64, 48));

        Assert.Equal(new Int2(0, 0), layout.AtlasOrigin(new(0, 0)));
        Assert.Equal(new Int2(24, 16), layout.AtlasOrigin(new(3, 2)));
    }

    /// <summary>A pixel standing exactly on an anchor reads that probe and nothing else.</summary>
    [Fact]
    public void AnAnchorPixelReadsItsOwnProbeWhole() {
        var layout = new ScreenProbeLayout(new(64, 48));

        Span<ScreenProbeTap> taps = stackalloc ScreenProbeTap[4];

        layout.Bilinear(new(24, 40), taps);

        var weight = 0f;

        foreach (var tap in taps) {
            if (tap.Probe == new Int2(1, 2)) {
                weight += tap.Weight;
            } else {
                Assert.Equal(0f, tap.Weight, 1e-6f);
            }
        }

        Assert.Equal(1f, weight, 1e-6f);
    }

    /// <summary>A pixel halfway between two anchors splits evenly between them.</summary>
    [Fact]
    public void HalfwayBetweenAnchorsIsAnEvenSplit() {
        var layout = new ScreenProbeLayout(new(64, 48));

        Span<ScreenProbeTap> taps = stackalloc ScreenProbeTap[4];

        layout.Bilinear(new(16, 8), taps);

        Assert.Equal(0.5f, Weight(taps, new(0, 0)), 1e-6f);
        Assert.Equal(0.5f, Weight(taps, new(1, 0)), 1e-6f);
    }

    /// <summary>
    ///     The weights always sum to one, everywhere — including the border, where the lattice clamps
    ///     rather than extrapolates.
    /// </summary>
    [Fact]
    public void WeightsAlwaysSumToOne() {
        var layout = new ScreenProbeLayout(new(33, 17));

        Span<ScreenProbeTap> taps = stackalloc ScreenProbeTap[4];

        for (var y = 0; y < 17; y++) {
            for (var x = 0; x < 33; x++) {
                layout.Bilinear(new(x, y), taps);

                var total = 0f;

                foreach (var tap in taps) {
                    Assert.InRange(tap.Weight, 0f, 1f);
                    Assert.InRange(tap.Probe.X, 0, layout.GridSize.X - 1);
                    Assert.InRange(tap.Probe.Y, 0, layout.GridSize.Y - 1);

                    total += tap.Weight;
                }

                Assert.Equal(1f, total, 1e-5f);
            }
        }
    }

    /// <summary>A corner pixel, outside the outermost anchors, takes the corner probe whole.</summary>
    [Fact]
    public void ACornerPixelClampsToTheCornerProbe() {
        var layout = new ScreenProbeLayout(new(64, 48));

        Span<ScreenProbeTap> taps = stackalloc ScreenProbeTap[4];

        layout.Bilinear(new(0, 0), taps);

        Assert.Equal(1f, Weight(taps, new(0, 0)), 1e-6f);

        layout.Bilinear(new(63, 47), taps);

        Assert.Equal(1f, Weight(taps, new(3, 2)), 1e-6f);
    }

    [Fact]
    public void WhatDoesNotExistIsRefused() {
        var layout = new ScreenProbeLayout(new(64, 48));
        var taps = new ScreenProbeTap[4];

        Assert.Throws<ArgumentOutOfRangeException>(() => layout.Anchor(new(4, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.AtlasOrigin(new(0, 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.Bilinear(new(64, 0), taps));
        Assert.Throws<ArgumentException>(() => layout.Bilinear(new(0, 0), new ScreenProbeTap[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenProbeLayout(new(0, 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenProbeLayout(new(4, 4), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenProbeLayout(new(4, 4), 16, 0));
    }

    static float Weight(ReadOnlySpan<ScreenProbeTap> taps, Int2 probe) {
        var total = 0f;

        foreach (var tap in taps) {
            if (tap.Probe == probe) {
                total += tap.Weight;
            }
        }

        return total;
    }
}
