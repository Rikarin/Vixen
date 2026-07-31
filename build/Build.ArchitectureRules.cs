// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

/// <summary>
///     The layer rules from docs/plan/00, checked rather than trusted.
/// </summary>
/// <remarks>
///     These are cheap to enforce now and expensive to enforce later. A layer violation is one line
///     to introduce and a month to unwind once twenty things depend on it having been allowed — and
///     the `Vixen.Ui` ⇸ `Vixen.Engine` boundary in particular is the thing that makes the
///     application-framework claim real rather than aspirational.
/// </remarks>
partial class Build {
    /// <summary>
    ///     Packages that rewrite IL. ADR-002 rejects the whole category: Stride's
    ///     `AssemblyProcessor` rewrites every assembly after compilation with Mono.Cecil, and that
    ///     is precisely what this engine exists without. Roslyn source generators do the same jobs
    ///     with output you can read and step through.
    /// </summary>
    static readonly string[] BannedPackages = [
        "Mono.Cecil",
        "dnlib",
        "ILRepack",
        "Fody",
        "Costura.Fody",
        "ILRepack.Lib.MSBuild.Task"
    ];

    /// <summary>
    ///     ADR-015: authoring-format importers are import-time only. Assimp is a large C++ library
    ///     that reads two dozen authoring formats, and a player loads the meshes the content build
    ///     has already produced — so a runtime assembly that references it drags the whole importer
    ///     into every shipped game for a job the runtime does not do. Shipped textures are KTX2 and
    ///     shipped meshes are compiled, both read by Vixen's own code.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>SixLabors.ImageSharp</c> used to sit here as well, and no longer does. Nothing in
    ///         the repository references it — the editor decodes with <c>StbImageSharp</c>, for the
    ///         reason <c>Directory.Packages.props</c> § Imaging records — so the entry was guarding
    ///         against a package that is not in the restore graph of any project and has no
    ///         <c>PackageVersion</c> to reference. A rule that names a package nobody can add
    ///         without first adding it to central package management is a rule about a hypothetical,
    ///         and it read as though ImageSharp were still a dependency somewhere.
    ///     </para>
    /// </remarks>
    static readonly string[] EditorOnlyPackages = ["Silk.NET.Assimp"];

    Target CheckArchitecture => definition => definition
        .Description("Fails on a layer violation, a banned IL-rewriting package, or editor-only code in a runtime assembly")
        .Executes(() => {
                var projects = RootDirectory
                    .GlobFiles("Core/**/*.csproj", "Platform/**/*.csproj", "Editor/**/*.csproj", "Raven/**/*.csproj", "Tools/**/*.csproj", "Samples/**/*.csproj")
                    .Where(path => !path.ToString().Contains("/bin/", StringComparison.Ordinal))
                    .Where(path => !path.ToString().Contains("/obj/", StringComparison.Ordinal))

                    // ⚠ Tools/Vixen.Templates/templates/ holds project files that are not this
                    // repository's — they are what `dotnet new` writes into somebody else's
                    // directory, they name packages rather than projects, and their layer is
                    // whatever the person who scaffolds them decides. Checking them here would be
                    // this build asserting rules about a project it does not own.
                    .Where(path => !path.ToString().Contains("/Vixen.Templates/templates/", StringComparison.Ordinal))
                    .ToList();

                Assert.True(projects.Count > 0, "Found no projects to check — the glob is wrong.");

                var violations = new List<string>();

                foreach (var project in projects) {
                    var name = project.NameWithoutExtension;
                    var layer = LayerOf(project);
                    var document = XDocument.Load(project);

                    var packages = document.Descendants("PackageReference")
                        .Select(element => element.Attribute("Include")?.Value)
                        .Where(value => value is not null)
                        .Select(value => value!)
                        .ToList();

                    var references = document.Descendants("ProjectReference")
                        .Select(element => element.Attribute("Include")?.Value)
                        .Where(value => value is not null)
                        .Select(value => AbsolutePath.Create(project.Parent / value!).NameWithoutExtension)
                        .ToList();

                    foreach (var banned in packages.Where(package => BannedPackages.Contains(package, StringComparer.OrdinalIgnoreCase))) {
                        violations.Add($"{name} references {banned}, which rewrites IL (ADR-002).");
                    }

                    // A test project may reference anything: it is not shipped, and forbidding it
                    // would mean a layer could not be tested against its neighbours.
                    if (name.EndsWith(".Tests", StringComparison.Ordinal)) {
                        continue;
                    }

                    if (layer is "Core" or "Platform") {
                        foreach (var editorOnly in packages.Where(package => EditorOnlyPackages.Contains(package, StringComparer.OrdinalIgnoreCase))) {
                            violations.Add($"{name} is a runtime assembly and references {editorOnly}, which is import-time only (ADR-015).");
                        }
                    }

                    foreach (var reference in references) {
                        // Core sits below Platform, and both sit below Editor and Tools. A
                        // reference upward makes the lower layer unusable without the higher one,
                        // which defeats the point of having layers.
                        if (layer == "Core" && LayerOfProject(projects, reference) is "Platform" or "Editor" or "Tools") {
                            violations.Add($"{name} is in Core and references {reference}, which is not.");
                        }

                        if (layer == "Platform" && LayerOfProject(projects, reference) is "Editor" or "Tools") {
                            violations.Add($"{name} is in Platform and references {reference}, which is above it.");
                        }

                        // The single most important boundary in the codebase. A UI framework that
                        // needs a scene, an ECS world and a game loop is not an application
                        // framework, and this reference is cheap to add and expensive to unwind.
                        if (name.StartsWith("Vixen.Ui", StringComparison.Ordinal) && reference == "Vixen.Engine") {
                            violations.Add($"{name} references Vixen.Engine. See docs/plan/00 § Layer discipline.");
                        }

                        // The same boundary, from the other side. docs/plan/02 § Samples describes
                        // 02-HelloUi as "Vixen.Ui only, no engine — proves the UI/Engine boundary",
                        // and doc 15 makes it what proves the framework standalone before the editor
                        // is allowed to depend on it. A sample that reached for Vixen.App would prove
                        // nothing — Vixen.App references Vixen.Engine — and it is exactly the change
                        // somebody makes to save writing a frame loop. Asserted here so that saving
                        // it fails the build rather than quietly deleting the demonstration.
                        if (name == "HelloUi" && reference is "Vixen.Engine" or "Vixen.App") {
                            violations.Add(
                                $"{name} references {reference}, and it exists to demonstrate that it does not have to. "
                                + "See docs/plan/02 § Samples."
                            );
                        }
                    }
                }

                foreach (var violation in violations) {
                    Log.Error("{Violation}", violation);
                }

                Assert.True(
                    violations.Count == 0,
                    $"{violations.Count} architecture violation(s). See the errors above."
                );

                Log.Information("Checked {Count} projects; no violations.", projects.Count);
            }
        );

    static string LayerOf(AbsolutePath project) {
        var relative = RootDirectory.GetRelativePathTo(project).ToString();
        var separator = relative.IndexOf('/');
        return separator < 0 ? "Root" : relative[..separator];
    }

    static string LayerOfProject(IReadOnlyList<AbsolutePath> projects, string name) {
        var match = projects.FirstOrDefault(project => project.NameWithoutExtension == name);
        return match is null ? "Unknown" : LayerOf(match);
    }
}
