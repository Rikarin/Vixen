// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Editor.Assets;
using Xunit;

namespace Vixen.AssetCompiler.Tests;

/// <summary>A graph importer a host links produces the same bytes here and in a worker, and the bytes it always did (#1300).</summary>
/// <remarks>
///     <para>
///         <b>The worker half of option A.</b> A worker is a process of its own, so nothing another test
///         linked in this process reaches it: it has the VFX importer only if <c>WorkerHost</c> links it,
///         and it starts only if its coordinator did not also hand it the same assembly as a
///         <c>--plugin</c> — two importers claiming <c>.vxvfx</c> is a registry <c>ImporterRegistry.Add</c>
///         refuses, and a worker that cannot build its registry never connects.
///     </para>
///     <para>
///         ⚠ <b>The digest was captured before the importer moved</b>, on master at 5f03c9b80, from the
///         sample's own effect. Moving an importer between assemblies must not move a byte of what it
///         writes. It is tied to <c>VfxEffectContent.Current</c> like the importer's version is: a
///         deliberate change to the content format changes it, and says so in the same commit.
///     </para>
/// </remarks>
public sealed class LinkedImporterTests : IDisposable {
    /// <summary>SHA-256 of the one chunk <c>Samples/13-ThirdPersonShooter/Assets/Effects/Embers.vxvfx</c> imports to.</summary>
    const string EmbersDigest = "65F58FC6205B2C00788F75C500AA22F7564F593E6C9979E1CC5346FE893B16DF";

    readonly string project = Path.Combine(Path.GetTempPath(), $"vixen-linked-importer-{Environment.ProcessId}-{Guid.NewGuid():N}");

    public LinkedImporterTests() {
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        File.Copy(Path.Combine(Root(), "Samples", "13-ThirdPersonShooter", "Assets", "Effects", "Embers.vxvfx"), Path.Combine(project, "Assets", "Embers.vxvfx"));
    }

    public void Dispose() => PluginImporterTests.Discard(() => Directory.Delete(project, recursive: true));

    /// <summary>One effect, two processes, the bytes captured before the move.</summary>
    [Fact]
    public async Task A_linked_vfx_importer_writes_the_same_bytes_here_and_in_a_worker() {
        var linked = new ImporterContributions();

        WorkerHost.LinkImporters(linked);

        var here = await new InProcessImportExecutor(
            BuiltInImporters.Create(linked, new ImporterContributions()),
            new PhysicalFileProvider(project, isReadOnly: true)
        ).ExecuteAsync(Job, Cancellation);

        using var pool = new CompilerPool(project, workers: 1, contributed: new ImporterContributions());

        var there = await pool.ExecuteAsync(Job, Cancellation);

        Assert.True(here.Succeeded, Why(here));
        Assert.True(there.Succeeded, Why(there));

        var chunk = Assert.Single(here.Artifacts);

        Assert.Equal("VfxEffectContent", chunk.Type);
        Assert.Equal(chunk.Type, Assert.Single(there.Artifacts).Type);
        Same(Digest(chunk.Content.Span), "in this process");
        Same(Digest(Assert.Single(there.Artifacts).Content.Span), "in a worker");
    }

    static ImportJob Job =>
        new(AssetId.New(), "VfxImporter", new VirtualPath("/Assets/Embers.vxvfx"), string.Empty, "Windows", EnforceDeclaredReads: true);

    static void Same(string digest, string where) =>
        Assert.True(
            digest == EmbersDigest,
            $"Embers.vxvfx imported {where} to {digest}, and to {EmbersDigest} before the importer moved. A move "
            + "changed what a project ships."
        );

    static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            if (File.Exists(Path.Combine(directory.FullName, "Vixen.slnx"))) {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root, which holds Vixen.slnx, is above no directory this test runs in.");
    }

    static string Why(ExecutedImport import) =>
        string.Join(
            "\n",
            import.Diagnostics.Select(diagnostic =>
                string.Create(CultureInfo.InvariantCulture, $"{diagnostic.Severity}: {diagnostic.Message}")
            )
        );

    static CancellationToken Cancellation => TestContext.Current.CancellationToken;
}
