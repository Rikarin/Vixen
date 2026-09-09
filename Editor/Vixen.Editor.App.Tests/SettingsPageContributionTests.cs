// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 36 § D4's <c>AddSettingsPage</c> row, asserted on the window rather than the registry.</summary>
/// <remarks>
///     <para>
///         <b>P2's rule about how to test one of these, applied.</b> The first <c>[Overlay]</c> test
///         asserted the <c>SceneOverlay</c> was in the registry — "which passes with
///         <c>ViewportChrome</c> never reading it". A declaration is built when something does
///         something with it, so every assertion here is about the Preferences window: the rail line,
///         the pane the page drew, and the pane after the contribution is withdrawn.
///     </para>
///     <para>
///         ⚠ <b>Three moments, because they fail independently.</b> A window that reads the registry
///         in its factory passes the first and fails the second — which is the failure
///         <c>RefreshAssetKinds</c> and <c>RefreshOverlays</c> already exist to prevent, arriving for
///         a third kind. A window that never removes passes the first two and fails the third, and
///         that is the one that leaves a rail line whose <c>Build</c> closes over an unloaded
///         assembly.
///     </para>
/// </remarks>
public class SettingsPageContributionTests {
    const string PageId = "sample.telemetry";
    const string PageText = "contributed by a plugin";

    /// <summary>A page registered before the window opens is in it when it does.</summary>
    [Fact]
    public void A_contributed_page_is_in_the_preferences_window() {
        var data = Directory();
        var registry = new EditorRegistry();

        try {
            using var scope = registry.Add(Page(SettingsScope.Preferences));
            using var editor = EditorSession.Start(new() { DataDirectory = data, Extensions = registry });

            var view = editor.Control<SettingsView>(EditorApplication.PreferencesPanel);

            editor.Settle();

            // The rail, which is the window's own list rather than the registry's.
            Assert.Contains(view.Categories, category => category.Id == PageId);

            // And the pane: selecting the page builds what the contribution said to build. A rail
            // line whose Build never runs is a line, not a settings page.
            Assert.True(view.Select(PageId));
            editor.Settle();

            Assert.Contains(Texts(view.Pane), text => text == PageText);
        } finally {
            Remove(data);
        }
    }

    /// <summary>
    ///     And one registered while the window is open appears in it, which is the half a factory
    ///     that reads the registry once gets wrong.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A plugin activating three seconds after start-up is always after the window was
    ///     built.</b> Enabling a plugin from the manager, a project script's first build and a reload
    ///     are all this moment, so a page that only appeared when the plugin happened to load first
    ///     would be one tier working and another silently not.
    /// </remarks>
    [Fact]
    public void A_page_contributed_while_the_window_is_open_appears_in_it() {
        var data = Directory();
        var registry = new EditorRegistry();

        try {
            using var editor = EditorSession.Start(new() { DataDirectory = data, Extensions = registry });

            var view = editor.Control<SettingsView>(EditorApplication.PreferencesPanel);

            editor.Settle();

            // The instrument: the window is open and has pages of its own, and none of them is this.
            Assert.NotEmpty(view.Categories);
            Assert.DoesNotContain(view.Categories, category => category.Id == PageId);

            using var scope = registry.Add(Page(SettingsScope.Preferences));

            editor.Settle();

            Assert.Contains(view.Categories, category => category.Id == PageId);

            Assert.True(view.Select(PageId));
            editor.Settle();

            Assert.Contains(Texts(view.Pane), text => text == PageText);
        } finally {
            Remove(data);
        }
    }

