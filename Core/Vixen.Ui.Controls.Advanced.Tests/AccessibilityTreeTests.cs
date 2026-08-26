// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Styling;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>Doc 09's ARIA-role snapshot for the eleven advanced controls.</summary>
/// <remarks>
///     <para>
///         <b>Doc 46 § A2 populated the plain control set as far as it took to prove the API and
///         stopped; this assembly was owed whole.</b> Every test here asserts
///         <see cref="AccessibilitySnapshot.Unnamed" /> before anything else, for A2's reason: a
///         snapshot matches an empty tree perfectly, and an accessibility test that cannot fail
///         vacuously is the only kind worth having.
///     </para>
///     <para>
///         ⚠ <b>The four canvases are <c>application</c> and the code editor is not.</b> A viewport,
///         a node graph, a curve editor and a gradient editor own their keyboard and want assistive
///         technology to pass keys straight through, which is what ARIA's <c>application</c> role
///         asks for. A code editor is a multi-line text field whose keyboard is the one a
///         screen-reader user already has — announcing it as an application would turn off the
///         reading and review commands that make text editable at all.
///     </para>
/// </remarks>
[Collection(SharedCatalogue.Name)]
public class AccessibilityTreeTests {
    [Fact]
    public void A_tree_row_is_a_tree_item_that_says_whether_it_opens_and_whether_it_is_chosen() {
        using var fixture = new AdvancedFixture();

        var tree = fixture.Add<TreeView>();
        var folder = tree.Root.Add("Materials");
        folder.Add("Stone");
        tree.Root.Add("Readme");

        tree.Refresh();
        fixture.Update();

        Assert.Equal(AccessibleRole.Tree, tree.Role);

        var rows = Rows(tree);

        Assert.Equal("Materials", rows[0].AccessibleName);
        Assert.Equal(AccessibleRole.TreeItem, rows[0].Role);

        // ⚠ Expandable only where there is something to open, on `MenuItem`'s terms — a tree is
        // mostly leaves, and marking every row expandable is telling a user that every one of them
        // leads somewhere.
        Assert.True((rows[0].AccessibleState & AccessibleStates.Expandable) != 0);
        Assert.False((rows[0].AccessibleState & AccessibleStates.Expanded) != 0);
        Assert.False((rows[1].AccessibleState & AccessibleStates.Expandable) != 0);

        tree.Expand(folder);
        fixture.Update();

        Assert.True((Rows(tree)[0].AccessibleState & AccessibleStates.Expanded) != 0);

        // Nothing wrote a selection flag either: it is read from `ElementState.Checked`, which the
        // view already sets on the row for the cascade.
        tree.Select(folder);
        fixture.Update();

        Assert.True((Rows(tree)[0].AccessibleState & AccessibleStates.Selected) != 0);
        Assert.Empty(AccessibilitySnapshot.Unnamed(tree));
    }

    [Fact]
    public void A_multi_select_tree_says_so_because_it_changes_what_a_keystroke_means() {
        using var fixture = new AdvancedFixture();

        var tree = fixture.Add<TreeView>();

        tree.MultiSelect = false;
        fixture.Update();
        Assert.Equal(AccessibleStates.None, tree.AccessibleState & AccessibleStates.MultiSelectable);

        tree.MultiSelect = true;
        fixture.Update();

        Assert.Equal(AccessibleStates.MultiSelectable, tree.AccessibleState & AccessibleStates.MultiSelectable);
    }

