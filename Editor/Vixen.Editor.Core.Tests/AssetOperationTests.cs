// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>Renaming, moving and deleting a file the project knows about.</summary>
/// <remarks>
///     ⚠ <b>Doc 20 calls a naive rename "the fastest way to corrupt a project", and these are what
///     it means.</b> Not a stale path — references are GUIDs and a GUID does not move — but a
///     sidecar left behind, which turns a rename into a silent loss of identity that nothing reports
///     until a scene opens with nothing in it.
/// </remarks>
public sealed class AssetOperationTests {
    static EditorProject Open(ProjectFixture fixture) {
        var project = new EditorProject(fixture.Paths);

        project.Open();
        return project;
    }

    static AssetId Guid(EditorProject project, string path) {
        Assert.True(project.Assets.TryGetByPath(path, out var entry), $"'{path}' is not in the index");

        return entry.Guid;
    }

    [Fact]
    public void A_rename_moves_the_asset_and_keeps_its_identity() {
        using var fixture = new ProjectFixture();
        var hero = fixture.Add("Assets/Textures/hero.png");

        var project = Open(fixture);

        Assert.True(AssetOperations.Rename(project, hero, "villain").Ok);

        Assert.True(project.Assets.TryGetByGuid(hero, out var entry));
        Assert.Equal("Assets/Textures/villain.png", entry.Path);
        Assert.False(project.Assets.TryGetByPath("Assets/Textures/hero.png", out _));
    }

    /// <summary>
    ///     ⚠ <b>The one that corrupts a project.</b> Move the file and leave the <c>.meta</c>, and
    ///     the next scan finds an asset with no identity, mints a new one, and quarantines the
    ///     orphan — at which point every reference in the project is dangling and nothing said so.
    /// </summary>
    [Fact]
    public void The_sidecar_travels_with_the_asset() {
        using var fixture = new ProjectFixture();
        var hero = fixture.Add("Assets/Textures/hero.png");

        var project = Open(fixture);

        Assert.True(AssetOperations.Rename(project, hero, "villain").Ok);

        Assert.True(File.Exists(fixture.Paths.Absolute("Assets/Textures/villain.png.meta")));
        Assert.False(File.Exists(fixture.Paths.Absolute("Assets/Textures/hero.png.meta")));
    }

    /// <summary>
    ///     The E1 exit's second scenario, at the level the project model can answer it: a scene that
    ///     points at an asset still points at it after the asset is renamed.
    /// </summary>
    [Fact]
    public void A_scene_still_resolves_an_asset_that_has_been_renamed() {
        using var fixture = new ProjectFixture();
        var hero = fixture.Add("Assets/Textures/hero.png");

        fixture.Add("Assets/Scenes/Main.vxscene", $"texture: vx:{hero}\n", importer: "SceneImporter");

        var project = Open(fixture);
        var scene = Guid(project, "Assets/Scenes/Main.vxscene");

        Assert.Contains(hero, project.References.ReferencesFrom(scene).Select(reference => reference.Asset));

        Assert.True(AssetOperations.Rename(project, hero, "villain").Ok);

        // Nothing was rewritten and nothing needed to be: the scene names a GUID, the GUID is in the
        // sidecar, and the sidecar moved with the file.
        Assert.Contains(hero, project.References.ReferencesFrom(scene).Select(reference => reference.Asset));
        Assert.True(project.Assets.TryGetByGuid(hero, out var entry));
        Assert.Equal("Assets/Textures/villain.png", entry.Path);
    }

