// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Vixen.Editor.Assets;

namespace Vixen.AssetCompiler;

/// <summary>The importers a worker has to be told about, because it did not load the plugin.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § Part 6's one correctness gap.</b> <c>BuiltInImporters.Create()</c> folds in
///         <c>ImporterContributions.Default</c>, and in a worker process that set is empty — nothing
///         there has loaded a plugin. So an asset only a plugin can import was imported by the
///         coordinator and refused by the pool, with the difference being which process happened to
///         run it. That is a content build producing different bytes than the editor showed.
///     </para>
///     <para>
///         <b>The worker is told which assemblies, not which importers.</b> An importer is a live
///         object with a settings type, and there is no way to send one down a pipe; what crosses is
///         the set of file paths the coordinator's own contributed importers came out of, and the
///         worker loads the same files. <see cref="AssembliesBehind" /> is the coordinator's half and
///         <see cref="Load" /> is the worker's.
///     </para>
///     <para>
///         ⚠ <b>Built fresh per worker, and deliberately not cached.</b>
///         <c>BuiltInImporters.Create</c>'s own remarks say the registry is assembled per run so that
///         the editor and the CLI cannot disagree about the set; a per-process cache of plugin
///         importers would reintroduce exactly that, one level down, and the disagreement would be
///         invisible because a worker is not a process anybody looks inside.
///     </para>
///     <para>
///         ⚠ <b>A plugin that cannot be loaded fails this worker rather than the pool.</b> Crash
///         isolation is the entire reason the workers exist, and the current arrangement gets it for
///         free by not loading the plugin at all. Loading it gives that up unless the failure is
///         contained: <see cref="Load" /> throws, the worker writes the reason to stderr and exits,
///         and <c>CompilerPool</c> reports a worker that never connected against the run rather than
///         waiting for a pipe that will not open.
///     </para>
///     <para>
///         ⚠ <b>An assembly with no file on disk cannot cross, and that is a real hole rather than a
///         detail.</b> A plugin is a <c>.dll</c> and a project's editor scripts are compiled to one
///         beside the library, so both have a path — but an importer contributed from a dynamic
///         assembly has none, and <see cref="AssembliesBehind" /> leaves it out. The worker then
///         disagrees again, silently, which is why <see cref="Missing" /> exists to be reported.
///     </para>
/// </remarks>
public static class PluginImporters {
    /// <summary>Where the contributed importers in this process came from, as files.</summary>
    /// <param name="contributions">The coordinator's set.</param>
    /// <returns>Each distinct assembly path, oldest contribution first.</returns>
    /// <remarks>
    ///     ⚠ <b>Distinct by path, because two importers from one plugin are one assembly.</b> Loading
    ///     it twice would give the worker two copies of every type in it and two settings descriptors
    ///     under one <c>[DataContract]</c> alias, which is a registry conflict caused entirely by the
    ///     transport.
    /// </remarks>
    public static IReadOnlyList<string> AssembliesBehind(ImporterContributions contributions) {
        ArgumentNullException.ThrowIfNull(contributions);

        List<string> paths = [];

        foreach (var importer in contributions.All) {
            var assembly = importer.GetType().Assembly;

            if (assembly.IsDynamic || assembly.Location.Length == 0) {
                continue;
            }

            if (!paths.Contains(assembly.Location, StringComparer.Ordinal)) {
                paths.Add(assembly.Location);
            }
        }

        return paths;
    }

    /// <summary>The contributed importers that have no file, and so cannot reach a worker.</summary>
    /// <param name="contributions">The coordinator's set.</param>
    /// <returns>Their type names.</returns>
    /// <remarks>
    ///     Reported rather than silently dropped: an importer a worker does not have is an asset
    ///     that imports in one process and fails in another, which is the failure this whole file
    ///     exists to end. A coordinator with any of these is one whose out-of-process build cannot
    ///     be trusted to agree with its in-process one.
    /// </remarks>
    public static IReadOnlyList<string> Missing(ImporterContributions contributions) {
        ArgumentNullException.ThrowIfNull(contributions);

        return [
            .. contributions.All
                .Where(importer => importer.GetType().Assembly is { IsDynamic: true } or { Location.Length: 0 })
                .Select(importer => importer.GetType().FullName ?? importer.GetType().Name)
        ];
    }

    /// <summary>Loads every <c>[Importer]</c> out of the named assemblies.</summary>
    /// <param name="assemblyPaths">The files, as <see cref="AssembliesBehind" /> produced them.</param>
    /// <returns>A set of its own, to hand to <c>BuiltInImporters.Create</c>.</returns>
    /// <exception cref="InvalidOperationException">
    ///     An assembly is not there, does not load, or declares an importer that cannot be made.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A set of its own rather than <c>ImporterContributions.Default</c>.</b> The
    ///         default is a process-wide singleton whose contract is that a contributor withdraws
    ///         what it added; nothing here ever withdraws, because the worker exits instead. Handing
    ///         a fresh set to <c>Create</c> is the same shape every test in the repository uses and
    ///         keeps the static empty in the one process that has no editor to manage it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An assembly scan, which <c>BuiltInImporters</c> refuses for the built-ins.</b>
    ///         That refusal is about the shipped set — a scan there would read metadata a trimmed
    ///         publish deleted and make "which importers imported this project" have a different
    ///         answer per process. This is the opposite case: the assembly is a plugin the
    ///         coordinator has already loaded and scanned, the walk is over one file, and the answer
    ///         it produces is what makes the two processes agree rather than what makes them differ.
    ///     </para>
    /// </remarks>
    public static ImporterContributions Load(IEnumerable<string> assemblyPaths) {
        ArgumentNullException.ThrowIfNull(assemblyPaths);

        var contributions = new ImporterContributions();

        foreach (var path in assemblyPaths) {
            if (!File.Exists(path)) {
                throw new InvalidOperationException(
                    $"The importer assembly '{path}' is not there. A worker is told which plugin assemblies "
                    + "its coordinator loaded, and one that has moved since would make this process import "
                    + "with a different set than the process that started it."
                );
            }

            Assembly assembly;

            try {
                assembly = new ImporterLoadContext(path).LoadFromAssemblyPath(path);
            } catch (Exception failure) when (failure is BadImageFormatException or FileLoadException or IOException) {
                throw new InvalidOperationException($"'{path}' could not be loaded: {failure.Message}", failure);
            }

            foreach (var type in Types(assembly)) {
                Add(contributions, type, path);
            }
        }

        return contributions;
    }

