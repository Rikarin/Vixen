// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets;

namespace Vixen.AssetCompiler;

/// <summary>Runs importers in worker processes, and survives one of them dying.</summary>
/// <remarks>
///     <para>
///         <b>The promise is crash isolation, and it is not the same promise an exception handler
///         makes.</b> <c>ImportPipeline</c> already catches an importer that throws and fails that
///         one asset. What it cannot catch is an importer that takes the process down — a malformed
///         FBX inside a C++ library, a stack overflow inside a recursive scene graph, a native
///         access violation. Doc 08 calls that the difference between "one bad file" and "the editor
///         won't open", and the only way to have it is a process boundary.
///     </para>
///     <para>
///         <b>One pipe per worker, not one pipe with many instances.</b> A shared pipe would need
///         every message to carry a correlation id and every worker to be told which replies are
///         its own; a pipe per worker makes a request and its response the only two things on that
///         stream, so the framing is a length prefix and nothing else.
///     </para>
///     <para>
///         <b>Artefacts come back over the wire rather than being written by the worker.</b> N
///         processes writing into one content-addressed store is a correctness problem — partial
///         files, torn reads, no single place that knows what was written — for a saving that is a
///         memory copy. The coordinator stays the only writer, which is also what keeps the cache and
///         the sidecars in one copy.
///     </para>
///     <para>
///         <b>What this does not yet buy is parallelism.</b> The pool runs as many imports at once as
///         it is given, and <c>ImportPipeline</c> hands it one at a time — because its sequential,
///         path-ordered loop is what guarantees a dependent sees its dependency's new artefacts.
///         Dispatching independent assets concurrently needs a scheduler that knows the dependency
///         graph, and that is owed; see the README.
///     </para>
/// </remarks>
public sealed class CompilerPool : IImportExecutor, IDisposable {
    readonly ConcurrentBag<Worker> idle = [];
    readonly List<Worker> all = [];
    readonly SemaphoreSlim available;
    readonly Lock gate = new();
    readonly string projectRoot;
    readonly string executable;
    readonly IReadOnlyList<string> prefix;

    bool disposed;

    /// <summary>How many workers it may run at once.</summary>
    public int WorkerCount { get; }

    /// <summary>How many times a worker has died and been replaced.</summary>
    /// <remarks>
    ///     Counted rather than only logged, because "the build succeeded" and "the build succeeded
    ///     after three workers were killed by the same file" are different states and only one of
    ///     them is fine.
    /// </remarks>
    public int Restarts { get; private set; }

    /// <summary>Starts a pool over a project.</summary>
    /// <param name="projectRoot">The project. Importer paths are relative to it.</param>
    /// <param name="workers">How many, or zero for <see cref="WorkerHost.DefaultWorkerCount" />.</param>
    /// <param name="workerCommand">
    ///     What to run, as the executable followed by its leading arguments. Defaults to this
    ///     assembly under <c>dotnet</c>.
    /// </param>
    public CompilerPool(string projectRoot, int workers = 0, IReadOnlyList<string>? workerCommand = null) {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);

        this.projectRoot = Path.GetFullPath(projectRoot);
        WorkerCount = workers > 0 ? workers : WorkerHost.DefaultWorkerCount;
        available = new(WorkerCount, WorkerCount);

