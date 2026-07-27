// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>A settings asset: an ordinary contract type, described by the generator like any other.</summary>
[DataContract("PlaySettings")]
public sealed class PlaySettings {
    /// <summary>Which scene play mode starts in.</summary>
    public string StartScene { get; set; } = "";

    /// <summary>How many player processes an out-of-process play session launches.</summary>
    public int Players { get; set; } = 1;

    /// <summary>Whether entering play mode reloads the domain.</summary>
    public bool ReloadDomain { get; set; } = true;
}

/// <summary>Settings assets under <c>ProjectSettings/</c>.</summary>
public sealed class ProjectSettingsStoreTests {
    [Fact]
    public void SettingsNobodyHasSavedAreTheDefaultsAndNoFileIsWritten() {
        using var project = new ProjectFixture();
        var store = new ProjectSettingsStore(project.Paths);

        var settings = store.Get<PlaySettings>();

        Assert.Equal(1, settings.Players);
        Assert.True(settings.ReloadDomain);
        Assert.False(File.Exists(store.FileFor<PlaySettings>()));
    }

    [Fact]
    public void EveryCallerGetsTheOneInstance() {
        using var project = new ProjectFixture();
        var store = new ProjectSettingsStore(project.Paths);

        Assert.Same(store.Get<PlaySettings>(), store.Get<PlaySettings>());
    }

    [Fact]
    public void SettingsSurviveBeingWrittenAndReadBack() {
        using var project = new ProjectFixture();
        var store = new ProjectSettingsStore(project.Paths);

        var settings = store.Get<PlaySettings>();
        settings.StartScene = "Assets/Scenes/Level1.vxscene";
        settings.Players = 4;
        settings.ReloadDomain = false;
        store.Save<PlaySettings>();

        var reopened = new ProjectSettingsStore(project.Paths).Get<PlaySettings>();

        Assert.Equal("Assets/Scenes/Level1.vxscene", reopened.StartScene);
        Assert.Equal(4, reopened.Players);
        Assert.False(reopened.ReloadDomain);
    }

    /// <summary>
    ///     The file is named after the contract's alias, not the C# type, so renaming
    ///     <c>PlaySettings</c> to something else does not orphan every project's settings.
    /// </summary>
    [Fact]
    public void TheFileIsNamedAfterTheContractAlias() {
        using var project = new ProjectFixture();
        var store = new ProjectSettingsStore(project.Paths);

        Assert.Equal(
            Path.Combine(project.Paths.ProjectSettings, "PlaySettings.vxsettings"),
            store.FileFor<PlaySettings>()
        );
    }

    [Fact]
    public void OnlyWhatWasMarkedChangedIsWritten() {
        using var project = new ProjectFixture();
        var store = new ProjectSettingsStore(project.Paths);

        _ = store.Get<PlaySettings>();
        Assert.False(store.HasUnsavedChanges);
        Assert.Equal(0, store.SaveAll());

        store.Get<PlaySettings>().Players = 2;
        store.MarkChanged<PlaySettings>();

        Assert.True(store.HasUnsavedChanges);
        Assert.Equal(1, store.SaveAll());
        Assert.False(store.HasUnsavedChanges);
        Assert.Equal(0, store.SaveAll());
    }

    /// <summary>
    ///     A project written by a newer editor opens in an older one. The key is ignored so the
    ///     project still loads, and reported so that it is not ignored silently.
    /// </summary>
    [Fact]
    public void AKeyNoMemberClaimsIsIgnoredAndReported() {
        using var project = new ProjectFixture();
        var store = new ProjectSettingsStore(project.Paths);
        Directory.CreateDirectory(project.Paths.ProjectSettings);
        File.WriteAllText(store.FileFor<PlaySettings>(), "players: 3\nheadlessPlayers: 2\n");

        var settings = store.Get<PlaySettings>();

        Assert.Equal(3, settings.Players);
        Assert.Equal(["PlaySettings.vxsettings: headlessPlayers"], store.UnknownKeys);
    }

    [Fact]
    public void ReloadingForgetsTheCachedInstanceAndReadsDiskAgain() {
        using var project = new ProjectFixture();
        var store = new ProjectSettingsStore(project.Paths);

        store.Get<PlaySettings>().Players = 7;
        store.MarkChanged<PlaySettings>();
        store.SaveAll();

        Directory.CreateDirectory(project.Paths.ProjectSettings);
        File.WriteAllText(store.FileFor<PlaySettings>(), "players: 9\n");
        store.Reload();

        Assert.Equal(9, store.Get<PlaySettings>().Players);
    }

    [Fact]
    public void AProjectExposesItsSettingsStoreOverItsOwnDirectory() {
        using var fixture = new ProjectFixture();
        var project = new EditorProject(fixture.Paths);

        Assert.Equal(
            Path.Combine(fixture.Paths.ProjectSettings, "PlaySettings.vxsettings"),
            project.Settings.FileFor<PlaySettings>()
        );
    }
}
