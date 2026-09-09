// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Reflection;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>An import can turn an <see cref="AssetId" /> into a path, and cannot do it quietly.</summary>
/// <remarks>
///     <para>
///         <b>The wall this is the other side of.</b> Doc 47 § 2 chose a whole prefab-override file
///         format around "an importer cannot turn an <c>AssetId</c> into a path, so it cannot open the
///         prefab a scene names", and navigation's placement bake stopped at the same sentence. An
///         importer was given its own guid, its own source path, a provider over <em>paths</em>, and a
///         <c>DependsOn(AssetId)</c> that declares an edge and returns nothing.
///     </para>
///     <para>
///         ⚠ <b>The half that matters is not the lookup, it is that the lookup cannot come apart from
///         the edge.</b> A resolve that handed back a path without declaring would let an importer
///         read a template and never re-run when the template changed — a compiled artefact that is
///         silently stale, which is worse than the refusal it replaces. So both halves are asserted
///         here: that the resolved file can be <em>read</em> at all (the reads check refuses an
///         undeclared path), and that editing the resolved asset re-runs the importer that named it.
///     </para>
/// </remarks>
public sealed class ImportResolveTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-tests", Guid.NewGuid().ToString("N"));
    readonly ProjectPaths paths;

    public ImportResolveTests() {
        paths = new(root);
        Directory.CreateDirectory(paths.Assets);
    }

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    /// <summary>The referrer names the template by id, opens it, and gets its bytes.</summary>
    [Fact]
    public async Task AnImporterOpensTheAssetItNamesById() {
        Write("Assets/base.tpl", "the template");
        Write("Assets/derived.ref", "placeholder");

        var (pipeline, artifacts, database) = Pipeline();

        Point("Assets/derived.ref", Entry(database, "Assets/base.tpl").Guid);

        var outcome = await pipeline.ImportAsync(
            Entry(database, "Assets/derived.ref"),
            TestContext.Current.CancellationToken
        );

        Assert.True(outcome.Succeeded, string.Join("; ", outcome.Diagnostics.Select(entry => entry.Message)));

        // The artefact is the template's bytes and not the referrer's, so this cannot pass against an
        // importer that resolved a path and read its own source instead.
        Assert.Equal("the template", Read(artifacts, outcome));

        // ⚠ Both edges, asserted separately, because they fail differently and one of them fails
        // silently. Dropping the *file* edge makes the read throw and every test here go red at once;
        // dropping the *asset* edge changes nothing anybody can see — the file edge still re-imports
        // this one when the template is edited — and only shows up the day the template's own import
        // changes what it produces without its bytes changing. That half is only covered by looking
        // at the record.
        var record = outcome.Record;

        Assert.NotNull(record);
        Assert.Contains(Entry(database, "Assets/base.tpl").Guid, record.AssetDependencies);
        Assert.Contains("/Assets/base.tpl", record.FileDependencies);
    }

    /// <summary>
    ///     ⚠ And editing the template re-imports the file that named it, which is the property the
    ///     resolve exists to preserve.
    /// </summary>
    /// <remarks>
    ///     Asserted as work rather than as a message: the second import is <em>not</em> cached, and
    ///     the artefact carries the new bytes. An importer that resolved without declaring would pass
    ///     the first test in this file and fail this one, having produced a stale artefact with no
    ///     diagnostic anywhere.
    /// </remarks>
    [Fact]
    public async Task EditingTheResolvedAssetReimportsTheOneThatNamedIt() {
        Write("Assets/base.tpl", "the template");
        Write("Assets/derived.ref", "placeholder");

        var (pipeline, artifacts, database) = Pipeline();

        Point("Assets/derived.ref", Entry(database, "Assets/base.tpl").Guid);

        var referrer = Entry(database, "Assets/derived.ref");

        await pipeline.ImportAsync(referrer, TestContext.Current.CancellationToken);

        Assert.True((await pipeline.ImportAsync(referrer, TestContext.Current.CancellationToken)).WasCached);

        Write("Assets/base.tpl", "the template, edited");

        var again = await pipeline.ImportAsync(referrer, TestContext.Current.CancellationToken);

        Assert.False(again.WasCached);
        Assert.Equal("the template, edited", Read(artifacts, again));
    }

    /// <summary>An id nothing in the project has resolves to nothing, and declares nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>The control.</b> A resolve that declared the edge before knowing whether the asset
    ///     exists would put a phantom into the cache key, and a key naming an asset that is not there
    ///     is a key nothing can ever reproduce.
    /// </remarks>
    [Fact]
    public void AnIdTheProjectDoesNotHaveResolvesToNothing() {
        Write("Assets/base.tpl", "the template");

        var database = Database();
        var context = Context(new ProjectAssetSources(database));

        Assert.True(context.CanResolve);
        Assert.False(context.TryResolve(AssetId.New(), out _));
        Assert.Empty(context.AssetDependencies);

        // Its own source, and nothing else: a miss adds no file edge either.
        Assert.Single(context.FileDependencies);
    }

    /// <summary>
    ///     ⚠ And a context with no lookup says so, rather than saying the asset is missing.
    /// </summary>
    /// <remarks>
    ///     The two have opposite fixes — "add the asset" against "this import ran somewhere that
    ///     cannot look" — and an importer that reported the first for the second would be a message
    ///     about somebody's project that is not true. The out-of-process worker is the case that is
    ///     really in this state today.
    /// </remarks>
    [Fact]
    public void AContextWithNoLookupSaysSoRatherThanReportingTheAssetMissing() {
        var context = Context(sources: null);

        Assert.False(context.CanResolve);
        Assert.False(context.TryResolve(AssetId.New(), out _));
    }

    static string Read(ObjectDatabase artifacts, ImportOutcome outcome) {
        var record = outcome.Record;

        Assert.NotNull(record);

        return Encoding.UTF8.GetString(artifacts.ReadRaw(Assert.Single(record.Artifacts).Id, out _));
    }

    ImportContext Context(IAssetSources? sources) =>
        new(
            AssetId.New(),
            new("/Assets/derived.ref"),
            new TemplateReferenceImporter().CreateSettings(),
            new PhysicalFileProvider(root),
            "TemplateReferenceImporter",
            "Windows",
            true,
            sources
        );

    (ImportPipeline Pipeline, ObjectDatabase Artifacts, AssetDatabase Database) Pipeline() {
        var database = Database();
        var files = new VirtualFileSystem();

        files.Mount(new VirtualPath("/"), new MemoryFileProvider());

        var registry = new ImporterRegistry()
            .Add(new TemplateReferenceImporter())
            .Add(new FolderImporter())
            .AddFallback(new RawImporter());

        var artifacts = new ObjectDatabase(new FileOdbBackend(files, new VirtualPath("/artifacts")));

        return (new(database, registry, artifacts, new PhysicalFileProvider(root)), artifacts, database);
    }

    AssetDatabase Database() {
        var database = new AssetDatabase(paths);
        database.Scan();
        return database;
    }

    static AssetEntry Entry(AssetDatabase database, string path) {
        Assert.True(database.TryGetByPath(path, out var entry));
        return entry;
    }

    /// <summary>Writes the referenced id into the referrer's own settings, the way a real one would.</summary>
    /// <remarks>
    ///     The sidecar is rewritten around the guid the scan gave it, because a freshly scanned
    ///     <c>.meta</c> has no <c>importer</c> node at all until an import writes one back.
    /// </remarks>
    void Point(string relativePath, AssetId template) {
        var metaPath = Path.Combine(root, relativePath + ".meta");
        var document = (YamlMapping)YamlReader.Read(File.ReadAllText(metaPath));
        var guid = ((YamlScalar)document["guid"]!).Value;

        File.WriteAllText(
            metaPath,
            $"guid: {guid}\nmetaVersion: 1\nimporter: !TemplateReferenceImporter\n  template: {template}\n"
        );
    }

    void Write(string relativePath, string content) {
        var absolute = Path.Combine(root, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }
}

