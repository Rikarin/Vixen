// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Nuke.Common;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
///     The documentation graph: every public type in the engine, classified as what it actually is.
/// </summary>
/// <remarks>
///     <para>
///         Spec: <a href="../docs/plan/25-documentation-generator-and-site.md">doc 25</a>. The work is
///         done by <c>Tools/Vixen.DocGen</c>; what lives here is the two decisions the build owns —
///         which configuration the graph is read in, and which commit its source links point at.
///     </para>
///     <para>
///         ⚠ <b>Release, and a built tree, and the two have to be the same one.</b> The engine
///         resolves its own generators through <c>ProjectReference</c> with
///         <c>OutputItemType=Analyzer</c>, so a design-time build in another configuration finds no
///         analyzer where the path says one is, runs none of them, and produces a graph missing
///         everything they emit — measured at 298 types and four whole kinds, with nothing in the
///         output to say so
///         (<a href="../docs/plan/spikes/docs-graph/RESULT.md">the spike</a> § F1). Release matches
///         <see cref="CheckApi" />'s subject, for the same reason it does.
///     </para>
/// </remarks>
partial class Build {
    [Parameter("Also fail when the documentation graph and the PublicAPI baselines disagree")]
    readonly bool VerifyDocs = true;

    /// <summary>
    ///     Rewrites <c>docs/DocsExempt.txt</c> instead of failing on it — the coverage gate's
    ///     <c>--update-api</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Assisted, not automatic, and the difference is the whole gate.</b> A gate that wrote
    ///     its own exemption whenever a type appeared without a page would fail on nothing, forever;
    ///     what makes the file worth having is that a person ran this, read the diff, and committed
    ///     it. Which is exactly the discipline <see cref="UpdateApi" /> has for the same reason.
    /// </remarks>
    [Parameter("Rewrite docs/DocsExempt.txt from the graph instead of failing — review the diff")]
    readonly bool UpdateExemptions;

    /// <summary>Set by <see cref="CheckDocs" />; `Docs` on its own emits without gating.</summary>
    bool CheckDocsGate;

    /// <summary>The commit the source links point at. Injected, so a CI run stamps the real one.</summary>
    [GitRepository]
    readonly GitRepository DocsRepository;

    AbsolutePath DocsDirectory => RootDirectory / "artifacts" / "docs";

    Target Docs => definition => definition
        .Description("Emits the documentation graph the site is rendered from")
        .DependsOn(Restore)
        .Produces(DocsDirectory / "graph.json")
        .Executes(() => {
                DotNetBuild(settings => settings
                    .SetProjectFile(Solution)
                    .SetConfiguration(Configuration.Release)
                    .EnableNoRestore()
                );

                var arguments = new List<string> {
                    RootDirectory / "Vixen.slnx",
                    "--output", DocsDirectory,
                    "--configuration", Configuration.Release,

                    // The one project a design-time build cannot compile, and the reason is worth
                    // the line: its parser visitors come from ANTLR's MSBuild task rather than from
                    // a Roslyn generator, and a workspace runs generators but not tasks. It is a
                    // test project holding the differential oracle of docs/plan/18, so nothing it
                    // declares is documented surface either way. The tool prints the excuse.
                    "--excuse", "Vixen.Raven.Tests"
                };

                if (UpdateExemptions) {
                    // Exclusive with the gate: this run rewrites the file the gate reads, so failing
                    // on its previous contents would be failing on something already fixed.
                    arguments.Add("--update-exemptions");
                } else if (CheckDocsGate) {
                    arguments.Add("--check-docs");
                }

                // Set by `Release`, which runs first and triggers this. The graph is archived under
                // docs/api-history/ and diffed against the previous release in the same pass that
                // produced it, so the table cannot describe a graph other than the one that shipped.
                if (ReleasingVersion is not null) {
                    arguments.Add("--release");
                    arguments.Add(ReleasingVersion);

                    if (ReleaseDate is not null) {
                        arguments.Add("--date");
                        arguments.Add(ReleaseDate);
                    }
                }

                // The commit the source links point at. A local run without one still produces the
                // graph — with paths and no URLs, which is honest about what a dirty tree can promise.
                var commit = DocsRepository?.Commit;

                if (commit is not null) {
                    arguments.Add("--commit");
                    arguments.Add(commit);
                } else {
                    Log.Warning("No commit resolved; the graph will carry paths without source links.");
                }

                if (VerifyDocs) {
                    arguments.Add("--verify-baselines");
                }

                DotNetRun(settings => settings
                    .SetProjectFile(RootDirectory / "Tools" / "Vixen.DocGen" / "Vixen.DocGen.csproj")
                    .SetConfiguration(Configuration.Release)
                    .EnableNoRestore()
                    .SetApplicationArguments(arguments)
                );

                if (UpdateExemptions) {
                    Log.Warning(
                        "docs/DocsExempt.txt has been rewritten. Read the diff before committing: a "
                        + "line added here is a public type nobody has written about, and saying so "
                        + "out loud is the only thing the file does."
                    );

                    return;
                }

                Log.Information("The documentation graph is in {Directory}.", DocsDirectory);
            }
        );

    /// <summary>
    ///     The written half's gate — docs/plan/25 § Part 5.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What it fails on: a guide page that breaks the five-heading contract, one whose `api:`
    ///         names a symbol the graph does not have, a link or a snippet region that resolves to
    ///         nothing, a page nothing links to, a public type with neither a page nor a line in
    ///         <c>docs/DocsExempt.txt</c>, and — the one worth the most — <b>an example that does not
    ///         compile</b>. Documentation examples rot silently and are the first thing a new user
    ///         copies.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The exemption file only ever shrinks.</b> It was seeded with the 3 674 types that
    ///         predate the gate, because switching coverage on for all of them at once would block
    ///         every merge for a quarter — which is the failure § Part 5 predicts for itself. What is
    ///         live from day one is the half that stops the backlog growing: a <em>new</em> public
    ///         type is not in the file, so it fails until it has a page or a reviewed reason.
    ///     </para>
    /// </remarks>
    Target CheckDocs => definition => definition
        .Description("Fails on a type with no page, a page that breaks its contract, or an example that does not compile")
        .DependsOn(Restore)
        .Executes(() => {
                CheckDocsGate = true;
            }
        )
        .Triggers(Docs);
}
