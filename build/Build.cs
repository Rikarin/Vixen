// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Runtime.InteropServices;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
///     The only sanctioned way to build, test, package or release Vixen. CI calls the same targets a
///     developer calls, so "works on my machine" and "works in CI" cannot diverge — which is the
///     entire reason this exists rather than a pile of shell scripts.
/// </summary>
/// <remarks>
///     The target graph in docs/plan/12 is the destination. What is implemented here is the part
///     that has something to act on today; the rest are added as the subsystems they serve arrive,
///     rather than checked in as bodies that silently do nothing.
/// </remarks>
partial class Build : NukeBuild {
    public static int Main() => Execute<Build>(x => x.Test);

    [Parameter("Configuration to build — Debug (default locally) or Release (default in CI)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution(GenerateProjects = false)]
    readonly Solution Solution;

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    AbsolutePath TestResultsDirectory => ArtifactsDirectory / "test-results";

    AbsolutePath PackagesDirectory => ArtifactsDirectory / "packages";

    Target Clean => definition => definition
        .Description("Removes every build output, including the artifacts directory")
        .Executes(() => {
                foreach (var directory in RootDirectory.GlobDirectories("*/*/bin", "*/*/obj", "build/bin", "build/obj")) {
                    directory.DeleteDirectory();
                }

                ArtifactsDirectory.CreateOrCleanDirectory();
            }
        );

    Target Restore => definition => definition
        .Description("Restores NuGet packages")
        .Executes(() =>
            DotNetRestore(settings => settings
                .SetProjectFile(Solution)
            )
        );

    Target Compile => definition => definition
        .Description("Builds the solution with warnings as errors")
        .DependsOn(Restore)
        .Executes(() =>
            DotNetBuild(settings => settings
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoRestore()
            )
        );

    Target Test => definition => definition
        .Description("Runs every test project")
        .DependsOn(Compile)
        .Produces(TestResultsDirectory / "*.trx")
        .Executes(() => {
                TestResultsDirectory.CreateOrCleanDirectory();

                DotNetTest(settings => settings
                    .SetProjectFile(Solution)
                    .SetConfiguration(Configuration)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    // Environment that has to exist before the process starts, which is the only
                    // kind that cannot be arranged from inside a test. See .runsettings for what
                    // and why; the short version is that macOS resolves the Vulkan validation
                    // layer's library through dyld, and dyld reads its search path exactly once.
                    .SetSettingsFile(RootDirectory / ".runsettings")
                    // A directory and no filename, deliberately. Naming the file pointed every test
                    // project in the solution at the same path, and they run concurrently — so the
                    // artifact CI published was whichever assembly finished last, and the other
                    // seventeen were silently overwritten. The build still failed on a red test,
                    // because the exit code does not go through the file; but the report a human
                    // opens to find out *which* test is the whole point of producing one.
                    .SetResultsDirectory(TestResultsDirectory)
                );
            }
        );

    [Parameter("Rewrite the golden reference images instead of checking them")]
    readonly bool UpdateGolden;

    AbsolutePath GoldenDiffDirectory => ArtifactsDirectory / "golden-diff";

    Target GoldenImages => definition => definition
        .Description("Renders the fixture suite and compares it with the committed reference images")
        .DependsOn(Compile)
        .Produces(GoldenDiffDirectory / "*.png")
        .Executes(() => {
                GoldenDiffDirectory.CreateOrCleanDirectory();

                // Set on this process so the test host inherits them. Nuke's typed settings have
                // moved their environment API between versions and the inherited environment has
                // not — the same reasoning as CheckFormat's raw CLI invocation above.
                Environment.SetEnvironmentVariable("VIXEN_GOLDEN_DIFF", GoldenDiffDirectory);
                Environment.SetEnvironmentVariable("VIXEN_UPDATE_GOLDEN", UpdateGolden ? "1" : "0");

                // Run separately from `Test` rather than only as part of it. The fixtures need a
                // driver, they write artefacts a human looks at, and `--update-golden` rewrites the
                // repository — none of which belongs behind a target whose job is to be run on every
                // save. They still run under `Test` too, so a broken picture fails a normal build.
                DotNetTest(settings => settings
                    .SetProjectFile(RootDirectory / "Platform" / "Vixen.Graphics.Golden.Tests"
                        / "Vixen.Graphics.Golden.Tests.csproj")
                    .SetConfiguration(Configuration)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetSettingsFile(RootDirectory / ".runsettings")
                    .SetResultsDirectory(TestResultsDirectory)
                );

                if (UpdateGolden) {
                    Serilog.Log.Warning(
                        "The reference images have been rewritten. Look at them before committing: a "
                        + "suite that updates its own expectations is a suite that always passes."
                    );
                }
            }
        );

    Target CheckFormat => definition => definition
        .Description("Fails if a file deviates from .editorconfig, lacks its SPDX header, or is a dependency nothing attributes")
        .DependsOn(Restore)
        .Executes(() => {
                // First, because it takes milliseconds and the two passes below take minutes. A
                // developer who forgot a header finds out before the format run, not after it.
                CheckLicenceHeaders();

                // The other half of ADR-015's licence obligation, and here for the same reason: it
                // reads three files and takes milliseconds. The header says whose each file is; this
                // says whose everything we did not write is. Also a target of its own — `nuke
                // CheckAttribution` — so it can be run, and watched failing, without the two
                // minute-long passes below.
                CheckAttributionManifest();

                // Invoked raw rather than through Nuke's typed settings, whose shape has moved
                // between versions; the CLI's has not.
                //
                // `style` and `analyzers`, deliberately not `whitespace`. The repository indents a
                // lambda body passed as an argument one level further than `dotnet format` does,
                // consistently, in every file — and there is no .editorconfig key that expresses
                // that, so the whitespace pass reports about nine hundred violations against code
                // that is entirely consistent with itself. Gating on it would mean reformatting
                // twenty-eight files against the tool that actually formats them, after which the
                // next edit in the IDE would put them back. The brace and spacing rules the config
                // *can* express are set (see .editorconfig § Layout), which is what took that
                // number down from roughly forty thousand.
                DotNet($"format style \"{Solution.Path}\" --verify-no-changes --severity warn --no-restore");
                DotNet($"format analyzers \"{Solution.Path}\" --verify-no-changes --severity warn --no-restore");
            }
        );

    /// <summary>
    ///     The two tags every authored source file has to carry, in the first
    ///     <see cref="LicenceHeaderLines" /> lines.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Presence, not spelling, and the comment syntax is not looked at. The header is
    ///         <c>//</c> in C#, TypeScript and ANTLR, <c>/* … */</c> in VCSS and
    ///         <c>&lt;!-- … --&gt;</c> in VXML — three shapes for one obligation, so matching the tag
    ///         rather than the line is what lets one rule cover all five file types. It is also what
    ///         SPDX itself specifies: the tag is the contract, the comment around it is the host
    ///         language's business.
    ///     </para>
    ///     <para>
    ///         Both tags, not just the licence. <c>SPDX-License-Identifier</c> says what may be done
    ///         with the file and <c>SPDX-FileCopyrightText</c> says whose it is; ADR-015 lists them
    ///         together because Apache-2.0 §4(c) is about attribution, and a licence with no
    ///         copyright holder attributes nothing. As of this commit the tree carries exactly one
    ///         spelling of each across all 4 510 files in scope — <c>Copyright (c) Rikarin</c> and
    ///         <c>Apache-2.0</c> — but the values are deliberately not asserted: a vendored file
    ///         that legitimately carries someone else's copyright, or a differently licensed one,
    ///         should be readable here rather than forced into a lie or into an exclusion list.
    ///     </para>
    /// </remarks>
    static readonly string[] LicenceHeaderTags = ["SPDX-FileCopyrightText:", "SPDX-License-Identifier:"];

    /// <summary>
    ///     How far into a file the header may be. Ten lines, so that a generated file's
    ///     <c>&lt;auto-generated&gt;</c> banner, a VXML comment block or a shebang can precede it.
    /// </summary>
    const int LicenceHeaderLines = 10;

    /// <summary>
    ///     The file types the header is enforced on, and the top-level directories searched for
    ///     them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Five extensions, chosen from what the tree already does rather than from what
    ///         would be nice.</b> <c>.cs</c>, <c>.g4</c>, <c>.vxml</c>, <c>.vcss</c> and <c>.ts</c>
    ///         are the authored source languages here, and every one of them was already at or
    ///         within a rounding error of complete coverage when this gate was written — 4 476 of
    ///         4 493 C# files, and 100% of the other four types. A gate written to match an existing
    ///         convention costs nothing to turn on; a gate that first requires a thousand-file
    ///         rewrite gets turned off instead.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is deliberately out of scope, and why each is not an oversight.</b>
    ///         <c>.rvn</c> shaders carry the header in one file of 125 — Raven's library predates the
    ///         relicence and heading it is a separate change with its own diff to read, not something
    ///         to smuggle in behind a build target. Project files (<c>.csproj</c>, <c>.props</c>,
    ///         <c>.targets</c>: 3 of 421) and Markdown (34 of 453) are the same story with weaker
    ///         motivation — nobody vendors a single <c>.csproj</c>. <c>.frag</c> and <c>.vert</c> are
    ///         GLSL fixtures, none headed. Binary and asset formats are excluded by not being listed:
    ///         a header cannot go in a <c>.png</c> or a <c>.spv</c>, which is what <c>NOTICE</c> and
    ///         the third-party manifest are for.
    ///     </para>
    /// </remarks>
    static readonly string[] LicenceHeaderRoots = [
        "Benchmarks", "Core", "Editor", "Gameplay", "Live", "Platform",
        "Raven", "Samples", "Testing", "Tools", "build", "docs", "www"
    ];

    /// <inheritdoc cref="LicenceHeaderRoots" />
    static readonly string[] LicenceHeaderExtensions = ["cs", "g4", "vxml", "vcss", "ts"];

    /// <summary>
    ///     Fails <see cref="CheckFormat" /> if an authored source file is missing its SPDX header,
    ///     naming every file that is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ADR-015 assigns this to <see cref="CheckFormat" /> and docs/plan/01 § Licence says
    ///         why the header is worth having at all: Apache-2.0 does not require a per-file header,
    ///         but it "removes all ambiguity for anyone vendoring a single file". Which is precisely
    ///         the case a <c>LICENSE</c> at the repository root cannot serve, because the file
    ///         arrives in somebody else's tree without it.
    ///     </para>
    ///     <para>
    ///         Every file is reported, not the first one. A gate that stops at the failure it found
    ///         turns a five-file omission into five runs of a target that takes minutes.
    ///     </para>
    /// </remarks>
    void CheckLicenceHeaders() {
        var patterns = LicenceHeaderRoots
            .SelectMany(root => LicenceHeaderExtensions.Select(extension => $"{root}/**/*.{extension}"))
            .ToArray();

        // ⚠ Tracked files only, and this is not a refinement of the glob — it is what makes the
        // gate mean the same thing twice. The glob walks the working tree, so it sees whatever
        // build output happens to be lying beside the source: `nuke Docs` writes five .ts files
        // into www/src/generated, which .gitignore covers and no author has ever opened. The gate
        // therefore passed on a clean checkout and failed on any machine that had built the docs,
        // which is the worst failure a gate can have — green in CI, red for whoever runs it next.
        // `git ls-files` is the definition of "authored source" the doc comment above already
        // claims, so ask git rather than adding a path to the exclusions each time one appears.
        var tracked = TrackedFiles();

        var files = RootDirectory
            .GlobFiles(patterns)
            .Where(path => tracked.Contains(path))
            .Where(path => !IsExcludedFromLicenceHeaders(path))
            .ToList();

        // The glob is the part of this that can rot silently: rename a top-level directory and the
        // gate goes on reporting green over a smaller and smaller tree. The floor is deliberately a
        // real number rather than one — four thousand files were in scope when it was written, and
        // a run that finds four hundred has lost something rather than deleted it.
        Assert.True(
            files.Count > 3000,
            $"found only {files.Count} files to check for an SPDX header, which is too few to be "
            + "the whole tree — LicenceHeaderRoots or the exclusions below are wrong."
        );

        var missing = files.Where(path => !HasLicenceHeader(path)).ToList();

        foreach (var path in missing) {
            Log.Error("{File} has no SPDX header.", RootDirectory.GetRelativePathTo(path));
        }

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} file(s) are missing the SPDX header. Add these two lines at the top, "
            + "in the file's own comment syntax:\n"
            + "  SPDX-FileCopyrightText: Copyright (c) Rikarin\n"
            + "  SPDX-License-Identifier: Apache-2.0"
        );

        Log.Information("Checked {Count} files for an SPDX header; none missing.", files.Count);
    }

