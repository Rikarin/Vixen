// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.Plugin.Tests;

/// <summary>Doc 20's E3 exit: a plugin switched off, switched back on, and reloaded.</summary>
/// <remarks>
///     ⚠ <b>Two switches, and telling them apart is the point.</b> <c>plugin.yaml</c>'s
///     <c>enabled:</c> is the author's and lives in the plugin's own directory — which for a plugin
///     checked into a repository is a file the whole team shares. <see cref="PluginHost.Suppress" />
///     is the user's, kept beside their layout and their keymap. Either alone keeps a plugin out,
///     and only the second can be undone from the manager.
/// </remarks>
public class SwitchingTests {
    const string Hello = """
                         using Vixen.Editor.Plugin;
                         using Vixen.Editor.Ui;

                         namespace Sample;

                         public sealed class Entry : IEditorPlugin {
                             public void Activate(PluginContext context) {
                                 context.AddCommand("sample.hello", new StringId("sample.hello", "Hello"), () => { });
                             }
                         }
                         """;

    [Fact]
    public void A_plugin_the_user_switched_off_is_never_activated() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write("sample", Hello);

        var host = new PluginHost(shell);

        // ⚠ Before the load rather than unloading afterwards, and the difference is the whole reason
        // this is a set the host consults. A plugin somebody switched off because it broke the
        // editor is exactly the one whose Activate must not run.
        host.Suppress(["sample"]);
        host.Load(PluginDiscovery.Scan(folder.Root));

        Assert.Equal(PluginState.Disabled, host.Find("sample")!.State);
        Assert.False(shell.Commands.TryGet("sample.hello", out _));
        Assert.True(host.IsSuppressed("sample"));
    }

    [Fact]
    public void Disabling_a_running_plugin_takes_its_registrations_back_out() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write("sample", Hello);

        var host = new PluginHost(shell);
        host.Load(PluginDiscovery.Scan(folder.Root));

        Assert.True(shell.Commands.TryGet("sample.hello", out _));

        Assert.True(host.Disable("sample"));

        Assert.False(shell.Commands.TryGet("sample.hello", out _));
        Assert.Contains("sample", host.Suppressed);
    }

    [Fact]
    public void Enabling_it_again_starts_it_from_disk() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write("sample", Hello);

        var host = new PluginHost(shell);
        host.Load(PluginDiscovery.Scan(folder.Root));
        host.Disable("sample");

        var report = host.Enable("sample");

        Assert.False(report.HasErrors);
        Assert.Equal(PluginState.Active, host.Find("sample")!.State);
        Assert.True(shell.Commands.TryGet("sample.hello", out _));
        Assert.DoesNotContain("sample", host.Suppressed);
    }

    /// <summary>
    ///     ⚠ Enabling goes through <see cref="PluginHost.Reload" />, so it picks up a rebuild. A
    ///     plugin somebody is switching back on <i>because they have just fixed it</i> is the
    ///     ordinary case, and reusing a descriptor read at start-up would load the copy that did not
    ///     work.
    /// </summary>
    [Fact]
    public void Enabling_it_reads_the_assembly_again_rather_than_the_one_loaded_at_start_up() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write("sample", Hello);

        var host = new PluginHost(shell);
        host.Load(PluginDiscovery.Scan(folder.Root));
        host.Disable("sample");

        folder.Rebuild(
            "sample",
            Hello.Replace("sample.hello", "sample.goodbye", StringComparison.Ordinal)
        );

        host.Enable("sample");

        Assert.True(shell.Commands.TryGet("sample.goodbye", out _));
        Assert.False(shell.Commands.TryGet("sample.hello", out _));
    }

    [Fact]
    public void The_authors_own_switch_is_not_the_users_and_is_reported_separately() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write(
            "sample",
            Hello,
            $"""
             id: sample
             name: sample
             version: 1.0.0
             api: {EditorApi.Version.ToString(2)}
             assembly: sample.dll
             enabled: false

             """
        );

        var host = new PluginHost(shell);
        host.Load(PluginDiscovery.Scan(folder.Root));

        Assert.Equal(PluginState.Disabled, host.Find("sample")!.State);

        // Switched off, but not by this user — so the manager says so rather than offering an
        // Enable that would be overruled by a file the whole team shares.
        Assert.True(host.IsSuppressed("sample"));
        Assert.DoesNotContain("sample", host.Suppressed);
    }
}
