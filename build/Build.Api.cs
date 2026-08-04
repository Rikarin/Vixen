// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
///     The public API gate: every packable assembly's surface, compared with the baseline committed
///     beside the project that produces it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/00 § Non-negotiables makes `public` require a reason and a
///         `PublicAPI.Unshipped.txt` entry. This is what turns that from a habit into a build
///         failure: an addition nobody wrote down fails, and — the half that is easy to forget — so
///         does a removal, which is the change that breaks a consumer without breaking anything
///         here.
///     </para>
///     <para>
///         The work is done by <c>Tools/Vixen.ApiCheck</c>. What lives here is only the decision
///         about which assemblies are covered, because that decision has to agree with
///         <c>Directory.Build.props</c>'s RUNTIME profile — the same set that gets
///         <c>IsPackable=true</c> is the set whose surface is a promise to somebody.
///     </para>
/// </remarks>
partial class Build {
    [Parameter("Rewrite the public API baselines instead of checking them")]
    readonly bool UpdateApi;

    /// <summary>
    ///     The framework the covered assemblies build for. `net10.0-ios`, `-android` and `-browser`
    ///     projects are deliberately not covered: they are outside <c>Vixen.slnx</c> (see
    ///     <see cref="CompileMobile" />), so nothing has built them and there would be nothing to
    ///     read.
    /// </summary>
    const string ApiFramework = "net10.0";

    Target CheckApi => definition => definition
        .Description("Fails if a packable assembly's public surface differs from its committed baseline")
        .DependsOn(Restore)
        .Executes(() => {
                var projects = ApiCheckedProjects();

                Assert.True(projects.Count > 0, "Found no packable projects to check — the glob is wrong.");

                // Release, whatever `--configuration` says, and this target is the one place in the
                // build where ignoring it is right. A public surface is a promise about a shipped
                // package, `Pack` ships Release, and the two configurations do not agree: the engine
                // has `public const bool` feature flags — LeakTracker.IsSupported,
                // JobScheduler.SafetyChecksEnabled — whose values are `#if DEBUG`, exactly as
                // intended. Baselining Debug would record `IsSupported = true` as the promise and
                // fail every CI run; baselining Release and checking whatever the developer last
                // built would fail on their machine instead. So the gate has one subject.
                DotNetBuild(settings => settings
                    .SetProjectFile(Solution)
                    .SetConfiguration(Configuration.Release)
                    .EnableNoRestore()
                );

                var arguments = new List<string>();

                if (UpdateApi) {
                    arguments.Add("--update");
                }

                foreach (var project in projects) {
                    var assembly = project.Parent / "bin" / Configuration.Release.ToString() / ApiFramework
                        / $"{AssemblyNameOf(project)}.dll";

                    // Not a warning. An assembly that is not where the build put every other one is
                    // either a project the glob should not have matched or a build that did not
                    // happen, and both make this target pass by checking less than it says it does.
                    Assert.FileExists(assembly, $"{project.NameWithoutExtension} has no built assembly at {assembly}.");

                    arguments.Add(assembly);
                }

                Log.Information("Checking the public surface of {Count} assemblies.", projects.Count);

                DotNetRun(settings => settings
                    .SetProjectFile(RootDirectory / "Tools" / "Vixen.ApiCheck" / "Vixen.ApiCheck.csproj")
                    .SetConfiguration(Configuration.Release)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetApplicationArguments(arguments)
                );

                if (UpdateApi) {
                    Log.Warning(
                        "The API baselines have been rewritten. Read the diff before committing: the "
                        + "point of the file is that somebody approved what is in it."
                    );
                }
            }
        );

    /// <summary>
    ///     The projects whose public surface is a promise: the RUNTIME profile of
    ///     <c>Directory.Build.props</c>, minus the ones that opt out of packing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Plus one exception, and it is the only one. <c>Editor/</c> is applications and is not
    ///         covered — but <c>Vixen.Editor.Plugin</c> is not an application, it is the contract a
    ///         third party compiles against, and
    ///         <a href="../docs/plan/11-editor.md">doc 11</a> § <c>Vixen.Editor.Plugin</c> asks for a
    ///         stricter compatibility policy there than anywhere else in the editor. A stricter promise
    ///         nobody diffed is not a promise, so it is checked here with everything else that makes one.
    ///     </para>
    ///     <para>
    ///         <c>Live/</c> and <c>Gameplay/</c> are covered for the same reason <c>Core/</c> is:
    ///         they pack, and something else compiles against them.
    ///         <a href="../docs/plan/27-mmo-framework.md">Doc 27</a> § The three assemblies a game
    ///         writes has a game's own contracts referencing <c>Vixen.Live.Abstractions</c>, and a
    ///         realm binary referencing <c>Vixen.Live.Realm</c> — a promise made to somebody else's
    ///         build, which is exactly the subject of this gate.
    ///     </para>
    /// </remarks>
    List<AbsolutePath> ApiCheckedProjects() =>
        RootDirectory
            .GlobFiles(
                "Core/**/*.csproj",
                "Gameplay/**/*.csproj",
                "Platform/**/*.csproj",
                "Live/**/*.csproj",
                "Editor/Vixen.Editor.Plugin/*.csproj"
            )
            .Where(path => !path.ToString().Contains("/bin/", StringComparison.Ordinal))
            .Where(path => !path.ToString().Contains("/obj/", StringComparison.Ordinal))
            .Where(path => !path.NameWithoutExtension.EndsWith(".Tests", StringComparison.Ordinal))
            .Where(path => !path.NameWithoutExtension.EndsWith(".Generator", StringComparison.Ordinal))
            .Where(path => !path.NameWithoutExtension.EndsWith(".Generators", StringComparison.Ordinal))
            .Where(path => !path.NameWithoutExtension.EndsWith(".Analyzers", StringComparison.Ordinal))
            .Where(IsPackable)
            .Where(path => TargetFrameworkOf(path) == ApiFramework)
            .OrderBy(path => path.NameWithoutExtension, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     A project that says <c>IsPackable=false</c> ships to nobody, so it promises nobody
    ///     anything. Everything else in Core and Platform is packable by profile rather than by
    ///     declaration, which is why the absence of the property means <em>yes</em> here.
    /// </summary>
    static bool IsPackable(AbsolutePath project) =>
        !string.Equals(PropertyOf(project, "IsPackable"), "false", StringComparison.OrdinalIgnoreCase);

    static string TargetFrameworkOf(AbsolutePath project) =>
        PropertyOf(project, "TargetFramework") ?? PropertyOf(project, "TargetFrameworks") ?? string.Empty;

    static string AssemblyNameOf(AbsolutePath project) =>
        PropertyOf(project, "AssemblyName") ?? project.NameWithoutExtension;

    static string? PropertyOf(AbsolutePath project, string name) =>
        XDocument.Load(project).Descendants(name).FirstOrDefault()?.Value.Trim();
}
