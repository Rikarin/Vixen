// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Editor.Blockout;
using Vixen.Editor.Plugin;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Blockout's menus, panels and mode, asserted in the editor the executable builds.</summary>
/// <remarks>
///     <para>
///         <b>By effect, on the path <c>EditorHost</c> takes.</b> <c>EditorSession</c> hands the
///         application <c>EditorModules.Standard()</c> exactly as the host does, so what is asserted
///         here is what somebody who opens the editor sees — not what the module would do if a test
///         stood a shell up for it. Doc 50 § D6's <c>ReachTest</c>, for the one module P1 migrates
///         first.
///     </para>
///     <para>
///         ⚠ <b>Written before the migration and red against the code it was written over</b>: the
///         modules were activated inside <c>RegisterModes</c>, before <c>SceneMenu</c> had put Scene on
///         the bar, so <c>BlockoutModule</c>'s <c>FindMenu("editor.menu.scene")</c> answered null and
///         it took its own early return — "a host with no Scene menu is a test" — in the one host that
///         is not a test. Doc 24's five submenus had not been in the shipped editor since they moved
///         out of the application, and no test had looked.
///     </para>
/// </remarks>
public class BlockoutReachTests {
    /// <summary>The six submenus, after Measure, exactly as doc 24's tables order them.</summary>
    /// <remarks>
    ///     ⚠ <b>Written out by hand and not read back from the module</b>, so that a migration from
    ///     hand registration to declarations is held to the arrangement that was there before it —
    ///     a snapshot taken from the code under test would agree with whatever that code did.
    /// </remarks>
    static string Expected() {
        var kinds = string.Join(" ", BlockoutMode.Kinds.Select(BlockoutMode.KindCommand));

        return string.Join(
            "\n",
            "editor.menu.elements[blockout.select-all blockout.select-none blockout.invert | blockout.select-loop "
            + "blockout.select-ring blockout.grow blockout.shrink | blockout.select-group blockout.select-coplanar "
            + "blockout.select-linked]",
            "editor.menu.geometry[blockout.extrude blockout.extrude-individual blockout.inset blockout.inset-individual "
            + "blockout.bevel blockout.loop-cut blockout.subdivide blockout.knife | blockout.bridge blockout.fill "
            + "blockout.weld blockout.dissolve blockout.delete | blockout.flip blockout.detach]",
            "editor.menu.blockout-create[editor.menu.blockout-shape[" + kinds + "] blockout.shape-tool "
            + "blockout.create-shape | blockout.cube-grid blockout.push-out blockout.push-in | blockout.duplicate "
            + "blockout.mirror blockout.array blockout.radial]",
            "editor.menu.blockout-surfaces[blockout.project-world blockout.project-box blockout.fit-uv | "
            + "blockout.smooth blockout.harden blockout.auto-smooth | blockout.new-group]",
            "editor.menu.blockout-boolean[blockout.union blockout.subtract blockout.intersect | blockout.plane-cut "
            + "blockout.trim | blockout.apply-boolean]",
            "editor.menu.blockout-handoff[blockout.bake blockout.make-editable | blockout.export-obj "
            + "blockout.export-gltf]"
        );
    }

    [Fact]
    public void The_scene_menu_carries_blockouts_six_submenus_after_measure() {
        using var session = EditorSession.Start();

        var scene = Assert.Single(session.Shell.Menus.Menus, menu => menu.Title.Id == "editor.menu.scene");
        var precision = scene.IndexOfSubmenu("editor.menu.precision");

        Assert.True(precision >= 0, "the Scene menu has no Measure submenu to put Blockout's after");

        var after = scene.Entries.Skip(precision + 1).Take(6).Select(Render);

        Assert.Equal(Expected(), string.Join("\n", after));
    }

    /// <summary>Each Blockout line is on the bar once, so a declaration beside a hand registration shows.</summary>
    /// <remarks>
    ///     ⚠ <b>A count, because presence cannot see the failure a migration invites.</b> A module that
    ///     gained its declarations and kept its hand registrations would put every line in twice and
    ///     pass every "is it there" assertion written.
    /// </remarks>
    [Fact]
    public void Every_blockout_line_panel_and_mode_is_registered_exactly_once() {
        using var session = EditorSession.Start();

        var shell = session.Shell;
        var lines = new List<string>();

        foreach (var menu in shell.Menus.Menus) {
            Collect(menu, lines);
        }

        var blockout = lines.Where(id => id.StartsWith("blockout.", StringComparison.Ordinal)).ToList();

        // Sixty-three: doc 24's five tables (ten, fifteen, nine, seven, six), the twelve shape kinds and
        // the four handoff verbs.
        Assert.Equal(63, blockout.Count);
        Assert.Equal(blockout.Count, blockout.Distinct(StringComparer.Ordinal).Count());

        Assert.Single(shell.Workspace.Panels, panel => panel.Id == BlockoutMode.PanelId);
        Assert.Single(shell.Workspace.Panels, panel => panel.Id == "blockout.uv");
        Assert.Single(shell.Modes.Modes, mode => mode.Id == BlockoutMode.ModeId);

        var module = session.Application.PluginHost.Find(BlockoutModule.ModuleId);

        Assert.NotNull(module);
        Assert.Equal(PluginState.Active, module.State);
        Assert.True(module.IsBuiltIn);

        // #1307: the verbs the commands run are the ones a plugin can Require — one instance.
        var mode = Assert.IsType<BlockoutMode>(shell.Modes.Modes.Single(entry => entry.Id == BlockoutMode.ModeId));

        Assert.Same(mode.Verbs, session.Application.PluginHost.Services.Require<IBlockout>());
    }

