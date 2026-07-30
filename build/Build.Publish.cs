// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
///     The editor as something a person who did not build it can run: a self-contained, per-runtime
///     publish of <c>Vixen.Editor.App</c>.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § Targets asks for "per-RID single-file publish of `Vixen.Editor.App`;
///         `.app` bundle + `.dmg` on macOS, AppImage on Linux, MSI/zip on Windows". This is the
///         first half. The packaging half — and the <c>Sign</c> and <c>Notarize</c> that follow it
///         in the same graph — is not here, because a bundle nobody has signed is not a shipping
///         step, it is a directory with a different name.
///     </para>
///     <para>
///         <b>Not trimmed, and not NativeAOT.</b> Both are deliberate and both are decided in
///         <c>Vixen.Editor.App.csproj</c> rather than here: plugin assemblies load into an
///         <c>AssemblyLoadContext</c> for unloadability, which is the one place in the codebase
///         where runtime reflection is required rather than merely allowed. A trimmed editor links
///         and starts and then fails to load the first plugin, which is the worst shape a build
///         setting can fail in. Single-file is what doc 12 asks for and is orthogonal to both.
///     </para>
///     <para>
///         <b>What this cannot do yet is make a redistributable macOS build.</b> The published tree
///         resolves Vulkan through <c>NativeLibraries</c>, which looks in the application's own
///         <c>runtimes/&lt;rid&gt;/native/</c> before anything else — so staging a loader there is
///         what turns "runs on the machine that built it" into "runs". No desktop Vulkan loader is
///         pinned in <c>build/native-dependencies.json</c> today (its MoltenVK entries are the iOS
///         static archives), so the loader can only come from the host, and only a host-runtime
///         publish may take it. Every case where nothing was staged is logged rather than left for
///         the first person to run the artefact on a clean machine.
///     </para>
/// </remarks>
partial class Build {
    [Parameter(
        "Runtime identifiers to publish the editor for, space-separated — defaults to this machine's"
    )]
    readonly string[] EditorRuntimes = [];

    /// <summary>
    ///     Whether to put a Vulkan loader in the published tree.
    /// </summary>
    /// <remarks>
    ///     On by default because an editor that cannot create a device is not a published editor.
    ///     Turned off with <c>--stage-vulkan false</c> for the case where the tree is being handed
    ///     to a packaging step that supplies its own — which is what a signed <c>.app</c> will do,
    ///     since a library copied in after signing invalidates the signature.
    /// </remarks>
    [Parameter("Stage a Vulkan loader into the published tree (default: true)")]
    readonly bool StageVulkan = true;

    /// <summary>
    ///     Whether to run the published editor for five frames before calling the publish good.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The same <c>--frames N</c> flag <c>Samples/01</c> introduced and for the same reason:
    ///         it is the difference between "MSBuild wrote some files" and "this starts, presents
    ///         and stops". Only a host-runtime publish can be smoke-tested at all — a cross-
    ///         published tree has no executable this machine can run.
    ///     </para>
    ///     <para>
    ///         <b>Off by default, which is not the obvious choice and is a measured one.</b> On
    ///         macOS the editor's apphost creates a window when a shell runs it and aborts when
    ///         <i>anything else</i> does — <c>SDL could not create a window: Failed to load Vulkan
    ///         Portability library</c>, exit 134, reproducibly, under <c>sh -c</c>, <c>env</c> and
    ///         <c>nohup</c> alike, and therefore under a build script. It is not this target's
    ///         doing: the plain <c>bin/</c> apphost does exactly the same, published or not, staged
    ///         or not, and only <c>dotnet run</c> — which reaches the app through the muxer rather
    ///         than the apphost — is unaffected. A gate that fails on a good artefact is worse than
    ///         no gate, so this is opt-in with <c>--publish-smoke true</c> until that is understood.
    ///     </para>
    /// </remarks>
    [Parameter("Run the published editor for five frames as a smoke test (default: false — see remarks)")]
    readonly bool PublishSmoke;

    AbsolutePath PublishDirectory => ArtifactsDirectory / "publish";

    AbsolutePath EditorProjectFile =>
        RootDirectory / "Editor" / "Vixen.Editor.App" / "Vixen.Editor.App.csproj";

    /// <summary>The runtime identifier this build is running as — <c>osx-arm64</c>.</summary>
    static string HostRuntime => RuntimeInformation.RuntimeIdentifier;

