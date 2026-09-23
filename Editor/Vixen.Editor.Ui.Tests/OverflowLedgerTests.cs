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
///         list. Twenty-three of the twenty-four are closed; the one below says why it is not.
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
///     <para>
///         ⚠ <b>A rule block is not the only way to write the declaration.</b>
///         <c>overflow-y-auto</c> is a registered utility class, so the same defect can be spelt on
///         the element and a sweep of the sheets alone would never see it — which is what
///         <c>No_production_source_asks_for_a_scroll_with_a_utility_class_either</c> is for. It
///         reads <c>.vxml</c> as well as <c>.cs</c>, because a <c>class=</c> attribute is where such
///         a class would be written.
///     </para>
/// </remarks>
public class OverflowLedgerTests {
    /// <summary>The rules still to convert, as <c>file:selector</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         Each is a plain element declaring a scroll container it will never be. The cure for
    ///         each is a <c>ScrollView</c> — the element under the tag, with <c>overflow: hidden;
    ///         position: relative</c> written on the rule since the user-agent rule cannot reach it
    ///         — and never a taller box, which moves the first unreachable row rather than reaching
    ///         it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sixteen became seven, and the nine that went were one shape: the asset editors'
    ///         fixed-width <c>*-side</c> field columns.</b> Every one of them sits in a
    ///         <c>flex-direction: row</c> body, so the column's height is its parent's and a
    ///         <c>ScrollView</c> there fills and scrolls without needing a <c>flex-basis</c> — which
    ///         is what makes them one batch rather than nine judgements.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>settings-rail</c> and <c>settings-pane</c> went next, and their reason was
    ///         the child combinators.</b> A <c>ScrollView</c> puts a <c>scroll-content</c> between the
    ///         box and its rows, so <c>settings-rail &gt; button.settings-tab</c> and
    ///         <c>settings-pane &gt; .filtered-out</c> became <c>… &gt; scroll-content &gt; …</c> — and
    ///         the picture caught a third thing the combinators did not: the tabs' <c>width: 100%</c>
    ///         had nothing definite to take a share of inside the content and the selected tab's
    ///         highlight shrank to its label. <c>ScrollingPanelPictureTests</c> holds the page.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>sprite-list</c> was the first of the column ones, and the column was not the
    ///         hard part.</b> The list got its <c>flex-basis: 0px</c>, and still could not scroll,
    ///         because nothing above it was height-bound: the texture document's tab set was not a
    ///         <c>document-tabs</c> set, so the sprite editor grew to 1 512 px in a 555 px tab and
    ///         the dock panel did the clipping. It is one now, and the picture test opens a real
    ///         texture on its Sprites tab and holds the list to the bottom of its document.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>compiled-scene-blocks</c> and <c>compiled-scene-diagnostics</c> were closed
    ///         by removing the declaration, and the ledger's premise for them was wrong.</b> They never
    ///         lost a row: the scene document's dock panel scrolls as a whole and
    ///         <c>dock-panel.scrolls &gt; *</c> keeps the tab set from shrinking, so the tables were
    ///         always as tall as their rows and the panel's bar reached the last one. A
    ///         <c>ScrollView</c> there was tried and its bar never appeared. The document is
    ///         pixel-identical without the declaration; what changed is the 7009 line on every open.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>mixer-strips</c> was the first sideways one.</b> The view keeps its column and
    ///         the strips run in a row in its content, with <c>min-height: 100%</c> there — a scroll
    ///         content is only as tall as what it holds, so without it every fader shrank to its
    ///         120 px floor (142 px measured, in a 521 px mixer).
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>input-debug</c> went the compiled scene's way, and its recorded reason was
    ///         beside the point.</b> It was held back because a component's host cannot be given a
    ///         control's type from the sheet — true, and irrelevant: its dock panel scrolls as a whole,
    ///         the view is the panel's direct child and so never shrinks, and the panel's bar reached
    ///         every row. The declaration went; the panel is pixel-identical and the 7009 line is gone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The one that is left is left for a reason, not for want of time.</b>
    ///         <c>override-body</c> carries a <b>child-combinator</b> rule
    ///         (<c>override-body &gt; override-row</c>), scrolls sideways, and lives in
    ///         <c>ImportSettingsView</c> — which in the texture document's Texture tab sits below the
    ///         mip ladder, past the tab's bottom edge at 1600×1000 with nothing to scroll the tab. So it
    ///         wants a model document, or that tab fixed, before it can be pictured.
    ///     </para>
    /// </remarks>
    static readonly string[] Remaining = [
        "Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss:override-body"
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

    /// <summary>The same defect spelt as a utility class, which a sweep of the sheets cannot see.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>overflow-y-auto</c> is a real class here and produces the identical
    ///         unreachable tail.</b> The utility families register all five keywords on all three
    ///         properties (<c>UtilityFamilies.cs:2648</c>), so
    ///         <c>class="overflow-y-auto"</c> on a panel asks for exactly what the sixteen ledger
    ///         rules ask for — and the ledger above, which parses <c>.vcss</c> rule blocks, would
    ///         stay green while the box clipped. Only the run-time 7009 line would say so.
    ///     </para>
    ///     <para>
    ///         Nothing uses one today, so this is a hole being closed rather than a defect being
    ///         found, and the expected answer is <i>nothing</i> rather than a ledger. ⚠ And it reads
    ///         <c>.vxml</c> as well as <c>.cs</c>: a view's <c>class=</c> attribute is where such a
    ///         class would actually be written, and a sweep that read only <c>.cs</c> would report a
    ///         clean tree whatever the markup said.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_production_source_asks_for_a_scroll_with_a_utility_class_either() {
        var root = RepositoryRoot();
        var found = new List<string>();

        foreach (var source in Sources(root)) {
            foreach (var name in ScrollingClasses(File.ReadAllText(source))) {
                found.Add($"{Path.GetRelativePath(root, source).Replace('\\', '/')}:{name}");
            }
        }

        Assert.True(
            found.Count == 0,
            "These utility classes ask a plain box to scroll, and in this UI that clips and never "
            + "scrolls — the same defect as the rules above, spelt on the element. Put a ScrollView "
            + "there (see Rikarin/Vixen#1275):\n  "
            + string.Join("\n  ", found.Order(StringComparer.Ordinal))
        );
    }

