// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using System.Text;
using Xunit;

namespace Vixen.Sdk.Tests;

/// <summary>
///     The SDK, driven by a real <c>dotnet build</c> of a real project.
/// </summary>
/// <remarks>
///     <para>
///         <b>There is no way to test MSBuild integration except by running MSBuild.</b> The whole
///         subject is what happens when a target's condition is evaluated, when a property is
///         computed relative to an import, and whether a hook fires before the compiler or after the
///         build — none of which exists until a build engine reads the files. Every test here starts
///         a build and reads what it said.
///     </para>
///     <para>
///         They are the slowest tests in the repository, at about a second each, and that is the
///         price of the only kind of test that can catch what they catch. The first thing a real
///         build found was an ordering bug that reads perfectly on the page: a property derived in a
///         <c>.props</c> from a property the consuming project sets in its body, which is always
///         empty at that point and always silently defaults.
///     </para>
///     <para>
///         <b>Every test here rests on that rule</b>, because every one of them sets
///         <c>VixenToolPath</c> in the project body and would otherwise be running
///         <c>dotnet vixen</c>, which is not installed. Moving the derivation back into the
///         <c>.props</c> fails six of the seven — which is how the rule is verified, and is not
///         what the first attempt at verifying it sabotaged.
///     </para>
/// </remarks>
public sealed class VixenSdkTests : IDisposable {
    static readonly string CliPath = Metadata("VixenCliPath");
    static readonly string SdkDirectory = Metadata("VixenSdkDirectory");

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-sdk-tests", Guid.NewGuid().ToString("N"));

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
    ///     The whole of doc 08's promise in one build: `dotnet build` imports the assets, packs the
    ///     content, and leaves it beside the binary. A user never runs a separate content step.
    /// </summary>
    [Fact]
    public void ABuildImportsPacksAndPutsTheContentBesideTheBinary() {
        Project();

        var build = Run();

        Assert.True(build.Succeeded, build.Output);
        Assert.Contains("Imported", build.Output, StringComparison.Ordinal);
        Assert.Contains("Built 1 address", build.Output, StringComparison.Ordinal);

        var content = Path.Combine(root, "bin", "Debug", "net10.0", "Content");

        Assert.True(File.Exists(Path.Combine(content, "catalog.bin")));
        Assert.True(File.Exists(Path.Combine(content, "catalog.bin.hash")));
        Assert.Single(Directory.GetFiles(content, "*.bundle"));
    }

    /// <summary>
    ///     <b>Point 6 of doc 08's list, and the reason the tool has a diagnostic format at all.</b>
    ///     What an importer said about an asset arrives as an MSBuild error with a code, so it is an
    ///     entry in the IDE's error list and a line in a CI log's summary rather than prose from a
    ///     subprocess that scrolled past.
    /// </summary>
    [Fact]
    public void WhatTheContentBuildRejectsBecomesAnMSBuildError() {
        Project(group: "Nonexistent");

        var build = Run();

        Assert.False(build.Succeeded);
        Assert.Contains("error VX2001", build.Output, StringComparison.Ordinal);
        Assert.Contains("no .vxgroup", build.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The target a project names decides which content gets built and where it lands, which is
    ///     what makes one project able to produce Android content on a laptop. It is also the path
    ///     everything else derives from, so it is worth one test of its own.
    /// </summary>
    [Fact]
    public void ATargetSetInTheProjectDecidesWhereTheContentGoes() {
        Project(properties: "<VixenTarget>Android</VixenTarget>");

        var build = Run();

        Assert.True(build.Succeeded, build.Output);
        Assert.True(File.Exists(Path.Combine(root, "Build", "Android", "catalog.bin")));
        Assert.False(Directory.Exists(Path.Combine(root, "Build", "MacOS")));
        Assert.False(Directory.Exists(Path.Combine(root, "Build", "Windows")));
        Assert.False(Directory.Exists(Path.Combine(root, "Build", "Linux")));
    }

    /// <summary>
    ///     One build imports once. The import is its own step so that generated C# exists before the
    ///     compiler runs, and the content build is told not to do it again — on a ten-thousand-asset
    ///     project the second one would be a full scan and ten thousand decisions for nothing.
    /// </summary>
    [Fact]
    public void AContentBuildInTheSameBuildDoesNotImportASecondTime() {
        Project();

        var build = Run();

        Assert.True(build.Succeeded, build.Output);

        var imports = build.Output.Split('\n').Count(line => line.Contains("Imported ", StringComparison.Ordinal));
        Assert.Equal(1, imports);
    }

    /// <summary>
    ///     And turning the import step off makes the content build do it itself, rather than packing
    ///     a project nothing has imported and reporting an error about every asset in it.
    /// </summary>
    [Fact]
    public void TurningTheImportStepOffLeavesTheContentBuildToDoIt() {
        Project(properties: "<VixenImportOnBuild>false</VixenImportOnBuild>");

        var build = Run();

        Assert.True(build.Succeeded, build.Output);
        Assert.Contains("Imported", build.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "Build", HostTarget, "catalog.bin")));
    }

