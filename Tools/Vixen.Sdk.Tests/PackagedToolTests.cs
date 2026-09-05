// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Xunit;

namespace Vixen.Sdk.Tests;

/// <summary>
///     That the package carries the tool its own targets look for, and that finding one there is what
///     a consuming build then runs.
/// </summary>
/// <remarks>
///     <para>
///         <b>The arrangement no build inside this repository exercises.</b> Every in-repo project sets
///         <c>VixenToolPath</c> and takes the first rung of the tool-path ladder, so the packed copy —
///         rung two — is reached only by somebody who restored the package. That is exactly the shape
///         of the one failure that shipped in every <c>Vixen.Ui.Styling.Utilities</c> package ever
///         produced and could only be found by extracting a <c>.nupkg</c>.
///     </para>
///     <para>
///         ⚠ <b>Packed from a stand-in CLI directory rather than the real one, and that is the point
///         rather than a shortcut.</b> The real output is ~430 MB and compressing it would make this
///         the slowest test in the repository by two orders of magnitude, while asserting nothing the
///         stand-in does not: what is under test is which <em>kinds</em> of file the packing rules
///         select, and a directory holding one of each kind states that where a real one buries it.
///         Whether the real tool starts from its packed layout is
///         <c>Build.CheckCliIsShippable</c>'s question, and it is asked of the package the solution
///         pack actually produces.
///     </para>
/// </remarks>
public sealed class PackagedToolTests : IDisposable {
    static readonly string SdkDirectory = Metadata("VixenSdkDirectory");

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-sdk-pack", Guid.NewGuid().ToString("N"));

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
    ///     The tool lands at the path <c>build/Vixen.Sdk.targets</c> has been looking for since it was
    ///     written, together with everything the host needs to start it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The two JSON files are named individually because the host will not start at all
    ///     without either.</b> A <c>tools/</c> holding only <c>vixen.dll</c> throws
    ///     <c>FileNotFoundException</c> out of an <c>Exec</c> on the consumer's first build — the
    ///     <c>Vixen.StyleGen</c> failure, one package over. <c>Vixen.AssetCompiler.dll</c> is here for
    ///     a reason of its own: <c>CompilerPool</c> starts the out-of-process import workers as
    ///     <c>dotnet "…/Vixen.AssetCompiler.dll"</c> from the tool's own directory, so a package
    ///     without it has a CLI that works until somebody imports a project in parallel.
    /// </remarks>
    [Fact]
    public void ThePackagePutsTheToolWhereItsOwnTargetsLookForIt() {
        var entries = Pack();

        Assert.Contains("tools/vixen.dll", entries);
        Assert.Contains("tools/vixen.deps.json", entries);
        Assert.Contains("tools/vixen.runtimeconfig.json", entries);
        Assert.Contains("tools/Vixen.AssetCompiler.dll", entries);
        Assert.Contains("tools/Vixen.AssetCompiler.deps.json", entries);
    }

    /// <summary>
    ///     And every RID's natives with it, in the tree the host resolves them through.
    /// </summary>
    /// <remarks>
    ///     <b>One portable copy rather than three RID-specific ones.</b> A framework-dependent build
    ///     picks its natives at run time from its own <c>.deps.json</c>, so the same package serves
    ///     win-x64, linux-x64, osx-arm64 — and linux-musl-x64, which is what a CI container usually
    ///     is and what a hand-maintained list of "the three desktop RIDs" would have left with no tool
    ///     at all. Flattening the tree would be the same thing as deleting it: the resolver looks for
    ///     <c>runtimes/&lt;rid&gt;/native</c> and nowhere else.
    /// </remarks>
    [Fact]
    public void TheNativesKeepTheirRuntimeIdentifierTree() {
        var entries = Pack();

        Assert.Contains("tools/runtimes/osx-arm64/native/libmade-up.dylib", entries);
        Assert.Contains("tools/runtimes/win-x64/lib/net10.0/MadeUp.Windows.dll", entries);
    }

    /// <summary>
    ///     ⚠ <b>And the apphosts beside the assemblies are not packed, because they are not portable.</b>
    /// </summary>
    /// <remarks>
    ///     <c>vixen</c>, <c>vixen-content-server</c> and <c>Vixen.AssetCompiler</c> sit in the build
    ///     output next to their <c>.dll</c>s with no extension at all: they are native launchers built
    ///     for whichever machine ran the build. Packing the directory wholesale would put a Mach-O
    ///     executable called <c>vixen</c> into the package a Windows consumer restores — inert, but
    ///     30 MB of it, and the obvious thing for somebody debugging the package to try to run.
    ///     Nothing needs them: every entry point here is started as <c>dotnet "….dll"</c>.
    /// </remarks>
    [Fact]
    public void TheApphostsAreNotPacked() {
        var entries = Pack();

        Assert.DoesNotContain("tools/vixen", entries);
        Assert.DoesNotContain("tools/Vixen.AssetCompiler", entries);
    }

