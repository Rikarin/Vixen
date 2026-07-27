// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform.Native;

/// <summary>Where a native library is looked for, and in what order.</summary>
/// <remarks>
///     <para>
///         <b>The application's own files first, the system last.</b> A published game ships its
///         natives in <c>runtimes/&lt;rid&gt;/native/</c> — the layout NuGet produces and
///         <c>dotnet publish</c> preserves — and those are the versions it was built and tested
///         against. Asking the operating system first would mean a machine with an older
///         system-wide copy of the same library silently wins, which is the shape of every "works
///         on my machine" report ever filed about native dependencies.
///     </para>
///     <para>
///         <b>Every path is produced before any file is touched.</b> The whole of this class is a
///         pure function from a name to a list of candidates, which is what makes the ordering
///         testable without a filesystem, on a machine that is not the target platform.
///     </para>
/// </remarks>
public static class NativeSearch {
    /// <summary>Where a published application keeps the natives it shipped with.</summary>
    public const string RuntimesFolder = "runtimes";

    /// <summary>The subdirectory under a runtime identifier that holds the binaries.</summary>
    public const string NativeFolder = "native";

    /// <summary>Every directory a native library may be in, most specific first.</summary>
    /// <param name="baseDirectory">The application's directory.</param>
    /// <param name="ridChain">The runtime identifiers to accept, most specific first.</param>
    /// <param name="extra">Directories a caller knows about — a package manager's prefix, an SDK.</param>
    /// <returns>The directories.</returns>
    public static IEnumerable<string> Directories(
        string baseDirectory,
        IReadOnlyList<string> ridChain,
        params ReadOnlySpan<string> extra
    ) {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        ArgumentNullException.ThrowIfNull(ridChain);

        var directories = new List<string>();

        foreach (var rid in ridChain) {
            directories.Add(Path.Combine(baseDirectory, RuntimesFolder, rid, NativeFolder));
        }

        // Beside the executable, which is where a single-file publish puts everything and where a
        // developer drops a library they are bisecting.
        directories.Add(baseDirectory);

        foreach (var directory in extra) {
            if (directory.Length > 0) {
                directories.Add(directory);
            }
        }

        return directories;
    }

    /// <summary>Every full path to try, in order.</summary>
    /// <param name="directories">Where to look.</param>
    /// <param name="fileNames">What the file may be called.</param>
    /// <returns>The paths.</returns>
    /// <remarks>
    ///     Directory-major: every name is tried in the most specific directory before the next
    ///     directory is considered. The alternative — name-major — would prefer a system copy under
    ///     the exact file name over the application's own copy under a versioned one, which is the
    ///     preference this class exists to invert.
    /// </remarks>
    public static IEnumerable<string> Paths(IEnumerable<string> directories, IEnumerable<string> fileNames) {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(fileNames);

        var names = fileNames as IReadOnlyList<string> ?? [.. fileNames];

        foreach (var directory in directories) {
            foreach (var name in names) {
                yield return Path.Combine(directory, name);
            }
        }
    }
}
