// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Editor.Testing;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>What a plugin holding device resources is told, and when.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/968">#968</a>.</b>
///         <c>IEditorGraphics</c> invites a plugin to <i>hold</i> a device — its own remarks argue
///         for that over a lend-for-one-call contract, because a pipeline cache has to survive
///         across evaluations — and published no way at all of learning that the loan had ended.
///         The device simply started answering differently, by which time the old one had been
///         destroyed with the plugin's pipelines, shader modules and descriptor pools still on it.
///     </para>
///     <para>
///         ⚠ <b>The assertion that matters is the <em>moment</em>, not the call.</b> A notification
///         raised after the application had stopped answering with the going device would satisfy
///         "the plugin was told" and be worth nothing, because the one thing a plugin needs the
///         announcement for is a window in which <c>Destroy</c> is still legal. Every test here that
///         asserts a call also asserts what <c>IEditorGraphics.Device</c> said while the call was
///         running.
///     </para>
/// </remarks>
public class PluginDeviceLossTests {
    /// <summary>⚠ Told, and told while the device is still the answer.</summary>
    [Fact]
    public void A_plugin_is_told_before_the_device_it_holds_stops_being_published() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var graphics = editor.Application.PluginHost.Services.Require<IEditorGraphics>();
        var plugin = new Holder();

        editor.Application.PluginHost.Activate("test.holder", "Holder", plugin);

        using var device = new NullDevice();

        editor.Application.GraphicsDevice = device;

        Assert.Empty(plugin.Released);

        editor.Application.GraphicsDevice = null;

        Assert.Equal([device], plugin.Released);