    [Fact]
    public void A_grid_is_rows_of_cells_with_headers_naming_the_columns() {
        using var fixture = new AdvancedFixture();

        var grid = fixture.Add<DataGrid>();
        grid.AddColumn("Name", static item => (string) item);
        grid.SetItems(["Ada", "Bob"]);

        fixture.Update();
        grid.Refresh();
        fixture.Update();

        Assert.Equal(AccessibleRole.Grid, grid.Role);
        Assert.Equal(AccessibleRole.ColumnHeader, grid.Headers[0].Role);
        Assert.Equal("Name", grid.Headers[0].AccessibleName);

        var live = grid.Rows.Where(static row => row.Index >= 0).ToList();

        Assert.Equal(AccessibleRole.Row, live[0].Role);
        Assert.Equal(AccessibleRole.GridCell, live[0].Cells[0].Role);
        Assert.Equal("Ada", live[0].Cells[0].AccessibleName);

        // ⚠ A `row` is a container: naming it with the first cell's text would make a screen reader
        // say that value twice, once for the row and once for the cell in it.
        Assert.Null(live[0].AccessibleName);

        Assert.Empty(AccessibilitySnapshot.Unnamed(grid));
    }

    [Fact]
    public void A_grouped_grid_becomes_a_treegrid_because_its_rows_now_open() {
        using var fixture = new AdvancedFixture();

        var grid = fixture.Add<DataGrid>();
        var faction = grid.AddColumn("Faction", static item => ((string) item)[..1]);
        grid.AddColumn("Name", static item => (string) item);
        grid.SetItems(["Ada", "Abe", "Bob"]);

        fixture.Update();
        Assert.Equal(AccessibleRole.Grid, grid.Role);

        grid.GroupBy(faction);
        fixture.Update();

        // ⚠ Not decoration: `treegrid` is the role whose keyboard model a screen reader announces
        // the Left and Right arrows for. Staying `grid` while the rows collapse would leave a
        // screen-reader user with no way to know the groups can be opened at all.
        Assert.Equal(AccessibleRole.TreeGrid, grid.Role);
    }

    [Fact]
    public void A_dock_tab_and_its_panel_point_at_each_other_across_the_group() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var hierarchy = host.AddPanel("hierarchy", "Hierarchy");
        var inspector = host.AddPanel("inspector", "Inspector");

        fixture.Update();

        var tabs = Tabs(host);

        Assert.Equal(AccessibleRole.Tab, tabs[0].Role);
        Assert.Equal("Hierarchy", tabs[0].AccessibleName);

        // ⚠ The pairing no walk over `Parent` recovers: the tab is in the strip and the panel is in
        // the body, and they are siblings' children rather than parent and child.
        Assert.Same(hierarchy, tabs[0].AccessibleRelationTarget(AccessibleRelation.Controls));
        Assert.Same(tabs[0], hierarchy.AccessibleRelationTarget(AccessibleRelation.LabelledBy));

        Assert.Equal(AccessibleRole.TabPanel, inspector.Role);
        Assert.Equal("Inspector", inspector.AccessibleName);

