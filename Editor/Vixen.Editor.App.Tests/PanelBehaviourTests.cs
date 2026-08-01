// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Things the editor got wrong that a person notices in the first ten minutes.</summary>
public class PanelBehaviourTests {
    /// <summary>
    ///     ⚠ Every entity gets a <i>new handle</i> when a play-mode snapshot is restored, so the
    ///     document's name and stable-id tables — both keyed by handle — named nothing afterwards.
    ///     <c>SceneDocument.Remap</c> was written for exactly this and nothing called it: the
    ///     outliner came back from play mode as a list of blank rows, which reads as the scene having
    ///     been lost.
    /// </summary>
    [Fact]
    public void The_scene_survives_a_trip_through_play_mode_with_its_names() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");

        var before = Rows(fixture.Hierarchy);

        Assert.Contains("Crate", before);

        Assert.True(fixture.Shell.Commands.Execute("play.play"));
        fixture.Frames(2);

        Assert.True(fixture.Shell.Commands.Execute("play.stop"));
        fixture.Frames(3);

        Assert.Equal(before, Rows(fixture.Hierarchy));
    }

    [Fact]
    public void A_selection_survives_it_too_and_points_at_the_new_handles() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");

        var scene = fixture.Scene;
        var crate = scene.Entities.First(entity => scene.NameOf(entity) == "Crate");

        scene.Selection.Set([crate]);
        fixture.Frames(2);

        Assert.True(fixture.Shell.Commands.Execute("play.play"));
        fixture.Frames(2);
        Assert.True(fixture.Shell.Commands.Execute("play.stop"));
        fixture.Frames(3);

        // The handle is a different one, and the name has to have travelled with it or the selection
        // points at a row the tree cannot label.
        var selected = Assert.Single(scene.Selection);

        Assert.Equal("Crate", scene.NameOf(selected));
        Assert.Equal("Crate", Assert.Single(fixture.Hierarchy.Selection).Text);
    }

    /// <summary>
    ///     ⚠ A row's own name is the thing you edit in an outliner. F2 renamed and a double-click did
    ///     nothing, which is the one gesture everybody tries first.
    /// </summary>
    [Fact]
    public void Double_clicking_a_row_opens_the_rename_editor() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");

        // The outliner opens with only its roots expanded, so a nested row is not realised — and a
        // click aimed at one that is not on screen lands on whatever is.
        fixture.ExpandAll(fixture.Hierarchy);
        fixture.DoubleClickRow(fixture.Hierarchy, "Crate");

        Assert.NotNull(Find<TextBox>(fixture.Hierarchy));
    }

    [Fact]
    public void A_rename_abandoned_by_clicking_elsewhere_is_kept_rather_than_thrown_away() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");
        fixture.ExpandAll(fixture.Hierarchy);
        fixture.DoubleClickRow(fixture.Hierarchy, "Crate");

        var editor = Find<TextBox>(fixture.Hierarchy)!;
        editor.Value = "Barrel Two";

        // ⚠ The third way out of a rename and the one people use without meaning to. Enter and
        // Escape are deliberate; clicking on something else is how a rename ends most of the time,
        // and an editor that discarded the typed name then loses one every session.
        fixture.Document.Focus(null);
        fixture.Frames(2);

        Assert.Contains("Barrel Two", Rows(fixture.Hierarchy));
        Assert.Null(Find<TextBox>(fixture.Hierarchy));
    }

    /// <summary>
    ///     ⚠ A tree row and a text field take focus, so the outliner and the scene lit up when
    ///     clicked; a console row and an inspector's label do not, so those two never showed as
    ///     focused however often they were clicked. A border right for two panels out of four reads
    ///     as broken rather than as absent.
    /// </summary>
    [Theory]
    [InlineData("hierarchy")]
    [InlineData("console")]
    [InlineData("inspector")]
    [InlineData("project")]
    public void Clicking_any_panel_makes_it_the_active_one(string id) {
        using var fixture = EditorSession.Start();

        fixture.Open(id);
        fixture.Click(Panel(fixture, id));

        var host = fixture.Shell.Workspace.Host;

        Assert.Equal(id, host.Active?.Id);
        Assert.True(GroupOf(fixture, id).HasClass("active"), $"the '{id}' group is not marked active");
    }

    [Fact]
    public void Only_one_group_is_the_active_one() {
        using var fixture = EditorSession.Start();

        fixture.Open("console");
        fixture.Click(Panel(fixture, "console"));

        fixture.Open("inspector");
        fixture.Click(Panel(fixture, "inspector"));

        var marked = fixture.Shell.Workspace.Host.Groups.Count(group => group.HasClass("active"));

        Assert.Equal(1, marked);
    }

    [Fact]
    public void Clicking_a_panel_takes_the_keyboard_out_of_the_one_it_left() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");
        fixture.ClickRow(fixture.Hierarchy, "Ground");

        Assert.Equal("hierarchy", fixture.Shell.Workspace.Host.Active?.Id);

        fixture.Open("console");
        fixture.Click(Panel(fixture, "console"));

        // Otherwise the outliner would still hold the keyboard and the next Delete would act on a
        // panel the user had visibly left.
        Assert.Equal("console", fixture.Shell.Workspace.Host.Active?.Id);
    }

    static DockPanel Panel(EditorSession fixture, string id) =>
        Descendants(fixture.Document.Root).OfType<DockPanel>().FirstOrDefault(panel => panel.Id == id)
        ?? throw new InvalidOperationException($"the '{id}' panel is not open");

    static DockGroupView GroupOf(EditorSession fixture, string id) =>
        fixture.Shell.Workspace.Host.Groups.FirstOrDefault(group => group.Node?.IndexOf(id) >= 0)
        ?? throw new InvalidOperationException($"no group holds '{id}'");

    static List<string> Rows(TreeView tree) => [.. Nodes(tree.Root).Select(node => node.Text ?? string.Empty)];

    static IEnumerable<TreeNode> Nodes(TreeNode node) {
        foreach (var child in node.Children) {
            yield return child;

            foreach (var found in Nodes(child)) {
                yield return found;
            }
        }
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

    static T? Find<T>(UiElement element) where T : UiElement {
        if (element is T match) {
            return match;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}