        // ⚠ The half a bookkeeping assertion cannot see. Raised one line later — after the setter
        // had written the new value through — this would still be a call with the right argument,
        // and the plugin would still have been holding pipelines on a device the editor no longer
        // admits to having.
        Assert.Equal([device], plugin.Answered);
    }

    /// <summary>⚠ A swap is a loss too: the old device goes and nothing else says so.</summary>
    /// <remarks>
    ///     <c>EditorHost</c> answers <c>Suspending</c> with a <c>Release</c> and builds another
    ///     device on the next <c>Present</c>. A plugin told only about a shutdown would go on
    ///     dispatching through pipelines belonging to the first one.
    /// </remarks>
    [Fact]
    public void A_second_device_announces_the_first_and_not_itself() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var plugin = new Holder();

        editor.Application.PluginHost.Activate("test.holder", "Holder", plugin);

        using var first = new NullDevice();
        using var second = new NullDevice();

        editor.Application.GraphicsDevice = first;
        editor.Application.GraphicsDevice = second;

        Assert.Equal([first], plugin.Released);

        editor.Application.GraphicsDevice = null;

        Assert.Equal([first, second], plugin.Released);
    }

    /// <summary>⚠ Acquiring a device is not losing one, and neither is writing the same one twice.</summary>
    /// <remarks>
    ///     The instrument check. A raise that fired on every write would tell a plugin to give back
    ///     resources it is about to need, on the frame the editor's window came up — which reads as
    ///     a working notification and is a preview pane that rebuilds forty-five kernels per assign.
    /// </remarks>
    [Fact]
    public void Gaining_a_device_and_re_publishing_the_same_one_announce_nothing() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var plugin = new Holder();

        editor.Application.PluginHost.Activate("test.holder", "Holder", plugin);

        using var device = new NullDevice();

        editor.Application.GraphicsDevice = null;
        editor.Application.GraphicsDevice = device;
        editor.Application.GraphicsDevice = device;

        Assert.Empty(plugin.Released);
    }

    /// <summary>⚠ An unloaded plugin is not called, which is what makes the hook scoped.</summary>
    /// <remarks>
    ///     A release callback is a delegate over the plugin's own device objects. One surviving its
    ///     unload would be a second release against a device that is still perfectly valid — and,
    ///     worse, a reference into a collectible assembly held by the editor for the session.
    /// </remarks>
    [Fact]
    public void An_unloaded_plugin_is_not_told() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var plugin = new Holder();

        editor.Application.PluginHost.Activate("test.holder", "Holder", plugin);

        using var device = new NullDevice();

        editor.Application.GraphicsDevice = device;

        Assert.True(editor.Application.PluginHost.Unload("test.holder"));

        editor.Application.GraphicsDevice = null;

        Assert.Empty(plugin.Released);
    }

    /// <summary>⚠ One plugin's failure does not cost the next one its release window.</summary>
    /// <remarks>
    ///     The device is destroyed whatever happens, so a host that stopped at the first throw would
    ///     turn one plugin's leak into every later plugin's leak — and the ordering that decides who
    ///     leaks is activation order, which nobody chose.
    /// </remarks>
    [Fact]
    public void A_plugin_that_throws_is_reported_and_the_next_is_still_told() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var thrower = new Thrower();
        var plugin = new Holder();

        editor.Application.PluginHost.Activate("test.thrower", "Thrower", thrower);
        editor.Application.PluginHost.Activate("test.holder", "Holder", plugin);

        using var device = new NullDevice();

        editor.Application.GraphicsDevice = device;
        editor.Application.GraphicsDevice = null;

        Assert.Equal([device], plugin.Released);

        Assert.Contains(
            editor.Application.PluginHost.Diagnostics,
            diagnostic => diagnostic.PluginId == "test.thrower"
                && diagnostic.Severity == PluginSeverity.Error
                && diagnostic.Message.Contains("device resources", StringComparison.Ordinal)
        );
    }

    /// <summary>⚠ And the path a host that never nulls the device takes on the way down.</summary>
    /// <remarks>
    ///     <c>EditorApplication.Dispose</c> unloads every plugin, and a plugin unloaded with a live
    ///     device still published would have been given no release window at all — which is #968
    ///     with the announcement in place and skipped. A test host is exactly such a host, and
    ///     <c>DisposeFrames</c>' own remarks say a real one that forgets is not hypothetical.
    /// </remarks>
    [Fact]
    public void Shutting_down_with_a_device_still_published_announces_it_before_unloading() {
        var editor = EditorSession.Start();
        var plugin = new Holder();

        using var device = new NullDevice();

        try {
            editor.Open("project");
            editor.Application.PluginHost.Activate("test.holder", "Holder", plugin);

            editor.Application.GraphicsDevice = device;
        } finally {
            editor.Dispose();
        }

        Assert.Equal([device], plugin.Released);
        Assert.Equal([device], plugin.Answered);
    }

    /// <summary>A plugin that takes the device up on the offer to hold it, and records the loan ending.</summary>
    sealed class Holder : IEditorPlugin {
        readonly List<IGraphicsDevice> released = [];
        readonly List<IGraphicsDevice?> answered = [];

        IEditorGraphics? graphics;

        /// <summary>The devices this plugin was told about, in order.</summary>
        public IReadOnlyList<IGraphicsDevice> Released => released;

        /// <summary>
        ///     What <c>IEditorGraphics.Device</c> answered <em>during</em> each of those calls, which
        ///     is the assertion a call count cannot make.
        /// </summary>
        public IReadOnlyList<IGraphicsDevice?> Answered => answered;

        /// <inheritdoc />
        public void Activate(PluginContext context) {
            graphics = context.Services.Require<IEditorGraphics>();

            context.OnDeviceLost(
                device => {
                    released.Add(device);
                    answered.Add(graphics.Device);
                }
            );
        }
    }

    /// <summary>A plugin whose release path is broken, which is the case the host has to survive.</summary>
    sealed class Thrower : IEditorPlugin {
        /// <inheritdoc />
        public void Activate(PluginContext context) =>
            context.OnDeviceLost(static _ => throw new InvalidOperationException("no"));
    }
}
