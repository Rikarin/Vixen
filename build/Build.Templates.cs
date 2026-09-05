// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

/// <summary>
///     Scaffolds every template from the packed feed, with nothing in the package cache, and builds
///     what comes out.
/// </summary>
/// <remarks>
///     <para>
///         Overview § 1.15 and [#114](https://github.com/Rikarin/Vixen/issues/114): the templates are
///         verified through the real <c>dotnet new</c> engine by
///         <c>Tools/Vixen.Templates.Tests</c> — packed, installed into a private hive, instantiated
///         outside the repository and compared byte for byte — and <b>none of the six is ever
///         restored or built</b>, because a restore needs a feed and no feed existed. This target is
///         that feed: <see cref="Pack" />'s own output directory, and a package cache with nothing in
///         it.
///     </para>
///     <para>
///         ⚠️ <b>The obvious version of this test passes for a reason that has nothing to do with the
///         templates.</b> Measured, twice, a month apart: a scaffolded project restored from a
///         directory outside this repository <i>succeeds</i> on a developer's machine, because the
///         global NuGet cache is holding ~57 <c>Vixen.*</c> packages at 0.1.0 from an earlier
///         <c>Pack</c>. The same restore against nuget.org with an empty cache is
///         <c>NU1101: Unable to find package Vixen.App</c>. So a "does it restore outside the repo"
///         check is green here, green on any runner with a warm cache, and a statement about nothing
///         — which is why the assertion that matters in this target is not that the restore
///         succeeded.
///     </para>
///     <para>
///         ⚠️ <b>The negative control is the assertion that matters.</b> Before any template is built,
///         one scaffolded project is restored with the local feed <i>absent</i>, and that restore is
///         required to <b>fail</b>. If it succeeds, the packages came from somewhere this target did
///         not put them — a warm cache, a machine-wide <c>nuget.config</c>, a fallback folder — and
///         every green result after it would be measuring that instead. Ask what this target prints
///         on the day the feed is not wired up, and the answer has to be a failure rather than six
///         builds that quietly used last month's packages.
///     </para>
///     <para>
///         Everything happens under the system temporary directory rather than under
///         <see cref="NukeBuild.TemporaryDirectory" />, which is inside the repository. A project
///         scaffolded there would inherit <c>Directory.Build.props</c>, <c>Directory.Packages.props</c>
///         and the repository's own <c>nuget.config</c>, and the question being asked is what a
///         stranger's machine does.
///     </para>
///     <para>
///         ⚠️ <b>What this does not prove, and what is still owed on #114.</b> The six targets it
///         builds are the six <i>templates</i>. It does not publish for Android, iOS or Web — a
///         desktop machine cannot, without those workloads — so the per-platform head question
///         (overview § 1.15, and the <c>vixen-game</c> row's ⛔) is untouched by it. And this file
///         has <b>never been executed</b>: reaching it needs a full <c>Pack</c>, which the session
///         that wrote it was not permitted to run. The first run is the review.
///     </para>
/// </remarks>
partial class Build {
    /// <summary>How many <c>Vixen.*</c> packages a feed has to hold before it is worth believing.</summary>
    /// <remarks>
    ///     A floor rather than a list. The point is to refuse an empty or half-written
    ///     <c>artifacts/packages</c>, which is what a <c>Pack</c> that failed halfway leaves behind
    ///     and which would otherwise reach the negative control and fail there, blaming the cache.
    ///     ~57 packages were counted on the run this number was written against.
    /// </remarks>
    const int FeedFloor = 20;

    /// <summary>Where the scaffolding happens: outside the repository, on purpose.</summary>
    AbsolutePath TemplateScaffoldDirectory =>
        (AbsolutePath)Path.GetTempPath() / "vixen-template-check";