    /// <summary>The instrument for the class sweep, over a source whose answer is known.</summary>
    [Fact]
    public void The_class_sweep_reads_markup_and_code_and_passes_prose_about_the_class_by() {
        const string markup = """
            <!-- a comment mentioning overflow-y-auto is not a use of it -->
            <Panel class="gap-2 overflow-y-auto rounded" />
            <Panel class="overflow-hidden" />
            """;

        const string code = """
            // and neither is overflow-x-scroll in a line comment
            /* nor overflow-auto in a block one */
            list.AddClass("overflow-scroll");
            list.AddClass("overflow-clip");
            """;

        Assert.Equal(["overflow-y-auto"], ScrollingClasses(markup));
        Assert.Equal(["overflow-scroll"], ScrollingClasses(code));
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

    /// <summary>Every scrolling utility class named in a source file, once each.</summary>
    /// <remarks>
    ///     Comments first, in all three spellings this tree writes them, so prose <i>about</i> the
    ///     class is not read as a use of it: <c>UtilityFamilies.cs:2648</c> names
    ///     <c>overflow-auto</c> while explaining what it used to be, and is the only place in
    ///     production that says the word at all.
    /// </remarks>
    static List<string> ScrollingClasses(string source) {
        var text = Regex.Replace(source, @"/\*.*?\*/|<!--.*?-->", string.Empty, RegexOptions.Singleline);
        text = Regex.Replace(text, @"//[^\r\n]*", string.Empty);

        return Regex
            .Matches(text, @"(?<![\w-])overflow(?:-[xy])?-(?:auto|scroll)(?![\w-])")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Every production stylesheet: the tree minus tests, build output and samples' data.</summary>
    static IEnumerable<string> Sheets(string root) => Production(root, "*.vcss");

    /// <summary>Every production source file a class can be written in — ⚠ <c>.vxml</c> as well as <c>.cs</c>.</summary>
    static IEnumerable<string> Sources(string root) =>
        Production(root, "*.cs").Concat(Production(root, "*.vxml"));

    static IEnumerable<string> Production(string root, string pattern) {
        foreach (var top in new[] { "Core", "Editor", "Samples", "Tools" }) {
            var directory = Path.Combine(root, top);

            if (!Directory.Exists(directory)) {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)) {
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
