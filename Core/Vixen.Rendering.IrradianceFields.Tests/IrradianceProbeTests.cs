// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.IrradianceFields.Tests;

/// <summary>The payload, and the one decision in it that is not obvious.</summary>
public class IrradianceProbeTests {
    /// <summary>
    ///     <b>An unfilled probe is invalid, not valid-and-black.</b> The safe direction: it reads as
    ///     "no answer here" and dilation replaces it, where the other default spreads darkness through
    ///     a scene and looks exactly like a correct field that happens to be dark.
    /// </summary>
    [Fact]
    public void AnUnfilledProbeIsNotBelieved() {
        Assert.Equal(0f, IrradianceProbe.Empty.Validity);
        Assert.Equal(SphericalHarmonicsL1.Zero, IrradianceProbe.Empty.Radiance);
        Assert.Equal(default, IrradianceProbe.Empty);
    }

    [Fact]
    public void ALitProbeIsBelievedEntirely() {
        var probe = IrradianceProbe.Lit(SphericalHarmonicsL1.Zero.Accumulated(new(0, 1, 0), new(1f), 1f));

        Assert.Equal(1f, probe.Validity);
        Assert.Equal(1f, probe.SunShadow);
        Assert.True(probe.Irradiance(new(0, 1, 0)).X > probe.Irradiance(new(0, -1, 0)).X);
    }

    /// <summary>
    ///     Validity blends with everything else, which is what makes an interpolation across the edge
    ///     of a valid region fade out rather than stop.
    /// </summary>
    [Fact]
    public void BlendingBlendsEverythingIncludingWhetherToBelieveIt() {
        var lit = new IrradianceProbe(SphericalHarmonicsL1.Zero.Accumulated(new(0, 1, 0), new(4f), 1f), 1f, 1f);
        var blended = IrradianceProbe.Lerp(IrradianceProbe.Empty, lit, 0.25f);

        Assert.Equal(0.25f, blended.Validity, 5);
        Assert.Equal(0.25f, blended.SunShadow, 5);
        Assert.Equal(lit.Irradiance(new(0, 1, 0)).X * 0.25f, blended.Irradiance(new(0, 1, 0)).X, 5);
    }

    /// <summary>The payload evaluates radiance into irradiance, rather than storing it that way.</summary>
    [Fact]
    public void AProbeAnswersPerNormal() {
        var probe = IrradianceProbe.Lit(SphericalHarmonicsL1.Zero.Accumulated(new(1, 0, 0), new(1f), 1f));

        Assert.NotEqual(probe.Irradiance(new(1, 0, 0)), probe.Irradiance(new(0, 0, 1)));
        Assert.Equal(Vector3.Zero, IrradianceProbe.Empty.Irradiance(new(0, 1, 0)));
    }
}
