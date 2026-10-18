// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;

namespace Vixen.Build;

/// <summary>
///     Doc 50 § D6's <c>ModuleRegistrationRule</c>: an assembly that declares an <c>IEditorPlugin</c>
///     registering round the side of <c>PluginContext</c> — straight into the shell, the theme or the
///     shared services bag — fails <c>CheckArchitecture</c>, outside a shrink-only exemption list.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why it matters.</b> <c>PluginContext.AddPanel</c>, <c>AddMode</c>, <c>AddCommand</c>,
///         <c>AddLayout</c> and <c>With</c> exist to record the removal, so that unloading a module
///         takes back everything it put in. A module that calls <c>Shell.RegisterPanel</c> leaves a
///         panel behind on unload; one that calls <c>context.Services.Add</c> leaves a service the
///         next activation cannot publish again, because <c>PluginServices.Add</c> throws on a
///         duplicate — which is exactly why <c>TerrainPhysicsModule</c> cannot be reloaded. A
///         <c>*Theme.Install</c> is a stylesheet nothing ever removes.
///     </para>
///     <para>
///         ⚠ <b><c>Shell.Commands.Add</c> counts even when an <c>OnUnload</c> removes it.</b> The
///         pairing is correct and is also a second spelling of <c>context.AddCommand</c>, which a
///         reader has to check by hand every time; the row's reason says when it is paired. Telling
///         a paired call from an unpaired one is a data-flow question a grep cannot answer honestly.
///     </para>
///     <para>
///         <b>Subjects are derived:</b> every non-test project, anywhere in the tree, holding a class
///         that implements <c>IEditorPlugin</c> — the editor's modules, the script host's
///         <c>ScriptModule</c>, and the <c>vixen-plugin</c> template, which is what a third party
///         copies and so must not teach the side door. The contract itself
///         (<c>Vixen.Editor.Plugin</c>) is not a subject: <c>PluginContext</c>'s wrappers are the
///         calls this rule sends everyone else to.
///     </para>
/// </remarks>
static class ModuleRegistrationRule {
    /// <summary>The exemption list, relative to the repository root.</summary>
    public const string ExemptionFile = "docs/ModuleRegistrationExempt.txt";

    /// <summary>The command that rewrites the list from the tree.</summary>
    public const string Regenerate = "./build.sh CheckArchitecture --update-exemptions";

    /// <summary>The contract, whose wrappers make the calls on everyone's behalf.</summary>
    public const string Contract = "Vixen.Editor.Plugin";

    static readonly Regex Plugin = new(@"\bclass\s+\w+[^{;]*?:[^{;]*\bIEditorPlugin\b", RegexOptions.CultureInvariant);

    /// <summary>Each side door, and the name its rows carry.</summary>
    static readonly (Regex Pattern, Func<Match, string> Name)[] SideDoors = [
        (new(@"\b[Ss]hell\.RegisterPanel\s*\(", RegexOptions.CultureInvariant), _ => "Shell.RegisterPanel"),
        (new(@"\b[Ss]hell\.RegisterLayout\s*\(", RegexOptions.CultureInvariant), _ => "Shell.RegisterLayout"),
        (new(@"\b[Ss]hell\.Modes\.Add\s*\(", RegexOptions.CultureInvariant), _ => "Shell.Modes.Add"),
        (new(@"\b[Ss]hell\.Commands\.Add\s*\(", RegexOptions.CultureInvariant), _ => "Shell.Commands.Add"),
        (new(@"\b(\w*Theme)\.Install\s*\(", RegexOptions.CultureInvariant), match => match.Groups[1].Value + ".Install"),
        (new(@"\bServices\.Add\s*[<(]", RegexOptions.CultureInvariant), _ => "Services.Add")
    ];

    /// <summary>The projects that declare a module, by directory relative to the root.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Directories (forward slashes), ordered.</returns>
    public static List<string> Subjects(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var subjects = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var top in Directory.EnumerateDirectories(root)) {
            var name = Path.GetFileName(top);

            if (name.StartsWith('.') || name is "build" or "docs" or "www" or "references") {
                continue;
            }

            foreach (var project in FeatureNameRule.Walk(top, file => file.EndsWith(".csproj", StringComparison.Ordinal))) {
                var directory = Path.GetDirectoryName(project)!;
                var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');

                if (Path.GetFileName(directory) == Contract) {
                    continue;
                }

                if (FeatureNameRule.Sources(directory, markup: false)
                    .Select(File.ReadAllText)
                    .Any(text => text.Contains("IEditorPlugin", StringComparison.Ordinal) && Plugin.IsMatch(FeatureNameRule.Lex(text).Code))) {
                    subjects.Add(relative);
                }
            }
        }

