// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace Vixen.Cli;

/// <summary>How a target is published: what to build it as, and what comes out.</summary>
/// <param name="Rid">The runtime identifier, or empty where the target framework decides.</param>
/// <param name="Framework">The target framework, or empty to leave the project's own alone.</param>
/// <param name="Artefact">What the output directory will hold, for the person reading the log.</param>
/// <param name="Runnable">Whether the host can launch the result.</param>
public readonly record struct TargetShape(string Rid, string Framework, string Artefact, bool Runnable);

/// <summary>
///     <c>vixen build</c> and <c>vixen run</c>: content build, then <c>dotnet publish</c>, then say
///     where it went.
/// </summary>
/// <remarks>
///     <para>
///         <b>The point is that it is one command, not that it is a new build system.</b>
///         [Doc 17](../../docs/plan/17-app-heads-and-shipping.md) asks for content build +
///         <c>dotnet publish</c> + platform packaging behind a single verb, so that the ordering — and
///         the fact that content is stale unless something rebuilt it — is not a thing every developer
///         has to know. Underneath it is the ordinary .NET publish, deliberately: a project that
///         cannot be built by <c>dotnet</c> alone is a project this tool has captured.
///     </para>
///     <para>
///         <b>The variant is a property, not a configuration.</b> Doc 17's five variants are
///         orthogonal to Debug/Release — a Development build is optimised and keeps its profiler, and
///         a Server build differs from a Release one only in having no window. So the variant travels
///         as <c>VixenVariant</c> and the compiler configuration is chosen from it, which keeps
///         `-c Release` meaning what it means everywhere else.
///     </para>
///     <para>
///         <b>What this does not do is sign anything.</b> Doc 17's packaging table ends in notarised
///         DMGs, provisioned IPAs and AABs with per-ABI splits. Those are Nuke's job and they need
///         credentials; what is here stops at the artefact <c>dotnet publish</c> produces, and says so
///         rather than implying a shippable result.
///     </para>
/// </remarks>
public static class PublishRunner {
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

    /// <summary>Builds the content and publishes the project.</summary>
    /// <param name="projectFile">The <c>.csproj</c> to publish.</param>
    /// <param name="shape">How to publish it.</param>
    /// <param name="variant">Which build variant.</param>
    /// <param name="output">Where the artefact goes.</param>
    /// <param name="log">Where to narrate.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>The exit code, and the directory the artefact is in.</returns>
    public static async Task<(ExitCode Code, string Directory)> PublishAsync(
        string projectFile,
        TargetShape shape,
        string variant,
        string output,
        TextWriter log,
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
        // is right when somebody types `dotnet build`. Here the content is already built — this
        // command did it a moment ago — so leaving the SDK enabled would repeat a full scan and ten
        // thousand decisions inside the publish. It would also require the `vixen` tool to be on the
        // PATH of the process this one just started, which is a strange thing for a command that *is*
        // the tool to demand.
        //
        // The copy step stays on: it is what puts the built content beside the binary, and it is the
        // half that has nothing to do with rebuilding it.
        arguments.Add("-p:VixenImportOnBuild=false");
        arguments.Add("-p:VixenContentBuildOnBuild=false");

        log.WriteLine($"  dotnet {string.Join(' ', arguments)}");

        var exit = await RunAsync("dotnet", arguments, log, cancellationToken).ConfigureAwait(false);

        return exit == 0 ? (ExitCode.Success, output) : (ExitCode.Failed, output);
    }

    /// <summary>Launches what was published.</summary>
    /// <param name="directory">Where the artefact is.</param>
    /// <param name="assemblyName">The executable's name, without an extension.</param>
    /// <param name="passthrough">Arguments to hand to the application.</param>
    /// <param name="log">Where to narrate.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The application's own exit code, so a script can read it.</returns>
    /// <remarks>
    ///     The application's exit code is returned rather than translated. A game that crashes exits
    ///     1 by <c>VixenApplication.Run</c>'s own contract, and flattening that into this tool's
    ///     <see cref="ExitCode.Failed" /> would lose the distinction between "the build failed" and
    ///     "the game ran and stopped badly".
    /// </remarks>
    public static async Task<int> LaunchAsync(
        string directory,
        string assemblyName,
        IReadOnlyList<string> passthrough,
        TextWriter log,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(log);

        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? $"{assemblyName}.exe" : assemblyName);

        if (!File.Exists(executable)) {
            log.WriteLine($"  The publish produced no executable named '{Path.GetFileName(executable)}' in {directory}.");
            return (int)ExitCode.Failed;
        }

        log.WriteLine($"  {executable} {string.Join(' ', passthrough)}");
        log.WriteLine();

        return await RunAsync(executable, passthrough, log, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Which compiler configuration a variant is built as.
    /// </summary>
    /// <remarks>
    ///     Debug is the only unoptimised one. Development is optimised and keeps its diagnostics —
    ///     that is the whole reason doc 17 lists it separately, and building it as Debug would make
    ///     every performance number measured during a playtest a lie.
    /// </remarks>
    static string ConfigurationFor(string variant) =>
        variant.Equals("Debug", StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";

    static string HostMacRid() =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
            is System.Runtime.InteropServices.Architecture.Arm64
            ? "osx-arm64"
            : "osx-x64";

    /// <summary>Runs a process and forwards everything it says.</summary>
    /// <remarks>
    ///     Streamed rather than captured. A publish takes tens of seconds and a run takes as long as
    ///     somebody plays for; buffering either would turn this into a command that appears to hang.
    /// </remarks>
    static async Task<int> RunAsync(
        string file,
        IReadOnlyList<string> arguments,
        TextWriter log,
        CancellationToken cancellationToken
    ) {
        var start = new ProcessStartInfo(file) { UseShellExecute = false };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"'{file}' could not be started.");

        try {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Killed rather than left running. A cancelled `vixen run` that leaves the game on screen
            // is a process somebody has to find in a task manager.
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return process.ExitCode;
    }
}
