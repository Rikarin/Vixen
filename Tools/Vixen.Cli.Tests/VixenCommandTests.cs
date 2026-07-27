// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text;
using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Content;
using Xunit;

namespace Vixen.Cli.Tests;

/// <summary>
///     The command line, driven the way a person drives it and read the way a build script reads it.
///     Everything here goes through the real parser and the real project on a real disk, because the
///     failures this tool exists to prevent — an asset that was never imported, a bundle that is not
///     where the catalog says — are all failures of the parts meeting.
/// </summary>
public sealed class VixenCommandTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-cli-tests", Guid.NewGuid().ToString("N"));

    public VixenCommandTests() => Directory.CreateDirectory(Path.Combine(root, "Assets"));

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
    public async Task ImportingWritesTheIndexAndTheCache() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");

        var (code, output) = await Run("import");

        Assert.Equal(ExitCode.Success, code);
        Assert.True(File.Exists(Path.Combine(root, "Library", "GuidIndex")));
        Assert.True(File.Exists(Path.Combine(root, "Library", "ImportCache")));
        Assert.Contains("Imported 2", output, StringComparison.Ordinal);
    }

    /// <summary>A second run does nothing, which is the whole point of the cache being on disk.</summary>
    [Fact]
    public async Task ASecondImportIsAllCache() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");

        await Run("import");
        var (code, output) = await Run("import");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("Imported 0, 2 unchanged", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContentBuildWritesBundlesACatalogAndItsHash() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");

        var (code, _) = await Run("content", "build");
        var directory = Build();

        Assert.Equal(ExitCode.Success, code);

        var catalog = File.ReadAllBytes(Path.Combine(directory, "catalog.bin"));
        Assert.True(CatalogFormat.Read(catalog).Contains("ui/hero"));
        Assert.Single(Directory.GetFiles(directory, "*.bundle"));

        // The hash file a CDN will not synthesise and ContentUpdate reads before the catalog.
        Assert.Equal(
            ContentHash.Compute(catalog).ToString(),
            File.ReadAllText(Path.Combine(directory, "catalog.bin.hash"))
        );
    }

    /// <summary>
    ///     <b>Phase 3's determinism gate, at the level a person runs it.</b> Two builds of the same
    ///     content are byte-identical, which is what makes a content update able to ship a diff and
    ///     what stops CI failing on a difference nobody can reproduce.
    /// </summary>
    [Fact]
    public async Task TwoBuildsOfTheSameContentAreByteIdentical() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Asset("villain.txt", "villain", address: "ui/villain", group: "UiCore", labels: ["hd"]);
        Group("UiCore");

        var first = Path.Combine(root, "out-1");
        var second = Path.Combine(root, "out-2");

        Assert.Equal(ExitCode.Success, (await Run("content", "build", "--output", first)).Code);
        Assert.Equal(ExitCode.Success, (await Run("content", "build", "--output", second)).Code);

        var left = Files(first);
        var right = Files(second);

        // The catalog, its hash and one bundle. Asserted so that two empty directories cannot agree
        // with each other and be read as a build that reproduced.
        Assert.Equal(3, left.Count);
        Assert.Equal(left.Keys.Order(StringComparer.Ordinal), right.Keys.Order(StringComparer.Ordinal));

        foreach (var (name, bytes) in left) {
            Assert.True(bytes.SequenceEqual(right[name]), $"'{name}' differs between two builds of one project.");
        }
    }

    /// <summary>
    ///     <b>What makes the determinism gate hold across three operating systems, tested on one.</b>
    ///     Two projects at different paths, whose assets were created in a different order and carry
    ///     different GUIDs, build to the same bytes. Everything that would break a cross-machine build
    ///     — an absolute path reaching the catalog, a directory enumeration order leaking into it, an
    ///     authoring identity being shipped — fails this without a second operating system to run on.
    /// </summary>
    /// <remarks>
    ///     It also asserts doc 08's own sentence, which nothing had checked: "the GUID is the
    ///     authoring identity and never appears in a shipped build".
    /// </remarks>
    [Fact]
    public async Task TwoProjectsWithTheSameContentAtDifferentPathsBuildToTheSameBytes() {
        // Created in opposite orders, under differently-named roots, with fresh GUIDs each.
        var one = await BuildElsewhere("project-alpha", ["hero", "villain", "sidekick"]);
        var other = await BuildElsewhere("a-differently-named-project", ["sidekick", "villain", "hero"]);

        Assert.Equal(3, one.Count);
        Assert.Equal(one.Keys.Order(StringComparer.Ordinal), other.Keys.Order(StringComparer.Ordinal));

        foreach (var (name, bytes) in one) {
            Assert.True(bytes.SequenceEqual(other[name]), $"'{name}' differs between two builds of the same content.");
        }
    }

    /// <summary>
    ///     A bundle's file name carries its content hash, so changed content writes a new name. The
    ///     old file is removed rather than left, because a directory that accumulates every bundle
    ///     ever built is one somebody eventually uploads.
    /// </summary>
    [Fact]
    public async Task ABundleFromAnEarlierBuildIsNotLeftBehind() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");

        await Run("content", "build");
        var directory = Build();
        var before = Directory.GetFiles(directory, "*.bundle").Single();

        File.WriteAllText(Path.Combine(root, "Assets", "hero.txt"), "a different hero");
        await Run("content", "build");

        var after = Directory.GetFiles(directory, "*.bundle").Single();

        Assert.NotEqual(Path.GetFileName(before), Path.GetFileName(after));
        Assert.False(File.Exists(before));
    }

    /// <summary>Something a person put in the build directory is not this tool's to delete.</summary>
    [Fact]
    public async Task AnythingElseInTheBuildDirectoryIsLeftAlone() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");

        var directory = Build();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "publish.sh"), "rsync ...");

        await Run("content", "build");

        Assert.True(File.Exists(Path.Combine(directory, "publish.sh")));
    }

    [Fact]
    public async Task AnAddressInAGroupNothingDefinesFailsTheBuild() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "Missing");

        var (code, output) = await Run("content", "build");

        Assert.Equal(ExitCode.Failed, code);
        Assert.Contains("no .vxgroup", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Build(), "catalog.bin")));
    }

    /// <summary>
    ///     A project with nothing addressable is not a broken one. It builds, with an empty catalog,
    ///     and says so rather than reporting a success that looks like it packed something.
    /// </summary>
    [Fact]
    public async Task AProjectWithNoAddressesBuildsAndSaysItIsEmpty() {
        Asset("notes.txt", "just a file");

        var (code, output) = await Run("content", "build");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("has an address", output, StringComparison.Ordinal);
        Assert.Equal(0, CatalogFormat.Read(File.ReadAllBytes(Path.Combine(Build(), "catalog.bin"))).Count);
    }

    /// <summary>
    ///     <b>What makes an asset problem an entry in the IDE's error list.</b> MSBuild picks
    ///     <c>file: error CODE: text</c> out of a tool's output and nothing else, so the file has to
    ///     be absolute — a relative one is resolved against whatever directory the build is running
    ///     in, which is not the project's — and the code has to be there or the line is prose.
    /// </summary>
    [Fact]
    public async Task TheMsbuildFormatCarriesAnAbsolutePathAndACode() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "Missing");

        var (code, output) = await Run("content", "build", "--format", "msbuild");

        Assert.Equal(ExitCode.Failed, code);
        Assert.Contains($"error {DiagnosticCode.Plan}:", output, StringComparison.Ordinal);

        // No "  error  " column, which is the human form and which MSBuild reads as prose.
        Assert.DoesNotContain("  error  ", output, StringComparison.Ordinal);
    }

    /// <summary>An asset's own diagnostics carry the asset, absolute, so the IDE can open it.</summary>
    [Fact]
    public async Task AnAssetDiagnosticNamesTheAssetByItsFullPath() {
        File.WriteAllText(Path.Combine(root, "Assets", "loose.txt"), "no sidecar");

        var (_, output) = await Run("import", "--format", "msbuild");

        var expected = Path.Combine(root, "Assets", "loose.txt");
        Assert.Contains(expected, output, StringComparison.Ordinal);
        Assert.Contains(Path.DirectorySeparatorChar, expected);
    }

    /// <summary>
    ///     A tool that cannot find a project has to say so with a code too. Without one, MSBuild
    ///     reports "exited with code 2" and nothing else, which is the least actionable failure a
    ///     build can have.
    /// </summary>
    [Fact]
    public async Task TheToolsOwnFailureCarriesACodeInTheMsbuildFormat() {
        var elsewhere = Path.Combine(root, "not-a-project");
        Directory.CreateDirectory(elsewhere);

        var (code, _, error) = await RunFull("import", "--project", elsewhere, "--format", "msbuild");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.StartsWith($"error {DiagnosticCode.Usage}:", error, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Information is not an error-list entry, and dressing it as one would put "this project has
    ///     no addressable assets" in a CI failure summary.
    /// </summary>
    [Fact]
    public async Task AnInformationLineIsNotDressedAsADiagnostic() {
        Asset("notes.txt", "just a file");

        var (code, output) = await Run("content", "build", "--format", "msbuild");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("has an address", output, StringComparison.Ordinal);
        Assert.DoesNotContain("error", output, StringComparison.Ordinal);
        Assert.DoesNotContain("warning", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>--no-import</c> exists for one caller — the SDK, which has provably just imported in
    ///     the same build — and it does what it says rather than importing anyway.
    /// </summary>
    [Fact]
    public async Task NoImportSkipsTheImportAndStillBuilds() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");

        await Run("import");
        var (code, output) = await Run("content", "build", "--no-import");

        Assert.Equal(ExitCode.Success, code);
        Assert.DoesNotContain("Imported", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Build(), "catalog.bin")));
    }

    [Fact]
    public async Task DoctorOnAnImportedProjectFindsNothingBroken() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");
        await Run("content", "build");

        var (code, output) = await Run("doctor");

        Assert.Equal(ExitCode.Success, code);
        Assert.DoesNotContain("broken", output, StringComparison.Ordinal);
        Assert.Contains("UiCore", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The check that pays for the command: an asset that is addressable and has never been
    ///     imported produces a build with no chunk for it, and that is discovered on a device.
    /// </summary>
    [Fact]
    public async Task DoctorFindsAnAddressableAssetThatWasNeverImported() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");
        await Run("import");

        Asset("villain.txt", "villain", address: "ui/villain", group: "UiCore");

        var (code, output) = await Run("doctor");

        Assert.Equal(ExitCode.Failed, code);
        Assert.Contains("never been imported", output, StringComparison.Ordinal);
        Assert.Contains("villain.txt", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <b>The doctor repairs nothing.</b> A person asking what is wrong wants the answer, not a
    ///     working tree with edits in it — and a build server asking the same question wants it more.
    ///     Import is the one that creates a missing sidecar.
    /// </summary>
    [Fact]
    public async Task DoctorLeavesAMissingSidecarMissingAndImportCreatesIt() {
        File.WriteAllText(Path.Combine(root, "Assets", "loose.txt"), "no sidecar");
        var meta = Path.Combine(root, "Assets", "loose.txt.meta");

        await Run("doctor");
        Assert.False(File.Exists(meta));

        await Run("import");
        Assert.True(File.Exists(meta));
    }

    [Fact]
    public async Task DoctorNamesABundleTheCatalogClaimsAndTheDirectoryDoesNotHave() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");
        await Run("content", "build");

        File.Delete(Directory.GetFiles(Build(), "*.bundle").Single());

        var (code, output) = await Run("doctor");

        Assert.Equal(ExitCode.Failed, code);
        Assert.Contains("names bundle", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADirectoryThatIsNotAProjectIsAUsageError() {
        var elsewhere = Path.Combine(root, "not-a-project");
        Directory.CreateDirectory(elsewhere);

        var (code, output, error) = await RunFull("doctor", "--project", elsewhere);

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("no Assets/ directory", error, StringComparison.Ordinal);
        Assert.Equal("", output);
    }

    [Fact]
    public async Task ADirectoryThatIsNotThereIsAUsageError() {
        var (code, _, error) = await RunFull("import", "--project", Path.Combine(root, "nowhere"));

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("no directory", error, StringComparison.Ordinal);
    }

    /// <summary>Run it from a subdirectory and it finds the project above, the way git does.</summary>
    [Fact]
    public async Task TheProjectIsFoundByWalkingUpFromTheWorkingDirectory() {
        Asset("UI/hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");

        var was = Environment.CurrentDirectory;

        try {
            Environment.CurrentDirectory = Path.Combine(root, "Assets", "UI");
            var (code, output, _) = await RunFull("import");

            Assert.Equal(ExitCode.Success, code);
            Assert.Contains("Imported", output, StringComparison.Ordinal);
        } finally {
            Environment.CurrentDirectory = was;
        }
    }

    /// <summary>
    ///     Serving refuses before it opens a socket, which is what makes the check testable at all —
    ///     and what stops a phone being pointed at an empty directory and told the content is broken.
    /// </summary>
    [Fact]
    public async Task ServingWithNoBuildSaysSoRatherThanServingNothing() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");

        var (code, _, error) = await RunFull("content", "serve", "--project", root);

        Assert.Equal(ExitCode.Failed, code);
        Assert.Contains("no content build", error, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The three verbs doc 14 lists that are not here are not here, rather than parsing and
    ///     apologising: a build script can only discover the second kind at run time.
    /// </summary>
    [Theory]
    [InlineData("new")]
    [InlineData("run")]
    [InlineData("build")]
    public void TheVerbsThatNeedWhatDoesNotExistYetAreAbsent(string verb) =>
        Assert.NotEmpty(VixenCommand.Create().Parse([verb]).Errors);

    string Build() => Path.Combine(root, "Build", Project.HostTarget.Replace('/', '-'));

    /// <summary>Builds a project of its own, under its own name, and returns what it wrote.</summary>
    async Task<Dictionary<string, byte[]>> BuildElsewhere(string name, string[] assets) {
        var elsewhere = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(elsewhere, "Assets"));

        foreach (var asset in assets) {
            var file = Path.Combine(elsewhere, "Assets", $"{asset}.txt");
            File.WriteAllText(file, $"the {asset}");

            AssetMetaFile.WriteFile(
                AssetMetaFile.PathFor(file),
                new() {
                    Guid = AssetId.New(),
                    Addressable = new() { Address = $"ui/{asset}", Group = "UiCore" }
                }
            );
        }

        File.WriteAllText(
            Path.Combine(elsewhere, "Assets", "UiCore.vxgroup"),
            YamlSerializer.ToYaml(new AddressableGroup { Name = "UiCore" })
        );

        var (code, _, _) = await RunFull("content", "build", "--project", elsewhere);
        Assert.Equal(ExitCode.Success, code);

        return Files(Path.Combine(elsewhere, "Build", Project.HostTarget.Replace('/', '-')));
    }

    /// <summary>Writes an asset and the sidecar that says where it appears in a build.</summary>
    void Asset(string relativePath, string content, string? address = null, string? group = null, string[]? labels = null) {
        var absolute = Path.Combine(root, "Assets", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);

        if (address is null && group is null) {
            return;
        }

        AssetMetaFile.WriteFile(
            AssetMetaFile.PathFor(absolute),
            new() {
                Guid = AssetId.New(),
                Addressable = new() { Address = address, Group = group, Labels = labels ?? [] }
            }
        );
    }

    void Group(string name) =>
        File.WriteAllText(
            Path.Combine(root, "Assets", $"{name}.vxgroup"),
            YamlSerializer.ToYaml(new AddressableGroup { Name = name })
        );

    static Dictionary<string, byte[]> Files(string directory) =>
        Directory.GetFiles(directory)
            .ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes, StringComparer.Ordinal);

    /// <summary>Runs a command against this test's project.</summary>
    async Task<(ExitCode Code, string Output)> Run(params string[] args) {
        var (code, output, _) = await RunFull([.. args, "--project", root]);
        return (code, output);
    }

    /// <summary>Runs a command exactly as written, for the tests that are about finding a project.</summary>
    static async Task<(ExitCode Code, string Output, string Error)> RunFull(params string[] args) {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };

        var parsed = VixenCommand.Create(output, error).Parse(args);

        if (parsed.Errors.Count > 0) {
            return (ExitCode.UsageError, output.ToString(), string.Join("\n", parsed.Errors.Select(e => e.Message)));
        }

        var code = await parsed.InvokeAsync(null, TestContext.Current.CancellationToken);
        return ((ExitCode)code, output.ToString(), error.ToString());
    }
}
