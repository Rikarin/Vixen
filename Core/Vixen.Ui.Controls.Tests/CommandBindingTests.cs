// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A control bound to a command id, and the four things it then shows without being told.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here writes an enablement rule, and that is the assertion.</b> Doc 45's
///         headline is that an application declares a menu of <i>ids</i> and the greying falls out
///         of who is listening — so every test in this file registers handlers on views, moves the
///         real focus, and reads <see cref="Control.Disabled" /> back off a control that no line of
///         test code ever assigned it on.
///     </para>
///     <para>
///         ⚠ <b>And none of it references <c>Vixen.Editor.Ui</c>.</b> This assembly cannot: it is a
///         <c>Vixen.Ui.Controls</c> test project, which is exactly the application the criterion is
///         written about.
///     </para>
/// </remarks>
public class CommandBindingTests {
    /// <summary>A focusable element, which is what a view is for these purposes.</summary>
    static UiElement View(UiElement parent) {
        var element = parent.Add("div");
        element.Focusable = true;

        return element;
    }

    /// <summary>A menu on the root, opened so that its items have asked the route.</summary>
    static Menu Menu(ControlFixture fixture) {
        var menu = fixture.Document.Root.Add<Menu>();
        fixture.Update();

        return menu;
    }

    static MenuItem Item(Menu menu, string label, string commandId) {
        var item = menu.AddItem(label);
        item.Command = commandId;

        return item;
    }

    [Fact]
    public void An_id_nothing_handles_disables_the_item_and_the_menu_writes_no_rule() {
        using var fixture = new ControlFixture();

        var view = View(fixture.Document.Root);
        view.AddCommandHandler("edit.copy", () => { });
        fixture.Document.Focus(view);

        var menu = Menu(fixture);
        var copy = Item(menu, "Copy", "edit.copy");
        var paste = Item(menu, "Paste", "edit.paste");

        menu.Open();
        fixture.Update();

        // The whole criterion, in two lines: one id is answered and one is not, and the difference
        // is visible without the menu knowing what either command is.
        Assert.False(copy.Disabled);
        Assert.True(paste.Disabled);

        // And it is the cascade that has been told, not only a bool — `:disabled` is what greys it.
        Assert.True(paste.State.HasFlag(ElementState.Disabled));
        Assert.False(copy.State.HasFlag(ElementState.Disabled));
    }

    [Fact]
    public void Enablement_follows_a_view_s_predicate_with_no_code_in_the_menu() {
        using var fixture = new ControlFixture();

        var selection = 0;

        var view = View(fixture.Document.Root);
        view.AddCommandHandler("edit.copy", () => { }, () => selection > 0);
        fixture.Document.Focus(view);

        var menu = Menu(fixture);
        var copy = Item(menu, "Copy", "edit.copy");

        menu.Open();
        fixture.Update();
        Assert.True(copy.Disabled);

        // The selection changes and the menu is asked again. Nothing in this test — and nothing in
        // `Menu` — knows what a selection is.
        selection = 3;
        menu.Close();
        menu.Open();
        fixture.Update();
        Assert.False(copy.Disabled);

        selection = 0;
        menu.Close();
        menu.Open();
        fixture.Update();
        Assert.True(copy.Disabled);
    }

    [Fact]
    public void The_focused_view_decides_what_one_menu_item_runs() {
        using var fixture = new ControlFixture();

        var ran = "";

        var outliner = View(fixture.Document.Root);
        var browser = View(fixture.Document.Root);

        outliner.AddCommandHandler("edit.copy", () => ran = "outliner");
        browser.AddCommandHandler("edit.copy", () => ran = "browser");

        var menu = Menu(fixture);
        var copy = Item(menu, "Copy", "edit.copy");

        fixture.Document.Focus(outliner);
        menu.Open();
        fixture.Update();
        copy.Activate();
        Assert.Equal("outliner", ran);

        // ⚠ The same item. Not a rebuilt menu, not a second item — the one control, whose behaviour
        // changed because the focus did and for no other reason.
        fixture.Document.Focus(browser);
        menu.Open();
        fixture.Update();
        copy.Activate();
        Assert.Equal("browser", ran);
    }

