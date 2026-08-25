// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.Plugin.Tests;

/// <summary>A plugin compiled, loaded, run, unloaded and rebuilt, against a real shell.</summary>
/// <remarks>
///     The shell is constructible with no platform, device or window underneath it — see
///     <c>Vixen.Editor.Ui</c> — which is what lets the whole of plugin loading be tested against the
///     registries a plugin actually writes to rather than against a stand-in for them.
/// </remarks>
public class LoadingTests {
    const string Hello = """
                         using Vixen.Editor.Plugin;
                         using Vixen.Editor.Ui;
                         using Vixen.Ui;

                         namespace Sample;

                         public sealed class Entry : IEditorPlugin {
                             public void Activate(PluginContext context) {
                                 context.AddCommand("sample.hello", new StringId("sample.hello", "Hello"), () => { });
                             }
                         }
                         """;

    static PluginReport LoadFrom(PluginHost host, PluginFolder folder) => host.Load(PluginDiscovery.Scan(folder.Root));

    [Fact]
    public void A_plugin_registers_a_command_that_is_reachable_from_everything_that_shows_one() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write("sample", Hello);

        var host = new PluginHost(shell);
        var report = LoadFrom(host, folder);

        Assert.False(report.HasErrors);
        Assert.Single(report.Activated);
        Assert.Equal(PluginState.Active, host.Find("sample")!.State);

