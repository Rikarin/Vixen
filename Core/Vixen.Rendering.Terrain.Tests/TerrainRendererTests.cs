// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering.Terrain;
using Vixen.Terrain;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>
///     The terrain renderer, against a recording device — [docs/plan/31 § T2].
/// </summary>
/// <remarks>
///     What a headless device can say: how many draws, how many instances, what was bound and how
///     many bytes moved. What it cannot say is whether the result looks like ground, which is the
///     golden image and needs a GPU.
/// </remarks>
public sealed class TerrainRendererTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    static TerrainDescription Shape(int tiles = 2) =>
        TerrainDescription.Default with {
            TileSamples = 32, TilesX = tiles, TilesZ = tiles,
            MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
        };

    TerrainRenderer Build(TerrainMap terrain, int gridQuads = 8) =>
        new(
            device,
            terrain,
            new(
                device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "terrain.vs"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "terrain.fs")
            ),
            new([PixelFormat.Rgba8UNorm], PixelFormat.Depth32Float),
            TerrainLodRanges.Default with { NearRange = 32f },
            gridQuads
        );

    static TerrainView View(Vector3 at) {
        var view = Matrix4x4.LookAt(at, at + new Vector3(1f, -0.5f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI * 0.9f, 1f, 0.1f, 100_000f);

        return new(view * projection, at, new(view * projection));
    }

    ICommandList Record() => device.BeginCommandList();

    /// <summary>Draws a frame into a throwaway target, and submits it.</summary>
    /// <remarks>
    ///     ⚠ <b>The submit is what makes the recorder see anything.</b> A null command list buffers
    ///     its own calls and flushes them into the <c>CommandRecorder</c> at submit rather than as it
    ///     records — which is deliberate, because lists record on their own threads. A test that
    ///     asserted before submitting would be asserting on an empty stream and would pass for a
    ///     renderer that drew nothing.
    /// </remarks>
    void Draw(TerrainRenderer renderer) {
        var target = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 16, 16, TextureUsage.ColourTarget))
        );

        var commands = device.BeginCommandList();
        commands.BeginRenderPass(new([new(target)], name: "terrain"));
        renderer.Record(commands);
        commands.EndRenderPass();
        commands.Finish();

        device.GraphicsQueue.Submit([commands]);
    }

    [Fact]
    public void ATerrainIsOneDrawHoweverManyPatchesItTakes() {
        var terrain = new TerrainMap(Shape(tiles: 2));
        using var renderer = Build(terrain);

        var commands = Record();
        var patches = renderer.Upload(commands, View(new(10f, 40f, 10f)));

        Assert.True(patches > 1, $"only {patches} patches were selected.");

        device.Recorder!.Clear();
        Draw(renderer);

        var draw = Assert.Single(device.Recorder.OfKind(RecordedCommandKind.DrawIndexed));

        Assert.Equal(1, renderer.Draws);
        Assert.Equal(TerrainGridPatch.IndexCount(8), (int)draw.A);
        Assert.Equal(patches, (int)draw.B);
    }

    [Fact]
    public void ItBindsNoVertexBufferBecauseTheLatticeIsTheVertexIndex() {
        var terrain = new TerrainMap(Shape());
        using var renderer = Build(terrain);

        var commands = Record();
        renderer.Upload(commands, View(new(10f, 40f, 10f)));

        device.Recorder!.Clear();
        Draw(renderer);

        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.BindVertexBuffer));
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindIndexBuffer));
    }

    [Fact]
    public void ItBindsOneDescriptorSetForTheWholeTerrain() {
        var terrain = new TerrainMap(Shape());
        using var renderer = Build(terrain);

        var commands = Record();
        renderer.Upload(commands, View(new(10f, 40f, 10f)));

        device.Recorder!.Clear();
        Draw(renderer);

        var bind = Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindDescriptorSet));
        Assert.Equal((long)DescriptorSetSlot.PerMaterial, bind.A);
    }

    [Fact]
    public void ATerrainNothingCanSeeDrawsNothing() {
        var terrain = new TerrainMap(Shape());
        using var renderer = Build(terrain);

        // Looking away from the terrain, from a long way off.
        var at = new Vector3(-10_000f, 500f, -10_000f);
        var view = Matrix4x4.LookAt(at, at - new Vector3(1f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 8f, 1f, 0.1f, 500f);

        var commands = Record();
        Assert.Equal(0, renderer.Upload(commands, new(view * projection, at, new(view * projection))));

        device.Recorder!.Clear();
        Draw(renderer);

        Assert.Equal(0, renderer.Draws);
        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.DrawIndexed));
    }

    /// <summary>
    ///     A frame in which nothing was sculpted copies no heights.
    /// </summary>
    /// <remarks>
    ///     The claim that makes editing affordable. Uploading the whole heightmap per frame would put
    ///     a 33 MB transfer on a 4 km² terrain behind every mouse move.
    /// </remarks>
    [Fact]
    public void AFrameThatChangedNothingUploadsNoHeights() {
        var terrain = new TerrainMap(Shape());
        using var renderer = Build(terrain);

        var commands = Record();

        // The first frame stages the whole thing, because nothing has been uploaded yet.
        renderer.Upload(commands, View(new(10f, 40f, 10f)));
        Assert.True(renderer.UploadedBytes > 0);

        renderer.Upload(commands, View(new(11f, 40f, 11f)));
        Assert.Equal(0, renderer.UploadedBytes);
    }

    /// <summary>
    ///     A stroke on one tile moves that tile's rows, not the terrain's.
    /// </summary>
    [Fact]
    public void AStrokeOnOneTileUploadsAFractionOfTheHeightmap() {
        var terrain = new TerrainMap(Shape(tiles: 4));
        var layer = terrain.AddLayer("Sculpt");
        using var renderer = Build(terrain);

        var commands = Record();
        renderer.Upload(commands, View(new(10f, 40f, 10f)));

        var whole = renderer.UploadedBytes;
        Assert.True(whole > 0);

        TerrainSculpt.Sculpt(
            terrain, layer,
            TerrainBrush.Default with { Radius = 3f, Strength = 1f },
            new(new(8f, 8f)), 10f
        );

        renderer.Upload(commands, View(new(10f, 40f, 10f)));

        Assert.True(renderer.UploadedBytes > 0, "the stroke moved nothing.");
        Assert.True(
            renderer.UploadedBytes < whole / 2,
            $"a one-tile stroke moved {renderer.UploadedBytes} of {whole} bytes."
        );
    }

    [Fact]
    public void TheTriangleCountIsThePatchesTimesTheLattice() {
        var terrain = new TerrainMap(Shape());
        using var renderer = Build(terrain, gridQuads: 8);

        var commands = Record();
        var patches = renderer.Upload(commands, View(new(10f, 40f, 10f)));

        Assert.Equal(patches * 8 * 8 * 2, renderer.Triangles);
    }

    [Fact]
    public void TheRecordBufferGrowsRatherThanDroppingPatches() {
        var terrain = new TerrainMap(Shape(tiles: 4));
        using var renderer = Build(terrain, gridQuads: 8);

        var commands = Record();
        var patches = renderer.Upload(commands, View(new(120f, 20f, 120f)));

        Assert.True(renderer.Capacity >= patches);
        Assert.Equal(patches, renderer.PatchCount);
    }

    [Fact]
    public void AShaderlessRendererIsRefused() {
        var terrain = new TerrainMap(Shape());

        Assert.Throws<ArgumentException>(
            () => new TerrainRenderer(
                device,
                terrain,
                default,
                new([PixelFormat.Rgba8UNorm], PixelFormat.Depth32Float),
                TerrainLodRanges.Default
            )
        );
    }

    [Fact]
    public void DisposingTwiceIsHarmless() {
        var terrain = new TerrainMap(Shape());
        var renderer = Build(terrain);

        renderer.Dispose();
        renderer.Dispose();
    }
}
