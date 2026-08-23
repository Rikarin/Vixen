// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using System.Text;
using Xunit;

namespace Vixen.Ui.Markup.Generators.Tests;

/// <summary>
///     The half of "a <c>.vxml</c> compiles by being in the project" that no driver can check.
/// </summary>
/// <remarks>
///     <para>
///         Everything in <see cref="VxmlGeneratorTests" /> hands the generator its files. Nothing
///         there says a build <i>finds</i> them, and finding them is a glob in a
///         <c>.targets</c> plus two <c>CompilerVisibleProperty</c> items — MSBuild evaluation
///         order, which does not exist until a build engine reads the files. Same argument as
///         <c>Vixen.Sdk.Tests</c>, and the same price: these are slow, and they are the only kind of
///         test that can catch what they catch.
///     </para>
///     <para>
///         The namespace is the assertion that covers the most: it can only be right if the glob
///         found the file, if <c>ProjectDir</c> came through so the path was made relative, and if
///         <c>RootNamespace</c> came through as well.
///     </para>
/// </remarks>
public sealed class BuildIntegrationTests : IDisposable {
    static readonly string UiProject = Metadata("VixenUiProject");
    static readonly string GeneratorProject = Metadata("VixenMarkupGeneratorProject");
    static readonly string BuildTargets = Metadata("VixenUiBuildTargets");
    static readonly string RepositoryTargets = Metadata("VixenRepositoryTargets");

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-vxml-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    [Fact]
    public void A_vxml_dropped_into_a_project_compiles_without_the_project_naming_it() {
        Project();
        Markup(
            "Ui/Widgets/Counter.vxml",
            """
            @component Counter
            @using Vixen.Ui.Reactive

            @code {
                public Signal<int> Count { get; } = new(0);
            }

            <div class="root">
                <span>Count: @Count.Value</span>
            </div>
            """
        );

        var (succeeded, output) = Run();
        Assert.True(succeeded, output);

        // Named from the root namespace and the file's own folders, which is only possible if the
        // glob found it and both MSBuild properties reached the generator.
        var generated = Directory.EnumerateFiles(root, "*.g.cs", SearchOption.AllDirectories)
            .Single(path => path.EndsWith("Ui_Widgets_Counter.g.cs", StringComparison.Ordinal));

        Assert.Contains("namespace Demo.Ui.Widgets;", File.ReadAllText(generated), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ And a broken one fails the build with a message an IDE can act on: a path, a line, a
    ///     column, a code. A generator that threw instead would report CS8785 against the project.
    /// </summary>
    [Fact]
    public void A_broken_vxml_fails_the_build_at_the_line_that_broke_it() {
        Project();
        Markup("Ui/Broken.vxml", "@component Broken\n<div>\n    <span>oops\n</div>\n");

        var (succeeded, output) = Run();

        Assert.False(succeeded);
        Assert.Contains("Ui/Broken.vxml(3,6): error VXML1002", output.Replace('\\', '/'), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The half-wired project: the <c>.vxml</c> is compiler input and no analyzer will read
    ///     it. <c>Vixen.Editor.NodeGraph</c> was in this state and the build's account of it was
    ///     "does not contain a definition for" once per member the markup declares, naming a
    ///     hand-written file that is correct.
    /// </summary>
    /// <remarks>
    ///     The second assertion is the one that matters as much as the first: the check runs before
    ///     <c>CoreCompile</c>, so the wall of C# errors that used to be the whole of the message is
    ///     not printed at all. A diagnostic that arrives underneath thirty of them is a diagnostic
    ///     nobody reads.
    /// </remarks>
    [Fact]
    public void A_vxml_with_no_markup_generator_loaded_fails_naming_the_project_file() {
        Unwired($"""<Import Project="{BuildTargets}" />""");
        Markup("Ui/Counter.vxml", "@component Counter\n<div>hello</div>\n");

        var (succeeded, output) = Run();

        Assert.False(succeeded);
        Assert.Contains("error VX4002", output, StringComparison.Ordinal);
        Assert.Contains("Ui/Counter.vxml", output.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains("<VixenUi>true</VixenUi>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("error CS", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The unwired project: a <c>.vxml</c> on disk that is not an item at all, which is what
    ///     <c>Vixen.Editor.Water</c> hit and what the repository's <c>Directory.Build.targets</c> is
    ///     the only possible place to catch — a check inside <c>Vixen.Ui.targets</c> is a check
    ///     inside the file that was never imported.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The project says nothing about Vixen whatsoever</b> — no reference, no import,
    ///         no property — because that is the state being tested. It reaches
    ///         <c>Directory.Build.targets</c> through <c>DirectoryBuildTargetsPath</c>, which is the
    ///         property MSBuild's own auto-import reads, so the file lands at exactly the point in
    ///         the evaluation it lands at for the 391 projects in this tree rather than at a
    ///         hand-picked one that happens to work.
    ///     </para>
    ///     <para>
    ///         And the markup is deliberately valid. The claim is not "a broken .vxml is reported";
    ///         it is that a perfectly good one, in a project that forgot one line, is not silently
    ///         ignored.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_vxml_no_project_globs_fails_before_it_can_be_silently_ignored() {
        Unwired(string.Empty, $"<DirectoryBuildTargetsPath>{RepositoryTargets}</DirectoryBuildTargetsPath>");
        Markup("Ui/Counter.vxml", "@component Counter\n<div>hello</div>\n");

        var (succeeded, output) = Run();

        Assert.False(succeeded);
        Assert.Contains("error VX4001", output, StringComparison.Ordinal);
        Assert.Contains("Ui/Counter.vxml", output.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains("<VixenUi>true</VixenUi>", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     And the escape hatch both messages name works, because a check with no way out is a
    ///     check somebody deletes. <c>Tools/Vixen.Templates</c> is the one project in the tree that
    ///     needs it: the <c>.vxml</c> under <c>templates/</c> belongs to a project this one copies
    ///     rather than builds.
    /// </summary>
    [Fact]
    public void A_project_that_says_its_markup_is_not_its_own_is_left_alone() {
        Unwired(
            string.Empty,
            $"<DirectoryBuildTargetsPath>{RepositoryTargets}</DirectoryBuildTargetsPath>"
            + "<VixenUiMarkupCheck>false</VixenUiMarkupCheck>"
        );

        Markup("templates/Counter.vxml", "@component Counter\n<div>hello</div>\n");

        var (succeeded, output) = Run();

        Assert.True(succeeded, output);
        Assert.DoesNotContain("VX4001", output, StringComparison.Ordinal);
    }

    // ================================================================== Plumbing

    void Markup(string relativePath, string text) {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary>
    ///     A project that references the framework and imports the targets, and says nothing at all
    ///     about markup — which is the claim.
    /// </summary>
    /// <remarks>
    ///     The generator is named explicitly because analyzers are not transitive through a
    ///     <c>ProjectReference</c>; through the package it travels in <c>analyzers/dotnet/cs</c> and
    ///     the targets arrive with it. The two forms land on the same compilation.
    /// </remarks>
    void Project() {
        Directory.CreateDirectory(root);

        File.WriteAllText(
            Path.Combine(root, "Demo.csproj"),
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <PropertyGroup>
                 <TargetFramework>net10.0</TargetFramework>
                 <Nullable>enable</Nullable>
                 <RootNamespace>Demo</RootNamespace>
                 <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
               </PropertyGroup>
               <ItemGroup>
                 <ProjectReference Include="{UiProject}" />
                 <ProjectReference Include="{GeneratorProject}" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
               </ItemGroup>
               <Import Project="{BuildTargets}" />
             </Project>
             """
        );
    }

    /// <summary>
    ///     A project with no reference to anything of ours, which is what the two wiring checks are
    ///     about: they have to fire on a project that has not been told what a <c>.vxml</c> is.
    /// </summary>
    /// <remarks>
    ///     The placeholder source is not scenery. A project with no <c>.cs</c> at all gives <c>csc</c>
    ///     nothing to do and CS2008 to say about it, and a test asserting on the absence of C#
    ///     diagnostics should not be reading around one it caused itself.
    /// </remarks>
    void Unwired(string body, string properties = "") {
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "Placeholder.cs"), "namespace Demo; static class Placeholder;\n");

        File.WriteAllText(
            Path.Combine(root, "Demo.csproj"),
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <PropertyGroup>
                 <TargetFramework>net10.0</TargetFramework>
                 <Nullable>enable</Nullable>
                 <RootNamespace>Demo</RootNamespace>
                 {properties}
               </PropertyGroup>
               {body}
             </Project>
             """
        );
    }

    (bool Succeeded, string Output) Run() {
        var process = new Process {
            StartInfo = new ProcessStartInfo("dotnet") {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.StartInfo.ArgumentList.Add("build");
        process.StartInfo.ArgumentList.Add("--nologo");

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
