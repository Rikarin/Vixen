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
    ///     <b>Doc 36 § P5: a project's <c>Editor/</c> folders are the editor's, not the game's.</b>
    ///     They are compiled by the running editor into an assembly that is never shipped — and if
    ///     the game's own build compiled them too, the first tool anybody wrote would break their
    ///     build with a wall of CS0246 about a reference a game does not have.
    /// </summary>
    /// <remarks>
    ///     ⚠ The script here does not compile against a game's references on purpose. Passing means
    ///     the file was excluded; a test whose script happened to be valid C# would pass whether or
    ///     not the exclusion worked.
    /// </remarks>
    [Fact]
    public void EditorScriptsAreNotCompiledIntoTheGame() {
        Project();

        var editor = Path.Combine(root, "Assets", "Editor");

        Directory.CreateDirectory(editor);

        File.WriteAllText(
            Path.Combine(editor, "Tools.cs"),
            """
            using Vixen.Editor.Plugin;

            public static class T {
                [EditorMenu("Tools/X")]
                public static void X() { }
            }
            """
        );

        var build = Run();

        // ⚠ The build succeeding *is* the assertion. The script references a package a game does
        // not have, so a compilation that saw it could not have succeeded — and the importer does
        // mention the file by name, because it is still an asset under `Assets/` and still wants a
        // `.meta`. What must not appear is a compiler error.
        Assert.True(build.Succeeded, build.Output);
        Assert.DoesNotContain("error CS", build.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A project that keeps runtime code in a folder it happens to have called
    ///     <c>Editor</c> has to be able to say so.</b> That is a name collision rather than an
    ///     opinion, and a convention with no way out is a trap.
    /// </summary>
    [Fact]
    public void AProjectCanKeepItsEditorFolderInTheBuild() {
        Project(properties: "<VixenExcludeEditorScripts>false</VixenExcludeEditorScripts>");

        var editor = Path.Combine(root, "Assets", "Editor");

        Directory.CreateDirectory(editor);
        File.WriteAllText(Path.Combine(editor, "Tools.cs"), "public static class T { public static int X => 1; }");

        var build = Run();

        Assert.True(build.Succeeded, build.Output);
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

    /// <summary>
    ///     <b>The only thing the <c>BeforeTargets="CoreCompile"</c> ordering exists for, proved by
    ///     compiling against it.</b> The opt-in makes the import write <c>Addresses.g.cs</c> into
    ///     <c>obj/</c> and add it to <c>Compile</c> from inside the target, and this asserts the
    ///     whole chain the only way it can be asserted: a program that names the constant, built.
    /// </summary>
    /// <remarks>
    ///     ⚠ Every step of that chain was untested. The CLI half has its own tests
    ///     (<c>VixenCommandTests.ImportingWritesTheAddressConstantsWhenAskedTo</c>) and the emitter
    ///     has twenty more, but nothing asked whether the SDK passes <c>--addresses</c>, whether
    ///     the file lands where the <c>Compile</c> item looks for it, or whether what the emitter
    ///     writes is C# the compiler accepts. The first two are properties of an MSBuild
    ///     evaluation and the third is a property of Roslyn, so only a real build sees any of them
    ///     — and a generated file that does not compile fails a game's build, not ours.
    /// </remarks>
    [Fact]
    public void AnAddressConstantTheImportWroteCompilesIntoTheAssembly() {
        Project(
            properties: "<VixenAddressConstants>true</VixenAddressConstants>"
            + "<VixenAddressNamespace>Probe</VixenAddressNamespace>",
            entryPoint: AddressProbe
        );

        var build = Run("run");

        Assert.True(build.Succeeded, build.Output);

        var generated = Path.Combine(root, "obj", "Debug", "net10.0", "Vixen", "Addresses.g.cs");
        Assert.True(File.Exists(generated), build.Output);
        Assert.Contains("namespace Probe;", File.ReadAllText(generated), StringComparison.Ordinal);

        // Run rather than built, so the address is read back out of the assembly the compiler
        // produced: a file written and never reached by Compile cannot get this far, and a
        // constant carrying the wrong text would print the wrong thing rather than nothing.
        Assert.Contains("address=ui/hero", build.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The control that stops the test above passing for the wrong reason.
    /// </summary>
    /// <remarks>
    ///     Same program, same assets, opt-in off — and it must fail to compile. Without this the
    ///     test above would still be green if <c>Probe.Addresses</c> came from anywhere else, or if
    ///     the SDK had started writing the constants unconditionally, which is the one outcome
    ///     neither of us wants: a project that declined a build step and got it anyway.
    /// </remarks>
    [Fact]
    public void WithoutTheOptInTheSameProgramDoesNotCompile() {
        Project(entryPoint: AddressProbe);

        var build = Run();

        Assert.False(build.Succeeded, build.Output);
        Assert.Contains("CS0103", build.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "obj", "Debug", "net10.0", "Vixen", "Addresses.g.cs")));
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
    ///     <b>And the variant reaches the <i>content</i> build, which is the half that was missing.</b>
    ///     A project built as a Server packs the server profile: the groups its <c>.vxgroup</c> files
    ///     say a dedicated server does not need are not in the catalog.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the test the <c>vixen-mmo</c> Dockerfile's comment needed and did not
    ///         have.</b> That comment said "the content build writes the server profile" while
    ///         <c>VixenVariant</c> reached the assembly attribute, reached <c>dotnet publish</c>, and
    ///         was dropped before <c>vixen content build</c> — so a shard image shipped full client
    ///         content and nothing said so. The claim and the behaviour are now the same thing, and
    ///         this asserts it through the same <c>-p:VixenVariant=Server</c> the Dockerfile passes.
    ///     </para>
    ///     <para>
    ///         Asserted against the catalog on disk as well as the log, because a log line is exactly
    ///         what the old comment was: a statement about what should have happened. This project
    ///         references no engine assembly by design — see the csproj — so the catalog is searched
    ///         for the address as bytes rather than parsed. An address is written to it as UTF-8, so
    ///         its absence from the file is its absence from the build.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheBuildVariantAlsoReachesTheContentBuild() {
        // The probe entry point, because naming a variant makes the SDK emit the attribute and the
        // type it names has to exist somewhere — the same arrangement, and the same reason, as the
        // test above.
        Project(properties: "<VixenVariant>Server</VixenVariant>", entryPoint: VariantProbe, onServer: false);

        var build = Run();

        Assert.True(build.Succeeded, build.Output);
        Assert.Contains("server profile", build.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ui/hero", Catalog(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     And a client build of the same project ships it, so the group flag is a profile decision
    ///     rather than a way of turning an asset off.
    /// </summary>
    [Fact]
    public void AClientBuildOfTheSameProjectStillShipsThatGroup() {
        Project(onServer: false);

        var build = Run();

        Assert.True(build.Succeeded, build.Output);
        Assert.DoesNotContain("server profile", build.Output, StringComparison.Ordinal);
        Assert.Contains("ui/hero", Catalog(), StringComparison.Ordinal);
    }

    /// <summary>The catalog this build wrote, as text an address can be looked for in.</summary>
    string Catalog() =>
        Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(root, "Build", HostTarget, "catalog.bin")));

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

    /// <summary>
    ///     Prints the address the content build wrote a constant for. It names nothing the project
    ///     declares itself — unlike <see cref="VariantProbe" />, which has to declare the attribute
    ///     it looks for — so the only way this compiles is the generated file reaching
    ///     <c>Compile</c>, which is the whole claim.
    /// </summary>
    const string AddressProbe = """
        System.Console.WriteLine($"address={Probe.Addresses.Ui.Hero.Address}");
        """;

    void Project(
        string? group = "UiCore",
        string? properties = null,
        bool assets = true,
        string? entryPoint = null,
        bool onServer = true
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

            File.WriteAllText(
                Path.Combine(assetDirectory, "UiCore.vxgroup"),
                "name: UiCore\n" + (onServer ? string.Empty : "includeInServerBuild: false\n")
            );
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
        //
        // ⚠ Both handlers run on thread-pool threads, and the two streams are read by two different
        // ones, so the builder they share has to be locked. Unsynchronised, this threw
        // `ArgumentException: Destination is too short` out of `StringBuilder.AppendLine` — a torn
        // internal length rather than anything about the process — and xunit reported it as a
        // CATASTROPHIC FAILURE that took the whole assembly with it. It survived because the
        // failure is a race: it needs both streams to be producing at once, which is why it waited
        // for a run under load to appear.
        var output = new StringBuilder();
        var guard = new Lock();

        void Append(DataReceivedEventArgs line) {
            lock (guard) {
                output.AppendLine(line.Data);
            }
        }

        process.OutputDataReceived += (_, line) => Append(line);
        process.ErrorDataReceived += (_, line) => Append(line);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Parameterless, so it also waits for both readers to reach end of stream — an overload
        // taking a timeout returns without that, and the last lines of the build would be missing
        // from the very output this asserts on.
        process.WaitForExit();

        lock (guard) {
            return (process.ExitCode == 0, output.ToString());
        }
    }

    static string Metadata(string key) =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == key)
            .Value!;
}
