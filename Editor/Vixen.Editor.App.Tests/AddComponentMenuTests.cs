// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Ecs;
using Vixen.Editor.Testing;
using Vixen.Engine.Cameras;
using Vixen.Rendering.Ecs;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The Add Component drop: as wide as its button, a focused search, then categories.</summary>
/// <remarks>
///     ⚠ <b>Through a running editor rather than over a bare control.</b> Three of the four claims
///     below are about the picker's relationship with something outside it — the button's width, the
///     document's focus, the registry's contents — and none of those is true or false in isolation.
/// </remarks>
public class AddComponentMenuTests {
    static EditorSession Selected() {
        var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ClickRow(editor.Hierarchy, "Directional Light");
        editor.Open("inspector");
        editor.Frames(2);

        return editor;
    }

    static AddComponentMenu Open(EditorSession editor) {
        Descendants(editor.Panel("inspector"))
            .OfType<ButtonBase>()
            .First(button => button.Label == "Add Component")
            .Activate();

        editor.Settle();

        return Descendants(editor.Document.Root)
            .OfType<AddComponentMenu>()
            .FirstOrDefault(candidate => candidate.IsOpen)
            ?? throw editor.Fail("the Add Component picker did not open");
    }

    static Button Trigger(EditorSession editor) =>
        Descendants(editor.Panel("inspector")).OfType<Button>().First(button => button.Label == "Add Component");

    [Fact]
    public void It_is_as_wide_as_the_button_that_dropped_it() {
        using var editor = Selected();

        var button = Trigger(editor);
        var picker = Open(editor);

        editor.Frames(2);

        // A drop 140 pixels wide under a 300-pixel control reads as a different, smaller thing
        // having happened. Within a pixel, because the width is written as formatted text.
        Assert.True(button.Width > 0f, "the button should have been laid out");
        Assert.Equal(button.Width, picker.Width, 1f);
    }

    /// <summary>
    ///     ⚠ <b>A `max-height` on the popup clamped the popup and nothing else.</b> Its children had
    ///     already been laid out against the height it would have had, so a query matching thirty
    ///     components produced a 340-pixel box with a 620-pixel list drawn straight through the
    ///     bottom of it and off the window. The cap belongs on the thing that scrolls.
    /// </summary>
    [Fact]
    public void A_long_list_scrolls_inside_the_popup_rather_than_through_it() {
        using var editor = Selected();

        var picker = Open(editor);

        picker.Field.Value = "a";
        editor.Frames(3);

        Assert.True(picker.LineCount > 12, $"the query should match plenty, and matched {picker.LineCount}");

        // The region that scrolls is inside the box that is drawn…
        Assert.True(
            picker.List.Bounds.Bottom <= picker.Bounds.Bottom + 0.5f,
            $"the list runs to {picker.List.Bounds.Bottom} and the popup ends at {picker.Bounds.Bottom}"
        );

        // …and there is more content than fits, which is what makes it a scroll rather than a fit.
        Assert.True(
            picker.List.Content.Height > picker.List.Height,
            "the content should overflow its region, or this proves nothing"
        );

        // ⚠ And the whole popup is inside the window. It opens under a button near the bottom of a
        // docked inspector, so a list that grew after it was placed used to hang off the screen —
        // `Overlay` now places itself again when a pass changes its size, and flips above the button
        // when there is no room below.
        Assert.True(
            picker.Bounds.Bottom <= editor.Document.Viewport.ViewportHeight + 0.5f,
            $"the popup ends at {picker.Bounds.Bottom}, past the {editor.Document.Viewport.ViewportHeight} window"
        );

        Assert.True(picker.Bounds.Y >= 0f);
    }

    [Fact]
    public void The_search_has_the_focus_the_moment_it_opens() {
        using var editor = Selected();

        var picker = Open(editor);

        // ⚠ The reason this is not a `ContextMenu`: a menu focuses its first item when it opens, so
        // the field would lose the focus on the frame it was wanted and the first letter typed would
        // go to a list that treats letters as nothing.
        Assert.True(picker.Field.IsFocused, "the query field should have the focus");
    }

