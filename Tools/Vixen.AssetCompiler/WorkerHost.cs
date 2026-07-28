// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets;

namespace Vixen.AssetCompiler;

/// <summary>What a worker process does: answer import requests until the pipe closes.</summary>
/// <remarks>
///     <para>
///         The whole worker is this loop and one executor. It holds no cache, writes no sidecars and
///         knows nothing about build plans — all of that stays in the coordinator, so there is one
///         copy of it and a worker cannot disagree with the process that started it.
///     </para>
///     <para>
///         <b>Its registry comes from <see cref="BuiltInImporters" />, not from an argument.</b> A
///         worker with a different importer set produces different artefacts for the same file, and
///         the disagreement surfaces as a cache that never hits — or as a build whose output depends
///         on how many cores the machine has.
///     </para>
/// </remarks>
public sealed class WorkerHost {
    readonly IImportExecutor executor;

    /// <summary>Serves imports out of a project directory.</summary>
    /// <param name="projectRoot">The project. Importer paths are relative to it.</param>
    public WorkerHost(string projectRoot) {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);

        executor = new InProcessImportExecutor(
            BuiltInImporters.Create(),
            new PhysicalFileProvider(projectRoot, isReadOnly: true)
        );
    }

    /// <summary>Serves imports through a given executor, which is what a test hands it.</summary>
    /// <param name="executor">Where importers run.</param>
    public WorkerHost(IImportExecutor executor) {
        ArgumentNullException.ThrowIfNull(executor);
        this.executor = executor;
    }

    /// <summary>Answers requests until the other end goes away.</summary>
    /// <param name="stream">The pipe.</param>
    /// <param name="cancellationToken">Stops serving.</param>
    /// <returns>The task.</returns>
    public async Task ServeAsync(Stream stream, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(stream);

        while (!cancellationToken.IsCancellationRequested) {
            var request = await Framing.ReadAsync<ImportRequestMessage>(stream, cancellationToken)
                .ConfigureAwait(false);

            if (request is null) {
                return;
            }

            var response = await RunAsync(request, cancellationToken).ConfigureAwait(false);
            await Framing.WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs one request.</summary>
    /// <param name="request">What to import.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    /// <returns>What to send back.</returns>
    public async Task<ImportResponseMessage> RunAsync(
        ImportRequestMessage request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        if (!AssetId.TryParse(request.Guid, null, out var guid)) {
            return Failed($"'{request.Guid}' is not an asset id.");
        }

        var job = new ImportJob(
            guid,
            request.Importer,
            new VirtualPath(request.Source),
            request.Settings,
            request.Target,
            request.EnforceDeclaredReads
        );

        var result = await executor.ExecuteAsync(job, cancellationToken).ConfigureAwait(false);

        return new() {
            Succeeded = result.Succeeded,
            Artifacts = [
                .. result.Artifacts.Select(artifact => new ArtifactMessage {
                        SubAsset = artifact.SubAsset.IsMain ? string.Empty : artifact.SubAsset.ToString(),
                        Type = artifact.Type,
                        Content = artifact.Content.ToArray()
                    }
                )
            ],
            SubAssets = [
                .. result.SubAssets.Select(entry => new SubAssetMessage {
                        Id = entry.Id.ToString(),
                        Name = entry.Name,
                        Type = entry.Type
                    }
                )
            ],
            Diagnostics = [
                .. result.Diagnostics.Select(diagnostic => new DiagnosticMessage {
                        Severity = (int)diagnostic.Severity,
                        Message = diagnostic.Message
                    }
                )
            ],
            FileDependencies = [.. result.FileDependencies],
            AssetDependencies = [.. result.AssetDependencies.Select(asset => asset.ToString())]
        };
    }

    static ImportResponseMessage Failed(string message) =>
        new() {
            Succeeded = false,
            Diagnostics = [new() { Severity = (int)ImportSeverity.Error, Message = message }]
        };

    /// <summary>Parses the arguments a worker is started with.</summary>
    /// <param name="arguments">What was on the command line.</param>
    /// <param name="pipe">The pipe to connect to.</param>
    /// <param name="root">The project directory.</param>
    /// <returns>Whether both were given.</returns>
    /// <remarks>
    ///     Hand-rolled rather than System.CommandLine, and that is the one place in the repository
    ///     where that is the right call: a worker is started by a coordinator and never by a person,
    ///     so there is no help text anybody reads and no completion anybody wants — and a parser is a
    ///     dependency this process would load on every start.
    /// </remarks>
    public static bool TryParse(IReadOnlyList<string> arguments, out string pipe, out string root) {
        pipe = string.Empty;
        root = string.Empty;

        ArgumentNullException.ThrowIfNull(arguments);

        for (var index = 0; index + 1 < arguments.Count; index++) {
            switch (arguments[index]) {
                case "--pipe":
                    pipe = arguments[index + 1];
                    break;

                case "--root":
                    root = arguments[index + 1];
                    break;
            }
        }

        return pipe.Length > 0 && root.Length > 0;
    }

    /// <summary>How many workers to run when nobody said.</summary>
    /// <remarks>
    ///     Cores minus one, as doc 08 asks, and never less than one. The spare core is for the
    ///     coordinator, which is doing real work between requests — hashing sources, writing chunks
    ///     and rewriting sidecars — and would otherwise be competing with the workers it is feeding.
    /// </remarks>
    public static int DefaultWorkerCount => Math.Max(1, Environment.ProcessorCount - 1);
}
