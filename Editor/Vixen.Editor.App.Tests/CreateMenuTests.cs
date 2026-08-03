// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Mathematics;
using Vixen.Editor.Assets;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>What the hierarchy's Create menu offers, and whether the lines on it do anything.</summary>
/// <remarks>
///     ⚠ <b>A menu entry naming a command nothing registered is skipped in silence.</b> That is
///     deliberate — it is what lets the shell name <c>file.save</c> without owning it — and it means
///     a typo in an id, or a command registered after the menu was described, costs a line off the
///     menu and no error anywhere. From the outside that is indistinguishable from the feature never
///     having been added, so it is worth an assertion per line rather than one that the menu exists.
/// </remarks>
public class CreateMenuTests {
    [Fact]
    public void Every_kind_of_thing_the_menu_offers_has_a_command_behind_it() {
        using var fixture = EditorSession.Start();
        var commands = fixture.Shell.Commands;

        Assert.True(commands.TryGet("scene.create-entity", out _));
        Assert.True(commands.TryGet("scene.create-camera", out _));

        foreach (var kind in PrimitiveShapes.All) {
            var id = "scene.create-" + PrimitiveShapes.NameOf(kind).ToLowerInvariant();
            Assert.True(commands.TryGet(id, out _), $"{id} is on the menu and is not registered");
        }

        foreach (var kind in Lights.All) {
            var id = "scene.create-light-" + Lights.NameOf(kind).ToLowerInvariant();
            Assert.True(commands.TryGet(id, out _), $"{id} is on the menu and is not registered");
        }
    }

    [Fact]
    public void The_hierarchy_menu_groups_the_creatable_things_into_submenus() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");
        fixture.Frames(2);

        // ⚠ The hierarchy's, not "the only one". The toolbar's dropdowns are context menus too and
        // they hang off the document root for the same reason this one does — so "the single context
        // menu in the tree" stopped being an identity the moment the toolbar grew a section.
        var menu = Assert.Single(Menus(fixture), static candidate => candidate.Items.Any(item => item.Label == "3D Object"));
        var labels = menu.Items.Select(static item => item.Label).ToList();

        Assert.Contains("3D Object", labels);
        Assert.Contains("Light", labels);
        Assert.Contains("Camera", labels);

        // ⚠ Grouped rather than listed flat. Thirteen create lines beside Rename and Delete is a
        // menu where the two destructive entries are somewhere in the middle of a wall of nouns.
        var lights = Assert.Single(menu.Items, static item => item.Label == "Light").Submenu;

        Assert.NotNull(lights);
        Assert.Equal(Lights.All.Count, lights.Items.Count);
        Assert.Equal(Lights.All.Select(Lights.TitleOf), lights.Items.Select(static item => item.Label));

        // And a line that opens a menu says so, which is the only thing distinguishing it from one
        // that commits to something.
        Assert.Contains(
            Assert.Single(menu.Items, static item => item.Label == "3D Object").Children,
            static child => child.HasClass("submenu")
        );
    }

    [Fact]
    public void Creating_a_light_from_its_command_puts_a_real_light_in_the_scene() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");
        Assert.True(fixture.Shell.Commands.Execute("scene.create-light-spot"));

        fixture.Frames(2);

        var scene = fixture.Scene;
        var created = Assert.Single(scene.Selection);

        Assert.Equal("Spot Light", scene.NameOf(created));
        Assert.True(Lights.TryGet(scene.World, created, out var light));
        Assert.Equal(LightKind.Spot, light.Kind);

        // ⚠ Aimed rather than left at the identity. A spot at the identity points along +Z, which is
        // a cone lying flat in the floor — and that reads as the command having done nothing.
        Assert.NotEqual(Quaternion.Identity, scene.World.Read<LocalTransform>(created).Rotation);
    }

    [Fact]
    public void Creating_a_camera_from_its_command_puts_one_that_can_see_in_the_scene() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");
        Assert.True(fixture.Shell.Commands.Execute("scene.create-camera"));

        fixture.Frames(2);

        var scene = fixture.Scene;
        var created = Assert.Single(scene.Selection);

        Assert.True(scene.World.Has<Camera>(created));
        Assert.True(scene.World.Read<Camera>(created).FarPlane > 0f);
    }

    /// <summary>Every file kind doc 34 introduced can be made from the Assets menu.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The check that was missing while three of these had editors and no way to make a
    ///         file of one.</b> Registering an editor and forgetting the Create line leaves a format
    ///         that opens perfectly and that nobody can ever get a file of — and nothing anywhere says
    ///         so, because both halves are individually correct.
    ///     </para>
    ///     <para>
    ///         The starter text is checked too, and by the real importer: a template that produced a
    ///         file with an import error would be a Create line that greets somebody with a red mark
    ///         beside the thing they have just made.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("assets.create-move-set", ".vxmoveset")]
    [InlineData("assets.create-proxy-shapes", ".vxproxyshapes")]
    [InlineData("assets.create-shape-vocabulary", ".vxshapevocab")]
    [InlineData("assets.create-priorities", ".vxpriorities")]
    [InlineData("assets.create-constraint-template", ".vxconstraints")]
    public async Task An_animation_asset_kind_is_creatable_and_imports_clean(string command, string extension) {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.CanRun(command), command);
        fixture.Run(command).Settle();

        var path = Assert.Single(
            Directory.EnumerateFiles(fixture.Project.Paths.Assets, "*" + extension, SearchOption.AllDirectories)
        );

        Assert.DoesNotContain(
            await Import(path),
            entry => entry.Severity is ImportSeverity.Error or ImportSeverity.Warning
        );
    }

    /// <summary>
    ///     ⚠ <b>A new harness is the one that imports with an error, and the error is the
    ///     instructions.</b> A plan naming no clip and no rig is a build step that always passes,
    ///     which is worse than one that says what it is missing — so the two lines an author has to
    ///     fill in are the two the importer complains about the moment the file exists.
    /// </summary>
    [Fact]
    public async Task A_new_harness_says_what_it_is_missing() {
        using var fixture = EditorSession.Start();

        fixture.Run("assets.create-harness").Settle();

        var path = Assert.Single(
            Directory.EnumerateFiles(fixture.Project.Paths.Assets, "*.vxharness", SearchOption.AllDirectories)
        );

        Assert.Contains(
            await Import(path),
            entry => entry.Severity == ImportSeverity.Error
                && entry.Message.Contains("always passes", StringComparison.Ordinal)
        );
    }

    /// <summary>Runs the real importer over a created file, so the starter text is checked by it.</summary>
    static async Task<IReadOnlyList<ImportDiagnostic>> Import(string path) {
        var importers = BuiltInImporters.Create();

        Assert.True(importers.TryGetForFile(path, out var importer), $"nothing imports '{path}'");

        var virtualPath = new VirtualPath("/Assets/" + Path.GetFileName(path));
        var files = new MemoryFileProvider();

        files.Seed(virtualPath, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        var context = new ImportContext(
            AssetId.New(),
            virtualPath,
            importer!.CreateSettings(),
            files,
            importer.Name,
            "Windows"
        );

        return (await importer.ImportAsync(context, TestContext.Current.CancellationToken)).Diagnostics;
    }

    static IEnumerable<ContextMenu> Menus(EditorSession fixture) {
        foreach (var child in fixture.Document.Root.Children) {
            if (child is ContextMenu menu) {
                yield return menu;
            }
        }
    }
}
