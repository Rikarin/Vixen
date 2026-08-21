// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Terrain;
using Vixen.Editor.Terrain.Physics;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Vixen.Physics.Ecs;
using Vixen.Rendering.Terrain;
using Vixen.Terrain;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.App.Tests;

/// <summary>Whether the terrain tools are pointed at the scene in front of them.</summary>
/// <remarks>
///     ⚠ <b>Everything here was true of a build in which every panel test above passed.</b> The
///     panels registered, the buttons existed, the create form derived its numbers and the layer
///     stack said "No terrain" — and the mode never had a terrain unless you had made one in that
///     session, because nothing read a <c>.vxterrain</c> back. From the outside that is a toolset
///     where half the buttons do nothing, and no test that asks a panel what it says can see it.
/// </remarks>
public class TerrainSessionTests {
    /// <summary>A created terrain is still the one being sculpted several frames later.</summary>
    /// <remarks>
    ///     ⚠ <b>The frames are the test.</b> Create sets <c>TerrainEdit.Terrain</c> directly; what the
    ///     session does is decide, every frame afterwards, what it should be — so a session that read
    ///     the file back would replace the object the mode is holding, and a stroke made in between
    ///     would land on something nothing points at.
    /// </remarks>
    [Fact]
    public void A_created_terrain_survives_the_frames_after_it() {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.Shell.Modes.Activate(TerrainMode.ModeId));
        fixture.Frames(2);

        Created(fixture);
        fixture.Frames(4);

