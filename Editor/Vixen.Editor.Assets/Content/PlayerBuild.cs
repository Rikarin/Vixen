// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vixen.Editor.Assets.Content;

/// <summary>How a target is published: what to build it as, and what comes out.</summary>
/// <param name="Rid">The runtime identifier, or empty where the target framework decides.</param>
/// <param name="Framework">The target framework, or empty to leave the project's own alone.</param>
/// <param name="Artefact">What the output directory will hold, for the person reading the log.</param>
/// <param name="Runnable">Whether the host can launch the result.</param>
public readonly record struct TargetShape(string Rid, string Framework, string Artefact, bool Runnable);

/// <summary>
///     Turning a project into an application: <c>dotnet publish</c>, and what to tell it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The point is that it is one command, not that it is a new build system.</b>
///         <a href="../../../docs/plan/17-app-heads-and-shipping.md">Doc 17</a> asks for content build
///         + <c>dotnet publish</c> + platform packaging behind a single verb, so that the ordering —
///         and the fact that content is stale unless something rebuilt it — is not a thing every
///         developer has to know. Underneath it is the ordinary .NET publish, deliberately: a project
///         that cannot be built by <c>dotnet</c> alone is a project this tool has captured.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in <c>Tools/Vixen.Cli</c>, which is where it was, and for
///         <see cref="ProjectWorkspace" />'s reason.</b> Doc 20's B7 asks for a Build Settings window
///         "over <c>Tools/Vixen.Cli</c>'s existing calls", and the only way for a window to be over
///         those calls rather than beside them is for the calls to live somewhere both heads can
///         reach. Two orchestrations over one <c>dotnet publish</c> drift, and the way this
///         particular drift shows up is the editor's Build and Run producing a different artefact
///         from <c>vixen build</c> for the same project — which reads as a machine problem for as
///         long as it takes somebody to compare two output directories by hand.
///     </para>
///     <para>
///         <b>The variant is a property, not a configuration.</b> Doc 17's variants are orthogonal to
///         Debug/Release — a Development build is optimised and keeps its profiler, and a Server
///         build differs from a Release one only in having no window. So the variant travels as
///         <c>VixenVariant</c> and the compiler configuration is chosen from it, which keeps
///         <c>-c Release</c> meaning what it means everywhere else.
///     </para>
///     <para>
///         <b>What this does not do is sign anything.</b> Doc 17's packaging table ends in notarised
///         DMGs, provisioned IPAs and AABs with per-ABI splits. Those are Nuke's job and they need
///         credentials; what is here stops at the artefact <c>dotnet publish</c> produces, and says so
///         rather than implying a shippable result.
///     </para>
/// </remarks>
public static class PlayerBuild {
    /// <summary>The targets doc 20's Build menu offers, in menu order.</summary>
    /// <remarks>
    ///     ⚠ <b>Six, and <see cref="TryDescribe" /> knows five of them.</b> Web is on the list because
    ///     it is a target the content build imports for and a target doc 17 ships; what it is not is
    ///     something <c>dotnet publish</c> produces an application from today. Leaving it off the list
    ///     would make it look like a platform the engine has never heard of; leaving it on and
    ///     undescribed makes the editor grey it with the reason, which is doc 20's first bar.
    /// </remarks>
    public static IReadOnlyList<string> Targets { get; } = ["Windows", "Linux", "MacOS", "Android", "iOS", "Web"];

    /// <summary>Doc 17's build variants, less the Editor one, in the order that table lists them.</summary>
    /// <remarks>
    ///     ⚠ <b>Editor is deliberately absent.</b> It is a variant in doc 17's table because the
    ///     editor is an app head like any other — but it is the head doing the asking here, and
    ///     offering a project a build of the editor would be offering it something this build cannot
    ///     produce.
    /// </remarks>
    public static IReadOnlyList<string> Variants { get; } = ["Debug", "Development", "Release", "Server"];