    [Fact]
    public void A_greyed_item_runs_nothing_when_it_is_activated() {
        using var fixture = new ControlFixture();

        var runs = 0;

        var view = View(fixture.Document.Root);
        view.AddCommandHandler("edit.copy", () => runs++, () => false);
        fixture.Document.Focus(view);

        var menu = Menu(fixture);
        var copy = Item(menu, "Copy", "edit.copy");
        var unhandled = Item(menu, "Paste", "edit.paste");

        menu.Open();
        fixture.Update();

        copy.Activate();
        unhandled.Activate();

        Assert.Equal(0, runs);
    }

    [Fact]
    public void A_handler_that_renames_itself_renames_the_item_and_a_plain_one_leaves_the_label_alone() {
        using var fixture = new ControlFixture();

        var what = "Move";

        var view = View(fixture.Document.Root);
        view.AddCommandHandler("edit.undo", () => { }, title: () => $"Undo {what}");
        view.AddCommandHandler("edit.copy", () => { });
        fixture.Document.Focus(view);

        var menu = Menu(fixture);
        var undo = Item(menu, "Undo", "edit.undo");
        var copy = Item(menu, "Copy", "edit.copy");

        menu.Open();
        fixture.Update();

        Assert.Equal("Undo Move", undo.Label);

        // ⚠ The other half, and the one that would go unnoticed: a handler with no title must leave
        // the label the menu was written with. A binding that assigned `Title` unconditionally
        // blanks every ordinary line and every one of the assertions above still passes.
        Assert.Equal("Copy", copy.Label);

        what = "Delete";
        menu.Close();
        menu.Open();
        fixture.Update();

        Assert.Equal("Undo Delete", undo.Label);
        Assert.Equal("Copy", copy.Label);
    }

    [Fact]
    public void A_checkable_command_gets_a_tick_and_a_plain_one_gets_no_gutter() {
        using var fixture = new ControlFixture();

        var grid = true;

        var view = View(fixture.Document.Root);
        view.AddCommandHandler("view.grid", () => grid = !grid, isChecked: () => grid);
        view.AddCommandHandler("edit.copy", () => { });
        fixture.Document.Focus(view);

        var menu = Menu(fixture);
        var toggle = Item(menu, "Show Grid", "view.grid");
        var copy = Item(menu, "Copy", "edit.copy");

        menu.Open();
        fixture.Update();

        Assert.True(toggle.State.HasFlag(ElementState.Checked));
        Assert.Equal("flex", toggle.Mark.GetStyle("display"));

        // ⚠ The command that is not a toggle never grew a mark at all, which is what stops an
        // ordinary menu being indented by a column of empty ticks. Asserted over the children
        // rather than through `Mark`, because reading `Mark` is what creates it.
        Assert.DoesNotContain(copy.Children, child => child is Icon);
        Assert.False(copy.State.HasFlag(ElementState.Checked));

        grid = false;
        menu.Close();
        menu.Open();
        fixture.Update();

        Assert.False(toggle.State.HasFlag(ElementState.Checked));
        Assert.Equal("none", toggle.Mark.GetStyle("display"));
    }

    [Fact]
    public void An_open_menu_takes_the_focus_and_the_command_route_does_not_follow_it() {
        using var fixture = new ControlFixture();

        var view = View(fixture.Document.Root);
        view.AddCommandHandler("edit.copy", () => { });
        fixture.Document.Focus(view);

        var menu = Menu(fixture);
        var copy = Item(menu, "Copy", "edit.copy");

        menu.Open();
        fixture.Update();

        // ⚠ The measurement, not the consequence. The menu really does take the focus — `OnOpened`
        // focuses the first item so the arrow keys work — and that is exactly what would have made
        // every menu item resolve `edit.copy` from inside the menu and find nothing.
        Assert.Same(copy, fixture.Document.Focused);

        // The route did not follow it, so the view is still who answers.
        Assert.Same(view, fixture.Document.CommandFocus);
        Assert.Same(view, CommandRoute.Resolve(fixture.Document, "edit.copy")!.Value.Element);
        Assert.False(copy.Disabled);
    }