    /// <summary>Whether a globbed path is one this repository is entitled to put its name on.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>bin/</c>, <c>obj/</c>, <c>artifacts/</c> and <c>node_modules/</c> are build output
    ///         and restored dependencies — git ignores all four, and so does this.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Tools/Vixen.Templates/templates/</c> is the load-bearing exclusion, and it is
    ///         the same one <see cref="CheckArchitecture" /> makes.</b> Those files are not this
    ///         repository's source: they are what <c>dotnet new</c> writes into somebody else's
    ///         directory, and stamping <c>Copyright (c) Rikarin</c> on the first file of a third
    ///         party's game would be a false claim about their code. None of the four shipped
    ///         templates carries the header today, deliberately; a fifth must not either.
    ///     </para>
    ///     <para>
    ///         <c>*.g.cs</c> is excluded because a generated file's header belongs to whatever
    ///         emitted it, and the emitter's own source is checked here like anything else. Most of
    ///         them do carry it — the offline generators under <c>Tools/</c> write it — but
    ///         <c>Vixen.Shaders.Generators</c> is a Roslyn source generator whose output lands in the
    ///         <em>consumer's</em> compilation, and a header claiming Rikarin's copyright over a file
    ///         generated inside a third party's build would be the same false claim as above.
    ///     </para>
    /// </remarks>
    /// <summary>Every file git tracks, as absolute paths.</summary>
    /// <returns>The set, for membership tests.</returns>
    /// <remarks>
    ///     Untracked and ignored files are both absent, which is the point: an ignored file is
    ///     build output and an untracked one is not yet anybody's source. A header is a claim about
    ///     a file somebody may vendor, and neither kind is a file anybody can vendor from here.
    /// </remarks>
    HashSet<AbsolutePath> TrackedFiles() {
        var output = GitTasks.Git("ls-files", RootDirectory, logOutput: false, logInvocation: false);

        return [.. output.Select(line => RootDirectory / line.Text.Trim()).Where(path => path.FileExists())];
    }

