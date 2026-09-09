// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Reflection;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Xunit;

namespace Vixen.AssetCompiler.Tests;

/// <summary>An import that resolves an asset id gets the same answer in a worker as in the editor.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>It did not, and nothing said so</b> (#1150). <c>ImportContext.TryResolve</c> landed
///         with an <see cref="IAssetSources" /> the host supplies, <c>ImportPipeline</c> supplies one
///         over the project's <see cref="AssetDatabase" />, and <see cref="WorkerHost" /> supplied
///         none — so a crash-isolated build answered <c>CanResolve</c> false and missed every
///         resolve. Not silently, which is why it was a task and not a defect, but it made any asset
///         kind that needs a resolve quietly in-process-only.
///     </para>
///     <para>
///         <b>The negative half is as load-bearing as the positive one.</b> "This build cannot look"
///         and "no asset in this project has that id" have opposite fixes, so a worker over a project
///         with no index must go on saying the first — which is what a resolver that was always
///         present and always missed would destroy.
///     </para>
/// </remarks>
public sealed class WorkerResolveTests : IDisposable {
    readonly string project = Path.Combine(
        Path.GetTempPath(),
        $"vixen-workerresolve-{Environment.ProcessId}-{Guid.NewGuid():N}"
    );

    readonly ProjectPaths paths;

    public WorkerResolveTests() {
        paths = new(project);
        Directory.CreateDirectory(paths.Assets);
        File.WriteAllText(Path.Combine(paths.Assets, "base.tpl"), "the template");
        File.WriteAllText(Path.Combine(paths.Assets, "derived.ref"), "placeholder");
    }

    public void Dispose() {
        try {
            if (Directory.Exists(project)) {
                Directory.Delete(project, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    /// <summary>The worker opens the asset the job names by id, and comes back with its bytes.</summary>
    /// <remarks>
    ///     The artefact is the template's content and not the referrer's, so this cannot pass against
    ///     a worker that resolved nothing and read its own source instead.
    /// </remarks>
    [Fact]
    public async Task AWorkerResolvesThroughTheIndexTheCoordinatorSaved() {
        var template = Saved();
        var response = await Host().RunAsync(Request(template), Cancellation);

        Assert.True(response.Succeeded, string.Join("; ", response.Diagnostics.Select(entry => entry.Message)));
        Assert.Equal("the template", Encoding.UTF8.GetString(Assert.Single(response.Artifacts).Content));

        // The edge crosses too, because it is what the coordinator's cache key is made of. A resolve
        // that produced the bytes and lost the dependency would give a worker artefacts that never
        // go stale.
        Assert.Contains(template.ToString(), response.AssetDependencies);
    }

    /// <summary>
    ///     ⚠ And a worker over a project with no saved index says it cannot look, rather than
    ///     reporting the asset missing.
    /// </summary>
    [Fact]
    public async Task AWorkerWithNoIndexSaysItCannotLookRatherThanReportingTheAssetMissing() {
        // Scanned but never saved: the assets are there, the index on disk is not.
        var database = new AssetDatabase(paths);
        database.Scan();

        Assert.True(database.TryGetByPath("Assets/base.tpl", out var entry));

        var response = await Host().RunAsync(Request(entry.Guid), Cancellation);

        Assert.False(response.Succeeded);

        Assert.Equal(
            "This import cannot resolve an asset id, so the template could not be opened.",
            Assert.Single(response.Diagnostics).Message
        );
    }

    /// <summary>The lookup itself is null when there is no index and a real one when there is.</summary>
    /// <remarks>
    ///     The two tests above go through <see cref="WorkerHost.RunAsync" /> and are what prove the
    ///     wiring; this one names the seam directly so a failure says which half moved.
    /// </remarks>
    [Fact]
    public void TheLookupIsNullUntilTheIndexIsOnDisk() {
        Assert.Null(WorkerHost.Sources(project));

        var template = Saved();
        var sources = WorkerHost.Sources(project);

        Assert.NotNull(sources);
        Assert.True(sources.TryGetPath(template, out var path));
        Assert.Equal("/Assets/base.tpl", path.ToString());
    }

    /// <summary>Scans and writes <c>Library/GuidIndex</c>, which is what the coordinator does.</summary>
    /// <returns>The template's id.</returns>
    AssetId Saved() {
        var database = new AssetDatabase(paths);

        database.Scan();
        database.Save();

        Assert.True(database.TryGetByPath("Assets/base.tpl", out var entry));
        return entry.Guid;
    }

    WorkerHost Host() =>
        new(
            project,
            new ImporterRegistry()
                .Add(new WorkerTemplateImporter())
                .Add(new FolderImporter())
                .AddFallback(new RawImporter())
        );

    static ImportRequestMessage Request(AssetId template) =>
        new() {
            Guid = AssetId.New().ToString(),
            Importer = "WorkerTemplateImporter",
            Source = "/Assets/derived.ref",
            Settings = $"template: {template}\n",
            Target = "Windows",
            EnforceDeclaredReads = true
        };

    static CancellationToken Cancellation => TestContext.Current.CancellationToken;
}

/// <summary>Settings naming another asset by id, which is the shape a prefab override has.</summary>
[DataContract("WorkerTemplateImporter")]
public sealed record WorkerTemplateImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;

    /// <summary>The asset this one is written against.</summary>
    public string Template { get; init; } = string.Empty;
}

/// <summary>An importer that reads the asset it names by id.</summary>
/// <remarks>
///     ⚠ No built-in importer resolves an id today, so the out-of-process resolve path had no
///     production caller to test through. That is the reason this exists rather than a built-in one
///     being driven — and it is worth knowing that the capability's only exercise anywhere is a
///     fixture like this one.
/// </remarks>
[Importer(".ref")]
public sealed class WorkerTemplateImporter : AssetImporter<WorkerTemplateImportSettings> {
    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        WorkerTemplateImportSettings settings,
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

        await using var stream = await context.Files.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        context.Write(SubAssetId.Main, "Template", buffer.ToArray());

        return context.Finish();
    }
}
