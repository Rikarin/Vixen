// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The outliner's eye, its padlock, and the order its rows come in.</summary>
/// <remarks>
///     ⚠ <b>Editor state and not scene state, which is the line both Unreal and Unity draw.</b>
///     Hiding something to work on what is behind it must not change what ships — so these are sets
///     on <c>SceneDocument</c> rather than components, nothing here is written to a file, and none
///     of it is undoable.
/// </remarks>
public class OutlinerColumnTests {
    static Entity Named(EditorSession editor, string name) =>
        editor.Scene.Entities.First(entity => editor.Scene.NameOf(entity) == name);

    static ToggleButton Column(EditorSession editor, string text, string className) {
        var row = editor.Row(editor.Hierarchy, text);

        return row.Children.OfType<ToggleButton>().FirstOrDefault(button => button.HasClass(className))
            ?? throw editor.Fail($"The '{text}' row has no '{className}' column.");
    }

    [Fact]
    public void Every_row_has_an_eye_and_a_padlock() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);

        Assert.False(Column(editor, "Crate", "outliner-hidden").IsChecked);
        Assert.False(Column(editor, "Crate", "outliner-locked").IsChecked);
    }

    /// <summary>
    ///     ⚠ <b>The two columns are glyphs and the glyph changes, which is the whole of what they
    ///     say.</b> They were a cross and a tick with the words "Hide" and "Lock" beside them: a
    ///     label on every row of the outliner, four times the button's width, naming the action
    ///     rather than the state. The word is still set — it is the tooltip and what a screen reader
    ///     reads — and the theme is what keeps it off the screen.
    /// </summary>
    [Fact]
    public void The_columns_draw_a_glyph_per_state_and_never_a_word() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);

        var eye = Column(editor, "Crate", "outliner-hidden");
        var padlock = Column(editor, "Crate", "outliner-locked");

        Assert.Same(EditorIcons.Eye, eye.LeadingIcon.Geometry);
        Assert.Same(ControlIcons.Unlock, padlock.LeadingIcon.Geometry);

        // Set, and taking up no room: `display: none` leaves the element in the tree with nothing
        // laid out for it, which is exactly the state a tooltip can still read.
        Assert.Equal("Hide", eye.Label);
        Assert.Equal(0f, Text(eye).Width);
        Assert.Equal(0f, Text(padlock).Width);

        eye.Activate();
        padlock.Activate();
        editor.Settle();

        Assert.Same(EditorIcons.EyeOff, eye.LeadingIcon.Geometry);
        Assert.Same(ControlIcons.Lock, padlock.LeadingIcon.Geometry);
    }

    /// <summary>The element carrying a control's word, which the theme is expected to hide here.</summary>
    static UiElement Text(ToggleButton button) =>
        button.Children.First(child => child.Tag == "label");

    /// <summary>
    ///     ⚠ <b>The click has to reach the viewport, not just the row.</b> An eye that greys a name
    ///     and leaves the object on screen is the version of this feature that is worse than not
    ///     having it.
    /// </summary>
    [Fact]
    public void Hiding_an_entity_takes_it_out_of_what_is_drawn() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);

        var meshes = new SceneMeshes();

        meshes.Build(editor.Scene);

        var before = meshes.Count;

        Assert.True(before > 0, "the seeded scene draws nothing, so this proves nothing");

        Column(editor, "Crate", "outliner-hidden").Activate();
        editor.Settle();

        Assert.True(editor.Scene.IsHidden(Named(editor, "Crate")));

        meshes.Build(editor.Scene);
        Assert.Equal(before - 1, meshes.Count);
    }

    /// <summary>
    ///     ⚠ <b>Something you cannot see and can still click is worse than either.</b> You drag what
    ///     you are not looking at, which is the whole reason an outliner has an eye rather than a
    ///     delete key.
    /// </summary>
    [Fact]
    public void A_hidden_or_locked_entity_is_not_picked_in_the_viewport() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);

        var picker = new ScenePicker(editor.Scene);
        var crate = Named(editor, "Crate");
        var camera = new EditorCamera();

        var position = new Transform(editor.Scene.World, crate).Position;
        var ray = new Ray(position + new Vector3(0f, 0f, 10f), -Vector3.UnitZ);

        Assert.Equal(crate, picker.Under(ray, camera, 800, 600));

        editor.Scene.SetLocked(crate, true);
        Assert.NotEqual(crate, picker.Under(ray, camera, 800, 600));

        editor.Scene.SetLocked(crate, false);
        editor.Scene.SetHidden(crate, true);
        Assert.NotEqual(crate, picker.Under(ray, camera, 800, 600));
    }

    /// <summary>
    ///     ⚠ <b>Hiding a prop and finding its four children still drawn is what makes a visibility
    ///     column useless.</b> The walk is upwards from the child rather than a mark pushed down,
    ///     because unhiding the parent has to put back exactly what was there — and a pushed mark
    ///     cannot tell which descendants the user hid on purpose.
    /// </summary>
    [Fact]
    public void A_child_of_a_hidden_entity_is_hidden_and_says_it_is_inherited() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);

        var ground = Named(editor, "Ground");
        var crate = Named(editor, "Crate");

        editor.Scene.SetHidden(ground, true);
        editor.Settle();

        Assert.True(editor.Scene.IsHidden(crate));
        Assert.False(editor.Scene.IsHiddenDirectly(crate));

        // The row says so rather than showing the eye on: clicking it would clear a mark that is on
        // an ancestor, so a button that looked on would be lying about what pressing it does.
        var eye = Column(editor, "Crate", "outliner-hidden");

        Assert.False(eye.IsChecked);
        Assert.True(eye.HasClass("inherited"), "the inherited mark is not shown on the child's row");

        // And unhiding the parent puts the child back rather than leaving it marked.
        editor.Scene.SetHidden(ground, false);
        Assert.False(editor.Scene.IsHidden(crate));
    }

    /// <summary>
    ///     ⚠ <b>Every entity gets a new handle when a play-mode snapshot is restored.</b> A hidden
    ///     set keyed by the old ones comes back hiding whatever took those slots, which reads as
    ///     objects disappearing when play mode stops — the same failure the names had.
    /// </summary>
    [Fact]
    public void The_marks_survive_a_trip_through_play_mode() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");

        editor.Scene.SetHidden(Named(editor, "Crate"), true);
        editor.Scene.SetLocked(Named(editor, "Barrel"), true);

        editor.Run("play.play");
        editor.Run("play.stop");

        Assert.True(editor.Scene.IsHiddenDirectly(Named(editor, "Crate")));
        Assert.True(editor.Scene.IsLockedDirectly(Named(editor, "Barrel")));

        // And nothing else picked the marks up, which is what a table keyed by stale handles does.
        Assert.Single(editor.Scene.Hidden);
        Assert.Single(editor.Scene.Locked);
    }

    /// <summary>
    ///     ⚠ <b>All of them get what the first one is not, rather than each being flipped.</b>
    ///     Toggling a mixed selection per entity swaps which half is hidden, which nobody means, and
    ///     makes pressing the key twice a no-op that looks like the key not working.
    /// </summary>
    [Fact]
    public void The_command_toggles_the_whole_selection_together() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");

        var crate = Named(editor, "Crate");
        var barrel = Named(editor, "Barrel");

        editor.Scene.SetHidden(crate, true);
        editor.Scene.Selection.Set([crate, barrel]);
        editor.Settle();

        editor.Run("entity.toggle-hidden");

        Assert.False(editor.Scene.IsHiddenDirectly(crate));
        Assert.False(editor.Scene.IsHiddenDirectly(barrel));

        editor.Run("entity.toggle-hidden");

        Assert.True(editor.Scene.IsHiddenDirectly(crate));
        Assert.True(editor.Scene.IsHiddenDirectly(barrel));
    }

    /// <summary>
    ///     ⚠ <b>Hierarchy order is the default because it is the only one that carries information
    ///     the others destroy.</b> The order of siblings is something the user arranged.
    /// </summary>
    [Fact]
    public void The_rows_can_be_sorted_by_name_and_put_back() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);

        var authored = EditorSession.Labels(editor.Hierarchy);

        Assert.Contains("Crate", authored);

        var order = Find<Select>(editor.Panel("hierarchy"))
            ?? throw editor.Fail("the outliner has no sort dropdown");

        order.Value = "Name (A–Z)";
        editor.Settle();

        var sorted = Siblings(editor, "Ground");

        Assert.Equal(sorted.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase), sorted);

        order.Value = "Name (Z–A)";
        editor.Settle();

        Assert.Equal(sorted.AsEnumerable().Reverse(), Siblings(editor, "Ground"));

        order.Value = "Hierarchy order";
        editor.Settle();
        editor.ExpandAll(editor.Hierarchy);

        Assert.Equal(authored, EditorSession.Labels(editor.Hierarchy));
    }

    /// <summary>
    ///     ⚠ Per level, not over the flattened tree. A global sort would put a child above its own
    ///     parent, which is not a tree.
    /// </summary>
    static List<string> Siblings(EditorSession editor, string parent) {
        foreach (var node in EditorSession.NodesOf(editor.Hierarchy)) {
            if (node.Text == parent) {
                return [.. node.Children.Select(child => child.Text ?? string.Empty)];
            }
        }

        throw editor.Fail($"there is no '{parent}' row to read the siblings of");
    }

    static T? Find<T>(Vixen.Ui.UiElement element) where T : Vixen.Ui.UiElement {
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
