// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
}
