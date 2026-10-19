// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;

namespace Vixen.Build;

/// <summary>
///     Doc 50 § D6's <c>ApplicationSizeRule</c>: every file holding a part of
///     <c>partial class EditorApplication</c> has a committed line ceiling, and the ceilings may only
///     go down.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a ceiling rather than the target.</b> Doc 36 § P3's exit criterion —
///         <c>EditorApplication.cs</c> under 800 — was recorded at 3,601, 3,641, 3,675, 3,787, 4,593,
///         5,282 and 5,305 and was 5,826 when this rule was written; every phase that gave the
///         application something to own added tens of lines and nothing failed. A target nobody is
///         at asserts nothing until the day it is met. A ceiling at today's size fails the first
///         commit that grows it.
///     </para>
///     <para>
///         ⚠ <b>Both directions, the <c>CheckWhitespace</c> shape.</b> A file over its row fails; a
///         row more than <see cref="Slack" /> lines above its file fails too, so the table cannot
///         become a description of history — an extraction that moved four hundred lines out is
///         told to lower its row in the same commit. And a <c>(total)</c> row over all the parts,
///         with a part not in the table failing as well, is what stops the obvious evasion: moving a
///         verb from <c>EditorParity.cs</c> into a new <c>partial</c> file shrinks a row and moves
///         nothing.
///     </para>
///     <para>
///         ⚠ <b>Lines as <c>wc -l</c> counts them, comments included, and that is deliberate.</b>
///         Thirty-nine per cent of the class is comment, so a commit that adds only a remark can fail
///         this rule — a false positive by one reading, accepted on purpose: every figure doc 50 and
///         doc 36 cite is <c>wc -l</c>, and a rule that counted something else would disagree with
///         the documents it enforces. What it cannot see is the other way round: a verb moved into a
///         new <c>internal static class</c> beside the application lowers a ceiling without leaving
///         <c>Vixen.Editor.App</c>. That is <c>FeatureNameRule</c>'s and review's to catch, not this.
///     </para>
/// </remarks>
static class ApplicationSizeRule {
    /// <summary>Where the application's parts live, relative to the repository root.</summary>
    public const string Directory = "Editor/Vixen.Editor.App";

    /// <summary>The committed table, relative to the repository root.</summary>
    public const string TableFile = "build/ApplicationSizeCeilings.txt";

    /// <summary>The row the sum of every part is held to.</summary>
    public const string Total = "(total)";

    /// <summary>How far a row may sit above its file before it has to be lowered.</summary>
    /// <remarks>
    ///     Not zero, so that deleting a stale remark does not demand a table edit; small, so that any
    ///     extraction worth the name does.
    /// </remarks>
    public const int Slack = 20;

    /// <summary>The command that rewrites the table from the tree.</summary>
    public const string Regenerate = "./build.sh CheckArchitecture --update-exemptions";

    static readonly Regex Declaration = new(@"\bpartial\s+class\s+EditorApplication\b", RegexOptions.CultureInvariant);

    /// <summary>Every part of the class, by path under <see cref="Directory" />, with its line count.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Path (forward slashes) → lines, ordered.</returns>
    public static SortedDictionary<string, int> Measure(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var directory = Path.Combine(root, Directory.Replace('/', Path.DirectorySeparatorChar));
        var parts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        if (!System.IO.Directory.Exists(directory)) {
            return parts;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');

            // Build output is not source; a generated copy under obj/ would be a second row.
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)) {
                continue;
            }

            var text = File.ReadAllText(file);

