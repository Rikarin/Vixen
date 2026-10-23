// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Diagnostics;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The transport as a module over <see cref="IPlaySession" /> (doc 50 § P2a, #1289), asserted by effect.</summary>
public class PlayModuleTests {
    /// <summary>
    ///     An editor opened the way the host opens one has the module active, the service published,
    ///     and F5's command reaching the service — the same object a plugin gets.
    /// </summary>
    [Fact]
    public void The_transport_is_a_module_over_the_published_service() {
        using var editor = EditorSession.Start();

        Assert.Equal(PluginState.Active, editor.Plugins.Find(PlayModule.ModuleId)!.State);

        var play = editor.Plugins.Services.Require<IPlaySession>();

        Assert.Same(play, editor.Plugins.Services.Require<IActivePlaySession>());

        editor.Run("play.play");

        Assert.Equal(PlayState.Playing, play.State);
        Assert.NotNull(play.Current);

        editor.Run("play.pause");
        Assert.Equal(PlayState.Paused, play.State);

        editor.Run("play.stop");
        Assert.Equal(PlayState.Editing, play.State);
    }

    /// <summary>
    ///     ⚠ The module is what registers the four commands, so switching it off takes them out — the
    ///     sabotage that shows the module is not decoration over a second registration somewhere.
    /// </summary>
    [Fact]
    public void Switching_the_module_off_takes_the_transport_with_it() {
        using var editor = EditorSession.Start();

        Assert.NotNull(editor.Shell.Commands["play.play"]);

        editor.Plugins.Disable(PlayModule.ModuleId);

        Assert.Null(editor.Shell.Commands["play.play"]);
        Assert.Null(editor.Shell.Commands["play.stop"]);

        editor.Plugins.Enable(PlayModule.ModuleId);

        Assert.NotNull(editor.Shell.Commands["play.play"]);
    }

    /// <summary>
    ///     What entering play says — the discard warning and the log line naming what the session runs —
    ///     is the module's (#1296): said on a plugin's <c>Enter</c> as on F5, and not said once it is off.
    /// </summary>
    [Fact]
    public void Entering_play_is_announced_by_the_module() {
        using var editor = EditorSession.Start();

        var play = editor.Plugins.Services.Require<IPlaySession>();
        var log = editor.Plugins.Services.Require<RingBufferSink>();
        var session = (EditorPlaySession) play;

        int Said() => editor.Shell.Notifications.History.Count(static note => note.Message == "Play mode");

        // What the running session describes, asked while it runs: the passes differ once the module is off.
        int Logged() => log.Snapshot().Count(record => record.Message == session.DescribeRunning());

        Assert.True(play.Enter());
        Assert.Equal(1, Said());
        Assert.Equal(1, Logged());
        play.Leave();

        editor.Plugins.Disable(PlayModule.ModuleId);

        Assert.True(play.Enter());
        Assert.Equal(1, Said());
        Assert.Equal(0, Logged());
        play.Leave();
    }

    /// <summary>
    ///     The editor's passes for a session — diagnostics, physics, animation, agents, keyboard — are
    ///     the module's contributions (#1293), so they are withdrawn with it and come back with it, in
    ///     the order a contribution with no declared order keeps.
    /// </summary>
    [Fact]
    public void The_sessions_passes_are_the_modules_contributions() {
        using var editor = EditorSession.Start();

        var registry = editor.Plugins.Services.Require<IEditorRegistry>();

        string[] Passes() => [.. registry.All<IPlaySystems>().Select(static systems => systems.GetType().Name)];

        string[] expected = ["PlayDiagnostics", "PlayPhysics", "PlayAnimation", "PlayAi", "PlayInput"];

        Assert.Equal(expected, Passes().Where(expected.Contains));

        editor.Plugins.Disable(PlayModule.ModuleId);
        Assert.DoesNotContain(Passes(), expected.Contains);

        editor.Plugins.Enable(PlayModule.ModuleId);
        Assert.Equal(expected, Passes().Where(expected.Contains));
    }
}
