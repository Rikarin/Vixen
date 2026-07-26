// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