    Target CheckTemplates => definition => definition
        .Description("Scaffolds every dotnet new template from the packed feed with an empty package cache and builds it")
        .DependsOn(Pack)
        .Executes(() => {
            var feed = PackagesDirectory;

            var packages = feed.GlobFiles("*.nupkg")
                .Where(file => !file.Name.EndsWith(".symbols.nupkg", StringComparison.Ordinal))
                .ToList();

            Assert.True(
                packages.Count >= FeedFloor,
                $"{feed} holds {packages.Count} package(s), fewer than the {FeedFloor} a whole Pack "
                + "produces. A feed this thin cannot restore a scaffolded project, and the failure "
                + "would arrive later wearing a cache's clothes."
            );

            var templatePackage = packages.FirstOrDefault(file =>
                file.Name.StartsWith("Vixen.Templates.", StringComparison.Ordinal)
            );

            Assert.True(
                templatePackage is not null,
                $"there is no Vixen.Templates package in {feed}, so there is nothing to install and "
                + "nothing to scaffold from."
            );

            var root = TemplateScaffoldDirectory;
            root.CreateOrCleanDirectory();

            var cache = root / "packages";
            var hive = root / "hive";
            cache.CreateDirectory();

            var templates = TemplateIds();

            Assert.True(
                templates.Count > 0,
                "no template ids were found under Tools/Vixen.Templates/templates, so this target "
                + "would install a package and instantiate nothing while reporting success."
            );

            AssertTheCacheIsCold(root, cache, templates[0]);

            WriteTemplateFeedConfiguration(root, feed);
            InstallTemplatePackage(templatePackage!, hive);

            foreach (var id in templates) {
                BuildScaffolded(root, cache, hive, id);
            }

            Log.Information(
                "{Count} template(s) scaffolded from {Feed} and built with an empty package cache",
                templates.Count,
                feed
            );
        });

    /// <summary>The template ids, read off the tree rather than listed here.</summary>
    /// <returns>Each template's directory name, which is its short name.</returns>
    /// <remarks>
    ///     A seventh template is then covered by existing, rather than by somebody remembering this
    ///     file — the same reason <c>Vixen.Templates.Tests</c> enumerates rather than lists.
    /// </remarks>
    List<string> TemplateIds() =>
        (RootDirectory / "Tools" / "Vixen.Templates" / "templates").GlobDirectories("*")
        .Select(directory => directory.Name)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    ///     Requires that a scaffolded project cannot restore before the feed is wired up.
    /// </summary>
    /// <param name="root">The scaffolding root.</param>
    /// <param name="cache">The empty package cache.</param>
    /// <param name="id">A template id, used only for the shape of the project written here.</param>
    /// <remarks>
    ///     ⚠️ This is the instrument, and it is the whole point of the target. The project written
    ///     here does not come from the template engine — it is two lines naming <c>Vixen.App</c> at
    ///     the version this repository produces — because the question is about the <i>cache and the
    ///     sources</i> and not about the template. Measured on 2026-09-05 with an empty
    ///     <c>--packages</c> directory against nuget.org: <c>error NU1101: Unable to find package
    ///     Vixen.App</c>. A success here means the packages are reachable from somewhere this target
    ///     did not arrange, and every later green is that instead of a template.
    /// </remarks>
    void AssertTheCacheIsCold(AbsolutePath root, AbsolutePath cache, string id) {
        var probe = root / "cold-cache-probe";
        probe.CreateOrCleanDirectory();

        (probe / "nuget.config").WriteAllText(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """
        );

        (probe / "Probe.csproj").WriteAllText(
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
                 <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                 <ItemGroup><PackageReference Include="Vixen.App" Version="{VersionOfThisRepository()}" /></ItemGroup>
             </Project>
             """
        );

        var restore = ProcessTasks.StartProcess(
            "dotnet",
            $"restore \"{probe / "Probe.csproj"}\" --packages \"{cache}\"",
            probe,
            logOutput: false
        );

        restore.WaitForExit();

        Assert.True(
            restore.ExitCode != 0,
            $"a project naming Vixen.App restored successfully with no Vixen feed configured and "
            + $"'{cache}' as its package cache, while checking {id} and its five siblings. The "
            + "packages are therefore coming from somewhere this target did not put them — a warm "
            + "global cache, a machine-wide nuget.config, a fallback folder — and this run would "
            + "have proved nothing about a clean machine, which is the only thing #114 asks about."
        );

        Log.Information("The package cache is cold: a Vixen reference does not resolve without the feed");
    }

