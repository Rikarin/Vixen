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
///         ⚠ <b>The entry assembly is read into memory rather than mapped from disk.</b>
///         <c>LoadFromAssemblyPath</c> holds the file open until the context is collected, which on
///         Windows means the developer's next build fails to write the DLL it has just been asked to
///         reload. Its own dependencies <i>are</i> mapped, so a plugin that changes a library beside
///         itself still needs a restart — that is a shadow-copy feature and this is not it, but the
///         file a plugin author rebuilds ten times an hour is the one that is free.
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
    /// <remarks>
    ///     Read into memory, with the symbols beside it if there are any — a plugin whose exception
    ///     arrives with line numbers is one whose bug reports are worth reading, and the cost is one
    ///     file read of a file that is about to be loaded anyway.
    /// </remarks>
    public Assembly LoadPlugin() {
        var bytes = File.ReadAllBytes(AssemblyPath);
        var symbolsPath = Path.ChangeExtension(AssemblyPath, ".pdb");

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
        return path is null ? null : LoadFromAssemblyPath(path);
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
