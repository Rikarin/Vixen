// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>The project's settings as a document (#1310): its own history, never the active one.</summary>
public sealed class SettingsDocumentTests {
    /// <summary>Opening it leaves the focus where it was, which is what keeps the viewport's Ctrl+Z off it.</summary>
    [Fact]
    public void Opening_it_does_not_take_the_active_document() {
        using var fixture = new ProjectFixture();
        var project = new EditorProject(fixture.Paths);
        var scene = new TestDocument(project, "Scene");

        var settings = new SettingsDocument(project, "Project Settings");

        Assert.Same(scene, project.ActiveDocument.Value);
        Assert.Contains(settings, project.Documents);
    }

    /// <summary>A write that did not go through its stack is a change from outside it, saved and not undoable.</summary>
    [Fact]
    public void A_reset_is_a_change_from_outside_its_history_and_saving_writes_it() {
        using var fixture = new ProjectFixture();
        var project = new EditorProject(fixture.Paths);
        var settings = new SettingsDocument(project, "Project Settings");

        project.Settings.Get<PlaySettings>().Players = 4;
        project.Settings.Reset<PlaySettings>();

        Assert.False(settings.IsDirty.Value);
        Assert.True(settings.ExternallyModified.Value);
        Assert.True(project.HasUnsavedChanges.Value);

        settings.Save();

        Assert.False(settings.HasUnsavedChanges.Value);
        Assert.False(project.Settings.HasUnsavedChanges);
        Assert.True(File.Exists(project.Settings.FileFor<PlaySettings>()));
    }

    /// <summary>An edit recorded against a type is written by the save, though the store was never marked.</summary>
    [Fact]
    public void A_recorded_type_is_what_the_save_writes() {
        using var fixture = new ProjectFixture();
        var project = new EditorProject(fixture.Paths);
        var settings = new SettingsDocument(project, "Project Settings");

        project.Settings.Get<PlaySettings>().StartScene = "Arena";
        settings.Record(typeof(PlaySettings));

        Assert.False(project.Settings.HasUnsavedChanges);

        settings.Save();

        Assert.Contains("Arena", File.ReadAllText(project.Settings.FileFor<PlaySettings>()), StringComparison.Ordinal);
    }

    /// <summary>Revert reads the files again and is clean.</summary>
    [Fact]
    public void Revert_reads_the_files_again() {
        using var fixture = new ProjectFixture();
        var project = new EditorProject(fixture.Paths);
        var settings = new SettingsDocument(project, "Project Settings");

        project.Settings.Get<PlaySettings>().Players = 4;
        project.Settings.MarkChanged<PlaySettings>();

        Assert.True(settings.Reload());

        Assert.Equal(1, project.Settings.Get<PlaySettings>().Players);
        Assert.False(settings.HasUnsavedChanges.Value);
    }
}
