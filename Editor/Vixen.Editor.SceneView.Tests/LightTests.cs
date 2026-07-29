// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Entities that light the scene: making them, saving them, and drawing what they reach.</summary>
public class LightTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-lights-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;

    public LightTests() {
        Directory.CreateDirectory(root);
        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
    }

    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_created_light_is_named_after_itself_and_carries_the_kind() {
        var spot = scene.CreateLight(LightKind.Spot, LocalTransform.Identity);

        Assert.Equal("Spot Light", scene.NameOf(spot));
        Assert.True(Lights.TryGet(world, spot, out var light));
        Assert.Equal(LightKind.Spot, light.Kind);
    }

    [Fact]
    public void A_new_light_is_one_you_can_see_by() {
        var point = scene.CreateLight(LightKind.Point, LocalTransform.Identity);

        Assert.True(Lights.TryGet(world, point, out var light));

        // ⚠ Not `default`. A zeroed light has no intensity and no reach, so a scene lit by one is a
        // black scene — and the command that made it looks like it did nothing at all.
        Assert.True(light.Intensity > 0f);
        Assert.True(light.Range > 0f);
        Assert.NotEqual(Color3.Black, light.Colour);
    }

    [Fact]
    public void A_sun_has_no_range_and_a_spot_has_a_cone() {
        var sun = Lights.Default(LightKind.Directional);
        var spot = Lights.Default(LightKind.Spot);

        // A directional light does not fall off, so a range on it would be a number that looks like
        // it does something.
        Assert.Equal(0f, sun.Range);

        Assert.True(spot.OuterAngle > spot.InnerAngle);
        Assert.True(spot.OuterAngle < MathF.PI * 0.5f);
    }

    [Fact]
    public void Creating_one_is_undoable_and_the_light_comes_back_with_it() {
        var lamp = scene.CreateLight(LightKind.Point, LocalTransform.Identity);

        Assert.True(scene.Stack.Undo());
        Assert.False(world.IsAlive(lamp));

        Assert.True(scene.Stack.Redo());

        // The redo restores a snapshot rather than running the initialiser again, so this says the
        // snapshot carries what the create command attached.
        Assert.True(Lights.TryGet(world, lamp, out var light));
        Assert.Equal(LightKind.Point, light.Kind);
    }

    [Fact]
    public void An_entity_with_no_light_has_none() {
        Assert.False(Lights.TryGet(world, scene.Add("Empty", LocalTransform.Identity), out _));
    }

    [Fact]
    public void Attaching_twice_changes_the_light_rather_than_adding_a_second_one() {
        var entity = scene.Add("Thing", LocalTransform.Identity);

        Lights.Attach(world, entity, LightKind.Point);
        Lights.Attach(world, entity, LightKind.Spot);

        Assert.True(Lights.TryGet(world, entity, out var light));
        Assert.Equal(LightKind.Spot, light.Kind);
    }

    [Fact]
    public void The_menu_offers_every_kind_exactly_once() {
        // A kind missing from the list is one nobody can create; one listed twice is two menu lines
        // that do the same thing and two commands registered under the same id.
        Assert.Equal(Enum.GetValues<LightKind>().Length, Lights.All.Count);
        Assert.Equal(Lights.All.Count, Lights.All.Distinct().Count());
    }

    [Fact]
    public void Every_kind_round_trips_through_its_name_and_reads_as_a_light_in_a_menu() {
        foreach (var kind in Lights.All) {
            Assert.True(Lights.TryParse(Lights.NameOf(kind), out var parsed));
            Assert.Equal(kind, parsed);

            // ⚠ The menu title is not the file name, which is the one place lights differ from
            // shapes: "Cube" is a complete answer and "Point" is not.
            Assert.EndsWith("Light", Lights.TitleOf(kind), StringComparison.Ordinal);
        }

        Assert.Equal("Area Light", Lights.TitleOf(LightKind.Rect));
    }

    [Fact]
    public void A_name_the_editor_does_not_know_is_not_a_light() {
        Assert.False(Lights.TryParse(null, out _));
        Assert.False(Lights.TryParse("   ", out _));
        Assert.False(Lights.TryParse("Bioluminescence", out _));

        // Case-insensitive, because a hand-edited file is the case this has to survive.
        Assert.True(Lights.TryParse("point", out var kind));
        Assert.Equal(LightKind.Point, kind);
    }

    [Fact]
    public void A_light_survives_a_file_round_trip_with_everything_on_it() {
        var spot = scene.CreateLight(LightKind.Spot, LocalTransform.At(new Vector3(1f, 4f, 2f)));

        Lights.Attach(
            world,
            spot,
            new Light {
                Kind = LightKind.Spot,
                Colour = new Color3(0.9f, 0.4f, 0.2f),
                Intensity = 3.5f,
                Range = 22f,
                Radius = 0.25f,
                InnerAngle = 0.3f,
                OuterAngle = 0.6f,
                HalfLength = 0.75f
            }
        );

        var yaml = SceneSerializer.ToYaml(scene);
        Assert.Contains("Spot", yaml, StringComparison.Ordinal);

        using var other = new World("Reloaded");
        var reloaded = new SceneDocument(project, other, AssetId.Empty, "Untitled");

        SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(yaml));

        Assert.True(Lights.TryGet(other, Assert.Single(reloaded.Roots), out var light));

        // ⚠ Every field, not just the kind. A light is seven numbers behind a name, and a round trip
        // that kept the name and dropped the cone would look right in the hierarchy and wrong in the
        // viewport — which is the shape of loss nobody notices until the scene is reopened.
        Assert.Equal(LightKind.Spot, light.Kind);
        Assert.Equal(0.9f, light.Colour.R, 3);
        Assert.Equal(0.4f, light.Colour.G, 3);
        Assert.Equal(0.2f, light.Colour.B, 3);
        Assert.Equal(3.5f, light.Intensity, 3);
        Assert.Equal(22f, light.Range, 3);
        Assert.Equal(0.25f, light.Radius, 3);
        Assert.Equal(0.3f, light.InnerAngle, 3);
        Assert.Equal(0.6f, light.OuterAngle, 3);
        Assert.Equal(0.75f, light.HalfLength, 3);
    }

    [Fact]
    public void An_entity_with_no_light_gains_none_on_the_way_back() {
        scene.Add("Empty", LocalTransform.Identity);

        using var other = new World("Reloaded");
        var reloaded = new SceneDocument(project, other, AssetId.Empty, "Untitled");

        SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene)));

        Assert.False(Lights.TryGet(other, Assert.Single(reloaded.Roots), out _));
    }

    [Fact]
    public void A_kind_the_file_names_and_this_editor_does_not_leaves_the_entity_in_place() {
        var file = new SceneFile();

        file.Roots.Add(
            new SceneEntityData {
                Name = "From the future",
                Light = new SceneLightData { Kind = "Bioluminescence", Intensity = 2f }
            }
        );

        using var other = new World("Reloaded");
        var reloaded = new SceneDocument(project, other, AssetId.Empty, "Untitled");

        // Opened, minus the lighting — the argument an unknown shape makes, where the cost of
        // refusing is somebody's whole scene.
        Assert.Equal(1, SceneSerializer.Load(reloaded, file));
        Assert.False(Lights.TryGet(other, Assert.Single(reloaded.Roots), out _));
    }

    [Fact]
    public void A_created_camera_can_actually_see() {
        var camera = scene.CreateCamera(LocalTransform.Identity);

        Assert.Equal("Camera", scene.NameOf(camera));
        Assert.True(world.Has<Camera>(camera));

        // ⚠ `Camera.Perspective` and not `default`, whose zero far plane makes every matrix built
        // from it degenerate.
        var settings = world.Read<Camera>(camera);

        Assert.True(settings.FieldOfView > 0f);
        Assert.True(settings.FarPlane > settings.NearPlane);
    }

    [Fact]
    public void A_camera_survives_a_file_round_trip_as_an_ordinary_component() {
        scene.CreateCamera(LocalTransform.At(new Vector3(0f, 2f, -8f)));

        var yaml = SceneSerializer.ToYaml(scene);

        // ⚠ Unlike a light, a camera is a *registered* runtime component, so it is written into the
        // entity's component list by its `[DataContract]` alias rather than into a key of the
        // editor's own. This asserts that difference is real: nothing about lights should have
        // pushed cameras out of the general path.
        Assert.Contains("!Camera", yaml, StringComparison.Ordinal);

        using var other = new World("Reloaded");
        var reloaded = new SceneDocument(project, other, AssetId.Empty, "Untitled");

        SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(yaml));

        Assert.True(other.Has<Camera>(Assert.Single(reloaded.Roots)));
    }

    [Fact]
    public void A_light_is_drawn_as_the_shape_it_reaches() {
        using var pane = new Pane();
        var lines = new SceneLines();

        scene.Add("Empty", LocalTransform.Identity);
        lines.Build(scene, pane.Viewport, 600);

        var markersOnly = lines.World.Count;

        scene.CreateLight(LightKind.Point, LocalTransform.Identity);
        lines.Build(scene, pane.Viewport, 600);

        // ⚠ Far more than the six vertices the marker cross costs, which is all the entity would get
        // for merely existing. A light is invisible, so without a gizmo which way a spot points and
        // how far a point light carries — the two things somebody placing lights is adjusting — are
        // not legible from the viewport at all.
        var drawn = lines.World.Count - markersOnly;

        Assert.True(drawn > 100, $"a light drew {drawn} vertices beyond its marker cross; expected a gizmo");
    }
}
