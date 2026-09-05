// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What happens to the focus when a pool parks the element that has it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Skipping hidden elements in the tab order fixed the walk and left this half
///         standing.</b> An element is only kept out of the order at the moment somebody asks for the
///         order; nothing looks at the element that was <i>already focused</i> when it was hidden. A
///         pool parking a node the user is typing into left a <c>display: none</c> element holding
///         the keyboard, with <c>:focus-within</c> still lit on every ancestor above it.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is made after an <c>Update</c>, and that is the point.</b> The
///         class is added by application code and the style that follows from it is resolved by the
///         pass — so a test that asserted immediately after adding the class would be asking about a
///         document whose styles still said the element was visible, and would pass against a fix
///         that never ran.
///     </para>
/// </remarks>
public class ParkedFocusTests {
    const string Sheet = """
        root { width: 200px; height: 200px; }
        div { width: 100px; height: 40px; }
        .parked { display: none; }
        .invisible { visibility: hidden; }
        """;

    static UiElement Stop(UiElement parent) {
        var element = parent.Add("div");
        element.Focusable = true;
        return element;
    }

    /// <summary>
    ///     ⚠ To the nearest ancestor that can hold it, rather than to nothing. The web's answer is
    ///     the document body — the focus is simply lost — and for a pooled interface that is wrong:
    ///     the ancestor a parked element hangs from is the thing that parked it, so a canvas takes
    ///     the keyboard back from its own port box instead of throwing the user out of the graph.
    /// </summary>
    [Fact]
    public void Parking_the_focused_element_hands_the_focus_to_the_nearest_ancestor_that_can_hold_it() {
        using var document = new UiDocument(200f, 200f);
        document.Load(Sheet);

        var canvas = Stop(document.Root);
        var item = canvas.Add("div");
        var field = Stop(item);

        document.Update();
        document.Focus(field);

        Assert.Same(field, document.Focused);

        item.AddClass("parked");
        document.Update();

        Assert.Same(canvas, document.Focused);
        Assert.False(field.IsFocused);
    }

    /// <summary>When there is no ancestor to hand it to, the focus is lost — which is the web's answer.</summary>
    [Fact]
    public void With_no_focusable_ancestor_the_focus_is_dropped() {
        using var document = new UiDocument(200f, 200f);
        document.Load(Sheet);

        var panel = document.Root.Add("div");
        var field = Stop(panel);

        document.Update();
        document.Focus(field);

        panel.AddClass("parked");
        document.Update();

        Assert.Null(document.Focused);
    }

    /// <summary>
    ///     ⚠ The visible half of the defect. <c>MoveFocus</c> finds its place by the focused
    ///     element's index in the order, and a hidden element is no longer in it — so the index was
    ///     <c>-1</c> and the next Tab restarted from the top of the document. A user who panned a
    ///     canvas and pressed Tab was thrown to the first control in the window.
    /// </summary>
    [Fact]
    public void Tab_after_a_park_carries_on_from_where_the_focus_landed() {
        using var document = new UiDocument(200f, 200f);
        document.Load(Sheet);

        var first = Stop(document.Root);
        var canvas = Stop(document.Root);
        var item = canvas.Add("div");
        var field = Stop(item);
        var last = Stop(document.Root);

        document.Update();
        document.Focus(field);

        item.AddClass("parked");
        document.Update();

        Assert.True(document.MoveFocus(FocusDirection.Next));
        Assert.Same(last, document.Focused);
        Assert.NotSame(first, document.Focused);
    }

    /// <summary>
    ///     ⚠ <c>visibility: hidden</c> takes the focus too, and it is asked of the element alone.
    ///     It inherits, so a descendant declaring <c>visible</c> is painted and clickable and stays a
    ///     stop — which is why this cannot be folded into the <c>display</c> climb.
    /// </summary>
    [Fact]
    public void An_element_made_invisible_loses_the_focus_as_well() {
        using var document = new UiDocument(200f, 200f);
        document.Load(Sheet);

        var panel = Stop(document.Root);
        var field = Stop(panel);

        document.Update();
        document.Focus(field);

        field.AddClass("invisible");
        document.Update();

        Assert.Same(panel, document.Focused);
    }

    /// <summary>
    ///     ⚠ <b>A hidden ancestor takes its descendants with it even though <c>display</c> does not
    ///     inherit.</b> The parked element's own computed display is still <c>flex</c>; what makes it
    ///     unreachable is the climb, and a check that only asked the focused element would find
    ///     nothing wrong with it.
    /// </summary>
    [Fact]
    public void A_park_two_levels_up_still_takes_the_focus() {
        using var document = new UiDocument(200f, 200f);
        document.Load(Sheet);

        var canvas = Stop(document.Root);
        var outer = canvas.Add("div");
        var inner = outer.Add("div");
        var field = Stop(inner);

        document.Update();
        document.Focus(field);

        outer.AddClass("parked");
        document.Update();

        Assert.Same(canvas, document.Focused);
    }

    /// <summary>An ordinary pass moves nothing, which is what the check being cheap and quiet means.</summary>
    [Fact]
    public void A_visible_focused_element_is_left_alone() {
        using var document = new UiDocument(200f, 200f);
        document.Load(Sheet);

        var panel = Stop(document.Root);
        var field = Stop(panel);

        document.Update();
        document.Focus(field);

        for (var pass = 0; pass < 4; pass++) {
            document.Update();
            Assert.Same(field, document.Focused);
        }
    }

    /// <summary>
    ///     ⚠ A parked element does not get to refuse. A focus veto is a control saying "not yet"
    ///     about a move somebody asked for — a field with an invalid value refusing to be left — and
    ///     nobody asked for this one. An element that can veto its way out of being unparked keeps
    ///     the keyboard on something no longer on the screen, which is this feature's failure mode in
    ///     every framework that ships it.
    /// </summary>
    [Fact]
    public void A_parked_element_cannot_veto_its_way_out_of_losing_the_focus() {
        using var document = new UiDocument(200f, 200f);
        document.Load(Sheet);

        var canvas = Stop(document.Root);
        var item = canvas.Add("div");
        var field = Stop(item);

        field.AddHandler<FocusEvent>(
            static (_, args) => {
                if (!args.Gained) {
                    args.Cancel = true;
                }
            }
        );

        document.Update();
        document.Focus(field);

        item.AddClass("parked");
        document.Update();

        Assert.Same(canvas, document.Focused);
    }
}
