// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Terrain;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.App.Tests;

/// <summary>The ground, in the viewport — [docs/plan/31 § T2] reaching the editor.</summary>
/// <remarks>
///     ⚠ <b>Sculpting a heightfield you cannot see is the state this closes.</b> Every terrain tool
///     worked and the viewport drew a grid: the presenter is not the compositor, and the only modules
///     it had were the four standalone <c>.rvn</c> beside it. What made <c>Terrain.rvn</c> different
///     is that it <em>imports</em>, and a package's declarations are visible to a sibling only within
///     one compilation — so <c>./build.sh CheckShaders</c> compiles the import closure and commits the
///     bytes, and these are what assert they arrived.
/// </remarks>
public sealed class TerrainViewportTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    /// <summary>The two modules are embedded, which is what the build target produces.</summary>
    /// <remarks>
    ///     ⚠ <b>A missing module is a viewport with no terrain and no error anywhere.</b>
    ///     <c>EditorHost.TerrainModules</c> answers <see langword="default" /> rather than throwing,
    ///     because a working tree in which the target has never run should still start — so the only
    ///     thing that can notice the absence is a test that looks for the resource.
    /// </remarks>
    [Theory]
    [InlineData("Terrain.vert.spv")]
    [InlineData("Terrain.frag.spv")]
    public void The_terrain_modules_are_embedded_in_the_editor(string module) {
        var assembly = typeof(ScenePresenter).Assembly;

        var resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(entry => entry.EndsWith(module, StringComparison.Ordinal));

        Assert.NotNull(resource);

        using var stream = assembly.GetManifestResourceStream(resource)!;

        var bytes = new byte[4];

        Assert.Equal(4, stream.Read(bytes));

        // SPIR-V's magic number, little-endian. A module that is not one is a build step that wrote
        // something else into the resource — which the loader would hand to the driver.
        Assert.Equal<byte[]>([0x03, 0x02, 0x23, 0x07], bytes);
    }

    /// <summary>A terrain the scene supplies is uploaded and drawn.</summary>
    /// <remarks>
    ///     ⚠ <b>One draw for the whole terrain, and it is in the same pass as everything else.</b>
    ///     The claim [§ D3] makes is that a terrain is one indexed instanced call however many patches
    ///     it takes, and the presenter is where that either survives or turns into a draw per patch.
    /// </remarks>
    [Fact]
    public void A_terrain_the_scene_supplies_is_drawn() {
        using var presenter = Presenter();
        var scene = new OneTerrain(Built(), Vector3.Zero);

        presenter.TerrainScene = scene;

        presenter.UploadTerrain(device.BeginCommandList(), new EditorCamera(), 1f);

        Assert.Equal(1, presenter.TerrainsDrawn);

        device.Recorder!.Clear();
        Record(presenter);

        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.DrawIndexed));
    }

    /// <summary>A presenter with no modules draws no terrain and does not fail.</summary>
    [Fact]
    public void Without_the_modules_the_pane_still_draws() {
        using var presenter = Presenter(withTerrain: false);

        presenter.TerrainScene = new OneTerrain(Built(), Vector3.Zero);

        presenter.UploadTerrain(device.BeginCommandList(), new EditorCamera(), 1f);

        Assert.Equal(0, presenter.TerrainsDrawn);
    }

    /// <summary>Two entities naming one heightfield share a renderer, and therefore an atlas.</summary>
    /// <remarks>
    ///     ⚠ <b>A renderer per placement is a second copy of the whole heightfield on the device.</b>
    ///     The atlas is the largest thing a terrain owns, and two placements of one asset are two
    ///     entities rather than two terrains.
    /// </remarks>
    [Fact]
    public void Two_placements_of_one_terrain_upload_it_once() {
        using var presenter = Presenter();

        var map = Built();

        presenter.TerrainScene = new TwoPlacements(map);

        presenter.UploadTerrain(device.BeginCommandList(), new EditorCamera(), 1f);

        // Two draws, because there are two placements — and one renderer behind them, which is what
        // the second upload not creating a second atlas means.
        Assert.Equal(2, presenter.TerrainsDrawn);

        var renderers = (System.Collections.IDictionary)typeof(ScenePresenter)
            .GetField("terrains", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(presenter)!;

        Assert.True(renderers.Count == 1, $"{renderers.Count} renderers for one heightfield.");
    }

    ScenePresenter Presenter(bool withTerrain = true) {
        var presenter = new ScenePresenter(
            device,
            new Vixen.Rendering.LineShaders(Stage(ShaderStage.Vertex, "line.vs"), Stage(ShaderStage.Fragment, "line.fs")),
            new Vixen.Rendering.MeshShaders(Stage(ShaderStage.Vertex, "mesh.vs"), Stage(ShaderStage.Fragment, "mesh.fs")),
            new Vixen.Rendering.MeshInstanceShaders(Stage(ShaderStage.Vertex, "inst.vs"), Stage(ShaderStage.Fragment, "inst.fs")),
            PixelFormat.Rgba8UNorm
        );

        if (withTerrain) {
            presenter.TerrainStages = new(Stage(ShaderStage.Vertex, "terrain.vs"), Stage(ShaderStage.Fragment, "terrain.fs"));
        }

        return presenter;
    }

    ShaderHandle Stage(ShaderStage stage, string name) => device.CreateShader(stage, [1, 2, 3, 4], name);

    void Record(ScenePresenter presenter) {
        var target = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 16, 16, TextureUsage.ColourTarget))
        );

        var commands = device.BeginCommandList();

        commands.BeginRenderPass(new([new(target)], name: "scene"));
        presenter.RecordTerrain(commands);
        commands.EndRenderPass();
        commands.Finish();

        device.GraphicsQueue.Submit([commands]);
    }

    static TerrainMap Built() =>
        new(
            TerrainDescription.Default with {
                TileSamples = 32, TilesX = 2, TilesZ = 2,
                MetresPerQuad = 1f, MinHeight = -50f, MaxHeight = 50f
            }
        );

    sealed class OneTerrain(TerrainMap terrain, Vector3 origin) : ITerrainScene {
        public IReadOnlyList<(TerrainMap Terrain, Vector3 Origin)> Terrains() => [(terrain, origin)];
    }

    sealed class TwoPlacements(TerrainMap terrain) : ITerrainScene {
        public IReadOnlyList<(TerrainMap Terrain, Vector3 Origin)> Terrains() =>
            [(terrain, Vector3.Zero), (terrain, new(500f, 0f, 0f))];
    }
}
