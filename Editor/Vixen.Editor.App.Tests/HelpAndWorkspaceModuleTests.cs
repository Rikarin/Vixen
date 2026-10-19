// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Editor.Ui.Documents;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 50 § D3: the Help menu and the layout presets are modules, and what they need is published.</summary>
/// <remarks>
///     ⚠ <b>By effect, through the editor the host builds.</b> <c>ModuleReachTests</c>' manifest is the
///     wider proof — it records each line under the module that put it there — and these are the two
///     things it cannot say: that the verbs read only what a plugin is handed, and that turning the
///     module off takes them away.
/// </remarks>
public class HelpAndWorkspaceModuleTests {
    /// <summary>The six Help lines are the module's, and greyed on a host with no link opener.</summary>
    [Fact]
    public void The_help_lines_are_registered_over_the_published_link_opener() {
        using var session = EditorSession.Start();

        var links = session.Plugins.Services.Require<IExternalLinks>();

        // A headless session has no opener, which is the state each link line greys itself in.
        Assert.False(links.CanOpen);

        foreach (var id in (string[])["help.documentation", "help.api-reference", "help.release-notes", "help.report-bug", "help.show-log-folder"]) {
            Assert.NotNull(session.Shell.Commands[id]);
            Assert.False(session.CanRun(id), $"'{id}' is enabled on a host that cannot open a link.");
        }

        Assert.True(session.CanRun("help.about"));
    }

    /// <summary>Disabling a module takes its lines with it, which is what being a module means.</summary>
    /// <remarks>
    ///     ⚠ <b>The negative half, and the one a registration check cannot see.</b> A Help line the
    ///     application still registered by hand would survive the module being switched off — exactly
    ///     the shape doc 50 § F2 measured, where the attribute said one thing and the shell another.
    /// </remarks>
    [Theory]
    [InlineData(HelpModule.ModuleId, "help.about")]
    [InlineData(WorkspaceModule.ModuleId, "view.layout.Default")]
    [InlineData(WorkspaceModule.ModuleId, "view.layout.Sequencing")]
    [InlineData(WorkspaceModule.ModuleId, "view.save-layout")]
    [InlineData(WorkspaceModule.ModuleId, "tools.reload-styles")]
    public void Disabling_the_module_takes_its_lines_away(string module, string command) {
        using var session = EditorSession.Start();

        Assert.NotNull(session.Shell.Commands[command]);

        Assert.True(session.Plugins.Disable(module));

        Assert.Null(session.Shell.Commands[command]);

        session.Plugins.Enable(module);

        Assert.NotNull(session.Shell.Commands[command]);
    }

    /// <summary>
    ///     Save Layout and Reload Styles are the module's names for <see cref="IWorkspace" />'s verbs
    ///     (#1296), and they reach the user's store — the arrangement kept, the theme read again.
    /// </summary>
    [Fact]
    public void Save_layout_and_reload_styles_reach_the_users_store_through_the_workspace() {
        using var session = EditorSession.Start();

        var store = new EditorUserStore(session.DataDirectory);
        var workspace = session.Plugins.Services.Require<IWorkspace>();

        session.Run("view.save-layout");

        Assert.Contains(session.Shell.Workspace.DefaultPreset + " (custom)", store.Layouts());

        Assert.Equal(string.Empty, workspace.ThemeTokens);
        store.Write(EditorWorkspace.ThemeFile, "root { --accent: #3f7fd8; }");

        session.Run("tools.reload-styles");

        Assert.Equal("root { --accent: #3f7fd8; }", workspace.ThemeTokens);
    }

    /// <summary>The Profiling preset is the diagnostics module's, because its panels are.</summary>
    [Fact]
    public void The_profiling_preset_belongs_to_the_module_whose_panels_it_names() {
        using var session = EditorSession.Start();

        Assert.NotNull(session.Shell.Commands["view.layout.Profiling"]);

        Assert.True(session.Plugins.Disable("vixen.diagnostics"));

        Assert.Null(session.Shell.Commands["view.layout.Profiling"]);
        Assert.NotNull(session.Shell.Commands["view.layout.Default"]);
    }
}