    /// <summary>
    ///     ⚠ <b>Nor Assimp's second soname, and which of the two is second is the whole finding.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Ultz.Native.Assimp</c> 6.0.2 ships <c>libassimp.so.5</c> <i>and</i>
    ///         <c>libassimp.so.6</c> in every Linux RID, and both <c>.dylib</c> majors in every macOS
    ///         one; Windows ships a single unversioned <c>Assimp64.dll</c>. Only one of each pair is
    ///         ever opened, and it is the **5** — <c>Silk.NET.Assimp</c> 2.23.0 binds Assimp 5's C
    ///         ABI, which is why <c>ci.yml</c> installs Ubuntu's <c>libassimp5</c> and says in as many
    ///         words that a 6 there "would load and then be wrong in ways a signature cannot catch".
    ///         44 251 540 bytes of major 6 across the five RIDs that carry a pair.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves are asserted, and the first is the one that matters.</b> An exclusion
    ///         written as <c>libassimp.so.*</c> — or written against the wrong major, which is how
    ///         `Rikarin/Vixen#624` filed it — produces a package that restores, installs, runs, and
    ///         throws <c>FileNotFoundException</c> out of <c>Assimp.GetApi()</c> the first time
    ///         anybody imports a model. A test that only checked the 6 was gone would pass on it.
    ///         What ties the number here to the binding rather than to this comment is
    ///         <c>Vixen.Editor.Assets.Tests.AssimpSonameTests</c>, which reads the name the binding
    ///         asks for and holds this file's exclusion to it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void OnlyTheAssimpSonameTheBindingOpensIsPacked() {
        var entries = Pack();

        Assert.Contains("tools/runtimes/osx-arm64/native/libassimp.5.dylib", entries);
        Assert.Contains("tools/runtimes/linux-x64/native/libassimp.so.5", entries);
        Assert.Contains("tools/runtimes/win-x64/native/Assimp64.dll", entries);

