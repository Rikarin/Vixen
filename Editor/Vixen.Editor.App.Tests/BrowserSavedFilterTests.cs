// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20 § B1's saved filters: a named search that survives a restart.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A saved filter holds the query and never its result, which is the whole of what
///         separates it from the collections the same doc row asks for.</b> A query re-runs, so it
///         goes on answering as the project changes; a stored list of matches is a snapshot that is
///         wrong the moment somebody imports anything.
///         <see cref="A_saved_filter_re_runs_rather_than_replaying_what_it_matched" /> is the
///         assertion that cannot pass if it were the other thing.
///     </para>
///     <para>
///         ⚠ <b>A menu behind one button rather than a fifth control in the filter bar.</b> The bar
///         already carries a search box, a kind dropdown, the view toggle and the tile-size picker,
///         and a menu is also the only surface with somewhere to put Save and Forget.
///     </para>
/// </remarks>
public class BrowserSavedFilterTests {
    /// <summary>
    ///     ⚠ <b>The whole feature in one assertion: named in one session, applied in the next.</b> A
    ///     filter that lived only in the panel would be one whose menu is empty every time the editor
    ///     is opened — which is the state the view toggle and the tile size were both in before they
    ///     were written to the preferences file.
    /// </summary>
    [Fact]
    public void A_saved_filter_survives_a_restart_and_applies_from_the_menu() {
        using var scope = new Scratch();
        using var editor = Started(scope.Directory);

        Search(editor).Value = "Main";
        editor.Settle();

        Press(editor, "Filters");

        var save = Line(editor, "Save Filter…");

        Assert.False(save.Disabled, "the Save line is disabled with a filter set");
        save.Activate();
        editor.Settle();

        Type(editor, "Scenes only");
        editor.Answer("Save");

        var kept = Assert.Single(editor.AssetFilters);

        Assert.Equal("Scenes only", kept.Name);
        Assert.Equal("Main", kept.Search);

        editor.Restart();
        editor.Open("project");
        editor.Settle();

        // ⚠ Nothing is applied by a restart. What comes back is the *offer*: the browser opens at
        // whatever it opens at, and a saved filter is a thing somebody chooses. A restart that
        // re-applied the last filter would be an editor that hides most of the project until
        // somebody works out why.
        Assert.True(string.IsNullOrEmpty(Search(editor).Value), "a restart applied a saved filter by itself");

        Press(editor, "Filters");
        Line(editor, "Scenes only").Activate();
        editor.Settle();

        Assert.Equal("Main", Search(editor).Value);
    }

    /// <summary>
    ///     ⚠ <b>The query, not its result — which is the sentence doc 20 § B1 makes about saved
    ///     filters and the opposite of the one it makes about collections.</b> A filter saved before
    ///     a file existed finds it afterwards, because what was kept was the search and not the two
    ///     assets it happened to match on the day it was named.
    /// </summary>
    [Fact]
    public void A_saved_filter_re_runs_rather_than_replaying_what_it_matched() {
        using var editor = Started();

        File.WriteAllText(Path.Combine(editor.ProjectRoot, "Assets", "widget-a.png"), "x");
        editor.Run("assets.refresh");
        editor.Settle();

        Search(editor).Value = "widget";
        editor.Settle();

        Assert.Equal(["widget-a.png"], Shown(editor));

        editor.SaveFilter("Widgets", "widget", string.Empty);

        // The project grows *after* the filter was named, which is the half a stored result cannot
        // answer for.
        File.WriteAllText(Path.Combine(editor.ProjectRoot, "Assets", "widget-b.png"), "x");
        editor.Run("assets.refresh");
        editor.Settle();

        Search(editor).Value = string.Empty;
        editor.Settle();

        Press(editor, "Filters");
        Line(editor, "Widgets").Activate();
        editor.Settle();

        // ⚠ The count is part of the assertion. A filter that narrowed to nothing satisfies every
        // "does not contain" claim by containing nothing at all.
        Assert.Equal(["widget-a.png", "widget-b.png"], Shown(editor));
    }

    /// <summary>
    ///     ⚠ <b>Applying a filter writes the bar, not just the rows.</b> A filter applied to the
    ///     rebuild alone would narrow the browser while the search box sat empty — a panel showing a
    ///     fifth of the project with nothing on screen saying why, which is the state people restart
    ///     the editor to get out of.
    /// </summary>
    [Fact]
    public void Applying_a_filter_puts_it_back_in_the_bar_where_it_can_be_seen_and_undone() {
        using var editor = Started();

        File.WriteAllText(Path.Combine(editor.ProjectRoot, "Assets", "widget-a.png"), "x");
        File.WriteAllText(Path.Combine(editor.ProjectRoot, "Assets", "other.png"), "x");
        editor.Run("assets.refresh");
        editor.Settle();

        Assert.Equal(["other.png", "widget-a.png"], Shown(editor));

        editor.SaveFilter("Widgets", "widget", string.Empty);

        Press(editor, "Filters");
        Line(editor, "Widgets").Activate();
        editor.Settle();

        Assert.Equal(["widget-a.png"], Shown(editor));

        // ⚠ And the bar says why. The search box is what a person clears to get the rest of the
        // project back, so a filter that narrowed the rows without writing the control would be one
        // there is no way out of except a restart.
        Assert.Equal("widget", Search(editor).Value);

        Search(editor).Value = string.Empty;
        editor.Settle();

        Assert.Equal(["other.png", "widget-a.png"], Shown(editor));
    }

