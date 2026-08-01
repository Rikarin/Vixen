// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;

namespace Vixen.Cli;

/// <summary>Loads a project's own assemblies, so the components it declares exist during a build.</summary>
/// <remarks>
///     <para>
///         <b>The gap this closes.</b> A game declares scene components of its own — <c>[Component]</c>
///         beside <c>[DataContract]</c> — and <c>Vixen.Engine.Generators</c> emits a
///         <c>[ModuleInitializer]</c> that tells <c>SceneComponentRegistry</c> and the type registry
///         about them. That initializer runs when the assembly is first touched, and in a build tool
///         that never references the game, nothing ever touches it. So a level carrying the game's own
///         <c>!SpawnPoint</c> compiled against a registry that had never heard of one, and the build
///         failed with "nothing in this build claims the name" — about a type that is right there in
///         the project being built.
///     </para>
///     <para>
///         ⚠ <b>Loading is not enough; the module constructor has to be run.</b> The CLR runs a module
///         initializer lazily, at the first access to a type in that module, and an assembly loaded
///         and never otherwise used has had no such access. <see cref="RuntimeHelpers.RunModuleConstructor" />
///         is what makes the load mean something — it is idempotent, so a module that has already run
///         is not run twice.
///     </para>
///     <para>
///         <b>Only ever additive.</b> Nothing here is called for its return value: the effect is the
///         registrations the initializers perform. A path that does not exist is skipped rather than
///         failing the build, because the first build of a new project imports its assets before the
///         compiler has produced anything to load — which is exactly the ordering
///         <c>Vixen.Sdk.targets</c> documents, and why the content-build pass after <c>Build</c> is
///         the one that carries the assembly.
///     </para>
/// </remarks>
public static class GameAssemblies {
    /// <summary>Loads each assembly and everything it references, running every module initializer.</summary>
    /// <param name="paths">Paths to assemblies, of which the ones that do not exist are skipped.</param>
    /// <param name="report">Told about each one that could not be loaded, and why.</param>
    /// <returns>How many modules were initialized.</returns>
    /// <remarks>
    ///     ⚠ <b>The reference walk is not thoroughness for its own sake.</b> Loading only the game's
    ///     own assembly registers the game's own components and nothing else, and the engine's are in
    ///     the same position: <c>MeshRenderable</c> lives in <c>Vixen.Rendering</c>, which this tool
    ///     references and may never touch. Whether it does depends on which importers happened to run
    ///     — so a full build compiled a level fine and an incremental one, where every model was
    ///     cached and nothing loaded the renderer, failed on the first <c>!MeshRenderable</c> in it.
    ///     Walking the game's references is the exact set the game was compiled against, which is the
    ///     right definition of "what this build should know about".
    /// </remarks>
    public static int Load(IEnumerable<string> paths, Action<string>? report = null) {
        ArgumentNullException.ThrowIfNull(paths);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var initialized = 0;

        foreach (var path in paths) {
            if (path is not { Length: > 0 }) {
                continue;
            }

            var full = Path.GetFullPath(path);

            if (!File.Exists(full)) {
                continue;
            }

            // LoadFrom rather than LoadFile, so the engine assemblies sitting beside the game's own
            // resolve to the copies this process has already loaded instead of to a second set. Two
            // Vixen.Engines in one process would give the registry two of every component type and
            // the build a name collision that reads as nonsense.
            if (Try(() => Assembly.LoadFrom(full), full, report) is { } assembly) {
                initialized += Initialize(assembly, seen, report);
            }
        }

        return initialized;
    }

    /// <summary>Runs one assembly's module initializer, then its references'.</summary>
    static int Initialize(Assembly assembly, HashSet<string> seen, Action<string>? report) {
        var name = assembly.GetName().Name;

        if (name is null || !seen.Add(name)) {
            return 0;
        }

        // The CLR runs a module initializer lazily, at the first access to a type in the module. An
        // assembly loaded and never otherwise used has had no such access, so the load alone
        // registers nothing. This is idempotent — a module that has already run is not run again.
        if (Try(
                () => {
                    RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
                    return assembly;
                },
                name,
                report
            ) is null) {
            return 0;
        }

        var initialized = 1;

        foreach (var reference in assembly.GetReferencedAssemblies()) {
            // The framework and third-party packages declare no Vixen components and walking them
            // would load half of NuGet to find that out. The engine's own assemblies are the ones
            // whose initializers matter, and they all share a prefix.
            if (reference.Name is not { } referenced || !referenced.StartsWith("Vixen", StringComparison.Ordinal)) {
                continue;
            }

            if (Try(() => Assembly.Load(reference), referenced, report) is { } loaded) {
                initialized += Initialize(loaded, seen, report);
            }
        }

        return initialized;
    }

    /// <summary>Runs something that loads, and reports rather than throws when it will not.</summary>
    /// <remarks>
    ///     Not fatal, on purpose. A project naming a native library, a reference assembly or
    ///     something built against a different engine still has a build worth finishing — what it
    ///     loses is the components that assembly declared, and the compile then says so about each
    ///     one by name, which is a better message than a stack trace from the loader.
    /// </remarks>
    static Assembly? Try(Func<Assembly> load, string what, Action<string>? report) {
        try {
            return load();
        } catch (Exception failure) when (failure is BadImageFormatException
                                             or FileLoadException
                                             or FileNotFoundException
                                             or TypeInitializationException) {
            report?.Invoke($"{what}: {failure.Message}");
            return null;
        }
    }
}
