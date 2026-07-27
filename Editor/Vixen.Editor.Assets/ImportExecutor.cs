// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets;

/// <summary>One import, described in terms nothing has to be in this process to understand.</summary>
/// <param name="Guid">Which asset.</param>
/// <param name="Importer">Which importer, by name.</param>
/// <param name="Source">Where its source file is, as a virtual path.</param>
/// <param name="Settings">Its settings, as YAML with the per-target overrides already resolved.</param>
/// <param name="Target">Which build target.</param>
/// <param name="EnforceDeclaredReads">Whether an undeclared read fails the import.</param>
/// <remarks>
///     <b>YAML rather than a bound settings object</b>, and that is the point of the type. A bound
///     object cannot cross a process boundary without a serializer for every importer's settings
///     type; the text it was bound from crosses trivially and is what the cache key already hashes.
///     It also means the binding failure — and its message — happens in the same place whichever
///     executor runs the job.
/// </remarks>
public sealed record ImportJob(
    AssetId Guid,
    string Importer,
    VirtualPath Source,
    string Settings,
    string Target,
    bool EnforceDeclaredReads
);

/// <summary>What running one import produced, including what it declared it read.</summary>
/// <param name="Succeeded">Whether it came out with no errors.</param>
/// <param name="Artifacts">The chunks it produced.</param>
/// <param name="SubAssets">What the asset now declares it contains.</param>
/// <param name="Diagnostics">Everything the importer said.</param>
/// <param name="FileDependencies">Every file it declared, including its own source.</param>
/// <param name="AssetDependencies">Every other asset it declared.</param>
/// <remarks>
///     The dependencies are part of the result rather than read back off a context, because a context
///     is a live object with a file provider in it and the whole reason this type exists is that the
///     import may have happened somewhere else.
/// </remarks>
public sealed record ExecutedImport(
    bool Succeeded,
    IReadOnlyList<ImportedArtifact> Artifacts,
    IReadOnlyList<SubAssetEntry> SubAssets,
    IReadOnlyList<ImportDiagnostic> Diagnostics,
    IReadOnlyList<string> FileDependencies,
    IReadOnlyList<AssetId> AssetDependencies
) {
    /// <summary>An import that produced nothing and failed.</summary>
    /// <param name="message">Why.</param>
    /// <returns>The result.</returns>
    public static ExecutedImport Failed(string message) =>
        new(false, [], [], [new(ImportSeverity.Error, message)], [], []);
}

/// <summary>Whatever actually runs an importer.</summary>
/// <remarks>
///     The seam <c>Tools/Vixen.AssetCompiler</c> plugs into. Everything else about an import — which
///     importer claims the file, what the cache key is, whether anything needs to run at all, what
///     gets written back to the sidecar — stays in <see cref="ImportPipeline" /> where there is one
///     copy of it. What crosses the boundary is one asset's worth of work.
/// </remarks>
public interface IImportExecutor {
    /// <summary>Runs one import.</summary>
    /// <param name="job">What to import.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    /// <returns>What it produced.</returns>
    ValueTask<ExecutedImport> ExecuteAsync(ImportJob job, CancellationToken cancellationToken = default);
}

/// <summary>Runs an importer here, in this process.</summary>
/// <remarks>
///     What the pipeline did inline before there was anywhere else to do it, and still the default.
///     An importer that throws fails that asset and not the run; an importer that takes the process
///     down takes this with it, which is the difference the out-of-process executor exists for.
/// </remarks>
public sealed class InProcessImportExecutor(ImporterRegistry importers, IFileProvider files) : IImportExecutor {
    readonly ImporterRegistry importers = importers ?? throw new ArgumentNullException(nameof(importers));
    readonly IFileProvider files = files ?? throw new ArgumentNullException(nameof(files));

    /// <inheritdoc />
    public async ValueTask<ExecutedImport> ExecuteAsync(
        ImportJob job,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(job);

        if (!importers.TryGetByName(job.Importer, out var importer)) {
            return ExecutedImport.Failed(
                $"This build has no importer called '{job.Importer}'. Importers are registered in code, so a "
                + "sidecar naming one this build does not have is a project built with a different tool."
            );
        }

        IImportSettings settings;

        try {
            var node = YamlReader.Read(job.Settings) as YamlMapping ?? new YamlMapping { Tag = importer.Name };
            settings = (IImportSettings)YamlSerializer.Deserialize(node, importer.SettingsType)!;
        } catch (Exception failure) when (failure is YamlBindingException or YamlParseException) {
            return ExecutedImport.Failed($"Its import settings do not fit {importer.Name}: {failure.Message}");
        }

        var context = new ImportContext(
            job.Guid,
            job.Source,
            settings,
            files,
            importer.Name,
            job.Target,
            job.EnforceDeclaredReads
        );

        ImportResult result;

        try {
            result = await importer.ImportAsync(context, cancellationToken).ConfigureAwait(false);
        } catch (Exception failure) when (failure is not OperationCanceledException) {
            // One bad file must not stop the import of everything else — the difference, as doc 08
            // puts it, between "one bad asset" and "the editor won't open".
            return ExecutedImport.Failed($"{importer.Name} threw: {failure.Message}");
        }

        return new(
            result.Succeeded,
            result.Artifacts,
            result.SubAssets,
            result.Diagnostics,
            [.. context.FileDependencies.Select(path => path.ToString())],
            [.. context.AssetDependencies]
        );
    }
}