    Target PublishEditor => definition => definition
        .Description("Publishes the editor per runtime identifier into artifacts/publish/<rid>")
        // Doc 12's graph puts this after `Test`, which is right for CI and heavy for a developer
        // who only wants the artefact. `--skip Test` is the escape hatch, and naming it here is
        // cheaper than everyone rediscovering it.
        .DependsOn(Test)
        .Produces(PublishDirectory / "**")
        .Executes(() => {
                Assert.FileExists(EditorProjectFile);

                if (Configuration != Configuration.Release) {
                    Log.Warning(
                        "Publishing {Configuration}. A build for anyone but yourself wants --configuration Release.",
                        Configuration
                    );
                }

                var runtimes = EditorRuntimes.Length > 0 ? EditorRuntimes : [HostRuntime];

                foreach (var runtime in runtimes) {
                    PublishEditorFor(runtime);
                }
            }
        );

    void PublishEditorFor(string runtime) {
        var output = PublishDirectory / runtime;

        // Cleaned rather than published over. A publish is a description of one build, and a
        // directory that still holds an assembly the current one no longer produces is a tree that
        // loads it — which is a stale plugin or a stale backend, found at run time.
        output.CreateOrCleanDirectory();

        Log.Information("Publishing the editor for {Runtime} into {Output}", runtime, output);

        DotNetPublish(settings => settings
            .SetProject(EditorProjectFile)
            .SetConfiguration(Configuration)
            .SetRuntime(runtime)
            .SetSelfContained(true)
            // Doc 12 asks for single-file. It costs nothing here — the resolver already reads
            // AppContext.BaseDirectory rather than Assembly.Location, which is the assumption
            // single-file breaks and the reason NativeLibraries exists (R11).
            .SetPublishSingleFile(true)
            // Emphatically not. See the type's remarks: the editor loads plugins by reflection.
            .SetPublishTrimmed(false)
            // The reference documentation is for someone writing against these assemblies, not for
            // someone running them: 49 XML files, two fifths of the tree's file count, for nothing
            // a published editor reads.
            .SetProperty("PublishDocumentationFile", false)
            .SetProperty("PublishReferencesDocumentationFiles", false)
            .SetOutput(output)
            // No --no-restore, deliberately. `Restore` restored the solution for this machine;
            // a RID-specific publish needs the runtime pack for *that* RID, which it has not.
        );

        if (StageVulkan) {
            StageVulkanFor(runtime, output);
        }

        SmokeTest(runtime, output);
    }

    /// <summary>The executable's name in a published tree.</summary>
    /// <remarks><c>VixenEditor</c> is the project's <c>AssemblyName</c>, not its file name.</remarks>
    static string EditorExecutable(string runtime) =>
        runtime.StartsWith("win", StringComparison.Ordinal) ? "VixenEditor.exe" : "VixenEditor";

    /// <summary>
    ///     The native libraries a published tree needs, per operating system, most important first.
    /// </summary>
    /// <remarks>
    ///     These are file names rather than library names because a published tree holds files. The
    ///     versioned soname is the real file and the undecorated one is a development symlink — see
    ///     <c>NativeLibraryNames</c>, which resolves the other direction at run time.
    /// </remarks>
    static IReadOnlyList<string> VulkanFiles(string runtime) =>
        runtime.StartsWith("win", StringComparison.Ordinal) ? ["vulkan-1.dll"]
        : runtime.StartsWith("osx", StringComparison.Ordinal) ? ["libvulkan.1.dylib", "libMoltenVK.dylib"]
        : ["libvulkan.so.1"];