        var command = workerCommand ?? DefaultCommand();
        executable = command[0];
        prefix = [.. command.Skip(1)];
    }

    /// <summary>
    ///     How to start a worker when nobody said.
    /// </summary>
    /// <remarks>
    ///     <c>dotnet</c> plus this assembly's own path, because a <c>ProjectReference</c> copies the
    ///     dependency's assembly next to its consumer and not the native apphost that would launch
    ///     it. Naming the runtime explicitly is what makes the worker findable from the CLI, from the
    ///     editor and from a test, all of which have different base directories and none of which
    ///     have an executable called <c>Vixen.AssetCompiler</c> in them.
    /// </remarks>
    public static IReadOnlyList<string> DefaultCommand() => [
        Environment.ProcessPath is { Length: > 0 } host && Path.GetFileNameWithoutExtension(host) == "dotnet"
            ? host
            : "dotnet",
        Path.Combine(AppContext.BaseDirectory, "Vixen.AssetCompiler.dll")
    ];

    /// <inheritdoc />
    public async ValueTask<ExecutedImport> ExecuteAsync(
        ImportJob job,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(job);
        ObjectDisposedException.ThrowIf(disposed, this);

        await available.WaitAsync(cancellationToken).ConfigureAwait(false);

        Worker? worker = null;

        try {
            worker = idle.TryTake(out var existing) ? existing : await StartAsync(cancellationToken).ConfigureAwait(false);

            var response = await worker.AskAsync(Request(job), cancellationToken).ConfigureAwait(false);

            if (response is null) {
                // The pipe ended mid-request, which is what a worker dying looks like from here.
                // That is the whole reason this class exists, so it is a failed asset and not a
                // failed run — and the worker is not put back.
                Retire(worker);
                worker = null;

                return ExecutedImport.Failed(
                    $"The worker importing this file stopped. That is a crash inside {job.Importer} rather than "
                    + "an error it reported, so the file itself is the thing to look at; the rest of the import "
                    + "carries on."
                );
            }

            idle.Add(worker);
            worker = null;

            return Translate(response);
        } catch (Exception) when (worker is not null) {
            // Including cancellation: a worker interrupted mid-request has a half-read message in
            // its pipe and cannot be handed the next one.
            Retire(worker);
            worker = null;
            throw;
        } finally {
            available.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        lock (gate) {
            foreach (var worker in all) {
                worker.Dispose();
            }

            all.Clear();
        }

        idle.Clear();
        available.Dispose();
    }

    /// <summary>The workers this pool has started, for a test that wants to kill one.</summary>
    internal IReadOnlyList<Worker> Workers {
        get {
            lock (gate) {
                return [.. all];
            }
        }
    }

    async Task<Worker> StartAsync(CancellationToken cancellationToken) {
        // A fresh name per worker, so two pools in one process — or two builds on one machine —
        // never meet on the same pipe.
        var name = $"vixen-ac-{System.Guid.NewGuid():N}";

        var server = new NamedPipeServerStream(
            name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
        );

        var start = new ProcessStartInfo(executable) { UseShellExecute = false };

        foreach (var argument in prefix) {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(name);
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(projectRoot);

        Process process;

        try {
            process = Process.Start(start) ?? throw new InvalidOperationException("no process");
        } catch (Exception failure) {
            server.Dispose();

            throw new InvalidOperationException(
                $"The asset-compiler worker could not be started as '{executable}'. Out-of-process importing "
                + "needs it beside the tool that is asking for it.",
                failure
            );
        }

        try {
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        } catch (Exception) {
            Kill(process);
            server.Dispose();
            throw;
        }

        var worker = new Worker(process, server);

        lock (gate) {
            all.Add(worker);
        }

        return worker;
    }

    void Retire(Worker worker) {
        lock (gate) {
            all.Remove(worker);
            Restarts++;
        }

        worker.Dispose();
    }

    static ImportRequestMessage Request(ImportJob job) =>
        new() {
            Guid = job.Guid.ToString(),
            Importer = job.Importer,
            Source = job.Source.ToString(),
            Settings = job.Settings,
            Target = job.Target,
            EnforceDeclaredReads = job.EnforceDeclaredReads
        };

    static ExecutedImport Translate(ImportResponseMessage response) =>
        new(
            response.Succeeded,
            [
                .. response.Artifacts.Select(artifact => new ImportedArtifact(
                        artifact.SubAsset.Length == 0 || !SubAssetId.TryParse(artifact.SubAsset, null, out var sub)
                            ? SubAssetId.Main
                            : sub,
                        artifact.Type,
                        artifact.Content
                    )
                )
            ],
            [
                .. response.SubAssets.Select(entry => new SubAssetEntry {
                        Id = SubAssetId.TryParse(entry.Id, null, out var id) ? id : SubAssetId.Main,
                        Name = entry.Name,
                        Type = entry.Type
                    }
                )
            ],
            [.. response.Diagnostics.Select(diagnostic => new ImportDiagnostic((ImportSeverity)diagnostic.Severity, diagnostic.Message))],
            response.FileDependencies,
            [.. response.AssetDependencies.Select(text => AssetId.TryParse(text, null, out var id) ? id : AssetId.Empty).Where(id => !id.IsEmpty)]
        );

    static void Kill(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        } catch (InvalidOperationException) {
            // It exited between the check and the kill, which is the outcome that was wanted.
        }

        process.Dispose();
    }

    /// <summary>One worker process and the pipe to it.</summary>
    internal sealed class Worker(Process process, NamedPipeServerStream pipe) : IDisposable {
        readonly SemaphoreSlim one = new(1, 1);

        /// <summary>The process, so a test can kill it the way a crash would.</summary>
        internal Process Process { get; } = process;

        /// <summary>Sends a request and waits for its answer.</summary>
        /// <returns>The response, or <see langword="null" /> if the worker went away.</returns>
        internal async Task<ImportResponseMessage?> AskAsync(
            ImportRequestMessage request,
            CancellationToken cancellationToken
        ) {
            await one.WaitAsync(cancellationToken).ConfigureAwait(false);

            try {
                await Framing.WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
                return await Framing.ReadAsync<ImportResponseMessage>(pipe, cancellationToken).ConfigureAwait(false);
            } catch (Exception failure) when (failure is IOException or ObjectDisposedException) {
                // A broken pipe is a dead worker, and it is reported the same way an orderly end of
                // stream is: null. The caller cannot act differently on the two and should not have
                // to tell them apart.
                return null;
            } finally {
                one.Release();
            }
        }

        public void Dispose() {
            pipe.Dispose();
            Kill(Process);
            one.Dispose();
        }
    }
}
