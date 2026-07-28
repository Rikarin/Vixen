// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Xr.Tests;

/// <summary>
///     The projection, which is the one piece of arithmetic here that cannot be checked by looking at
///     a headset — a wrong one looks almost right.
/// </summary>
public sealed class XrProjectionTests {
    [Fact]
    public void ASymmetricFrustumGivesTheSameMatrixTheEnginesOwnPerspectiveDoes() {
        // The claim that this is the engine's convention and not some other engine's, stated as an
        // equality rather than as a comment.
        var vertical = MathUtil.DegreesToRadians(90f);
        var horizontal = MathUtil.DegreesToRadians(90f);

        var xr = XrProjection.FromFieldOfView(
            XrFieldOfView.Symmetric(horizontal, vertical),
            0.1f,
            100f
        );

        var engine = Matrix4x4.PerspectiveFieldOfView(vertical, 1f, 0.1f, 100f);

        AssertClose(engine, xr);
    }

    [Fact]
    public void AnAspectRatioOtherThanOneAlsoAgrees() {
        var vertical = MathUtil.DegreesToRadians(80f);
        var horizontal = 2f * MathF.Atan(MathF.Tan(vertical * 0.5f) * 1.6f);

        var xr = XrProjection.FromFieldOfView(XrFieldOfView.Symmetric(horizontal, vertical), 0.05f, 500f);
        var engine = Matrix4x4.PerspectiveFieldOfView(vertical, 1.6f, 0.05f, 500f);

        AssertClose(engine, xr);
    }

    [Fact]
    public void TheNearPlaneIsOneAndTheFarPlaneIsZero() {
        // Reverse-Z, which the whole renderer depends on: the depth test is GREATER and depth clears
        // to 0. A projection from another convention renders a headset entirely black or entirely
        // near.
        var projection = XrProjection.FromFieldOfView(Fov(), 0.1f, 100f);

        Assert.Equal(1f, Depth(projection, 0.1f), 3);
        Assert.Equal(0f, Depth(projection, 100f), 3);
    }

    [Fact]
    public void AnInfiniteFarPlaneStillPutsTheNearPlaneAtOne() {
        var projection = XrProjection.FromFieldOfView(Fov(), 0.1f);

        Assert.Equal(1f, Depth(projection, 0.1f), 3);
        Assert.Equal(0f, Depth(projection, 100_000f), 3);
    }

    [Fact]
    public void TheFrustumsEdgesLandOnTheEdgesOfTheClipVolume() {
        var fov = new XrFieldOfView(-0.9f, 0.7f, 0.8f, -1.0f);
        var projection = XrProjection.FromFieldOfView(fov, 0.1f, 100f);

        // A point on each edge of the frustum, one metre away, must project to ±1.
        Assert.Equal(-1f, ProjectX(projection, MathF.Tan(fov.AngleLeft), 1f), 3);
        Assert.Equal(1f, ProjectX(projection, MathF.Tan(fov.AngleRight), 1f), 3);
        Assert.Equal(1f, ProjectY(projection, MathF.Tan(fov.AngleUp), 1f), 3);
        Assert.Equal(-1f, ProjectY(projection, MathF.Tan(fov.AngleDown), 1f), 3);
    }

    [Fact]
    public void AnAsymmetricFrustumsCentreIsNotTheViewAxis() {
        // The whole reason this type exists. With the lenses canted, the point straight ahead is not
        // in the middle of the image — and a symmetric projection would put it there.
        var fov = new XrFieldOfView(-1.0f, 0.6f, 0.8f, -0.8f);
        var projection = XrProjection.FromFieldOfView(fov, 0.1f, 100f);

        Assert.NotEqual(0f, ProjectX(projection, 0f, 1f), 3);
    }

    [Fact]
    public void AFrustumWithNoVolumeIsRefused() =>
        Assert.Throws<ArgumentException>(
            () => XrProjection.FromFieldOfView(new XrFieldOfView(0.5f, -0.5f, 0.5f, -0.5f))
        );

    [Fact]
    public void ANearPlaneOfZeroIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => XrProjection.FromFieldOfView(Fov(), 0f));

    [Fact]
    public void AFarPlaneNearerThanTheNearOneIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => XrProjection.FromFieldOfView(Fov(), 1f, 0.5f));

    static XrFieldOfView Fov() =>
        XrFieldOfView.Symmetric(MathUtil.DegreesToRadians(100f), MathUtil.DegreesToRadians(90f));

    /// <summary>The depth a point at a distance down −Z projects to.</summary>
    static float Depth(in Matrix4x4 projection, float distance) {
        var point = new Vector4(0f, 0f, -distance, 1f);
        var clip = Transform(in projection, point);

        return clip.Z / clip.W;
    }

    static float ProjectX(in Matrix4x4 projection, float tangent, float distance) {
        var clip = Transform(in projection, new Vector4(tangent * distance, 0f, -distance, 1f));

        return clip.X / clip.W;
    }

    static float ProjectY(in Matrix4x4 projection, float tangent, float distance) {
        var clip = Transform(in projection, new Vector4(0f, tangent * distance, -distance, 1f));

        return clip.Y / clip.W;
    }

    /// <summary>A row vector times the matrix, which is this engine's convention.</summary>
    static Vector4 Transform(in Matrix4x4 m, Vector4 v) => new(
        (v.X * m.M11) + (v.Y * m.M21) + (v.Z * m.M31) + (v.W * m.M41),
        (v.X * m.M12) + (v.Y * m.M22) + (v.Z * m.M32) + (v.W * m.M42),
        (v.X * m.M13) + (v.Y * m.M23) + (v.Z * m.M33) + (v.W * m.M43),
        (v.X * m.M14) + (v.Y * m.M24) + (v.Z * m.M34) + (v.W * m.M44)
    );

    static void AssertClose(in Matrix4x4 expected, in Matrix4x4 actual) {
        Assert.Equal(expected.M11, actual.M11, 4);
        Assert.Equal(expected.M22, actual.M22, 4);
        Assert.Equal(expected.M31, actual.M31, 4);
        Assert.Equal(expected.M32, actual.M32, 4);
        Assert.Equal(expected.M33, actual.M33, 4);
        Assert.Equal(expected.M34, actual.M34, 4);
        Assert.Equal(expected.M43, actual.M43, 4);
    }
}
