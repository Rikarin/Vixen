// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using System.Text;
using Vixen.Cli;
using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Templates.Tests;

/// <summary>The packed template package, installed once into a hive of its own.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A class fixture, and the reason is arithmetic rather than tidiness.</b> xUnit builds
///         a fresh instance of a test class for every test in it, so the same work held in an
///         instance field packed and installed the package thirteen times — two minutes eleven
///         against ten seconds for the run that shares one. A fixture is constructed once and
///         disposed after the last case.
///     </para>
///     <para>
///         <c>--debug:custom-hive</c> is what keeps this out of the developer's own template list. A
///         test that installed into the real one would change the machine it ran on, and its second
///         run would be reading the first run's leftovers.
///     </para>
/// </remarks>
public sealed class TemplateHive : IDisposable {
    static readonly string TemplateProject = Metadata("VixenTemplateProject");

    readonly Lazy<(string Hive, string Output)> installed;

    /// <summary>Where everything this fixture writes goes.</summary>
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "vixen-template-install", Guid.NewGuid().ToString("N"));

    /// <summary>The hive the templates are installed into.</summary>
    public string Hive => installed.Value.Hive;

    /// <summary>What <c>dotnet new install</c> said, which is its listing of what it found.</summary>
    public string Listing => installed.Value.Output;

    /// <summary>Prepares the fixture. Nothing is packed until a test asks.</summary>
    public TemplateHive() => installed = new(Install);

    /// <inheritdoc />
    public void Dispose() {
        try {
            if (Directory.Exists(Root)) {
                Directory.Delete(Root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    /// <summary>Runs `dotnet` and returns what it said.</summary>
    /// <param name="arguments">The arguments.</param>
    /// <returns>Whether it succeeded, and its whole output.</returns>
    public static (bool Succeeded, string Output) Run(params string[] arguments) {
        var process = new Process {
            StartInfo = new("dotnet") {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var argument in arguments) {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        process.OutputDataReceived += (_, line) => output.AppendLine(line.Data);
        process.ErrorDataReceived += (_, line) => output.AppendLine(line.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        return (process.ExitCode == 0, output.ToString());
    }

    /// <summary>Packs the template package and installs it.</summary>
    /// <remarks>
    ///     ⚠ Packed at <see cref="ProjectScaffold.SdkVersion" /> rather than at whatever this build
    ///     is versioned as, so that the byte comparison in
    ///     <see cref="TemplateInstallationTests.TheRealEngineWritesWhatTheScaffolderWrites" /> is
    ///     comparing two things that were told the same version. The two substitutions are the whole
    ///     point of the pack; making them disagree for a reason that is not the template's would turn
    ///     every one of those assertions into a diff about a version number.
    /// </remarks>
    (string Hive, string Output) Install() {
        var packages = Path.Combine(Root, "package");
        var hive = Path.Combine(Root, "hive");

        Directory.CreateDirectory(hive);

        var packed = Run(
            "pack",
            TemplateProject,
            "-c",
            "Debug",
            "--nologo",
            "-o",
            packages,
            "-p:Version=" + ProjectScaffold.SdkVersion,

            // Its own obj/ and bin/, so a pack driven from a test cannot collide with the build that
            // produced the assembly this test is running from.
            "-p:BaseIntermediateOutputPath=" + Path.Combine(Root, "obj") + Path.DirectorySeparatorChar,
            "-p:BaseOutputPath=" + Path.Combine(Root, "bin") + Path.DirectorySeparatorChar
        );

        Assert.True(packed.Succeeded, packed.Output);

        var package = Assert.Single(Directory.GetFiles(packages, "Vixen.Templates.*.nupkg"));

        // ⚠ An absolute path. `dotnet new install` resolves a relative one against the hive rather
        // than against the working directory, and answers with "is not supported, or doesn't exist"
        // about a file that is plainly there.
        var install = Run("new", "install", Path.GetFullPath(package), "--debug:custom-hive", hive);

        Assert.True(install.Succeeded, install.Output);

        return (hive, install.Output);
    }

    static string Metadata(string key) =>
        typeof(TemplateHive).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == key)
            .Value!;
}

/// <summary>
///     The templates as somebody who has never opened this repository gets them: packed, installed
///     into the real <c>dotnet new</c> engine, and instantiated into a directory outside the tree.
/// </summary>
/// <remarks>
///     <para>
///         <b>Everything else in this project reads the templates through
///         <see cref="TemplateCatalog" />, which is <i>ours</i>.</b> It parses <c>template.json</c>
///         with its own reader and applies its own substitution, so a pack the real template engine
///         cannot even open passes every one of those tests. That is not hypothetical:
///         <c>Vixen.Templates.csproj</c> carries <c>NoDefaultExcludes</c> because NuGet drops
///         anything beginning with a dot, and without it the package "builds, installs, and contains
///         no templates at all" — a sentence in a comment, guarded by nothing.
///     </para>
///     <para>
///         ⚠ <b>What this cannot do, and the reason issue #114 stays open: none of the six targets is
///         exercised here.</b> Every one of them needs a restore and a restore needs a feed with the
///         engine packages on it. Doc 14 § Phase 11's clean-machine criterion is a feed problem, not
///         a template problem, and no test on a developer's machine can stand in for it.
///     </para>
///     <para>
///         ⚠ <b>And the obvious way to fake it passes.</b> Restoring a scaffolded project from a
///         temporary directory outside the repository succeeds on this machine — not because the
///         repository is on disk, but because ~57 <c>Vixen.*</c> packages at 0.1.0 are sitting in the
///         global NuGet cache from an earlier <c>Pack</c>. The same restore with
///         <c>--packages &lt;empty&gt; --source nuget.org</c> is
///         <c>NU1101: Unable to find package Vixen.App</c>. A "does it restore outside the repo" test
///         would therefore be green here, green on any CI runner with a warm cache, and a statement
///         about nothing — which is why there is no such test in this file, and why
///         <see cref="EveryPackageATemplatePinsIsOneThisRepositoryProduces" /> asserts the necessary
///         condition instead.
///     </para>
/// </remarks>
/// <param name="hive">The installed package.</param>
public sealed class TemplateInstallationTests(TemplateHive hive) : IClassFixture<TemplateHive> {
    static readonly string RepositoryRoot = Metadata("VixenRepositoryRoot");

    /// <summary>Read once: the walk is the whole tree, and every case below asks it the same thing.</summary>
    static readonly Lazy<HashSet<string>> PackageIds = new(Packable);

    /// <summary>The real engine finds every template in the packed package.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this guards is silent and total.</b> NuGet's default exclusions drop
    ///     anything whose name begins with a dot, and every template here is identified by a
    ///     <c>.template.config/</c> directory — so a package built without <c>NoDefaultExcludes</c>
    ///     installs successfully and offers nothing. Nothing else in this project would notice,
    ///     because nothing else in it reads the package.
    /// </remarks>
    [Fact]
    public void TheRealEngineFindsEveryTemplateInThePackedPackage() {
        foreach (var template in TemplateCatalog.All) {
            Assert.Contains(template.Id, hive.Listing, StringComparison.Ordinal);
        }

        // And that there were some to find, so that a listing which named nothing at all could not
        // pass an empty loop — which is what a catalog read from an empty assembly would give.
        Assert.NotEmpty(TemplateCatalog.All);
    }

    /// <summary>
    ///     ⚠ <b><c>dotnet new vixen-game</c> and <c>vixen new game</c> write the same directory,
    ///     asserted against the real engine rather than against ourselves.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The package README has claimed this since the pack existed, and what stood behind the
    ///         claim was that both paths read the same embedded files — which makes the two agree by
    ///         construction about <i>content</i> and says nothing at all about <i>substitution</i>.
    ///         The template engine substitutes derived forms of <c>sourceName</c> — a lower-cased
    ///         <c>vixengame1</c> becomes <c>asteroids</c> — and <see cref="TemplateCatalog" />
    ///         deliberately implements none of them. What keeps the two equal is the rule that no
    ///         template mentions its source name in any other casing, which is asserted one file
    ///         over, on our side of the fence. This is the other side.
    ///     </para>
    ///     <para>
    ///         Byte for byte, and into a directory outside the repository, because that is where the
    ///         person this is about will be standing.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(TemplateTests.Templates), MemberType = typeof(TemplateTests))]
    public void TheRealEngineWritesWhatTheScaffolderWrites(string id) {
        var template = TemplateCatalog.All.Single(candidate => candidate.Id == id);
        var where = Path.Combine(hive.Root, "out", id);

        var created = TemplateHive.Run("new", id, "-n", "Kestrel", "-o", where, "--debug:custom-hive", hive.Hive);

        Assert.True(created.Succeeded, created.Output);

        var written = Directory
            .EnumerateFiles(where, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(where, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var scaffolded = template.Instantiate("Kestrel", ScaffoldRunner.SdkVersion);

        Assert.Equal(scaffolded.Select(file => file.Path).Order(StringComparer.Ordinal).ToArray(), written);

        foreach (var file in scaffolded) {
            Assert.Equal(file.Content, File.ReadAllBytes(Path.Combine(where, file.Path)));
        }
    }

    /// <summary>
    ///     Every package a template pins is one this repository produces, at the version the scaffold
    ///     writes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The necessary condition for the clean-machine restore that <i>can</i> be checked
    ///         without a feed.</b> A template naming a package nobody publishes fails at the one
    ///         moment its author has no context to debug it — the judgement <c>vixen-plugin</c>
    ///         waited a whole phase on, and the reason three of doc 27's eight projects are still not
    ///         scaffolded. A restore proves the positive; this rules out the negative.
    ///     </para>
    ///     <para>
    ///         ⚠ The version is checked too, and it is the half that would rot quietly. The templates
    ///         carry a token rather than a literal precisely so that it cannot go stale — so a
    ///         reference that had somehow acquired one would produce projects asking for a version
    ///         nobody publishes, and the pack-time substitution would sail straight past it.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(TemplateTests.Templates), MemberType = typeof(TemplateTests))]
    public void EveryPackageATemplatePinsIsOneThisRepositoryProduces(string id) {
        var template = TemplateCatalog.All.Single(candidate => candidate.Id == id);
        var pinned = 0;

        foreach (var file in template.Instantiate("Kestrel", ScaffoldRunner.SdkVersion)) {
            if (!file.Path.EndsWith(".csproj", StringComparison.Ordinal)) {
                continue;
            }

            foreach (var (package, version) in References(Encoding.UTF8.GetString(file.Content))) {
                Assert.True(
                    PackageIds.Value.Contains(package),
                    $"{id}/{file.Path} pins {package}, which no packable project in this repository "
                    + "produces — so a scaffolded project cannot restore however good the feed is."
                );

                Assert.Equal(ScaffoldRunner.SdkVersion, version);
                pinned++;
            }
        }

        // ⚠ And that something was read. A reader that found no references at all would pass every
        // assertion above by never reaching one, which is the shape this repository's own notes call
        // a predicate that cannot be false.
        Assert.True(pinned > 0, $"No package reference was read out of {id}, which cannot be right.");
    }

    /// <summary>The package ids this repository can publish.</summary>
    /// <remarks>
    ///     Read off the project files rather than the solution, because <c>PackageId</c> defaults to
    ///     the assembly name and the assembly name to the file name — so a project's own file is the
    ///     only place all three reconcile. <c>IsPackable=false</c> is what a test project and a sample
    ///     say about themselves, and that is exactly the distinction that matters here: a template
    ///     may not pin something that never becomes a package.
    /// </remarks>
    static HashSet<string> Packable() {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)) {
            // Somebody's bin/ or obj/ holding a copy, and — more to the point — the templates' own
            // project files, which are a third party's and are not this repository's to publish.
            if (Segment(project, "obj") || Segment(project, "bin") || Segment(project, "templates")) {
                continue;
            }

            var text = File.ReadAllText(project);

            if (text.Contains("<IsPackable>false</IsPackable>", StringComparison.Ordinal)) {
                continue;
            }

            ids.Add(Between(text, "<PackageId>", "</PackageId>") ?? Path.GetFileNameWithoutExtension(project));
        }

        return ids;
    }

    static bool Segment(string path, string name) =>
        path.Contains(
            $"{Path.DirectorySeparatorChar}{name}{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal
        );

    /// <summary>The packages a project file pins, as ids and versions.</summary>
    /// <remarks>
    ///     ⚠ The <c>Sdk</c> attribute is a package reference in everything but spelling:
    ///     <c>Vixen.Sdk/0.1.0</c> is resolved by NuGet like any other, and a project naming one
    ///     nobody publishes fails the same way and earlier — before restore, in the SDK resolver.
    /// </remarks>
    static IEnumerable<(string Package, string Version)> References(string project) {
        if (Between(project, "<Project Sdk=\"", "\"") is { } sdk && sdk.Contains('/', StringComparison.Ordinal)) {
            var slash = sdk.IndexOf('/', StringComparison.Ordinal);

            yield return (sdk[..slash], sdk[(slash + 1)..]);
        }

        var at = 0;

        while (project.IndexOf("<PackageReference", at, StringComparison.Ordinal) is var start and >= 0) {
            var end = project.IndexOf("/>", start, StringComparison.Ordinal);

            if (end < 0) {
                yield break;
            }

            var element = project[start..end];

            if (Between(element, "Include=\"", "\"") is { } package
                && Between(element, "Version=\"", "\"") is { } version) {
                yield return (package, version);
            }

            at = end;
        }
    }

    static string? Between(string text, string opening, string closing) {
        var start = text.IndexOf(opening, StringComparison.Ordinal);

        if (start < 0) {
            return null;
        }

        start += opening.Length;

        var end = text.IndexOf(closing, start, StringComparison.Ordinal);

        return end < 0 ? null : text[start..end];
    }

    static string Metadata(string key) =>
        typeof(TemplateInstallationTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == key)
            .Value!;
}
