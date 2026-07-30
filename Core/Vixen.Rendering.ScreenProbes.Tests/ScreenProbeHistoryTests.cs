// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.ScreenProbes.Tests;

/// <summary>The temporal accumulator, held to the running mean's own arithmetic.</summary>
/// <remarks>
///     The atlases are seeded by hand — the storage does not know what filled it — because the
///     recurrences under test are exact: a running mean of a constant is the constant, a flip
///     follows <c>(h·w + c)/(w+1)</c> to the digit, and a rejection is the current frame alone.
///     Everything runs under the placement tests' orthographic camera, where a probe's surface and
///     its pixel are one line of arithmetic apart.
/// </remarks>
public class ScreenProbeHistoryTests {
    static readonly Matrix4x4 Camera = Matrix4x4.Orthographic(4f, 4f, 1f, 9f);

    [Fact]
    public void AConstantSceneConvergesToItselfExactly() {
        var atlas = Seeded(_ => 0.6f);
        var history = new ScreenProbeHistory(atlas.Layout);

        for (var frame = 0; frame < 5; frame++) {
            history.Accumulate(atlas, Camera);
        }

        // lerp of equal values is the value — no drift, however many frames.
        Assert.Equal(0.6f, history.Resolved(new(1, 1)).Irradiance(new(0f, 0f, 1f)).X, 1e-5f);
        Assert.Equal(5f, history.Weight(new(1, 1)));

        // Frame one had nothing to reproject; every later frame reused everything.
        Assert.Equal(atlas.Layout.ProbeCount, history.Reprojected);
        Assert.Equal(0, history.Rejected);
    }

    [Fact]
    public void AFlippedLightFollowsTheRunningMean() {
        var history = new ScreenProbeHistory(new ScreenProbeAtlas(new(new(64, 64))).Layout);
        var bright = Seeded(_ => 1f);
        var dark = Seeded(_ => 0f);

        for (var frame = 0; frame < 3; frame++) {
            history.Accumulate(bright, Camera);
        }

        var probe = new Int2(1, 1);
        var up = new Vector3(0f, 0f, 1f);

        // (1·3 + 0) / 4, then (0.75·4 + 0) / 5 — the mean, not a mood.
        history.Accumulate(dark, Camera);
        Assert.Equal(0.75f, history.Resolved(probe).Irradiance(up).X, 1e-5f);

        history.Accumulate(dark, Camera);
        Assert.Equal(0.6f, history.Resolved(probe).Irradiance(up).X, 1e-5f);
    }

    [Fact]
    public void TheCapAgesTheOldestFramesOut() {
        var history = new ScreenProbeHistory(new ScreenProbeAtlas(new(new(64, 64))).Layout) { MaxFrames = 4 };
        var bright = Seeded(_ => 1f);
        var dark = Seeded(_ => 0f);

        for (var frame = 0; frame < 10; frame++) {
            history.Accumulate(bright, Camera);
        }

        var probe = new Int2(1, 1);

        // The weight saturates at the cap, so the flip converges at rate 1/4 per frame instead of
        // freezing under ten frames of accumulated confidence.
        Assert.Equal(4f, history.Weight(probe));

        history.Accumulate(dark, Camera);
        Assert.Equal(0.75f, history.Resolved(probe).Irradiance(new(0f, 0f, 1f)).X, 1e-5f);
    }

    [Fact]
    public void ADisocclusionStartsOver() {
        var history = new ScreenProbeHistory(new ScreenProbeAtlas(new(new(64, 64))).Layout);
        var floor = Seeded(_ => 1f);

        for (var frame = 0; frame < 3; frame++) {
            history.Accumulate(floor, Camera);
        }

        // One probe's surface jumps a unit closer to the camera — a doorway opened — while its
        // neighbours stand where they stood.
        var moved = Seeded(_ => 0f);
        var probe = new Int2(1, 1);
        var anchor = moved.Layout.Anchor(probe);

        moved.SetSurface(probe, new(World(anchor.X), World(anchor.Y), -4f), new(0f, 0f, 1f));

        history.Accumulate(moved, Camera);

        var up = new Vector3(0f, 0f, 1f);

        // The moved probe rejected its ghost and answers this frame alone; the neighbour blends.
        Assert.Equal(1, history.Rejected);
        Assert.Equal(0f, history.Resolved(probe).Irradiance(up).X, 1e-5f);
        Assert.Equal(1f, history.Weight(probe));
        Assert.Equal(0.75f, history.Resolved(new(2, 1)).Irradiance(up).X, 1e-5f);
        Assert.Equal(4f, history.Weight(new(2, 1)));
    }

