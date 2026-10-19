// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Editor.Water;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Water;
using Vixen.Water;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The water toolset, reached by effect, through the front door — and out of it again (#1286).</summary>
/// <remarks>
///     <para>
///         <b>Every assertion is about what the shell has, never about what the module called.</b> A
///         module that registered through <c>Shell.*</c> and one that registered through its
///         <c>PluginContext</c> look identical in a session that never unloads anything; the
///         difference is entirely in what is left after an unload and what a reload does.
///     </para>
///     <para>
///         ⚠ <b>Against the tree before #1286 every test here is red, each for its own reason</b>: the
///         panels and the mode outlived the unload, the reload threw "already registered" and failed
///         the module, the mode's <c>Drawn</c> handler kept the module reachable, and the debug
///         switches were <c>WaterDebug</c>'s statics so a second session saw the first one's tick.
///     </para>
/// </remarks>
public sealed class WaterModuleReachTests {
    /// <summary>The two panels' ids, which is what a View-menu line and a layout name.</summary>
    static readonly string[] Panels = [WaterModule.ZonePanel, WaterModule.BodyPanel];

    /// <summary>A verb from each of the three places the module registers one.</summary>
    static readonly string[] Verbs = [
        WaterModule.ShowFlowCommand,
        WaterModule.CarveTerrainCommand,
        WaterMode.CreateZoneCommand,
        EditorModes.ModeCommand(WaterMode.ModeId)
    ];

    /// <summary>A session opened the way the host opens one has every piece of the toolset.</summary>
    [Fact]
    public void A_session_has_the_water_panels_mode_and_verbs() {
        using var session = EditorSession.Start();

        AssertPresentOnce(session);
    }

    /// <summary>Unloading the module takes every piece back out, and activating it again puts each back once.</summary>
    /// <remarks>
    ///     ⚠ <b>A fresh instance on the way back</b>, which is what a disk plugin's reload is and the
    ///     harder of the two: nothing of the first activation is still holding a panel id for it.
    ///     <see cref="Reloading_puts_everything_back_exactly_once" /> is the same-instance path.
    /// </remarks>
    [Fact]
    public void Unloading_takes_everything_out_and_activating_again_puts_it_back_once() {
        using var session = EditorSession.Start();

        Assert.True(session.Plugins.Unload(WaterModule.ModuleId));
        session.Settle();

        foreach (var panel in Panels) {
            Assert.DoesNotContain(session.Shell.Workspace.Panels, descriptor => descriptor.Id == panel);
            Assert.Null(session.Shell.Commands[EditorShell.PanelCommand(panel)]);
        }

        foreach (var verb in Verbs) {
            Assert.Null(session.Shell.Commands[verb]);
        }

        Assert.False(session.Shell.Modes.TryGet(WaterMode.ModeId, out _));
        Assert.Empty(session.Extensions.All<WaterDebugFlags>());

        var again = session.Plugins.Activate(WaterModule.ModuleId, WaterModule.ModuleName, new WaterModule());

        Assert.Equal(PluginState.Active, again.State);
        session.Settle();

        AssertPresentOnce(session);
    }

    /// <summary>The plugin manager's Reload re-activates the same module, and nothing doubles.</summary>
    [Fact]
    public void Reloading_puts_everything_back_exactly_once() {
        using var session = EditorSession.Start();

        var report = session.Plugins.Reload(WaterModule.ModuleId);

        Assert.Empty(report.Diagnostics);
        Assert.Equal(PluginState.Active, session.Plugins.Find(WaterModule.ModuleId)!.State);
        session.Settle();

        AssertPresentOnce(session);
    }

    /// <summary>After an unload nothing the editor still holds reaches the module.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A <see cref="WeakReference" />, because <c>WaitForCollection</c> cannot see this.</b>
    ///         A built-in has no load context, so <c>PluginHost.WaitForCollection</c> answers true for
    ///         it unconditionally (#740). The module instance is the honest probe — the shape
    ///         <c>TexturingModuleTests</c> uses — and a single registration left behind turns it red.
    ///     </para>
    ///     <para>
    ///         The session's own instance is unloaded first and a fresh one activated in a method of
    ///         its own, so no local in this frame roots it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Nothing_the_editor_holds_reaches_the_module_after_an_unload() {
        using var session = EditorSession.Start();

        Assert.True(session.Plugins.Unload(WaterModule.ModuleId));

        var reference = ActivateFresh(session);

        session.Settle();
        Assert.True(session.Plugins.Unload(WaterModule.ModuleId));
        session.Settle();

        for (var attempt = 0; attempt < 8 && reference.IsAlive; attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(reference.IsAlive, "something the editor still holds is pointing at the water module.");
    }

    /// <summary>Two editors in one process tick water's debug verbs independently.</summary>
    /// <remarks>
    ///     ⚠ <b>Read through each session's own command and its own registry</b>, which are the two
    ///     things a person and a viewport respectively read. Before #1286 both answered from
    ///     <c>WaterDebug</c>'s statics, so ticking Show Flow in one editor ticked it in the other.
    /// </remarks>
    [Fact]
    public void Two_sessions_toggle_water_debug_independently() {
        using var first = EditorSession.Start();
        using var second = EditorSession.Start();

        try {
            first.Run(WaterModule.ShowFlowCommand);

            Assert.True(Checked(first, WaterModule.ShowFlowCommand));
            Assert.False(Checked(second, WaterModule.ShowFlowCommand));

            Assert.True(Assert.Single(first.Extensions.All<WaterDebugFlags>()).ShowFlow);
            Assert.False(Assert.Single(second.Extensions.All<WaterDebugFlags>()).ShowFlow);

            // And the process's set, which is a game console's, is not what either of them flipped.
            Assert.False(WaterDebug.ShowFlow);

            second.Run(WaterModule.ShowFlowCommand);
            first.Run(WaterModule.ShowFlowCommand);

            Assert.False(Checked(first, WaterModule.ShowFlowCommand));
            Assert.True(Checked(second, WaterModule.ShowFlowCommand));
        } finally {
            // ⚠ The static, in case this ran against a tree where the verbs still flip it — a leaked
            // `true` would decide what every water test the runner picks next draws.
            WaterDebug.Reset();
        }
    }

    /// <summary>The viewport draws the switches its own editor contributed, and not another's.</summary>
    /// <remarks>
    ///     ⚠ <b>The presenter half, on a recording device.</b> The module owning its flags proves
    ///     nothing if the pane still reads the statics — that is the "finished thing nothing calls"
    ///     this repository keeps shipping — so the arrows are counted where the pane collects them.
    /// </remarks>
    [Fact]
    public void The_pane_draws_its_own_editors_switches() {
        using var device = new NullDevice(new() { Record = true });
        using var presenter = Presenter(device);
        using var world = River();

        var mine = new EditorRegistry();
        var flags = new WaterDebugFlags();

        mine.Add(flags);

        presenter.WaterScene = new StraightRiver();
        presenter.Extensions = mine;

        try {
            // Somebody else's editor — or a game console — has the process's set on.
            WaterDebug.ShowFlow = true;

            presenter.Water.Upload(world, new EditorCamera(), 1f);

            Assert.Empty(presenter.Water.DebugLines);

            WaterDebug.Reset();
            flags.ShowFlow = true;

            presenter.Water.Upload(world, new EditorCamera(), 1f);

            Assert.NotEmpty(presenter.Water.DebugLines);
        } finally {
            WaterDebug.Reset();
        }
    }

    /// <summary>The six debug switches, in the order Scene ▸ Water ▸ Debug lists them.</summary>
    static readonly string[] Switches = [
        WaterModule.ShowTilesCommand,
        WaterModule.ShowLodCommand,
        WaterModule.ShowInfoCommand,
        WaterModule.ShowFlowCommand,
        WaterModule.ShowBuoyancyCommand,
        WaterModule.ShowRipplesCommand
    ];

    /// <summary>
    ///     Scene ▸ Water ▸ Debug holds the six switches once each, after Blockout's submenus, and a line
    ///     ticks this session's flags (#1286).
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Red before the declarations</b>: the switches were the command palette's alone, and a
    ///     remark claiming "the order the mode's menu lists them" had been false since it was written.
    ///     Each line is run as the menu runs it — by its command id — and the check it draws is the
    ///     command's <c>Checked</c>, so the assertion is on the same two things the presenter reads.
    /// </remarks>
    [Fact]
    public void The_scene_menu_has_the_six_water_debug_switches_once_each_and_they_tick_this_sessions_flags() {
        using var session = EditorSession.Start();

        var debug = DebugMenu(session);

        Assert.NotNull(debug);
        Assert.Equal(Switches, debug.Entries.OfType<MenuCommand>().Select(line => line.CommandId).ToArray());

        var flags = Assert.Single(session.Extensions.All<WaterDebugFlags>());

        try {
            foreach (var id in Switches) {
                Assert.False(Checked(session, id), $"'{id}' is ticked before anybody ticked it.");

                session.Run(id);

                Assert.True(Checked(session, id), $"'{id}' did not tick.");
            }

            Assert.True(flags is { ShowTiles: true, ShowLod: true, ShowInfo: true, ShowFlow: true, ShowBuoyancy: true, ShowRipples: true });

            session.Run(WaterModule.ShowFlowCommand);

            Assert.False(flags.ShowFlow);
            Assert.False(Checked(session, WaterModule.ShowFlowCommand));
        } finally {
            WaterDebug.Reset();
        }

        // Beside Blockout's submenus, which is the Scene menu's mode-tool run, and not before them.
        var scene = Assert.Single(session.Shell.Menus.Menus, menu => menu.Title.Id == "editor.menu.scene");
        var submenus = scene.Entries.OfType<MenuSubmenu>().Select(entry => entry.Group.Title.Id).ToList();

        Assert.Equal(submenus.IndexOf("editor.menu.blockout-handoff") + 1, submenus.IndexOf(WaterStrings.MenuWater.Id));
    }

    /// <summary>The Water ▸ Debug submenu leaves with the module, and comes back once with a reload.</summary>
    [Fact]
    public void The_water_debug_submenu_leaves_on_unload_and_comes_back_once() {
        using var session = EditorSession.Start();

        Assert.True(session.Plugins.Unload(WaterModule.ModuleId));
        session.Settle();

        var scene = Assert.Single(session.Shell.Menus.Menus, menu => menu.Title.Id == "editor.menu.scene");

        Assert.DoesNotContain(scene.Entries.OfType<MenuSubmenu>(), entry => entry.Group.Title.Id == WaterStrings.MenuWater.Id);
        Assert.Null(DebugMenu(session));

        Assert.Equal(
            PluginState.Active,
            session.Plugins.Activate(WaterModule.ModuleId, WaterModule.ModuleName, new WaterModule()).State
        );
        session.Settle();

        Assert.Single(scene.Entries.OfType<MenuSubmenu>(), entry => entry.Group.Title.Id == WaterStrings.MenuWater.Id);
        Assert.Equal(Switches, DebugMenu(session)!.Entries.OfType<MenuCommand>().Select(line => line.CommandId).ToArray());

        Assert.Empty(session.Plugins.Reload(WaterModule.ModuleId).Diagnostics);
        session.Settle();

        Assert.Single(scene.Entries.OfType<MenuSubmenu>(), entry => entry.Group.Title.Id == WaterStrings.MenuWater.Id);
        Assert.Equal(Switches, DebugMenu(session)!.Entries.OfType<MenuCommand>().Select(line => line.CommandId).ToArray());
    }

    /// <summary>Scene ▸ Water ▸ Debug, or <see langword="null" /> when the bar has none.</summary>
    static MenuGroup? DebugMenu(EditorSession session) {
        var scene = session.Shell.Menus.Menus.SingleOrDefault(menu => menu.Title.Id == "editor.menu.scene");
        var water = scene?.Entries.OfType<MenuSubmenu>().SingleOrDefault(entry => entry.Group.Title.Id == WaterStrings.MenuWater.Id);

        return water?.Group.Entries.OfType<MenuSubmenu>()
            .SingleOrDefault(entry => entry.Group.Title.Id == WaterStrings.MenuWaterDebug.Id)
            ?.Group;
    }

    static void AssertPresentOnce(EditorSession session) {
        foreach (var panel in Panels) {
            Assert.Single(session.Shell.Workspace.Panels, descriptor => descriptor.Id == panel);
            Assert.NotNull(session.Shell.Commands[EditorShell.PanelCommand(panel)]);
        }

        foreach (var verb in Verbs) {
            Assert.Single(session.Shell.Commands.Commands, command => command.Id == verb);
        }

        Assert.Single(session.Shell.Modes.Modes, mode => mode.Id == WaterMode.ModeId);
        Assert.Single(session.Extensions.All<WaterDebugFlags>());
        Assert.Single(session.Extensions.All<IWaterScene>());
    }

    static bool Checked(EditorSession session, string command) =>
        session.Shell.Commands[command] is { Checked: { } read } && read();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference ActivateFresh(EditorSession session) {
        var module = new WaterModule();

        Assert.Equal(
            PluginState.Active,
            session.Plugins.Activate(WaterModule.ModuleId, WaterModule.ModuleName, module).State
        );

        return new WeakReference(module);
    }

    static ScenePresenter Presenter(NullDevice device) {
        ShaderHandle Stage(ShaderStage stage, string name) => device.CreateShader(stage, [1, 2, 3, 4], name);

        return new ScenePresenter(
            device,
            new LineShaders(Stage(ShaderStage.Vertex, "line.vs"), Stage(ShaderStage.Fragment, "line.fs")),
            new MeshShaders(Stage(ShaderStage.Vertex, "mesh.vs"), Stage(ShaderStage.Fragment, "mesh.fs")),
            new MeshInstanceShaders(Stage(ShaderStage.Vertex, "inst.vs"), Stage(ShaderStage.Fragment, "inst.fs")),
            PixelFormat.Rgba8UNorm
        );
    }

    /// <summary>A zone at the origin with one flowing river through it, which is what Show Flow draws.</summary>
    /// <remarks><c>WaterDebugDrawTests.Folded</c>'s scene, so a draw that works there works here.</remarks>
    static World River() {
        var world = new World();
        var zone = world.Create();

        world.Add(zone, WaterZoneComponent.Default with { Extent = 128f, Resolution = 65, Waves = WaterWaveSpectrum.Calm });
        world.Add(zone, new Vixen.Engine.Transforms.WorldTransform { Value = Matrix4x4.Identity });

        var body = world.Create();

        world.Add(
            body,
            WaterBodyComponent.Default with {
                Kind = WaterBodyKind.River,
                Spline = "River",
                SurfaceHeight = 2f,
                HalfWidth = 12f,
                Velocity = 3f
            }
        );

        world.Add(body, new Vixen.Engine.Transforms.WorldTransform { Value = Matrix4x4.Identity });

        return world;
    }

    /// <summary>What a water module answers: one straight river, no sea states, flat ground below.</summary>
    sealed class StraightRiver : IWaterScene {
        public Spline? SplineFor(string name, in Matrix4x4 placement) =>
            name.Length == 0
                ? null
                : new(
                    Spline.SmoothTangents(
                        [new(-50f, 2f, 0f), new(0f, 2f, 0f), new(50f, 2f, 0f)],
                        closed: false
                    ),
                    closed: false
                );

        public WaterWaveSpectrum? SpectrumFor(string name) => null;

        public float GroundAt(Vector2 ground) => -5f;
    }
}
