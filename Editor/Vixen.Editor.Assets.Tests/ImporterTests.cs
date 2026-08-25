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

    /// <summary>
    ///     ⚠ <b>Suffixed and warned about rather than refused, which is a change of mind.</b> Two
    ///     meshes with one name is what a <c>.glb</c> from anywhere but your own DCC tool looks like,
    ///     and failing the whole asset with "rename one of them" was advice nothing in the editor
    ///     could act on — the names are in the file.
    /// </summary>
    [Fact]
    public async Task TwoSubAssetsWithOneNameAreSuffixedRatherThanRefused() {
        var files = Provider(("/Assets/hero.png", "PNG"));
        var context = Context(files, new PaletteImporter());

        context.DeclareSubAsset("Palette", "swatch");
        context.DeclareSubAsset("Palette", "swatch");

        var result = context.Finish();

        Assert.Equal(["swatch", "swatch_1"], result.SubAssets.Select(entry => entry.Name));

        // The second one records what the file called it, which is the key a rename is stored under.
        Assert.Equal(["", "swatch"], result.SubAssets.Select(entry => entry.Source));

        // And it is said out loud, because the suffix depends on the order they appear in the file —
        // the one property a derived id exists to avoid, and the one case where it cannot.
        var warning = Assert.Single(result.Diagnostics);

        Assert.Equal(ImportSeverity.Warning, warning.Severity);
        Assert.Contains("swatch_1", warning.Message, StringComparison.Ordinal);

        await Task.CompletedTask;
    }

    /// <summary>
    ///     ⚠ And a suffix that would land on a name the file genuinely uses keeps bumping, or the
    ///     collision would simply move.
    /// </summary>
    [Fact]
    public async Task AGeneratedNameNeverStealsOneTheFileAlreadyUses() {
        var files = Provider(("/Assets/hero.png", "PNG"));
        var context = Context(files, new PaletteImporter());

        context.DeclareSubAsset("Palette", "swatch");
        context.DeclareSubAsset("Palette", "swatch_1");
        context.DeclareSubAsset("Palette", "swatch");

        Assert.Equal(
            ["swatch", "swatch_1", "swatch_2"],
            context.Finish().SubAssets.Select(entry => entry.Name)
        );

        await Task.CompletedTask;
    }

    /// <summary>
    ///     ⚠ A name of one kind does not collide with the same name of another: an FBX with a mesh and
    ///     a material both called Body is ordinary, and their ids differ where their names do not.
    /// </summary>
    [Fact]
    public async Task TwoKindsMayShareAName() {
        var files = Provider(("/Assets/hero.png", "PNG"));
        var context = Context(files, new PaletteImporter());

        context.DeclareSubAsset("Palette", "Body");
        context.DeclareSubAsset("Mesh", "Body");

        Assert.Equal(["Body", "Body"], context.Finish().SubAssets.Select(entry => entry.Name));
        await Task.CompletedTask;
    }

    /// <summary>
    ///     ⚠ <b>The author's rename lands before the id is derived, and is keyed by what the file
    ///     calls the thing.</b> That is what makes it survive a re-export that reorders the meshes —
    ///     which is the whole reason an id comes from a name rather than a position.
    /// </summary>
    [Fact]
    public async Task AnAuthorsRenameIsAppliedToWhatTheSourceCalledIt() {
        var files = Provider(("/Assets/hero.png", "PNG"));

        var context = new ImportContext(
            AssetId.New(),
            Source,
            new Models.ModelImportSettings { SubAssetNames = [new() { Source = "Cube", Name = "Body" }] },
            files,
            "ModelImporter"
        );

        context.DeclareSubAsset("Mesh", "Cube");
        context.DeclareSubAsset("Mesh", "Cube");

        var result = context.Finish();

        // Both go through the rename, because the file cannot tell them apart — so the second one
        // still takes a suffix, off the renamed stem rather than the original.
        Assert.Equal(["Body", "Body_1"], result.SubAssets.Select(entry => entry.Name));
        Assert.Equal(SubAssets.Derive("ModelImporter", "Mesh", "Body"), result.SubAssets[0].Id);

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

    /// <summary>
    ///     Whether to open every other asset in the project — the accidental O(n²) an import budget
    ///     exists to catch, on purpose, so that <c>ImportBudgetTests</c> has a defect to point its
    ///     instrument at.
    /// </summary>
    /// <remarks>
    ///     ⚠ Undeclared reads, which is why a pipeline using this has to turn
    ///     <see cref="ImportPipeline.EnforceDeclaredReads" /> off. That is the shape of the
    ///     regression: an importer quietly acquiring a walk over the project, not one that announces
    ///     a dependency on ten thousand files.
    /// </remarks>
    public bool ReadsEveryPeer { get; init; }

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

        if (ReadsEveryPeer) {
            foreach (var peer in context.Files.Enumerate(new VirtualPath("/Assets"), recursive: true)) {
                if (!peer.IsDirectory && peer.Path.ToString().EndsWith(".pal", StringComparison.Ordinal)) {
                    await using var opened = await context.Files.OpenReadAsync(peer.Path, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

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
