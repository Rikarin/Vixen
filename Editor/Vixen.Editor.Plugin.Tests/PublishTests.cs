// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.Plugin.Tests;

/// <summary>A module's own service, published for other plugins and withdrawn with the module.</summary>
public class PublishTests {
    public interface IThing {
        int Answer();
    }

    sealed class Thing : IThing {
        public int Answer() => 42;
    }

    sealed class Module(Action<PluginContext> activate) : IEditorPlugin {
        public void Activate(PluginContext context) => activate(context);
    }

    [Fact]
    public void A_published_service_is_required_by_type_and_withdrawn_on_unload() {
        using var shell = new EditorShell(1280f, 800f);
        var host = new PluginHost(shell);
        var thing = new Thing();

        host.Activate("mine", "Mine", new Module(context => context.Publish<IThing>(thing)));

        Assert.Same(thing, host.Services.Require<IThing>());

        host.Unload("mine");

        Assert.False(host.Services.Contains<IThing>());
    }

    /// <summary>
    ///     ⚠ <b>The case <c>Services.Add</c> fails</b>: published straight into the bag, the service
    ///     outlived its module, and the reload's second publish of the same type threw — so a module
    ///     with a service of its own could be loaded exactly once.
    /// </summary>
    [Fact]
    public void A_module_that_publishes_can_be_reloaded() {
        using var shell = new EditorShell(1280f, 800f);
        var host = new PluginHost(shell);

        host.Activate("mine", "Mine", new Module(context => context.Publish<IThing>(new Thing())));

        var report = host.Reload("mine");

        Assert.Empty(report.Diagnostics);
        Assert.Equal(PluginState.Active, host.Find("mine")!.State);
        Assert.Equal(42, host.Services.Require<IThing>().Answer());
    }

    /// <summary>Unloading withdraws only what this plugin published, never somebody else's under that type.</summary>
    [Fact]
    public void A_plugin_that_failed_to_publish_does_not_withdraw_the_one_that_did() {
        using var shell = new EditorShell(1280f, 800f);
        var host = new PluginHost(shell);
        var first = new Thing();

        host.Activate("first", "First", new Module(context => context.Publish<IThing>(first)));

        var second = host.Activate("second", "Second", new Module(context => context.Publish<IThing>(new Thing())));

        Assert.Equal(PluginState.Failed, second.State);
        Assert.Same(first, host.Services.Require<IThing>());
    }
}
