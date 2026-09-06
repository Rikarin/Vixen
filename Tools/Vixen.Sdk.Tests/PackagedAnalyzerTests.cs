// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Vixen.Sdk.Tests;

/// <summary>
///     Every package that carries a Roslyn generator, asserted over the bytes of its <c>.nupkg</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>No build inside this repository can state this property, and that is the whole
///         reason it is a test rather than a note.</b> Every in-repo consumer of every generator
///         names it as a <c>ProjectReference</c> with <c>OutputItemType="Analyzer"</c>, and
///         analyzers are not transitive through one — so each project that wants a generator says
///         so, the generator runs, and every test of every generator is green <i>whatever the
///         package contains</i>. <c>Vixen.Shaders</c> shipped with no generator in it for as long as
///         the generator existed, and nothing in the tree was red.
///     </para>
///     <para>
///         ⚠ <b>Presence is the easy half; the wrong path is the one that hides.</b>
///         <c>TargetsForTfmSpecificContentInPackage</c> contributes to the same item the framework
///         assembly is placed by, so a <c>PackagePath</c> of <c>lib/net10.0</c> produces a package
///         that still packs, still restores, and resolves to nothing — a consumer gets an extra
///         reference assembly and no generator. Both directions are asserted.
///     </para>
///     <para>
///         ⚠ <b>The census is read out of the project files rather than typed here, because the
///         defect was found twice by two people who happened to think about the archive.</b> Two
///         packages had a gate and five did not, and which five depended on nobody. A package that
///         starts carrying a generator tomorrow is covered by this the day it does, with nothing to
///         remember — and <see cref="EveryPackageKnownToShipOneStillDoes" /> is the other half, so
///         a package that quietly stops declaring one cannot leave the census by the same door.
///     </para>
///     <para>
///         Here rather than in seven sibling suites: one assembly packs each package once, where
///         seven would each pay their own restore, and this project already exists to assert over
///         the bytes of a <c>.nupkg</c> this repository produces
///         (<see cref="PackagedToolTests" />). ⚠ <c>Vixen.Shaders.Tests</c> and
///         <c>Vixen.Net.Tests</c> keep their own copies of the two assertions; they were written
///         first, they are the pattern this follows, and deleting them to remove a duplicated pack
///         would trade coverage for time in the direction this issue is about.
///     </para>
/// </remarks>
public sealed class PackagedAnalyzerTests : IDisposable {
    /// <summary>The repository, handed in by the project file rather than searched for.</summary>
    static readonly string Repository = Path.GetFullPath(Metadata("VixenRepositoryRoot"));

    /// <summary>
    ///     The configuration this test assembly was compiled in, which is the one the tree around it
    ///     has bin/ directories for. Written by the SDK from <c>$(Configuration)</c>.
    /// </summary>
    static readonly string Configuration =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
        ?? throw new InvalidOperationException(
            "This assembly carries no AssemblyConfigurationAttribute, so the pack below cannot know which "
            + "bin/ the generator projects were built into."
        );

    /// <summary>
    ///     The packages that carry a generator today — a floor under the census, not the list under
    ///     test.
    /// </summary>
    /// <remarks>
    ///     Discovery alone would let a package leave coverage by deleting the packing target it was
    ///     meant to keep, which is the failure this is guarding, inverted. Adding one here is not
    ///     required: the theory runs over what the tree declares.
    /// </remarks>
    static readonly string[] Known = [
        "Vixen.Core.Reflection",
        "Vixen.Core.Serialization",
        "Vixen.Engine",
        "Vixen.Input",
        "Vixen.Net",
        "Vixen.Shaders",
        "Vixen.Ui"
    ];

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-analyzer-pack", Guid.NewGuid().ToString("N"));

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    /// <summary>Every <c>Core/</c> library whose project file packs a generator, with its generators.</summary>
    public static TheoryData<string> PackagesCarryingAGenerator() {
        var data = new TheoryData<string>();

        foreach (var package in Carriers().Keys.OrderBy(name => name, StringComparer.Ordinal)) {
            data.Add(package);
        }

        return data;
    }

    /// <summary>
    ///     The census still holds every package that had a generator when this was written.
    /// </summary>
    /// <remarks>
    ///     ⚠ The instrument, and it costs nothing. A discovery that matched no project files would
    ///     produce an empty theory, and an empty theory is a green suite: xunit reports zero cases
    ///     run as success, which is precisely the shape of failure this file exists to refuse.
    /// </remarks>
    [Fact]
    public void EveryPackageKnownToShipOneStillDoes() {
        var carriers = Carriers();

        foreach (var package in Known) {
            Assert.True(
                carriers.ContainsKey(package),
                $"{package} no longer declares TargetsForTfmSpecificContentInPackage, so it packs no "
                + "generator and has left this suite's census. Either it stopped shipping one — say "
                + "so by removing it from `Known`, in a commit that says why — or the packing target "
                + "was lost, which is the failure this file is about."
            );
        }

        foreach (var (package, generators) in carriers) {
            Assert.True(
                generators.Count > 0,
                $"{package} names a packing target and no generator project, so the theory below would "
                + "pack it and assert nothing."
            );
        }
    }

