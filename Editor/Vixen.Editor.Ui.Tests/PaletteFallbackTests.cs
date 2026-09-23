// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>
///     Every <c>var(--token, literal)</c> whose token a palette underneath already declares, and whose
///     literal agrees with none of that token's declarations — by name, so the list can only shrink.
/// </summary>
/// <remarks>
///     <para>
///         <b>Dead twice over, and the second half is what makes it a defect rather than
///         redundancy.</b> A fallback fires only when the token is undeclared. <c>ControlTheme.vcss</c>
///         is the sheet every control arrives with and <c>EditorTheme.vcss</c> is the one every editor
///         sheet is installed over, so a fallback for a token either declares never fires where the
///         sheet is used. On its own that is dead code. But a literal that is none of the token's
///         values is a colour no stylesheet in the tree chose: the one circumstance in which the line
///         does anything — a host missing the palette — draws the element in it, and it looks
///         deliberate because it renders. <c>var(--warning, #e2b341)</c> sat on five asset-editor
///         rules like that; <c>#e2b341</c> is not <c>#9a6200</c>, <c>#d99a3c</c>, <c>#a26507</c> or
///         <c>#e0a33a</c>. See <c>Rikarin/Vixen#1351</c>.
///     </para>
///     <para>
///         ⚠ <b>A fallback that agrees with a declaration is passed by on purpose.</b>
///         <c>var(--radius-control, 4px)</c> is dead in the editor too, and harmless: the one host
///         that would reach it draws what the editor draws. The census is for the fallback that is
///         wrong, which is the one that costs something.
///     </para>
///     <para>
///         ⚠ <b>The same shape written twenty-three more times is why this is a ledger and not an
///         assertion of nothing.</b> <c>var(--danger, #f2696e)</c> seventeen times and
///         <c>var(--accent, #6ba4f2)</c> four times in <c>AssetEditorTheme.vcss</c> are the identical
///         finding for two other tokens — <c>#f2696e</c> is none of <c>--danger</c>'s four values and
///         <c>#6ba4f2</c> none of <c>--accent</c>'s five. ⚠ And it is not only colours:
///         <c>var(--radius-row, 6px)</c> in <c>WorldTheme.vcss</c> and <c>BrowserTheme.vcss</c> names
///         a radius <c>EditorTheme</c> declares as <c>4px</c>. They are a change of the same size and
///         the same look, and #1351 named the five <c>--warning</c> sites only, so they are recorded
///         here as owed rather than converted under its number.
///     </para>
/// </remarks>
public class PaletteFallbackTests {
    /// <summary>The fallbacks still owed, as <c>file: var(...) ×count</c>.</summary>
    static readonly string[] Remaining = [
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss: var(--accent, #6ba4f2) ×4",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss: var(--danger, #f2696e) ×17",
        "Editor/Vixen.Editor.App/WorldTheme.vcss: var(--radius-row, 6px) ×1",
        "Editor/Vixen.Editor.Inspector/BrowserTheme.vcss: var(--radius-row, 6px) ×1"
    ];

    [Fact]
    public void No_fallback_names_a_value_its_token_is_never_declared_as() {
        var root = RepositoryRoot();
        var sheets = Production(root).ToList();

        var declared = Declarations(sheets.Select(File.ReadAllText));

        var control = TokensOf(File.ReadAllText(Path.Combine(root, "Core/Vixen.Ui.Controls/ControlTheme.vcss")));
        var editor = TokensOf(File.ReadAllText(Path.Combine(root, "Editor/Vixen.Editor.Ui/Theming/EditorTheme.vcss")));

        var found = new List<string>();

        foreach (var sheet in sheets) {
            var relative = Path.GetRelativePath(root, sheet).Replace('\\', '/');
            var underneath = relative.StartsWith("Editor/", StringComparison.Ordinal)
                ? control.Union(editor).ToHashSet(StringComparer.Ordinal)
                : control;

            found.AddRange(
                WrongFallbacks(File.ReadAllText(sheet), underneath, declared)
                    .GroupBy(site => site, StringComparer.Ordinal)
                    .Select(group => $"{relative}: {group.Key} ×{group.Count()}")
            );
        }

        var unexpected = found.Except(Remaining, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var gone = Remaining.Except(found, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            unexpected.Count == 0,
            "These fallbacks name a token the palette underneath already declares, with a literal that is "
            + "none of its declared values — dead where the sheet is used, and a colour nobody chose where "
            + "it is not. Write the token bare (see Rikarin/Vixen#1351):\n  "
            + string.Join("\n  ", unexpected)
        );

        Assert.True(
            gone.Count == 0,
            "These ledger entries no longer match the sheets — correct or remove them, so the list only "
            + "shrinks:\n  "
            + string.Join("\n  ", gone)
        );
    }

    /// <summary>The instrument, over sheets whose answer is known.</summary>
    [Fact]
    public void The_sweep_reports_a_wrong_literal_and_passes_an_agreeing_one_a_nested_var_and_an_undeclared_token() {
        const string palette = """
            root { --warning: #9a6200; --radius: 4px; }
            root.dark { --warning: #D99A3C; }
            """;

        const string sheet = """
            /* var(--warning, #123456) in a comment is not a site */
            a-row { color: var(--warning, #e2b341); }
            b-row { color: var(--warning,#d99a3c); }
            c-row { border-radius: var(--radius, 4px); }
            d-row { color: var(--warning, var(--danger)); }
            e-row { color: var(--nobody-declares-this, #e2b341); }
            f-row { background-color: var(--warning, #E2B341); color: var(--warning); }
            """;

        var tokens = TokensOf(palette);
        var declared = Declarations([palette]);

        Assert.Equal(
            ["var(--warning, #e2b341)", "var(--warning, #e2b341)"],
            WrongFallbacks(sheet, tokens, declared)
        );
    }

    /// <summary>Every <c>var(--t, literal)</c> in a sheet whose token is underneath and whose literal is wrong.</summary>
    static List<string> WrongFallbacks(
        string css,
        IReadOnlySet<string> underneath,
        IReadOnlyDictionary<string, HashSet<string>> declared
    ) {
        var text = Uncommented(css);
        var found = new List<string>();

        foreach (Match site in Regex.Matches(text, @"var\(\s*(--[\w-]+)\s*,\s*([^()]+?)\s*\)")) {
            var token = site.Groups[1].Value;
            var literal = site.Groups[2].Value.Trim().ToLowerInvariant();

            if (!underneath.Contains(token)) {
                continue;
            }

            if (declared.TryGetValue(token, out var values) && values.Contains(literal)) {
                continue;
            }

            found.Add($"var({token}, {literal})");
        }

        return found;
    }

    /// <summary>The custom properties a sheet declares.</summary>
    static HashSet<string> TokensOf(string css) =>
        Regex.Matches(Uncommented(css), @"(?<![\w-])(--[\w-]+)\s*:")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every value every token is declared as anywhere in the given sheets, lower-cased.</summary>
    static Dictionary<string, HashSet<string>> Declarations(IEnumerable<string> sheets) {
        var declared = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var css in sheets) {
            foreach (Match match in Regex.Matches(Uncommented(css), @"(?<![\w-])(--[\w-]+)\s*:\s*([^;{}]+)")) {
                var token = match.Groups[1].Value;

                if (!declared.TryGetValue(token, out var values)) {
                    declared[token] = values = new HashSet<string>(StringComparer.Ordinal);
                }

                values.Add(match.Groups[2].Value.Trim().ToLowerInvariant());
            }
        }

        return declared;
    }

    static string Uncommented(string css) => Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    /// <summary>Every production stylesheet: the tree minus tests and build output.</summary>
    static IEnumerable<string> Production(string root) {
        foreach (var top in new[] { "Core", "Editor", "Samples", "Tools" }) {
            var directory = Path.Combine(root, top);

            if (!Directory.Exists(directory)) {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.vcss", SearchOption.AllDirectories)) {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

                if (relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains(".Tests/", StringComparison.Ordinal)) {
                    continue;
                }

                yield return file;
            }
        }
    }

    static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
