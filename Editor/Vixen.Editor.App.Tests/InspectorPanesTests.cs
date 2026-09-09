// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Two inspectors, and the members somebody keeps at the top of one.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 20 § B1's first two inspector rows, which are one user need said twice: what the
///         inspector keeps while the selection moves.</b> The panel already floated and already
///         locked; what was missing was a second <i>instance</i>, because the application held one
///         view and one component section in two fields and everything that fed them wrote to
///         exactly one.
///     </para>
///     <para>
///         ⚠ <b>The lock is what arbitrates, and half of it was missing.</b>
///         <c>InspectorView.Inspect</c> refuses while locked and always did — but the component
///         section under it is not the inspector's rows and knew nothing about the lock, so the
///         foldouts of a locked panel followed the selection anyway. With one inspector that was
///         invisible; with two it is the whole feature.
///     </para>
/// </remarks>
public class InspectorPanesTests {
    /// <summary>
    ///     ⚠ <b>The thing somebody opens a second inspector to do.</b> Pinning one panel to entity A
    ///     while selecting entity B is how a value is copied between two things, and it is the first
    ///     gesture anybody who has used either reference editor tries.
    /// </summary>
    [Fact]
    public void A_locked_inspector_keeps_its_entity_while_the_other_follows_the_selection() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Crate");
        editor.Open("inspector");
        editor.Open("inspector-2");
        editor.Settle();

        var crate = editor.Scene.Selection[0];

        var first = Section(editor, "inspector");
        var second = Section(editor, "inspector-2");

        // ⚠ Both, before anything is locked. A second inspector that opened empty and only filled on
        // the next click would be a panel somebody has to poke to make useful, and it would make the
        // assertion below pass for the wrong reason.
        Assert.Equal(crate, first.Entity);
        Assert.Equal(crate, second.Entity);

        View(editor, "inspector-2").IsLocked = true;
        editor.Settle();

        editor.ClickRow(editor.Hierarchy, "Directional Light");
        editor.Settle();

        var light = editor.Scene.Selection[0];

        Assert.NotEqual(crate, light);

        // The unlocked one moved; the locked one did not, in both of its halves.
        Assert.Equal(light, first.Entity);
        Assert.Equal(crate, second.Entity);

        Assert.Contains(View(editor, "inspector").Targets, target => Named(target) == "Directional Light");
        Assert.Contains(View(editor, "inspector-2").Targets, target => Named(target) == "Crate");

        // And letting the lock off catches it up, which is what `LockChanged` is subscribed for.
        View(editor, "inspector-2").IsLocked = false;
        editor.Settle();