    /// <summary>Unloading Blockout takes every line, panel, mode and rule out; loading it again puts each back once.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Red before #1285 on three counts.</b> The two panels went through
    ///         <c>Shell.RegisterPanel</c>, which records no removal, so they and their View-menu toggles
    ///         survived the unload and the reload's second registration threw; the theme was installed
    ///         and never released; and the menus — once they were on the bar at all — were the only
    ///         part that was taken back.
    ///     </para>
    ///     <para>
    ///         <b>The loaded-sheet count returns to the count before the module across load, unload and
    ///         load</b> — a reload and then a fresh instance — and the theme's text is in the cascade
    ///         exactly once while it runs and not at all after. Before #1484 a sheet could be emptied
    ///         and not removed, so this was held to the count after the first load. ⚠ Red against the
    ///         emptying theme: the unloaded count was one too many. ⚠ <c>WaitForCollection</c> is not
    ///         asserted, because for a built-in it answers true without collecting anything — there is
    ///         no load context to wait for — and a predicate that cannot be false proves nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Unloading_and_reloading_blockout_takes_everything_out_and_puts_it_back_once() {
        using var session = EditorSession.Start();

        var shell = session.Shell;
        var host = session.Application.PluginHost;
        var styles = shell.Document.Styles;

        // The module is already in, so the document without it is one sheet fewer.
        var loaded = styles.LoadedSheetCount;
        var baseline = loaded - 1;
        Assert.Equal(1, Themes(styles));

        // A reload is an unload and a load of the same module: everything its first activation
        // registered is taken out and registered again — once.
        var report = host.Reload(BlockoutModule.ModuleId);

        Assert.Empty(report.Diagnostics);
        Assert.Equal(PluginState.Active, host.Find(BlockoutModule.ModuleId)!.State);

        Assert.Equal(63, Lines(shell).Count(id => id.StartsWith("blockout.", StringComparison.Ordinal)));
        Assert.Single(shell.Workspace.Panels, panel => panel.Id == BlockoutMode.PanelId);
        Assert.Single(shell.Workspace.Panels, panel => panel.Id == "blockout.uv");
        Assert.Single(shell.Modes.Modes, mode => mode.Id == BlockoutMode.ModeId);
        Assert.Equal(1, Themes(styles));
        Assert.Equal(loaded, styles.LoadedSheetCount);
        Assert.True(host.Services.Contains<IBlockout>());

        // And an unload leaves nothing of it registered, and nothing of its rules in the cascade.
        Assert.True(host.Unload(BlockoutModule.ModuleId));

        Assert.DoesNotContain(Lines(shell), id => id.StartsWith("blockout.", StringComparison.Ordinal));
        Assert.DoesNotContain(shell.Workspace.Panels, panel => panel.Id.StartsWith("blockout", StringComparison.Ordinal));
        Assert.Null(shell.Commands[EditorShell.PanelCommand(BlockoutMode.PanelId)]);
        Assert.DoesNotContain(shell.Modes.Modes, mode => mode.Id == BlockoutMode.ModeId);
        Assert.Equal(0, Themes(styles));
        Assert.Equal(baseline, styles.LoadedSheetCount);
        Assert.False(host.Services.Contains<IBlockout>());

        // And a fresh instance, which is what a disk plugin's reload is, puts the one sheet back.
        var again = host.Activate(BlockoutModule.ModuleId, BlockoutModule.ModuleName, new BlockoutModule());

        Assert.Equal(PluginState.Active, again.State);
        Assert.Equal(1, Themes(styles));
        Assert.Equal(loaded, styles.LoadedSheetCount);
    }

    static int Themes(StyleEngine styles) =>
        Enumerable.Range(0, styles.SheetCount).Count(index => styles.SheetText(index) == BlockoutTheme.Css);

    static List<string> Lines(EditorShell shell) {
        var lines = new List<string>();

        foreach (var menu in shell.Menus.Menus) {
            Collect(menu, lines);
        }

        return lines;
    }

    static string Render(MenuEntry entry) => entry switch {
        MenuCommand(var id) => id,
        MenuSeparator => "|",
        MenuSubmenu(var group) => group.Title.Id + "[" + string.Join(" ", group.Entries.Select(Render)) + "]",
        MenuDynamic(var ids) => string.Join(" ", ids()),
        _ => entry.ToString() ?? "?"
    };

    static void Collect(MenuGroup group, List<string> into) {
        foreach (var entry in group.Entries) {
            switch (entry) {
                case MenuCommand(var id):
                    into.Add(id);
                    break;
                case MenuSubmenu(var child):
                    Collect(child, into);
                    break;
                case MenuDynamic(var ids):
                    into.AddRange(ids());
                    break;
                default:
                    break;
            }
        }
    }
}