    /// <summary>
    ///     The generators are under <c>analyzers/dotnet/cs</c>, and the library is still under
    ///     <c>lib/</c> without them.
    /// </summary>
    /// <param name="package">The package to pack and read.</param>
    [Theory]
    [MemberData(nameof(PackagesCarryingAGenerator))]
    public void TheGeneratorsTravelWhereNuGetLoadsAnalyzersFrom(string package) {
        var generators = Carriers()[package];
        var entries = Pack(package);

        // Not merely "something is under analyzers/": the assembly a consumer's compiler has to
        // load, by name, because a target that packs the wrong project's output packs a file.
        foreach (var generator in generators) {
            Assert.Contains($"analyzers/dotnet/cs/{generator}.dll", entries);

            // The wrong-path case, which packs and restores and resolves to nothing.
            Assert.DoesNotContain($"lib/net10.0/{generator}.dll", entries);
        }

        // And the target that puts the generator in has not displaced the library it belongs to.
        Assert.Contains($"lib/net10.0/{package}.dll", entries);
    }

    /// <summary>
    ///     Every <c>Core/</c> project that packs a generator, mapped to the generator assemblies it
    ///     packs.
    /// </summary>
    /// <remarks>
    ///     Read from the <c>&lt;MSBuild Projects="…"&gt;</c> the packing target invokes, which is
    ///     where the generator projects are actually named. ⚠ Deliberately <i>not</i> from the
    ///     <c>PackagePath</c> beside it: the expected path is the constant NuGet loads analyzers
    ///     from, so a project file that names the wrong one fails rather than moving the goalposts.
    /// </remarks>
    static Dictionary<string, IReadOnlyList<string>> Carriers() {
        var core = Path.Combine(Repository, "Core");

        Assert.True(Directory.Exists(core), $"Core/ is not at {core}, so this suite found no projects to read.");

        var carriers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var project in Directory.GetFiles(core, "*.csproj", SearchOption.AllDirectories)) {
            if (project.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || project.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) {
                continue;
            }

            var document = XDocument.Load(project);

            var targets = document
                .Descendants("TargetsForTfmSpecificContentInPackage")
                .SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(name => !name.StartsWith('$'))
                .ToHashSet(StringComparer.Ordinal);

            if (targets.Count == 0) {
                continue;
            }

            var generators = document
                .Descendants("Target")
                .Where(element => targets.Contains(element.Attribute("Name")?.Value ?? string.Empty))
                .SelectMany(element => element.Descendants("MSBuild"))
                .Select(element => element.Attribute("Projects")?.Value ?? string.Empty)
                .SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))

                // ⚠ Split by hand rather than with Path.GetFileNameWithoutExtension. A project file
                // writes `..\Name\Name.csproj`, and a backslash is an ordinary filename character on
                // Unix — so the framework call returns the whole path there and the right answer on
                // Windows, which is a test that passes on one machine and not the other.
                .Select(value => value.Replace('\\', '/').Split('/')[^1])
                .Select(name => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? name[..^".csproj".Length] : name)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            carriers[Path.GetFileNameWithoutExtension(project)] = generators;
        }

        return carriers;
    }

    /// <summary>Packs one <c>Core/</c> package and returns every entry path the archive holds.</summary>
    /// <param name="package">The project name, which is also the package id.</param>
    /// <returns>The entry paths.</returns>
    HashSet<string> Pack(string package) {
        var project = Path.Combine(Repository, "Core", package, package + ".csproj");

        // Asserted rather than assumed: a path that has gone stale would otherwise make `dotnet
        // pack` fail for a reason that reads nothing like "this test cannot find its project".
        Assert.True(File.Exists(project), $"{package} is not at {project}.");

        var output = Path.Combine(root, package);

        // ⚠ The configuration this assembly was built in, not a hard-coded `Debug`, and the
        // difference is what made master's `Test` leg red on all three operating systems (#943).
        // `PackNetGenerators` and its six siblings ask the generator project for `GetTargetPath`
        // rather than `Build` — deliberately, since `NoBuild` is global and asking for `Build` here
        // is NETSDK1085 — so packing lists a path the pack itself does not produce. A developer
        // machine defaults `Configuration` to Debug and has therefore built that path already; CI
        // builds Release and only Release, so a Debug pack named
        // `Core/Vixen.Net.Generators/bin/Debug/netstandard2.1` and NuGet refused a directory that
        // was never going to exist. Packing what the tree in front of the test was actually built
        // in is true on both.
        var pack = Run("pack", project, "-c", Configuration, "--nologo", "-o", output);

        // ⚠ Only the output directory is redirected, not obj/ and bin/. PackagedToolTests packs with
        // BaseIntermediateOutputPath pointed at its own temporary directory; doing that here is
        // NETSDK1005, because those are *global* properties and MSBuild hands a global property to
        // every project it invokes — so the netstandard2.x generator projects these reference would
        // all look for their assets in one obj/ restored for net10.0.

        // ⚠ A pack that fails inside NuGet.Build.Tasks.Pack with a missing generator output is not a
        // packaging bug: it is a generator this project has not caused to be built. Six of the seven
        // are built by their own package's analyzer reference; the exception is named in the project
        // file beside Vixen.Cli, and an eighth arriving with the same arrangement lands here.
        Assert.True(pack.Succeeded, $"packing {package} failed.{Environment.NewLine}{pack.Output}");

        var archive = Assert.Single(Directory.GetFiles(output, package + ".*.nupkg"));

        using var opened = ZipFile.OpenRead(archive);

        return opened.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
    }

    static (bool Succeeded, string Output) Run(params string[] arguments) {
        var process = new Process {
            StartInfo = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }
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

    static string Metadata(string key) =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == key)
            .Value!;
}
