// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;
using Xunit;

namespace Vixen.Rendering.ScreenProbes.Tests;

/// <summary>Probe placement from a frame's own depth and normals.</summary>
/// <remarks>
///     The orthographic case is worked by hand rather than round-tripped, for the octahedral map's
///     reason: a round trip through a matrix and its inverse passes with the axes swapped, and the
///     axis conventions — y down in UV and NDC alike, reversed depth — are exactly what this type
///     exists to get right once.
/// </remarks>
public class ReconstructedScreenSurfaceTests {
    [Fact]
    public void AnOrthographicCameraReconstructsByHand() {
        var surface = new ReconstructedScreenSurface(new(32, 32));

        // Identity view — a camera at the origin looking down -Z — and a 4×4 ortho volume between
        // planes at 1 and 9. Row-vector: clip.z = (z + far) / (far - near), so device depth d puts
        // the surface at z = 8d - 9. Reversed: d = 1 is the near plane at z = -1.
        var projection = Matrix4x4.Orthographic(4f, 4f, 1f, 9f);

        Assert.True(Matrix4x4.Invert(projection, out var inverse));

        surface.InverseViewProjection = inverse;
        surface.Depth.Fill(0.25f);
        surface.Normals.Fill(new(0.5f, 1f, 0.5f, 0f));

        Assert.True(surface.TrySurface(new(0, 0), out var position, out var normal));

        // The top-left pixel, and the y is the whole point. Its centre is UV (0.5/32, 0.5/32), which
        // is NDC -0.96875 in x and +0.96875 in y — the reconstruction negates y, so the *top* row
        // maps to the *upper* half of the view volume and this pixel is up and to the left.
        //
        // ⚠ This assertion used to read -1.9375, defended by a comment saying Vulkan's NDC points y
        // down like the engine's UV and that a reconstruction which negated y "would pass every round
        // trip" while being wrong. The premise is false and the conclusion had it backwards: a shader
        // never sees Vulkan-native NDC here, because the projection is built y-up and the backend
        // lands it with a negative-height viewport. So clip y = +1 is the top of the screen, and the
        // old expectation put the top-left pixel *below* the camera's axis — which is the frame
        // upside down, and is exactly how it rendered once anything ran the post chain.
        Assert.Equal(-1.9375f, position.X, 1e-4f);
        Assert.Equal(1.9375f, position.Y, 1e-4f);
        Assert.Equal(-7f, position.Z, 1e-3f);

        // Encoded (0.5, 1, 0.5) is +Y — the shader's xyz * 2 - 1.
        Assert.Equal(0f, normal.X, 1e-5f);
        Assert.Equal(1f, normal.Y, 1e-5f);
        Assert.Equal(0f, normal.Z, 1e-5f);

        // The bottom-right pixel mirrors it: right and down.
        Assert.True(surface.TrySurface(new(31, 31), out position, out _));
        Assert.Equal(1.9375f, position.X, 1e-4f);
        Assert.Equal(-1.9375f, position.Y, 1e-4f);
    }

    [Fact]
    public void APerspectiveCameraInvertsItsOwnProjection() {
        var view = Matrix4x4.LookAt(new(0f, 3f, 5f), Vector3.Zero, new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 100f);
        var viewProjection = view * projection;

        Assert.True(Matrix4x4.Invert(viewProjection, out var inverse));

        var surface = new ReconstructedScreenSurface(new(32, 32)) { InverseViewProjection = inverse };

        surface.Normals.Fill(new(0.5f, 1f, 0.5f, 0f));

        foreach (var (pixel, deviceDepth) in (ReadOnlySpan<(Int2, float)>)[
            (new Int2(5, 7), 0.9f),
            (new Int2(16, 16), 0.5f),
            (new Int2(30, 2), 0.05f)
        ]) {
            surface.Depth.Fill(deviceDepth);

            Assert.True(surface.TrySurface(pixel, out var position, out _));

            // Forward through the same camera: the reconstructed point projects back onto the
            // pixel's own centre at the depth it was read from. This is what "one function evaluated
            // twice" buys — the conventions cancel exactly, not approximately.
            var clip = Matrix4x4.TransformVector4(new(position, 1f), viewProjection);
            var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
            // `Transform.NdcToUv` by hand, negation included — the forward half of the round trip
            // has to be the same mapping as the reverse or the conventions cannot cancel.
            var uv = (new Vector2(ndc.X, -ndc.Y) * 0.5f) + new Vector2(0.5f, 0.5f);

            Assert.Equal(pixel.X + 0.5f, uv.X * 32f, 1e-2f);
            Assert.Equal(pixel.Y + 0.5f, uv.Y * 32f, 1e-2f);
            Assert.Equal(deviceDepth, ndc.Z, 1e-4f);
        }
    }

