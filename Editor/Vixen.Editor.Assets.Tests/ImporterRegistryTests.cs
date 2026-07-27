// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Editor.Assets.Tests;

public sealed class ImporterRegistryTests {
    [Fact]
    public void AnImporterIsFoundByExtensionAndByName() {
        var registry = new ImporterRegistry().Add(new PaletteImporter());

        Assert.True(registry.TryGetForFile("Assets/swatch.pal", out var byExtension));
        Assert.Equal("PaletteImporter", byExtension.Name);
        Assert.True(registry.TryGetByName("PaletteImporter", out _));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void AnExtensionIsMatchedWhateverItsCase() {
        var registry = new ImporterRegistry().Add(new PaletteImporter());

        Assert.True(registry.TryGetForFile("Assets/SWATCH.PAL", out _));
    }

    /// <summary>
    ///     Two importers claiming one extension is an error naming both. Last-one-wins would mean an
    ///     artist's file being imported as the wrong kind of thing because a plugin loaded in a
    ///     different order today.
    /// </summary>
    [Fact]
    public void TwoImportersClaimingOneExtensionIsAnError() {
        var registry = new ImporterRegistry().Add(new PaletteImporter());

        var failure = Assert.Throws<InvalidOperationException>(() => registry.Add(new RivalPaletteImporter()));

        Assert.Contains("PaletteImporter", failure.Message, StringComparison.Ordinal);
        Assert.Contains("RivalPaletteImporter", failure.Message, StringComparison.Ordinal);
        Assert.Contains(".pal", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     "This format has no importer yet" is a shrug rather than a blocker: a game that wants to
    ///     ship a CSV or a licence file gets an address for it today.
    /// </summary>
    [Fact]
    public void TheFallbackTakesAnythingNothingElseClaimed() {
        var registry = new ImporterRegistry().Add(new PaletteImporter()).AddFallback(new RawImporter());

        Assert.True(registry.TryGetForFile("Assets/credits.txt", out var fallback));
        Assert.Equal("RawImporter", fallback.Name);

        Assert.True(registry.TryGetForFile("Assets/swatch.pal", out var claimed));
        Assert.Equal("PaletteImporter", claimed.Name);
    }

    [Fact]
    public void WithNoFallbackAnUnclaimedFileIsSimplyUnclaimed() {
        var registry = new ImporterRegistry().Add(new PaletteImporter());

        Assert.False(registry.TryGetForFile("Assets/credits.txt", out _));
    }
}

/// <summary>Settings for an importer that wants an extension somebody else has.</summary>
[Vixen.Core.DataContract("RivalPaletteImporter")]
public sealed record RivalPaletteImportSettings : Vixen.Core.Yaml.Meta.IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>An importer that wants an extension somebody else has.</summary>
[Importer(".pal")]
public sealed class RivalPaletteImporter : AssetImporter<RivalPaletteImportSettings> {
    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        RivalPaletteImportSettings settings,
        CancellationToken cancellationToken
    ) =>
        ValueTask.FromResult(context.Finish());
}
