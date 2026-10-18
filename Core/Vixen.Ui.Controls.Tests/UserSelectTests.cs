// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>user-select: none</c> inside a text field: a drag that starts on it is not a text selection (#225).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The per-control half of #225, and the tests say which half.</b> Vixen has no
///         document-wide selection, so <c>select-none</c> on a button outside any text control has
///         nothing to suppress. What it could fail to suppress was a field's own drag-selection
///         starting on a part inside the field — a search box's magnifier — and these drive exactly
///         that drag with a mouse.
///     </para>
///     <para>
///         ⚠ <b>No <c>user-select</c> in any sheet here but the theme's.</b> The search box case reads
///         <c>ControlTheme.vcss</c> through <see cref="ControlFixture" />'s install, so it fails if the
///         theme stops declaring the property as surely as if the field stops reading it.
///     </para>
/// </remarks>
public class UserSelectTests {
    const string Wide = "search-box, textbox { width: 400px; }";

    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>A mouse drag from a point to the right along the same row.</summary>
    static void DragRight(ControlFixture fixture, float x, float y, float distance) {
        fixture.Press(x, y, type: PointerType.Mouse);

        for (var step = 1; step <= 4; step++) {
            fixture.MovePointer(x + (distance * step / 4f), y, type: PointerType.Mouse);
            fixture.Advance(Frame);
        }

        fixture.Release(x + distance, y, type: PointerType.Mouse);
    }

    [Fact]
    public void A_drag_that_starts_on_a_search_boxs_magnifier_selects_nothing() {
        using var fixture = new ControlFixture(css: Wide);

        var search = fixture.Add<SearchBox>();
        search.Value = "mesh mesh mesh mesh mesh";
        fixture.Update();

        var glass = search.SearchIcon.Bounds;
        var before = search.CaretIndex;

        DragRight(fixture, glass.X + (glass.Width * 0.5f), glass.Y + (glass.Height * 0.5f), 150f);

        // ⚠ Before #225 this selected the first word or two: the press bubbled from the icon to the
        // field, which moved the caret and captured the pointer for a selection drag.
        Assert.False(search.HasSelection, $"a drag off the magnifier selected `{search.SelectedText}`");
        Assert.Equal(before, search.CaretIndex);

        // A press anywhere in the field's box still focuses it.
        Assert.True(search.IsFocused);
    }

    [Fact]
    public void The_same_drag_starting_on_the_text_still_selects() {
        // The paired half: what changed is where the drag may start, not the field's ability to select.
        using var fixture = new ControlFixture(css: Wide);

        var search = fixture.Add<SearchBox>();
        search.Value = "mesh mesh mesh mesh mesh";
        fixture.Update();

        var glass = search.SearchIcon.Bounds;

        DragRight(fixture, glass.X + glass.Width + 12f, glass.Y + (glass.Height * 0.5f), 120f);

        Assert.True(search.HasSelection, "a drag across the text selected nothing");
    }

    [Fact]
    public void Select_none_on_the_field_itself_leaves_its_text_selectable() {
        // ⚠ CSS UI 4 § 6.1: on an editable element the used value is `contain`, whatever was
        // declared — so `select-none` on a text box, or on a panel around one, does not take its
        // text away from the person typing it. The walk stops short of the field for this reason.
        using var fixture = new ControlFixture(css: Wide + " .locked { user-select: none; }");

        var box = fixture.Add<TextBox>();
        box.AddClass("locked");
        box.Value = "mesh mesh mesh mesh mesh";
        fixture.Update();

        var bounds = box.Bounds;
        DragRight(fixture, bounds.X + 8f, bounds.Y + (bounds.Height * 0.5f), 120f);

        Assert.True(box.HasSelection, "select-none on an editable field took its selection away");
    }
}
