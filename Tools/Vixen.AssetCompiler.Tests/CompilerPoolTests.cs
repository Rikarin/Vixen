// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Editor.Assets;
using Xunit;

namespace Vixen.AssetCompiler.Tests;

/// <summary>
///     Against the real worker binary, over a real pipe, importing real files. There is no fake
///     worker anywhere in here on purpose: the entire claim this class makes is about what happens
///     when another process dies, and a fake that "dies" by returning null would be testing the
///     handling of a value this code writes itself.
/// </summary>
public sealed class CompilerPoolTests : IDisposable {
    readonly string project = Path.Combine(
        Path.GetTempPath(),
        $"vixen-assetcompiler-{Environment.ProcessId}-{Guid.NewGuid():N}"
    );

    public CompilerPoolTests() {
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        File.WriteAllText(Path.Combine(project, "Assets", "notes.txt"), "the quick brown fox");
        File.WriteAllText(Path.Combine(project, "Assets", "hero.vxmat"), "shader: Standard\n");
    }

    public void Dispose() {
        if (Directory.Exists(project)) {
            Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public async Task AnImportRunsInAnotherProcessAndComesBackWhole() {
        using var pool = new CompilerPool(project, workers: 1);

        var result = await pool.ExecuteAsync(Job("RawImporter", "/Assets/notes.txt"), Cancellation);

        Assert.True(result.Succeeded);
        Assert.Equal("the quick brown fox", Encoding.UTF8.GetString(Assert.Single(result.Artifacts).Content.Span));
        Assert.Contains("/Assets/notes.txt", result.FileDependencies);
    }

    /// <summary>
    ///     The dependency graph has to survive the trip, because it is what the coordinator puts in
    ///     the cache key. An out-of-process import that lost it would produce artefacts that never
    ///     go stale.
    /// </summary>
    [Fact]
    public async Task DeclaredDependenciesCrossTheBoundary() {
        File.WriteAllText(
            Path.Combine(project, "Assets", "hero.vxmat"),
            "shader: Standard\nalbedo: vx:9e8a44c9930c64e388ca034c5fe4c426\n"
        );

        using var pool = new CompilerPool(project, workers: 1);

        var result = await pool.ExecuteAsync(Job("NativeFormatImporter", "/Assets/hero.vxmat"), Cancellation);

        Assert.True(result.Succeeded);
        Assert.Equal(
            new AssetId(Guid.Parse("9e8a44c9930c64e388ca034c5fe4c426")),
            Assert.Single(result.AssetDependencies)
        );
    }

    [Fact]
    public async Task WhatTheImporterSaidComesBackWithItsSeverity() {
        File.WriteAllText(Path.Combine(project, "Assets", "broken.vxmat"), "albedo: vx:notaguid\n");

        using var pool = new CompilerPool(project, workers: 1);

        var result = await pool.ExecuteAsync(Job("NativeFormatImporter", "/Assets/broken.vxmat"), Cancellation);

        Assert.False(result.Succeeded);
        Assert.Equal(ImportSeverity.Error, Assert.Single(result.Diagnostics).Severity);
    }

    /// <summary>
    ///     <b>The whole reason this class exists.</b> An importer that throws is already handled in
    ///     process; an importer that takes the process down is not, and cannot be. Killing the worker
    ///     is exactly what a native access violation inside Assimp looks like from the coordinator,
    ///     and the contract is that it fails <em>that asset</em> and the import carries on.
    /// </summary>
    [Fact]
    public async Task AWorkerThatDiesFailsItsAssetAndNotTheRun() {
        using var pool = new CompilerPool(project, workers: 1);

        // Warm one worker up, so there is a live process to kill.
        Assert.True((await pool.ExecuteAsync(Job("RawImporter", "/Assets/notes.txt"), Cancellation)).Succeeded);

        Assert.Single(pool.Workers).Process.Kill(entireProcessTree: true);

        var killed = await pool.ExecuteAsync(Job("RawImporter", "/Assets/notes.txt"), Cancellation);

        Assert.False(killed.Succeeded);
        Assert.Contains("stopped", Assert.Single(killed.Diagnostics).Message, StringComparison.Ordinal);
        Assert.Equal(1, pool.Restarts);

        // And the next asset gets a fresh worker rather than the corpse of the last one.
        var after = await pool.ExecuteAsync(Job("RawImporter", "/Assets/notes.txt"), Cancellation);

        Assert.True(after.Succeeded);
        Assert.Equal(1, pool.Restarts);
    }

    [Fact]
    public async Task AnImporterThisBuildDoesNotHaveIsRefusedByName() {
        using var pool = new CompilerPool(project, workers: 1);

        var result = await pool.ExecuteAsync(Job("KaleidoscopeImporter", "/Assets/notes.txt"), Cancellation);

        Assert.False(result.Succeeded);
        Assert.Contains("KaleidoscopeImporter", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Two workers, both busy, and neither handed the other's request. A pipe per worker is what
    ///     makes that true without a correlation id in every message.
    /// </summary>
    [Fact]
    public async Task TwoWorkersServeTwoImportsWithoutCrossingTheirAnswers() {
        File.WriteAllText(Path.Combine(project, "Assets", "other.txt"), "a completely different string");

        using var pool = new CompilerPool(project, workers: 2);

        var first = pool.ExecuteAsync(Job("RawImporter", "/Assets/notes.txt"), Cancellation).AsTask();
        var second = pool.ExecuteAsync(Job("RawImporter", "/Assets/other.txt"), Cancellation).AsTask();

        var results = await Task.WhenAll(first, second);

        Assert.Equal("the quick brown fox", Encoding.UTF8.GetString(results[0].Artifacts[0].Content.Span));
        Assert.Equal("a completely different string", Encoding.UTF8.GetString(results[1].Artifacts[0].Content.Span));
    }

    /// <summary>
    ///     A pipeline with the pool in force behaves like one without it. That is the point of the
    ///     seam being where it is: the cache, the key and the sidecar do not know which side of a
    ///     process boundary the importer ran on.
    /// </summary>
    [Fact]
    public async Task ThePoolIsAnExecutorTheProcessBoundaryIsInvisibleThrough() {
        using var pool = new CompilerPool(project, workers: 1);

        IImportExecutor executor = pool;

        var out_of_process = await executor.ExecuteAsync(Job("RawImporter", "/Assets/notes.txt"), Cancellation);

        var in_process = await new InProcessImportExecutor(
            BuiltInImporters.Create(),
            new PhysicalFileProvider(project, isReadOnly: true)
        ).ExecuteAsync(Job("RawImporter", "/Assets/notes.txt"), Cancellation);

        Assert.Equal(in_process.Succeeded, out_of_process.Succeeded);
        Assert.Equal(in_process.Artifacts[0].Content.ToArray(), out_of_process.Artifacts[0].Content.ToArray());
        Assert.Equal(in_process.Artifacts[0].Type, out_of_process.Artifacts[0].Type);
        Assert.Equal(in_process.FileDependencies, out_of_process.FileDependencies);
    }

    [Fact]
    public void ItRunsOneWorkerPerCoreLessOneWhenNobodySaid() {
        using var pool = new CompilerPool(project);

        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 1), pool.WorkerCount);
    }

    static ImportJob Job(string importer, string source) =>
        new(AssetId.New(), importer, new VirtualPath(source), string.Empty, "Windows", EnforceDeclaredReads: true);

    static CancellationToken Cancellation => TestContext.Current.CancellationToken;
}
