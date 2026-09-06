// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Content;
using Vixen.Shaders;
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

        // The catalog, its hash, the scene manifest and one bundle. Asserted so that two empty
        // directories cannot agree with each other and be read as a build that reproduced — and so
        // that the manifest is inside the gate rather than beside it: a file the build writes and
        // determinism does not cover is a file that can drift.
        Assert.Equal(4, left.Count);
        Assert.Equal(left.Keys.Order(StringComparer.Ordinal), right.Keys.Order(StringComparer.Ordinal));

        foreach (var (name, bytes) in left) {
            Assert.True(bytes.SequenceEqual(right[name]), $"'{name}' differs between two builds of one project.");
        }
    }

    /// <summary>
    ///     <b>What makes the determinism gate hold across three operating systems, tested on one.</b>
    ///     Two projects at different paths, whose assets were created in a different order, build to
    ///     the same bytes. An absolute path reaching the catalog, or a directory enumeration order
    ///     leaking into it, fails this without a second operating system to run on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The assets share their GUIDs, and that is the assertion changing rather than the
    ///         assertion weakening.</b> This used to give each project fresh ones and call the match
    ///         proof of doc 08's "the GUID is the authoring identity and never appears in a shipped
    ///         build". That sentence stopped being true on purpose: a component holds an
    ///         <c>AssetId</c> — it is what survives renaming the file, which an address does not — so
    ///         every <c>CatalogEntry</c> now carries its reference and the runtime resolves through
    ///         it. Two projects whose assets have different GUIDs are therefore no longer the same
    ///         content, and a build that produced identical bytes for them would have thrown the
    ///         identity away.
    ///     </para>
    ///     <para>
    ///         So the GUIDs are equal and derived from the asset's name, which keeps every other
    ///         difference this test exists for: different roots, opposite creation order, and
    ///         therefore a different directory enumeration order on the machine that runs it.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TwoProjectsWithTheSameContentAtDifferentPathsBuildToTheSameBytes() {
        // Created in opposite orders, under differently-named roots, with the same identities.
        var one = await BuildElsewhere("project-alpha", ["hero", "villain", "sidekick"]);
        var other = await BuildElsewhere("a-differently-named-project", ["sidekick", "villain", "hero"]);

        Assert.Equal(4, one.Count);
        Assert.Equal(one.Keys.Order(StringComparer.Ordinal), other.Keys.Order(StringComparer.Ordinal));

        foreach (var (name, bytes) in one) {
            Assert.True(bytes.SequenceEqual(other[name]), $"'{name}' differs between two builds of the same content.");
        }
    }

    /// <summary>
    ///     <b>Why comparing one runner's build against another's needs <c>--target</c> spelled out.</b>
    ///     A build is a function of its target, the target is written into the catalog, and the target
    ///     nobody names is the operating system doing the building — so the same content built on
    ///     three runners produces three different catalogs, and correctly so.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the trap a cross-runner byte gate falls into on its first run.</b> The two
    ///         tests above compare two builds made on one machine, so the defaulted target is the same
    ///         string both times and the difference is invisible to them. Uploading their output from
    ///         `ubuntu-latest`, `windows-latest` and `macos-14` and diffing it would go red
    ///         immediately — not because anything is wrong, but because `"Linux"`, `"Windows"` and
    ///         `"MacOS"` are three different strings in the catalog's ordinal string table, of two
    ///         different lengths, and every offset and the trailing CRC move with them.
    ///     </para>
    ///     <para>
    ///         So this test exists to make that a stated property rather than a discovery. It is not
    ///         asserting that the difference is a defect — <c>ProjectWorkspace.HostTarget</c> defaults
    ///         to "for this computer" deliberately, and a target-specific build is the whole point of
    ///         having targets. It is asserting that the difference is <i>real</i>, so that a gate
    ///         comparing runners pins the target and a reader who removes the pin finds out here.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheSameContentBuiltForTwoTargetsIsNotTheSameBytes() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");

        var windows = Path.Combine(root, "out-windows");
        var linux = Path.Combine(root, "out-linux");

        Assert.Equal(ExitCode.Success, (await Run("content", "build", "--target", "Windows", "--output", windows)).Code);
        Assert.Equal(ExitCode.Success, (await Run("content", "build", "--target", "Linux", "--output", linux)).Code);

        var forWindows = Files(windows);
        var forLinux = Files(linux);

        // Both builds happened, so a pair of empty directories cannot be read as agreement.
        Assert.Equal(4, forWindows.Count);
        Assert.Equal(forWindows.Keys.Order(StringComparer.Ordinal), forLinux.Keys.Order(StringComparer.Ordinal));

        // The catalog carries the target string, so it moves. This is the byte difference a
        // cross-runner comparison would otherwise report as a determinism failure.
        Assert.False(
            forWindows["catalog.bin"].SequenceEqual(forLinux["catalog.bin"]),
            "the catalog does not record which target it was built for, so a build is no longer a function of its target."
        );

        // And the same content, built for the same target twice, still is what it was — otherwise the
        // line above would pass for a build that is simply not reproducible, which is the opposite
        // claim. This is the control that keeps the assertion above meaning what it says.
        var again = Path.Combine(root, "out-windows-again");

        Assert.Equal(ExitCode.Success, (await Run("content", "build", "--target", "Windows", "--output", again)).Code);

        foreach (var (name, bytes) in Files(again)) {
            Assert.True(bytes.SequenceEqual(forWindows[name]), $"'{name}' differs between two builds for one named target.");
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
    public async Task AProjectWithNoAddressesShipsEverythingUnderItsPaths() {
        Asset("notes.txt", "just a file");

        var (code, output) = await Run("content", "build");

        Assert.Equal(ExitCode.Success, code);

        // ⚠ One address, and nobody typed it. A project used to have to name every asset it wanted
        // shipped; the path is the name now, and this is that seen from a terminal.
        var catalog = CatalogFormat.Read(File.ReadAllBytes(Path.Combine(Build(), "catalog.bin")));

        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.Contains("Assets/notes.txt"));

        // And it says where the group it invented came from, because the moment a project cares
        // about compression or remote delivery it needs a real .vxgroup.
        Assert.Contains("Default", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <b>What makes an asset problem an entry in the IDE's error list.</b> MSBuild picks
    ///     <c>file: error CODE: text</c> out of a tool's output and nothing else, so the file has to
    ///     be absolute — a relative one is resolved against whatever directory the build is running
    ///     in, which is not the project's — and the code has to be there or the line is prose.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is a <i>plan</i> diagnostic, and it used to arrive with no file at all.</b>
    ///     The planner names the asset inside its sentence, which serves a person reading a log and
    ///     serves an error list nothing: MSBuild attributed the line to the project, and
    ///     double-clicking it opened the <c>.csproj</c>. <c>ImportDiagnostic.Path</c> is what closed
    ///     that, and asserting the code alone — which is what this test did — passes either way.
    /// </remarks>
    [Fact]
    public async Task TheMsbuildFormatCarriesAnAbsolutePathAndACode() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "Missing");

        var (code, output) = await Run("content", "build", "--format", "msbuild");

        Assert.Equal(ExitCode.Failed, code);
        Assert.Contains($"error {DiagnosticCode.Plan}:", output, StringComparison.Ordinal);

        var expected = Path.Combine(root, "Assets", "hero.txt");

        Assert.Contains($"{expected}: error {DiagnosticCode.Plan}:", output, StringComparison.Ordinal);

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
        Assert.Contains("Default", output, StringComparison.Ordinal);
        Assert.DoesNotContain("error", output, StringComparison.Ordinal);
        Assert.DoesNotContain("warning", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <b>Doc 17 § Build variants, and the reason it is a group question.</b> A server build's
    ///     content is the client's less the groups an author said a dedicated server does not need.
    ///     Nothing is dropped by asset <i>type</i>: a heightmap is a texture and
    ///     <c>TerrainColliderSystem</c> bakes a server's collision out of one, so a build that
    ///     stripped "the textures" would take the ground away and say nothing.
    /// </summary>
    [Fact]
    public async Task AServerBuildLeavesOutTheGroupsAServerWasToldItDoesNotNeed() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "Pixels");
        Asset("sword.txt", "sword", address: "items/sword", group: "Rules");
        Group("Pixels", onServer: false);
        Group("Rules");

        var client = Path.Combine(root, "out-client");
        var server = Path.Combine(root, "out-server");

        Assert.Equal(ExitCode.Success, (await Run("content", "build", "--output", client)).Code);
        Assert.Equal(
            ExitCode.Success,
            (await Run("content", "build", "--variant", "Server", "--output", server)).Code
        );

        var forClient = CatalogFormat.Read(File.ReadAllBytes(Path.Combine(client, "catalog.bin")));
        var forServer = CatalogFormat.Read(File.ReadAllBytes(Path.Combine(server, "catalog.bin")));

        Assert.True(forClient.Contains("ui/hero"));
        Assert.True(forClient.Contains("items/sword"));

        // Both halves, because the dangerous failure is the second one. A build that dropped the
        // pixels is only correct if it still shipped everything a realm asks for by name.
        Assert.False(forServer.Contains("ui/hero"));
        Assert.True(forServer.Contains("items/sword"));
    }

    /// <summary>
    ///     And a client build is unchanged by the flag existing: the default profile ships every
    ///     group, so the same project builds the same catalog it always did.
    /// </summary>
    [Fact]
    public async Task AGroupAServerDoesNotNeedIsStillInTheClientBuild() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "Pixels");
        Group("Pixels", onServer: false);

        var (code, _) = await Run("content", "build");

        Assert.Equal(ExitCode.Success, code);
        Assert.True(CatalogFormat.Read(File.ReadAllBytes(Path.Combine(Build(), "catalog.bin"))).Contains("ui/hero"));
    }

    /// <summary>
    ///     A variant this build does not know is refused by name rather than quietly building the
    ///     client profile — which is the failure this whole feature exists to stop being silent.
    /// </summary>
    [Fact]
    public async Task AnUnknownVariantIsAUsageError() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");

        var (code, _, error) = await RunFull("content", "build", "--project", root, "--variant", "Dreamcast");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("Dreamcast", error, StringComparison.Ordinal);
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

    // --- The shader bundle --------------------------------------------------

    /// <summary>
    ///     A build with a shader manifest writes the bundle a shipping run loads instead of compiling.
    /// </summary>
    /// <remarks>
    ///     The last step of doc 06's third tier. Everything below it was already a library call; what
    ///     this asserts is that <c>vixen build</c> produces the file, beside the catalog, in a form
    ///     the runtime reads — which is the only place the three parts (a project's shaders, a
    ///     manifest somebody committed, and the bundle format) are ever in the same room.
    /// </remarks>
    [Fact]
    public async Task ABuildCompilesTheShaderManifestIntoABundle() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");
        Shader("Tint.rvn");
        Manifest("""{ "Effects": [ { "Shader": "Tint", "Permutations": { "Tint.Bright": "true" } } ] }""");

        var (code, output) = await Run("content", "build");

        Assert.True(code == ExitCode.Success, output);
        Assert.Contains("Compiled 1 shader variant", output, StringComparison.Ordinal);

        var bundle = Path.Combine(Build(), ShaderBuildRunner.BundleFileName);

        Assert.True(File.Exists(bundle));

        // And what came out is the record the runtime resolves through, not merely a file of bytes.
        var store = new EffectStore(Serializer.Read<EffectBundle>(File.ReadAllBytes(bundle)));
        var key = Assert.Single(store.Keys);

        Assert.Equal("Tint", key.ShaderName);
        Assert.Equal("true", Assert.Single(key.Values).Value);
    }

    /// <summary>
    ///     A server build compiles no shader bundle at all, and removes one a previous client build
    ///     left in the same directory.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A dedicated server runs <c>Vixen.Graphics.Null</c> and creates no pipeline, so every
    ///         variant in the manifest is dead weight — and the bundle is a sibling of the catalog
    ///         rather than an addressed chunk, so its absence cannot leave the catalog naming
    ///         something that is not there. <c>ContentMount</c> already reports "No baked shaders"
    ///         once and boots, which is what makes this safe rather than merely smaller.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Skipped whole rather than compiled with permutations dropped.</b> A value in
    ///         <c>Permutations</c> that is not also in <c>PermutationKeys</c> never reaches the
    ///         compiler and the variant silently takes the <c>.rvn</c> default — so "compile fewer"
    ///         is a change whose failure mode is invisible, and "compile none" is one this test can
    ///         see.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task AServerBuildCompilesNoShaderBundleAndClearsAStaleOne() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");
        Shader("Tint.rvn");
        Manifest("""{ "Effects": [ { "Shader": "Tint", "Permutations": { "Tint.Bright": "true" } } ] }""");

        Assert.Equal(ExitCode.Success, (await Run("content", "build")).Code);

        var bundle = Path.Combine(Build(), ShaderBuildRunner.BundleFileName);

        Assert.True(File.Exists(bundle));

        var (code, output) = await Run("content", "build", "--variant", "Server");

        Assert.True(code == ExitCode.Success, output);
        Assert.DoesNotContain("Compiled 1 shader variant", output, StringComparison.Ordinal);

        // The stale one goes, because an output directory is what somebody copies into an image and
        // one carrying the previous build's shaders is a server image shipping a client's.
        Assert.False(File.Exists(bundle));
    }

    /// <summary>
    ///     A graph-authored material is compiled into the shipping bundle, which is the end of the
    ///     graph story.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The "finished thing nothing calls" assertion, and the reason it lives in the
    ///         CLI's suite.</b> <c>ShaderGraphSources</c> can be perfect and this runner never call
    ///         it — which is exactly the state the shader graph shipped in for its whole life,
    ///         emitting correct Raven that no compilation in the process had ever seen. Testing the
    ///         enumerator proves the enumerator; only this proves the build asks it, and only this
    ///         proves the answer reaches the bundle a shipping run loads.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It also depends on the engine's library being beside the CLI, which it was
    ///         not.</b> A graph's surface imports <c>Vixen.Shaders.Material</c>, and this runner
    ///         compiled a project's own <c>*.rvn</c> and nothing else — so every project shader that
    ///         imports the engine library was unbakeable, and the editor could compile a shader the
    ///         build could not. That gap is older than the graph and this test is what holds it shut.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ABuildCompilesAGraphAuthoredMaterialIntoTheBundle() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");

        var graph = new Vixen.Editor.NodeGraph.NodeGraphModel { Name = "Painted" };

        graph.Add("Master/Surface");

        Asset(
            "Painted.vxshadergraph",
            YamlSerializer.ToYaml(Vixen.Editor.NodeGraph.NodeGraphDocument.Save(graph))
        );

        // The variant a material naming this graph would ask for: the forward pass, with the graph
        // in the chain's first slot.
        Manifest(
            """
            {
              "Effects": [
                {
                  "Shader": "ForwardPlus",
                  "Composition": { "CompositeSurface.first": "Painted" }
                }
              ]
            }
            """
        );

        var (code, output) = await Run("content", "build");

        Assert.True(code == ExitCode.Success, output);
        Assert.Contains("Compiled 1 shader variant", output, StringComparison.Ordinal);

        var store = new EffectStore(
            Serializer.Read<EffectBundle>(
                File.ReadAllBytes(Path.Combine(Build(), ShaderBuildRunner.BundleFileName))
            )
        );

        var key = Assert.Single(store.Keys);

        Assert.Equal("ForwardPlus", key.ShaderName);

        // ⚠ The graph's name in the composition, which is the whole claim: what was baked is the
        // pass composed with a shader that exists only because a `.vxshadergraph` was compiled.
        Assert.Equal("Painted", key.Composition.Resolve("CompositeSurface.first"));
    }

    /// <summary>
    ///     A shader graph reaches the build's own shader compilation, and its complaints reach the
    ///     build log.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Reported at import, before the shader step.</b> <c>ShaderGraphImporter</c> compiles
    ///     the graph to find out whether it is one — <c>MaterialImporter</c>'s arrangement — so a
    ///     graph with no master fails the build against the file that caused it rather than as a
    ///     shader that mysteriously never appears.
    /// </remarks>
    [Fact]
    public async Task ABuildReportsAShaderGraphThatDoesNotCompile() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");
        Shader("Tint.rvn");
        Manifest("""{ "Effects": [ { "Shader": "Tint", "Permutations": { "Tint.Bright": "true" } } ] }""");

        // A graph the editor would have saved, with an input node and nothing to write it to.
        // Written through the real document writer, so the fixture cannot pass by being a file the
        // reader happens to reject for a different reason than the one under test.
        var graph = new Vixen.Editor.NodeGraph.NodeGraphModel { Name = "Unfinished" };

        graph.Add("Input/UV");

        Asset(
            "Unfinished.vxshadergraph",
            YamlSerializer.ToYaml(Vixen.Editor.NodeGraph.NodeGraphDocument.Save(graph))
        );

        var (code, output) = await Run("content", "build");

        Assert.True(code == ExitCode.Failed, output);
        Assert.Contains("Unfinished.vxshadergraph", output, StringComparison.Ordinal);
        Assert.Contains("SG0003", output, StringComparison.Ordinal);
    }

    /// <summary>A project that has not got to a manifest yet still builds.</summary>
    /// <remarks>
    ///     It runs against a compiler in development, so it is not broken — it is early. Saying that
    ///     rather than failing is the difference between a step somebody takes when they are ready and
    ///     a step they work around.
    /// </remarks>
    [Fact]
    public async Task ABuildWithNoShaderManifestSaysWhatToDo() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");

        var (code, output) = await Run("content", "build");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("EffectSystem.Requests", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Build(), ShaderBuildRunner.BundleFileName)));
    }

    /// <summary>A manifest naming a shader nobody has is a warning, and the build still finishes.</summary>
    /// <remarks>
    ///     The usual cause is a manifest older than the material it was captured from, and failing a
    ///     build for a line somebody can delete would be the wrong trade. The run that needs it
    ///     reports it as a miss, by the same name.
    /// </remarks>
    [Fact]
    public async Task AStaleShaderManifestWarnsRatherThanFailing() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");
        Shader("Tint.rvn");
        Manifest("""{ "Effects": [ { "Shader": "Deleted" } ] }""");

        var (code, output) = await Run("content", "build");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("no shader in this project answers to it", output, StringComparison.Ordinal);
    }

    /// <summary>A shader that does not compile fails the build, saying what the compiler said.</summary>
    [Fact]
    public async Task AShaderThatDoesNotCompileFailsTheBuild() {
        Asset("hero.txt", "hero", address: "ui/hero", group: "UiCore");
        Group("UiCore");
        Asset("Broken.rvn", "package Game\n\nshader Broken {\n    var tint: float3 = nonsense\n}\n");
        Manifest("""{ "Effects": [ { "Shader": "Broken" } ] }""");

        var (code, output) = await Run("content", "build");

        Assert.Equal(ExitCode.Failed, code);
        Assert.Contains("error", output, StringComparison.OrdinalIgnoreCase);
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
    ///     Every verb doc 14 lists now parses. This test used to assert the opposite — that `new`,
    ///     `build` and `run` were absent rather than present-and-apologising, because a build script
    ///     can only discover the second kind at run time. That was the right assertion while they
    ///     were owed, and it is kept inverted rather than deleted so the transition is visible.
    /// </summary>
    [Theory]
    [InlineData("new")]
    [InlineData("run")]
    [InlineData("build")]
    [InlineData("import")]
    [InlineData("doctor")]
    public void EveryVerbTheRoadmapNamesIsPresent(string verb) =>
        Assert.Contains(VixenCommand.Create().Subcommands, command => command.Name == verb);


    // ── The advisory pass ───────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A scene naming a component nothing in this build declares. What `Vixen.Sdk`'s pre-compile
    ///     import meets on every clean build of every game that puts its own components in a level.
    /// </summary>
    void SceneNamingAnUndeclaredComponent(string relativePath) =>
        Asset(
            relativePath,
            """
            version: 1
            name: Arena
            roots:
              - name: Floor
                position: 0 0 0
                components:
                  - !BoxCollision { halfExtents: 32 0.5 32 }
            """
        );

    /// <summary>
    ///     ⚠ <b>The control, and the reason the test below is about anything.</b> Without the flag
    ///     this is an error-list entry and a failed run, which is what every other caller keeps.
    /// </summary>
    /// <remarks>
    ///     An <em>error</em>, and it reached a build log as a warning only because
    ///     <c>ContinueOnError</c> demotes what a task emitted. Two demotions deep is a long way from
    ///     "an importer said this", which is part of why this class was twice misread.
    /// </remarks>
    [Fact]
    public async Task AnUnresolvableComponentIsAnErrorAndAFailureByDefault() {
        SceneNamingAnUndeclaredComponent("Scenes/Arena.vxscene");

        var (code, output) = await Run("import", "--format", "msbuild");

        Assert.Equal(ExitCode.Failed, code);
        Assert.Contains($"error {DiagnosticCode.Import}:", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Under <c>--advisory</c> the same finding keeps its code, its path and its sentence, and
    ///     stops being an entry in a list of things to act on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The pass runs <c>BeforeTargets=CoreCompile</c>, so the assembly declaring
    ///         <c>BoxCollision</c> is the one the compiler it precedes would have produced: it cannot
    ///         resolve this and never will. The content build after <c>Build</c> loads the assembly
    ///         and does fail, which is where a real one is reported.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted as "not an MSBuild diagnostic" rather than as "not printed".</b> Silence
    ///         would leave <c>2 failed</c> in the log with nothing saying what — and this pass is the
    ///         only one some project configurations run at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task AnAdvisoryImportSaysTheSameThingWithoutDressingItAsADiagnostic() {
        SceneNamingAnUndeclaredComponent("Scenes/Arena.vxscene");

        var (code, output) = await Run("import", "--format", "msbuild", "--advisory");

        Assert.Equal(ExitCode.Success, code);

        // Neither word, so MSBuild reads the line as prose and no error list gains an entry.
        Assert.DoesNotContain($"warning {DiagnosticCode.Import}:", output, StringComparison.Ordinal);
        Assert.DoesNotContain($"error {DiagnosticCode.Import}:", output, StringComparison.Ordinal);

        // And everything a reader needs is still on it.
        Assert.Contains($"advisory {DiagnosticCode.Import}:", output, StringComparison.Ordinal);
        Assert.Contains("BoxCollision", output, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(root, "Assets", "Scenes", "Arena.vxscene"), output, StringComparison.Ordinal);
        Assert.Contains("the authority", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The half that is a bug rather than noise.</b> The addresses are written after the
    ///     import, so returning early on a failed asset skipped them — on precisely the build that
    ///     had no assembly to resolve a level against, and whose whole reason for running before
    ///     <c>CoreCompile</c> is to put that file in front of it.
    /// </summary>
    [Fact]
    public async Task AnAdvisoryImportStillWritesTheAddressConstantsAFailedAssetUsedToCost() {
        SceneNamingAnUndeclaredComponent("Scenes/Arena.vxscene");
        Asset("sword.txt", "sword", address: "items/sword", group: "Core");
        Group("Core");

        var into = Path.Combine(root, "obj", "Addresses.g.cs");
        var (code, _) = await Run("import", "--addresses", into, "--addresses-namespace", "MyGame", "--advisory");

        Assert.Equal(ExitCode.Success, code);

        var source = await File.ReadAllTextAsync(into, TestContext.Current.CancellationToken);
        Assert.Contains("public const string Address = \"items/sword\";", source, StringComparison.Ordinal);
    }

    /// <summary>The same run without the flag, which is what the file above used to be.</summary>
    [Fact]
    public async Task AFailedAssetStillCostsTheAddressConstantsWithoutTheFlag() {
        SceneNamingAnUndeclaredComponent("Scenes/Arena.vxscene");
        Asset("sword.txt", "sword", address: "items/sword", group: "Core");
        Group("Core");

        var into = Path.Combine(root, "obj", "Addresses.g.cs");
        var (code, _) = await Run("import", "--addresses", into, "--addresses-namespace", "MyGame");

        Assert.Equal(ExitCode.Failed, code);
        Assert.False(File.Exists(into));
    }

    // ── Address constants ───────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Written by `import` rather than by `content build`, and the ordering is the whole point:
    ///     Vixen.Sdk runs the import BeforeTargets=CoreCompile precisely so that generated C# exists
    ///     before the compiler reads its inputs. A constant emitted after the build is one build out
    ///     of date, every build.
    /// </summary>
    [Fact]
    public async Task ImportingWritesTheAddressConstantsWhenAskedTo() {
        Asset("sword.txt", "sword", address: "items/weapons/flamebrand", group: "Core");
        Asset("map.txt", "map", address: "maps/greenmarch", group: "Core");
        Group("Core");

        var into = Path.Combine(root, "obj", "Addresses.g.cs");
        var (code, _) = await Run("import", "--addresses", into, "--addresses-namespace", "MyGame");

        Assert.Equal(ExitCode.Success, code);

        var source = await File.ReadAllTextAsync(into, TestContext.Current.CancellationToken);

        Assert.Contains("namespace MyGame;", source, StringComparison.Ordinal);
        Assert.Contains("public const string Address = \"items/weapons/flamebrand\";", source, StringComparison.Ordinal);
        Assert.Contains("public const string Address = \"maps/greenmarch\";", source, StringComparison.Ordinal);
    }

    /// <summary>Nothing is written unless asked, because most projects are not gameplay projects.</summary>
    [Fact]
    public async Task ImportingWritesNoConstantsByDefault() {
        Asset("sword.txt", "sword", address: "items/sword", group: "Core");
        Group("Core");

        var (code, _) = await Run("import");

        Assert.Equal(ExitCode.Success, code);
        Assert.False(Directory.Exists(Path.Combine(root, "obj")));
    }

    /// <summary>
    ///     ⚠ Rewritten only when it changed. An unconditional write makes MSBuild rebuild the whole
    ///     project on every build, which is how an incremental build stops being one.
    /// </summary>
    [Fact]
    public async Task ASecondImportDoesNotTouchAnUnchangedFile() {
        Asset("sword.txt", "sword", address: "items/sword", group: "Core");
        Group("Core");

        var into = Path.Combine(root, "obj", "Addresses.g.cs");

        await Run("import", "--addresses", into);

        var first = File.GetLastWriteTimeUtc(into);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await Run("import", "--addresses", into);

        Assert.Equal(first, File.GetLastWriteTimeUtc(into));
    }

    /// <summary>The DefId half is opt-in, or a game that declined doc 28 gets a file it cannot compile.</summary>
    [Fact]
    public async Task TheDefIdHalfIsOptIn() {
        Asset("sword.txt", "sword", address: "items/sword", group: "Core");
        Group("Core");

        var into = Path.Combine(root, "obj", "Addresses.g.cs");

        await Run("import", "--addresses", into);
        Assert.DoesNotContain("Vixen.Gameplay", await File.ReadAllTextAsync(into, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        await Run("import", "--addresses", into, "--address-ids");
        Assert.Contains("using Vixen.Gameplay;", await File.ReadAllTextAsync(into, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    string Build() => Path.Combine(root, "Build", Project.HostTarget.Replace('/', '-'));

    /// <summary>The same asset name gives the same id, so two projects can hold the same content.</summary>
    /// <remarks>
    ///     Derived rather than a table of literals: the point is that both projects agree, and a
    ///     hash of the name says that in one line and keeps saying it when an asset is added.
    /// </remarks>
    static AssetId IdentityOf(string asset) =>
        new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(asset)).AsSpan(0, 16)));

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
                    Guid = IdentityOf(asset),
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

    /// <summary>A shader with one permutation that changes its output, under <c>Assets/</c>.</summary>
    /// <remarks>
    ///     Deliberately trivial and deliberately SPIR-V-clean: no <c>bool</c> uniform, which the
    ///     SPIR-V backend refuses because std140 gives one four bytes and SPIR-V has no four-byte
    ///     boolean to put there.
    /// </remarks>
    void Shader(string relativePath) =>
        Asset(
            relativePath,
            """
            package Game

            shader Tint {
                [Permutation] val Bright: bool = false

                var tint: float3

                [VertexShader]
                [Semantic("SV_Position")]
                func Vertex(position: float3): float4 {
                    return float4(position, 1f)
                }

                [FragmentShader]
                [Semantic("SV_Target")]
                func Fragment(): float4 {
                    var color = tint

                    if (Bright) {
                        color = color * 2f
                    }

                    return float4(color, 1f)
                }
            }

            """
        );

    /// <summary>The shader manifest, under <c>ProjectSettings/</c> where it is committed.</summary>
    void Manifest(string json) {
        var settings = Path.Combine(root, "ProjectSettings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, ShaderBuildRunner.ManifestFileName), json);
    }

    /// <summary>A group file under <c>Assets/</c>, optionally one a dedicated server leaves out.</summary>
    /// <remarks>
    ///     ⚠ The <c>onServer: false</c> line is appended as text rather than set on the record, so
    ///     that this helper says what the <i>file</i> has to contain. A <c>.vxgroup</c> is authored
    ///     and reviewed in a diff, so the key an author types is the contract, and a test that went
    ///     through the record would pass on a property the YAML binding never reads.
    /// </remarks>
    void Group(string name, bool onServer = true) {
        var yaml = YamlSerializer.ToYaml(new AddressableGroup { Name = name, IncludeInServerBuild = onServer });

        // ⚠ Asserted rather than assumed. The key an author types into a .vxgroup is the contract,
        // and a serializer that dropped it — because the value equalled the default, or because the
        // property was never bound — would leave every one of these tests passing against a file
        // that says nothing about the server profile.
        Assert.Contains($"includeInServerBuild: {(onServer ? "true" : "false")}", yaml, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(root, "Assets", $"{name}.vxgroup"), yaml);
    }

    static Dictionary<string, byte[]> Files(string directory) =>
        Directory.GetFiles(directory)
            .ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes, StringComparer.Ordinal);

    // ── vixen new ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A scaffolded game is a project the SDK drives, which is the whole reason `new` waited for
    ///     `Vixen.Sdk` to exist: the alternative is a template listing package references that are
    ///     wrong one release later.
    /// </summary>
    [Fact]
    public async Task NewGameWritesAProjectDrivenByTheSdk() {
        var where = Path.Combine(root, "Fresh");

        var (code, output, _) = await RunFull("new", "game", "Asteroids", "-o", where);

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("Created game 'Asteroids'", output, StringComparison.Ordinal);

        var project = await File.ReadAllTextAsync(
            Path.Combine(where, "Asteroids.csproj"),
            TestContext.Current.CancellationToken
        );

        Assert.Contains($"Sdk=\"Vixen.Sdk/{ScaffoldRunner.SdkVersion}\"", project, StringComparison.Ordinal);

        // Everything a first `dotnet run` needs: a host, a game, somewhere for assets, and a
        // gitignore that keeps Library/ out of the history.
        Assert.True(File.Exists(Path.Combine(where, "Program.cs")));
        Assert.True(File.Exists(Path.Combine(where, "AsteroidsGame.cs")));
        Assert.True(File.Exists(Path.Combine(where, "Assets", "Default.vxgroup")));
        Assert.Contains("Library/", await File.ReadAllTextAsync(Path.Combine(where, ".gitignore"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The name reaches both places it has to: the namespace and the type the host starts.
    /// </summary>
    [Fact]
    public async Task TheNameIsUsedAsBothANamespaceAndATypeName() {
        var where = Path.Combine(root, "Named");

        await RunFull("new", "game", "Asteroids", "-o", where);

        var program = await File.ReadAllTextAsync(Path.Combine(where, "Program.cs"), TestContext.Current.CancellationToken);
        var game = await File.ReadAllTextAsync(Path.Combine(where, "AsteroidsGame.cs"), TestContext.Current.CancellationToken);

        Assert.Contains("VixenApp.Run<AsteroidsGame>(args)", program, StringComparison.Ordinal);
        Assert.Contains("using Asteroids;", program, StringComparison.Ordinal);
        Assert.Contains("namespace Asteroids;", game, StringComparison.Ordinal);
        Assert.Contains("public sealed class AsteroidsGame : Game", game, StringComparison.Ordinal);

        // Top-level statements cannot follow a namespace declaration, so Program.cs must not have one.
        Assert.DoesNotContain("namespace Asteroids;\n\n// Everything", program, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Nothing is written when anything would be overwritten — and nothing means nothing, not
    ///     "the files that did not collide". A half-scaffolded directory is worse than an untouched
    ///     one, because the second is obviously a no-op.
    /// </summary>
    [Fact]
    public async Task NewRefusesRatherThanOverwritingAndWritesNothingAtAll() {
        var where = Path.Combine(root, "Occupied");
        Directory.CreateDirectory(where);
        await File.WriteAllTextAsync(Path.Combine(where, "Program.cs"), "mine", TestContext.Current.CancellationToken);

        var (code, output, _) = await RunFull("new", "game", "Asteroids", "-o", where);

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("Nothing was written", output, StringComparison.Ordinal);
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(where, "Program.cs"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(where, "Asteroids.csproj")));
    }

    /// <summary>
    ///     A name that is not a legal identifier is refused here rather than by the compiler, whose
    ///     complaint arrives after the files exist and names a generated line.
    /// </summary>
    [Theory]
    [InlineData("9Lives")]
    [InlineData("my-game")]
    [InlineData("my game")]
    public async Task AnUnusableNameIsRefusedBeforeAnythingIsWritten(string name) {
        var where = Path.Combine(root, "Bad");

        var (code, _, _) = await RunFull("new", "game", name, "-o", where);

        Assert.Equal(ExitCode.UsageError, code);
        Assert.False(Directory.Exists(where) && Directory.GetFiles(where).Length > 0);
    }

    /// <summary>
    ///     A library gets no SDK, because it has no assets to import and no content to build — two
    ///     no-op build steps and a tool dependency for nothing.
    /// </summary>
    [Fact]
    public async Task NewLibraryDoesNotUseTheSdk() {
        var where = Path.Combine(root, "Lib");

        var (code, _, _) = await RunFull("new", "library", "Physics", "-o", where);

        Assert.Equal(ExitCode.Success, code);

        var project = await File.ReadAllTextAsync(Path.Combine(where, "Physics.csproj"), TestContext.Current.CancellationToken);

        Assert.Contains("Microsoft.NET.Sdk", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Vixen.Sdk", project, StringComparison.Ordinal);
    }

    /// <summary>An application head, and what a scaffolded one is now made of.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This asserted that <c>Shaders/ui.vert.spv</c> came out with its SPIR-V magic
    ///         number intact — a module written byte for byte rather than decoded as text and written
    ///         back, because one that went through a string is a device lost rather than a compile
    ///         error. There is no such file any more.</b> <c>vixen-app</c> carried eight committed
    ///         modules because it carried its own frame loop; it takes <c>Vixen.Ui.Desktop</c> now,
    ///         which embeds them, so the whole <c>Shaders/</c> folder went with the four hundred lines
    ///         of C# around it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The binary-round-trip rule is still asserted, and it is asserted where it lives:</b>
    ///         <c>Vixen.Templates.Tests.ABinaryFileIsCopiedRatherThanSubstitutedInto</c> tests
    ///         <c>TemplateCatalog.IsTextFile</c> directly, and <c>NoTemplateShipsABinaryFile</c> fails
    ///         the day a template ships one again — which is the signal to bring an assertion like the
    ///         old one back, here or there.
    ///     </para>
    ///     <para>
    ///         What is left for this test is the scaffold itself: the two files a new application is,
    ///         and the absence of the folder it used to have.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task NewApplicationWritesMarkupAndNoShaders() {
        var where = Path.Combine(root, "App");

        var (code, _, _) = await RunFull("new", "app", "Painter", "-o", where);

        Assert.Equal(ExitCode.Success, code);

        Assert.True(File.Exists(Path.Combine(where, "AppShell.vxml")), "a new application is markup.");
        Assert.True(File.Exists(Path.Combine(where, "Theme", "vixen.ui.vcss")), "and a stylesheet.");

        Assert.False(
            Directory.Exists(Path.Combine(where, "Shaders")),
            "a scaffolded application owns no shader modules: Vixen.Ui.Desktop embeds them."
        );

        // ⚠ And the one reference that brings all of it — the compiler, the item types, the utility
        // step and the frame loop. It was five.
        var project = await File.ReadAllTextAsync(
            Path.Combine(where, "Painter.csproj"),
            TestContext.Current.CancellationToken
        );

        Assert.Contains("Vixen.Ui.Desktop", project, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A batch head, and the sentence a scaffolded one cannot say for itself.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A new <c>vixen-tool</c> project run as-is reports that there is nothing to check,
    ///     which reads exactly like a broken scaffold.</b> It is not: a batch head reads the content
    ///     beside its own binary and a project one minute old has none. Where content comes from is
    ///     the one thing about this template that is in no file it writes, so `new` says it — the
    ///     same argument the `vixen-plugin` and `vixen-mmo` blocks above are there for.
    /// </remarks>
    [Fact]
    public async Task NewToolSaysWhereItsContentComesFrom() {
        var where = Path.Combine(root, "Tool");

        var (code, output, _) = await RunFull("new", "tool", "Bake", "-o", where);

        Assert.Equal(ExitCode.Success, code);
        Assert.True(File.Exists(Path.Combine(where, "BakeTool.cs")), "a batch head is a Game with no head.");
        Assert.Contains("--vixen-loose-content", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A template that does not exist is answered with the ones that do, because the next thing
    ///     the person is going to do is guess again.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The name here was <c>plugin</c> until <c>vixen-plugin</c> was written</b>, at which
    ///     point this test scaffolded a real project into a temporary directory and asserted a usage
    ///     error it no longer got. A test whose subject is "not a template" has to name something
    ///     that will not become one.
    /// </remarks>
    [Fact]
    public async Task AnUnknownTemplateListsTheOnesThatExist() {
        var (code, output, _) = await RunFull("new", "nonsense", "Extension", "-o", Path.Combine(root, "None"));

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("is not a template", output, StringComparison.Ordinal);
        Assert.Contains("game", output, StringComparison.Ordinal);
    }

    // ── vixen build / run ───────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every target doc 17's packaging table names has a shape, and a target that is not one is
    ///     refused by name rather than handed to `dotnet publish` to fail on.
    /// </summary>
    [Theory]
    [InlineData("Windows", "win-x64")]
    [InlineData("Linux", "linux-x64")]
    [InlineData("iOS", "ios-arm64")]
    public void EveryPublishableTargetHasARuntimeIdentifier(string target, string rid) {
        Assert.True(PlayerBuild.TryDescribe(target, out var shape));
        Assert.Equal(rid, shape.Rid);
    }

    /// <summary>
    ///     Android is selected by target framework rather than runtime identifier — publishing
    ///     `net10.0` for an Android RID produces a console application that cannot start.
    /// </summary>
    [Fact]
    public void AndroidIsSelectedByFrameworkRatherThanRuntimeIdentifier() {
        Assert.True(PlayerBuild.TryDescribe("Android", out var shape));
        Assert.Equal("net10.0-android", shape.Framework);
        Assert.False(shape.Runnable);
    }

    [Fact]
    public void AnUnknownTargetIsNotDescribed() {
        Assert.False(PlayerBuild.TryDescribe("Dreamcast", out _));
    }

    /// <summary>
    ///     And the command says so, rather than spending a minute in `dotnet publish` first.
    /// </summary>
    [Fact]
    public async Task BuildingForAnUnknownTargetIsAUsageError() {
        var (code, _, error) = await RunFull("build", "--project", root, "--target", "Dreamcast");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("Dreamcast", error, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A project directory with no .csproj has nothing to publish, and saying which command
    ///     writes one is more useful than reporting that MSBuild found no project.
    /// </summary>
    [Fact]
    public async Task BuildingWithNoProjectFileSaysHowToGetOne() {
        var (code, _, error) = await RunFull("build", "--project", root);

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("vixen new game", error, StringComparison.Ordinal);
    }

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
