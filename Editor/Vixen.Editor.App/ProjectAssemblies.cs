// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;

namespace Vixen.Editor.App;

/// <summary>What building a project's own code produced, and what to say if it did not.</summary>
/// <param name="Assembly">The loaded assembly, or <see langword="null" /> if nothing was loaded.</param>
/// <param name="Output">What the compiler said, verbatim.</param>
/// <param name="Failed">Whether the build failed. A project with no code to build has neither.</param>
public readonly record struct ProjectBuild(Assembly? Assembly, string Output, bool Failed);

/// <summary>The project's own code, built and loaded so the editor can see what it declares.</summary>
/// <remarks>
///     <para>
///         <b>The fourth seam, and the one that makes the other three about a <i>game</i> rather than
///         about the engine.</b> A behaviour is authorable because a generator declared it and a
///         bridge draws it — but only for assemblies the editor already references. This is what puts
///         a script somebody wrote this morning in the Add Component menu.
///     </para>
///     <para>
///         ⚠ <b>The context is <see cref="PluginLoadContext" />, which already solved every hard part
///         of this.</b> Collectible, so a rebuild can replace it; <c>Vixen.*</c> resolved against the
///         host, so the project's <c>Behavior</c> is the editor's <c>Behavior</c> and not a
///         same-named stranger; and the entry assembly read into memory rather than mapped, so the
///         next build can overwrite the file it just loaded. Reusing it is not thrift — a second
///         context that got any of those wrong would fail in exactly the ways its remarks describe.
///     </para>
///     <para>
///         ⚠ <b>Loading an assembly does not run its module initializers.</b> The runtime defers them
///         to the first access of the module, and nothing here accesses anything — so a project whose
///         behaviours register from a <c>[ModuleInitializer]</c> would load, hold them, and declare
///         nothing. <see cref="Load" /> runs the module constructor by hand, which is
///         <c>ComponentsView.Prime</c>'s problem meeting the same answer from the other side.
///     </para>
///     <para>
///         ⚠ <b>What is <i>not</i> here is unloading.</b> The context is collectible and nothing
///         calls <c>Unload</c>, because the registries a load fills have no way to empty — see
///         <see cref="Reload" />. A project rebuilt in place therefore needs the editor restarted,
///         which is the honest half of this feature and is said out loud rather than half-built.
///     </para>
/// </remarks>
public sealed class ProjectAssemblies {
    readonly ProjectPaths paths;

    PluginLoadContext? context;

    /// <summary>How long a build may take before it is given up on.</summary>
    /// <remarks>
    ///     Generous, because a first build restores packages over a network. A build that has not
    ///     finished by then has gone wrong in a way waiting longer will not fix, and the editor has
    ///     to come back to the user either way.
    /// </remarks>
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The assembly currently loaded, or <see langword="null" /> if none is.</summary>
    public Assembly? Loaded { get; private set; }

    /// <summary>Watches one project's code.</summary>
    /// <param name="project">Where the project lives.</param>
    public ProjectAssemblies(ProjectPaths project) {
        ArgumentNullException.ThrowIfNull(project);
        paths = project;
    }

    /// <summary>The project's own <c>.csproj</c>, or <see langword="null" /> if it has none.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Exactly one, and more than one is nothing rather than a guess.</b> A project root
    ///         holding two <c>.csproj</c> files is either a solution the editor has no opinion about
    ///         or a mistake, and picking the alphabetically first would make "which of my scripts are
    ///         loaded" depend on a file name. The template writes one.
    ///     </para>
    ///     <para>
    ///         The root only — not a recursive search. Everything under <c>Assets/</c> is content,
    ///         and a <c>.csproj</c> found down there belongs to something the project references
    ///         rather than to the project.
    ///     </para>
    /// </remarks>
    public string? Project {
        get {
            if (!Directory.Exists(paths.Root)) {
                return null;
            }

            var found = Directory.GetFiles(paths.Root, "*.csproj", SearchOption.TopDirectoryOnly);

            return found.Length == 1 ? found[0] : null;
        }
    }

    /// <summary>Builds the project's code and loads what came out.</summary>
    /// <returns>What happened, for a caller that has somewhere to report it.</returns>
    /// <remarks>
    ///     ⚠ <b>A project with no <c>.csproj</c> is not a failure.</b> Plenty of projects are content
    ///     and nothing else, and an editor that reported an error for one would be reporting on the
    ///     absence of a feature nobody asked for.
    /// </remarks>
    public ProjectBuild Reload() {
        if (Project is not { } project) {
            return new(null, string.Empty, Failed: false);
        }

        var (built, output) = Build(project);

        if (built is null) {
            return new(null, output, Failed: true);
        }

        return new(Load(built), output, Failed: false);
    }

