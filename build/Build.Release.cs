// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
///     The release ritual — docs/plan/25 § 6.2.
/// </summary>
/// <remarks>
///     <para>
///         One target, because the two halves are the same fact written twice. <c>Vixen.ApiCheck</c>
///         folds <c>PublicAPI.Unshipped.txt</c> into <c>PublicAPI.Shipped.txt</c>: everything approved
///         becomes everything promised. <c>Vixen.DocGen</c> archives the graph and diffs it against
///         the previous release: everything promised becomes a table a reader can act on. Run apart,
///         the two would eventually disagree about what shipped — and the one that would be wrong is
///         the changelog, which is the one people read.
///     </para>
///     <para>
///         ⚠ <b>Everything it writes is committed.</b> The archive, the table, the changelog section
///         and the folded baselines are part of the release, not build output — § 6.1's argument is
///         that rebuilding an old release two years from now means restoring an old SDK, and the
///         first release where that fails is the release where the changelog quietly stops existing.
///     </para>
/// </remarks>
partial class Build {
    [Parameter("The version being released, without the leading v — e.g. 0.2.0")]
    readonly string ReleaseVersion;

    [Parameter("The release's date, ISO-8601. Defaults to today, UTC")]
    readonly string ReleaseDate;

    /// <summary>Set by <see cref="Release" />; <see cref="Docs" /> archives when it is there.</summary>
    string ReleasingVersion;

    Target Release => definition => definition
        .Description("Folds the API baselines, archives the graph and emits the release's table")
        // Release, for the reason CheckApi gives: the public surface is a promise about a packed
        // assembly, and Debug's `#if DEBUG` feature flags are not that promise. ⚠ This built the
        // solution itself and Docs, which it triggers, then built it again — so a release run paid
        // the 166 s twice over, and its own copy passed no `-m:` so it ignored `--workers` as well.
        .DependsOn(CompileRelease)
        .Requires(() => ReleaseVersion)
        .Executes(() => {
                var version = ReleaseVersion.TrimStart('v');
                var projects = ApiCheckedProjects();

                Assert.True(projects.Count > 0, "Found no packable projects to fold — the glob is wrong.");

                var arguments = new List<string> { "--fold" };

                arguments.AddRange(projects.Select(project =>
                    (project.Parent / "bin" / Configuration.Release.ToString() / ApiFramework
                        / $"{AssemblyNameOf(project)}.dll").ToString()));

                Log.Information("Folding the public surface of {Count} assemblies into Shipped.", projects.Count);

                DotNetRun(settings => settings
                    .SetProjectFile(RootDirectory / "Tools" / "Vixen.ApiCheck" / "Vixen.ApiCheck.csproj")
                    .SetConfiguration(Configuration.Release)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetApplicationArguments(arguments)
                );

                // Docs runs next and reads this. Set after the fold, so a fold that threw does not
                // leave an archive claiming a release that was never made.
                ReleasingVersion = version;

                Log.Information(
                    "Folded. {Version} is archived by the Docs target that follows, and the table it "
                    + "writes into CHANGELOG.md and docs/api-history/ is part of the commit.",
                    version
                );
            }
        )
        .Triggers(Docs);
}