            if (Declaration.IsMatch(text)) {
                parts[relative] = Lines(text);
            }
        }

        return parts;
    }

    /// <summary>Lines as <c>wc -l</c> counts them, plus one for a last line with no newline.</summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The count.</returns>
    public static int Lines(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var count = 0;

        foreach (var character in text) {
            if (character == '\n') {
                count++;
            }
        }

        return text.Length > 0 && text[^1] != '\n' ? count + 1 : count;
    }

    /// <summary>Reads the table.</summary>
    /// <param name="text">The file's text.</param>
    /// <returns>Row → ceiling. Blank and <c>#</c> lines are dropped.</returns>
    /// <exception cref="FormatException">A row with no number after its name.</exception>
    public static Dictionary<string, int> Parse(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var rows = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var raw in text.Split('\n')) {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length < 2 || !int.TryParse(fields[1], out var ceiling)) {
                throw new FormatException($"'{line}' in {TableFile} is not '<file> <ceiling>'.");
            }

            rows[fields[0]] = ceiling;
        }

        return rows;
    }

    /// <summary>Reads the committed table, or an empty one when it is not there.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Row → ceiling.</returns>
    public static Dictionary<string, int> Read(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var path = Path.Combine(root, TableFile.Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(path) ? Parse(File.ReadAllText(path)) : new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>Why the rule is checking nothing, if it is.</summary>
    /// <param name="parts">What <see cref="Measure" /> found.</param>
    /// <param name="ceilings">The table.</param>
    /// <returns>The reason, or <see langword="null" /> when there is a real subject.</returns>
    public static string? Vacuity(IReadOnlyDictionary<string, int> parts, IReadOnlyDictionary<string, int> ceilings) {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(ceilings);

        if (parts.Count == 0) {
            return $"No file under {Directory} declares partial class EditorApplication, so the size rule is "
                + "checking nothing. If the class has really gone, delete the rule and its table in the same "
                + "commit — doc 50 § P2's exit is that day.";
        }

        return ceilings.Count == 0 ? $"{TableFile} has no rows, so every part is unbounded." : null;
    }

    /// <summary>Everything wrong with the parts against the table.</summary>
    /// <param name="parts">What <see cref="Measure" /> found.</param>
    /// <param name="ceilings">The table.</param>
    /// <returns>One sentence per violation.</returns>
    public static List<string> Violations(IReadOnlyDictionary<string, int> parts, IReadOnlyDictionary<string, int> ceilings) {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(ceilings);

        var violations = new List<string>();

        foreach (var (file, lines) in parts.Append(new(Total, parts.Values.Sum()))) {
            if (!ceilings.TryGetValue(file, out var ceiling)) {
                violations.Add(
                    $"{Directory}/{file} holds part of EditorApplication and has no row in {TableFile}. A new "
                    + "partial file moves lines rather than removing them — move the verb out of the class "
                    + $"(doc 50 § D2), or, if the file is genuinely new state, add its row with `{Regenerate}` "
                    + "and say why in the commit."
                );

                continue;
            }

            if (lines > ceiling) {
                violations.Add(
                    $"{(file == Total ? "EditorApplication, all parts" : $"{Directory}/{file}")} is {lines} lines "
                    + $"and its ceiling is {ceiling}. The ceilings in {TableFile} may only go down: a use case "
                    + "added to the application belongs on a service (doc 50 § D2), not on the composition root."
                );
            } else if (ceiling - lines > Slack) {
                violations.Add(
                    $"{(file == Total ? "EditorApplication, all parts" : $"{Directory}/{file}")} is {lines} lines "
                    + $"and its ceiling is still {ceiling}. Lower the row to {lines} in {TableFile} (or run "
                    + $"`{Regenerate}`) — a ceiling above the file is room to grow back into, and the table "
                    + "may only shrink."
                );
            }
        }

        foreach (var row in ceilings.Keys.Where(row => row != Total && !parts.ContainsKey(row))) {
            violations.Add(
                $"{TableFile} has a row for {row}, which no longer holds part of EditorApplication. Delete the "
                + "row — a ceiling for a file that is gone is a rule asserting nothing."
            );
        }

        return violations;
    }

    /// <summary>The table, written from what the tree holds now.</summary>
    /// <param name="parts">What <see cref="Measure" /> found.</param>
    /// <returns>The file's text.</returns>
    public static string Render(IReadOnlyDictionary<string, int> parts) {
        ArgumentNullException.ThrowIfNull(parts);

        var text = new StringBuilder();

        text.Append("# Line ceilings for every file holding part of `partial class EditorApplication` (doc 50 § D6,\n")
            .Append("# #1279). Read by CheckArchitecture through build/ApplicationSizeRule.cs.\n")
            .Append("#\n")
            .Append("# ⚠ These may only go DOWN. A file over its row fails; a row more than ")
            .Append(Slack)
            .Append(" lines above its file\n")
            .Append("# fails too, so an extraction lowers its rows in the same commit; a new partial file fails until\n")
            .Append("# it has a row, and (total) holds the sum. Lines are `wc -l`, comments included.\n")
            .Append("#\n")
            .Append("# Regenerate after a merge with `").Append(Regenerate).Append("` — and read the diff:\n")
            .Append("# a row that went UP is the application growing.\n\n");

        var width = parts.Keys.Append(Total).Max(key => key.Length) + 2;

        foreach (var (file, lines) in parts) {
            text.Append(file.PadRight(width)).Append(lines).Append('\n');
        }

        text.Append(Total.PadRight(width)).Append(parts.Values.Sum()).Append('\n');

        return text.ToString();
    }
}
