// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;
using Xunit;

namespace Vixen.Rendering.ScreenProbes.Tests;

/// <summary>The trace order's first stage, against a depth buffer with known geometry in it.</summary>
/// <remarks>
///     Everything runs under the placement tests' orthographic camera — identity view, a 4×4 volume
///     between planes at 1 and 9 — because there device depth is affine in world z: a surface at
///     <c>z</c> reads <c>(z + 9) / 8</c>, so every closed form below is one line of arithmetic and a
///     thickness in device depth is exact rather than approximate.
/// </remarks>
public class ScreenSpaceTraceTests {
    /// <summary>A wall filling the buffer at z = −5, device depth 0.5.</summary>
    const float WallDepth = 0.5f;

    [Fact]
    public void AWallInTheBufferStopsTheRayTowardIt() {
        var trace = Trace(WallDepth);

        // From z = −2, into the screen: samples cross the wall plane at z = −5 and one lands in
        // the shell just behind it.
        Assert.True(trace.Hit(new(0f, 0f, -2f), new(0f, 0f, -1f), 8f));

        // Toward the camera instead, the ray only ever stands in front of the wall.
        Assert.False(trace.Hit(new(0f, 0f, -2f), new(0f, 0f, 1f), 8f));
    }

    [Fact]
    public void DeepBehindTheWallIsNotInsideIt() {
        var trace = Trace(WallDepth);

        // Sideways at z = −8: every sample is behind the wall, and all of them deeper than its
        // shell — the ray passes behind, which is the whole reason thickness exists.
        Assert.False(trace.Hit(new(0f, 0f, -8f), new(1f, 0f, 0f), 3f));

        // Sideways just behind the wall's plane, inside the shell, it is inside the wall.
        Assert.True(trace.Hit(new(0f, 0f, -5.05f), new(1f, 0f, 0f), 1f));
    }

    [Fact]
    public void TheSkyOccludesNothing() {
        Assert.False(Trace(0f).Hit(new(0f, 0f, -2f), new(0f, 0f, -1f), 8f));
    }

    [Fact]
    public void LeavingTheViewportEndsTheScreensAnswer() {
        var trace = Trace(WallDepth);

        // Inside the shell but starting at the right edge, moving right: the first sample is
        // already off screen, and off screen the ray is not the screen's to answer.
        Assert.False(trace.Hit(new(1.99f, 0f, -5.05f), new(1f, 0f, 0f), 8f));
    }

    [Fact]
    public void BehindTheCameraThereIsNoPixelToAsk() {
        var trace = Trace(WallDepth);

        // Nearer than the near plane at z = −1, depth leaves (0, 1) and no buffer texel saw it.
        Assert.False(trace.Hit(new(0f, 0f, -0.5f), new(0f, 0f, 1f), 0.4f));
    }

    [Fact]
    public void TheGatherAsksTheScreenFirst() {
        // A wall the depth buffer can see and the distance field cannot: directions into the screen
        // go dark, directions out of it still reach the sky — the trace order, observed end to end.
        // Eight units of ray, not the default hundred: a fixed-step march samples every
        // quarter-unit at this budget, and the wall's shell is sixteen hundredths of a unit deep —
        // the step-versus-thickness trade the HZB traversal exists to dissolve.
        var trace = Trace(WallDepth);

        var gather = new TracedScreenProbeGather(
            new EmptySpace(),
            new UniformSky(),
            new ScreenProbeGatherSettings { MaxDistance = 8f }
        ) { ScreenTrace = trace };
        var atlas = new ScreenProbeAtlas(new(new(32, 32)));

        Assert.True(gather.FillProbe(atlas, new(0, 0), new(0f, 0f, -2f), new(0f, 0f, 1f)));

        var probe = new Int2(0, 0);
        var resolution = atlas.Layout.MapResolution;
        var inward = OctahedralMap.Texel(new(0f, 0f, -1f), resolution);
        var outward = OctahedralMap.Texel(new(0f, 0f, 1f), resolution);

        Assert.Equal(Vector3.Zero, atlas[probe, inward]);
        Assert.Equal(new Vector3(1f), atlas[probe, outward]);
    }

    /// <summary>The orthographic snapshot: every pixel at one device depth, the camera at the origin.</summary>
    static ScreenSpaceTrace Trace(float bufferDepth) {
        var surface = new ReconstructedScreenSurface(new(32, 32));

        surface.Depth.Fill(bufferDepth);

        return new(surface) { ViewProjection = Matrix4x4.Orthographic(4f, 4f, 1f, 9f) };
    }

    sealed class EmptySpace : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    sealed class UniformSky : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => new(1f);

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }
}
