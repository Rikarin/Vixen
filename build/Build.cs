// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
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
        .Description("Fails if any file deviates from .editorconfig")
        .DependsOn(Restore)
        .Executes(() => {
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
            }
        );
}