    /// <summary>
    ///     ⚠ A person typing into a rename box types "Crate", not "Crate.png". Taking them at their
    ///     word produces a file no importer claims, which looks like the asset being destroyed.
    /// </summary>
    [Fact]
    public void A_name_with_no_extension_keeps_the_one_the_file_had() {
        using var fixture = new ProjectFixture();
        var hero = fixture.Add("Assets/Textures/hero.png");

        var project = Open(fixture);

        Assert.True(AssetOperations.Rename(project, hero, "villain").Ok);
        Assert.True(project.Assets.TryGetByGuid(hero, out var kept));
        Assert.Equal("TextureImporter", kept.ImporterTag);

        // And a name that has one is taken as written, because that is somebody changing the format
        // on purpose.
        Assert.True(AssetOperations.Rename(project, hero, "villain.txt").Ok);
        Assert.True(project.Assets.TryGetByGuid(hero, out var changed));
        Assert.Equal("Assets/Textures/villain.txt", changed.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("Props/Crate")]
    [InlineData("hero.png.meta")]
    public void A_name_that_cannot_be_used_is_refused_with_a_reason(string name) {
        using var fixture = new ProjectFixture();
        var hero = fixture.Add("Assets/Textures/hero.png");

        var project = Open(fixture);
        var result = AssetOperations.Rename(project, hero, name);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));