    /// <summary>Adds one type, if it is an importer.</summary>
    /// <remarks>
    ///     ⚠ <b>The three refusals are <c>EditorScripts</c>'s, word for word in intent.</b> A type
    ///     that is an <c>IAssetImporter</c> without <c>[Importer]</c> claims no extension and a type
    ///     without a parameterless constructor cannot be made — and a worker that skipped either
    ///     quietly would be back to the disagreement this file removes, with the plugin author
    ///     told nothing.
    /// </remarks>
    static void Add(ImporterContributions contributions, Type type, string path) {
        if (type.IsAbstract || !type.IsClass || !typeof(IAssetImporter).IsAssignableFrom(type)) {
            return;
        }

        if (Attribute.GetCustomAttribute(type, typeof(ImporterAttribute)) is null) {
            return;
        }

        if (type.GetConstructor(Type.EmptyTypes) is null) {
            throw new InvalidOperationException(
                $"'{type.Name}' in '{path}' is an asset importer with no parameterless constructor, so nothing "
                + "could make one."
            );
        }

        IAssetImporter importer;

        try {
            importer = (IAssetImporter)Activator.CreateInstance(type)!;
        } catch (Exception failure) when (failure is MissingMethodException or TargetInvocationException) {
            throw new InvalidOperationException($"'{type.Name}' in '{path}' could not be made: {failure.Message}", failure);
        }

        // ⚠ Read here rather than left to the registry. `Name` is the settings type's [DataContract]
        // alias out of `TypeRegistry`, which a plugin's own generated module initializer fills in
        // when the assembly is first touched — and an assembly built without that generator has no
        // descriptor, so the failure is "this importer has no name" from four frames inside
        // `ImporterRegistry.Add`. Said here, it names the assembly the reader has to go and fix.
        try {
            _ = importer.Name;
        } catch (InvalidOperationException failure) {
            throw new InvalidOperationException(
                $"'{type.Name}' in '{path}' cannot be registered in a worker: {failure.Message}",
                failure
            );
        }

        contributions.Add(importer);
    }

    /// <summary>Every type an assembly declares that can be loaded.</summary>
    /// <remarks>
    ///     ⚠ <b>A load failure is not fatal</b>, on <c>DeclaredContributions</c>'s terms:
    ///     <c>GetTypes</c> throws when a type references something the context could not resolve and
    ///     hands back the ones it could, which for a plugin referencing an editor assembly this
    ///     headless worker has never loaded is exactly the useful half.
    /// </remarks>
    static IEnumerable<Type> Types(Assembly assembly) {
        try {
            return assembly.GetTypes();
        } catch (ReflectionTypeLoadException failure) {
            return failure.Types.Where(type => type is not null).Select(type => type!);
        }
    }
}

/// <summary>One plugin's assemblies, loaded into a worker that will never unload them.</summary>
/// <remarks>
///     <para>
///         <b>Not collectible, unlike <c>PluginLoadContext</c>, and the difference is the process.</b>
///         The editor's contexts are collectible because a plugin author rebuilds one ten times an
///         hour and must not have to restart the editor; a worker serves one build and exits, so a
///         collectible context here would buy nothing and cost the JIT's every optimisation that
///         needs a non-collectible method table.
///     </para>
///     <para>
///         ⚠ <b>The shared-assembly rule is the same one, and it is the rule that makes this work at
///         all.</b> If a plugin's folder carries its own <c>Vixen.Editor.Assets.dll</c> and this
///         context loads it, the plugin's <c>IAssetImporter</c> is a different type from the
///         worker's — same name, same assembly, different context — and the cast in
///         <c>PluginImporters.Add</c> fails with a message that reads like a lie.
///     </para>
/// </remarks>
sealed class ImporterLoadContext : AssemblyLoadContext {
    readonly AssemblyDependencyResolver resolver;
    readonly HashSet<string> shared;

    internal ImporterLoadContext(string assemblyPath)
        : base("vixen-worker-importer:" + Path.GetFileNameWithoutExtension(assemblyPath)) {
        resolver = new(assemblyPath);

        shared = Default.Assemblies
            .Select(assembly => assembly.GetName().Name)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName) {
        if (assemblyName.Name is { } name
            && (name.StartsWith("Vixen.", StringComparison.Ordinal) || shared.Contains(name))) {
            try {
                return Default.LoadFromAssemblyName(assemblyName);
            } catch (FileNotFoundException) {
                // A Vixen.* this worker has not got. The prefix rule was a guess about who owns it
                // and the guess was wrong, so let the plugin's own copy answer.
            }
        }

        var path = resolver.ResolveAssemblyToPath(assemblyName);

        // Null means "ask the default context", which is the right answer for the framework
        // assemblies the resolver deliberately does not claim.
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <inheritdoc />
    protected override nint LoadUnmanagedDll(string unmanagedDllName) {
        var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : NativeLibrary.Load(path);
    }
}
