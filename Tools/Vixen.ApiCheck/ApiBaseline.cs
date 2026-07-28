// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.ApiCheck;

/// <summary>
///     The committed record of what an assembly's public surface is allowed to be, and the
///     comparison of a reading against it.
/// </summary>
/// <remarks>
///     <para>
///         Two files per project, both beside its <c>.csproj</c>:
///         <c>PublicAPI.Shipped.txt</c> is what a released package published and is never edited by
///         this tool; <c>PublicAPI.Unshipped.txt</c> is everything approved since, including
///         <c>*REMOVED*</c> lines for shipped API that has been taken away. At release the second
///         is folded into the first.
///     </para>
///     <para>
///         The split is the point rather than bookkeeping: it is what makes "this release removed
///         something a consumer was using" a visible line in a reviewed file instead of an absence
///         nobody looked for. Until the first release, <c>Shipped</c> is empty and honest —
///         everything in this repository is unshipped, and writing it into <c>Shipped</c> would
///         claim a compatibility promise that has not been made.
///     </para>
/// </remarks>
public static class ApiBaseline {
    public const string ShippedFileName = "PublicAPI.Shipped.txt";
    public const string UnshippedFileName = "PublicAPI.Unshipped.txt";

    /// <summary>Marks a shipped entry that has since been removed.</summary>
    public const string RemovedPrefix = "*REMOVED*";

    /// <summary>
    ///     Written as the first line of every baseline. Entries carry <c>!</c> and <c>?</c>
    ///     annotations, and this says so — the file is read by people at least as often as by this
    ///     tool.
    /// </summary>
    const string Header = "#nullable enable";

    /// <summary>
    ///     Where the baselines for an assembly live: the directory of the project that produced it,
    ///     found by walking up from the build output until a <c>.csproj</c> appears.
    /// </summary>
    /// <remarks>
    ///     Derived rather than passed alongside each assembly, so that the caller — a Nuke target
    ///     with a list of paths — cannot pair an assembly with the wrong project's baseline. The
    ///     walk survives a changed output layout, which hard-coding <c>bin/&lt;config&gt;/&lt;tfm&gt;</c>
    ///     would not.
    /// </remarks>
    public static string DirectoryFor(string assemblyPath) {
        var directory = Directory.GetParent(Path.GetFullPath(assemblyPath));

        while (directory is not null) {
            if (directory.EnumerateFiles("*.csproj").Any()) {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No project directory above '{assemblyPath}': walked to the root without finding a .csproj, "
            + "so there is nowhere for its API baseline to live."
        );
    }

    /// <summary>
    ///     Reads a baseline file, or an empty list when there is none. Blank lines and comments are
    ///     not entries.
    /// </summary>
    public static IReadOnlyList<string> Read(string path) =>
        File.Exists(path)
            ? [
                .. File.ReadAllLines(path)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0 && !line.StartsWith('#'))
            ]
            : [];

    /// <summary>Writes a baseline file, sorted, with the header and Unix line endings.</summary>
    /// <remarks>
    ///     The line ending is fixed rather than the platform's. These files are regenerated on
    ///     whichever operating system a developer happens to run <c>--update-api</c> on, and a
    ///     baseline that changes every line when it is regenerated on Windows is a baseline whose
    ///     diffs say nothing.
    /// </remarks>
    public static void Write(string path, IEnumerable<string> entries) {
        ArgumentNullException.ThrowIfNull(entries);

        var content = new StringBuilder().Append(Header).Append('\n');

        foreach (var entry in entries.OrderBy(entry => entry, StringComparer.Ordinal)) {
            content.Append(entry).Append('\n');
        }

        File.WriteAllText(path, content.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    ///     The surface a consumer has been promised: everything shipped, plus everything approved
    ///     since, minus everything a <c>*REMOVED*</c> line has withdrawn.
    /// </summary>
    public static IReadOnlySet<string> Approved(IEnumerable<string> shipped, IEnumerable<string> unshipped) {
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(unshipped);

        var approved = new HashSet<string>(shipped, StringComparer.Ordinal);

        foreach (var entry in unshipped) {
            if (entry.StartsWith(RemovedPrefix, StringComparison.Ordinal)) {
                approved.Remove(entry[RemovedPrefix.Length..]);
            } else {
                approved.Add(entry);
            }
        }

        return approved;
    }

    /// <summary>Compares a reading of the surface against the baseline that approves it.</summary>
    public static ApiDifference Compare(
        IReadOnlyList<string> surface,
        IEnumerable<string> shipped,
        IEnumerable<string> unshipped
    ) {
        ArgumentNullException.ThrowIfNull(surface);

        var approved = Approved(shipped, unshipped);
        var present = new HashSet<string>(surface, StringComparer.Ordinal);

        return new(
            [.. surface.Where(entry => !approved.Contains(entry)).OrderBy(entry => entry, StringComparer.Ordinal)],
            [.. approved.Where(entry => !present.Contains(entry)).OrderBy(entry => entry, StringComparer.Ordinal)]
        );
    }

    /// <summary>
    ///     The contents <c>PublicAPI.Unshipped.txt</c> should have for this surface: everything not
    ///     already shipped, and a <c>*REMOVED*</c> line for everything shipped that is gone.
    /// </summary>
    public static IReadOnlyList<string> Rebase(IReadOnlyList<string> surface, IEnumerable<string> shipped) {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(shipped);

        var released = new HashSet<string>(shipped, StringComparer.Ordinal);
        var present = new HashSet<string>(surface, StringComparer.Ordinal);

        var unshipped = new SortedSet<string>(surface.Where(entry => !released.Contains(entry)), StringComparer.Ordinal);

        foreach (var gone in released.Where(entry => !present.Contains(entry))) {
            unshipped.Add(RemovedPrefix + gone);
        }

        return [.. unshipped];
    }
}

/// <summary>What a reading of the surface has that the baseline does not, and the reverse.</summary>
/// <param name="Added">Entries in the assembly that no baseline approves — an unapproved addition.</param>
/// <param name="Removed">Entries the baseline approves that the assembly no longer has — a break.</param>
public sealed record ApiDifference(IReadOnlyList<string> Added, IReadOnlyList<string> Removed) {
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;
}