    [Fact]
    public void TheSkyHasNoSurface() {
        var surface = new ReconstructedScreenSurface(new(8, 8));

        surface.Normals.Fill(new(0.5f, 1f, 0.5f, 0f));

        // A fresh surface is all sky — the depth clear of zero, because depth is reversed.
        Assert.False(surface.TrySurface(new(3, 3), out _, out _));

        // And only the written half stops being sky.
        for (var y = 0; y < 8; y++) {
            for (var x = 4; x < 8; x++) {
                surface.Depth[(y * 8) + x] = 0.5f;
            }
        }

        surface.InverseViewProjection = Matrix4x4.Identity;

        Assert.False(surface.TrySurface(new(3, 5), out _, out _));
        Assert.True(surface.TrySurface(new(4, 5), out _, out _));
    }

    [Fact]
    public void AnUnwrittenNormalHasNoSurface() {
        var surface = new ReconstructedScreenSurface(new(8, 8));

        surface.Depth.Fill(0.5f);
        surface.Normals.Fill(new(0.5f, 0.5f, 0.5f, 0f));

        // Depth was drawn but the normal is the encoded mid-grey that decodes to nothing — a probe
        // that cannot be biased off its surface does not stand on it.
        Assert.False(surface.TrySurface(new(2, 2), out _, out _));
    }

    [Fact]
    public void PlacementFeedsTheGatherThroughTheAnchors() {
        // The whole seam at once: a half-sky frame placed by reconstruction, gathered under a
        // uniform sky. Probes anchored on the drawn half are valid, the sky half's are not.
        var surface = new ReconstructedScreenSurface(new(64, 32));

        Assert.True(Matrix4x4.Invert(Matrix4x4.Orthographic(4f, 4f, 1f, 9f), out var inverse));

        surface.InverseViewProjection = inverse;
        surface.Normals.Fill(new(0.5f, 1f, 0.5f, 0f));

        for (var y = 0; y < 32; y++) {
            for (var x = 32; x < 64; x++) {
                surface.Depth[(y * 64) + x] = 0.5f;
            }
        }

        var atlas = new ScreenProbeAtlas(new(new(64, 32)));

        new TracedScreenProbeGather(new EmptySpace(), new UniformSky()).Fill(atlas, surface);

        // Anchors at x = 8, 24 show the sky; 40 and 56 the surface.
        Assert.False(atlas.IsValid(new(0, 0)));
        Assert.False(atlas.IsValid(new(1, 1)));
        Assert.True(atlas.IsValid(new(2, 0)));
        Assert.True(atlas.IsValid(new(3, 1)));
    }

    [Fact]
    public void APixelOutsideTheViewportRefuses() {
        var surface = new ReconstructedScreenSurface(new(8, 8));

        Assert.Throws<ArgumentOutOfRangeException>(() => surface.TrySurface(new(8, 0), out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => surface.TrySurface(new(0, -1), out _, out _));
    }

    sealed class EmptySpace : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    sealed class UniformSky : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => new(0.5f);

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }
}
