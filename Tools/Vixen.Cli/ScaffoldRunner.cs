// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Cli;

/// <summary>Which kind of project to write.</summary>
public enum Template {
    /// <summary>A game: a <c>Game</c> subclass, a one-line host, and an <c>Assets/</c> folder.</summary>
    Game = 0,

    /// <summary>A library both a game and the editor can reference.</summary>
    Library = 1
}

/// <summary>Writes a new project.</summary>
/// <remarks>
///     <para>
///         <b>Why this exists next to <c>dotnet new</c> rather than instead of it.</b>
///         [Doc 17](../../docs/plan/17-app-heads-and-shipping.md) specifies a template pack —
///         <c>dotnet new vixen-game</c> and four siblings — and that is still the right thing for
///         somebody who has installed one. This is the version that works before anything is
///         installed, which is the state a person is in when they are deciding whether to try the
///         engine at all. The two produce the same layout; when the pack exists it should be
///         generated from these strings rather than kept beside them.
///     </para>
///     <para>
///         <b>What it scaffolds against is the SDK, not a pile of package references.</b> A project
///         says <c>&lt;Project Sdk="Vixen.Sdk/x.y.z"&gt;</c> and gets the import-before-compile and
///         content-build-after-build wiring with nothing else written down —
///         <c>Tools/Vixen.Sdk</c>'s whole point. The alternative, a template listing every
///         <c>PackageReference</c> the engine currently needs, is a template that is wrong one
///         release later.
///     </para>
///     <para>
///         <b>Nothing is overwritten.</b> A scaffolder that clobbers is one nobody runs twice, and
///         "I pointed it at the wrong directory" is the ordinary mistake rather than the exotic one.
///     </para>
/// </remarks>
public static class ScaffoldRunner {
    /// <summary>The SDK version a new project pins.</summary>
    /// <remarks>
    ///     Read from this assembly rather than written down, so a scaffolded project asks for the SDK
    ///     that matches the tool that scaffolded it. A hard-coded version here is one that silently
    ///     goes stale and produces projects that will not restore.
    /// </remarks>
    public static string SdkVersion { get; } =
        typeof(ScaffoldRunner).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.1.0";