        // And nothing happened, which is the half a test of the message alone would miss.
        Assert.True(project.Assets.TryGetByGuid(hero, out var entry));
        Assert.Equal("Assets/Textures/hero.png", entry.Path);
    }

    [Fact]
    public void A_name_that_is_already_taken_is_refused_rather_than_overwriting() {
        using var fixture = new ProjectFixture();
        var hero = fixture.Add("Assets/Textures/hero.png");

        fixture.Add("Assets/Textures/villain.png");

        var project = Open(fixture);
        var result = AssetOperations.Rename(project, hero, "villain");

        Assert.False(result.Ok);
        Assert.Contains("already", result.Message ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void A_move_puts_the_asset_and_its_sidecar_in_the_new_folder() {
        using var fixture = new ProjectFixture();
        var hero = fixture.Add("Assets/Textures/hero.png");

        fixture.Add("Assets/Ui/placeholder.png");

        var project = Open(fixture);

        Assert.True(AssetOperations.Move(project, hero, "Assets/Ui").Ok);

        Assert.True(project.Assets.TryGetByGuid(hero, out var entry));
        Assert.Equal("Assets/Ui/hero.png", entry.Path);
        Assert.True(File.Exists(fixture.Paths.Absolute("Assets/Ui/hero.png.meta")));
    }

    /// <summary>
    ///     ⚠ Dropping a folder into its own child is a move whose destination is under its source,
    ///     and the file system's answer to that is to delete the tree.
    /// </summary>
    [Fact]
    public void A_folder_cannot_be_moved_inside_itself() {
        using var fixture = new ProjectFixture();

        fixture.Add("Assets/Props/Crates/crate.png");

        var project = Open(fixture);
        var props = Guid(project, "Assets/Props");

        var result = AssetOperations.Move(project, props, "Assets/Props/Crates");

        Assert.False(result.Ok);
        Assert.True(Directory.Exists(fixture.Paths.Absolute("Assets/Props/Crates")));
    }

    [Fact]
    public void A_delete_takes_the_sidecar_with_it() {
        using var fixture = new ProjectFixture();
        var hero = fixture.Add("Assets/Textures/hero.png");

        var project = Open(fixture);

        Assert.True(AssetOperations.Delete(project, hero).Ok);

        Assert.False(File.Exists(fixture.Paths.Absolute("Assets/Textures/hero.png")));

        // ⚠ An orphaned sidecar is quarantined by the next scan, which is a repair nobody asked for
        // appearing in their working tree.
        Assert.False(File.Exists(fixture.Paths.Absolute("Assets/Textures/hero.png.meta")));
        Assert.False(project.Assets.TryGetByGuid(hero, out _));
    }

    [Fact]
    public void Deleting_a_folder_takes_what_is_in_it() {
        using var fixture = new ProjectFixture();

        fixture.Add("Assets/Props/crate.png");
        fixture.Add("Assets/Props/barrel.png");

        var project = Open(fixture);
        var props = Guid(project, "Assets/Props");

        Assert.True(AssetOperations.Delete(project, props).Ok);

        Assert.False(Directory.Exists(fixture.Paths.Absolute("Assets/Props")));
        Assert.False(project.Assets.TryGetByPath("Assets/Props/crate.png", out _));
    }

    /// <summary>
    ///     ⚠ <b>Asked before the delete, not reported after it.</b> A list of newly-broken scenes
    ///     shown once the file has gone is not a warning.
    /// </summary>
    [Fact]
    public void What_would_break_is_answerable_before_anything_is_deleted() {
        using var fixture = new ProjectFixture();
        var hero = fixture.Add("Assets/Textures/hero.png");

        fixture.Add("Assets/Scenes/Main.vxscene", $"texture: vx:{hero}\n", importer: "SceneImporter");

        var project = Open(fixture);

        Assert.Equal(["Assets/Scenes/Main.vxscene"], AssetOperations.Breakage(project, [hero]));
        Assert.True(File.Exists(fixture.Paths.Absolute("Assets/Textures/hero.png")));
    }

    /// <summary>
    ///     ⚠ Deleting a material and the texture it uses together breaks nothing. Reporting "1 scene
    ///     would break" for a file that is itself going is the warning that teaches people to ignore
    ///     warnings.
    /// </summary>
    [Fact]
    public void A_referrer_that_is_itself_being_deleted_is_not_breakage() {
        using var fixture = new ProjectFixture();
        var hero = fixture.Add("Assets/Textures/hero.png");
        var scene = fixture.Add("Assets/Scenes/Main.vxscene", $"texture: vx:{hero}\n", importer: "SceneImporter");

        var project = Open(fixture);

        Assert.Empty(AssetOperations.Breakage(project, [hero, scene]));
    }

    [Fact]
    public void A_new_folder_is_made_and_indexed() {
        using var fixture = new ProjectFixture();

        fixture.Add("Assets/Textures/hero.png");

        var project = Open(fixture);

        Assert.True(AssetOperations.CreateFolder(project, "Assets", "Props").Ok);
        Assert.True(project.Assets.TryGetByPath("Assets/Props", out var folder));
        Assert.True(folder.IsFolder);
    }

    /// <summary>
    ///     ⚠ "Make me somewhere to put this" is the gesture, and an error dialog is not an answer to
    ///     it. Renaming to a taken name is the opposite case and does refuse, because there the name
    ///     is the point.
    /// </summary>
    [Fact]
    public void A_second_new_folder_is_numbered_rather_than_refused() {
        using var fixture = new ProjectFixture();

        fixture.Add("Assets/Textures/hero.png");

        var project = Open(fixture);

        Assert.True(AssetOperations.CreateFolder(project, "Assets", "New Folder").Ok);
        Assert.True(AssetOperations.CreateFolder(project, "Assets", "New Folder").Ok);

        Assert.True(project.Assets.TryGetByPath("Assets/New Folder", out _));
        Assert.True(project.Assets.TryGetByPath("Assets/New Folder 2", out _));
    }

    /// <summary>
    ///     ⚠ <b>The reverse index is rebuilt, and it is the half that is easy to forget.</b> One
    ///     built against the previous scan answers "what breaks if I delete this" about assets that
    ///     have since moved — a wrong answer to the one question that must not have one.
    /// </summary>
    [Fact]
    public void The_reverse_index_is_rebuilt_after_an_operation() {
        using var fixture = new ProjectFixture();
        var hero = fixture.Add("Assets/Textures/hero.png");
        var scene = fixture.Add("Assets/Scenes/Main.vxscene", $"texture: vx:{hero}\n", importer: "SceneImporter");

        var project = Open(fixture);

        Assert.True(AssetOperations.Delete(project, scene).Ok);
        Assert.Empty(project.References.ReferrersOf(hero));
    }

    [Fact]
    public void An_asset_the_index_does_not_have_is_a_failure_rather_than_a_crash() {
        using var fixture = new ProjectFixture();

        fixture.Add("Assets/Textures/hero.png");

        var project = Open(fixture);
        var stranger = AssetId.New();

        Assert.False(AssetOperations.Rename(project, stranger, "whatever").Ok);
        Assert.False(AssetOperations.Move(project, stranger, "Assets").Ok);
        Assert.False(AssetOperations.Delete(project, stranger).Ok);
    }
}
