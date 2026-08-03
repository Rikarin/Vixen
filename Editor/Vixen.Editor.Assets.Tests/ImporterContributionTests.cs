// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>Doc 36 § F8: an importer something other than this build contributed.</summary>
/// <remarks>
///     <para>
///         <b>F8 said "importers are constructed and handed in; there is no registry for a plugin to
///         add to", and the second half was the part that mattered.</b> <see cref="ImporterRegistry" />
///         has existed all along — it is built fresh per run by <see cref="BuiltInImporters.Create" />,
///         inside a background task, so that the editor and the CLI cannot disagree about the set. A
///         plugin had nothing to add to because every registry it could reach was about to be thrown
///         away.
///     </para>
///     <para>
///         What was needed is a set that outlives a run, which is <see cref="ImporterContributions" />.
///     </para>
/// </remarks>
public sealed class ImporterContributionTests {
    /// <remarks>
    ///     ⚠ <b>A set of its own rather than <c>ImporterContributions.Default</c>.</b> The default has
    ///     to be a process-wide singleton — the callers are static factories in background tasks with
    ///     no editor to be handed — so a suite that mutated it would race every other test in this
    ///     assembly that builds a registry. <c>Create()</c> is one line over <c>Create(contributed)</c>,
    ///     which is what these drive.
    /// </remarks>
    readonly ImporterContributions contributions = new();

    [Fact]
    public void AContributedImporterIsInEveryRegistryBuiltAfterwards() {
        using (contributions.Add(new PaletteImporter())) {
            var registry = BuiltInImporters.Create(contributions);

            Assert.True(registry.TryGetForFile("Assets/swatch.pal", out var importer));
            Assert.Equal("PaletteImporter", importer.Name);
        }

        // ⚠ And the *next* registry does not have it. A contribution that outlived its contributor
        // would mean an unloaded plugin's importer claiming files in the project after it, which is
        // the leak this whole path is arranged to avoid.
        var after = BuiltInImporters.Create(contributions);

        Assert.False(after.TryGetForFile("Assets/swatch.pal", out var stale) && stale.Name == "PaletteImporter");
    }

    /// <summary>
    ///     ⚠ <b>Folded in after the built-ins, so a contribution cannot silently take an extension
    ///     one of them already claims.</b> <c>ImporterRegistry</c> refuses with both names for the
    ///     reason it gives: an artist's PNG being imported as a cubemap depending on load order is
    ///     not a thing anybody can debug.
    /// </summary>
    [Fact]
    public void AContributionThatCollidesWithABuiltInIsRefusedWithBothNames() {
        using var _ = contributions.Add(new RivalTextureImporter());

        var failure = Assert.Throws<InvalidOperationException>(() => BuiltInImporters.Create(contributions));

        Assert.Contains("RivalTextureImporter", failure.Message, StringComparison.Ordinal);
        Assert.Contains(".png", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The fallback stays the fallback.</b> A contribution is added before
    ///     <c>RawImporter</c>, because <c>RawImporter</c> takes anything nothing else claimed and a
    ///     contributed importer is something else claiming it.
    /// </summary>
    [Fact]
    public void AContributionDoesNotDisplaceTheFallback() {
        using var _ = contributions.Add(new PaletteImporter());

        var registry = BuiltInImporters.Create(contributions);

        Assert.Equal("RawImporter", registry.Fallback?.Name);
        Assert.True(registry.TryGetForFile("Assets/notes.unheardof", out var anything));
        Assert.Equal("RawImporter", anything.Name);
    }

    [Fact]
    public void WithdrawingTwiceIsNotAnError() {
        var scope = contributions.Add(new PaletteImporter());

        scope.Dispose();
        scope.Dispose();

        Assert.Empty(contributions.All);
    }
}

/// <summary>Settings for a contribution that wants an extension a built-in already claims.</summary>
[Vixen.Core.DataContract("RivalTextureImporter")]
public sealed record RivalTextureImportSettings : Vixen.Core.Yaml.Meta.IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>A contribution that collides with <c>TextureImporter</c>.</summary>
[Importer(".png")]
public sealed class RivalTextureImporter : AssetImporter<RivalTextureImportSettings> {
    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        RivalTextureImportSettings settings,
        CancellationToken cancellationToken
    ) =>
        ValueTask.FromResult(context.Finish());
}
