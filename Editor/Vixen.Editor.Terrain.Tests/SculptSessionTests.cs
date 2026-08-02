// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;
using Vixen.Terrain;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>A document with an undo stack and nothing else, which is all a stroke needs.</summary>
/// <remarks>
///     ⚠ <b>Not a <c>SceneDocument</c>, and the difference is the point.</b> A terrain stroke changes
///     a heightfield and pushes one entry; it does not touch an entity, a mesh or a component, so
///     driving the exit criterion through a whole scene would be testing the scene. What is needed is
///     a real <see cref="CommandStack" /> — with its merging, its redo truncation and its ordering —
///     and <see cref="EditorDocument" /> is what has one.
/// </remarks>
sealed class SculptDocument(EditorProject project) : EditorDocument(project, AssetId.New(), "Terrain") {
    /// <inheritdoc />
    /// <remarks>Nothing: what writing a <c>.vxterrain</c> is belongs to the asset database, which
    ///     this test does not have and the exit criterion does not name.</remarks>
    protected override void SaveCore() {
    }
}

/// <summary>
///     [docs/plan/31 § T3]'s exit criterion, run end to end.
/// </summary>
/// <remarks>
///     "An artist creates a terrain, sculpts a valley, erodes a ridge, adds an edit layer, flattens a
///     building pad on it, hides the layer, shows it, undoes eight strokes and redoes them — and can
///     walk on the result in play mode." Every clause of that is a step below; the last one is the
///     collider rebuild, which this assembly can only assert the <em>call</em> of — see
///     <see cref="ITerrainColliders" />.
/// </remarks>
public sealed class SculptSessionTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-tests", Guid.NewGuid().ToString("N"));
    readonly SculptDocument document;
    readonly EditorShell shell;
    readonly TerrainMode mode;

    public SculptSessionTests() {
        var paths = new ProjectPaths(root);

        Directory.CreateDirectory(paths.Assets);

        document = new(new EditorProject(paths));
        shell = new(1280f, 800f);
        mode = new() { Document = document };

        shell.Modes.Add(new SelectMode());
        shell.Modes.Add(mode);
        shell.Modes.Changed += modes => shell.Context = modes.Context ?? "scene";
        shell.Modes.Activate(TerrainMode.ModeId);
    }

    /// <inheritdoc />
    public void Dispose() {
        shell.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_artist_builds_a_valley_a_ridge_and_a_building_pad_and_can_take_it_all_back() {
        var colliders = new RecordingColliders();

        mode.Editing.Colliders = colliders;

        // --- Creates a terrain ----------------------------------------------
        mode.Create.TileSamples = 64;
        mode.Create.TilesX = 2;
        mode.Create.TilesZ = 2;
        mode.Create.MetresPerQuad = 1f;
        mode.Create.MinHeight = -100f;
        mode.Create.MaxHeight = 100f;

        TerrainMap? made = null;
        mode.Created += terrain => made = terrain;

        Assert.True(mode.Create.IsValid, mode.Create.Validate());
        Assert.True(shell.Commands.Execute(TerrainMode.CreateCommand));

        var ground = made!;
        var rest = ground.Description.HeightOf(ground.Description.StoreHeight(0f));

        Assert.Same(ground, mode.Editing.Terrain);
        Assert.Equal(126f, ground.Description.WidthX, 1);

        var sculpt = mode.Editing.Layer!;

        mode.Editing.Brush.Radius = 10f;
        mode.Editing.Brush.Strength = 1f;
        mode.Editing.Brush.Falloff = 1f;

        // --- Sculpts a valley -----------------------------------------------
        Assert.True(shell.Commands.Execute(TerrainMode.ToolCommand(TerrainTool.Sculpt)));
        mode.Editing.Tools.Metres = 12f;

        mode.Editing.Begin(new(20f, 60f), invert: true);

        for (var x = 24; x <= 60; x += 4) {
            mode.Editing.Extend(new(x, 60f));
        }

        Assert.NotNull(mode.Commit());
        Assert.True(Height(ground, 40, 60) < -5f, "the valley should be well below the plain.");

        // --- Erodes a ridge --------------------------------------------------
        mode.Tool = TerrainTool.Sculpt;
        mode.Editing.Tools.Metres = 30f;
        mode.Editing.Begin(new(80f, 60f));
        mode.Commit();

        var summit = Height(ground, 80, 60);

        Assert.True(shell.Commands.Execute(TerrainMode.ToolCommand(TerrainTool.Erosion)));
        mode.Editing.Tools.Talus = 0.1f;
        mode.Editing.Tools.Iterations = 6;
        mode.Editing.Begin(new(80f, 60f));
        mode.Commit();

        Assert.True(Height(ground, 80, 60) < summit, "the ridge should have lost material to its slope.");

        // --- Adds an edit layer ----------------------------------------------
        Assert.True(shell.Commands.Execute(TerrainMode.AddLayerCommand));

        var pads = mode.Editing.Layer!;

        Assert.Equal(2, ground.Layers.Count);
        Assert.NotSame(sculpt, pads);

        // --- Flattens a building pad on it -----------------------------------
        Assert.True(shell.Commands.Execute(TerrainMode.ToolCommand(TerrainTool.Flatten)));
        mode.Editing.Brush.Radius = 8f;
        mode.Editing.Tools.PickTarget = false;
        mode.Editing.Tools.FlattenTarget = 4f;

        mode.Editing.Begin(new(40f, 60f));
        mode.Commit();

        Assert.Equal(4f, Height(ground, 40, 60), 0);

        // ⚠ On the layer above, so the valley below it survives. This is [§ D4]'s whole argument: a
        // kernel that wrote the composite would have flattened the valley away.
        Assert.False(pads.IsEmpty);

        // --- Hides the layer, and shows it -----------------------------------
        var hidden = TerrainLayerCommands.SetVisible(ground, pads, false);
        document.Stack.Execute(hidden);

        Assert.False(pads.IsVisible);
        Assert.True(Height(ground, 40, 60) < -5f, "hiding the pad should show the valley under it.");

        document.Stack.Undo();

        Assert.True(pads.IsVisible);
        Assert.Equal(4f, Height(ground, 40, 60), 0);

        // --- Eight strokes, undone and redone --------------------------------
        mode.Tool = TerrainTool.Sculpt;
        mode.Editing.Tools.Metres = 3f;
        mode.Editing.Brush.Radius = 6f;

        var before = Snapshot(ground);
        var depth = document.Stack.Depth.Value;

        for (var index = 0; index < 8; index++) {
            mode.Editing.Begin(new(20f + (index * 10f), 20f));
            mode.Editing.Extend(new(24f + (index * 10f), 24f));
            mode.Commit();
        }

        Assert.Equal(depth + 8, document.Stack.Depth.Value);

        var after = Snapshot(ground);
        Assert.NotEqual(before, after);

        for (var index = 0; index < 8; index++) {
            Assert.True(document.Stack.Undo(), $"undo {index + 1} of 8 refused.");
        }

        ground.Resolve();
        Assert.Equal(before, Snapshot(ground));

        for (var index = 0; index < 8; index++) {
            Assert.True(document.Stack.Redo(), $"redo {index + 1} of 8 refused.");
        }

        ground.Resolve();
        Assert.Equal(after, Snapshot(ground));

        // --- And it can be walked on ------------------------------------------
        // Every stroke named the tiles it touched, which is what a play session rebuilds its Jolt
        // height fields from. Four tiles, because the strokes crossed the middle of the terrain.
        Assert.Equal(4, colliders.Tiles.Count);

        // The pad is where the artist put it, and it is what the collider would be built from.
        Assert.Equal(4f, TerrainPick.HeightAt(ground, 40f, 60f), 0);

        Assert.True(
            TerrainPick.Cast(ground, new(40f, 500f, 60f), -Vector3.UnitY, out var landing),
            "a ray straight down at the pad should meet it."
        );

        Assert.Equal(4f, landing.Position.Y, 0);
        Assert.True(landing.Position.Y > rest, "the pad should stand above the plain it was cut into.");
    }

    static float Height(TerrainMap terrain, int x, int z) {
        terrain.Resolve();
        return terrain.Composite.MetresAt(x, z);
    }

    static float[] Snapshot(TerrainMap terrain) {
        var heights = new float[terrain.Description.SampleCount];
        var at = 0;

        for (var z = 0; z < terrain.Description.SamplesZ; z++) {
            for (var x = 0; x < terrain.Description.SamplesX; x++) {
                heights[at++] = terrain.Composite.MetresAt(x, z);
            }
        }

        return heights;
    }
}
