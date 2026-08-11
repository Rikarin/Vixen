// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets;
using Vixen.Editor.Core;

namespace Vixen.Cli;

/// <summary>How diagnostics are written.</summary>
public enum DiagnosticFormat {
    /// <summary>For a person at a terminal.</summary>
    Text,

    /// <summary>
    ///     For MSBuild, which parses <c>file: error CODE: text</c> out of a tool's standard output and
    ///     puts it in the IDE's error list and the CI log's summary.
    /// </summary>
    MSBuild
}

/// <summary>The codes this tool emits. Registered in <c>docs/manual/diagnostic-codes.md</c>.</summary>
/// <remarks>
///     <b>A code is the contract; the wording is not.</b> An error in a build log is searched for by
///     its code long after the sentence beside it has been reworded, and MSBuild will not put a line
///     in the error list without one.
/// </remarks>
public static class DiagnosticCode {
    /// <summary>An importer said something about an asset.</summary>
    public const string Import = "VX1001";

    /// <summary>The asset database found something while scanning.</summary>
    public const string Scan = "VX1002";

    /// <summary>The build plan found something.</summary>
    public const string Plan = "VX2001";

    /// <summary>The content builder found something.</summary>
    public const string Pack = "VX2002";

    /// <summary>The shader bundle build found something.</summary>
    public const string Shaders = "VX2003";

    /// <summary>The tool could not run at all.</summary>
    public const string Usage = "VX9001";
}

/// <summary>Writes what a command found, in the form its reader can use.</summary>
/// <remarks>
///     <para>
///         Two readers, one set of facts. A person wants the project-relative path and an aligned
///         column they can skim; MSBuild wants an absolute path, a code, and the exact word
///         <c>error</c> or <c>warning</c> — get that wrong and the line is prose in a log rather than
///         an entry in an error list that can be clicked.
///     </para>
///     <para>
///         <b>The path is absolute in the MSBuild form.</b> A relative one is resolved against
///         whatever directory the build happens to be running in, which is not the project's, so the
///         IDE opens nothing.
///     </para>
///     <para>
///         ⚠ <b>A third word, <c>advisory</c>, for a run whose verdict is provisional.</b> Only
///         <c>error</c> and <c>warning</c> make an MSBuild diagnostic, so writing anything else keeps
///         the line prose — and this deliberately is not "print less": the code and the absolute path
///         stay on it, so the same search finds it and a person reading the log learns what the pass
///         could not resolve. What it stops is the line reaching the error list and the CI summary,
///         which is where a reader is entitled to assume that everything listed is worth acting on.
///         The damage a provisional warning does is never its own; it is that it makes a real one
///         indistinguishable, and a build whose every clean run carries two <c>VX1001</c>s teaches
///         its readers to skip <c>VX1001</c>.
///     </para>
/// </remarks>
/// <param name="output">Where the lines go.</param>
/// <param name="format">Which reader they are written for.</param>
/// <param name="root">The project, for turning an asset's relative path into an absolute one.</param>
/// <param name="advisory">
///     Whether this run is allowed to be wrong — see the remarks. Only <c>vixen import --advisory</c>
///     sets it, and only <c>Vixen.Sdk</c>'s pre-compile pass passes that.
/// </param>
public sealed class DiagnosticWriter(
    TextWriter output,
    DiagnosticFormat format,
    string? root = null,
    bool advisory = false
) {
    /// <summary>How it is writing.</summary>
    public DiagnosticFormat Format => format;

    /// <summary>Says something about one asset.</summary>
    /// <param name="severity">How much attention it needs.</param>
    /// <param name="path">The asset, project-relative.</param>
    /// <param name="code">Which diagnostic it is.</param>
    /// <param name="message">What to say.</param>
    public void Asset(ImportSeverity severity, string path, string code, string message) {
        if (format == DiagnosticFormat.Text) {
            output.WriteLine($"  {Word(severity, advisory)} {path}: {message}");
            return;
        }

        var file = root is { Length: > 0 }
            ? Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))
            : path;

        // An information-level line is not an error-list entry, and dressing it as one would put
        // "this project has no addressable assets" in a CI failure summary.
        output.WriteLine(
            severity == ImportSeverity.Information
                ? $"{file}: {message}"
                : $"{file}: {Word(severity, advisory).TrimEnd()} {code}: {message}"
        );
    }

    /// <summary>Says something about the project rather than about one asset in it.</summary>
    /// <param name="severity">How much attention it needs.</param>
    /// <param name="code">Which diagnostic it is.</param>
    /// <param name="message">What to say.</param>
    /// <remarks>
    ///     No file, which MSBuild attributes to the project being built. The build plan's messages
    ///     name the asset they are about inside their text — a person can act on them, and only the
    ///     IDE's jump-to-file loses. See the README for why that is not fixed here.
    /// </remarks>
    public void Project(ImportSeverity severity, string code, string message) {
        if (format == DiagnosticFormat.Text) {
            output.WriteLine($"  {Word(severity, advisory)} {message}");
            return;
        }

        output.WriteLine(
            severity == ImportSeverity.Information ? message : $"{Word(severity, advisory).TrimEnd()} {code}: {message}"
        );
    }

    /// <summary>Says something that is neither, like a summary line.</summary>
    /// <param name="message">What to say.</param>
    public void Line(string message) => output.WriteLine(message);

    /// <summary>The severity an asset-database issue carries.</summary>
    /// <param name="kind">What the scan found.</param>
    /// <returns>How much attention it needs.</returns>
    public static ImportSeverity SeverityOf(AssetIssueKind kind) =>
        kind switch {
            AssetIssueKind.MetaUnreadable or AssetIssueKind.DuplicateGuid => ImportSeverity.Warning,
            _ => ImportSeverity.Information
        };

    /// <summary>The word that decides whether a line is a diagnostic or prose.</summary>
    /// <remarks>
    ///     Both columns are seven characters wide so the human form stays aligned, and
    ///     <c>advisory</c> is eight — which is the one place the alignment is deliberately given up,
    ///     because a reader skimming that column should notice the odd one out.
    /// </remarks>
    static string Word(ImportSeverity severity, bool advisory = false) =>
        (severity, advisory) switch {
            (ImportSeverity.Information, _) => "note   ",
            (_, true) => "advisory",
            (ImportSeverity.Error, _) => "error  ",
            _ => "warning"
        };
}