    /// <summary>Writes the project.</summary>
    /// <param name="template">What to write.</param>
    /// <param name="name">The project's name. Becomes the assembly name and the root namespace.</param>
    /// <param name="directory">Where to write it. Created if it is not there.</param>
    /// <param name="output">Where to report what was written.</param>
    /// <returns>Success, or a usage error with the reason already written.</returns>
    public static ExitCode Run(Template template, string name, string directory, TextWriter output) {
        ArgumentNullException.ThrowIfNull(output);

        if (!IsUsableName(name)) {
            output.WriteLine(
                $"'{name}' cannot be a project name. It has to start with a letter and hold only "
                + "letters, digits, underscores and dots — it becomes both an assembly name and a "
                + "namespace."
            );

            return ExitCode.UsageError;
        }

        var root = Path.GetFullPath(directory);
        var files = template is Template.Game ? GameFiles(name) : LibraryFiles(name);

        // Every collision is found before anything is written. A half-scaffolded directory is worse
        // than an untouched one, because the second is obviously a no-op and the first is not.
        var existing = files.Keys
            .Select(relative => Path.Combine(root, relative))
            .Where(File.Exists)
            .ToList();

        if (existing.Count > 0) {
            output.WriteLine($"Nothing was written: {existing.Count} file(s) are already there.");

            foreach (var path in existing) {
                output.WriteLine($"  {Path.GetRelativePath(root, path)}");
            }

            return ExitCode.UsageError;
        }

        foreach (var (relative, content) in files) {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        output.WriteLine($"Created {template.ToString().ToLowerInvariant()} '{name}' in {root}");

        foreach (var relative in files.Keys.Order(StringComparer.Ordinal)) {
            output.WriteLine($"  {relative}");
        }

        if (template is Template.Game) {
            output.WriteLine();
            output.WriteLine("  dotnet run     — build the content and play it");
            output.WriteLine("  vixen build    — publish it for a target");
        }

        return ExitCode.Success;
    }

    /// <summary>
    ///     Whether a name can be both an assembly name and a namespace.
    /// </summary>
    /// <remarks>
    ///     Checked here rather than left to the compiler, because the compiler's complaint arrives
    ///     after the files exist and names a generated line rather than the argument that caused it.
    /// </remarks>
    static bool IsUsableName(string name) =>
        name.Length > 0
        && char.IsLetter(name[0])
        && name.All(character => char.IsLetterOrDigit(character) || character is '_' or '.');

    static Dictionary<string, string> GameFiles(string name) =>
        new(StringComparer.Ordinal) {
            [$"{name}.csproj"] =
                $"""
                 <Project Sdk="Vixen.Sdk/{SdkVersion}">

                     <PropertyGroup>
                         <TargetFramework>net10.0</TargetFramework>
                         <OutputType>Exe</OutputType>
                         <Nullable>enable</Nullable>
                         <ImplicitUsings>enable</ImplicitUsings>
                     </PropertyGroup>

                     <ItemGroup>
                         <!--
                             The SDK wires the build — import before the compiler, content build after
                             it — and deliberately references no engine assemblies. What a project
                             links against is the project's decision, so the host is named here.
                             Vixen.App brings Vixen.Core, Vixen.Platform and the desktop platform with
                             it; add Vixen.Engine for the ECS and Vixen.Graphics.Vulkan for a renderer.
                         -->
                         <PackageReference Include="Vixen.App" Version="{SdkVersion}" />
                     </ItemGroup>

                 </Project>

                 """,

            // Top-level statements, so there is no namespace declaration here: one before them does
            // not compile, and the Game type is reached by the using instead.
            ["Program.cs"] =
                $"""
                 using {name};
                 using Vixen.App;

                 // Everything VixenApp.Run does is a public call you can inline and edit. See
                 // docs/plan/17: nothing in the boot path is inaccessible.
                 return VixenApp.Run<{name}Game>(args);

                 """,

            [$"{name}Game.cs"] =
                $$"""
                  using Vixen.App;
                  using Vixen.Core;

                  namespace {{name}};

                  public sealed class {{name}}Game : Game {
                      protected override void OnConfigure(AppConfig config) {
                          config.Name = "{{name}}";
                          config.Window = new() { Title = "{{name}}", Size = new(1280, 720), IsVisible = true };
                      }

                      protected override void OnUpdate(GameTime time) {
                      }

                      protected override void OnRender(GameTime time) {
                      }
                  }

                  """,

            // An empty group, so `vixen content build` has somewhere to put the first asset rather
            // than reporting that the project configures nothing. The planner invents a Default
            // group when none exists, and a file the author can see beats a default they cannot.
            ["Assets/Default.vxgroup"] =
                """
                name: Default
                loadPath: Local
                packing: PackTogether
                bundleNaming: FilenameHash

                """,

            [".gitignore"] = Ignore
        };

    static Dictionary<string, string> LibraryFiles(string name) =>
        new(StringComparer.Ordinal) {
            // No Vixen.Sdk: a library has no assets to import and no content to build, so the SDK
            // would add two no-op build steps and a tool dependency for nothing.
            [$"{name}.csproj"] =
                """
                <Project Sdk="Microsoft.NET.Sdk">

                    <PropertyGroup>
                        <TargetFramework>net10.0</TargetFramework>
                        <Nullable>enable</Nullable>
                        <ImplicitUsings>enable</ImplicitUsings>
                    </PropertyGroup>

                    <ItemGroup>
                        <PackageReference Include="Vixen.Engine" Version="0.1.0" />
                    </ItemGroup>

                </Project>

                """,

            [$"{name}.cs"] =
                $$"""
                  namespace {{name}};

                  public static class {{name}} {
                  }

                  """,

            [".gitignore"] = Ignore
        };

    /// <summary>
    ///     What a Vixen project never commits.
    /// </summary>
    /// <remarks>
    ///     <c>Library/</c> is the one worth spelling out: it is the import cache and the artefact
    ///     database, it is reproducible from the assets and their <c>.meta</c> files, and it is large.
    ///     A project that commits it has merge conflicts in binary blobs on every branch.
    /// </remarks>
    const string Ignore =
        """
        bin/
        obj/

        # Reproducible from Assets/ and the .meta files beside them. Never committed.
        Library/

        """;
}
