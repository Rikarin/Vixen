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

    [Fact]
    public void AScreenHitRadiatesTheFramesColourAtThePixelThatStoppedIt() {
        // The same fixture, with a colour to read: the wall the depth buffer sees is no longer a
        // pure occluder but a surface radiating what the frame drew there. ⚠ Without this the two
        // hit paths of one gather disagreed — a *field* hit answers through the surface cache and a
        // *screen* hit answered black — and the disagreement ran one way, because the screen is
        // consulted precisely for geometry whose light the field is also missing.
        var gather = Gather(Trace(WallDepth));
        var wall = new Vector3(0.25f, 0.5f, 0.75f);

        gather.ScreenColour = _ => wall;

        var atlas = new ScreenProbeAtlas(new(new(32, 32)));

        Assert.True(gather.FillProbe(atlas, new(0, 0), new(0f, 0f, -2f), new(0f, 0f, 1f)));

        var resolution = atlas.Layout.MapResolution;

        var probe = new Int2(0, 0);

        Assert.Equal(wall, atlas[probe, OctahedralMap.Texel(new(0f, 0f, -1f), resolution)]);
    }

    [Fact]
    public void TheScreenTraceNoLongerSubtractsLight() {
        // The closed form the defect was: light the screen trace on a wall lit exactly as the sky
        // it hides, and the gather must come back texel for texel the gather that never marched the
        // screen at all. Under the old answer every texel the screen could stop went to zero — the
        // more the screen saw, the darker the probe — and the two sums differ by that whole cone.
        var without = Fill(Gather(null));
        var with = Gather(Trace(WallDepth));

        with.ScreenColour = _ => new(1f);

        var texels = Fill(with);

        Assert.Equal(without.Length, texels.Length);
        Assert.NotEmpty(texels);

        // Both halves, because a predicate that cannot be false proves nothing: the screen actually
        // stopped rays here (the trace is the same one `TheGatherAsksTheScreenFirst` fires), and
        // stopping them changed no texel.
        for (var i = 0; i < texels.Length; i++) {
            Assert.Equal(without[i], texels[i]);
        }

        var dark = Fill(Gather(Trace(WallDepth)));

        Assert.NotEqual(Sum(without), Sum(dark));
        Assert.True(Sum(dark) < Sum(without), "the occlusion answer must be the darker one");
    }

    /// <summary>A gather over empty space under a uniform sky, with the screen trace given.</summary>
    static TracedScreenProbeGather Gather(ScreenSpaceTrace? trace) =>
        new(new EmptySpace(), new UniformSky(), new ScreenProbeGatherSettings { MaxDistance = 8f }) {
            ScreenTrace = trace
        };

    /// <summary>Every texel of the one probe, in atlas order.</summary>
    static Vector3[] Fill(TracedScreenProbeGather gather) {
        var atlas = new ScreenProbeAtlas(new(new(32, 32)));

        Assert.True(gather.FillProbe(atlas, new(0, 0), new(0f, 0f, -2f), new(0f, 0f, 1f)));

        var probe = new Int2(0, 0);
        var resolution = atlas.Layout.MapResolution;
        var texels = new Vector3[resolution * resolution];

        for (var y = 0; y < resolution; y++) {
            for (var x = 0; x < resolution; x++) {
                texels[(y * resolution) + x] = atlas[probe, new(x, y)];
            }
        }

        return texels;
    }

    /// <summary>How much light a map holds — the quantity the defect removed.</summary>
    static float Sum(Vector3[] texels) {
        var total = 0f;

        foreach (var texel in texels) {
            total += texel.X + texel.Y + texel.Z;
        }

        return total;
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
