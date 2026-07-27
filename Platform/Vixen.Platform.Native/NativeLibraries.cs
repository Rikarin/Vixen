// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Vixen.Platform.Native;

/// <summary>How one native library is found.</summary>
/// <param name="Library">The name a <c>DllImport</c> uses — <c>vulkan</c>.</param>
/// <param name="Versions">Soname versions to try — <c>1</c> gives <c>libvulkan.so.1</c>.</param>
/// <param name="ExtraDirectories">Places this particular library is installed, beyond the application's own.</param>
public sealed record NativeLibrarySpec(
    string Library,
    IReadOnlyList<string> Versions,
    IReadOnlyList<string> ExtraDirectories
) {
    /// <summary>A library with no versioned soname and nowhere special to look.</summary>
    /// <param name="library">The name.</param>
    public NativeLibrarySpec(string library) : this(library, [], []) { }
}

/// <summary>
///     Resolves the engine's native dependencies itself, instead of leaving it to whatever the
///     binding library does.
/// </summary>
/// <remarks>
///     <para>
///         <b>This exists because of a measured failure, not a preference.</b> Silk.NET locates a
///         native library by asking where its own managed assembly is on disk
///         (<c>Assembly.Location</c>) and by reading the dependency manifest
///         (<c>DependencyContext.Default</c>). A NativeAOT application has neither — it is one
///         native binary — so that path cannot work, and <c>nuke CheckAot</c> reports six IL3000 and
///         IL3002 diagnostics saying exactly that. See R11 in
///         <c>docs/plan/15-risks-and-open-questions.md</c>.
///     </para>
///     <para>
///         <b>What this fixes, and what it does not.</b> A registered resolver runs before the
///         default rules, so the engine's own <c>runtimes/&lt;rid&gt;/native/</c> layout answers
///         first and the binding library's probing is never reached at run time. It does <i>not</i>
///         silence those diagnostics: ILC's analysis is static, and the unreachable-in-practice code
///         is still reachable in the graph. Suppressing them is a separate, deliberate decision that
///         is only defensible once this is in force — and it is not made here, because a suppression
///         and the thing that justifies it should not arrive in the same breath.
///     </para>
///     <para>
///         <b>iOS is not this problem.</b> There, everything is statically linked and there is no
///         resolution step to intercept at all; what is needed is the library's symbols at link
///         time. R11 records both halves.
///     </para>
/// </remarks>
public static class NativeLibraries {
    static readonly ConcurrentDictionary<string, NativeLibrarySpec> Known = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<Assembly, bool> Registered = [];
    static readonly ConcurrentDictionary<string, string> ResolvedFrom = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the application's own files are.</summary>
    /// <remarks>
    ///     <see cref="AppContext.BaseDirectory" /> rather than <c>Assembly.Location</c>, which is the
    ///     whole point: the second returns an empty string in exactly the configuration this class
    ///     exists to serve.
    /// </remarks>
    public static string BaseDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>Which library each resolved name came from, for logging at boot and for a bug report.</summary>
    public static IReadOnlyDictionary<string, string> Resolved => ResolvedFrom;

    /// <summary>Teaches the resolver about a library.</summary>
    /// <param name="spec">How to find it.</param>
    public static void Describe(NativeLibrarySpec spec) {
        ArgumentNullException.ThrowIfNull(spec);
        Known[spec.Library] = spec;
    }

    /// <summary>Installs the resolver for an assembly's <c>DllImport</c>s.</summary>
    /// <param name="assembly">The assembly whose imports to resolve — usually a binding library's.</param>
    /// <remarks>
    ///     Idempotent, because <see cref="NativeLibrary.SetDllImportResolver" /> throws if it is
    ///     called twice for one assembly, and "which of my subsystems registered first" is not a
    ///     question any caller should have to answer.
    /// </remarks>
    public static void Register(Assembly assembly) {
        ArgumentNullException.ThrowIfNull(assembly);

        if (Registered.TryAdd(assembly, true)) {
            NativeLibrary.SetDllImportResolver(assembly, Resolve);
        }
    }

    /// <summary>Every path that would be tried for a library, in order, without trying any of them.</summary>
    /// <param name="library">The name.</param>
    /// <returns>The candidates.</returns>
    public static IEnumerable<string> Candidates(string library) {
        ArgumentException.ThrowIfNullOrEmpty(library);

        var spec = Known.TryGetValue(library, out var known) ? known : new NativeLibrarySpec(library);

        return NativeSearch.Paths(
            NativeSearch.Directories(BaseDirectory, NativeRid.Chain, [.. spec.ExtraDirectories]),
            NativeLibraryNames.For(spec.Library, [.. spec.Versions])
        );
    }

    /// <summary>
    ///     Answers a <c>DllImport</c>, or hands the question back to the runtime by returning zero.
    /// </summary>
    /// <param name="library">The name in the import.</param>
    /// <param name="assembly">Who is importing it.</param>
    /// <param name="searchPath">What the import asked for. Not used: these paths are absolute.</param>
    /// <returns>The handle, or <see cref="IntPtr.Zero" /> to fall through to the default rules.</returns>
    /// <remarks>
    ///     <b>Falling through matters as much as succeeding.</b> Every library the engine does not
    ///     know about — and every one it does, on a machine where the system copy is the only copy —
    ///     has to reach the default rules unchanged. A resolver that answered every question would
    ///     turn one unshipped dependency into a total failure to start.
    /// </remarks>
    static IntPtr Resolve(string library, Assembly assembly, DllImportSearchPath? searchPath) {
        foreach (var candidate in Candidates(library)) {
            if (!File.Exists(candidate) || !NativeLibrary.TryLoad(candidate, out var handle)) {
                continue;
            }

            ResolvedFrom[library] = candidate;
            return handle;
        }

        return IntPtr.Zero;
    }
}