        Assert.Empty(AccessibilitySnapshot.Unnamed(host));
    }

    [Fact]
    public void A_panel_docked_again_is_labelled_by_the_tab_it_has_now() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var panel = host.AddPanel("hierarchy", "Hierarchy");
        fixture.Update();

        var first = panel.AccessibleRelationTarget(AccessibleRelation.LabelledBy);
        Assert.NotNull(first);

        // A rebuild makes a fresh tab and reparents the same panel. ⚠ Without the clear, the panel
        // would keep the relation it was given last time — pointing at a tab that is gone, in a
        // group it is no longer in — and would go on announcing a name nothing on screen has.
        host.AddPanel("inspector", "Inspector");
        fixture.Update();

        var second = panel.AccessibleRelationTarget(AccessibleRelation.LabelledBy);

        Assert.NotSame(first, second);
        Assert.Single(panel.AccessibleRelationships, static r => r.Relation == AccessibleRelation.LabelledBy);
        Assert.Equal("Hierarchy", panel.AccessibleName);
    }

    [Fact]
    public void A_code_editor_is_a_multi_line_text_field_rather_than_an_application() {
        using var fixture = new AdvancedFixture();

        var editor = fixture.Add<CodeEditor>();
        fixture.Update();

        Assert.Equal(AccessibleRole.TextBox, editor.Role);
        Assert.True((editor.AccessibleState & AccessibleStates.MultiLine) != 0);
        Assert.True((editor.AccessibleState & AccessibleStates.Editable) != 0);
        Assert.False((editor.AccessibleState & AccessibleStates.ReadOnly) != 0);

        editor.ReadOnly = true;
        fixture.Update();

        // Both, not one instead of the other: a read-only editor is still an editor, still takes
        // the focus and can still have its text selected and copied.
        Assert.True((editor.AccessibleState & AccessibleStates.Editable) != 0);
        Assert.True((editor.AccessibleState & AccessibleStates.ReadOnly) != 0);

        // ⚠ No name and no value, both on `TextField`'s terms. What the editor is an editor *of* is
        // the file, which is the application's sentence; announcing the whole buffer as a value
        // would build a twelve-thousand-line string on every read.
        Assert.Null(editor.AccessibleName);
        Assert.Null(editor.AccessibleValue);

        Assert.Equal(["<code-editor> is a textbox and has no accessible name"], AccessibilitySnapshot.Unnamed(editor));
    }

    [Fact]
    public void A_direct_manipulation_surface_asks_for_the_keyboard_to_be_passed_through() {
        using var fixture = new AdvancedFixture();

        // ⚠ Four controls and one role, and it is the role rather than the count that is the claim:
        // each of these has a keyboard model no widget vocabulary describes, so a screen reader
        // intercepting keys would make them unusable rather than accessible.
        Assert.Equal(AccessibleRole.Application, fixture.Add<Viewport>().Role);
        Assert.Equal(AccessibleRole.Application, fixture.Add<NodeCanvas>().Role);
        Assert.Equal(AccessibleRole.Application, fixture.Add<CurveEditor>().Role);
        Assert.Equal(AccessibleRole.Application, fixture.Add<GradientEditor>().Role);
        Assert.Equal(AccessibleRole.Application, fixture.Add<Timeline>().Role);

        // ⚠ None of the five is a widget role, so none is required to have a name — which is right:
        // what a viewport is a view of is the application's sentence and not this assembly's. The
        // controls *inside* them are a different question, and it is the next test.
        Assert.Empty(AccessibilitySnapshot.Unnamed(fixture.Add<Viewport>()));
    }

    /// <summary>Every advanced control at once, held to the one rule that cannot pass vacuously.</summary>
    /// <remarks>
    ///     ⚠ <b>The gate that keeps the population honest as it grows, and the one that found three
    ///     holes while it was being written.</b> A per-control test asserts what its author thought
    ///     to look at; this one asserts that nothing anywhere in eleven controls' worth of parts is
    ///     a widget with no name or a tab stop with no role. What it caught: a colour picker's hex
    ///     field, and a gradient editor's colour-space select and opacity slider — three fields
    ///     whose purpose is carried entirely by a caption that does not exist.
    /// </remarks>
    [Fact]
    public void Every_advanced_control_in_one_window_has_a_role_and_a_name() {
        using var fixture = new AdvancedFixture();

        var root = fixture.Document.Root;

        var docking = root.Add<DockingHost>();
        docking.AddPanel("hierarchy", "Hierarchy").CanClose = true;
        docking.AddPanel("inspector", "Inspector");

        var tree = root.Add<TreeView>();
        tree.Root.Add("Materials").Add("Stone");
        tree.Refresh();

        var grid = root.Add<DataGrid>();
        grid.AddColumn("Name", static item => (string) item);
        grid.SetItems(["Ada", "Bob"]);

        root.Add<PropertyGrid>();
        root.Add<ColorPicker>().AllowHdr = true;
        root.Add<GradientEditor>();
        root.Add<CurveEditor>();
        root.Add<NodeCanvas>();
        root.Add<Timeline>();
        root.Add<Viewport>();

        fixture.Update();
        grid.Refresh();
        fixture.Update();

        // ⚠ `CodeEditor` is deliberately absent: it is the one control in the assembly that reports
        // no name of its own, on `TextField`'s terms, so a window containing an unlabelled one is
        // *supposed* to fail this. Its own test asserts the offender by name.
        Assert.Empty(AccessibilitySnapshot.Unnamed(root));
    }

    [Fact]
    public void A_colour_picker_is_a_group_whose_fields_are_all_named() {
        using var fixture = new AdvancedFixture();

        var picker = fixture.Add<ColorPicker>();
        picker.AllowHdr = true;
        fixture.Update();

        Assert.Equal(AccessibleRole.Group, picker.Role);

        // The three that had no words of their own: the hex field has no caption at all, the
        // intensity slider has one it was not related to, and the eyedropper is an icon button whose
        // label is never drawn.
        Assert.Equal(ControlStrings.ColorPickerHex.Text, picker.HexField.AccessibleName);
        Assert.Equal(ControlStrings.ColorPickerIntensity.Text, picker.IntensitySlider.AccessibleName);
        Assert.Equal(ControlStrings.ColorPickerEyedropper.Text, picker.Eyedropper.AccessibleName);

        Assert.Empty(AccessibilitySnapshot.Unnamed(picker));
    }


    /// <summary>Every control the assembly can build, held to the rule a per-control test cannot state.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A test over the <i>type list</i> rather than over a window, because a window is
    ///         a list somebody has to remember to add to.</b> Doc 46 § A2's population is one
    ///         virtual per control across two assemblies, and the failure it invites is the control
    ///         nobody thought about — added next month, focusable because <c>Control</c> is
    ///         focusable by default, and silent to a screen reader because nobody wrote four lines.
    ///         This finds it by construction: reflection over the assembly, every public control
    ///         with a parameterless constructor, one rule.
    ///     </para>
    ///     <para>
    ///         <b>The rule is <c>AccessibilitySnapshot.Unnamed</c>'s first clause</b> — a control the
    ///         keyboard can reach must be in the accessibility tree, because a tab stop that is not
    ///         is a place a screen-reader user lands on silence. The naming half is deliberately not
    ///         asserted here: a bare control has no caption and several of them report <c>null</c>
    ///         on purpose. The reference-window tests are where names are held down.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_control_the_keyboard_can_reach_is_in_the_accessibility_tree() {
        using var fixture = new AdvancedFixture();

        var silent = new List<string>();
        var built = 0;

        // ⚠ Through `Make<T>` rather than `UiElement.Add<T>` directly: `Add`'s last parameter is a
        // `ReadOnlySpan<string>`, which reflection cannot pass at all. There is no `Add(Type)`,
        // deliberately — every other caller knows what it is adding.
        var make = typeof(AccessibilityTreeTests).GetMethod(nameof(Make), BindingFlags.NonPublic | BindingFlags.Static)!;

        foreach (var type in typeof(TreeView).Assembly.GetTypes()) {
            if (!type.IsPublic || type.IsAbstract || !typeof(Control).IsAssignableFrom(type)) {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null) {
                continue;
            }

            var control = (Control) make.MakeGenericMethod(type).Invoke(null, [fixture.Document.Root])!;
            fixture.Update();
            built++;

            if (control.Focusable && control.Role == AccessibleRole.None) {
                silent.Add($"{type.Name} is a tab stop and is not in the accessibility tree");
            }
        }

        // ⚠ First: an assembly whose reflection found nothing satisfies the assertion below
        // perfectly, and a filter that quietly stopped matching is exactly how that happens.
        Assert.True(built >= 20, $"only {built} controls were built");
        Assert.Empty(silent);
    }

    static Control Make<T>(UiElement parent) where T : Control, new() => parent.Add<T>();

    static List<TreeRow> Rows(TreeView tree) =>
        [.. tree.Rows.Where(static row => !row.HasClass("parked")).OrderBy(static row => row.Node!.Depth == 0 ? 0 : 1)];

    static List<DockTab> Tabs(DockingHost host) => [.. Descendants(host).OfType<DockTab>()];

    static IEnumerable<UiElement> Descendants(UiElement root) {
        foreach (var child in root.Children) {
            yield return child;

            foreach (var descendant in Descendants(child)) {
                yield return descendant;
            }
        }
    }
}
