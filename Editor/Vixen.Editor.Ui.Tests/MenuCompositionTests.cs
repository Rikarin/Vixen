// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Input;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>Two things that had a load-order answer and should not have.</summary>
public class MenuCompositionTests {
    static StringId Tools => new("editor.menu.tools", "Tools");

    /// <summary>
    ///     ⚠ <b>The editor came up with two menus called Tools.</b> The application described its own
    ///     bar and a project script's <c>[EditorMenu("Tools/…")]</c> created a second, because both
    ///     <c>AddMenu</c> and <c>InsertMenu</c> made one unconditionally — so which menu held which
    ///     lines depended on whether the modules activated before the application built its bar.
    /// </summary>
    [Fact]
    public void A_menu_named_twice_is_one_menu_whichever_order_it_is_named_in() {
        var model = new MenuModel();

        var first = model.AddMenu(Tools);
        var second = model.InsertMenu(0, Tools);
        var third = model.AddMenu(Tools);

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Single(model.Menus, menu => menu.Title.Id == "editor.menu.tools");
    }

    /// <summary>
    ///     ⚠ <b>And the one already there keeps its place.</b> Letting a later <c>InsertMenu</c> move
    ///     it would put the bar's layout back under load order's control by a different route.
    /// </summary>
    [Fact]
    public void Naming_an_existing_menu_does_not_move_it() {
        var model = new MenuModel();

        model.AddMenu(new StringId("editor.menu.file", "File"));
        model.AddMenu(Tools);
        model.InsertMenu(0, Tools);

        Assert.Equal("editor.menu.file", model.Menus[0].Title.Id);
        Assert.Equal("editor.menu.tools", model.Menus[1].Title.Id);
    }

    [Fact]
    public void Find_answers_for_a_menu_on_the_bar_and_not_for_one_that_is_not() {
        var model = new MenuModel();

        model.AddMenu(Tools);

        Assert.NotNull(model.Find("editor.menu.tools"));
        Assert.Null(model.Find("editor.menu.nothing"));
    }

    /// <summary>
    ///     ⚠ <b>Focus Selection is pressed several times a minute in every mode</b>, and blockout's
    ///     Fill Hole had bound the same key for its own context — so the key stopped working in the
    ///     one mode where somebody is looking around the most. A context binding beats a global one,
    ///     which is right for almost everything and wrong for this.
    /// </summary>
    [Fact]
    public void A_reserved_command_keeps_its_key_inside_a_context_that_binds_the_same_chord() {
        var keys = new KeyMap { ContextOf = id => id.StartsWith("blockout.", StringComparison.Ordinal) ? "blockout" : null };
        var chord = new KeyChord(InputKey.F, ModifierKeys.None);

        keys.SetDefault("scene.focus", chord);
        keys.SetDefault("blockout.fill", chord);

        // As it was: inside the context, the context's command answers.
        Assert.Equal("blockout.fill", keys.CommandFor(chord, "blockout"));

        keys.Reserve("scene.focus");

        Assert.True(keys.IsReserved("scene.focus"));
        Assert.Equal("scene.focus", keys.CommandFor(chord, "blockout"));
        Assert.Equal("scene.focus", keys.CommandFor(chord));
    }

    /// <summary>
    ///     ⚠ <b>The command is reserved, not the chord.</b> Rebinding Focus Selection has to move the
    ///     protection with it — a reserved chord would freeze the original key and go on shielding
    ///     whatever later moved onto it.
    /// </summary>
    [Fact]
    public void Rebinding_a_reserved_command_moves_the_protection_and_frees_the_old_key() {
        var keys = new KeyMap();
        var f = new KeyChord(InputKey.F, ModifierKeys.None);
        var g = new KeyChord(InputKey.G, ModifierKeys.None);

        keys.SetDefault("scene.focus", f);
        keys.Reserve("scene.focus");
        keys.Bind("scene.focus", g, replace: true);

        keys.SetDefault("blockout.fill", f);

        Assert.Equal("scene.focus", keys.CommandFor(g, "blockout"));
        Assert.Equal("blockout.fill", keys.CommandFor(f, "blockout"));
    }
}