    /// <summary>What each target is published as.</summary>
    /// <param name="target">The target name, as <c>--target</c> spells it.</param>
    /// <param name="shape">How to publish it.</param>
    /// <returns><see langword="false" /> if the target is not one this tool knows.</returns>
    /// <remarks>
    ///     Android and iOS carry a target framework rather than only a runtime identifier, because on
    ///     those the framework is what selects the platform's SDK — publishing <c>net10.0</c> for
    ///     <c>ios-arm64</c> produces a console application that cannot start. Everywhere else the
    ///     framework is the project's own business.
    /// </remarks>
    public static bool TryDescribe(string target, out TargetShape shape) {
        ArgumentNullException.ThrowIfNull(target);

        shape = target.ToLowerInvariant() switch {
            "windows" => new("win-x64", "", "a folder with the executable in it", Runnable: OperatingSystem.IsWindows()),
            "linux" => new("linux-x64", "", "a folder with the executable in it", Runnable: OperatingSystem.IsLinux()),
            "macos" => new(HostMacRid(), "", "a folder with the executable in it", Runnable: OperatingSystem.IsMacOS()),
            "android" => new("", "net10.0-android", "an APK", Runnable: false),
            "ios" => new("ios-arm64", "net10.0-ios", "an .ipa", Runnable: false),
            _ => default
        };

        return shape != default;
    }

    /// <summary>Why a target cannot be published, or <see langword="null" /> when it can.</summary>
    /// <param name="target">The target name.</param>
    /// <returns>A sentence for a greyed menu line, or <see langword="null" />.</returns>
    /// <remarks>
    ///     A sentence rather than a bool, because doc 20's rule for anything that is not built is that
    ///     it is <i>visibly</i> not built: a Build button that refuses without saying which of the two
    ///     reasons applies — an unknown platform, or a project with no <c>.csproj</c> — is a button
    ///     people press twice and then stop trusting.
    /// </remarks>
    public static string? WhyNotPublishable(string target) =>
        TryDescribe(target, out _)
            ? null
            : string.Equals(target, "Web", StringComparison.OrdinalIgnoreCase)
                ? "A Web player is a `wasm` publish with a host page beside it, which doc 17's packaging "
                + "table owns and this build does not produce."
                : $"'{target}' is not a target this build knows. Try Windows, Linux, MacOS, Android or iOS.";

    /// <summary>The one project file in the project root.</summary>
    /// <param name="root">The project directory.</param>
    /// <param name="projectFile">The <c>.csproj</c>.</param>
    /// <returns>Whether there is exactly one.</returns>
    /// <remarks>
    ///     Refuses when there is more than one rather than picking. A directory with two of them is a
    ///     solution, and guessing which is the game is how a tool publishes the wrong thing quietly.
    /// </remarks>
    public static bool TryFindProjectFile(string root, out string projectFile) {
        ArgumentException.ThrowIfNullOrEmpty(root);

        var candidates = Directory.Exists(root)
            ? Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly)
            : [];

        projectFile = candidates.Length == 1 ? candidates[0] : string.Empty;

