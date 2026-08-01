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
    ///     A build with a game assembly imports twice, and the second pass is the one that matters.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This asserted <c>1</c> until a project tried to put its own component in a level.</b>
    ///         The import step runs <c>BeforeTargets=CoreCompile</c>, so that generated C# exists before
    ///         the compiler reads its inputs — which means it runs before the compiler has produced the
    ///         assembly declaring that component. On a clean build there is nothing to load, and the
    ///         level fails to compile with "nothing in this build claims the name", about a type in the
    ///         very project being built.
    ///     </para>
    ///     <para>
    ///         So the content build, which runs <c>AfterTargets=Build</c> where the assembly always
    ///         exists, does its own import and is the authority on whether the content is good. The
    ///         cost is one extra incremental scan per build; the alternative is a limit no project can
    ///         work around.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AContentBuildImportsAgainWithTheGameAssemblyInHand() {
        Project();

        var build = Run();

        Assert.True(build.Succeeded, build.Output);

        var lines = build.Output.Split('\n');

        var imports = lines
            .Select((line, index) => (line, index))
            .Where(entry => entry.line.Contains("Imported ", StringComparison.Ordinal))
            .Select(entry => entry.index)
            .ToList();

        Assert.Equal(2, imports.Count);

        // ⚠ And the second one is after the compiler, which is the whole reason it runs: that is the
        // first moment the assembly declaring the project's own components exists to be loaded. An
        // assertion on the count alone would still pass if both passes ran before CoreCompile.
        var compiled = Array.FindIndex(lines, line => line.Contains("Game -> ", StringComparison.Ordinal));

        Assert.True(compiled >= 0, build.Output);
        Assert.True(imports[0] < compiled, build.Output);
        Assert.True(imports[1] > compiled, build.Output);
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
    /// <summary>
    ///     The variant reaches the binary that has to know it. Doc 17's five variants are orthogonal
    ///     to Debug/Release — a Server build differs from a Release one only in having no window — so
    ///     nothing at run time can recover it from the compiler configuration.
    /// </summary>
    /// <remarks>
    ///     It was travelling and dying: the CLI passed <c>VixenVariant</c> into the publish as a
    ///     property and neither the build nor the runtime read it, so a server publish started up,
    ///     detected Release, and asked for a window. The project here declares the two types itself
    ///     rather than referencing <c>Vixen.App</c>, because the SDK deliberately adds no engine
    ///     references and there is no feed to resolve one from — what is under test is whether the
    ///     SDK emits the attribute, not where the type lives.
    /// </remarks>
    [Fact]
    public void TheBuildVariantReachesTheAssemblyThatWasBuiltWithIt() {
        Project(properties: "<VixenVariant>Server</VixenVariant>", entryPoint: VariantProbe);

        var build = Run("run");

        Assert.True(build.Succeeded, build.Output);
        Assert.Contains("variant=Server", build.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     And a project that names no variant gets no attribute, so a plain `dotnet build` does not
    ///     silently declare one. <c>BuildVariants.Detect</c> falls back to the compilation's own
    ///     <c>DEBUG</c> flag there, which is a worse answer and an honest one.
    /// </summary>
    [Fact]
    public void AProjectThatNamesNoVariantDeclaresNone() {
        Project(entryPoint: VariantProbe);

        var build = Run("run");

        Assert.True(build.Succeeded, build.Output);
        Assert.Contains("variant=none", build.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Prints whatever variant the assembly declares, the way <c>BuildVariants.Detect</c> reads
    ///     it. The attribute is declared here because the SDK adds no engine references by design.
    /// </summary>
    const string VariantProbe = """
        using System;
        using System.Linq;
        using System.Reflection;

        var declared = Assembly.GetEntryAssembly()!
            .GetCustomAttributes<Vixen.App.BuildVariantAttribute>()
            .SingleOrDefault();

        Console.WriteLine($"variant={(declared is null ? "none" : declared.Variant.ToString())}");

        namespace Vixen.App {
            public enum BuildVariant { Editor, Debug, Development, Release, Server }

            [AttributeUsage(AttributeTargets.Assembly)]
            public sealed class BuildVariantAttribute(BuildVariant variant) : Attribute {
                public BuildVariant Variant { get; } = variant;
            }
        }
        """;

    void Project(
        string? group = "UiCore",
        string? properties = null,
        bool assets = true,
        string? entryPoint = null
    ) {
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

        File.WriteAllText(Path.Combine(root, "Program.cs"), entryPoint ?? "System.Console.WriteLine(\"game\");\n");

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
