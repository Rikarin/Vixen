// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Editor.Testing;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary><see cref="PluginContext.OnFrame" />: a plugin's in-frame work runs when the host's frame does, and stops on unload (#1299).</summary>
/// <remarks>
///     Added for the shader graph's preview renderer, which records and submits a command list of its
///     own and so must run between the host's <c>BeginFrame</c> and its panes rather than from
///     <see cref="PluginContext.OnUpdate" />. ⚠ The editor's own update does not run it: a plugin
///     whose in-frame work ran from there would be the race the hook exists to avoid.
/// </remarks>
public sealed class PluginFrameTests {
    [Fact]
    public void A_plugins_frame_work_runs_in_the_hosts_frame_and_not_after_it_unloads() {
        using var editor = EditorSession.Start();
        using var device = new NullDevice();

        List<IGraphicsDevice> recorded = [];

        editor.Application.PluginHost.Activate("test.framer", "Framer", new Framer(recorded));

        // The editor's update is not the frame.
        editor.Frames(2);
        Assert.Empty(recorded);

        editor.Application.RecordFrame(device);
        Assert.Equal([device], recorded);

        Assert.True(editor.Application.PluginHost.Unload("test.framer"));

        editor.Application.RecordFrame(device);
        Assert.Single(recorded);
    }

    sealed class Framer(List<IGraphicsDevice> recorded) : IEditorPlugin {
        public void Activate(PluginContext context) => context.OnFrame(recorded.Add);
    }
}
