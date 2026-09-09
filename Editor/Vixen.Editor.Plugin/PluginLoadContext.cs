// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Vixen.Editor.Plugin;

/// <summary>One plugin's assemblies, loaded so that they can be thrown away again.</summary>
/// <remarks>
///     <para>
///         <b>Collectible, which is the whole point.</b> A plugin author who has to restart the
///         editor to see a change has a ten-second edit cycle and stops iterating; one whose plugin
///         can be unloaded and reloaded in place has the cycle the rest of the editor has. Doc 11
///         asks for exactly this, and it is the reason <c>Vixen.Editor.App</c> is not NativeAOT —
///         a collectible context and an ahead-of-time-compiled process are not compatible ideas.
///     </para>
///     <para>
///         ⚠ <b>Anything shared with the host resolves to the host's copy, and getting this wrong is
///         the classic failure.</b> If a plugin's folder contains <c>Vixen.Editor.Plugin.dll</c> and
///         this context loads it, the plugin's <c>IEditorPlugin</c> is a <i>different type</i> from
///         the host's — same name, same assembly name, different load context — so the cast in the
///         loader fails with a message that reads like a lie ("cannot cast IEditorPlugin to
///         IEditorPlugin"). The rule below prevents it: an assembly the host already has, or any
///         <c>Vixen.*</c>, comes from the default context.
///     </para>
///     <para>
///         ⚠ <b>Nothing in a plugin's folder is mapped from disk — the entry assembly and every
///         library beside it are read into memory.</b> <c>LoadFromAssemblyPath</c> holds the file
///         open until the context is collected, which on Windows means the developer's next build
///         fails to write the DLL it has just been asked to reload. This used to be true of the
///         entry assembly only, so a plugin that changed a library beside itself needed a restart
///         and the fix was written down as "shadow-copy the folder" — ⚠ <b>but a shadow copy is a
///         second copy on disk to keep in step, and reading the bytes is the same guarantee with
///         nothing to keep in step.</b> A plugin's dependency is loaded exactly the way its entry
///         assembly always was.
///     </para>
///     <para>
///         The cost is stated rather than hidden: an assembly loaded from a stream has no
///         <see cref="Assembly.Location" />, so a plugin that finds a data file by asking its own
///         assembly where it lives must ask its <i>directory</i> instead — which
///         <see cref="AssemblyPath" /> and the manifest both give it. That trade was already made
///         for the entry assembly, and a plugin folder is the unit anyway.
///     </para>
///     <para>
///         The <c>.deps.json</c> beside the assembly is what resolves everything else, through
///         <see cref="AssemblyDependencyResolver" /> — the same file <c>dotnet build</c> writes with
///         no work from the plugin author, and the same mechanism that finds the right native
///         library for the running RID.
///     </para>
/// </remarks>
public sealed class PluginLoadContext : AssemblyLoadContext {
    /// <summary>Assemblies with this prefix always come from the host.</summary>
    /// <remarks>
    ///     A blunt rule with a sharp reason: every type a plugin exchanges with the editor is
    ///     declared in one of these, and a plugin that shipped its own copy of one would not be
    ///     extending this editor so much as running beside it. The cost is that a plugin cannot
    ///     bring a newer <c>Vixen.*</c> than the editor has, which is the correct answer anyway —
    ///     the manifest's <c>api</c> is where that conversation belongs.
    /// </remarks>
    public const string SharedPrefix = "Vixen.";

    readonly AssemblyDependencyResolver resolver;
    readonly HashSet<string> shared;

    /// <summary>Opens a context for one plugin.</summary>
    /// <param name="assemblyPath">The plugin's assembly. Its folder is where dependencies come from.</param>
    /// <param name="name">What the context is called in a debugger and in a stack trace.</param>
    public PluginLoadContext(string assemblyPath, string? name = null)
        : base(name ?? "vixen-plugin:" + Path.GetFileNameWithoutExtension(assemblyPath), isCollectible: true) {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);

        AssemblyPath = assemblyPath;
        resolver = new AssemblyDependencyResolver(assemblyPath);

        // Snapshotted rather than asked each time. What the host has loaded grows while it runs —
        // opening a shader graph loads assemblies the editor had not touched at start-up — and a
        // rule that changed underneath a plugin would make "does this resolve to the host's copy"
        // depend on which panels the user had opened, which is not a thing anybody could debug.
        shared = Default.Assemblies
            .Select(assembly => assembly.GetName().Name)
            .Where(assemblyName => assemblyName is not null)
            .Select(assemblyName => assemblyName!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The plugin's assembly.</summary>
    public string AssemblyPath { get; }

    /// <summary>Loads the plugin's own assembly into this context.</summary>
    /// <returns>The assembly.</returns>
    public Assembly LoadPlugin() => LoadUnmapped(AssemblyPath);

    /// <summary>Reads one assembly's bytes into this context, leaving the file on disk unheld.</summary>
    /// <param name="path">The assembly.</param>
    /// <returns>It, loaded.</returns>
    /// <remarks>
    ///     With the symbols beside it if there are any — a plugin whose exception arrives with line
    ///     numbers is one whose bug reports are worth reading, and the cost is one file read of a
    ///     file that is about to be loaded anyway.
    /// </remarks>
    Assembly LoadUnmapped(string path) {
        var bytes = File.ReadAllBytes(path);
        var symbolsPath = Path.ChangeExtension(path, ".pdb");

        using var assembly = new MemoryStream(bytes);

        if (!File.Exists(symbolsPath)) {
            return LoadFromStream(assembly);
        }

        using var symbols = new MemoryStream(File.ReadAllBytes(symbolsPath));
        return LoadFromStream(assembly, symbols);
    }

    /// <summary>Whether an assembly is one the host owns rather than one the plugin brings.</summary>
    /// <param name="name">The assembly's simple name.</param>
    /// <returns>Whether it is.</returns>
    public bool IsShared(string? name) =>
        name is not null && (name.StartsWith(SharedPrefix, StringComparison.Ordinal) || shared.Contains(name));

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName) {
        if (IsShared(assemblyName.Name)) {
            try {
                return Default.LoadFromAssemblyName(assemblyName);
            } catch (FileNotFoundException) {
                // A Vixen.* the host has not got. The prefix rule was a guess about who owns it and
                // the guess was wrong, so fall through and let the plugin's own copy answer.
            }
        }

        var path = resolver.ResolveAssemblyToPath(assemblyName);

        // Null means "ask the default context", which is the right answer for the framework
        // assemblies the resolver deliberately does not claim.
        //
        // ⚠ Read rather than mapped, which is the difference between a plugin author who rebuilds
        // and reloads and one who rebuilds and restarts. A resolved path here is always inside the
        // plugin's own folder, which is the thing being iterated on.
        return path is null ? null : LoadUnmapped(path);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The RID-specific native library out of the plugin's own <c>runtimes/</c>, which is how a
    ///     plugin wrapping a C library works at all. Falls back to the default probing — the
    ///     process's own directory, then the OS's search path — for a library the plugin expects the
    ///     machine to have.
    /// </remarks>
    protected override nint LoadUnmanagedDll(string unmanagedDllName) {
        var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : NativeLibrary.Load(path);
    }
}
