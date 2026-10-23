// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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
///         claim a compatibility promise that has not been made. ⚠ The one exception is
///         <c>Vixen.Editor.Plugin</c>'s stable core, shipped by decision because that promise was
///         made to plugin authors (#1311).
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

/// <summary>
///     A baseline line written in <c>Microsoft.CodeAnalysis.PublicApiAnalyzers</c>' dialect rather
///     than this tool's (#1340).
/// </summary>
/// <param name="Line">The 1-based line number in the file.</param>
/// <param name="Entry">The line as written.</param>
internal sealed record ApiDialectError(int Line, string Entry) {
    /// <summary>What a reader needs to hear: that the line is in the other dialect, and what to change.</summary>
    /// <param name="file">The baseline's path, for the message.</param>
    /// <returns>One line, <c>file:line</c> first.</returns>
    public string Describe(string file) =>
        file + ":" + Line.ToString(CultureInfo.InvariantCulture) + ": '" + Entry
        + "' is in PublicApiAnalyzers' dialect — a '!' after a type marks a non-nullable reference there, "
        + "and this tool writes a non-nullable reference bare and a nullable one with '?'. Delete the '!'. "
        + "Left in, the line describes no member and the gate reports it as one removal beside one "
        + "addition of the same member.";
}

/// <summary>The one dialect check a baseline line gets without a Release build.</summary>
internal static class ApiDialect {
    /// <summary>
    ///     Every line of a baseline that carries the analyzer's non-nullable <c>!</c>.
    /// </summary>
    /// <param name="lines">The file's lines, as written, comments and all.</param>
    /// <returns>The offending lines, in order.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two formats are not one, whatever this tool's project file used to say.</b>
    ///         <see cref="ApiSurfaceReader" /> writes with
    ///         <c>SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier</c>, which
    ///         puts <c>?</c> on a nullable reference and has no <c>!</c> counterpart; the analyzer's own
    ///         writer puts <c>!</c> on every non-nullable reference. The analyzer is referenced
    ///         nowhere in this repository, so a line in its dialect was read by one thing — a
    ///         Release-configured <c>CheckApi</c> — and reported as an addition and a removal of the
    ///         same member, which is also what a rename looks like (#650's <c>KeyMap! keys</c>).
    ///     </para>
    ///     <para>
    ///         A <c>!</c> straight after an identifier character, <c>&gt;</c>, <c>]</c> or <c>)</c> —
    ///         the end of a type — and not followed by <c>=</c>. That keeps <c>operator !=</c> and
    ///         <c>operator !</c>, the only other <c>!</c> this tool can write, and a <c>!</c> inside a
    ///         constant's string or character value is not looked at.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<ApiDialectError> Errors(IEnumerable<string> lines) {
        ArgumentNullException.ThrowIfNull(lines);

        var errors = new List<ApiDialectError>();
        var number = 0;

        foreach (var line in lines) {
            number++;

            if (line.TrimStart().StartsWith('#')) {
                continue;
            }

            if (NonNullableMarker.IsMatch(Literal.Replace(line, string.Empty))) {
                errors.Add(new(number, line.Trim()));
            }
        }

        return errors;
    }

    static readonly System.Text.RegularExpressions.Regex NonNullableMarker = new(
        """[\w>\])]!(?!=)""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );

    /// <summary>A C# string or character literal, as a constant's value is displayed.</summary>
    static readonly System.Text.RegularExpressions.Regex Literal = new(
        @"""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );
}

/// <summary>What a reading of the surface has that the baseline does not, and the reverse.</summary>
/// <param name="Added">Entries in the assembly that no baseline approves — an unapproved addition.</param>
/// <param name="Removed">Entries the baseline approves that the assembly no longer has — a break.</param>
public sealed record ApiDifference(IReadOnlyList<string> Added, IReadOnlyList<string> Removed) {
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;
}