        // One Add call, and it is in the registry the menu, the toolbar, the palette and the
        // keymap are all views over. That is the whole architecture, from a plugin's side.
        Assert.True(shell.Commands.TryGet("sample.hello", out var command));
        Assert.Equal("Hello", command.Title.Source);
    }

    [Fact]
    public void A_plugin_shares_the_hosts_contract_types_rather_than_loading_its_own() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        // The plugin is compiled against this process's assemblies and its folder has no copy of
        // them, but the rule under test is the one that matters when it does: an IEditorPlugin from
        // a second copy of Vixen.Editor.Plugin.dll is a different type with the same name, and the
        // cast in the loader would fail with a message that reads like a lie.
        folder.Write("sample", Hello);
        File.Copy(typeof(IEditorPlugin).Assembly.Location, Path.Combine(folder.Root, "sample", "Vixen.Editor.Plugin.dll"));

        var host = new PluginHost(shell);
        var report = LoadFrom(host, folder);

        Assert.False(report.HasErrors);
        Assert.Single(report.Activated);
    }

    [Fact]
    public void Unloading_takes_back_everything_the_plugin_registered() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write(
            "sample",
            """
            using Vixen.Editor.Plugin;
            using Vixen.Editor.Ui;
            using Vixen.Ui;

            namespace Sample;

            public sealed class Entry : IEditorPlugin {
                public void Activate(PluginContext context) {
                    context.AddCommand("sample.hello", new StringId("sample.hello", "Hello"), () => { });
                    context.AddPanel("sample.panel", new StringId("sample.panel", "Sample"), panel => panel.Text = "hi");
                    context.AddMenu(new StringId("sample.menu", "Sample")).Add("sample.hello");
                }
            }
            """
        );

        var host = new PluginHost(shell);
        LoadFrom(host, folder);

        shell.Workspace.Open("sample.panel");

        Assert.True(shell.Workspace.IsOpen("sample.panel"));
        Assert.Contains(shell.Menus.Menus, menu => menu.Title.Id == "sample.menu");

        Assert.True(host.Unload("sample"));

        // Every one of these is a reference into the plugin's assembly. A command left behind is
        // not one stale menu line, it is a load context that can never be collected.
        Assert.False(shell.Commands.TryGet("sample.hello", out _));
        Assert.False(shell.Commands.TryGet(EditorShell.PanelCommand("sample.panel"), out _));
        Assert.False(shell.Workspace.IsOpen("sample.panel"));
        Assert.DoesNotContain(shell.Workspace.Panels, panel => panel.Id == "sample.panel");
        Assert.DoesNotContain(shell.Menus.Menus, menu => menu.Title.Id == "sample.menu");

        Assert.Equal(PluginState.Unloaded, host.Find("sample")!.State);
    }

    [Fact]
    public void A_plugins_mode_is_entered_and_is_left_again_when_the_plugin_goes() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        shell.Modes.Add(new SelectMode());

        folder.Write(
            "sample",
            """
            using System;
            using System.Collections.Generic;
            using Vixen.Editor.Plugin;
            using Vixen.Editor.Ui;
            using Vixen.Ui;

            namespace Sample;

            public sealed class Sculpt : IEditorMode {
                public string Id => "sample.sculpt";
                public StringId Title { get; } = new StringId("sample.mode", "Sculpt");
                public PathBuilder? Icon => null;
                public string? Context => "sample.sculpt";
                public string? Panel => null;
                public IReadOnlyList<ToolbarEntry> Toolbar => Array.Empty<ToolbarEntry>();

                public void Register(EditorShell shell) =>
                    shell.Commands.Add(new EditorCommand("sample.brush", new StringId("sample.brush", "Brush"), () => { }) {
                        Context = "sample.sculpt"
                    });

                public void Unregister(EditorShell shell) => shell.Commands.Remove("sample.brush");

                public void Activated() { }
                public void Deactivated() { }
                public bool Pointer(PointerEvent args) => false;
                public bool Key(KeyEvent args) => false;
            }

            public sealed class Entry : IEditorPlugin {
                public void Activate(PluginContext context) => context.AddMode(new Sculpt());
            }
            """
        );

        var host = new PluginHost(shell);
        LoadFrom(host, folder);

        Assert.True(shell.Modes.Activate("sample.sculpt"));
        Assert.Equal("sample.sculpt", shell.Modes.Context);

        Assert.True(host.Unload("sample"));

        // ⚠ Both halves. The viewport must not still mean a mode whose assembly has gone, and the
        // mode's own commands are lambdas over the plugin's types — one left behind is not a stale
        // palette entry, it is a load context that can never be collected.
        Assert.Equal(SelectMode.ModeId, shell.Modes.Active?.Id);
        Assert.DoesNotContain(shell.Modes.Modes, mode => mode.Id == "sample.sculpt");
        Assert.False(shell.Commands.TryGet("sample.brush", out _));
        Assert.False(shell.Commands.TryGet(EditorModes.ModeCommand("sample.sculpt"), out _));
    }

    [Fact]
    public void An_unloaded_plugins_assembly_actually_leaves_memory() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write("sample", Hello);

        var host = new PluginHost(shell);
        LoadFrom(host, folder);

        Assert.True(host.Unload("sample"));

        // ⚠ The claim the whole design rests on, and the one the runtime reports nothing about. A
        // context that cannot be collected unloads on paper, stays in memory in fact, and is not
        // noticed until the same plugin is loaded a second time and its static state is not what it
        // was.
        Assert.True(host.WaitForCollection("sample", TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Reloading_picks_up_a_rebuild_of_the_same_file() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write("sample", Hello);

        var host = new PluginHost(shell);
        LoadFrom(host, folder);

        Assert.Equal("Hello", shell.Commands["sample.hello"]!.Title.Source);

        folder.Rebuild("sample", Hello.Replace("Hello", "Goodbye", StringComparison.Ordinal));

        // The plugin-development loop: build over the folder the editor is watching, reload, see
        // the change — with the project, the scene, the layout and the undo history still open.
        // The assembly is read into memory rather than mapped precisely so this rewrite can happen.
        var report = host.Reload("sample");

        Assert.False(report.HasErrors);
        Assert.Equal("Goodbye", shell.Commands["sample.hello"]!.Title.Source);
    }

    [Fact]
    public void A_plugin_that_throws_while_activating_leaves_nothing_behind() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write(
            "sample",
            """
            using System;
            using Vixen.Editor.Plugin;
            using Vixen.Editor.Ui;
            using Vixen.Ui;

            namespace Sample;

            public sealed class Entry : IEditorPlugin {
                public void Activate(PluginContext context) {
                    context.AddCommand("sample.first", new StringId("sample.first", "First"), () => { });
                    throw new InvalidOperationException("no");
                }
            }
            """
        );

        var host = new PluginHost(shell);
        var report = LoadFrom(host, folder);

        // Half a plugin, permanently, is the alternative — plus a load context nothing can ever
        // collect, because the registry is still holding a lambda over the plugin's own state.
        Assert.True(report.HasErrors);
        Assert.Single(report.Failed);
        Assert.False(shell.Commands.TryGet("sample.first", out _));
        Assert.Equal(PluginState.Failed, host.Find("sample")!.State);
        Assert.IsType<InvalidOperationException>(host.Find("sample")!.Failure);
    }

    [Fact]
    public void A_plugin_naming_a_command_somebody_already_owns_is_refused_and_the_owner_keeps_it() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        shell.Commands.Add("sample.hello", new StringId("editor.hello", "The editor's own"), () => { });
        folder.Write("sample", Hello);

        var host = new PluginHost(shell);
        var report = LoadFrom(host, folder);

        // CommandRegistry refuses a duplicate rather than replacing it, so a plugin cannot take
        // over `file.save` by naming it. What this checks is that the refusal reaches the report
        // with the id in it instead of escaping as an exception.
        Assert.True(report.HasErrors);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Message.Contains("sample.hello", StringComparison.Ordinal));
        Assert.Equal("The editor's own", shell.Commands["sample.hello"]!.Title.Source);
    }

    [Fact]
    public void A_plugin_built_against_another_contract_version_never_runs() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write("sample", Hello, manifest: "id: sample\nname: Sample\napi: 9.9\nassembly: sample.dll\n");

        var host = new PluginHost(shell);
        var report = LoadFrom(host, folder);

        // Refused from the manifest, before a byte of its IL is mapped into this process. The
        // alternative is a MissingMethodException from inside somebody else's code on a machine
        // that is not yours.
        Assert.True(report.HasErrors);
        Assert.DoesNotContain(shell.Commands.Commands, command => command.Id.StartsWith("sample.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_disabled_plugin_is_not_loaded_and_is_not_an_error() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write("sample", Hello, manifest: "id: sample\nname: Sample\napi: 0.1\nassembly: sample.dll\nenabled: false\n");

        var host = new PluginHost(shell);
        var report = LoadFrom(host, folder);

        Assert.False(report.HasErrors);
        Assert.Empty(report.Activated);
        Assert.Equal(PluginState.Disabled, host.Find("sample")!.State);
    }

    [Fact]
    public void Two_entry_points_and_no_manifest_line_is_a_refusal_rather_than_a_guess() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        const string Two = """
                           using Vixen.Editor.Plugin;

                           namespace Sample;

                           public sealed class First : IEditorPlugin { public void Activate(PluginContext context) { } }
                           public sealed class Second : IEditorPlugin { public void Activate(PluginContext context) { } }
                           """;

        folder.Write("sample", Two);

        var host = new PluginHost(shell);
        var report = LoadFrom(host, folder);

        // Which of two plugins in one file ran is not something anybody should discover by
        // experiment, and the fix is one line in a file the author already has open.
        Assert.True(report.HasErrors);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Message.Contains("Sample.First, Sample.Second", StringComparison.Ordinal));

        // And that line is all it takes.
        using var named = new PluginFolder();
        using var second = new EditorShell(1280f, 800f);

        named.Write("sample", Two, manifest: "id: sample\nname: Sample\napi: 0.1\nassembly: sample.dll\nentryPoint: Sample.Second\n");

        Assert.False(LoadFrom(new PluginHost(second), named).HasErrors);
    }

    [Fact]
    public void An_entry_point_the_manifest_names_and_the_assembly_has_not_got_says_which() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write("sample", Hello, manifest: "id: sample\nname: Sample\napi: 0.1\nassembly: sample.dll\nentryPoint: Sample.Missing\n");

        var report = LoadFrom(new PluginHost(shell), folder);

        Assert.True(report.HasErrors);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Message.Contains("Sample.Missing", StringComparison.Ordinal));
    }

    [Fact]
    public void An_assembly_with_no_plugin_in_it_says_so() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write("sample", "public sealed class NotAPlugin { }");

        var report = LoadFrom(new PluginHost(shell), folder);

        Assert.True(report.HasErrors);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Message.Contains("no public IEditorPlugin", StringComparison.Ordinal));
    }

    [Fact]
    public void A_plugin_reaches_the_extension_points_the_shell_does_not_own_through_services() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write(
            "sample",
            """
            using System.Collections.Generic;
            using Vixen.Editor.Plugin;

            namespace Sample;

            public sealed class Entry : IEditorPlugin {
                public void Activate(PluginContext context) {
                    var sink = context.Services.Require<List<string>>();
                    sink.Add("terrain");
                    context.OnUnload(() => sink.Remove("terrain"));
                }
            }
            """
        );

        var registry = new List<string>();
        var host = new PluginHost(shell, new PluginServices().Add(registry));

        Assert.False(LoadFrom(host, folder).HasErrors);
        Assert.Equal(["terrain"], registry);

        // OnUnload is the escape hatch every extension point outside the shell goes through: a
        // drawer, an importer, a node type. A registration with no matching undo is a leak with no
        // symptom.
        host.Unload("sample");
        Assert.Empty(registry);
    }

    [Fact]
    public void A_plugin_needing_a_service_this_host_has_not_got_fails_with_a_sentence_about_it() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        folder.Write(
            "sample",
            """
            using System.Collections.Generic;
            using Vixen.Editor.Plugin;

            namespace Sample;

            public sealed class Entry : IEditorPlugin {
                public void Activate(PluginContext context) => context.Services.Require<List<string>>();
            }
            """
        );

        var report = LoadFrom(new PluginHost(shell), folder);

        Assert.True(report.HasErrors);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Message.Contains("PluginException", StringComparison.Ordinal));
    }

    [Fact]
    public void Dependencies_decide_the_order_a_plugin_sees_the_editor_in() {
        using var folder = new PluginFolder();
        using var shell = new EditorShell(1280f, 800f);

        const string Provider = """
                                using Vixen.Editor.Plugin;
                                using Vixen.Editor.Ui;
                                using Vixen.Ui;

                                namespace Sample;

                                public sealed class Entry : IEditorPlugin {
                                    public void Activate(PluginContext context) =>
                                        context.AddCommand("base.thing", new StringId("base.thing", "Thing"), () => { });
                                }
                                """;

        // The consumer asserts, inside its own Activate, that the plugin it depends on has already
        // registered — which is what a dependency buys and the only way to check it from here.
        const string Consumer = """
                                using System;
                                using Vixen.Editor.Plugin;
                                using Vixen.Editor.Ui;
                                using Vixen.Ui;

                                namespace Sample;

                                public sealed class Entry : IEditorPlugin {
                                    public void Activate(PluginContext context) {
                                        if (!context.Shell.Commands.TryGet("base.thing", out _)) {
                                            throw new InvalidOperationException("activated before its dependency");
                                        }

                                        context.AddCommand("top.thing", new StringId("top.thing", "Thing"), () => { });
                                    }
                                }
                                """;

        // Named so that discovery's alphabetical order is the wrong one: without the dependency
        // sort, 'aaa' activates first and its own assertion fails.
        folder.Write("aaa", Consumer, manifest: "id: aaa\nname: Consumer\napi: 0.1\nassembly: aaa.dll\ndependencies:\n  - zzz\n");
        folder.Write("zzz", Provider);

        var report = LoadFrom(new PluginHost(shell), folder);

        Assert.False(report.HasErrors);
        Assert.Equal(["zzz", "aaa"], report.Activated.Select(plugin => plugin.Id));
    }
}
