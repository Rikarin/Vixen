// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using Vixen.Build;

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

    /// <summary>
    ///     The one edge from <c>Live/</c> into <c>Tools/</c> that is allowed, named rather than
    ///     hidden.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>docs/plan/27</c> § Repository layout calls this "one wart, named rather than
    ///         hidden": <c>Vixen.Live.Realm</c> needs the application host, and the application host
    ///         is <c>Tools/Vixen.App</c>. Two ways out — allow-list the edge, or move
    ///         <c>Vixen.App</c> into <c>Core/</c> where an application host arguably belongs. M-Q4
    ///         recommends moving it, <em>separately, with its own reasoning</em>, and allow-listing
    ///         this in the meantime so the MMO work is not blocked on that argument.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is a pair rather than a project name because the exception is this edge, not
    ///         that project.</b> A second <c>Live/</c> project reaching into <c>Tools/</c> would be a
    ///         new decision and should fail here until somebody makes it.
    ///     </para>
    /// </remarks>
    static readonly (string From, string To)[] AllowedUpwardReferences = [("Vixen.Live.Realm", "Vixen.App")];

    /// <summary>The edges out of <c>Core/</c> that are allowed, named rather than hidden.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>Vixen.Fuzz</c> is in <c>Core/</c> and is not a shipped library.</b> Its own
    ///         project file says so — <c>IsPackable</c> is false, and the comment above it reads
    ///         "test infrastructure, not a shipped library … it stays out of the packages even
    ///         though it lives in Core alongside what it tests". It links <c>Vixen.Raven</c> because
    ///         the compiler is one of its twenty fuzz targets, and per its README the Raven target
    ///         is "the only oracle here that is not marking its own homework".
    ///     </para>
    ///     <para>
    ///         The rule skips <c>*.Tests</c> for exactly this reason and this project does not carry
    ///         the suffix. Keying the exemption on <c>IsPackable</c> would be a second, weaker rule —
    ///         a shipped assembly is one edit from exempting itself — so it is a named pair, and a
    ///         second <c>Core/</c> project reaching into <c>Raven/</c> is a new decision that fails
    ///         here until somebody makes it.
    ///     </para>
    /// </remarks>
    static readonly (string From, string To)[] AllowedCoreEdges = [("Vixen.Fuzz", "Vixen.Raven")];

    /// <summary>
    ///     Orleans, which ADR-016 confines to the control plane and ADR-017 keeps out of the client.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>docs/plan/27</c> ADR-016 puts Orleans in exactly one tier: "no packet a player is
    ///         waiting on passes through a grain call". ADR-017 goes further — the client does not
    ///         link it, does not hold a cluster client, and does not know a grain exists, because a
    ///         cluster client is a <em>peer</em> of the cluster and handing one to an untrusted
    ///         machine makes every grain interface a public API reachable by an attacker.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The exclusion inside <c>Live/</c> is the load-bearing half.</b>
    ///         <c>Vixen.Live.Abstractions</c> is the one assembly a game client transitively
    ///         references, so it must stay Orleans-free — which is why the cluster assembly carries
    ///         serialization surrogates for the whole vocabulary rather than the vocabulary carrying
    ///         Orleans attributes. A reference added here would compile, ship, and quietly put a
    ///         server framework in an iOS binary.
    ///     </para>
    /// </remarks>
    const string OrleansPrefix = "Microsoft.Orleans";

    /// <summary>The projects inside <c>Live/</c> that still may not see Orleans.</summary>
    static readonly string[] OrleansFreeInLive = ["Vixen.Live.Abstractions"];

    /// <summary>The suffix that marks a game's own grain-interface assembly.</summary>
    /// <remarks>
    ///     ⚠ <b>A game writes an Orleans assembly, and this rule used to forbid the one doc 27 tells
    ///     it to write.</b> § The three assemblies a game writes gives <c>MyGame.Cluster</c> the row
    ///     "may reference <c>Microsoft.Orleans.Sdk</c> and <c>MyGame.Contracts</c>", and
    ///     <c>Samples/14-Mmo</c> is that row built — so the check was failing the reference
    ///     implementation of the design it cites.
    ///     <para>
    ///         The allowance is the suffix rather than a list of names because it is the naming the
    ///         doc and the template both use, and because what ADR-017 actually protects is the
    ///         <em>client's</em> reference graph: a game's cluster assembly is precisely the one its
    ///         client may not name, which the layer rules below still check. Nothing here lets
    ///         Orleans into <c>.Contracts</c>, <c>.Shared</c> or <c>.Client</c>.
    ///     </para>
    /// </remarks>
    const string ClusterSuffix = ".Cluster";

    Target CheckArchitecture => definition => definition
        .Description(
            "Fails on a layer violation, a banned IL-rewriting package, editor-only code in a runtime "
            + "assembly, or an editor plugin that reaches the editor application"
        )
        .Executes(() => {
                var projects = RootDirectory
                    .GlobFiles(
                        "Core/**/*.csproj",
                        "Gameplay/**/*.csproj",
                        "Platform/**/*.csproj",
                        "Editor/**/*.csproj",
                        "Raven/**/*.csproj",
                        "Tools/**/*.csproj",
                        "Live/**/*.csproj",
                        "Samples/**/*.csproj"
                    )
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

                    // ADR-016 and ADR-017. Orleans lives in Live/ and not everywhere in it.
                    var orleans = packages
                        .Where(package => package.StartsWith(OrleansPrefix, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (orleans.Count > 0) {
                        if (layer == "Samples" && name.EndsWith(ClusterSuffix, StringComparison.Ordinal)) {
                            // Doc 27's MyGame.Cluster row, which is allowed exactly this.
                        } else if (layer != "Live") {
                            violations.Add(
                                $"{name} is in {layer} and references {orleans[0]}. Orleans is the control plane and "
                                + "only the control plane — see docs/plan/27 ADR-016."
                            );
                        } else if (OrleansFreeInLive.Contains(name, StringComparer.Ordinal)) {
                            violations.Add(
                                $"{name} references {orleans[0]}, and it is the one Live/ assembly a game client "
                                + "transitively links (ADR-017). The surrogates in Vixen.Live.Cluster exist so that "
                                + "it does not have to."
                            );
                        }
                    }

                    foreach (var reference in references) {
                        var referenced = LayerOfProject(projects, reference);

                        // Core sits below Platform, and both sit below Editor and Tools. A
                        // reference upward makes the lower layer unusable without the higher one,
                        // which defeats the point of having layers.
                        //
                        // ⚠ Raven is in the list, and it was not. docs/plan/37's `AiLayeringTests`
                        // has forbidden `Vixen.Raven` to a Core assembly since it was written and
                        // this rule did not — so the assembly test and the gate meant to replace it
                        // disagreed, and the gate was the permissive one. Raven is a layer here
                        // already (see the Live rule below), it sits on Core rather than under it,
                        // and a shipped runtime library that links a shader compiler is the thing
                        // this whole block exists to refuse.
                        if (layer == "Core"
                            && referenced is "Gameplay" or "Platform" or "Editor" or "Tools" or "Live" or "Raven"
                            && !AllowedCoreEdges.Contains((name, reference))) {
                            violations.Add($"{name} is in Core and references {reference}, which is not.");
                        }

                        // Gameplay sits on Core and under everything else. docs/plan/28's libraries
                        // are engine-side runtime code — a client links them — so the thing that
                        // must never happen is one of them reaching into the online service tier and
                        // making "items and quests" undeployable without an orchestrator.
                        if (layer == "Gameplay" && referenced is "Editor" or "Tools" or "Live") {
                            violations.Add($"{name} is in Gameplay and references {reference}, which is above it.");
                        }

                        if (layer == "Platform" && referenced is "Editor" or "Tools" or "Live") {
                            violations.Add($"{name} is in Platform and references {reference}, which is above it.");
                        }

                        // docs/plan/27 § Repository layout: nothing in Core/, Gameplay/, Platform/,
                        // Editor/ or Raven/ may reference Live/, and Live/ may not reference
                        // Editor/. The first half is what keeps a game client from linking an
                        // orchestrator; the second is what keeps a shipped, operated tier from
                        // depending on a developer tool.
                        if ((layer is "Editor" or "Raven") && referenced == "Live") {
                            violations.Add($"{name} is in {layer} and references {reference}, which is in Live.");
                        }

                        if (layer == "Live" && referenced == "Editor") {
                            violations.Add(
                                $"{name} is in Live and references {reference}. Live/ is shipped and operated; "
                                + "Editor/ is a developer tool. See docs/plan/27 § Repository layout."
                            );
                        }

                        // Live sits above Tools only by exception, and the exception is one edge.
                        if (layer == "Live"
                            && referenced == "Tools"
                            && !AllowedUpwardReferences.Contains((name, reference))) {
                            violations.Add(
                                $"{name} is in Live and references {reference}, which is in Tools. Only "
                                + $"{string.Join(", ", AllowedUpwardReferences.Select(edge => $"{edge.From} → {edge.To}"))} "
                                + "is allowed, and docs/plan/27 M-Q4 is the argument for removing even that."
                            );
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

                // Doc 48 exit criterion 10. A plugin is a project that names the plugin contract, and
                // no plugin may reach the editor application — directly or through anything else.
                //
                // ⚠ The rule itself lives in PluginReferenceRule, which this target is one caller of
                // and Vixen.Editor.Texturing.Tests is the other. It was written inside this
                // `Executes` and could therefore only ever answer by running a gate that compiles the
                // solution in Release — so it shipped without anybody having seen it produce an
                // answer, which is the state this repository's own rule says to fix first.
                var edges = PluginReferenceRule.Edges(projects.Select(project => project.ToString()));

                Assert.True(PluginReferenceRule.Vacuity(edges) is null, PluginReferenceRule.Vacuity(edges) ?? "");

                violations.AddRange(PluginReferenceRule.Violations(edges));

                foreach (var violation in violations) {
                    Log.Error("{Violation}", violation);
                }

                Assert.True(
                    violations.Count == 0,
                    $"{violations.Count} architecture violation(s). See the errors above."
                );

                // ⚠ The plugin count is logged because it is the rule's subject set, and a subject set
                // is the half of a rule nothing checks. It read nine while the editor application —
                // which references the contract because it hosts plugins — was being counted as one
                // of them.
                Log.Information(
                    "Checked {Count} projects, of which {Plugins} are editor plugins; no violations.",
                    projects.Count,
                    PluginReferenceRule.Plugins(edges).Count
                );
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
