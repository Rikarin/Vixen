// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20's A2: "which project", asked before anything else and answered without a restart.</summary>
public class ProjectBrowserTests {
    [Fact]
    public void Opening_a_project_records_it_and_puts_it_at_the_top() {
        using var scope = new Scratch();

        var history = new ProjectHistory(scope.Directory);

        Assert.Empty(history.Entries);

        history.Record(Path.Combine(scope.Directory, "Alpha"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        history.Record(Path.Combine(scope.Directory, "Beta"), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(["Beta", "Alpha"], history.Entries.Select(entry => entry.Name));

        // Reopening one moves it back to the top rather than adding a second entry.
        history.Record(Path.Combine(scope.Directory, "Alpha"), new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(["Alpha", "Beta"], history.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public void The_list_is_bounded_and_survives_a_reopen() {
        using var scope = new Scratch();

        var history = new ProjectHistory(scope.Directory) { Limit = 3 };

        for (var index = 0; index < 6; index++) {
            history.Record(
                Path.Combine(scope.Directory, "Project" + index),
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index)
            );
        }

        Assert.Equal(3, history.Entries.Count);
        Assert.Equal(["Project5", "Project4", "Project3"], history.Entries.Select(entry => entry.Name));

        var reopened = new ProjectHistory(scope.Directory);

        Assert.Equal(["Project5", "Project4", "Project3"], reopened.Entries.Select(entry => entry.Name));
    }

    /// <summary>
    ///     ⚠ A project on a volume that is not mounted stays in the list. Pruning it is the one thing
    ///     there is no way back from — the user cannot type a path they no longer remember.
    /// </summary>
    [Fact]
    public void A_project_that_has_gone_is_kept_and_marked_rather_than_forgotten() {
        using var scope = new Scratch();

        var history = new ProjectHistory(scope.Directory);
        var missing = Path.Combine(scope.Directory, "Unplugged");

        history.Record(missing, DateTime.UtcNow);

        var entry = Assert.Single(history.Entries);

        Assert.False(entry.Exists);

        Directory.CreateDirectory(missing);
        Assert.True(new ProjectHistory(scope.Directory).Entries[0].Exists);
    }

    [Fact]
    public void A_broken_history_file_is_an_empty_list_rather_than_a_throw() {
        using var scope = new Scratch();

        File.WriteAllText(
            Path.Combine(scope.Directory, EditorUserStore.RecentProjectsFile),
            "projects: [ this is not\n  - yaml\n"
        );

        Assert.Empty(new ProjectHistory(scope.Directory).Entries);
    }

    /// <summary>The editor records the project it opened, which is what Open Recent lists.</summary>
    [Fact]
    public void The_editor_records_the_project_it_opened() {
        using var fixture = EditorSession.Start();

        var recorded = Assert.Single(fixture.RecentProjects);

        Assert.Equal(fixture.ProjectRoot, recorded.Path);
    }

    /// <summary>
    ///     Doc 20 filed New and Open Project as "swapping a project underneath a live editor". They
    ///     do not swap one: the request closes this editor and hands the host the next root.
    /// </summary>
    [Fact]
    public void Choosing_another_project_asks_the_host_to_reopen_over_it() {
        using var fixture = EditorSession.Start();
        using var scope = new Scratch();

        var next = Path.Combine(scope.Directory, "Next");
        Directory.CreateDirectory(next);

        fixture.RequestProject(next);

        Assert.True(fixture.IsClosing);
        Assert.Equal(Path.GetFullPath(next), fixture.PendingProject);
    }

    [Fact]
    public void Choosing_the_project_that_is_already_open_does_nothing() {
        using var fixture = EditorSession.Start();

        fixture.RequestProject(fixture.ProjectRoot);

        Assert.False(fixture.IsClosing);
        Assert.Null(fixture.PendingProject);
    }

    /// <summary>
    ///     ⚠ Doc 20's A2 lists "opening a second project with dirty documents" beside closing the
    ///     window as a case that must ask — and backing out has to leave both the editor and the
    ///     pending project alone, or the next close would silently act on a root nobody chose.
    /// </summary>
    [Fact]
    public void Unsaved_work_is_asked_about_and_backing_out_forgets_the_request() {
        using var fixture = EditorSession.Start();
        using var scope = new Scratch();

        var next = Path.Combine(scope.Directory, "Next");
        Directory.CreateDirectory(next);

        fixture.Run("scene.create-entity");
        Assert.True(fixture.Scene.IsDirty.Value);

        fixture.RequestProject(next);

        Assert.True(fixture.IsAsking);

        fixture.Answer("Cancel");

        Assert.False(fixture.IsClosing);
        Assert.Null(fixture.PendingProject);
    }

    [Fact]
    public void The_startup_browser_lists_the_recent_projects_and_can_be_dismissed() {
        using var fixture = EditorSession.Start();

        fixture.Run("file.open-project");

        Assert.True(fixture.IsAsking);

        var labels = Labels(fixture);

        Assert.Contains(EditorStrings.ProjectsBrowse.Text, labels);
        Assert.Contains(EditorStrings.ProjectsNew.Text, labels);
        Assert.Contains(EditorStrings.DialogCancel.Text, labels);

        fixture.Answer(EditorStrings.DialogCancel.Text);

        Assert.False(fixture.IsAsking);
        Assert.Null(fixture.PendingProject);
    }

    static List<string> Labels(EditorSession fixture) {
        List<string> found = [];

        Walk(fixture.Shell.Dialogs.Current!);
        return found;

        void Walk(Vixen.Ui.UiElement element) {
            if (element is Vixen.Ui.Controls.ButtonBase { Label: { } label }) {
                found.Add(label);
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }

}
