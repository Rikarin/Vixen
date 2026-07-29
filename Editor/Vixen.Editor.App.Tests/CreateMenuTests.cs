// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Ui.Controls;
using Vixen.Editor.Testing;
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

    static IEnumerable<ContextMenu> Menus(EditorSession fixture) {
        foreach (var child in fixture.Document.Root.Children) {
            if (child is ContextMenu menu) {
                yield return menu;
            }
        }
    }
}