        Assert.Equal(light, second.Entity);
    }

    /// <summary>
    ///     ⚠ <b>A pin is per member of a type, so the claim is about the <i>next</i> object of that
    ///     type rather than about this one.</b> Moving away and back is the whole assertion: a
    ///     panel that merely reordered the rows it had drawn would pass a test that only looked once.
    /// </summary>
    [Fact]
    public void A_pinned_member_is_at_the_top_of_its_foldout_after_the_selection_moves_away_and_back() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Directional Light");
        editor.Open("inspector");
        editor.Settle();

        var section = Section(editor, "inspector");
        var before = Rows(editor, section, "Light");

        // ⚠ The count is part of it: a foldout that drew no rows would satisfy every "is first"
        // assertion below by drawing nothing at all.
        Assert.True(before.Count > 1, $"the Light foldout drew {before.Count} rows");
        Assert.Contains("Intensity", before);
        Assert.NotEqual("Intensity", before[0]);

        Press(editor, section, "Light", "Intensity");

        Assert.Equal(["Light.Intensity"], section.Pinned);
        Assert.Equal("Intensity", Rows(editor, section, "Light")[0]);

        // Away — a crate has no light on it at all — and back.
        editor.ClickRow(editor.Hierarchy, "Crate");
        editor.Settle();

        Assert.DoesNotContain(section.Sections, fold => fold.Label == "Light");

        editor.ClickRow(editor.Hierarchy, "Directional Light");
        editor.Settle();

        var after = Rows(editor, section, "Light");

        Assert.Equal("Intensity", after[0]);

        // ⚠ Nothing else moved. A pin is a stable sort on one key, so the rest of the foldout keeps
        // the order the descriptor gave it — and a member's `[Header]` grouping with it.
        Assert.Equal(before.Where(name => name != "Intensity"), after.Skip(1));
    }

    /// <summary>
    ///     ⚠ <b>A pin is a preference rather than a fact about a panel, so a second inspector has the
    ///     same ones.</b> Two panels disagreeing about which member is pinned would be two answers to
    ///     one question — and the panel that was opened second is the one that would be wrong,
    ///     because its factory reads the preferences file rather than the panel beside it.
    /// </summary>
    [Fact]
    public void A_pin_made_in_one_inspector_is_read_by_the_next_one_opened() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Directional Light");
        editor.Open("inspector");
        editor.Settle();

        var first = Section(editor, "inspector");

        Press(editor, first, "Light", "Intensity");

        editor.Open("inspector-2");
        editor.Settle();

        var second = Section(editor, "inspector-2");

        Assert.Equal(["Light.Intensity"], second.Pinned);
        Assert.Equal("Intensity", Rows(editor, second, "Light")[0]);
    }

    /// <summary>The component section inside one inspector panel.</summary>
    /// <remarks>
    ///     ⚠ Found by pattern rather than by <c>OfType&lt;ComponentsView&gt;</c>, which is the call
    ///     #1022 reports nine <c>CA2021</c> errors over on this assembly.
    /// </remarks>
    static ComponentsView Section(EditorSession editor, string panel) {
        foreach (var child in Descendants(editor.Panel(panel))) {
            if (child is ComponentsView view) {
                return view;
            }
        }

        throw editor.Fail($"the '{panel}' panel has no components section");
    }

    static InspectorView View(EditorSession editor, string panel) =>
        Descendants(editor.Panel(panel)).OfType<InspectorView>().FirstOrDefault()
        ?? throw editor.Fail($"the '{panel}' panel has no inspector in it");

    /// <summary>What one component's foldout is drawing, by member name and in drawn order.</summary>
    static List<string> Rows(EditorSession editor, ComponentsView section, string component) =>
        Descendants(Fold(editor, section, component))
            .OfType<InspectorRow>()
            .Select(row => row.Field.Member.Name)
            .ToList();

    static Expander Fold(EditorSession editor, ComponentsView section, string component) =>
        section.Sections.FirstOrDefault(fold => fold.Label == component)
        ?? throw editor.Fail(
            $"no '{component}' foldout. Showing: "
            + string.Join(", ", section.Sections.Select(fold => fold.Label ?? "?"))
            + "."
        );

    /// <summary>
    ///     Pins a member the way a person does: a secondary click on its row, then the one line on
    ///     the menu that opens.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A menu rather than a button in the row, and the button is what this test drove
    ///     first.</b> A pin control in every inspector row made the panel's content wider than the
    ///     panel, and what that broke was three panels away: a foldout's remove button slid outside
    ///     the scroll region's clip and stopped taking clicks. The menu costs no layout.
    /// </remarks>
    static void Press(EditorSession editor, ComponentsView section, string component, string member) {
        var row = Descendants(Fold(editor, section, component))
                .OfType<InspectorRow>()
                .FirstOrDefault(candidate => candidate.Field.Member.Name == member)
            ?? throw editor.Fail($"'{component}' has no '{member}' row");

        var bounds = row.Bounds;

        editor.Ui.At(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f)).RightClick();
        editor.Settle();

        var menu = Descendants(editor.Document.Root)
                .OfType<ContextMenu>()
                .FirstOrDefault(candidate => candidate.IsOpen && candidate.Items.Any(Pins))
            ?? throw editor.Fail($"a right-click on the '{member}' row opened no menu with a pin on it");

        var line = menu.Items.First(Pins);

        Assert.False(line.Disabled, $"the menu opened on the '{member}' row with its pin line disabled");

        line.Activate();
        editor.Settle();
    }

    static bool Pins(MenuItem item) => item.Label is "Pin to Top" or "Unpin";

    static string Named(object target) => target is SceneEntity entity ? entity.Name : string.Empty;

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
