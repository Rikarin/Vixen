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
}
