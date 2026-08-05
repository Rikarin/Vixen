// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Foliage.Tests;

/// <summary>
///     The seat a vegetation texture had nowhere to sit.
/// </summary>
/// <remarks>
///     <para>
///         <c>Grass.rvn</c> and <c>Foliage.rvn</c> have declared an <c>albedoMap</c> since they were
///         written, and no rule could name one — so every field and every stand in every project drew
///         through the draw pass's white 1×1. It looked deliberate, which is why it lasted: a field
///         with no texture assigned <em>should</em> draw as plain grass.
///     </para>
///     <para>
///         What is asserted here is the authored half. The runtime half is
///         <c>TerrainSceneRenderer.Resolved</c>, which turns the name into a view every frame because
///         a texture is not resolvable until its pixels are on the device.
///     </para>
/// </remarks>
public sealed class VegetationAlbedoTests {
    /// <summary>A new rule names no texture, which is the white default and not a fault.</summary>
    [Fact]
    public void ANewRuleNamesNoTexture() {
        Assert.Equal("", GrassType.Of("Meadow").Albedo);
        Assert.Equal("", FoliageType.Of("Pine").Albedo);
    }

    /// <summary>And a rule that names one keeps it through a record copy.</summary>
    /// <remarks>
    ///     A name and not a handle, which is what makes a rule serialisable on a machine with no
    ///     device — <see cref="GrassType.Mesh" />'s argument, one field over.
    /// </remarks>
    [Fact]
    public void ARuleCarriesTheNameItWasGiven() {
        var grass = GrassType.Of("Meadow") with { Albedo = "vx:6a17bc2149f845118f5497862fa98c7e" };

        Assert.Equal("vx:6a17bc2149f845118f5497862fa98c7e", grass.Albedo);
        Assert.Equal(grass, grass with { });
    }

    /// <summary>The projection into a palette entry carries it across.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this catches has no error in it.</b>
    ///     <see cref="GrassType.ToFoliageType" /> is what puts a grass rule into a volume's palette,
    ///     and a field that survives the trip without its texture draws white — which is exactly what
    ///     it drew before the field existed, so nothing would look newly broken.
    /// </remarks>
    [Fact]
    public void TheProjectionIntoAPaletteCarriesIt() {
        var grass = GrassType.Of("Meadow") with { Albedo = "Textures/blade" };
        var entry = grass.ToFoliageType();

        Assert.Equal("Textures/blade", entry.Albedo);
        Assert.Equal(FoliageStorage.Derived, entry.Storage);
    }
}
