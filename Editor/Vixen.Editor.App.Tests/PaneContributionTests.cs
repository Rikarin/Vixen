// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Terrain;
using Vixen.Water;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.App.Tests;

/// <summary>A pane draws whatever the editor's registry holds now, not what it held when the pane was made.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The host used to copy the three scene seams onto a pane once, when it made the
///         pane</b> — <c>presenter.TerrainScene = editor.TerrainScene</c> and its two siblings. So a
///         module unloaded after that went on being drawn by every open pane, and the pane's
///         reference was what kept the unloaded module reachable: <c>WaterSceneSource</c> holds its
///         module. The reach tests could not see it, because <c>EditorSession</c> has no host and no
///         pane; this is the pane half (#1286, #1287, #1299).
///     </para>
///     <para>
///         ⚠ <b>Set by hand still wins</b>, because that is how every viewport test hands a pane a
///         scene and it is a fair thing for a tool to do.
///     </para>
/// </remarks>
public sealed class PaneContributionTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    [Fact]
    public void The_pane_follows_the_registrys_scenes_through_an_unload_and_a_reload() {
        using var presenter = Presenter();
        var registry = new EditorRegistry();

        presenter.Extensions = registry;

        var terrain = new NoTerrain();
        var vegetation = new NoVegetation();
        var water = new NoWater();

        var removals = new[] { registry.Add<ITerrainScene>(terrain), registry.Add<IVegetationScene>(vegetation), registry.Add<IWaterScene>(water) };

        Assert.Same(terrain, presenter.TerrainScene);
        Assert.Same(vegetation, presenter.VegetationScene);
        Assert.Same(water, presenter.WaterScene);

        // The module unloads.
        foreach (var removal in removals) {
            removal.Dispose();
        }

        Assert.Null(presenter.TerrainScene);
        Assert.Null(presenter.VegetationScene);
        Assert.Null(presenter.WaterScene);

        // And a fresh copy of it activates.
        var again = new NoTerrain();

        using var back = registry.Add<ITerrainScene>(again);

        Assert.Same(again, presenter.TerrainScene);
    }

    [Fact]
    public void A_scene_set_by_hand_wins_over_the_registry() {
        using var presenter = Presenter();
        var registry = new EditorRegistry();

        presenter.Extensions = registry;

        using var contributed = registry.Add<ITerrainScene>(new NoTerrain());

        var mine = new NoTerrain();

        presenter.TerrainScene = mine;

        Assert.Same(mine, presenter.TerrainScene);
    }

    ScenePresenter Presenter() {
        ShaderHandle Stage(ShaderStage stage, string name) => device.CreateShader(stage, [1, 2, 3, 4], name);

        return new ScenePresenter(
            device,
            new LineShaders(Stage(ShaderStage.Vertex, "line.vs"), Stage(ShaderStage.Fragment, "line.fs")),
            new MeshShaders(Stage(ShaderStage.Vertex, "mesh.vs"), Stage(ShaderStage.Fragment, "mesh.fs")),
            new MeshInstanceShaders(Stage(ShaderStage.Vertex, "inst.vs"), Stage(ShaderStage.Fragment, "inst.fs")),
            PixelFormat.Rgba8UNorm
        );
    }

    sealed class NoTerrain : ITerrainScene {
        public IReadOnlyList<(TerrainMap Terrain, Vector3 Origin)> Terrains() => [];
    }

    sealed class NoVegetation : IVegetationScene {
        public FoliageVolume Foliage() => new(new(32f));

        public Vixen.Core.AssetReference MeshOf(string reference) => default;
    }

    sealed class NoWater : IWaterScene {
        public Spline? SplineFor(string name, in Matrix4x4 placement) => null;

        public WaterWaveSpectrum? SpectrumFor(string name) => null;

        public float GroundAt(Vector2 ground) => 0f;
    }
}