    /// <summary>A project with no assets is a normal .NET project, and nothing runs.</summary>
    [Fact]
    public void AProjectWithNoAssetsDirectoryJustBuilds() {
        Project(assets: false);

        var build = Run();

        Assert.True(build.Succeeded, build.Output);
        Assert.DoesNotContain("Imported", build.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "Build")));
    }

    /// <summary>
    ///     Clean takes back what the build put in the output directory, and leaves the project's own
    ///     Build/ alone — it is where a person keeps the script that publishes it, and deleting
    ///     somebody's directory because they typed `dotnet clean` is not a trade this makes.
    /// </summary>
    [Fact]
    public void CleanRemovesTheCopiedContentAndNotTheBuildDirectory() {
        Project();
        Assert.True(Run().Succeeded);

        var content = Path.Combine(root, "bin", "Debug", "net10.0", "Content");
        Assert.True(File.Exists(Path.Combine(content, "catalog.bin")));

        Assert.True(Run("clean").Succeeded);

        Assert.False(File.Exists(Path.Combine(content, "catalog.bin")));
        Assert.True(File.Exists(Path.Combine(root, "Build", HostTarget, "catalog.bin")));
    }

    static string HostTarget =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "MacOS" : "Linux";

    /// <summary>Writes a game project that imports the SDK the way a consumer of the package would.</summary>
    void Project(string? group = "UiCore", string? properties = null, bool assets = true) {
        Directory.CreateDirectory(root);

        if (assets) {
            var assetDirectory = Path.Combine(root, "Assets");
            Directory.CreateDirectory(assetDirectory);
            File.WriteAllText(Path.Combine(assetDirectory, "hero.txt"), "the hero");

            File.WriteAllText(
                Path.Combine(assetDirectory, "hero.txt.meta"),
                $"guid: 4d6b1f2a3c5e47889a0b1c2d3e4f5061\nmetaVersion: 1\naddressable:\n  address: ui/hero\n  group: {group}\n"
            );

            File.WriteAllText(Path.Combine(assetDirectory, "UiCore.vxgroup"), "name: UiCore\n");
        }

        File.WriteAllText(Path.Combine(root, "Program.cs"), "System.Console.WriteLine(\"game\");\n");

        // Imported by path rather than through `<Project Sdk="Vixen.Sdk">`, because the Sdk form
        // resolves through a NuGet feed and there is no published package to resolve. The two forms
        // land on these same two files, which is why one of them can stand for both.
        File.WriteAllText(
            Path.Combine(root, "Game.csproj"),
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <Import Project="{SdkDirectory}/build/Vixen.Sdk.props" />
               <PropertyGroup>
                 <TargetFramework>net10.0</TargetFramework>
                 <OutputType>Exe</OutputType>
                 <Nullable>enable</Nullable>
                 <VixenToolPath>{CliPath}</VixenToolPath>
                 {properties}
               </PropertyGroup>
               <Import Project="{SdkDirectory}/build/Vixen.Sdk.targets" />
             </Project>
             """
        );
    }

    (bool Succeeded, string Output) Run(string verb = "build") {
        var process = new Process {
            StartInfo = new("dotnet") {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.StartInfo.ArgumentList.Add(verb);
        process.StartInfo.ArgumentList.Add("--nologo");

        // The build's own output is the thing under test, so it is captured whole rather than
        // sampled: a diagnostic that reached the wrong stream is a diagnostic nobody sees.
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