        return [.. subjects];
    }

    /// <summary>Every side door every subject opens.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="subjects">What <see cref="Subjects" /> found.</param>
    /// <returns>One finding per file and door, ordered.</returns>
    public static List<FeatureNameRule.Finding> Scan(string root, IEnumerable<string> subjects) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(subjects);

        var findings = new List<FeatureNameRule.Finding>();

        foreach (var subject in subjects) {
            var directory = Path.Combine(root, subject.Replace('/', Path.DirectorySeparatorChar));

            foreach (var file in FeatureNameRule.Sources(directory)) {
                var text = File.ReadAllText(file);

                if (file.EndsWith(".vxml", StringComparison.Ordinal)) {
                    text = Regex.Replace(text, "<!--.*?-->", " ", RegexOptions.Singleline);
                }

                var code = FeatureNameRule.Lex(text).Code;
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

                foreach (var (name, count) in Doors(code)) {
                    findings.Add(new(relative, name, count));
                }
            }
        }

        return [.. findings.OrderBy(finding => finding.Key, StringComparer.Ordinal)];
    }

    /// <summary>The side doors one file's code opens.</summary>
    /// <param name="code">The code stream, comments and literals gone.</param>
    /// <returns>Door → count, ordered.</returns>
    public static SortedDictionary<string, int> Doors(string code) {
        ArgumentNullException.ThrowIfNull(code);

        var doors = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var (pattern, name) in SideDoors) {
            foreach (Match match in pattern.Matches(code)) {
                var door = name(match);
                doors[door] = doors.GetValueOrDefault(door) + 1;
            }
        }

        return doors;
    }

    /// <summary>Reads the committed list, or an empty one.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Key → (count, reason).</returns>
    public static Dictionary<string, (int Count, string Reason)> Exempt(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var path = Path.Combine(root, ExemptionFile.Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(path) ? FeatureNameRule.Parse(File.ReadAllText(path)) : new(StringComparer.Ordinal);
    }

    /// <summary>Why the rule is checking nothing, if it is.</summary>
    /// <param name="subjects">What <see cref="Subjects" /> found.</param>
    /// <returns>The reason, or <see langword="null" />.</returns>
    public static string? Vacuity(IReadOnlyCollection<string> subjects) {
        ArgumentNullException.ThrowIfNull(subjects);

        return subjects.Count == 0
            ? "No project declares a class implementing IEditorPlugin, so the module-registration rule is checking "
            + "nothing — the editor ships seven built-in modules, so the walk is reading the wrong tree."
            : null;
    }

    /// <summary>Everything wrong with the modules against the list.</summary>
    /// <param name="findings">What <see cref="Scan" /> found.</param>
    /// <param name="exempt">The list.</param>
    /// <returns>One sentence per violation.</returns>
    public static List<string> Violations(
        IReadOnlyList<FeatureNameRule.Finding> findings,
        IReadOnlyDictionary<string, (int Count, string Reason)> exempt
    ) {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(exempt);

        var violations = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var finding in findings) {
            seen.Add(finding.Key);

            if (!exempt.TryGetValue(finding.Key, out var row)) {
                violations.Add(
                    $"{finding.File} calls {finding.Name} ({finding.Count}×) from an assembly that declares an IEditorPlugin. "
                    + "Register through PluginContext — AddPanel, AddMode, AddCommand, AddLayout, With — so unloading the "
                    + "module takes it back (doc 50 § F7)."
                );
            } else if (finding.Count > row.Count) {
                violations.Add(
                    $"{finding.File} calls {finding.Name} {finding.Count}× and {ExemptionFile} allows {row.Count}. The "
                    + "list may only shrink."
                );
            } else if (finding.Count < row.Count) {
                violations.Add(
                    $"{finding.File} calls {finding.Name} {finding.Count}× and {ExemptionFile} still says {row.Count}. "
                    + $"Lower the count (or run `{Regenerate}`)."
                );
            }
        }

        foreach (var key in exempt.Keys.Where(key => !seen.Contains(key)).Order(StringComparer.Ordinal)) {
            violations.Add(
                $"{ExemptionFile} lists '{key}', which no longer goes round PluginContext. Delete the line — the list "
                + "may only shrink."
            );
        }

        return violations;
    }

    /// <summary>The list, written from the findings, keeping each surviving row's reason.</summary>
    /// <param name="findings">What <see cref="Scan" /> found.</param>
    /// <param name="previous">The list as it was.</param>
    /// <returns>The file's text.</returns>
    public static string Render(
        IReadOnlyList<FeatureNameRule.Finding> findings,
        IReadOnlyDictionary<string, (int Count, string Reason)> previous
    ) {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(previous);

        var text = new StringBuilder();

        text.Append("# Registrations an editor module makes round the side of PluginContext (doc 50 § D6, #1281). Read by\n")
            .Append("# CheckArchitecture through build/ModuleRegistrationRule.cs. `<file> <door> <count> [reason]`.\n")
            .Append("#\n")
            .Append("# ⚠ This list can only SHRINK: a new door fails, a count above its row fails, and a row above its file's\n")
            .Append("# count, or for a door the file no longer opens, fails too. Subjects are every non-test project holding a\n")
            .Append("# class that implements IEditorPlugin; the doors are Shell.RegisterPanel / RegisterLayout / Modes.Add /\n")
            .Append("# Commands.Add, any *Theme.Install, and Services.Add.\n")
            .Append("#\n")
            .Append("# Regenerate after a merge with `").Append(Regenerate).Append("`, and read the diff.\n\n");

        foreach (var finding in findings) {
            text.Append(finding.File).Append(' ').Append(finding.Name).Append(' ').Append(finding.Count);

            if (previous.TryGetValue(finding.Key, out var row) && row.Reason.Length > 0) {
                text.Append(' ').Append(row.Reason);
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}
