// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Terrain;
using Vixen.Editor.Terrain.Physics;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Rendering.Terrain;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The terrain toolset and its collision, reached by effect, through the front door (#1287, #740).</summary>
/// <remarks>
///     <para>
///         <b>Every assertion is about what the shell and the registry hold</b>, for
///         <c>WaterModuleReachTests</c>' reason: a module registering through <c>Shell.*</c> and one
///         registering through its context are indistinguishable until something unloads.
///     </para>
///     <para>
///         ⚠ <b>Against the tree before #1287 every test here but the first is red.</b> The five
///         panels and two modes outlived an unload (#740), a reload of Terrain threw "already
///         registered", and <c>TerrainPhysicsModule</c> published into <c>PluginServices</c> —
///         which has no removal and throws on a duplicate — so it could not be activated twice in one
///         session at all.
///     </para>
/// </remarks>
public sealed class TerrainModuleReachTests {
    /// <summary>The five panels doc 31's Part 2 describes, by the ids a layout names.</summary>
    static readonly string[] Panels = [
        TerrainMode.PanelId,
        FoliageMode.PanelId,
        "terrain.growth",
        "terrain.splines",
        "terrain.grass"
    ];

    static readonly string[] Modes = [TerrainMode.ModeId, FoliageMode.ModeId];

    /// <summary>A session opened the way the host opens one has every piece of both modules, once.</summary>
    [Fact]
    public void A_session_has_the_terrain_panels_modes_and_collision() {
        using var session = EditorSession.Start();

        AssertPresentOnce(session);
    }

    /// <summary>Unloading both modules takes every piece out; activating both again puts each back once.</summary>
    [Fact]
    public void Unloading_takes_everything_out_and_activating_again_puts_it_back_once() {
        using var session = EditorSession.Start();

        Assert.True(session.Plugins.Unload(TerrainPhysicsModule.ModuleId));
        Assert.True(session.Plugins.Unload(TerrainModule.ModuleId));
        session.Settle();

        foreach (var panel in Panels) {
            Assert.DoesNotContain(session.Shell.Workspace.Panels, descriptor => descriptor.Id == panel);
            Assert.Null(session.Shell.Commands[EditorShell.PanelCommand(panel)]);
        }

        foreach (var mode in Modes) {
            Assert.False(session.Shell.Modes.TryGet(mode, out _));
            Assert.Null(session.Shell.Commands[EditorModes.ModeCommand(mode)]);
        }

        Assert.Empty(session.Extensions.All<ITerrainScene>());
        Assert.Empty(session.Extensions.All<IVegetationScene>());
        Assert.Empty(session.Extensions.All<ITerrainColliders>());

        Assert.Equal(
            PluginState.Active,
            session.Plugins.Activate(TerrainModule.ModuleId, TerrainModule.ModuleName, new TerrainModule()).State
        );

        Assert.Equal(
            PluginState.Active,
            session.Plugins.Activate(
                TerrainPhysicsModule.ModuleId,
                TerrainPhysicsModule.ModuleName,
                new TerrainPhysicsModule()
            ).State
        );

        session.Settle();

        AssertPresentOnce(session);
    }

    /// <summary>The plugin manager's Reload on each module re-activates it, and nothing doubles.</summary>
    [Theory]
    [InlineData(TerrainModule.ModuleId)]
    [InlineData(TerrainPhysicsModule.ModuleId)]
    public void Reloading_puts_everything_back_exactly_once(string id) {
        using var session = EditorSession.Start();

        var report = session.Plugins.Reload(id);

        Assert.Empty(report.Diagnostics);
        Assert.Equal(PluginState.Active, session.Plugins.Find(id)!.State);
        session.Settle();

        AssertPresentOnce(session);
    }

    /// <summary>The sculpt tools follow whichever collision module is loaded, and let go of an unloaded one.</summary>
    /// <remarks>
    ///     ⚠ <b>The half <c>Owns</c> buys and <c>PluginServices.Add</c> could not.</b> Published into
    ///     the services bag, the colliders were bound once and never re-read — so after an unload the
    ///     sculpt tools went on pushing every stroke into an object from a module that was gone, and
    ///     kept it reachable. Through the registry the binding follows what is published.
    /// </remarks>
    [Fact]
    public void The_sculpt_tools_follow_the_published_colliders_through_an_unload() {
        using var session = EditorSession.Start();

        Assert.True(session.Shell.Modes.Activate(TerrainMode.ModeId));
        session.Frames(2);

        var first = Assert.IsType<PlayColliders>(Mode(session).Editing.Colliders);

        Assert.True(session.Plugins.Unload(TerrainPhysicsModule.ModuleId));
        session.Frames(2);

        Assert.Null(Mode(session).Editing.Colliders);

        session.Plugins.Activate(
            TerrainPhysicsModule.ModuleId,
            TerrainPhysicsModule.ModuleName,
            new TerrainPhysicsModule()
        );

        session.Frames(2);

        var second = Assert.IsType<PlayColliders>(Mode(session).Editing.Colliders);

        Assert.NotSame(first, second);
    }

    /// <summary>Colliders a host or a test set by hand are left alone, which is why the binding is not a plain re-read.</summary>
    [Fact]
    public void Colliders_set_by_hand_are_not_overwritten() {
        using var session = EditorSession.Start();

        Assert.True(session.Shell.Modes.Activate(TerrainMode.ModeId));
        session.Frames(2);

        var mine = new HandSet();

        Mode(session).Editing.Colliders = mine;
        session.Frames(2);

        Assert.Same(mine, Mode(session).Editing.Colliders);

        Assert.True(session.Plugins.Unload(TerrainPhysicsModule.ModuleId));
        session.Frames(2);

        Assert.Same(mine, Mode(session).Editing.Colliders);
    }

    /// <summary>After an unload nothing the editor holds reaches either module.</summary>
    /// <remarks>
    ///     <c>WaterModuleReachTests</c>' probe and its reason: <c>WaitForCollection</c> answers true for
    ///     a built-in unconditionally (#740), so the instance is what is weakly held.
    /// </remarks>
    [Theory]
    [InlineData(TerrainModule.ModuleId)]
    [InlineData(TerrainPhysicsModule.ModuleId)]
    public void Nothing_the_editor_holds_reaches_the_module_after_an_unload(string id) {
        using var session = EditorSession.Start();

        Assert.True(session.Shell.Modes.Activate(TerrainMode.ModeId));
        Assert.True(session.Plugins.Unload(id));

        var reference = ActivateFresh(session, id);

        session.Frames(2);
        Assert.True(session.Plugins.Unload(id));
        session.Frames(2);

        for (var attempt = 0; attempt < 8 && reference.IsAlive; attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(reference.IsAlive, $"something the editor still holds is pointing at '{id}'.");
    }

    static void AssertPresentOnce(EditorSession session) {
        foreach (var panel in Panels) {
            Assert.Single(session.Shell.Workspace.Panels, descriptor => descriptor.Id == panel);
            Assert.Single(session.Shell.Commands.Commands, command => command.Id == EditorShell.PanelCommand(panel));
        }

        foreach (var mode in Modes) {
            Assert.Single(session.Shell.Modes.Modes, entry => entry.Id == mode);
            Assert.Single(session.Shell.Commands.Commands, command => command.Id == EditorModes.ModeCommand(mode));
        }

        Assert.Single(session.Extensions.All<ITerrainScene>());
        Assert.Single(session.Extensions.All<IVegetationScene>());
        Assert.Single(session.Extensions.All<ITerrainColliders>());
    }

    static TerrainMode Mode(EditorSession session) =>
        (TerrainMode)session.Shell.Modes.Modes.Single(mode => mode.Id == TerrainMode.ModeId);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference ActivateFresh(EditorSession session, string id) {
        IEditorPlugin module = id == TerrainModule.ModuleId ? new TerrainModule() : new TerrainPhysicsModule();
        var name = id == TerrainModule.ModuleId ? TerrainModule.ModuleName : TerrainPhysicsModule.ModuleName;

        Assert.Equal(PluginState.Active, session.Plugins.Activate(id, name, module).State);

        return new WeakReference(module);
    }

    /// <summary>What a host or a test assigns directly, which the binding must not take back.</summary>
    sealed class HandSet : ITerrainColliders {
        public void Rebuild(Vixen.Terrain.Terrain terrain, int tileX, int tileZ) { }
    }
}