    /// <summary>
    ///     And withdrawing it takes the page out of the open window, including when it is the page
    ///     being shown.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The half that matters most, because what is left behind is a delegate.</b>
    ///     <c>SettingsCategory.Build</c> is an <c>Action&lt;UiElement&gt;</c> the plugin supplied, so a
    ///     page kept after an unload is a rail line pointing into an assembly nothing else refers to —
    ///     the one failure the whole plugin arrangement is built to prevent.
    /// </remarks>
    [Fact]
    public void Withdrawing_the_contribution_takes_the_page_out_of_the_open_window() {
        var data = Directory();
        var registry = new EditorRegistry();

        try {
            using var editor = EditorSession.Start(new() { DataDirectory = data, Extensions = registry });

            var view = editor.Control<SettingsView>(EditorApplication.PreferencesPanel);
            var scope = registry.Add(Page(SettingsScope.Preferences));

            editor.Settle();

            Assert.True(view.Select(PageId));
            editor.Settle();

            Assert.Equal(PageId, view.Current);

            scope.Dispose();
            editor.Settle();

            Assert.DoesNotContain(view.Categories, category => category.Id == PageId);
            Assert.NotEqual(PageId, view.Current);

            // And the pane is no longer showing what the withdrawn page drew.
            Assert.DoesNotContain(Texts(view.Pane), text => text == PageText);
        } finally {
            Remove(data);
        }
    }

    /// <summary>
    ///     A page scoped to Project Settings does not turn up in Preferences, which is what makes the
    ///     scope a decision rather than a label.
    /// </summary>
    [Fact]
    public void A_project_scoped_page_stays_out_of_the_preferences_window() {
        var data = Directory();
        var registry = new EditorRegistry();

        try {
            using var scope = registry.Add(Page(SettingsScope.Project));
            using var editor = EditorSession.Start(new() { DataDirectory = data, Extensions = registry });

            var preferences = editor.Control<SettingsView>(EditorApplication.PreferencesPanel);

            editor.Settle();

            Assert.DoesNotContain(preferences.Categories, category => category.Id == PageId);

            var projects = editor.Control<SettingsView>(EditorApplication.ProjectSettingsPanel);

            editor.Settle();

            Assert.Contains(projects.Categories, category => category.Id == PageId);
        } finally {
            Remove(data);
        }
    }

    /// <summary>
    ///     ⚠ A contributed page naming an id the editor's own window already uses is skipped, not
    ///     thrown: <c>SettingsView.Add</c> refuses a duplicate, and nothing may fail the editor for a
    ///     plugin's mistake.
    /// </summary>
    [Fact]
    public void A_page_naming_a_built_in_id_does_not_take_the_window_down() {
        var data = Directory();
        var registry = new EditorRegistry();

        try {
            using var scope = registry.Add(
                new SettingsPage(
                    SettingsScope.Preferences,
                    new SettingsCategory("general", new StringId("editor.settings.sample", "Sample"), Draw)
                )
            );

            using var editor = EditorSession.Start(new() { DataDirectory = data, Extensions = registry });

            var view = editor.Control<SettingsView>(EditorApplication.PreferencesPanel);

            editor.Settle();

            // The editor's own General page is still the one under that id, and the window is up.
            Assert.Single(view.Categories, category => category.Id == "general");
            Assert.DoesNotContain(Texts(view.Pane), text => text == PageText);
        } finally {
            Remove(data);
        }
    }

    static SettingsPage Page(SettingsScope scope) =>
        new(scope, new SettingsCategory(PageId, new StringId("editor.settings.sample", "Sample"), Draw));

    static void Draw(UiElement pane) => pane.Add<TextBlock>().Text = PageText;

    /// <summary>Every string a pane's tree is showing, which is what a reader would see.</summary>
    static List<string> Texts(UiElement element) {
        List<string> found = [];

        void Walk(UiElement node) {
            if (node is TextBlock { Text: { } text }) {
                found.Add(text);
            }

            foreach (var child in node.Children) {
                Walk(child);
            }
        }

        Walk(element);
        return found;
    }

    static string Directory() =>
        Path.Combine(Path.GetTempPath(), "vixen-settings-page-" + Guid.NewGuid().ToString("N"));

    static void Remove(string data) {
        if (System.IO.Directory.Exists(data)) {
            System.IO.Directory.Delete(data, recursive: true);
        }
    }
}
