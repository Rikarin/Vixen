// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ui.Reactive;
using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>Opening a project, and the selection over it.</summary>
public sealed class EditorProjectTests {
    [Fact]
    public void OpeningAProjectScansItAndBuildsTheReferenceIndex() {
        using var fixture = new ProjectFixture();
        var texture = fixture.Add("Assets/hero.png");
        var material = fixture.Add("Assets/hero.vxmat", $"albedo: vx:{texture}\n");

        var project = new EditorProject(fixture.Paths);
        var report = project.Open();

        Assert.Equal(2, report.Assets);
        Assert.True(project.Assets.TryGetByGuid(texture, out _));
        Assert.Equal([material], project.References.ReferrersOf(texture));
    }

    [Fact]
    public void OpeningItAgainUsesTheSavedIndexRatherThanRescanning() {
        using var fixture = new ProjectFixture();
        fixture.Add("Assets/hero.png");

        new EditorProject(fixture.Paths).Open();

        var report = new EditorProject(fixture.Paths).Open();

        // Nothing was scanned, which is what an editor launching against a hundred thousand assets
        // needs to be true.
        Assert.Equal(0, report.Assets);
    }

    [Fact]
    public void ConstructingAProjectTouchesNoDisk() {
        var project = new EditorProject(new(Path.Combine(Path.GetTempPath(), "vixen-does-not-exist")));

        Assert.Equal(0, project.Assets.Count);
        Assert.False(Directory.Exists(project.Paths.Root));
    }

    [Fact]
    public void AProjectIsNamedAfterItsDirectory() {
        using var fixture = new ProjectFixture();

        Assert.Equal(Path.GetFileName(fixture.Paths.Root), new EditorProject(fixture.Paths).Name);
    }

    [Fact]
    public void ActivatingADocumentThatIsNotOpenIsRefused() {
        var first = ModelFixture.Project();
        var second = ModelFixture.Project();
        var stranger = new TestDocument(second);

        Assert.Throws<InvalidOperationException>(() => first.Activate(stranger));
    }
}

/// <summary>The selection: ordered, deduplicated, and something a view can bind to.</summary>
public sealed class SelectionTests {
    [Fact]
    public void SelectingSomethingTwiceSelectsItOnce() {
        var selection = new Selection<AssetId>();
        var asset = AssetId.New();

        Assert.True(selection.Add(asset));
        Assert.False(selection.Add(asset));
        Assert.Single(selection);
    }

    [Fact]
    public void ThePrimaryIsTheOneAddedLast() {
        var selection = new Selection<AssetId>();
        var first = AssetId.New();
        var second = AssetId.New();

        selection.Add(first);
        selection.Add(second);

        Assert.Equal(second, selection.Primary);
        Assert.Equal([first, second], selection);
    }

    [Fact]
    public void SettingOneThingReplacesEverything() {
        var selection = new Selection<AssetId>();
        var kept = AssetId.New();

        selection.Set([AssetId.New(), AssetId.New()]);
        selection.Set(kept);

        Assert.Equal([kept], selection);
        Assert.False(selection.IsEmpty);
    }

    [Fact]
    public void TogglingSelectsThenDeselects() {
        var selection = new Selection<AssetId>();
        var asset = AssetId.New();

        Assert.True(selection.Toggle(asset));
        Assert.True(selection.Contains(asset));
        Assert.False(selection.Toggle(asset));
        Assert.True(selection.IsEmpty);
    }

    /// <summary>
    ///     What a row in the project browser binds to. It has to re-evaluate when the selection
    ///     changes, including when it changes to something of the same size.
    /// </summary>
    [Fact]
    public void AskingWhetherSomethingIsSelectedIsAReactiveRead() {
        var selection = new Selection<AssetId>();
        var watched = AssetId.New();
        var highlighted = new Computed<bool>(() => selection.Contains(watched));

        Assert.False(highlighted.Value);

        selection.Set(watched);
        Assert.True(highlighted.Value);

        selection.Set(AssetId.New());
        Assert.False(highlighted.Value);
    }
}