/// <summary>Settings naming another asset by id, which is the shape a prefab override has.</summary>
[DataContract("TemplateReferenceImporter")]
public sealed record TemplateReferenceImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;

    /// <summary>The asset this one is written against.</summary>
    public string Template { get; init; } = string.Empty;
}

/// <summary>
///     An importer that reads the asset it names by id — the thing no importer could do until
///     <see cref="ImportContext.TryResolve" /> existed.
/// </summary>
[Importer(".ref")]
public sealed class TemplateReferenceImporter : AssetImporter<TemplateReferenceImportSettings> {
    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        TemplateReferenceImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        if (!AssetId.TryParse(settings.Template, out var template)) {
            context.Report(ImportSeverity.Error, $"'{settings.Template}' is not an asset id.");
            return context.Finish();
        }

        if (!context.TryResolve(template, out var path)) {
            context.Report(
                ImportSeverity.Error,
                context.CanResolve
                    ? $"No asset in this project has the id {template}."
                    : "This import cannot resolve an asset id, so the template could not be opened."
            );

            return context.Finish();
        }

        // The read is the assertion: `Files` refuses anything the import has not declared, so this
        // line only works because resolving declared it.
        await using var stream = await context.Files.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        context.Write(SubAssetId.Main, "Template", buffer.ToArray());

        return context.Finish();
    }
}