        Assert.DoesNotContain("tools/runtimes/osx-arm64/native/libassimp.6.dylib", entries);
        Assert.DoesNotContain("tools/runtimes/linux-x64/native/libassimp.so.6", entries);
    }

    /// <summary>Nor the symbols, which are most of what a native package weighs.</summary>
    [Fact]
    public void TheSymbolsAreNotPacked() {
        var entries = Pack();

        Assert.DoesNotContain("tools/vixen.pdb", entries);
        Assert.DoesNotContain("tools/runtimes/osx-arm64/native/libmade-up.pdb", entries);
        Assert.DoesNotContain("tools/vixen.xml", entries);
    }

    /// <summary>
    ///     And a tool sitting there is the one a consuming build runs, rather than the
    ///     <c>dotnet vixen</c> it would otherwise need installed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Asked of the property rather than of a build, deliberately.</b> Running the resolved
    ///     command would need the resolved command to be a real CLI, which is the thing this test is
    ///     arranging not to have to build; and <c>VixenToolCommand</c> is where the whole ladder ends
    ///     up, so a wrong answer anywhere on it is a wrong answer here. It is also the property whose
    ///     derivation the rest of this suite exists to protect — see <c>VixenSdkTests</c>' remarks on
    ///     why it may not be computed in the <c>.props</c>.
    /// </remarks>
    [Fact]
    public void AToolInThePackageIsWhatTheTargetsRun() {
        var sdk = Layout(tool: true);
        var command = ToolCommand(sdk);

        Assert.StartsWith("dotnet \"", command, StringComparison.Ordinal);
        Assert.EndsWith("\"", command, StringComparison.Ordinal);

        // ⚠ Normalised before comparing, because what the targets build is
        // `…/build/../tools/vixen.dll` — the `.props` knows where it is and steps up from there.
        // Every filesystem call and every shell takes that unchanged, so normalising it in the
        // targets would be tidying rather than fixing; normalising it here is what lets the
        // assertion name the path a reader expects.
        Assert.Equal(Path.Combine(sdk, "tools", "vixen.dll"), Path.GetFullPath(command[8..^1]));
    }

    /// <summary>And a package without one falls through to the installed tool, exactly as before.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that makes the test above mean something.</b> A ladder whose second rung is
    ///     taken unconditionally would pass the assertion above while being broken for every consumer
    ///     of a package packed before the CLI was built — the failure this suite's sibling gate in
    ///     <c>build/Build.cs</c> exists for. The fallback has to still be reachable.
    /// </remarks>
    [Fact]
    public void WithoutOneTheyFallThroughToTheInstalledTool() {
        var sdk = Layout(tool: false);

        Assert.Equal("dotnet vixen", ToolCommand(sdk));
    }

    /// <summary>Packs the SDK over a stand-in CLI directory and returns what the package holds.</summary>
    /// <returns>Every entry path in the produced <c>.nupkg</c>.</returns>
    IReadOnlyCollection<string> Pack() {
        var cli = Path.Combine(root, "cli");
        var output = Path.Combine(root, "package");

        // One file of every kind the real output directory holds, and no more: an assembly, the two
        // JSON files the host reads, the worker the pool starts, a native under its RID, a
        // RID-specific managed asset, the symbols and doc comments that must not travel, and the
        // extension-less apphosts that must not either.
        Write(Path.Combine(cli, "vixen.dll"), "assembly");
        Write(Path.Combine(cli, "vixen.deps.json"), "{}");
        Write(Path.Combine(cli, "vixen.runtimeconfig.json"), "{}");
        Write(Path.Combine(cli, "vixen.pdb"), "symbols");
        Write(Path.Combine(cli, "vixen.xml"), "<doc />");
        Write(Path.Combine(cli, "vixen"), "an apphost for whichever machine built it");
        Write(Path.Combine(cli, "Vixen.AssetCompiler.dll"), "worker");
        Write(Path.Combine(cli, "Vixen.AssetCompiler.deps.json"), "{}");
        Write(Path.Combine(cli, "Vixen.AssetCompiler"), "another apphost");
        Write(Path.Combine(cli, "runtimes", "osx-arm64", "native", "libmade-up.dylib"), "native");
        Write(Path.Combine(cli, "runtimes", "osx-arm64", "native", "libmade-up.pdb"), "native symbols");
        Write(Path.Combine(cli, "runtimes", "win-x64", "lib", "net10.0", "MadeUp.Windows.dll"), "managed");

        // Both sonames `Ultz.Native.Assimp` ships per RID, so the exclusion has something to choose
        // between. Real names rather than made-up ones, because the exclusion names them literally.
        Write(Path.Combine(cli, "runtimes", "osx-arm64", "native", "libassimp.5.dylib"), "the one that loads");
        Write(Path.Combine(cli, "runtimes", "osx-arm64", "native", "libassimp.6.dylib"), "the one that does not");
        Write(Path.Combine(cli, "runtimes", "linux-x64", "native", "libassimp.so.5"), "the one that loads");
        Write(Path.Combine(cli, "runtimes", "linux-x64", "native", "libassimp.so.6"), "the one that does not");
        Write(Path.Combine(cli, "runtimes", "win-x64", "native", "Assimp64.dll"), "no pair on Windows");

        var pack = Run(
            "pack",
            Path.Combine(SdkDirectory, "Vixen.Sdk.csproj"),
            "-c",
            "Debug",
            "--nologo",
            "-o",
            output,

            // A trailing separator, because the property is used as a directory prefix.
            "-p:VixenCliOutputDirectory=" + cli + Path.DirectorySeparatorChar,

            // ⚠ Its own obj/ and bin/, so that a pack driven from a test cannot collide with the
            // solution build that produced the assembly this test is running from.
            "-p:BaseIntermediateOutputPath=" + Path.Combine(root, "obj") + Path.DirectorySeparatorChar,
            "-p:BaseOutputPath=" + Path.Combine(root, "bin") + Path.DirectorySeparatorChar
        );

        Assert.True(pack.Succeeded, pack.Output);

        var package = Assert.Single(Directory.GetFiles(output, "Vixen.Sdk.*.nupkg"));

        using var archive = ZipFile.OpenRead(package);

        return archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Builds the directory layout a restored package has, with or without its tool.</summary>
    /// <param name="tool">Whether to put a <c>tools/vixen.dll</c> in it.</param>
    /// <returns>The layout's root.</returns>
    /// <remarks>
    ///     Copied rather than packed and restored, because what decides the answer is one
    ///     <c>Exists</c> against a path relative to the imported <c>.props</c> — so the property under
    ///     test is a function of the directory layout alone, and building the layout directly is the
    ///     same experiment without a feed in it.
    /// </remarks>
    string Layout(bool tool) {
        var sdk = Path.Combine(root, tool ? "with-tool" : "without-tool");

        Directory.CreateDirectory(Path.Combine(sdk, "build"));

        foreach (var file in Directory.GetFiles(Path.Combine(SdkDirectory, "build"))) {
            File.Copy(file, Path.Combine(sdk, "build", Path.GetFileName(file)));
        }

        if (tool) {
            Write(Path.Combine(sdk, "tools", "vixen.dll"), "assembly");
        }

        return sdk;
    }

    /// <summary>What a project importing that layout would run the tool as.</summary>
    /// <param name="sdk">The layout's root.</param>
    /// <returns>The value of <c>VixenToolCommand</c>.</returns>
    string ToolCommand(string sdk) {
        var project = Path.Combine(root, Path.GetFileName(sdk) + ".csproj");

        File.WriteAllText(
            project,
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <Import Project="{sdk}/build/Vixen.Sdk.props" />
               <PropertyGroup>
                 <TargetFramework>net10.0</TargetFramework>
               </PropertyGroup>
               <Import Project="{sdk}/build/Vixen.Sdk.targets" />
             </Project>
             """
        );

        var read = Run("msbuild", project, "-getProperty:VixenToolCommand", "-nologo");

        Assert.True(read.Succeeded, read.Output);

        return read.Output.Trim();
    }

    static void Write(string path, string content) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    static (bool Succeeded, string Output) Run(params string[] arguments) {
        var process = new Process {
            StartInfo = new("dotnet") {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
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

    static string Metadata(string key) =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == key)
            .Value!;
}
