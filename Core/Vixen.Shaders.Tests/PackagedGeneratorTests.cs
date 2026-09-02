// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Vixen.Shaders.Tests;

/// <summary>
///     Whether the binding generator reaches somebody who is not us.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>It did not, and nothing could see that.</b> Every consumer of
///         <c>Vixen.Shaders.Generators</c> in this repository names it as a <c>ProjectReference</c>
///         with <c>OutputItemType="Analyzer"</c>, and analyzers are not transitive through one — so
///         each project that wants the generator says so, the generator runs, and every test of it
///         is green. None of that is a claim about the <i>package</i>. A consumer restoring
///         <c>Vixen.Shaders</c> or <c>Vixen.Rendering</c> got the runtime assembly and no generator,
///         so a compositor node naming <c>BloomKeys.SourceBinding</c> outside this repository did not
///         compile at all.
///     </para>
///     <para>
///         ADR-002 and doc 08 § <c>Vixen.Sdk</c> step 3 both put the parameter keys in a source
///         generator rather than in a tool writing <c>.cs</c> to disk, and that decision is right —
///         <c>ShaderBindingsGenerator</c>'s own header argues it. What was missing was the last
///         mile, which no amount of testing the generator can reach. <c>Vixen.Ui</c>,
///         <c>Vixen.Input</c>, <c>Vixen.Engine</c>, <c>Vixen.Core.Reflection</c> and
///         <c>Vixen.Core.Serialization</c> all pack theirs; this one was the exception.
///     </para>
///     <para>
///         So the assertion is over the bytes of a real <c>.nupkg</c>, in the style of
///         <c>Vixen.Sdk.Tests.PackagedToolTests</c>: the only evidence that survives is what the
///         archive holds.
///     </para>
/// </remarks>
public sealed class PackagedGeneratorTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-shaders-pack", Guid.NewGuid().ToString("N"));

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    /// <summary>
    ///     The generator is in the package, under the one path NuGet loads analyzers from.
    /// </summary>
    [Fact]
    public void TheBindingGeneratorTravelsInsideThePackage() {
        var entries = Pack();

        Assert.Contains("analyzers/dotnet/cs/Vixen.Shaders.Generators.dll", entries);
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

        Assert.Contains("lib/net10.0/Vixen.Shaders.dll", entries);
        Assert.DoesNotContain("lib/net10.0/Vixen.Shaders.Generators.dll", entries);
    }

    /// <summary>Packs <c>Vixen.Shaders</c> and returns every entry path the package holds.</summary>
    /// <returns>The entry paths.</returns>
    HashSet<string> Pack() {
        var project = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Vixen.Shaders", "Vixen.Shaders.csproj")
        );

        // Asserted rather than assumed: a path that has gone stale would otherwise make `dotnet
        // pack` fail for a reason that reads nothing like "this test cannot find its project".
        Assert.True(File.Exists(project), $"Vixen.Shaders is not at {project}.");

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

        // ⚠ Only the output directory is redirected, not obj/ and bin/. Vixen.Sdk.Tests packs with
        // BaseIntermediateOutputPath pointed at its own temporary directory; doing that here is
        // NETSDK1005, because those are *global* properties and MSBuild hands a global property to
        // every project it invokes — so the netstandard2.1 generator projects this one references
        // would all look for their assets in one obj/ restored for net10.0.

        Assert.True(pack.Succeeded, pack.Output);

        var package = Assert.Single(Directory.GetFiles(output, "Vixen.Shaders.*.nupkg"));

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