    /// <summary>
    ///     Where a Vulkan loader can be copied from, most trustworthy first.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>artifacts/native/&lt;rid&gt;/</c> comes first and is the only source that is
    ///         pinned, checksummed and reproducible — it is what <see cref="RestoreNativeDeps" />
    ///         writes. Nothing puts a desktop loader there today, and the ordering is what makes
    ///         adding one to <c>build/native-dependencies.json</c> a manifest edit rather than a
    ///         manifest edit plus a change here.
    ///     </para>
    ///     <para>
    ///         The rest are the host's, and are offered only for a host-runtime publish: a
    ///         <c>libvulkan.so.1</c> from this Mac is not a Linux artefact, and copying it into one
    ///         produces a tree that fails at <c>dlopen</c> on the target rather than here.
    ///     </para>
    /// </remarks>
    IEnumerable<AbsolutePath> VulkanSources(string runtime) {
        yield return NativeDirectory / runtime;

        if (runtime != HostRuntime) {
            yield break;
        }

        // The same list VulkanLoader.Prefixes searches at run time, and for the same reasons:
        // VULKAN_SDK first because someone who set it meant it, then the package managers —
        // macOS's dynamic linker does not search /opt/homebrew/lib, which is where Homebrew puts
        // everything on Apple silicon.
        if (Environment.GetEnvironmentVariable("VULKAN_SDK") is { Length: > 0 } sdk) {
            yield return (AbsolutePath)sdk / (OperatingSystem.IsWindows() ? "Bin" : "lib");
        }

        if (OperatingSystem.IsWindows()) {
            yield break;
        }

        yield return (AbsolutePath)"/opt/homebrew/lib";
        yield return (AbsolutePath)"/usr/local/lib";
        yield return (AbsolutePath)"/usr/lib";
    }

    /// <summary>Puts the Vulkan libraries this runtime needs where the resolver looks first.</summary>
    void StageVulkanFor(string runtime, AbsolutePath output) {
        // runtimes/<rid>/native/ — the layout NuGet produces, which is why NativeLibraries reads it
        // and why a library placed here beats the machine's own copy.
        var destination = output / "runtimes" / runtime / "native";
        var sources = VulkanSources(runtime).ToList();
        var wanted = VulkanFiles(runtime);
        var staged = new List<string>();

        foreach (var file in wanted) {
            if (sources.Select(source => source / file).FirstOrDefault(candidate => candidate.FileExists())
                is not { } found) {
                continue;
            }

            destination.CreateDirectory();
            File.Copy(found, destination / file, overwrite: true);
            staged.Add(file);
            Log.Information("  {Rid}/{File} staged from {Source}", runtime, file, found);
        }

        // The loader is wanted[0]; anything after it is an implementation behind it.
        if (staged.Contains(wanted[0], StringComparer.Ordinal)) {
            return;
        }

        // Whether an unstaged loader is a problem is entirely a question of which platform this is
        // for, so it is reported as two different things. Windows gets vulkan-1.dll from the
        // graphics driver and Linux gets libvulkan.so.1 from the distribution: on both, shipping a
        // loader is a choice about which version runs, and not shipping one is the normal case.
        // macOS has no system Vulkan at all, so the same silence there means the artefact starts on
        // the machine that built it and on no other.
        var message = "No Vulkan loader was staged for {Runtime}. Looked for {Files} in:\n  {Sources}";

        if (runtime.StartsWith("osx", StringComparison.Ordinal)) {
            Log.Warning($"{message}\nmacOS has no system Vulkan, so this tree is not redistributable as it stands.",
                runtime,
                string.Join(", ", wanted),
                string.Join("\n  ", sources)
            );
        } else {
            Log.Information($"{message}\nThe driver or the distribution supplies one there, which is the usual case.",
                runtime,
                string.Join(", ", wanted),
                string.Join("\n  ", sources)
            );
        }

        if (runtime != HostRuntime) {
            Log.Information(
                "{Runtime} is a cross-publish, so only artifacts/native/{Runtime} was eligible — "
                + "a host library is not an artefact for another platform.",
                runtime,
                runtime
            );
        }
    }

    /// <summary>Runs the thing that was just published, which is the only proof it publishes.</summary>
    void SmokeTest(string runtime, AbsolutePath output) {
        var executable = output / EditorExecutable(runtime);

        Assert.FileExists(executable);

        if (!PublishSmoke || runtime != HostRuntime) {
            Log.Information(
                "Published {Executable}. Not smoke-tested ({Reason}).",
                executable,
                PublishSmoke ? $"{runtime} is not this machine's runtime" : "--publish-smoke is off by default"
            );

            return;
        }

        // Working directory set to the tree, so what is exercised is the published layout — the
        // resolver reads AppContext.BaseDirectory, and a run from anywhere else would be testing
        // the same binary against a different set of neighbours.
        ProcessTasks.StartProcess(executable, "--frames 5", output)
            .AssertZeroExitCode();

        Log.Information("Published and smoke-tested {Executable}", executable);
    }
}
