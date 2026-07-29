// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>The grid's spacing, placement, snapshots and play mode.</summary>
public class SceneTests {
    [Fact]
    public void The_grid_picks_round_numbers_and_gets_coarser_as_you_pull_back() {
        var grid = new SceneGrid();
        var near = new EditorCamera { Distance = 1f };
        var far = new EditorCamera { Distance = 1000f };

        var close = grid.Spacing(near, 800);
        var distant = grid.Spacing(far, 800);

        Assert.True(distant > close);

        foreach (var spacing in new[] { close, distant }) {
            // 1, 2 or 5 times a power of ten — the sequence people read as round.
            var decade = MathF.Pow(10f, MathF.Round(MathF.Log10(spacing)));
            var mantissa = spacing / MathF.Pow(10f, MathF.Floor(MathF.Log10(spacing)));

            Assert.True(
                MathF.Abs(mantissa - 1f) < 1e-3f
                || MathF.Abs(mantissa - 2f) < 1e-3f
                || MathF.Abs(mantissa - 5f) < 1e-3f
                || MathF.Abs(mantissa - 10f) < 1e-3f,
                $"{spacing} is not a round spacing (decade {decade})"
            );
        }
    }

    [Fact]
    public void The_grid_follows_the_camera_rather_than_running_out() {
        var grid = new SceneGrid { Extent = 4 };
        var camera = new EditorCamera { Pivot = new Vector3(10000f, 0f, 10000f), Distance = 10f };

        var lines = grid.Build(camera, 800);

        Assert.NotEmpty(lines);
        Assert.Contains(lines, line => MathF.Abs(line.From.X - 10000f) < 100f || MathF.Abs(line.From.Z - 10000f) < 100f);
    }

    [Fact]
    public void A_disabled_grid_draws_nothing() =>
        Assert.Empty(new SceneGrid { Enabled = false }.Build(new EditorCamera(), 800));

    [Fact]
    public void A_drop_with_nothing_under_it_lands_on_the_ground_plane() {
        var placement = new ScenePlacement();
        var camera = new EditorCamera { Distance = 10f };

        // Dragged downwards, which tips the scene away and puts the eye above it looking down — see
        // EditorCamera.Orbit.
        camera.Orbit(0f, 200f);

        Assert.True(camera.Position.Y > 0f);

        var result = placement.Resolve(camera.PickingRay(new Vector2(500f, 400f), 1000, 800));

        Assert.False(result.OnSurface);
        Assert.Equal(0f, result.Position.Y, 3);
    }

    [Fact]
    public void A_drop_with_the_ground_behind_the_camera_still_lands_in_front_of_it() {
        var placement = new ScenePlacement { FallbackDistance = 7f };

        // ⚠ Above the plane *and* looking up, which needs both a raised pivot and a positive pitch.
        // Pitch alone puts the eye underneath the plane, and a ray from under the ground still
        // crosses it going up — so the fallback below is never reached and the fixture asserts only
        // that a ground hit is in front of the camera, which it always is.
        var camera = new EditorCamera { Pivot = new Vector3(0f, 30f, 0f), Distance = 10f };

        camera.Orbit(0f, -200f);

        Assert.True(camera.Position.Y > 0f);
        Assert.True(camera.Forward.Y > 0f);

        var ray = camera.PickingRay(new Vector2(500f, 400f), 1000, 800);
        var result = placement.Resolve(ray);

        // Looking up at the sky: the ground plane is behind, and a drop still has to go somewhere the
        // user can see rather than at infinity.
        Assert.False(result.OnSurface);
        Assert.True(Vector3.Dot(result.Position - camera.Position, camera.Forward) > 0f);
        Assert.Equal(placement.FallbackDistance, (result.Position - ray.Origin).Length() / ray.Direction.Length(), 3);
    }

    [Fact]
    public void A_drop_onto_geometry_lands_on_it_and_only_aligns_when_asked() {
        var placement = new ScenePlacement();
        var camera = new EditorCamera { Distance = 10f };
        var probe = new StubProbe(new Vector3(1f, 2f, 3f), Vector3.Normalize(new Vector3(0f, 1f, 1f)));

        var flat = placement.Resolve(camera.PickingRay(new Vector2(500f, 400f), 1000, 800), probe);

        Assert.True(flat.OnSurface);
        Assert.Equal(new Vector3(1f, 2f, 3f), flat.Position);
        Assert.Equal(Quaternion.Identity, flat.Rotation);

        placement.Snap.SnapToSurface = true;
        var aligned = placement.Resolve(camera.PickingRay(new Vector2(500f, 400f), 1000, 800), probe);

        var up = Quaternion.Transform(Vector3.UnitY, aligned.Rotation);
        Assert.True(Vector3.NearEqual(up, probe.Normal, 1e-4f));
    }