        Assert.Contains(
            Text(fixture.Panel(TerrainMode.PanelId)),
            line => line.Contains("Sculpt", StringComparison.Ordinal)
        );
    }

    /// <summary>An entity naming a terrain on disk is enough to sculpt it.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the reopen case, and it is the one that made the feature look unfinished.</b>
    ///     A scene saved with a terrain in it and opened again has an entity and a file and no
    ///     in-memory heightfield — and every tool in the mode is enabled from <c>HasTerrain</c>.
    /// </remarks>
    [Fact]
    public void An_entity_naming_a_terrain_on_disk_binds_the_tools_to_it() {
        using var fixture = EditorSession.Start();

        Write(fixture, "Terrain/Hill.vxterrain", Built());

        fixture.Scene.Create(
            "Hill",
            LocalTransform.Identity,
            default,
            entity => fixture.Scene.World.Add(entity, TerrainComponent.Of("Terrain/Hill.vxterrain"))
        );

        Assert.True(fixture.Shell.Modes.Activate(TerrainMode.ModeId));
        fixture.Frames(4);

        // The layer the file was written with, which only a read could know about.
        Assert.Contains(
            Text(fixture.Panel(TerrainMode.PanelId)),
            line => line.Contains("Bedrock", StringComparison.Ordinal)
        );

        // And the tools are reachable, which is what HasTerrain gates.
        Assert.True(fixture.Shell.Commands.Execute(TerrainMode.ToolCommand(TerrainTool.Smooth)));
    }

    /// <summary>The brush's origin follows the entity the terrain is placed by.</summary>
    /// <remarks>
    ///     ⚠ <b>A stale origin is a brush that paints at an offset from the pointer</b>, which reads
    ///     as the tool being inaccurate rather than as the transform not having been noticed — so it
    ///     is re-read every frame rather than at bind time.
    /// </remarks>
    [Fact]
    public void Moving_the_terrain_entity_moves_where_the_brush_thinks_the_ground_is() {
        using var fixture = EditorSession.Start();

        Write(fixture, "Terrain/Hill.vxterrain", Built());

        var entity = fixture.Scene.Create(
            "Hill",
            LocalTransform.Identity with { Position = new(10f, 0f, 20f) },
            default,
            created => fixture.Scene.World.Add(created, TerrainComponent.Of("Terrain/Hill.vxterrain"))
        );

        fixture.Frames(4);

        Assert.Equal(new Vector3(10f, 0f, 20f), Mode(fixture).Origin);

        fixture.Scene.World.Set(entity, LocalTransform.Identity with { Position = new(-5f, 0f, 7f) });
        fixture.Frames(2);

        Assert.Equal(new Vector3(-5f, 0f, 7f), Mode(fixture).Origin);
    }

    /// <summary>Two terrains and no selection is a refusal rather than a guess.</summary>
    /// <remarks>
    ///     ⚠ <b>Guessing is worse than refusing here.</b> A stroke aimed at one terrain and applied to
    ///     another is an edit somebody has to find and undo, and it would happen silently — where a
    ///     mode that says it has no terrain is a state the panel already explains.
    /// </remarks>
    [Fact]
    public void Two_terrains_and_no_selection_binds_neither() {
        using var fixture = EditorSession.Start();

        Write(fixture, "Terrain/A.vxterrain", Built());
        Write(fixture, "Terrain/B.vxterrain", Built());

        var first = default(Vixen.Core.Entity);

        foreach (var name in (string[]) ["A", "B"]) {
            var made = fixture.Scene.Create(
                name,
                LocalTransform.Identity,
                default,
                entity => fixture.Scene.World.Add(entity, TerrainComponent.Of($"Terrain/{name}.vxterrain"))
            );

            if (first == default) {
                first = made;
            }
        }

        fixture.Scene.Selection.Set([]);
        fixture.Frames(4);

        Assert.False(Mode(fixture).HasTerrain);

        // Selecting one resolves it, which is what the ambiguity was about.
        fixture.Scene.Selection.Set([first]);
        fixture.Frames(4);

        Assert.True(Mode(fixture).HasTerrain);
    }

    /// <summary>A scene naming a terrain that is not there reports once, not every frame.</summary>
    /// <remarks>
    ///     ⚠ <b>Sixty notifications a second is the same information as one and is unusable.</b> A
    ///     deleted asset is an ordinary state to meet, and the frame loop is what would repeat it.
    /// </remarks>
    [Fact]
    public void A_missing_terrain_file_is_reported_once() {
        using var fixture = EditorSession.Start();

        fixture.Scene.Create(
            "Gone",
            LocalTransform.Identity,
            default,
            entity => fixture.Scene.World.Add(entity, TerrainComponent.Of("Terrain/Gone.vxterrain"))
        );

        fixture.Frames(8);

        Assert.Single(
            fixture.Shell.Notifications.History,
            note => note.Message.Contains("could not be read", StringComparison.Ordinal)
        );
    }

    /// <summary>The foliage brush has a volume without anybody creating one.</summary>
    /// <remarks>
    ///     ⚠ <b><c>FoliageEdit.Volume</c> is what every foliage verb is enabled from</b>, and the
    ///     application never set it — so the palette said "add a .vxfoliage" and Add type was greyed
    ///     out, which is a mode that cannot be started from any direction.
    /// </remarks>
    [Fact]
    public void The_foliage_mode_has_a_volume_to_paint_into() {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.Shell.Modes.Activate(FoliageMode.ModeId));
        fixture.Frames(4);

        Assert.True(
            fixture.Shell.Commands.Execute(FoliageMode.AddTypeCommand),
            "Add Foliage Type is disabled, which means the mode has no volume."
        );
    }

    /// <summary>Adding an asset to the palette needs one selected, and says so when there is not.</summary>
    [Fact]
    public void Adding_a_palette_entry_with_nothing_selected_says_what_to_select() {
        using var fixture = EditorSession.Start();

        fixture.Open(FoliageMode.PanelId);

        var add = Button(fixture.Panel(FoliageMode.PanelId), "Add selected asset");

        Assert.NotNull(add);

        add.Activate();
        fixture.Frames(2);

        Assert.Contains(
            fixture.Shell.Notifications.History,
            note => note.Message.Contains("Nothing to add", StringComparison.Ordinal)
        );
    }

    /// <summary>The four content assets are on the Create menu and each has a command.</summary>
    /// <remarks>
    ///     ⚠ <b>A menu entry naming a command nothing registered is skipped in silence</b> —
    ///     <c>CreateMenuTests</c>' own remark — so an id that is not registered costs a line off the
    ///     menu and no error anywhere, which is indistinguishable from the feature never having been
    ///     added.
    /// </remarks>
    [Theory]
    [InlineData("assets.create-terrain-layer")]
    [InlineData("assets.create-foliage")]
    [InlineData("assets.create-grass")]
    [InlineData("assets.create-spline")]
    public void The_terrain_asset_kinds_are_creatable(string id) {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.Shell.Commands.TryGet(id, out _), $"{id} is on the menu and is not registered");
    }

    /// <summary>And what they create is a file the importer accepts rather than an empty one.</summary>
    /// <remarks>
    ///     ⚠ <b>The empty-file bargain the other eight kinds make does not hold for these.</b> Those
    ///     are opened by a document that reads a zero-byte file as a sensible new one; a
    ///     <c>.vxspline</c> is read by an importer, and one with fewer than two control points is an
    ///     <em>error</em> — so an empty one arrives in the project already broken.
    /// </remarks>
    [Fact]
    public void A_created_spline_has_the_two_points_a_curve_needs() {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.Shell.Commands.Execute("assets.create-spline"));
        fixture.Frames(2);

        var file = Directory
            .EnumerateFiles(Path.Combine(fixture.ProjectRoot, "Assets"), "*.vxspline", SearchOption.AllDirectories)
            .FirstOrDefault();

        Assert.NotNull(file);

        var text = File.ReadAllText(file);

        Assert.Contains("points:", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split("- position").Length - 1);
    }

    /// <summary>A grass rule entered into a palette is derived, whatever the palette is told.</summary>
    /// <remarks>
    ///     ⚠ <b>Stored would write a million blades into the file beside the scene</b> — a cache of
    ///     something the scatter regenerates from its hash — which is exactly what
    ///     <c>FoliageStore.Persisted</c> exists to refuse.
    /// </remarks>
    [Fact]
    public void A_grass_type_becomes_a_derived_palette_entry() {
        var type = Vixen.Foliage.GrassType.Of("Meadow") with { Layer = "Grass", Density = 16f, MinWeight = 0.2f };
        var entry = type.ToFoliageType();

        Assert.Equal(Vixen.Foliage.FoliageStorage.Derived, entry.Storage);
        Assert.Equal("Meadow", entry.Name);
        Assert.Equal("Grass", entry.LayerFilter);
        Assert.Equal(0.2f, entry.LayerThreshold, 5);

        // Sixteen candidates a square metre is a quarter-metre square each.
        Assert.Equal(0.25f, entry.Radius, 5);
    }

    /// <summary>A stroke in a live editor reaches the collision rebuilder the editor publishes.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The defect this test exists for is an interface nothing implements <em>and</em>
    ///         nothing assigns.</b> <c>TerrainEdit.Colliders</c> was set in five test files and
    ///         nowhere else in the tree, so every assertion about a stroke naming the right tiles was
    ///         an assertion about a double talking to another double. This is the same claim made of
    ///         the editor the product actually starts.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the thing published is a switch, not a rebuilder.</b>
    ///         <c>TerrainModule.BindColliders</c> resolves the service in its per-frame follow and
    ///         keeps the first answer, so the object it binds has to outlive every play session — a
    ///         per-session adapter would leave the sculpt tools holding a disposed Jolt world for
    ///         every stroke after the first Stop. <see cref="PlayColliders.Idle" /> is what the
    ///         editing half of that costs: a stroke that reached the seam and had no simulation to
    ///         rebuild in.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_stroke_reaches_the_colliders_the_editor_publishes() {
        using var fixture = EditorSession.Start();

        Write(fixture, "Terrain/Hill.vxterrain", Built());

        fixture.Scene.Create(
            "Hill",
            LocalTransform.Identity,
            default,
            entity => fixture.Scene.World.Add(entity, TerrainComponent.Of("Terrain/Hill.vxterrain"))
        );

        Assert.True(fixture.Shell.Modes.Activate(TerrainMode.ModeId));
        fixture.Frames(4);

        var mode = Mode(fixture);

        Assert.True(mode.HasTerrain);

        var published = Assert.IsType<PlayColliders>(mode.Editing.Colliders);

        mode.Editing.Brush.Radius = 4f;
        mode.Editing.Brush.Strength = 1f;
        mode.Editing.Tools.Metres = 5f;

        // Well inside the low tile, so the brush cannot reach the boundary at sample 31.
        Assert.True(mode.Editing.Begin(new(12f, 12f)));
        Assert.NotNull(mode.Editing.Commit());

        // ⚠ Nothing is playing, so there is no physics world and the stroke rebuilds nothing — which
        // is `ITerrainColliders`' own "a terrain with no collision, not an error", and is also the
        // shape of "physics belongs to play, not to editing". The count is the proof the call
        // arrived; a seam nothing reaches is indistinguishable from one that had nothing to do.
        Assert.Equal(1, published.Idle);
        Assert.Equal(0, published.Missed);
    }

    /// <summary>Pressing Play builds the ground's collision, and a stroke rebuilds the tile it moved.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the end of <c>docs/plan/31</c> § D10's chain, and every earlier link was
    ///         built and unreachable.</b> <c>TerrainColliderSystem</c> had tests that dropped a body
    ///         onto a height field; <c>TerrainColliders</c> had tests that sculpted one. Neither could
    ///         see the thing that was actually wrong, which is that the editor constructed no physics
    ///         world at all — so this asserts about the *editor*: press Play, and the ground it draws
    ///         has bodies under it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="TerrainColliderSystem.Rebuilds" /> and not <c>TileCount</c> alone.</b>
    ///         A tile count says the first build happened, which the poll would have done on the next
    ///         frame anyway. What the seam adds is *when* — the frame the artist let go of the mouse —
    ///         and a rebuild counted between two ticks is the only way to see that.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Playing_gives_the_ground_collision_and_a_stroke_rebuilds_it() {
        using var fixture = EditorSession.Start();

        Write(fixture, "Terrain/Hill.vxterrain", Built());

        fixture.Scene.Create(
            "Hill",
            LocalTransform.Identity,
            default,
            entity => fixture.Scene.World.Add(entity, TerrainComponent.Of("Terrain/Hill.vxterrain"))
        );

        Assert.True(fixture.Shell.Modes.Activate(TerrainMode.ModeId));
        fixture.Frames(4);

        var mode = Mode(fixture);

        Assert.True(mode.HasTerrain);

        fixture.Run("play.play");
        fixture.Frames(2);

        var session = fixture.Editor.PlayMode.Session;

        Assert.NotNull(session);
        Assert.Empty(fixture.Editor.PlayMode.Refused);
        Assert.Equal(["physics", "terrain collision"], session.Running);

        // The simulation the terrain's bodies were created in, provided by the application rather
        // than stood up a second time by the terrain module.
        Assert.True(session.TryGet<PhysicsScene>(out var scene));
        Assert.NotNull(scene);

        var colliders = Assert.IsType<PlayColliders>(mode.Editing.Colliders);

        mode.Editing.Brush.Radius = 4f;
        mode.Editing.Brush.Strength = 1f;
        mode.Editing.Tools.Metres = 5f;

        Assert.True(mode.Editing.Begin(new(12f, 12f)));
        Assert.NotNull(mode.Editing.Commit());

        // ⚠ Zero, and it is the number that says the wiring is right. `Missed` climbs when a stroke
        // names a terrain the collider system has never heard of — which is what an
        // `ITerrainPlacements` that does not list the ground being sculpted looks like, and it has no
        // other symptom.
        Assert.Equal(0, colliders.Missed);
        Assert.Equal(0, colliders.Idle);

        // Four tiles of ground, with bodies under them, in the world the person is looking at.
        Assert.True(scene.BodyCount >= 4, $"the terrain has {scene.BodyCount} bodies under it");

        fixture.Run("play.stop");
        fixture.Frames(2);

        // ⚠ And stopping takes all of it away: the simulation is disposed, the tile entities went
        // with the snapshot's restore, and the seam the tools hold is pointed at nothing again. A
        // physics world that survived here would be one simulating under the next gizmo drag.
        Assert.Null(fixture.Editor.PlayMode.Session);
        Assert.True(scene.IsDisposed);

        Assert.True(mode.Editing.Begin(new(12f, 12f)));
        Assert.NotNull(mode.Editing.Commit());
        Assert.Equal(1, colliders.Idle);
    }

    static TerrainMode Mode(EditorSession fixture) =>
        (TerrainMode)fixture.Shell.Modes.Modes.Single(mode => mode.Id == TerrainMode.ModeId);

    /// <summary>A terrain small enough to write in a test, with a layer whose name is recognisable.</summary>
    static byte[] Built() {
        var terrain = new TerrainMap(
            TerrainDescription.Default with {
                TileSamples = 32, TilesX = 2, TilesZ = 2,
                MetresPerQuad = 1f, MinHeight = -50f, MaxHeight = 50f
            }
        );

        terrain.AddLayer("Bedrock");

        return TerrainStore.Write(terrain);
    }

    static void Write(EditorSession fixture, string relative, byte[] bytes) {
        var path = Path.Combine(fixture.ProjectRoot, "Assets", relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    static void Created(EditorSession fixture) {
        var made = Button(fixture.Panel(TerrainMode.PanelId), "Create terrain");

        Assert.NotNull(made);

        made.Activate();
        fixture.Frames(2);
    }

    static Button? Button(UiElement root, string label) =>
        Descendants(root).OfType<Button>().FirstOrDefault(button => button.Label == label);

    static List<string> Text(UiElement root) =>
        [.. Descendants(root).Select(element => element.Text ?? string.Empty).Where(text => text.Length > 0)];

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