    [Fact]
    public void APannedCameraFindsTheProbeThatStoodThere() {
        // Frame one: probes on the z = −5 plane under the identity-view camera, each column
        // holding its own constant. Frame two: the camera pans one tile — one world unit — right,
        // so probe (i, j) now stands on the world surface probe (i − 1, j) stood on, and its
        // history must come from there.
        var layout = new ScreenProbeAtlas(new(new(64, 64))).Layout;
        var history = new ScreenProbeHistory(layout);

        history.Accumulate(Seeded(probe => probe.X), Camera);

        var panned = Matrix4x4.FromTranslation(new(1f, 0f, 0f)) * Camera;
        var after = new ScreenProbeAtlas(new(new(64, 64)));

        for (var y = 0; y < layout.GridSize.Y; y++) {
            for (var x = 0; x < layout.GridSize.X; x++) {
                var probe = new Int2(x, y);
                var anchor = layout.Anchor(probe);

                // The same world plane, seen one unit to the left, gathering darkness this frame.
                after.SetSurface(probe, new(World(anchor.X) - 1f, World(anchor.Y), -5f), new(0f, 0f, 1f));
            }
        }

        after.Resolve();
        history.Accumulate(after, panned);

        var up = new Vector3(0f, 0f, 1f);

        // Probe 2 blended with column 1's history: (1·1 + 0) / 2.
        Assert.Equal(0.5f, history.Resolved(new(2, 1)).Irradiance(up).X, 1e-5f);

        // Probe 0's surface was off screen last frame — no history, honestly noisy.
        Assert.Equal(1f, history.Weight(new(0, 1)));
        Assert.Equal(0f, history.Resolved(new(0, 1)).Irradiance(up).X, 1e-5f);
    }

    [Fact]
    public void TheFilterLeavesAUniformFieldAlone() {
        var history = new ScreenProbeHistory(new ScreenProbeAtlas(new(new(64, 64))).Layout);

        history.Accumulate(Seeded(_ => 0.6f), Camera);

        var filtered = new SphericalHarmonicsL1[history.Layout.ProbeCount];

        history.Filter(filtered, 0.5f, 0.1f);

        // Every neighbour holds the same answer, so however many blend in, the weights normalise
        // back to it exactly.
        foreach (var value in filtered) {
            Assert.Equal(0.6f, value.Irradiance(new(0f, 0f, 1f)).X, 1e-5f);
        }
    }

    [Fact]
    public void ALoneSpikeSpreadsByTheStatedShare() {
        var history = new ScreenProbeHistory(new ScreenProbeAtlas(new(new(64, 64))).Layout);

        // One bright probe in a dark field, all on one plane.
        history.Accumulate(Seeded(probe => probe == new Int2(1, 1) ? 1f : 0f), Camera);

        var filtered = new SphericalHarmonicsL1[history.Layout.ProbeCount];

        history.Filter(filtered, 0.5f, 0.1f);

        var up = new Vector3(0f, 0f, 1f);

        // The spike keeps 1/(1 + 4·0.5) of itself; an edge-adjacent neighbour gains 0.5/(1 + 4·0.5)
        // of it — hand-computed from the kernel, not observed from the code.
        Assert.Equal(1f / 3f, filtered[history.Layout.ProbeIndex(new(1, 1))].Irradiance(up).X, 1e-5f);
        Assert.Equal(0.5f / 3f, filtered[history.Layout.ProbeIndex(new(2, 1))].Irradiance(up).X, 1e-5f);

        // Two steps away, nothing arrives in one pass.
        Assert.Equal(0f, filtered[history.Layout.ProbeIndex(new(3, 1))].Irradiance(up).X, 1e-5f);
    }

    [Fact]
    public void ADepthEdgeStopsTheSpread() {
        var atlas = Seeded(probe => probe.X <= 1 ? 1f : 0f);
        var layout = atlas.Layout;

        // Columns 2 and 3 stand on a nearer plane — the bright half is a different surface.
        for (var y = 0; y < layout.GridSize.Y; y++) {
            for (var x = 2; x < layout.GridSize.X; x++) {
                var probe = new Int2(x, y);
                var anchor = layout.Anchor(probe);

                atlas.SetSurface(probe, new(World(anchor.X), World(anchor.Y), -4f), new(0f, 0f, 1f));
            }
        }

        var history = new ScreenProbeHistory(layout);

        history.Accumulate(atlas, Camera);

        var filtered = new SphericalHarmonicsL1[layout.ProbeCount];

        history.Filter(filtered, 0.5f, 0.1f);

        var up = new Vector3(0f, 0f, 1f);

        // The dark probe beside the edge keeps its own answer to the bit: its bright neighbour is a
        // different surface, and blending it in is how light bleeds across a doorway.
        Assert.Equal(0f, filtered[layout.ProbeIndex(new(2, 1))].Irradiance(up).X, 1e-6f);

        // The bright probe on its own side still spreads within its plane.
        Assert.Equal(1f, filtered[layout.ProbeIndex(new(0, 1))].Irradiance(up).X, 1e-5f);
    }

    /// <summary>World coordinate of a pixel centre-ish under the test camera: one tile per unit.</summary>
    static float World(int pixel) => (pixel - 32) / 16f;

    /// <summary>An atlas of valid probes on the z = −5 plane, each map one constant.</summary>
    static ScreenProbeAtlas Seeded(Func<Int2, float> radiance) {
        var atlas = new ScreenProbeAtlas(new(new(64, 64)));
        var layout = atlas.Layout;

        for (var y = 0; y < layout.GridSize.Y; y++) {
            for (var x = 0; x < layout.GridSize.X; x++) {
                var probe = new Int2(x, y);
                var anchor = layout.Anchor(probe);

                atlas.SetSurface(probe, new(World(anchor.X), World(anchor.Y), -5f), new(0f, 0f, 1f));

                for (var ty = 0; ty < layout.MapResolution; ty++) {
                    for (var tx = 0; tx < layout.MapResolution; tx++) {
                        atlas[probe, new(tx, ty)] = new(radiance(probe));
                    }
                }
            }
        }

        atlas.Resolve();

        return atlas;
    }
}