    static bool IsExcludedFromLicenceHeaders(AbsolutePath path) {
        var text = path.ToString();

        return text.Contains("/bin/", StringComparison.Ordinal)
            || text.Contains("/obj/", StringComparison.Ordinal)
            || text.Contains("/artifacts/", StringComparison.Ordinal)
            || text.Contains("/node_modules/", StringComparison.Ordinal)
            || text.Contains("/Vixen.Templates/templates/", StringComparison.Ordinal)
            || text.EndsWith(".g.cs", StringComparison.Ordinal);
    }

    /// <summary>Whether both SPDX tags appear near the top of a file.</summary>
    /// <remarks>
    ///     Reads lines lazily and stops after <see cref="LicenceHeaderLines" />, so this costs a
    ///     couple of hundred bytes per file rather than the size of the tree.
    /// </remarks>
    static bool HasLicenceHeader(AbsolutePath path) {
        var header = string.Join('\n', File.ReadLines(path).Take(LicenceHeaderLines));

        return LicenceHeaderTags.All(tag => header.Contains(tag, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Publishes every runtime assembly ahead of time, with all of them rooted, and fails on any
    ///     trim or AOT warning.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Doc 14 puts this in Phase 3 and says why: iOS is NativeAOT-only, and every plan that
    ///         defers it discovers in month 30 that some subsystem needs reflection and pays for it
    ///         ten times over. This is that discovery, made cheap and made repeatable.
    ///     </para>
    ///     <para>
    ///         The subject is <c>Tools/Vixen.AotProbe</c>, which roots each runtime assembly rather
    ///         than calling into it — ILC analyses what is reachable, so a probe that constructs a
    ///         few types proves those types clean and says nothing about the rest.
    ///     </para>
    ///     <para>
    ///         Published for the host's own runtime identifier. ILC cross-compiles poorly and the
    ///         analysis that matters here — what needs reflection, what needs dynamic code — is the
    ///         same on every target, which is why one leg per operating system in CI is coverage
    ///         rather than three-thirds of one check.
    ///     </para>
    /// </remarks>
    Target CheckAot => definition => definition
        .Description("Fails if any runtime assembly cannot be published ahead of time")
        .DependsOn(Restore)
        .Executes(() =>
            DotNetPublish(settings => settings
                .SetProject(RootDirectory / "Tools" / "Vixen.AotProbe" / "Vixen.AotProbe.csproj")
                .SetConfiguration(Configuration.Release)
                .SetRuntime(RuntimeInformation.RuntimeIdentifier)
                .SetOutput(ArtifactsDirectory / "aot")
            )
        );

    /// <summary>
    ///     The same gate for iOS, which is the target the phase's exit criterion is actually about.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Separate from <see cref="CheckAot" /> and separate from the solution, because a
    ///         <c>net10.0-ios</c> project cannot even be evaluated without the <c>ios</c> workload —
    ///         putting it in <c>Vixen.slnx</c> would break <c>dotnet build</c> for every developer
    ///         and every CI leg that is not a Mac with Xcode. The cost is that <c>CheckFormat</c>
    ///         does not see it, which is two files.
    ///     </para>
    ///     <para>
    ///         Nothing is signed: the question is what compiles, not what can be installed on a
    ///         device.
    ///     </para>
    /// </remarks>
    Target CheckAotIos => definition => definition
        .Description("Fails if the runtime assemblies cannot be published for iOS ahead of time")
        .Requires(() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX))

        // The probe links MoltenVK, which is pinned and checksummed rather than committed. Depending
        // on the restore rather than assuming it is what lets a fresh clone run this target.
        .DependsOn(RestoreNativeDeps)
        .Executes(() =>
            DotNetPublish(settings => settings
                .SetProject(RootDirectory / "Tools" / "Vixen.AotProbe.iOS" / "Vixen.AotProbe.iOS.csproj")
                .SetConfiguration(Configuration.Release)
            )
        );

    /// <summary>
    ///     Builds the two mobile platform assemblies, which the solution cannot contain.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Vixen.Platform.iOS</c> and <c>Vixen.Platform.Android</c> target
    ///         <c>net10.0-ios</c> and <c>net10.0-android</c>, and a project with either target cannot
    ///         be <em>evaluated</em> without its workload — not built, evaluated, so its presence in
    ///         <c>Vixen.slnx</c> would break <c>dotnet build</c> outright for a developer or a CI leg
    ///         that lacks it. iOS additionally requires macOS and Xcode. So they are outside the
    ///         solution, exactly as <c>Tools/Vixen.AotProbe.iOS</c> is and for the same reason.
    ///     </para>
    ///     <para>
    ///         <b>The cost is real and is worth stating.</b> Neither assembly is seen by
    ///         <see cref="Test" />, <see cref="CheckFormat" />, <see cref="CheckApi" /> or
    ///         <see cref="Pack" />. That is why the parts of them that can be tested off a device —
    ///         the touch bookkeeping and the lifecycle state machine — live in
    ///         <c>Vixen.Platform</c> instead, where the solution does see them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This paragraph named <see cref="CheckArchitecture" /> too, and that was wrong.</b>
    ///         That gate globs <c>Platform/**/*.csproj</c> and <c>Samples/**/*.csproj</c> rather than
    ///         reading the solution, so it is the one gate that <em>does</em> evaluate both mobile
    ///         projects and the sample heads — which makes it the only layer check standing between
    ///         an out-of-solution head and a reference nobody would allow in the solution.
    ///         <see cref="PublishWeb" />'s own remarks have said so all along; these two disagreed
    ///         with it.
    ///     </para>
    ///     <para>
    ///         Android builds anywhere the workload is installed; the iOS half is skipped elsewhere
    ///         rather than failing, because a Linux CI leg not building an iOS assembly is the
    ///         expected outcome and not a broken build.
    ///     </para>
    /// </remarks>
    Target CompileMobile => definition => definition
        .Description("Builds the iOS and Android platform assemblies, which cannot live in the solution")
        .Executes(() => {
                DotNetBuild(settings => settings
                    .SetProjectFile(RootDirectory / "Platform" / "Vixen.Platform.Android" / "Vixen.Platform.Android.csproj")
                    .SetConfiguration(Configuration)
                );

                DotNetBuild(settings => settings
                    .SetProjectFile(RootDirectory / "Samples" / "01-HelloTriangle.Android" / "HelloTriangle.Android.csproj")
                    .SetConfiguration(Configuration)
                );

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
                    Log.Information("Skipping Vixen.Platform.iOS: it needs macOS and Xcode.");
                    return;
                }

                DotNetBuild(settings => settings
                    .SetProjectFile(RootDirectory / "Platform" / "Vixen.Platform.iOS" / "Vixen.Platform.iOS.csproj")
                    .SetConfiguration(Configuration)
                );

                // The sample heads too, because a platform assembly that compiles and an application
                // that runs are different claims — and the second is the one the phase's exit
                // criterion makes.
                DotNetBuild(settings => settings
                    .SetProjectFile(RootDirectory / "Samples" / "01-HelloTriangle.iOS" / "HelloTriangle.iOS.csproj")
                    .SetConfiguration(Configuration)
                );
            }
        );

    /// <summary>
    ///     Every project in the tree that targets <c>net10.0-browser</c>. Three, and the list is the
    ///     answer to a <c>grep</c> rather than to a memory — see <see cref="CompileWeb" />.
    /// </summary>
    IEnumerable<AbsolutePath> BrowserProjects => [
        RootDirectory / "Platform" / "Vixen.Platform.Web" / "Vixen.Platform.Web.csproj",
        RootDirectory / "Platform" / "Vixen.Graphics.WebGPU.Browser" / "Vixen.Graphics.WebGPU.Browser.csproj",
        RootDirectory / "Platform" / "Vixen.Audio.Backend.WebAudio" / "Vixen.Audio.Backend.WebAudio.csproj",

        // ⚠ Under Core/ and not Platform/, because it is a transport rather than a platform
        // binding — Vixen.Platform.Web's README says where it belongs, and it ships no JavaScript
        // at all. It is here for the reason the list exists: the solution cannot contain a
        // net10.0-browser project, so a browser project that is not named here is built by nothing.
        RootDirectory / "Core" / "Vixen.Net.Transport.WebSocket.Browser"
        / "Vixen.Net.Transport.WebSocket.Browser.csproj"
    ];

    /// <summary>
    ///     Builds the browser-targeted assemblies, which the solution cannot contain either.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Three projects target <c>net10.0-browser</c>: <c>Vixen.Platform.Web</c>,
    ///         <c>Vixen.Graphics.WebGPU.Browser</c> and <c>Vixen.Audio.Backend.WebAudio</c>. Exactly
    ///         as with the two mobile targets above, a project with that target cannot be
    ///         <em>evaluated</em> without the <c>wasm-tools</c> workload, so their presence in
    ///         <c>Vixen.slnx</c> would break <c>dotnet build</c> for anyone who has not installed it.
    ///         That is the whole reason this target exists.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It built one of the three, and its own description said "the browser
    ///         assemblies".</b> Only <c>Vixen.Audio.Backend.WebAudio</c> was named here, which is why
    ///         it finished in six seconds; <c>Vixen.Graphics.WebGPU.Browser.csproj</c> even carried a
    ///         comment saying <em>"nuke Compile does not build it and neither does CI today"</em>,
    ///         and nothing contradicted it. A gate that covers a quarter of its subject and reports
    ///         green is worse than no gate, because the green is read as coverage.
    ///     </para>
    ///     <para>
    ///         <c>Vixen.Platform.Web.Tests</c> is deliberately <em>not</em> in this list and is not
    ///         an omission: it targets plain <c>net10.0</c> and lives in <c>Vixen.slnx</c>, so
    ///         <see cref="Compile" /> and <see cref="Test" /> already see it. What it can assert is
    ///         limited by that TFM — see <c>BrowserModuleUrlTests</c>, which reaches the browser-only
    ///         constants by linking their source rather than by referencing the assembly.
    ///     </para>
    ///     <para>
    ///         <b>The cost is the same one <see cref="CompileMobile" /> names, and compiling is the
    ///         floor rather than the ceiling.</b> None of the three is seen by <see cref="Test" />,
    ///         <see cref="CheckFormat" />, <see cref="CheckApi" /> or <see cref="Pack" /> —
    ///         <see cref="CheckArchitecture" /> does see them, because it globs
    ///         <c>Platform/**/*.csproj</c> rather than reading the solution — and a compiler cannot
    ///         see a URL that will not resolve at run time, an emcc flag that
    ///         was never applied, or a file that is not published where it is fetched from. Two
    ///         further gates cover those: <c>BrowserModuleUrlTests</c> under <see cref="Test" />, and
    ///         <see cref="PublishWeb" />, which puts a real head through the SDK.
    ///     </para>
    /// </remarks>
    Target CompileWeb => definition => definition
        .Description("Builds the three browser assemblies, which cannot live in the solution")
        .Executes(() => {
                foreach (var project in BrowserProjects) {
                    Log.Information("Building {Project}", project.Name);

                    DotNetBuild(settings => settings
                        .SetProjectFile(project)
                        .SetConfiguration(Configuration)
                    );
                }
            }
        );

    AbsolutePath WebPublishDirectory => ArtifactsDirectory / "web";

    /// <summary>
    ///     Publishes a browser head and checks that the page it produces is loadable — which is a
    ///     different claim from "the assemblies compile", and the one three shipped defects broke.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="CompileWeb" /> builds three <em>libraries</em>, and a library never
    ///         evaluates <c>build/Vixen.Platform.Web.props</c> or <c>.targets</c> — those apply to
    ///         the head that consumes them, so the emcc relink, the WebGL2 assertion, the trimming
    ///         profile and the static-web-asset layout are all untouched by a compile. Every one of
    ///         the three defects the web-head spike found lives in exactly that gap:
    ///         <c>WasmMainJSPath</c> at the project root is not published at all, the
    ///         <c>vixen-*.js</c> content files have to arrive at the <em>site root</em> for the
    ///         default module URL to resolve, and <c>dotnet.run()</c> tears the runtime down.
    ///     </para>
    ///     <para>
    ///         <b>It answers the first two as questions about a directory, and the third only as far
    ///         as a file's shape — which is the difference that shapes the whole problem.</b> Where
    ///         a file landed is something a publish can see. Whether the runtime is still alive after
    ///         <c>Main</c> returns is not: it is a question about what happens once the page loads,
    ///         and only something that loads the page can answer it. So the third check reads the
    ///         published <c>main.js</c> and requires <c>runMain(</c> rather than <c>.run(</c> — the
    ///         exact line the defect was — and is honest that this is a regression guard on the
    ///         boot script and not a live-frame-loop assertion. The real one is still the Playwright
    ///         leg doc 10 asks for, still owed, and which must drive a real browser over CDP rather
    ///         than <c>--dump-dom</c>: that mode never fires <c>requestAnimationFrame</c>, so a leg
    ///         built on it would report a live frame loop as dead.
    ///     </para>
    ///     <para>
    ///         The subject is <c>Tools/Vixen.WebProbe</c>, the browser head this repository owns. It
    ///         was <c>docs/plan/spikes/web-head</c> until that spike's head was promoted — a build
    ///         target depending on a spike is not something to carry to a release — and the spike is
    ///         a document again, keeping the findings this target's messages cite. The probe draws
    ///         nothing and says so; a first web <em>sample</em> is a larger, separate thing that
    ///         doc 14 Phase 10 still owes, and is not what this gate needs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The head is outside <c>Vixen.slnx</c> and cannot be otherwise</b>, for the same
    ///         reason as the three libraries above and as <c>Tools/Vixen.AotProbe.iOS</c>:
    ///         <c>net10.0-browser</c> needs the <c>wasm-tools</c> workload to be evaluated at all.
    ///         So promotion buys a first-class project with a README and a name, and it does not buy
    ///         coverage from <see cref="Test" />, <see cref="CheckFormat" />, <see cref="CheckApi" />
    ///         or <see cref="Pack" />. <see cref="CheckArchitecture" /> does see it, because that
    ///         gate globs <c>Tools/**/*.csproj</c> rather than reading the solution.
    ///     </para>
    ///     <para>
    ///         The assertions after the publish are the <em>other half</em> of the invariant
    ///         <c>BrowserModuleUrlTests</c> checks. That test knows the constants — it links their
    ///         source — and asserts each resolves out of <c>_framework/</c> to the site root. It
    ///         cannot know whether the SDK actually puts the file there. This can, and does, by
    ///         looking at what was published.
    ///     </para>
    ///     <para>
    ///         It depends on <see cref="CompileWeb" /> and not the other way round, deliberately. The
    ///         compile is seconds and this is a minute or more, because it relinks with emcc; a
    ///         developer who wants the cheap gate can still have it on its own, and a broken binding
    ///         is reported by the target whose subject it is rather than as a link failure later.
    ///     </para>
    /// </remarks>
    Target PublishWeb => definition => definition
        .Description("Publishes a browser head and checks the page it produces is loadable")

        // So that a compile error in a binding is reported as a compile error, in the target whose
        // subject it is, rather than as an emcc link failure two minutes later. The head references
        // two of the three browser projects; this puts the third in front of the publish too.
        .DependsOn(CompileWeb)
        .Produces(WebPublishDirectory / "**")
        .Executes(() => {
                var head = RootDirectory / "Tools" / "Vixen.WebProbe" / "Vixen.WebProbe.csproj";

                if (!head.FileExists()) {
                    Assert.Fail(
                        $"the web head this target publishes is not at '{head}'. If it moved, point "
                        + "PublishWeb at wherever it went; if it was deleted, this gate went with it "
                        + "and the three defects in docs/plan/spikes/web-head/RESULT.md are "
                        + "uncovered again."
                    );
                }

                WebPublishDirectory.CreateOrCleanDirectory();

                DotNetPublish(settings => settings
                    .SetProject(head)
                    .SetConfiguration(Configuration.Release)
                    .SetOutput(WebPublishDirectory)
                );

                // The site root, which is not the output root: Microsoft.NET.Sdk.WebAssembly puts
                // the page and everything fetched by URL under wwwroot/, and the runtime under
                // wwwroot/_framework/. Both halves of the module-URL invariant are about that
                // relationship, so both are read from here rather than assumed.
                var siteRoot = WebPublishDirectory / "wwwroot";

                // index.html and main.js: defect 3. A WasmMainJSPath outside wwwroot/ is not a
                // static web asset, so the page 404s on its own entry point and nothing happens —
                // with no build error, because nothing was wrong with the build.
                //
                // vixen-platform.js and vixen-webgpu.js: defect 1's second half. The default module
                // URL is "../", resolved against the runtime's module in _framework/, and that is
                // only correct while these land *here*. If a future SDK or a packaging change moves
                // them, the constant becomes wrong and this is what says so.
                foreach (var required in new[] { "index.html", "main.js", "vixen-platform.js", "vixen-webgpu.js" }) {
                    Assert.FileExists(
                        siteRoot / required,
                        $"the published head has no '{required}' at its site root. See "
                        + "docs/plan/spikes/web-head/RESULT.md; a page missing any of these loads "
                        + "and then does nothing, which is the failure this target exists to catch."
                    );
                }

                Assert.DirectoryExists(
                    siteRoot / "_framework",
                    "the published head has no _framework/ directory, so there is no runtime and "
                    + "nothing for the browser bindings' relative module URLs to resolve against."
                );

                // Defect 2, as far as a published file can show it. `dotnet.run()` tears the runtime down
                // the moment Main returns and every requestAnimationFrame callback WebFrameLoop
                // registered dies with it, which the page reports as "Assert failed: .NET runtime
                // already exited with 0". A head must call `runtime.runMain()`.
                //
                // ⚠ This is a regression guard on the boot script's shape and NOT an assertion that
                // the frame loop lives. Only a loaded page can make that one — the Playwright leg
                // above. It is here because the check is free and the defect is a single call.
                //
                // ⚠ Comment lines are dropped first, and that is not tidiness. The head's own
                // main.js explains the rule in a comment that says `dotnet.run()`, so the first
                // version of this check failed on the correct file — which is at least a gate that
                // reported the truth about itself the first time it ran.
                var bootScript = (siteRoot / "main.js")
                    .ReadAllLines()
                    .Select(line => line.TrimStart())
                    .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
                    .Where(line => !line.StartsWith('*'))
                    .ToList();

                Assert.True(
                    bootScript.Any(line => line.Contains("runMain(", StringComparison.Ordinal))
                    && !bootScript.Any(line => line.Contains(".run(", StringComparison.Ordinal)),
                    "the published head's main.js does not boot with runtime.runMain(). dotnet.run() "
                    + "exits the runtime when Main returns, killing the frame loop — see defect 2 in "
                    + "docs/plan/spikes/web-head/RESULT.md. This checks the call and not the loop; "
                    + "only a browser can check the loop."
                );

                Log.Information("Published a loadable head to {Directory}", WebPublishDirectory);
            }
        );

    Target Pack => definition => definition
        .Description("Produces the NuGet packages")
        .DependsOn(Test)
        .Produces(PackagesDirectory / "*.nupkg")
        .Executes(() => {
                PackagesDirectory.CreateOrCleanDirectory();

                DotNetPack(settings => settings
                    .SetProject(Solution)
                    .SetConfiguration(Configuration)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetOutputDirectory(PackagesDirectory)
                );

                CheckStyleGenIsShippable();
                CheckCliIsShippable();
            }
        );

    /// <summary>
    ///     Asserts that <c>Vixen.Ui.Styling.Utilities</c> ships a <c>tools/</c> the utility build step
    ///     can actually start from.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the one failure in the tree that shipped in every package and could only be
    ///     found by extracting one.</b> <c>Tools/Vixen.StyleGen</c> is packed by path into this
    ///     package's <c>tools/</c> and run as <c>dotnet "…/Vixen.StyleGen.dll"</c> by
    ///     <c>buildTransitive/Vixen.Ui.Styling.Utilities.targets</c>. For a while what was packed was
    ///     the entry point alone — no <c>Vixen.Ui.Styling.Utilities.dll</c>, no <c>.deps.json</c> — so
    ///     the first line of <c>Main</c> threw <c>FileNotFoundException</c> out of an <c>Exec</c> on
    ///     the first build of the first project that referenced the package. Nothing in the tree could
    ///     see it: every in-repo project takes the third rung of the tool-path ladder and runs
    ///     <c>bin/</c> directly, so the packed copy is the one arrangement no build here exercises.
    ///     <para>
    ///         ⚠ <b>Conditional on the package having been produced, and that is deliberate rather
    ///         than lenient.</b> The pack above is solution-wide, so the file is there on any ordinary
    ///         run; a filtered pack that did not produce it should not fail on a package it was not
    ///         asked to build. What must not happen is the file being produced and being empty.
    ///     </para>
    ///     <para>
    ///         The four names are the four that were missing, not a sample: the host probes flat, so
    ///         it needs the implementation assembly beside the entry point, and it will not start at
    ///         all without both JSON files. A closure check rather than a launch — running the tool
    ///         needs a project to point it at, and the failure being guarded is a file that is not
    ///         in the package.
    ///     </para>
    /// </remarks>
    void CheckStyleGenIsShippable() {
        var package = PackagesDirectory.GlobFiles("Vixen.Ui.Styling.Utilities.*.nupkg")
            .FirstOrDefault(file => !file.Name.EndsWith(".symbols.nupkg", StringComparison.Ordinal));

        if (package is null) {
            Log.Information("No Vixen.Ui.Styling.Utilities package was produced; skipping the tools/ check");
            return;
        }

        using var archive = ZipFile.OpenRead(package);

        var entries = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var required in new[] {
                     "tools/Vixen.StyleGen.dll",
                     "tools/Vixen.StyleGen.deps.json",
                     "tools/Vixen.StyleGen.runtimeconfig.json",
                     "tools/Vixen.Ui.Styling.Utilities.dll",
                     "buildTransitive/Vixen.Ui.Styling.Utilities.targets",
                 }) {
            Assert.True(
                entries.Contains(required),
                $"{package.Name} does not contain {required}. The utility build step is packed into "
                + "this package's tools/ and started as `dotnet tools/Vixen.StyleGen.dll` by the "
                + "targets in buildTransitive/ — a tools/ missing any of these throws out of an Exec "
                + "on the consumer's first build. Packing has to follow a solution build, because the "
                + "tool is packed by path from Tools/Vixen.StyleGen/bin/$(Configuration)/net10.0/."
            );
        }

        Log.Information("{Package} ships a startable tools/ for the utility build step", package.Name);
    }