    /// <summary>
    ///     ⚠ <b>Nothing set is nothing to save.</b> An empty search over every kind is the browser's
    ///     resting state, and a menu offering to name it would be offering to keep a row that does
    ///     nothing when it is applied — which is how people learn a line does not work.
    /// </summary>
    [Fact]
    public void The_save_line_is_disabled_when_no_filter_is_set() {
        using var editor = Started();

        Press(editor, "Filters");

        Assert.True(Line(editor, "Save Filter…").Disabled, "the Save line is offered over an empty filter");

        // ⚠ And there is no Forget submenu at all rather than an empty one. A submenu that opens on
        // nothing is a line that looks broken.
        Assert.DoesNotContain("Forget Filter", Menu(editor).Items.Select(item => item.Label));

        Search(editor).Value = "Main";
        editor.Settle();

        Press(editor, "Filters");

        Assert.False(Line(editor, "Save Filter…").Disabled);
    }

    /// <summary>
    ///     ⚠ <b>Forgetting one takes it off the menu <i>and</i> out of the file.</b> A menu line that
    ///     vanished until the next restart is the shape of half-write this repository keeps finding.
    /// </summary>
    [Fact]
    public void Forgetting_a_filter_takes_it_off_the_menu_and_out_of_the_file() {
        using var scope = new Scratch();
        using var editor = Started(scope.Directory);

        editor.SaveFilter("Scenes only", "Main", string.Empty);
        editor.SaveFilter("Widgets", "widget", string.Empty);

        Assert.Equal(2, editor.AssetFilters.Count);

        Press(editor, "Filters");

        var forget = Menu(editor).Items.FirstOrDefault(item => item.Label == "Forget Filter")
            ?? throw editor.Fail("the filter menu has no Forget submenu");

        var inner = forget.Submenu ?? throw editor.Fail("Forget Filter opens no menu");

        inner.Items.First(item => item.Label == "Widgets").Activate();
        editor.Settle();

        Assert.Equal(["Scenes only"], editor.AssetFilters.Select(saved => saved.Name));

        Press(editor, "Filters");

        Assert.DoesNotContain("Widgets", Menu(editor).Items.Select(item => item.Label));

        // ⚠ Out of the file too, which is the half a menu cannot show. Restarting reads it back.
        editor.Restart();

        Assert.Equal(["Scenes only"], editor.AssetFilters.Select(saved => saved.Name));
    }

    /// <summary>
    ///     ⚠ <b>The kind is stored as the importer tag and never as the dropdown's <c>All types</c>
    ///     label.</b> A filter that kept the label would come back as a filter for assets whose
    ///     importer is called "All types" — one that matches nothing, silently, because an empty grid
    ///     is exactly what a narrow filter looks like.
    /// </summary>
    [Fact]
    public void Every_kind_is_stored_as_empty_rather_than_as_the_dropdown_label() {
        using var editor = Started();

        // The dropdown sits on its own label, which is what a browser filtering nothing shows.
        var kinds = Descendants(editor.Panel("project")).OfType<Select>().First();

        Assert.Equal("All types", kinds.Value);

        Search(editor).Value = "Main";
        editor.Settle();

        Press(editor, "Filters");
        Line(editor, "Save Filter…").Activate();
        editor.Settle();

        Type(editor, "Anything called Main");
        editor.Answer("Save");

        var kept = Assert.Single(editor.AssetFilters);

        Assert.Equal("Main", kept.Search);
        Assert.Equal(string.Empty, kept.Kind);
    }

    static EditorSession Started(string? data = null) {
        var editor = data is null
            ? EditorSession.Start()
            : EditorSession.Start(new EditorSessionOptions { DataDirectory = data });

        editor.Open("project");
        editor.Settle();

        return editor;
    }

    static SearchBox Search(EditorSession editor) =>
        Descendants(editor.Panel("project")).OfType<SearchBox>().FirstOrDefault()
        ?? throw editor.Fail("the browser has no search box");

    static List<string> Shown(EditorSession editor) {
        var grid = Descendants(editor.Panel("project")).OfType<AssetGrid>().FirstOrDefault()
            ?? throw editor.Fail("the browser has no grid");

        return [.. grid.Items.Where(item => !item.IsFolder).Select(item => item.Name).Order(StringComparer.Ordinal)];
    }

    static void Press(EditorSession editor, string label) {
        Descendants(editor.Panel("project"))
            .OfType<ButtonBase>()
            .FirstOrDefault(button => button.Label == label)
            ?.Activate();

        editor.Settle();
    }

    static ContextMenu Menu(EditorSession editor) =>
        Descendants(editor.Document.Root)
            .OfType<ContextMenu>()
            .FirstOrDefault(candidate =>
                candidate.IsOpen && candidate.Items.Any(item => item.Label == "Save Filter…")
            )
        ?? throw editor.Fail("no saved-filter menu is open");

    static MenuItem Line(EditorSession editor, string label) =>
        Menu(editor).Items.FirstOrDefault(item => item.Label == label)
        ?? throw editor.Fail(
            $"the filter menu has no '{label}'. It has: "
            + string.Join(", ", Menu(editor).Items.Select(item => item.Label ?? "?"))
            + "."
        );

    static void Type(EditorSession editor, string text) {
        var dialog = editor.Shell.Dialogs.Current ?? throw editor.Fail("nothing asked for a name");

        Descendants(dialog).OfType<TextBox>().First().Value = text;
        editor.Settle();
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