    /// <summary>Loads an already-built assembly into a context of its own.</summary>
    /// <param name="assemblyPath">The assembly.</param>
    /// <returns>It, loaded.</returns>
    /// <remarks>
    ///     Separate from <see cref="Reload" /> because a build and a load fail for entirely different
    ///     reasons and a caller reporting them wants to say which — and because a test that has an
    ///     assembly already has no reason to run a compiler to get one.
    /// </remarks>
    public Assembly Load(string assemblyPath) {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);

        context = new PluginLoadContext(assemblyPath, "vixen-project:" + Path.GetFileNameWithoutExtension(assemblyPath));

        var assembly = context.LoadPlugin();

        // ⚠ By hand, because loading is not touching. Everything a project declares to the editor —
        // its components, its behaviours, its serializers — arrives through a [ModuleInitializer],
        // and the runtime runs one at the first access of the module rather than at load. Nothing
        // here would otherwise access anything, so the assembly would sit there declaring nothing.
        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);

        Loaded = assembly;
        return assembly;
    }

    /// <summary>Runs the compiler and says where the assembly landed.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The path comes from MSBuild rather than from a convention.</b>
    ///         <c>bin/Debug/net10.0/&lt;name&gt;.dll</c> is right until a project sets an
    ///         <c>AssemblyName</c>, targets something else, or is built by an SDK that puts output
    ///         somewhere of its own — and <c>Vixen.Sdk</c> is exactly such an SDK. <c>--getProperty</c>
    ///         asks the build what it produced, which cannot drift from what it did.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both streams are captured and both are returned on failure.</b> A compiler error
    ///         a user cannot read is a build that "just does not work"; the console panel is where
    ///         this goes.
    ///     </para>
    /// </remarks>
    static (string? Assembly, string Output) Build(string project) {
        var (built, output) = Run(project, "build", "--nologo");

        if (!built) {
            return (null, output);
        }

        // ⚠ A second invocation, because `--getProperty` on its own *evaluates* rather than builds.
        // Passing it to the build above produced a path, an exit code of zero and no compiler at all
        // — a project with a syntax error in it "succeeded" and reported an assembly that was never
        // written. Building first and asking second is two processes and one truth.
        var (asked, path) = Run(project, "msbuild", "--nologo", "-getProperty:TargetPath");

        if (!asked) {
            return (null, output + Environment.NewLine + path);
        }

        // The last non-empty line: `-getProperty` prints the value, and anything with something to
        // say said it first.
        var assembly = path
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return assembly is not null && File.Exists(assembly)
            ? (assembly, output)
            : (null, output + Environment.NewLine + $"The build produced no assembly at '{assembly}'.");
    }

    /// <summary>Runs one `dotnet` command against the project and hands back everything it said.</summary>
    /// <remarks>
    ///     ⚠ <b>Both streams, and both on failure.</b> A compiler error the user cannot read is a
    ///     build that "just does not work", which is the failure this whole seam is most likely to
    ///     have. The console panel is where it goes.
    /// </remarks>
    static (bool Ok, string Output) Run(string project, params string[] arguments) {
        var start = new ProcessStartInfo("dotnet") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(project) ?? Environment.CurrentDirectory
        };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(project);

        try {
            using var process = Process.Start(start);

            if (process is null) {
                return (false, "Could not start `dotnet`. The SDK has to be on PATH for a project's own code to build.");
            }

            // ⚠ Read before the wait, not after. A child that fills the pipe buffer blocks writing to
            // it while the parent blocks waiting for the child, which is a deadlock that only shows
            // up on the build with enough warnings to fill four kilobytes.
            var output = process.StandardOutput.ReadToEnd();
            var errors = process.StandardError.ReadToEnd();

            if (!process.WaitForExit((int) Timeout.TotalMilliseconds)) {
                process.Kill(entireProcessTree: true);
                return (false, $"The build did not finish within {Timeout.TotalMinutes:0} minutes and was stopped.");
            }

            var text = string.IsNullOrWhiteSpace(errors) ? output : output + Environment.NewLine + errors;

            return (process.ExitCode == 0, text);
        } catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException) {
            return (false, $"Could not run `dotnet`: {exception.Message}");
        }
    }
}
