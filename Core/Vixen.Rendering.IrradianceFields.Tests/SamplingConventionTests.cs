// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.IrradianceFields.Tests;

/// <summary>The conventions the CPU field and the shader have to share.</summary>
/// <remarks>
///     <para>
///         Both are checked against arithmetic, separately, and both pass — which is exactly the
///         situation in which a shared convention can be wrong on one side and nothing notices. The
///         half-texel is the case: get it wrong in the shader alone and every probe shifts half a
///         texel, every closed-form test still passes, and the lighting is subtly in the wrong place
///         in a way that is invisible without something to compare against.
///     </para>
///     <para>
///         <b>This does not execute the shader.</b> Raven has no interpreter, so what is checked is
///         that a texel-accurate emulation of the shader's addressing agrees with
///         <see cref="IrradianceField.TrySample(Vector3, out IrradianceProbe)" />, and that the Raven
///         module's own constants are the ones this side uses. The first is the real content; the
///         second is a guard on the text.
///     </para>
/// </remarks>
public class SamplingConventionTests {
    static string Shader => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "IrradianceField.rvn"));

    /// <summary>
    ///     <b>The whole lookup, walked the way the shader walks it.</b> World position to voxel, voxel
    ///     to cell, cell to an indirection entry, entry to a pool origin and a local coordinate, local
    ///     to a texture coordinate, and a trilinear fetch at that coordinate — every step of which the
    ///     shader does with different code, and all of which have to end up in the same texels.
    /// </summary>
    /// <param name="refined">
    ///     Whether the field mixes brick sizes. Refined is the case the arithmetic gets interesting:
    ///     the divide by the brick's size and the floor of the cell by it are the two steps a uniform
    ///     field would never exercise, because there both are one.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheShadersAddressingReachesTheSameTexels(bool refined) {
        var field = Filled(refined);

        for (var z = 0; z <= 6; z++) {
            for (var y = 0; y <= 6; y++) {
                for (var x = 0; x <= 6; x++) {
                    // Deliberately off the probe positions: on them any convention agrees.
                    var point = new Vector3(0.13f + (x * 1.19f), 0.29f + (y * 1.17f), 0.41f + (z * 1.21f));

                    Assert.True(field.TrySample(point, out var direct));
                    Assert.Equal(direct.Value(), AsTheShaderWould(field, point), 0.0005f);
                }
            }
        }
    }

    /// <summary>
    ///     A brick spans four gaps and not five, on both sides. The fifth plane is the neighbour's
    ///     first probe, so scaling a local coordinate by five would put every sample a fifth of a brick
    ///     off — biggest at the far face, which is exactly where a seam would then appear.
    /// </summary>
    [Fact]
    public void BothSidesScaleALocalCoordinateByFour() {
        Assert.Equal(4, IrradianceBrickPool.BrickResolution);
        Assert.Equal(4f, Constant("BrickResolution"), 5);
    }

    /// <summary>
    ///     The shader's basis constants are this side's, folded. Two derivations of the same four
    ///     functions is how a probe and a skylight end up disagreeing about which way <c>+Y</c> is.
    /// </summary>
    [Fact]
    public void TheBasisConstantsAreTheOnesThisSideUses() {
        Span<float> basis = stackalloc float[SphericalHarmonicsL1.Count];
        SphericalHarmonicsL1.Evaluate(new(0, 1, 0), basis);

        Assert.Equal(basis[0], Constant("Constant"), 5);

        // The linear band already multiplied by the cosine lobe's own factor of two thirds, because a
        // shader has no reason to carry the two apart.
        Assert.Equal(0.488603f * 2f / 3f, Constant("Linear"), 5);
    }

    /// <summary>
    ///     <b>The half-texel, in the line that would silently drop it.</b> A probe lives at the centre
    ///     of its texel, so texel <c>i</c> is at <c>i + 0.5</c>. Without it every probe shifts half a
    ///     texel and nothing here or there fails.
    /// </summary>
    [Fact]
    public void TheShaderPutsProbesAtTexelCentres() {
        Assert.Contains("entry.rgb + float3(0.5f, 0.5f, 0.5f) + local * BrickResolution", Shader, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The payload is packed colour-major: a fetch of the red volume gives all three of red's
    ///     linear coefficients, not one coefficient's red, green and blue. Transposing it reads as
    ///     lighting whose colour rotates with the surface normal.
    /// </summary>
    [Fact]
    public void TheShaderUnpacksTheVolumesColourMajor() {
        Assert.Contains("val l1m1 = float3(r.r, g.r, b.r)", Shader, StringComparison.Ordinal);
        Assert.Contains("val l10 = float3(r.g, g.g, b.g)", Shader, StringComparison.Ordinal);
        Assert.Contains("val l11 = float3(r.b, g.b, b.b)", Shader, StringComparison.Ordinal);

        // Validity in the constant volume's alpha, the sun's shadow in the red one's.
        Assert.Contains("return IrradianceProbe(a.rgb, l1m1, l10, l11, a.a, r.a)", Shader, StringComparison.Ordinal);
    }

    /// <summary>The shader's addressing, written out in the order the shader writes it.</summary>
    static float AsTheShaderWould(IrradianceField field, Vector3 world) {
        var indirection = field.Indirection;
        var pool = field.Pool;
        var resolution = indirection.Resolution;

        var inverseCellSize = Vector3.One / indirection.CellSize;
        var voxel = (world - indirection.Bounds.Minimum) * inverseCellSize;

        var cell = new Vector3(
            Math.Clamp(MathF.Floor(voxel.X), 0, resolution.X - 1),
            Math.Clamp(MathF.Floor(voxel.Y), 0, resolution.Y - 1),
            Math.Clamp(MathF.Floor(voxel.Z), 0, resolution.Z - 1)
        );

        var entry = indirection[new((int)cell.X, (int)cell.Y, (int)cell.Z)];
        var size = (float)entry.Size;

        var origin = new Vector3(
            MathF.Floor(cell.X / size) * size,
            MathF.Floor(cell.Y / size) * size,
            MathF.Floor(cell.Z / size) * size
        );

        var local = Vector3.Clamp((voxel - origin) / size, Vector3.Zero, Vector3.One);
        var slot = pool.OriginOf(entry.Slot);
        var texels = pool.TexelResolution;

        var texel = new Vector3(slot.X, slot.Y, slot.Z)
            + new Vector3(0.5f)
            + (local * IrradianceBrickPool.BrickResolution);

        var uvw = texel / new Vector3(texels.X, texels.Y, texels.Z);

        return Trilinear(pool, (uvw * new Vector3(texels.X, texels.Y, texels.Z)) - new Vector3(0.5f));
    }

    /// <summary>What a hardware trilinear fetch does, in texel coordinates, over the whole pool.</summary>
    static float Trilinear(IrradianceBrickPool pool, Vector3 texel) {
        var resolution = pool.TexelResolution;
        var probes = pool.Texels.ToArray();

        var x0 = Math.Clamp((int)MathF.Floor(texel.X), 0, resolution.X - 2);
        var y0 = Math.Clamp((int)MathF.Floor(texel.Y), 0, resolution.Y - 2);
        var z0 = Math.Clamp((int)MathF.Floor(texel.Z), 0, resolution.Z - 2);

        var fx = Math.Clamp(texel.X - x0, 0f, 1f);
        var fy = Math.Clamp(texel.Y - y0, 0f, 1f);
        var fz = Math.Clamp(texel.Z - z0, 0f, 1f);

        float At(int x, int y, int z) =>
            probes[x + (resolution.X * (y + (resolution.Y * z)))].Value();

        static float Lerp(float from, float to, float amount) => from + ((to - from) * amount);

        var c00 = Lerp(At(x0, y0, z0), At(x0 + 1, y0, z0), fx);
        var c10 = Lerp(At(x0, y0 + 1, z0), At(x0 + 1, y0 + 1, z0), fx);
        var c01 = Lerp(At(x0, y0, z0 + 1), At(x0 + 1, y0, z0 + 1), fx);
        var c11 = Lerp(At(x0, y0 + 1, z0 + 1), At(x0 + 1, y0 + 1, z0 + 1), fx);

        return Lerp(Lerp(c00, c10, fy), Lerp(c01, c11, fy), fz);
    }

    /// <summary>One of the shader's own constants, read out of its source.</summary>
    /// <remarks>
    ///     Read numerically rather than matched as text, so that a constant written a different way
    ///     but meaning the same thing still passes — and one written the same way but meaning
    ///     something else does not.
    /// </remarks>
    static float Constant(string name) {
        var source = Shader;
        var marker = $"const val {name} = ";
        var start = source.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(start >= 0, $"the shader has no constant called {name}");

        start += marker.Length;

        var end = source.IndexOf('f', start);

        return float.Parse(source[start..end], CultureInfo.InvariantCulture);
    }

    /// <summary>A field over eight world units, filled with a linear ramp.</summary>
    static IrradianceField Filled(bool refined) {
        var field = new IrradianceField(new BoundingBox(new(0f), new(8f)), new(4));

        field.AllocateAll(2);

        if (refined) {
            field.Refine(new(new(0.5f), new(1.5f)));
            field.Refine(new(new(4.5f), new(5.5f)));
        }

        foreach (var brick in field.Bricks) {
            for (var z = 0; z < 4; z++) {
                for (var y = 0; y < 4; y++) {
                    for (var x = 0; x < 4; x++) {
                        field.SetProbe(brick, x, y, z, Probes.Of(Probes.Ramp(field.ProbePosition(brick, x, y, z))));
                    }
                }
            }
        }

        field.SyncBorders();

        return field;
    }
}
