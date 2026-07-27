// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Yaml.Meta;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

public sealed class ImporterTests {
    static readonly VirtualPath Source = new VirtualPath("/Assets/hero.png");

    [Fact]
    public void AnImportersNameIsItsSettingsContractName() {
        var importer = new PaletteImporter();

        Assert.Equal("PaletteImporter", importer.Name);
        Assert.Equal(typeof(PaletteImportSettings), importer.SettingsType);
        Assert.Equal([".pal"], importer.Extensions);
    }

    [Fact]
    public async Task AnImporterReadsItsSourceWithoutDeclaringIt() {
        var files = Provider(("/Assets/hero.png", "PNG"));
        var context = Context(files, new RawImporter());

        var result = await new RawImporter().ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("PNG", Encoding.UTF8.GetString(Assert.Single(result.Artifacts).Content.Span));
    }

    /// <summary>
    ///     The single most valuable check in the pipeline. An importer that quietly reads a sibling
    ///     produces an artefact that is correct today and stale for ever — the file it read can
    ///     change and nothing will re-run it. That failure surfaces as an artist rebuilding and
    ///     getting the old result, once, on one machine.
    /// </summary>
    [Fact]
    public async Task ReadingAFileWithoutDeclaringItFailsAtTheRead() {
        var files = Provider(("/Assets/hero.png", "PNG"), ("/Assets/shared.pal", "palette"));
        var context = Context(files, new PaletteImporter());

        var failure = await Assert.ThrowsAsync<UnregisteredReadException>(
            async () => await new PaletteImporter { Declare = false }.ImportAsync(
                context,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(new VirtualPath("/Assets/shared.pal"), failure.Path);
        Assert.Contains("DependsOnFile", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeclaringItFirstIsAllItTakes() {
        var files = Provider(("/Assets/hero.png", "PNG"), ("/Assets/shared.pal", "palette"));
        var context = Context(files, new PaletteImporter());

        var result = await new PaletteImporter().ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Contains(new VirtualPath("/Assets/shared.pal"), context.FileDependencies);
    }

    /// <summary>
    ///     Existence and metadata are not reads of content. An importer legitimately probes for a
    ///     sibling before deciding whether it depends on one, and being unable to look would make
    ///     the check unusable.
    /// </summary>
    [Fact]
    public void LookingWhetherAFileExistsIsNotAReadOfIt() {
        var files = Provider(("/Assets/hero.png", "PNG"), ("/Assets/shared.pal", "palette"));
        var context = Context(files, new PaletteImporter());

        Assert.True(context.Files.Exists(new VirtualPath("/Assets/shared.pal")));
        Assert.DoesNotContain(new VirtualPath("/Assets/shared.pal"), context.FileDependencies);
    }

    [Fact]
    public async Task TheCheckCanBeTurnedOffAndThenNothingIsRefused() {
        var files = Provider(("/Assets/hero.png", "PNG"), ("/Assets/shared.pal", "palette"));
        var context = Context(files, new PaletteImporter(), enforce: false);

        var result = await new PaletteImporter { Declare = false }.ImportAsync(
            context,
            TestContext.Current.CancellationToken
        );

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task SubAssetsAreDeclaredThroughTheContextSoEveryImporterGetsOneRule() {
        var files = Provider(("/Assets/hero.png", "PNG"));
        var context = Context(files, new PaletteImporter());

        var result = await new PaletteImporter { SubAssetName = "swatch" }.ImportAsync(
            context,
            TestContext.Current.CancellationToken
        );

        var entry = Assert.Single(result.SubAssets);
        Assert.Equal("swatch", entry.Name);
        Assert.Equal("Palette", entry.Type);
        Assert.Equal(SubAssets.Derive("PaletteImporter", "Palette", "swatch"), entry.Id);
    }

    [Fact]
    public async Task TwoSubAssetsThatDeriveOneIdAreRefusedRatherThanSilentlyMerged() {
        var files = Provider(("/Assets/hero.png", "PNG"));
        var context = Context(files, new PaletteImporter());

        context.DeclareSubAsset("Palette", "swatch");
        context.DeclareSubAsset("Palette", "swatch");

        Assert.Throws<SubAssetCollisionException>(context.Finish);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnImporterCanSayWhatWentWrongWithoutThrowing() {
        var files = Provider(("/Assets/hero.png", "PNG"));
        var context = Context(files, new PaletteImporter());

        var result = await new PaletteImporter { Complaint = "the palette has 257 colours" }.ImportAsync(
            context,
            TestContext.Current.CancellationToken
        );

        Assert.False(result.Succeeded);
        Assert.Equal(ImportSeverity.Error, Assert.Single(result.Diagnostics).Severity);
    }

    static ImportContext Context(IFileProvider files, IAssetImporter importer, bool enforce = true) =>
        new(
            AssetId.New(),
            Source,
            importer.CreateSettings(),
            files,
            importer.Name,
            "Windows",
            enforce
        );

    static MemoryFileProvider Provider(params (string Path, string Content)[] files) {
        var provider = new MemoryFileProvider();

        foreach (var (path, content) in files) {
            provider.Seed(new VirtualPath(path), content);
        }

        return provider;
    }
}

/// <summary>Settings for the fixture importer.</summary>
[DataContract("PaletteImporter")]
public sealed record PaletteImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>
///     An importer that reads a second file, which is the shape every real importer has and the one
///     the declared-reads check exists for.
/// </summary>
[Importer(".pal")]
public sealed class PaletteImporter : AssetImporter<PaletteImportSettings> {
    /// <summary>Whether it declares the sibling before reading it.</summary>
    public bool Declare { get; init; } = true;

    /// <summary>A sub-asset to declare, or <see langword="null" />.</summary>
    public string? SubAssetName { get; init; }

    /// <summary>Another asset to declare a dependency on, so the key picks up its artefacts.</summary>
    public AssetId DependsOnAsset { get; init; }

    /// <summary>Something to complain about, or <see langword="null" />.</summary>
    public string? Complaint { get; init; }

    /// <summary>Whether to throw rather than return, standing in for a malformed file.</summary>
    public bool Explode { get; init; }

    /// <summary>What to report as its version, so a bump can be tested.</summary>
    public int VersionOverride { get; init; } = 1;

    /// <inheritdoc />
    public override int Version => VersionOverride;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        PaletteImportSettings settings,
        CancellationToken cancellationToken
    ) {
        if (Explode) {
            throw new InvalidOperationException("this palette is malformed beyond recovery");
        }

        if (Complaint is not null) {
            context.Report(ImportSeverity.Error, Complaint);
            return context.Finish();
        }

        context.DependsOn(DependsOnAsset);

        var palette = new VirtualPath("/Assets/shared.pal");

        if (context.Files.Exists(palette)) {
            if (Declare) {
                context.DependsOnFile(palette);
            }

            await using var stream = await context.Files.OpenReadAsync(palette, cancellationToken)
                .ConfigureAwait(false);

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        // The source's own bytes, so that changing the file moves the artefact's id — which is what
        // a dependent's cache key is made of, and what makes a dependency's change reach it.
        await using var own = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false);
        using var content = new MemoryStream();
        await own.CopyToAsync(content, cancellationToken).ConfigureAwait(false);

        context.Write(SubAssetId.Main, "Palette", content.ToArray());

        if (SubAssetName is not null) {
            // A second chunk, under a sub-asset of its own — the shape a model importer has, where
            // the asset is one thing and the meshes inside it are others.
            context.Write(context.DeclareSubAsset("Palette", SubAssetName), "Palette", new byte[] { 4, 5, 6 });
        }

        return context.Finish();
    }
}