    [Fact]
    public void It_opens_on_categories_and_going_into_one_swaps_the_content() {
        using var editor = Selected();

        var picker = Open(editor);

        var headings = picker.Rows.Where(row => row.Index >= 0).Select(row => row.Label).ToList();

        // Categories, not components — a heading is not the name of anything that can be added.
        Assert.DoesNotContain("Camera", headings);
        Assert.Contains(headings, name => name is not null && picker.Offered.Any(entry => entry.Category == name));

        var audio = picker.Offered.First(entry => entry.Bridge.ComponentType == typeof(AudioSource)).Category;

        picker.Show(audio);
        editor.Settle();

        var inside = picker.Rows.Where(row => row.Index >= 0).Select(row => row.Label).ToList();

        Assert.Contains("Audio Source", inside);

        // Swapped rather than opened beside: one popup, whose content is now the category's.
        Assert.DoesNotContain(inside, name => name == audio);

        // And a way back, which is what makes it a swap rather than a trapdoor.
        Assert.Contains("All categories", inside);

        picker.Show(null);
        editor.Settle();

        Assert.DoesNotContain("Audio Source", picker.Rows.Where(row => row.Index >= 0).Select(row => row.Label));
    }

    /// <summary>
    ///     ⚠ <b>The query matches what can be added, never a category.</b> Somebody types because they
    ///     know the name; answering with a folder to open is one more click at the exact moment they
    ///     had already said what they wanted.
    /// </summary>
    [Fact]
    public void The_search_matches_components_and_not_categories() {
        using var editor = Selected();

        var picker = Open(editor);
        var audio = picker.Offered.First(entry => entry.Bridge.ComponentType == typeof(AudioSource)).Category;

        picker.Field.Value = audio;
        editor.Settle();

        var found = picker.Rows.Where(row => row.Index >= 0).Select(row => row.Label).ToList();

        // The category is called "Audio" and `AudioSource` is written out as "Audio Source", so the
        // query matches components *whose names contain it* and never the heading itself.
        Assert.Contains("Audio Source", found);
        // ⚠ On the row rather than on its chevron, which is where the class was ever written.
        // `row.Arrow.HasClass("parked")` was the assertion here and it could not fail: `parked` went
        // on the `AddComponentRow` and the arrow is an `Icon` inside it. The pool it was written
        // against is gone (doc 36 § F7 wave 8), so this now says the stronger thing it always meant —
        // and `AddComponentMenuDumpTests.Narrowing_the_query_leaves_nothing_behind` says it against
        // the tree, where a hidden element still counts.
        Assert.DoesNotContain(picker.Rows.Where(row => row.Index >= 0), row => row.HasClass("parked"));
        Assert.All(found, name => Assert.Contains(picker.Offered, entry => entry.Bridge.DisplayName == name));
    }

    [Fact]
    public void Typing_searches_across_every_category_rather_than_inside_the_open_one() {
        using var editor = Selected();

        var picker = Open(editor);
        var audio = picker.Offered.First(entry => entry.Bridge.ComponentType == typeof(AudioSource)).Category;

        picker.Show(audio);
        editor.Settle();

        picker.Field.Value = "Camera";
        editor.Settle();

        // ⚠ A scope somebody has to notice and clear is a mode, and a search that quietly answered
        // from three components while the name being typed is somewhere else looks broken.
        Assert.Null(picker.Category);
        Assert.Contains("Camera", picker.Rows.Where(row => row.Index >= 0).Select(row => row.Label));
    }

    [Fact]
    public void Choosing_a_component_adds_it_and_closes() {
        using var editor = Selected();

        var picker = Open(editor);

        picker.Field.Value = "Camera";
        editor.Settle();

        picker.Rows.First(row => row.Index >= 0 && row.Label == "Camera").Activate();
        editor.Settle();

        Assert.False(picker.IsOpen);

        var entity = Assert.Single(editor.Scene.Selection.Items);
        Assert.True(editor.Scene.World.Has<Camera>(entity));
    }

    /// <summary>Where a category name comes from, which is the namespace and nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b>The plumbing segments are dropped, and "Ecs" is the one that matters.</b> Every
    ///     component the engine ships is under one — it is where we keep them, not what they are —
    ///     and a picker whose headings were "Ecs" three times over would be the filing cabinet
    ///     describing itself.
    /// </remarks>
    [Fact]
    public void A_category_is_the_last_meaningful_part_of_the_namespace() {
        AuthoringSubsystems.Load();

        var bridges = ComponentsView.Default();

        var light = bridges.First(bridge => bridge.ComponentType == typeof(Light));
        var camera = bridges.First(bridge => bridge.ComponentType == typeof(Camera));
        var source = bridges.First(bridge => bridge.ComponentType == typeof(AudioSource));

        // Vixen.Rendering.Ecs → Rendering, Vixen.Audio.Ecs → Audio.
        Assert.Equal("Rendering", ComponentsView.CategoryOf(light));
        Assert.Equal("Audio", ComponentsView.CategoryOf(source));

        // Vixen.Engine.Cameras → Cameras, rather than the "Engine" half the engine is under.
        Assert.Equal("Cameras", ComponentsView.CategoryOf(camera));
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