    [Fact]
    public void A_snapshot_puts_back_the_entities_and_their_hierarchy() {
        using var world = new World("Scene");

        var parent = Hierarchy.CreateTransform(world, LocalTransform.At(new Vector3(1f, 0f, 0f)));
        var child = Hierarchy.CreateTransform(world, LocalTransform.At(new Vector3(0f, 2f, 0f)));

        Hierarchy.SetParent(world, child, parent);

        using var snapshot = WorldSnapshot.Capture(world);

        // Play mode: something moves an entity and spawns another.
        world.Get<LocalTransform>(parent).Position = new(99f, 99f, 99f);
        Hierarchy.CreateTransform(world, LocalTransform.Identity);

        var translation = snapshot.Restore(world);

        Assert.Equal(2, translation.Count);

        var restoredParent = translation[parent];
        var restoredChild = translation[child];

        Assert.Equal(new Vector3(1f, 0f, 0f), world.Read<LocalTransform>(restoredParent).Position);
        Assert.Equal(restoredParent, Hierarchy.ParentOf(world, restoredChild));

        // And the entity play mode spawned is gone, which is what the clear-first rule is for.
        var count = 0;

        foreach (var archetype in world.Archetypes) {
            count += archetype.EntityCount;
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public void A_selection_survives_a_play_stop_by_being_translated() {
        using var world = new World("Scene");
        var entity = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        using var play = new PlayModeController(world);

        Assert.True(play.Play());
        Assert.Equal(PlayState.Playing, play.State);

        var selection = play.Stop([entity]);

        Assert.Equal(PlayState.Editing, play.State);
        Assert.Single(selection);

        // A new handle, and one that names something that exists — an untranslated selection would
        // still hold the old handle and point at whatever landed in that slot.
        Assert.True(world.IsAlive(selection[0]));
    }

    [Fact]
    public void An_entity_play_mode_spawned_is_dropped_from_the_selection_rather_than_kept() {
        using var world = new World("Scene");
        using var play = new PlayModeController(world);

        play.Play();
        var spawned = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        Assert.Empty(play.Stop([spawned]));
    }

    [Fact]
    public void Pausing_stops_the_ticks_and_a_step_gives_back_exactly_one() {
        using var world = new World("Scene");
        using var play = new PlayModeController(world);

        play.Play();
        Assert.True(play.ShouldTick());

        play.Pause();
        Assert.False(play.ShouldTick());

        play.Step();
        Assert.True(play.ShouldTick());
        Assert.False(play.ShouldTick());

        play.Resume();
        Assert.True(play.ShouldTick());
    }

    [Fact]
    public void Stepping_from_stopped_starts_the_session_paused() {
        using var world = new World("Scene");
        using var play = new PlayModeController(world);

        play.Step();

        Assert.Equal(PlayState.Paused, play.State);
        Assert.True(play.ShouldTick());
        Assert.False(play.ShouldTick());
    }

    [Fact]
    public void Pressing_play_twice_does_not_restart_the_session() {
        using var world = new World("Scene");
        var entity = Hierarchy.CreateTransform(world, LocalTransform.At(new Vector3(1f, 0f, 0f)));

        using var play = new PlayModeController(world);

        play.Play();
        world.Get<LocalTransform>(entity).Position = new(5f, 0f, 0f);
        play.Play();

        var selection = play.Stop([entity]);

        // If the second press had re-snapshotted, stopping would restore the moved position.
        Assert.Equal(new Vector3(1f, 0f, 0f), world.Read<LocalTransform>(selection[0]).Position);
    }

    [Fact]
    public void A_player_s_command_line_is_something_a_person_can_read() {
        var arguments = PlayerSessions.ArgumentsFor(
            new PlayerLaunch(PlayerRole.Server, "Player", ["--scene", "Level1"]),
            34000
        );

        Assert.Equal(["--role", "server", "--inspector-port", "34000", "--scene", "Level1"], arguments);
    }

    sealed class StubProbe(Vector3 point, Vector3 normal) : ISurfaceProbe {
        public Vector3 Normal { get; } = normal;

        public bool Raycast(Ray ray, out SurfaceHit hit) {
            hit = new(point, Normal, 1f);
            return true;
        }
    }
}
