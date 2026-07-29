// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.DistanceFields.Tests;

/// <summary>
///     The one convention the CPU tracer and the shader have to share.
/// </summary>
/// <remarks>
///     <para>
///         Both are checked against arithmetic, separately, and both pass — which is exactly the
///         situation in which a shared convention can be wrong on one side and nothing notices. The
///         half-texel in the texture coordinate is the case: get it wrong in the shader alone and the
///         whole field shifts half a cell, every closed-form test still passes, and the picture is
///         subtly wrong in a way that is invisible without something to compare against.
///     </para>
///     <para>
///         <b>This does not execute the shader.</b> Raven has no interpreter, so what is checked is
///         that a texel-accurate emulation of hardware sampling agrees with
///         <see cref="MeshDistanceField.Sample" />, and that the Raven module computes the coordinate
///         the same way. The first is the real content; the second is a guard on the text.
///     </para>
/// </remarks>
public class SamplingConventionTests {
    /// <summary>
    ///     Sampling the volume the way a GPU would — texel centres, trilinear between them — is the
    ///     same answer as interpolating the array directly.
    /// </summary>
    [Fact]
    public void HardwareSamplingAgreesWithTheFieldsOwnInterpolation() {
        var (vertices, indices) = Shapes.Box(new(0.5f, 0.4f, 0.3f));
        var field = MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 12 });

        for (var z = 0; z <= 8; z++) {
            for (var y = 0; y <= 8; y++) {
                for (var x = 0; x <= 8; x++) {
                    // Deliberately off the grid points: on them any convention agrees.
                    var t = new Vector3(x / 8f, y / 8f, z / 8f);
                    var point = field.Bounds.Minimum + (field.Bounds.Size * t);

                    var direct = field.Sample(point);
                    var sampled = SampleAsHardwareWould(field, field.TextureCoordinate(point));

                    Assert.Equal(direct, sampled, 0.0005f);
                }
            }
        }
    }

    /// <summary>A grid point's coordinate is its texel's centre, not its corner.</summary>
    [Fact]
    public void AGridPointLandsOnItsTexelCentre() {
        var (vertices, indices) = Shapes.Box(new(0.5f));
        var field = MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 8 });

        var uvw = field.TextureCoordinate(field.PositionOf(0, 0, 0));

        Assert.Equal(0.5f / 8f, uvw.X, 5);

        var last = field.TextureCoordinate(field.PositionOf(7, 7, 7));

        Assert.Equal(7.5f / 8f, last.X, 5);
    }

    /// <summary>
    ///     And the shader computes it the same way. A text check, because Raven has no interpreter —
    ///     but the formula is one line and this is the line.
    /// </summary>
    [Fact]
    public void TheShaderComputesTheSameCoordinate() {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Shaders", "DistanceField.rvn")
        );

        Assert.Contains("val grid = (world - volume.minimum) * volume.inverseCellSize", source, StringComparison.Ordinal);
        Assert.Contains("return (grid + 0.5f) / Resolution(volume)", source, StringComparison.Ordinal);

        // And its resolution is the count, derived from the same two numbers the volume carries.
        Assert.Contains("return extent * volume.inverseCellSize + 1f", source, StringComparison.Ordinal);
    }

    /// <summary>Trilinear over texel centres, clamped at the edges — what the sampler is configured as.</summary>
    static float SampleAsHardwareWould(MeshDistanceField field, Vector3 uvw) {
        var size = new Vector3(field.Resolution.X, field.Resolution.Y, field.Resolution.Z);
        var texel = (uvw * size) - new Vector3(0.5f);

        var x0 = (int)MathF.Floor(texel.X);
        var y0 = (int)MathF.Floor(texel.Y);
        var z0 = (int)MathF.Floor(texel.Z);

        var tx = texel.X - x0;
        var ty = texel.Y - y0;
        var tz = texel.Z - z0;

        float At(int x, int y, int z) =>
            field[
                Math.Clamp(x, 0, field.Resolution.X - 1),
                Math.Clamp(y, 0, field.Resolution.Y - 1),
                Math.Clamp(z, 0, field.Resolution.Z - 1)
            ];

        static float Lerp(float a, float b, float t) => a + ((b - a) * t);

        var c00 = Lerp(At(x0, y0, z0), At(x0 + 1, y0, z0), tx);
        var c10 = Lerp(At(x0, y0 + 1, z0), At(x0 + 1, y0 + 1, z0), tx);
        var c01 = Lerp(At(x0, y0, z0 + 1), At(x0 + 1, y0, z0 + 1), tx);
        var c11 = Lerp(At(x0, y0 + 1, z0 + 1), At(x0 + 1, y0 + 1, z0 + 1), tx);

        return Lerp(Lerp(c00, c10, ty), Lerp(c01, c11, ty), tz);
    }
}
