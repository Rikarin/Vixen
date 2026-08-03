// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.Scripts.Tests;

/// <summary>Doc 36 § P5: a <c>.cs</c> file in a project's <c>Editor/</c> folder, compiled and loaded.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Real files in a real folder, compiled by the real compiler.</b> The claim is that
///         dropping a file in makes a menu item appear, and every part of that — the discovery, the
///         references the compiler is given, the load context, the attribute — is a place it can fail
///         quietly. A test that handed the loader an assembly would skip the half that is new.
///     </para>
///     <para>
///         Each test gets a project folder of its own, because the whole subject is a folder being
///         written to while something watches it.
///     </para>
/// </remarks>
public class EditorScriptTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-scripts-" + Guid.NewGuid().ToString("N"));
    readonly EditorShell shell;
    readonly PluginHost host;

    public EditorScriptTests() {
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Editor"));

        shell = new EditorShell(1280f, 800f);
        host = new PluginHost(shell);
    }

    public void Dispose() {
        host.UnloadAll();
        shell.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    EditorScripts Scripts() => new(host, root, Path.Combine(root, "Library", "EditorScripts"));

    void Write(string name, string source) =>
        File.WriteAllText(Path.Combine(root, "Assets", "Editor", name), source);

    /// <summary>A script that registers one verb, which is what these tests watch come and go.</summary>
    /// <remarks>
    ///     ⚠ <b>An <c>IEditorPlugin</c> rather than an <c>[EditorMenu]</c>, and the reason is where the
    ///     attributes are read.</b> Doc 36 § D3 put that in <c>PluginHost.Scanners</c> — filled by the
    ///     editor's application, because the attributes name types the plugin contract must not
    ///     reference — so a bare host has none and an attribute here would do nothing. What this suite
    ///     is about is the compile, the load, the reload and the unload; the attributes are asserted in
    ///     <c>Vixen.Editor.App.Tests</c>, against a host that wires the real scanner.
    /// </remarks>
    const string OneVerb = """
        using Vixen.Editor.Plugin;
        using Vixen.Editor.Ui;

        public sealed class ProjectTools : IEditorPlugin {
            public void Activate(PluginContext context) =>
                context.AddCommand("project.rebuild-navigation", new StringId("x", "Rebuild Navigation"), () => { });
        }
        """;

    /// <summary>
    ///     ⚠ <b>A file appears in the folder, and what it registered is in the shell</b> — no restart,
    ///     no project file, nothing named by hand anywhere in the editor.
    /// </summary>
    [Fact]
    public void A_script_dropped_in_reaches_the_shell() {
        Write("ProjectTools.cs", OneVerb);

        var state = Scripts().Rebuild();

        Assert.True(state.Loaded, string.Join(Environment.NewLine, state.Build.Diagnostics));
        Assert.Equal(1, state.Plugins);

        var command = shell.Commands["project.rebuild-navigation"];

        Assert.NotNull(command);
        shell.Commands.Execute(command.Id);
    }

    /// <summary>
    ///     ⚠ <b>The other half of the exit criterion: an error is a list, not a crash.</b> The build
    ///     fails, the editor keeps running, and what the panel gets is a file, a line and a message
    ///     rather than a wall of console text somebody has to parse.
    /// </summary>
    [Fact]
    public void A_compile_error_is_a_diagnostic_with_a_place_in_it() {
        Write("Broken.cs", """
            public static class Broken {
                public static void Run() => Nonexistent.Thing();
            }
            """);

        var state = Scripts().Rebuild();

        Assert.False(state.Loaded);
        Assert.True(state.Build.Failed);

        var error = Assert.Single(state.Build.Errors);

        Assert.Equal("CS0103", error.Id);
        Assert.EndsWith("Broken.cs", error.File, StringComparison.Ordinal);
        Assert.Equal(2, error.Line);
    }

    /// <summary>
    ///     ⚠ <b>A failed build leaves the previous one loaded.</b> Somebody halfway through typing a
    ///     method name should not lose the menu they were about to use — and an editor whose tools
    ///     silently vanished because of a missing semicolon is worse than one showing the last build.
    /// </summary>
    [Fact]
    public void A_failed_rebuild_keeps_what_was_working() {
        Write("ProjectTools.cs", OneVerb);

        var scripts = Scripts();

        Assert.True(scripts.Rebuild().Loaded);

        Write("Broken.cs", "this is not C#");

        var state = scripts.Rebuild();

        Assert.True(state.Build.Failed);
        Assert.NotEmpty(state.Build.Errors);

        // Still there, and still runnable.
        Assert.NotNull(shell.Commands["project.rebuild-navigation"]);
        Assert.Equal(PluginState.Active, host.Find(EditorScripts.PluginId)?.State);
    }

    /// <summary>
    ///     ⚠ <b>A rebuild replaces rather than adds.</b> The registration scope is the plugin host's,
    ///     so everything the previous assembly registered goes when it is unloaded — without that, a
    ///     folder saved ten times would be ten copies of every menu item.
    /// </summary>
    [Fact]
    public void A_rebuild_replaces_the_previous_assemblys_registrations() {
        Write("ProjectTools.cs", OneVerb);

        var scripts = Scripts();

        scripts.Rebuild();
        scripts.Rebuild();
        scripts.Rebuild();

        // ⚠ One command, not three. The registration scope is the plugin host's, so everything the
        // previous assembly registered goes when it is unloaded — without that, a folder saved ten
        // times would be ten copies of every verb, and `CommandRegistry` would have refused the
        // second.
        Assert.NotNull(shell.Commands["project.rebuild-navigation"]);
        Assert.Single(host.Plugins, plugin => plugin.Id == EditorScripts.PluginId);
    }

    /// <summary>
    ///     ⚠ <b>Unloading takes the menu with it.</b> Closing a project must not leave its scripts'
    ///     verbs in the command palette of the next one — which is the same rollback a plugin gets,
    ///     because it is literally the same scope.
    /// </summary>
    [Fact]
    public void Unloading_takes_the_scripts_contributions_out() {
        Write("ProjectTools.cs", OneVerb);

        var scripts = Scripts();

        scripts.Rebuild();
        Assert.NotNull(shell.Commands["project.rebuild-navigation"]);

        Assert.True(scripts.Unload());
        Assert.Null(shell.Commands["project.rebuild-navigation"]);
    }

    /// <summary>
    ///     ⚠ <b>An <c>IEditorPlugin</c> in a script is the full door.</b> The attribute is the small
    ///     thing; a script that wants a panel, a mode or a contribution writes the same interface a
    ///     packaged plugin does, and is handed the same context.
    /// </summary>
    [Fact]
    public void A_script_can_be_a_whole_plugin() {
        Write("Plugin.cs", """
            using Vixen.Editor.Plugin;
            using Vixen.Editor.Ui;

            public sealed class ScriptedPlugin : IEditorPlugin {
                public void Activate(PluginContext context) =>
                    context.AddCommand("scripted.verb", new StringId("scripted.verb", "Scripted Verb"), () => { });
            }
            """);

        var state = Scripts().Rebuild();

        Assert.True(state.Loaded, string.Join(Environment.NewLine, state.Build.Diagnostics));
        Assert.Equal(1, state.Plugins);
        Assert.NotNull(shell.Commands["scripted.verb"]);
    }

    /// <summary>
    ///     ⚠ <b>A project script cannot declare an asset importer, and the refusal has to say why.</b>
    ///     Doc 36 § F8 made importers contributable and a packaged plugin can add one; this tier
    ///     cannot, because an importer is named by its settings type's <c>[DataContract]</c> alias and
    ///     that alias is written by a source generator a loose <c>.cs</c> file never runs. Without the
    ///     message the author gets "WidgetImportSettings has no descriptor" — true, unactionable, and
    ///     about a type they did put the attribute on.
    /// </summary>
    [Fact]
    public void A_script_that_declares_an_importer_is_told_to_ship_a_plugin() {
        Write("WidgetImporter.cs", """
            using System.Threading;
            using System.Threading.Tasks;
            using Vixen.Core;
            using Vixen.Core.Yaml.Meta;
            using Vixen.Editor.Assets;

            [DataContract("WidgetImporter")]
            public sealed record WidgetImportSettings : IImportSettings {
                public int Version { get; init; } = 1;
            }

            [Importer(".widget")]
            public sealed class WidgetImporter : AssetImporter<WidgetImportSettings> {
                public override int Version => 1;

                protected override ValueTask<ImportResult> ImportAsync(
                    ImportContext context,
                    WidgetImportSettings settings,
                    CancellationToken cancellationToken
                ) => ValueTask.FromResult(new ImportResult([], [], []));
            }
            """);

        var state = Scripts().Rebuild();

        // ⚠ It compiled — the refusal is about registering it, not about the C#. A failure at compile
        // time would be the editor refusing valid code, which is a different and worse claim.
        Assert.False(state.Build.Failed, string.Join(Environment.NewLine, state.Build.Diagnostics));

        var refusal = Assert.Single(host.Diagnostics, diagnostic => diagnostic.PluginId == EditorScripts.PluginId);

        Assert.Contains("cannot declare one", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Ship it as a plugin", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A project with no <c>Editor/</c> folder is not a failure.</b> Most projects are content
    ///     and scenes, and an editor that reported a problem for one would be reporting the absence of
    ///     a feature nobody asked for.
    /// </summary>
    [Fact]
    public void A_project_with_no_scripts_reports_nothing_at_all() {
        var state = Scripts().Rebuild();

        Assert.False(state.Loaded);
        Assert.False(state.Build.Failed);
        Assert.Empty(state.Build.Diagnostics);
    }

    /// <summary>
    ///     ⚠ <b>Build output is not source.</b> A generated file under a folder called <c>Editor</c>
    ///     inside <c>Library/</c> is what the last build produced, and compiling it would make every
    ///     rebuild find one more copy of everything.
    /// </summary>
    [Fact]
    public void What_a_build_produced_is_not_compiled_again() {
        Directory.CreateDirectory(Path.Combine(root, "Library", "Editor"));
        File.WriteAllText(Path.Combine(root, "Library", "Editor", "Generated.cs"), "this is not C#");

        Write("ProjectTools.cs", OneVerb);

        var state = Scripts().Rebuild();

        Assert.True(state.Loaded, string.Join(Environment.NewLine, state.Build.Diagnostics));
        Assert.Equal(1, state.Build.Sources);
    }
}
