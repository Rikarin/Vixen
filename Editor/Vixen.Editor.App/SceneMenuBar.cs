// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>The menu bar's Scene menu, as <c>SceneModule</c>'s (#1290).</summary>
/// <remarks>
///     <para>
///         <b>Moved, not rewritten</b> — this was <c>EditorApplication.SceneMenu</c>. It is described
///         through the <see cref="PluginContext" /> a third party is handed, so switching the module off
///         takes the menu off the bar, and the lines in it that name the application's viewport verbs
///         go with it. Those commands stay registered: the palette and the keys still reach them.
///     </para>
///     <para>
///         ⚠ <b>Its structure is what Blockout and Water place their submenus against</b>, by
///         <c>[EditorMenu]</c> paths under <c>Scene/</c> and <c>MenuGroup.IndexOfSubmenu</c> after
///         Precision. <c>SceneModule</c> activates before both, so their <c>FindMenu</c> answers this
///         menu; with the module off they find none and make a Scene menu of their own.
///     </para>
/// </remarks>
static class SceneMenuBar {
    /// <summary>Puts the Scene menu on the bar, and takes it off again with the module.</summary>
    /// <param name="context">The module's context.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The menu is described after the commands are registered, not before.</b> A menu
    ///         entry naming a command nothing has registered is skipped when the bar is built — which
    ///         is the behaviour that lets the shell name <c>file.save</c> without owning it, and
    ///         which would silently swallow every line of this menu if the order were the other way
    ///         round.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Inserted rather than appended.</b> <c>MenuModel.AddMenu</c> puts a menu at the
    ///         end of the bar, which for the shell's default set is after Help — and a menu bar
    ///         reading File, Edit, View, Help, Scene is one where the most-used menu in a 3D editor
    ///         is past the point where people stop looking.
    ///     </para>
    /// </remarks>
    public static void Register(PluginContext context) {
        // ⚠ After Entity, which is doc 20's Part C order: File, Edit, Assets, Entity, Scene, Play,
        // Window, Build, Tools, Help. Found by counting to the Entity menu rather than by a literal,
        // so that a menu added to the shell's default bar does not silently put Scene somewhere else.
        var menu = context.AddMenu(EditorStrings.MenuScene, After(context, EditorStrings.MenuEntity));

        menu.Add("scene.create-entity");

        // The same submenus the hierarchy's context menu offers, from the same command ids and the
        // same code — which is the point of them being commands rather than something a menu does
        // for itself, and what stops the two drifting apart the next time one is added to.
        EditorApplication.Creatable(menu);

        menu.Add("edit.rename", "edit.delete").AddSeparator();

        menu.AddSubmenu(EditorStrings.MenuGizmo)
            .Add("scene.translate", "scene.rotate", "scene.scale")
            .AddSeparator()
            .Add("scene.toggle-space", "scene.toggle-pivot", "scene.toggle-snap");

        // ⚠ Its own submenu rather than more lines under Gizmo, because doc 24's D4 is that snapping
        // is a service above the gizmo rather than a setting on it — and a menu that filed it under
        // the tool would be the arrangement that view objects to, one level up.
        Fill(menu.AddSubmenu(EditorStrings.MenuSnap), EditorApplication.ViewportIds.SnapIds);

        // The work plane and the precision tools, which are doc 24's D5 and its "placement and
        // precision" group. Both are about where you are building rather than about what is selected.
        Fill(menu.AddSubmenu(EditorStrings.MenuWorkPlane), EditorApplication.ViewportIds.WorkPlaneIds);
        Fill(menu.AddSubmenu(EditorStrings.MenuPrecision), EditorApplication.ViewportIds.PrecisionIds);

        // ⚠ Doc 24's five blockout submenus used to be here and are now `BlockoutModule`'s, which
        // inserts them at this point through `PluginContext.AddSubmenu` — after Measure, where doc 24
        // § D5's placement-and-precision group ends. A feature that could only append would have
        // reordered this menu the day it stopped being compiled in.

        menu.AddSubmenu(EditorStrings.MenuCamera)
            .Add("scene.view-front", "scene.view-back")
            .Add("scene.view-right", "scene.view-left")
            .Add("scene.view-top", "scene.view-bottom")
            .AddSeparator()
            .Add("scene.toggle-projection");

        // ⚠ Its own submenu rather than three more lines on the Camera one. These are preferences —
        // they change what every future drag does rather than doing anything now — and mixing them
        // in with the six view keys would make a menu where half the entries move the camera and
        // half of them silently change how it moves.
        menu.AddSubmenu(EditorStrings.MenuNavigation)
            .Add("scene.orbit-around-selection", "scene.zoom-to-cursor", "scene.invert-orbit-y")
            .AddSeparator()
            .Add("scene.orbit-left", "scene.orbit-right", "scene.orbit-up", "scene.orbit-down");

        // ⚠ Five submenus rather than five flat runs, and the grouping is doc 20's Part C. What each
        // of them holds is unbounded in the same way the Create menu is — nine view modes, eight show
        // flags, nine bookmarks — and a Scene menu with thirty-one more lines on it is one where the
        // six things people use every minute are past the point they stop reading.
        menu.AddSubmenu(EditorStrings.MenuPanes).Add(EditorApplication.ViewportIds.Arrangements);

        menu.AddSubmenu(EditorStrings.MenuViewMode).Add(EditorApplication.ViewportIds.ViewModes);

        // The grid's toggle in front of the rest, for the reason `EditorApplication.ViewportIds.ShowFlagIds` gives: it
        // is the one show flag that had a command before there were show flags.
        menu.AddSubmenu(EditorStrings.MenuShow)
            .Add("scene.toggle-grid")
            .AddSeparator()
            .Add(EditorApplication.ViewportIds.ShowFlagIds);

        // ⚠ Recall above save, which is the order they are used in. Nine "Set View n" lines at the
        // top of a submenu is nine lines to scroll past every time somebody wants the view they saved.
        menu.AddSubmenu(EditorStrings.MenuBookmarks)
            .Add(EditorApplication.ViewportIds.GoBookmarks)
            .AddSeparator()
            .Add(EditorApplication.ViewportIds.SetBookmarks);

        menu.AddSubmenu(EditorStrings.MenuSpeed).Add(EditorApplication.ViewportIds.SpeedIds);

        menu.AddSeparator().Add("scene.focus", "scene.frame-all");

        // ⚠ Doc 20's B6, on the Scene menu rather than on a menu of its own. Every one of them is
        // about *this level* — its sky, its lighting budget, its agent size, which scenes are open
        // beside it — and a "World" menu would be a second place people have to learn to look for
        // something the Scene menu is already named after.
        menu.AddSeparator()
            .Add("scene.world-settings", "scene.lighting", "scene.navigation", "scene.layers")
            .AddSeparator()
            .Add("scene.scenes", "scene.open-additive", ScenesPanel.CloseCommand, "scene.save-all-scenes");

        menu.AddSeparator().Add("scene.maximise");

        // ⚠ Rebuilt here rather than left to the one a registration triggers, which is why this runs
        // last. `AddMenu` rebuilt the bar against a menu with nothing in it yet.
        context.Shell.MenuBar.Rebuild();
    }

    /// <summary>Puts a dropdown's id list on a menu, with its rules kept as separators.</summary>
    /// <remarks>
    ///     ⚠ <b>One list, two views.</b> The snap, work-plane and precision popovers on the viewport
    ///     strip and the submenus here are the same commands in the same order with the same grouping,
    ///     and they are the same array — a second copy is two places to add the next verb to, and the
    ///     one nobody remembers is the menu.
    /// </remarks>
    static void Fill(MenuGroup group, IReadOnlyList<string?> ids) {
        foreach (var id in ids) {
            if (id is null) {
                group.AddSeparator();
            } else {
                group.Add(id);
            }
        }
    }

    /// <summary>Just after a menu with a title on the bar, or the end of the bar when it has none.</summary>
    /// <remarks>Counted rather than a literal — see the remark in <see cref="Register" />.</remarks>
    static int After(PluginContext context, StringId title) {
        var menus = context.Shell.Menus.Menus;

        for (var index = 0; index < menus.Count; index++) {
            if (menus[index].Title.Id == title.Id) {
                return index + 1;
            }
        }

        return menus.Count;
    }
}