        return projectFile.Length > 0;
    }

    /// <summary>Publishes the project. The content build is the caller's, and runs first.</summary>
    /// <param name="projectFile">The <c>.csproj</c> to publish.</param>
    /// <param name="shape">How to publish it.</param>
    /// <param name="variant">Which build variant.</param>
    /// <param name="output">Where the artefact goes.</param>
    /// <param name="log">Where to narrate.</param>
    /// <param name="capture">
    ///     Whether the child's own output is read and written to <paramref name="log" /> rather than
    ///     inherited. False for a terminal, where inheriting keeps MSBuild's colours and its progress;
    ///     true for a host with no console of its own, which is every window.
    /// </param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>Whether it succeeded.</returns>
    public static async Task<bool> PublishAsync(
        string projectFile,
        TargetShape shape,
        string variant,
        string output,
        TextWriter log,
        bool capture = false,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(log);

        var arguments = new List<string> {
            "publish",
            projectFile,
            "-c",
            ConfigurationFor(variant),
            "-o",
            output
        };

        if (shape.Framework.Length > 0) {
            arguments.Add("-f");
            arguments.Add(shape.Framework);
        }

        if (shape.Rid.Length > 0) {
            arguments.Add("-r");
            arguments.Add(shape.Rid);
        }

        // Read by AppConfig at boot and by the SDK's own targets, so one flag decides what the
        // binary asserts, what it logs and where it reads content from.
        arguments.Add($"-p:VixenVariant={variant}");

        // And the SDK is told not to do the content work again.
        //
        // Vixen.Sdk runs `vixen import` before the compiler and `vixen content build` after it, which
        // is right when somebody types `dotnet build`. Here the content is already built — the caller
        // did it a moment ago — so leaving the SDK enabled would repeat a full scan and ten thousand
        // decisions inside the publish. It would also require the `vixen` tool to be on the PATH of
        // the process this one just started, which is a strange thing for a command that *is* the
        // tool to demand, and a stranger one for an editor that has just done the work in process.
        //
        // The copy step stays on: it is what puts the built content beside the binary, and it is the
        // half that has nothing to do with rebuilding it.
        arguments.Add("-p:VixenImportOnBuild=false");
        arguments.Add("-p:VixenContentBuildOnBuild=false");

        log.WriteLine($"  dotnet {string.Join(' ', arguments)}");

        return await RunAsync("dotnet", arguments, log, capture, cancellationToken).ConfigureAwait(false) == 0;
    }

    /// <summary>Launches what was published.</summary>
    /// <param name="directory">Where the artefact is.</param>
    /// <param name="assemblyName">The executable's name, without an extension.</param>
    /// <param name="passthrough">Arguments to hand to the application.</param>
    /// <param name="log">Where to narrate.</param>
    /// <param name="capture">Whether the child's output is read rather than inherited.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The application's own exit code, so a script can read it.</returns>
    /// <remarks>
    ///     The application's exit code is returned rather than translated. A game that crashes exits
    ///     1 by <c>VixenApplication.Run</c>'s own contract, and flattening that into a tool's failure
    ///     code would lose the distinction between "the build failed" and "the game ran and stopped
    ///     badly".
    /// </remarks>
    public static async Task<int> LaunchAsync(
        string directory,
        string assemblyName,
        IReadOnlyList<string> passthrough,
        TextWriter log,
        bool capture = false,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(log);

        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? $"{assemblyName}.exe" : assemblyName);

        if (!File.Exists(executable)) {
            log.WriteLine($"  The publish produced no executable named '{Path.GetFileName(executable)}' in {directory}.");
            return NoExecutable;
        }

        log.WriteLine($"  {executable} {string.Join(' ', passthrough)}");
        log.WriteLine();

        return await RunAsync(executable, passthrough, log, capture, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What <see cref="LaunchAsync" /> answers when there was nothing to launch.</summary>
    /// <remarks>
    ///     Not zero and not one. The return is the application's own exit code, so the two values a
    ///     game is likely to use are the two this must not collide with — see the method's remarks.
    /// </remarks>
    public const int NoExecutable = -1;

    /// <summary>Which compiler configuration a variant is built as.</summary>
    /// <param name="variant">The variant.</param>
    /// <returns><c>Debug</c> or <c>Release</c>.</returns>
    /// <remarks>
    ///     Debug is the only unoptimised one. Development is optimised and keeps its diagnostics —
    ///     that is the whole reason doc 17 lists it separately, and building it as Debug would make
    ///     every performance number measured during a playtest a lie.
    /// </remarks>
    public static string ConfigurationFor(string variant) =>
        string.Equals(variant, "Debug", StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";

    static string HostMacRid() =>
        RuntimeInformation.OSArchitecture is Architecture.Arm64 ? "osx-arm64" : "osx-x64";

    /// <summary>Runs a process and forwards everything it says.</summary>
    /// <remarks>
    ///     <para>
    ///         Streamed rather than buffered, on both paths. A publish takes tens of seconds and a run
    ///         takes as long as somebody plays for; collecting either and printing it at the end would
    ///         turn this into a command that appears to hang.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>When <paramref name="capture" /> is set the writer is written from the process's
    ///         own reader threads</b>, which is why it is locked here rather than left to the caller:
    ///         a build log that interleaves two half-lines is a build log somebody reports as a
    ///         compiler bug.
    ///     </para>
    /// </remarks>
    static async Task<int> RunAsync(
        string file,
        IReadOnlyList<string> arguments,
        TextWriter log,
        bool capture,
        CancellationToken cancellationToken
    ) {
        var start = new ProcessStartInfo(file) {
            UseShellExecute = false,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture
        };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"'{file}' could not be started.");

        if (capture) {
            process.OutputDataReceived += Forward;
            process.ErrorDataReceived += Forward;

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        try {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Killed rather than left running. A cancelled build that leaves the game on screen is a
            // process somebody has to find in a task manager.
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return process.ExitCode;

        void Forward(object _, DataReceivedEventArgs line) {
            if (line.Data is not { } text) {
                return;
            }

            lock (log) {
                log.WriteLine(text);
            }
        }
    }
}
