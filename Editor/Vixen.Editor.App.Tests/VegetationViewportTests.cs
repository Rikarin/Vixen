// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Terrain;
using Vixen.Terrain;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.App.Tests;

/// <summary>The painted vegetation, in the viewport — the half of the terrain toolset it was not showing.</summary>
/// <remarks>
///     ⚠ <b>Painting a forest you cannot see is the state these close.</b> The foliage brush wrote
///     instances into a volume and the grass types were rules over the painted weights, and the
///     presenter drew only the ground — so the editor that authors vegetation showed none of it.
///     Foliage goes through the CPU cull into the viewport's own instanced pipeline; grass goes
///     through the same device scatter the runtime uses, whose modules <c>./build.sh CheckShaders</c>
///     commits beside the terrain's.
/// </remarks>
public sealed class VegetationViewportTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    /// <summary>The grass modules are embedded, which is what the build target produces.</summary>
    /// <remarks>
    ///     <c>TerrainViewportTests</c>' own check, over the five grass files:
    ///     <c>EditorHost.GrassModules</c> answers <see langword="default" /> rather than throwing, so
    ///     the only thing that can notice an absence is a test that looks for the resource.
    /// </remarks>
    [Theory]
    [InlineData("Grass.vert.spv")]
    [InlineData("Grass.frag.spv")]
    [InlineData("GrassScatter.comp.spv")]
    [InlineData("GrassScatterUnbound.comp.spv")]
    [InlineData("GrassScatterArguments.comp.spv")]
    public void The_grass_modules_are_embedded_in_the_editor(string module) {
        var assembly = typeof(EditorModules).Assembly;

        var resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(entry => entry.EndsWith(module, StringComparison.Ordinal));

        Assert.NotNull(resource);

        using var stream = assembly.GetManifestResourceStream(resource)!;

        var bytes = new byte[4];

        Assert.Equal(4, stream.Read(bytes));

        // SPIR-V's magic number, little-endian.
        Assert.Equal<byte[]>([0x03, 0x02, 0x23, 0x07], bytes);
    }

    /// <summary>A painted stand is culled and drawn through the instanced pipeline.</summary>
    /// <remarks>
    ///     ⚠ <b>With no terrain anywhere in the scene</b>, deliberately: foliage paints onto any
    ///     surface, so a viewport with no heightfield still has to show the stand.
    /// </remarks>
    [Fact]
    public void Painted_foliage_is_culled_and_drawn() {
        using var presenter = Presenter();

        var volume = new FoliageVolume(new(32f));
        var oak = volume.AddType(FoliageType.Of("Oak") with { Mesh = "Meshes/oak.vxmesh" });

        // Clustered at the camera's pivot, where the default view is looking — a spread stand would
        // also test the frustum cull, which is `FoliageRendererTests`' business rather than this one's.
        for (var index = 0; index < 8; index++) {
            volume.Add(oak, new(new(index * 0.25f, 0f, 0f), Quaternion.Identity, 1f));
        }

        presenter.VegetationScene = new PaintedScene(volume);
        presenter.Surfaces.Meshes = new OneMesh();

        presenter.UploadTerrain(device.BeginCommandList(), new EditorCamera(), 1f);

        Assert.Equal(8, presenter.Vegetation.FoliageDrawn);
        Assert.Equal(0, presenter.Vegetation.FoliageWaiting);

        device.Recorder!.Clear();
        Record(presenter);

        // One instanced call for the whole stand: eight instances of one registered mesh.
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.DrawIndexed));
    }

    /// <summary>A type whose mesh has not arrived is counted as waiting rather than drawn wrong.</summary>
    [Fact]
    public void A_type_with_no_resident_mesh_waits() {
        using var presenter = Presenter();

        var volume = new FoliageVolume(new(32f));
        var oak = volume.AddType(FoliageType.Of("Oak") with { Mesh = "Meshes/oak.vxmesh" });

        volume.Add(oak, new(Vector3.Zero, Quaternion.Identity, 1f));

        presenter.VegetationScene = new PaintedScene(volume);
        presenter.Surfaces.Meshes = new NoMeshes();

        presenter.UploadTerrain(device.BeginCommandList(), new EditorCamera(), 1f);

        Assert.Equal(0, presenter.Vegetation.FoliageDrawn);
        Assert.Equal(1, presenter.Vegetation.FoliageWaiting);
    }

    /// <summary>A derived grass type over a terrain records the scatter and its indirect draws.</summary>
    /// <remarks>
    ///     The device path, end to end on a recording device: residency brings cells in around the
    ///     camera, the dispatch scatters them outside the render pass, and the pass draws one
    ///     indirect command per covered cell — the same pair the runtime uses.
    /// </remarks>
    [Fact]
    public void Grass_with_resident_cells_records_the_dispatch_and_the_draws() {
        using var presenter = Presenter(withGrass: true);

        var volume = new FoliageVolume(new(32f));

        // Through the type's own projection, which is how the palette actually receives a rule.
        volume.AddType(GrassType.Of("Meadow").ToFoliageType());

        presenter.TerrainScene = new OneTerrain(Built(), Vector3.Zero);
        presenter.VegetationScene = new PaintedScene(volume);

        // ⚠ Finished and submitted, because the recorder receives a list's commands at submit —
        // asserting on an open list counts nothing however much was recorded into it.
        var upload = device.BeginCommandList();

        presenter.UploadTerrain(upload, new EditorCamera(), 1f);
        upload.Finish();
        device.GraphicsQueue.Submit([upload]);

        Assert.True(presenter.Vegetation.GrassCells > 0, "no cells came resident around the camera");

        // Two dispatches per lane: the scatter and the argument phase.
        Assert.Equal(2, device.Recorder!.OfKind(RecordedCommandKind.Dispatch).Count);

        device.Recorder.Clear();
        Record(presenter);

        var draws = device.Recorder.OfKind(RecordedCommandKind.DrawIndexedIndirect);

        Assert.Equal(presenter.Vegetation.GrassCells, draws.Count);
    }

    /// <summary>Without the grass modules the same scene draws no grass and does not fail.</summary>
    [Fact]
    public void Without_the_modules_the_pane_still_draws() {
        using var presenter = Presenter();

        var volume = new FoliageVolume(new(32f));

        volume.AddType(GrassType.Of("Meadow").ToFoliageType());

        presenter.TerrainScene = new OneTerrain(Built(), Vector3.Zero);
        presenter.VegetationScene = new PaintedScene(volume);

        presenter.UploadTerrain(device.BeginCommandList(), new EditorCamera(), 1f);

        Assert.Equal(0, presenter.Vegetation.GrassCells);
    }

    /// <summary>A scene with nothing painted records nothing and throws nothing.</summary>
    [Fact]
    public void An_empty_scene_records_no_vegetation() {
        using var presenter = Presenter(withGrass: true);

        presenter.TerrainScene = new OneTerrain(Built(), Vector3.Zero);
        presenter.VegetationScene = new PaintedScene(new(new(32f)));

        var upload = device.BeginCommandList();

        presenter.UploadTerrain(upload, new EditorCamera(), 1f);
        upload.Finish();
        device.GraphicsQueue.Submit([upload]);

        Assert.Equal(0, presenter.Vegetation.FoliageDrawn);
        Assert.Equal(0, presenter.Vegetation.GrassCells);
        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.Dispatch));

        device.Recorder.Clear();
        Record(presenter);

        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.DrawIndexedIndirect));
    }

    /// <summary>And a pane with no vegetation seam at all is the pane the editor always had.</summary>
    [Fact]
    public void No_seam_is_no_vegetation_and_no_error() {
        using var presenter = Presenter(withGrass: true);

        presenter.UploadTerrain(device.BeginCommandList(), new EditorCamera(), 1f);

        Assert.Equal(0, presenter.Vegetation.FoliageDrawn);
        Assert.Equal(0, presenter.Vegetation.GrassCells);
    }

    ScenePresenter Presenter(bool withGrass = false) {
        var presenter = new ScenePresenter(
            device,
            new LineShaders(Stage(ShaderStage.Vertex, "line.vs"), Stage(ShaderStage.Fragment, "line.fs")),
            new MeshShaders(Stage(ShaderStage.Vertex, "mesh.vs"), Stage(ShaderStage.Fragment, "mesh.fs")),
            new MeshInstanceShaders(Stage(ShaderStage.Vertex, "inst.vs"), Stage(ShaderStage.Fragment, "inst.fs")),
            PixelFormat.Rgba8UNorm
        );

        presenter.TerrainStages = new(
            Stage(ShaderStage.Vertex, "terrain.vs"),
            Stage(ShaderStage.Fragment, "terrain.fs")
        );

        if (withGrass) {
            presenter.GrassStages = new(
                new(Stage(ShaderStage.Vertex, "grass.vs"), Stage(ShaderStage.Fragment, "grass.fs")),
                Stage(ShaderStage.Compute, "grass.scatter.cs"),
                Stage(ShaderStage.Compute, "grass.unbound.cs"),
                Stage(ShaderStage.Compute, "grass.arguments.cs")
            );
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
        presenter.Vegetation.Record(commands);
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

    sealed class PaintedScene(FoliageVolume volume) : IVegetationScene {
        public FoliageVolume Foliage() => volume;

        public AssetReference MeshOf(string reference) =>
            reference.Length > 0 ? new(AssetId.Parse("00000000-0000-0000-0000-0000000000aa"), default) : default;
    }

    /// <summary>A source with one mesh: a triangle, which is all a recording device needs.</summary>
    sealed class OneMesh : IMeshSource {
        public bool TryGet(AssetReference reference, out MeshData mesh) {
            mesh = new() {
                Name = "oak",
                Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
                Normals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
                Indices = [0, 1, 2]
            };

            return true;
        }
    }

    /// <summary>A source still loading everything, which is every source's first answer.</summary>
    sealed class NoMeshes : IMeshSource {
        public bool TryGet(AssetReference reference, out MeshData mesh) {
            mesh = null!;
            return false;
        }
    }
}
