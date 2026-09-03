// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Vixen.Net.Tests;

/// <summary>Whether the replication and RPC generators reach somebody who is not us.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>They did not, and nothing in this repository could see that.</b> Every consumer of
///         <c>Vixen.Net.Generators</c> in the tree — <c>Samples/08-Multiplayer</c>, the test
///         projects — names it as a <c>ProjectReference</c> with <c>OutputItemType="Analyzer"</c>,
///         and analyzers are not transitive through one. So each project that wants the generator
///         says so, the generator runs, and every test of it is green. None of that is a claim about
///         the <i>package</i>: a game restoring <c>Vixen.Net</c> from NuGet got the runtime assembly
///         and no generator, so its <c>[Replicated]</c> components and its <c>[ServerRpc]</c>
///         methods were collected by nothing.
///     </para>
///     <para>
///         The symptom is not an error about the attributes. <c>ReplicatedComponents.RegisterAll</c>
///         and <c>RpcMethods.RegisterAll</c> — the two entry points every sample and every guide page
///         calls — simply do not exist, which reads as the engine's own API being missing rather than
///         as a generator that never ran.
///     </para>
///     <para>
///         Written in the shape <c>Vixen.Shaders.Tests.PackagedGeneratorTests</c> established, and
///         for its reason: the only evidence that survives a <c>ProjectReference</c> is the bytes of
///         a real <c>.nupkg</c>.
///     </para>
/// </remarks>
public sealed class PackagedGeneratorTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-net-pack", Guid.NewGuid().ToString("N"));

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    /// <summary>The generator is in the package, under the one path NuGet loads analyzers from.</summary>
    [Fact]
    public void TheReplicationGeneratorTravelsInsideThePackage() {
        var entries = Pack();

        Assert.Contains("analyzers/dotnet/cs/Vixen.Net.Generators.dll", entries);
    }

    /// <summary>
    ///     And the runtime assembly is still where a reference expects it, so the target that puts
    ///     the generator in has not displaced the library it belongs to.
    /// </summary>
    /// <remarks>
    ///     <c>TargetsForTfmSpecificContentInPackage</c> contributes to the same item the framework
    ///     assembly is placed by, and a wrong <c>PackagePath</c> there produces a package that still
    ///     packs, still restores, and resolves to nothing.
    /// </remarks>
    [Fact]
    public void TheLibraryItselfIsStillPackedBesideIt() {
        var entries = Pack();

        Assert.Contains("lib/net10.0/Vixen.Net.dll", entries);
        Assert.DoesNotContain("lib/net10.0/Vixen.Net.Generators.dll", entries);
    }

    /// <summary>Packs <c>Vixen.Net</c> and returns every entry path the package holds.</summary>
    /// <returns>The entry paths.</returns>
    HashSet<string> Pack() {
        var project = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Vixen.Net", "Vixen.Net.csproj")
        );

        // Asserted rather than assumed: a path that has gone stale would otherwise make `dotnet
        // pack` fail for a reason that reads nothing like "this test cannot find its project".
        Assert.True(File.Exists(project), $"Vixen.Net is not at {project}.");

        var output = Path.Combine(root, "package");

        var pack = Run(
            "pack",
            project,
            "-c",
            "Debug",
            "--nologo",
            "-o",
            output
        );

        // ⚠ Only the output directory is redirected, not obj/ and bin/, for the reason
        // Vixen.Shaders.Tests gives: BaseIntermediateOutputPath is a *global* property and MSBuild
        // hands one to every project it invokes, so the netstandard2.1 generator project would look
        // for its assets in an obj/ restored for net10.0.

        Assert.True(pack.Succeeded, pack.Output);

        var package = Assert.Single(Directory.GetFiles(output, "Vixen.Net.[0-9]*.nupkg"));

        using var archive = ZipFile.OpenRead(package);

        return archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
    }

    static (bool Succeeded, string Output) Run(params string[] arguments) {
        var process = new Process {
            StartInfo = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }
        };

        foreach (var argument in arguments) {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        process.OutputDataReceived += (_, line) => output.AppendLine(line.Data);
        process.ErrorDataReceived += (_, line) => output.AppendLine(line.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        return (process.ExitCode == 0, output.ToString());
    }
}
