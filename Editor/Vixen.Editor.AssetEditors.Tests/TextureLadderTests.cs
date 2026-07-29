// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors.Importing;
using Vixen.Editor.Assets.Textures;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>What the mip inspector shows is arithmetic, and this is the arithmetic.</summary>
public class TextureLadderTests {
    /// <summary>A chain runs to a one-by-one level, and there are as many as the halving takes.</summary>
    [Fact]
    public void AChainEndsAtOneTexel() {
        var levels = TextureLadder.Build(256, 256, new() { GenerateMips = true });

        Assert.Equal(9, levels.Count);
        Assert.Equal(1, levels[^1].Width);
        Assert.Equal(1, levels[^1].Height);
    }

    /// <summary>Mips off is one level, not a chain of one.</summary>
    [Fact]
    public void NoMipsIsOneLevel() =>
        Assert.Single(TextureLadder.Build(256, 256, new() { GenerateMips = false }));

    /// <summary>A non-square texture keeps halving until both sides are one.</summary>
    [Fact]
    public void ANonSquareChainRunsToBothSides() {
        var levels = TextureLadder.Build(64, 16, new() { GenerateMips = true });

        Assert.Equal(7, levels.Count);
        Assert.Equal(1, levels[^1].Width);
        Assert.Equal(1, levels[^1].Height);
    }

    /// <summary>⚠ The size limit halves rather than resamples: 2048 under a limit of 1000 ships at 512.</summary>
    [Fact]
    public void TheLimitHalvesRatherThanResamples() {
        var (width, height) = TextureLadder.Fit(2048, 2048, 1000);

        Assert.Equal(512, width);
        Assert.Equal(512, height);
    }

    /// <summary>A texture already inside the limit is untouched.</summary>
    [Fact]
    public void ATextureInsideTheLimitIsUntouched() =>
        Assert.Equal((512, 256), TextureLadder.Fit(512, 256, 512));

    /// <summary>No limit means no halving.</summary>
    [Fact]
    public void NoLimitMeansNoHalving() => Assert.Equal((4096, 4096), TextureLadder.Fit(4096, 4096, 0));

    /// <summary>
    ///     ⚠ Automatic resolves the way <c>TextureImporter</c> resolves it. A preview that guessed
    ///     differently would show a cost the build does not produce.
    /// </summary>
    [Fact]
    public void AutomaticMatchesTheImporter() {
        Assert.Equal(
            TextureCompression.Bc5,
            TextureLadder.Resolve(new() { Content = TextureContent.NormalMap })
        );

        Assert.Equal(TextureCompression.Bc7, TextureLadder.Resolve(new() { Content = TextureContent.Colour }));
        Assert.Equal(TextureCompression.Bc7, TextureLadder.Resolve(new() { Content = TextureContent.Linear }));
    }

    /// <summary>An explicit format wins over the content.</summary>
    [Fact]
    public void AnExplicitFormatWins() =>
        Assert.Equal(
            TextureCompression.Bc1,
            TextureLadder.Resolve(new() { Content = TextureContent.NormalMap, Compression = TextureCompression.Bc1 })
        );

    /// <summary>⚠ A block format's tail costs a whole block, however small the level is.</summary>
    [Fact]
    public void ABlockFormatsTailCostsAWholeBlock() {
        Assert.Equal(16, TextureLadder.BytesFor(TextureCompression.Bc7, 1, 1));
        Assert.Equal(8, TextureLadder.BytesFor(TextureCompression.Bc1, 1, 1));
        Assert.Equal(4, TextureLadder.BytesFor(TextureCompression.None, 1, 1));
    }

    /// <summary>A whole chain costs about a third more than its base level.</summary>
    [Fact]
    public void AChainCostsAboutAThirdMore() {
        var settings = new TextureImportEdits { GenerateMips = true, Compression = TextureCompression.None };
        var levels = TextureLadder.Build(256, 256, settings);

        var total = TextureLadder.TotalBytes(levels);
        var baseLevel = levels[0].Bytes;

        Assert.True(total > baseLevel);
        Assert.True(total < baseLevel * 3 / 2);
    }

    /// <summary>A format nothing decodes is a sentence, not an exception.</summary>
    [Fact]
    public void AnUndecodableFormatSaysSo() {
        using var fixture = new EditorFixture();
        var path = fixture.Write("Assets/scene.exr", "not really an exr");

        Assert.Null(TextureLadder.TryDecode(path, out var reason));
        Assert.NotNull(reason);
        Assert.Contains(".exr", reason, StringComparison.Ordinal);
    }
}