    /// <summary>Points the scaffolding root at the packed feed, and at it alone for Vixen packages.</summary>
    /// <param name="root">The scaffolding root.</param>
    /// <param name="feed">The directory <see cref="Pack" /> wrote to.</param>
    /// <remarks>
    ///     nuget.org stays a source because a scaffolded project's transitive closure is full of
    ///     third-party packages this repository does not produce. The source mapping is what keeps
    ///     that from weakening the claim: <c>Vixen.*</c> may only come from the local feed, so a
    ///     package this build failed to produce cannot be quietly supplied by a published one.
    /// </remarks>
    static void WriteTemplateFeedConfiguration(AbsolutePath root, AbsolutePath feed) =>
        (root / "nuget.config").WriteAllText(
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <configuration>
               <packageSources>
                 <clear />
                 <add key="vixen-local" value="{feed}" />
                 <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
               </packageSources>
               <packageSourceMapping>
                 <packageSource key="vixen-local">
                   <package pattern="Vixen.*" />
                 </packageSource>
                 <packageSource key="nuget.org">
                   <package pattern="*" />
                 </packageSource>
               </packageSourceMapping>
             </configuration>
             """
        );

    /// <summary>Installs the packed templates into a private hive.</summary>
    /// <param name="package">The Vixen.Templates package.</param>
    /// <param name="hive">Where the template engine keeps its state for this run.</param>
    /// <remarks>
    ///     <c>--debug:custom-hive</c> for the reason <c>Vixen.Templates.Tests</c> gives: a target
    ///     that installed into the developer's own template list would leave it there, and would
    ///     read whatever was already in it.
    /// </remarks>
    static void InstallTemplatePackage(AbsolutePath package, AbsolutePath hive) {
        var install = ProcessTasks.StartProcess(
            "dotnet",
            $"new install \"{package}\" --debug:custom-hive \"{hive}\""
        );

        install.WaitForExit();

        Assert.True(
            install.ExitCode == 0,
            $"installing {package.Name} into a private hive exited {install.ExitCode}. Without "
            + "NoDefaultExcludes this package contains no templates at all — every template is "
            + "identified by a .template.config/ directory and NuGet drops anything beginning with "
            + "a dot — so an install that reports no templates is that, and not this target."
        );
    }

    /// <summary>Scaffolds one template outside the repository and builds it against the local feed.</summary>
    /// <param name="root">The scaffolding root.</param>
    /// <param name="cache">The package cache, empty when this target started.</param>
    /// <param name="hive">The private template hive.</param>
    /// <param name="id">The template's short name.</param>
    /// <remarks>
    ///     A build rather than a restore, because a restore that resolves every package can still
    ///     produce a project that does not compile — the SDK's import order, a missing item type, a
    ///     `.vxml` that nothing knows how to read. Overview § 1.15 records exactly that class of
    ///     failure for the per-platform heads.
    /// </remarks>
    void BuildScaffolded(AbsolutePath root, AbsolutePath cache, AbsolutePath hive, string id) {
        var where = root / id;
        where.CreateOrCleanDirectory();

        var create = ProcessTasks.StartProcess(
            "dotnet",
            $"new {id} -n Kestrel -o \"{where}\" --debug:custom-hive \"{hive}\"",
            root
        );

        create.WaitForExit();

        Assert.True(create.ExitCode == 0, $"`dotnet new {id}` exited {create.ExitCode}");

        var build = ProcessTasks.StartProcess(
            "dotnet",
            $"build \"{where}\" --packages \"{cache}\" --configuration Release",
            where
        );

        build.WaitForExit();

        Assert.True(
            build.ExitCode == 0,
            $"the project `dotnet new {id}` produced does not build against the packages this "
            + $"repository just packed (exit {build.ExitCode}). This is the six-target question in "
            + "#114 and the first thing to read is whether the failure names a Vixen package — a "
            + "pin the repository does not produce — or the compilation of the scaffolded source."
        );

        Log.Information("{Template} scaffolded and built", id);
    }

    /// <summary>The version the templates pin, which is the version <see cref="Pack" /> writes.</summary>
    /// <returns>The version prefix from Directory.Build.props.</returns>
    /// <remarks>
    ///     ⚠️ Read from the file rather than hard-coded, and deliberately not from
    ///     <c>GitVersion</c>: the substitution the template package performs is
    ///     <c>$(Version)</c> at pack time, so the number the probe has to name is the same one the
    ///     scaffolded projects will carry. A literal here would go stale the first time the
    ///     repository's version moved, and the negative control would then pass for the wrong
    ///     reason — it would be looking for a package nobody ever produced.
    /// </remarks>
    string VersionOfThisRepository() {
        var properties = (RootDirectory / "Directory.Build.props").ReadAllText();
        var open = properties.IndexOf("<VersionPrefix>", StringComparison.Ordinal);

        Assert.True(open >= 0, "Directory.Build.props has no <VersionPrefix>, so the feed's version is unknown");

        var start = open + "<VersionPrefix>".Length;

        return properties[start..properties.IndexOf("</VersionPrefix>", start, StringComparison.Ordinal)].Trim();
    }
}