    /// <summary>
    ///     Asserts that <c>Vixen.Sdk</c> ships a CLI, by extracting the package and starting it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A launch and not a file list, and the difference is the whole reason this is worth
    ///         more than its sibling above.</b> <see cref="CheckStyleGenIsShippable" /> can only name
    ///         the four files that were missing, because running that tool needs a project to point it
    ///         at; <c>vixen --version</c> needs nothing at all. So the question asked here is the one
    ///         that actually matters — does the host start this thing — and it is asked of the
    ///         assembly closure the package really contains rather than of a list somebody remembered
    ///         to keep up to date. The <c>Vixen.StyleGen</c> failure was exactly a missing dependency
    ///         that no list named.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Conditional on the package having been produced, for the reason its sibling gives:</b>
    ///         a filtered pack should not fail on a package it was not asked to build. What must not
    ///         happen is the package being produced with an empty <c>tools/</c> — which is what a pack
    ///         that ran before the solution build produces, because the CLI is packed by path.
    ///     </para>
    ///     <para>
    ///         The extraction is ~170 MB and the run is a fraction of a second. That size is the cost
    ///         of the decision recorded in <c>Tools/Vixen.Sdk/Vixen.Sdk.csproj</c>: one portable copy
    ///         carrying every RID's natives, so that the same package serves a developer's laptop and
    ///         an Alpine CI container without a hand-maintained list of runtime identifiers.
    ///     </para>
    /// </remarks>
    void CheckCliIsShippable() {
        var package = PackagesDirectory.GlobFiles("Vixen.Sdk.*.nupkg")
            .FirstOrDefault(file => !file.Name.EndsWith(".symbols.nupkg", StringComparison.Ordinal));

        if (package is null) {
            Log.Information("No Vixen.Sdk package was produced; skipping the tools/ check");
            return;
        }

        var extracted = TemporaryDirectory / "vixen-sdk-tools";
        extracted.CreateOrCleanDirectory();
        ZipFile.ExtractToDirectory(package, extracted);

        var tool = extracted / "tools" / "vixen.dll";

        Assert.True(
            tool.FileExists(),
            $"{package.Name} has no tools/vixen.dll. The CLI is packed by path from "
            + "Tools/Vixen.Cli/bin/$(Configuration)/net10.0/, so packing has to follow a solution "
            + "build — and without it every consumer falls through to `dotnet vixen`, a tool they "
            + "have to install themselves and which is then free to be a different version from "
            + "these targets."
        );

        // ⚠ Started the way build/Vixen.Sdk.targets starts it — `dotnet` plus the assembly — because
        // the apphost beside it is deliberately not packed: it is native, and built for whichever
        // machine ran the build.
        DotNet($"\"{tool}\" --version", workingDirectory: extracted);

        Log.Information("{Package} ships a CLI that starts from its packed layout", package.Name);
    }
}
