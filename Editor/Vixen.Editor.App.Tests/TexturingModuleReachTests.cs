// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Testing;
using Vixen.Editor.Texturing;
using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The texturing toolset in the editor a user launches, and out of it again on unload (#1276).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Until #1276 nothing a user launched activated this module.</b> Its own suite activates
///         it by hand and stayed green throughout, which is the shape of the defect: a finished thing
///         nothing calls. So every assertion here is on an <see cref="EditorSession" />, which opens
///         with <c>EditorModules.Standard()</c> exactly as <c>EditorHost</c> does, and every one was red
///         before the module joined that list.
///     </para>
///     <para>
///         <b>Counted, not just found</b>: a Tools line declared by the attributes and also read by a
///         scan would be on the menu twice (<c>TexturingModule.Activate</c>'s last remark), and a
///         panel registered twice throws on reload.
///     </para>
/// </remarks>
public sealed class TexturingModuleReachTests {
    /// <summary>The four panels' ids, which is what a View-menu line and a layout name.</summary>
    static readonly string[] Panels = [
        TexturingModule.GraphPanel,
        TexturingModule.StackPanel,
        TexturingModule.PaintPanel,
        TexturingModule.MeshPanel
    ];

    /// <summary>The Tools lines, each a command the module registers and an <c>[EditorMenu]</c> places.</summary>
    static readonly string[] ToolsLines = [
        TexturingModule.OpenCommand,
        TexturingModule.OpenStackCommand,
        TexturingModule.PaintCommand,
        TexturingModule.BakeCommand,
        TexturingModule.BakeStackCommand,
        TexturingModule.BakeSplatCommand,
        TexturingModule.ForceBakeCommand,
        TexturingModule.ParallaxBakeCommand,
        TexturingModule.SaveSmartCommand,
        TexturingModule.ApplySmartCommand
    ];

    /// <summary>The two Create ▸ kinds.</summary>
    static readonly string[] Kinds = ["texturing.create-texture-graph", "texturing.create-layer-stack"];

    /// <summary>The editor a user launches has every piece of the toolset, each exactly once.</summary>
    [Fact]
    public void The_launched_editor_has_the_texturing_panels_menu_lines_and_kinds_once() {
        using var session = EditorSession.Start();

        Assert.Equal(PluginState.Active, session.Plugins.Find(TexturingModule.ModuleId)?.State);
        AssertPresentOnce(session);
    }

    /// <summary>Unloading takes every piece back out, and a fresh activation puts each back once.</summary>
    [Fact]
    public void Unloading_takes_everything_out_and_activating_again_puts_it_back_once() {
        using var session = EditorSession.Start();

        Assert.True(session.Plugins.Unload(TexturingModule.ModuleId));
        session.Settle();

        foreach (var panel in Panels) {
            Assert.DoesNotContain(session.Shell.Workspace.Panels, descriptor => descriptor.Id == panel);
        }

        foreach (var line in ToolsLines) {
            Assert.Null(session.Shell.Commands[line]);
            Assert.Equal(0, MenuLines(session, line));
        }

        foreach (var kind in Kinds) {
            Assert.DoesNotContain(session.Extensions.All<NewAssetKind>(), registered => registered.Id == kind);
        }

        Assert.False(session.Plugins.Services.Contains<ITexturing>());

        var again = session.Plugins.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.Equal(PluginState.Active, again.State);
        session.Settle();

        AssertPresentOnce(session);
    }

    /// <summary>The plugin manager's Reload re-activates the same module, and nothing doubles.</summary>
    [Fact]
    public void Reloading_puts_everything_back_exactly_once() {
        using var session = EditorSession.Start();

        var report = session.Plugins.Reload(TexturingModule.ModuleId);

        Assert.Empty(report.Diagnostics);
        Assert.Equal(PluginState.Active, session.Plugins.Find(TexturingModule.ModuleId)!.State);
        session.Settle();

        AssertPresentOnce(session);
    }

    /// <summary>After an unload nothing the editor still holds reaches the module (#1288's leak class).</summary>
    /// <remarks>
    ///     A <see cref="WeakReference" /> rather than <c>WaitForCollection</c>, which answers true for a
    ///     built-in unconditionally (#740) — <see cref="WaterModuleReachTests" /> says why.
    /// </remarks>
    [Fact]
    public void Nothing_the_editor_holds_reaches_the_module_after_an_unload() {
        using var session = EditorSession.Start();

        Assert.True(session.Plugins.Unload(TexturingModule.ModuleId));

        var reference = ActivateFresh(session);

        session.Settle();
        Assert.True(session.Plugins.Unload(TexturingModule.ModuleId));
        session.Settle();

        for (var attempt = 0; attempt < 8 && reference.IsAlive; attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(reference.IsAlive, "something the editor still holds is pointing at the texturing module.");
    }

    static void AssertPresentOnce(EditorSession session) {
        foreach (var panel in Panels) {
            Assert.Single(session.Shell.Workspace.Panels, descriptor => descriptor.Id == panel);
        }

        foreach (var line in ToolsLines) {
            Assert.Single(session.Shell.Commands.Commands, command => command.Id == line);
            Assert.True(MenuLines(session, line) == 1, $"'{line}' is on the menu bar {MenuLines(session, line)} times.");
        }

        foreach (var kind in Kinds) {
            Assert.Single(session.Extensions.All<NewAssetKind>(), registered => registered.Id == kind);
        }

        Assert.True(session.Plugins.Services.Contains<ITexturing>());
    }

    /// <summary>How many lines on the menu bar, at any depth, run a command.</summary>
    static int MenuLines(EditorSession session, string command) {
        var count = 0;

        void Walk(MenuGroup group) {
            foreach (var entry in group.Entries) {
                switch (entry) {
                    case MenuCommand line when line.CommandId == command:
                        count++;
                        break;
                    case MenuSubmenu submenu:
                        Walk(submenu.Group);
                        break;
                }
            }
        }

        foreach (var menu in session.Shell.Menus.Menus) {
            Walk(menu);
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference ActivateFresh(EditorSession session) {
        var module = new TexturingModule();

        Assert.Equal(
            PluginState.Active,
            session.Plugins.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, module).State
        );

        return new WeakReference(module);
    }
}
