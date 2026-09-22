// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>Every production rule that asks a plain box to scroll, by name, so the list can only shrink.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>overflow: auto</c> and <c>overflow: scroll</c> clip and do not scroll in this UI</b>
///         — the layout bridge reads both as a scroll container (the min-content floor is dropped, a
///         gutter is reserved), the draw list clips, and nothing moves the content: the one scroller
///         is <c>ScrollView</c>, which is styled <c>overflow: hidden</c> and drives its own bars. So
///         every such rule on something that is not a <c>ScrollView</c> is a list that silently
///         loses its tail. The New Asset… picker sat behind one for as long as it had more than six
///         kinds (#1275), and the issue counted six more. ⚠ <b>The count was twenty-four</b>: the
///         issue grepped the shorthand, and <c>overflow-y: auto</c> is the same defect spelt for one
///         axis — sixteen more rules across the two editor themes, every one on a side panel or a
///         list.
///     </para>
///     <para>
///         <b>This is the build-time half of the report; <c>UiDocument</c>'s 7009 is the run-time
///         half</b> and names each box as it is styled. The two answer different questions: the log
///         says which box on <i>this</i> screen is cut off, and this says which rules in the tree
///         still ask for it. Written like <c>docs/WhitespaceExempt.txt</c> — the remaining sites are
///         named, a new one fails, and a converted one that is still listed fails too, so the ledger
///         cannot drift from the sheets.
///     </para>
///     <para>
///         ⚠ <b>A rule on a <c>ScrollView</c>'s own tag is not a site.</b> <c>choice-scroller</c>,
///         <c>console-detail</c>, <c>moveset-table</c> and the rest are scroll views under the
///         sheet's tags and write <c>overflow: hidden</c>, which is what the user-agent rule would
///         write for them under their own — so a sweep for <c>auto|scroll</c> passes them by
///         without needing to know what they are.
///     </para>
/// </remarks>
public class OverflowLedgerTests {
    /// <summary>The rules still to convert, as <c>file:selector</c>.</summary>
    /// <remarks>
    ///     Each is a plain element declaring a scroll container it will never be. The cure for each
    ///     is a <c>ScrollView</c> — the element under the tag, with <c>overflow: hidden; position:
    ///     relative</c> written on the rule since the user-agent rule cannot reach it — and never a
    ///     taller box, which moves the first unreachable row rather than reaching it.
    /// </remarks>
    static readonly string[] Remaining = [
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:compiled-scene-blocks, compiled-scene-diagnostics",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:override-body",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:sprite-list",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:shadergraph-side",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:vfx-side",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:animation-side, animgraph-side, input-side, mixer-side, font-side, sequence-side",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:harness-side",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:moveset-side",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:shape-side",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:vocab-side",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:mixer-strips",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:agent-debugger-agents",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:agent-debugger-detail",
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:input-debug",
        "Editor/Vixen.Editor.Ui/Theming/EditorTheme.vcss:settings-rail",
        "Editor/Vixen.Editor.Ui/Theming/EditorTheme.vcss:settings-pane"
    ];

    [Fact]
    public void Every_rule_asking_a_plain_box_to_scroll_is_named_here_and_nowhere_else() {
        var root = RepositoryRoot();
        var found = new List<string>();

        foreach (var sheet in Sheets(root)) {
            foreach (var selector in ScrollingSelectors(File.ReadAllText(sheet))) {
                found.Add($"{Path.GetRelativePath(root, sheet).Replace('\\', '/')}:{selector}");
            }
        }

        var unexpected = found.Except(Remaining, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var converted = Remaining.Except(found, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            unexpected.Count == 0,
            "These rules ask a plain box to scroll, and in this UI `overflow: auto` clips and never scrolls. "
            + "Put a ScrollView there (see Rikarin/Vixen#1275) rather than adding to the ledger:\n  "
            + string.Join("\n  ", unexpected)
        );

        Assert.True(
            converted.Count == 0,
            "These ledger entries no longer match a rule — take them out, so the list only shrinks:\n  "
            + string.Join("\n  ", converted)
        );
    }

    /// <summary>The instrument, checked against a sheet whose answer is known.</summary>
    [Fact]
    public void The_sweep_reads_the_shorthand_and_both_longhands_and_passes_a_named_clip_by() {
        const string sheet = """
            /* a comment saying overflow: auto is not a rule */
            a-list { flex-direction: column; overflow: auto; }
            b-list { overflow-y: auto; }
            c-list,
            d-list { height: 10px; overflow-x: scroll; }
            e-pane {
                width: 10px;
                overflow: hidden;
                position: relative;
            }
            f-pane { overflow: clip; }
            g-pane { overflow-anchor: none; }
            """;

        Assert.Equal(["a-list", "b-list", "c-list, d-list"], ScrollingSelectors(sheet));
    }

    /// <summary>
    ///     The selectors of every rule in a sheet whose block declares <c>overflow[-x|-y]: auto|scroll</c>.
    /// </summary>
    static List<string> ScrollingSelectors(string css) {
        // Comments first, so that prose about the property is not counted as a rule declaring it.
        var text = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var found = new List<string>();

        foreach (Match rule in Regex.Matches(text, @"([^{}]+)\{([^{}]*)\}")) {
            var block = rule.Groups[2].Value;

            if (Regex.IsMatch(block, @"(?<![\w-])overflow(?:-x|-y)?\s*:\s*(?:auto|scroll)\b")) {
                var selector = Regex.Replace(rule.Groups[1].Value.Trim(), @"\s*\r?\n\s*", " ");
                found.Add(selector);
            }
        }

        return found;
    }

    /// <summary>Every production stylesheet: the tree minus tests, build output and samples' data.</summary>
    static IEnumerable<string> Sheets(string root) {
        foreach (var top in new[] { "Core", "Editor", "Samples", "Tools" }) {
            var directory = Path.Combine(root, top);

            if (!Directory.Exists(directory)) {
                continue;
            }

            foreach (var sheet in Directory.EnumerateFiles(directory, "*.vcss", SearchOption.AllDirectories)) {
                var relative = Path.GetRelativePath(root, sheet).Replace('\\', '/');

                if (relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains(".Tests/", StringComparison.Ordinal)) {
                    continue;
                }

                yield return sheet;
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