    [Fact]
    public void A_command_bound_button_is_not_a_place_the_route_resolves_from() {
        using var fixture = new ControlFixture();

        var view = View(fixture.Document.Root);
        view.AddCommandHandler("file.save", () => { });
        fixture.Document.Focus(view);

        var button = fixture.Add<Button>();
        button.Command = "file.save";

        // A click focuses the button, the way a click on any control does.
        fixture.Click(button);
        Assert.Same(button, fixture.Document.Focused);

        // ⚠ And the strip it is on is still not what its own commands mean. A toolbar whose Copy
        // button resolved from the toolbar would copy from the toolbar.
        Assert.Same(view, fixture.Document.CommandFocus);
        Assert.False(button.Disabled);
    }

    [Fact]
    public void A_plain_button_binds_the_same_way_a_menu_item_does() {
        using var fixture = new ControlFixture();

        var runs = 0;
        var enabled = false;

        var view = View(fixture.Document.Root);
        view.AddCommandHandler("file.save", () => runs++, () => enabled);
        fixture.Document.Focus(view);

        var button = fixture.Add<Button>();
        button.Label = "Save";
        button.Command = "file.save";

        Assert.True(button.Disabled);

        button.Activate();
        Assert.Equal(0, runs);

        enabled = true;
        button.RefreshCommand();

        Assert.False(button.Disabled);
        button.Activate();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_button_that_is_always_on_screen_follows_the_invalidation_instead_of_polling() {
        using var fixture = new ControlFixture();

        var asked = 0;
        var enabled = false;

        var view = View(fixture.Document.Root);
        view.AddCommandHandler(
            "file.save",
            () => { },
            () => {
                asked++;

                return enabled;
            }
        );

        fixture.Document.Focus(view);

        var button = fixture.Add<Button>();
        button.Command = "file.save";

        fixture.Advance(TimeSpan.FromMilliseconds(16));
        Assert.True(button.Disabled);

        // ⚠ Ten frames in which nothing said anything, and the predicate is not asked again. That
        // is the half of step 5 that a coalescing test cannot show: a strip of twenty buttons
        // costs nothing on the frames where nothing changed.
        var settled = asked;

        for (var i = 0; i < 10; i++) {
            fixture.Advance(TimeSpan.FromMilliseconds(16));
        }

        Assert.Equal(settled, asked);

        // ⚠ And the other direction: the button really does follow, without a menu to open and
        // without anyone touching the button. The view says its selection changed; that is the
        // only line of application code in this.
        enabled = true;
        fixture.Document.InvalidateCommands();
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.True(asked > settled);
        Assert.False(button.Disabled);
    }

    [Fact]
    public void A_removed_button_stops_following_the_document() {
        using var fixture = new ControlFixture();

        var asked = 0;

        var view = View(fixture.Document.Root);
        view.AddCommandHandler(
            "file.save",
            () => { },
            () => {
                asked++;

                return true;
            }
        );

        fixture.Document.Focus(view);

        var button = fixture.Add<Button>();
        button.Command = "file.save";

        fixture.Document.Remove(button);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        var settled = asked;

        // A shell that rebuilds its toolbar every time a mode changes would otherwise accumulate a
        // full set of dead buttons, each still asking the route on every invalidation for ever.
        fixture.Document.InvalidateCommands();
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal(settled, asked);
    }

    [Fact]
    public void An_item_bound_from_markup_is_bound_the_same_as_one_bound_from_code() {
        using var fixture = new ControlFixture();

        var runs = 0;

        var view = View(fixture.Document.Root);
        view.AddCommandHandler("edit.copy", () => runs++);
        fixture.Document.Focus(view);

        var sheet = new CommandMenu();
        BuildContext.BuildInto(sheet, fixture.Document, fixture.Document.Root);
        fixture.Update();

        // ⚠ A real `.vxml`, compiled by the generator, because `Command="edit.copy"` reaching the
        // property is the whole claim: markup is the intended authoring path and a C#-only binding
        // would be an engine gap rather than a style preference.
        var menu = sheet.Menu;
        menu.Open();
        fixture.Update();

        var items = menu.Items;
        Assert.Equal(2, items.Count);

        Assert.Equal("edit.copy", items[0].Command);
        Assert.False(items[0].Disabled);
        Assert.True(items[1].Disabled);

        items[0].Activate();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_focused_text_box_answers_select_all_and_outranks_the_shell() {
        using var fixture = new ControlFixture();

        var shell = "";
        fixture.Document.Root.AddCommandHandler("edit.select-all", () => shell = "shell");

        var field = fixture.Document.Root.Add<TextBox>();
        field.Value = "hello";
        fixture.Update();

        var menu = Menu(fixture);
        var selectAll = Item(menu, "Select All", "edit.select-all");

        // Nothing focused: the walk starts at the root and the shell's meaning is the only one.
        menu.Open();
        fixture.Update();
        selectAll.Activate();
        Assert.Equal("shell", shell);
        Assert.Equal(0, field.CaretIndex);

        // ⚠ The first production instance of `CommandRoute`'s defining rule. `AddCommandHandler` had
        // zero callers outside test projects, so "the nearest responder wins" was a claim only its
        // own tests could make. The field is nearer than the root, so the same menu item means
        // something different because the caret is in a text box.
        shell = "";
        fixture.Document.Focus(field);
        menu.Open();
        fixture.Update();
        selectAll.Activate();

        Assert.Equal("", shell);
        Assert.Equal(0, field.SelectionAnchor);
        Assert.Equal(5, field.CaretIndex);
    }

    [Fact]
    public void An_empty_text_box_greys_select_all_without_the_menu_knowing_why() {
        using var fixture = new ControlFixture();

        var field = fixture.Document.Root.Add<TextBox>();
        fixture.Update();
        fixture.Document.Focus(field);

        var menu = Menu(fixture);
        var selectAll = Item(menu, "Select All", "edit.select-all");

        menu.Open();
        fixture.Update();
        Assert.True(selectAll.Disabled);

        field.Value = "hello";
        menu.Close();
        menu.Open();
        fixture.Update();
        Assert.False(selectAll.Disabled);
    }

    [Fact]
    public void Disabling_a_control_takes_the_focus_off_it_even_when_it_refuses() {
        using var fixture = new ControlFixture();

        var field = fixture.Document.Root.Add<TextBox>();
        field.Value = "not a number";
        field.AddHandler<FocusEvent>((_, args) => args.Cancel = !args.Gained);

        fixture.Update();
        fixture.Document.Focus(field);
        Assert.Same(field, fixture.Document.Focused);

        // ⚠ The third path that is not a user's decision, beside removal and teardown. A field that
        // can refuse to resign must not be able to refuse being disabled: the keyboard would be left
        // talking to a control that will not answer, and Tab starts from the focus so there is no way
        // out of it.
        field.Disabled = true;

        Assert.Null(fixture.Document.Focused);
    }


    [Fact]
    public void A_bound_toggle_holds_the_command_s_check_state_in_the_property_the_readers_read() {
        using var fixture = new ControlFixture();

        var grid = true;

        var view = View(fixture.Document.Root);
        view.AddCommandHandler("view.grid", () => grid = !grid, isChecked: () => grid);
        fixture.Document.Focus(view);

        var toggle = fixture.Document.Root.Add<ToggleButton>();
        toggle.Command = "view.grid";
        fixture.Update();

        // ⚠ Both, and the second one is the point. `ElementState.Checked` is what the theme draws,
        // so a binding that wrote only it produced a control that LOOKED right; `IsChecked` is what
        // every C# reader and every `bind:IsChecked` reads, and it was left wherever the last click
        // put it.
        Assert.True(toggle.State.HasFlag(ElementState.Checked));
        Assert.True(toggle.IsChecked);

        grid = false;
        // ⚠ `Advance` and not `Update`: `CommandsInvalidated` is coalesced and raised from
        // `UiDocument.Tick`, so a test that only lays out never asks the route again and passes
        // whatever the control was left holding.
        fixture.Document.InvalidateCommands();
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.False(toggle.State.HasFlag(ElementState.Checked));
        Assert.False(toggle.IsChecked);
    }

    [Fact]
    public void A_command_that_refuses_to_follow_the_click_leaves_the_bound_toggle_where_it_was() {
        using var fixture = new ControlFixture();

        // The command runs — it is not disabled — and declines to move its own check state. A
        // "wireframe" that cannot be turned on while the viewport is 2D is exactly this shape.
        var runs = 0;

        var view = View(fixture.Document.Root);
        view.AddCommandHandler("view.wireframe", () => runs++, isChecked: () => false);
        fixture.Document.Focus(view);

        var toggle = fixture.Document.Root.Add<ToggleButton>();
        toggle.Command = "view.wireframe";
        fixture.Update();

        Assert.False(toggle.IsChecked);

        toggle.Activate();
        fixture.Update();

        // ⚠ The optimistic flip in `ToggleBase.Activate` is a guess, and the command is the
        // authority. It ran once and said no, so the control says no — without the surface keeping
        // a hand-written read-back on `Clicked`, which is what every consumer had to do.
        Assert.Equal(1, runs);
        Assert.False(toggle.IsChecked);
        Assert.False(toggle.State.HasFlag(ElementState.Checked));
    }

    [Fact]
    public void A_command_that_does_follow_the_click_moves_the_bound_toggle_once_and_not_twice() {
        using var fixture = new ControlFixture();

        var wireframe = false;
        var changes = 0;

        var view = View(fixture.Document.Root);
        view.AddCommandHandler("view.wireframe", () => wireframe = !wireframe, isChecked: () => wireframe);
        fixture.Document.Focus(view);

        var toggle = fixture.Document.Root.Add<ToggleButton>();
        toggle.Command = "view.wireframe";
        fixture.Update();

        toggle.CheckedChanged += (_, _) => changes++;

        toggle.Activate();
        fixture.Update();

        Assert.True(wireframe);
        Assert.True(toggle.IsChecked);

        // Order, not timing: the re-read after the command agrees with the optimistic flip, and the
        // property only raises on a real change — so a consumer counting `CheckedChanged` sees one
        // event per click and not the flip-plus-confirmation pair.
        Assert.Equal(1, changes);

        toggle.Activate();
        fixture.Update();

        Assert.False(wireframe);
        Assert.False(toggle.IsChecked);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void An_ordinary_command_does_not_un_draw_the_toggle_it_is_bound_to() {
        using var fixture = new ControlFixture();

        var view = View(fixture.Document.Root);
        view.AddCommandHandler("edit.copy", () => { });
        fixture.Document.Focus(view);

        var toggle = fixture.Document.Root.Add<ToggleButton>();
        toggle.IsChecked = true;
        toggle.Command = "edit.copy";
        fixture.Update();

        // ⚠ The mirror image of the defect above, and the reason this override does not defer to
        // the base for the non-checkable case. `RefreshCommand` calls `ShowCheck(false, false)` on
        // every invalidation, and the base clears `ElementState.Checked` — so a toggle bound to a
        // command that is not a toggle would be drawn off while `IsChecked` stayed true. A command
        // with no check state has nothing to say about this control's.
        Assert.True(toggle.IsChecked);
        Assert.True(toggle.State.HasFlag(ElementState.Checked));

        // ⚠ `Advance` and not `Update`: `CommandsInvalidated` is coalesced and raised from
        // `UiDocument.Tick`, so a test that only lays out never asks the route again and passes
        // whatever the control was left holding.
        fixture.Document.InvalidateCommands();
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.True(toggle.IsChecked);
        Assert.True(toggle.State.HasFlag(ElementState.Checked));
    }
}
