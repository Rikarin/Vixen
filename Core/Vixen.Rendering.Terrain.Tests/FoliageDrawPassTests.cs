// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>The foliage draw — [docs/plan/31 § T5]'s missing half.</summary>
/// <remarks>
///     ⚠ <b><see cref="FoliageCullPass" /> patches indirect commands, and for a long time nothing
///     consumed them.</b> The cull's own tests pass over dispatches whose survivors no draw ever
///     read — a complete two-phase compaction feeding nothing. What is asserted here is the other
///     half: a pipeline, a set and a mesh beside every draw, one draw per level per batch, and the
///     honest counters for a mesh that has not arrived.
/// </remarks>
public sealed class FoliageDrawPassTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    static FoliageType Tree =>
        FoliageType.Of("Tree") with {
            Mesh = "vx:9e8a44c9930c64e388ca034c5fe4c426",
            Radius = 2f,
            StartCullDistance = 180f,
            EndCullDistance = 200f
        };

    static RenderOutput Output => new([PixelFormat.Rgba8UNorm], PixelFormat.Depth32Float);

    static BoundingFrustum Everything() =>
        new(
            Matrix4x4.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
            * Matrix4x4.PerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.1f, 4000f)
        );

    static (FoliageVolume Volume, int Type) Filled(int count, float z = -100f) {
        var volume = new FoliageVolume(new(32f));
        var type = volume.AddType(Tree);

        for (var index = 0; index < count; index++) {
            volume.Add(type, new(new(((index % 16) - 8) * 2f, 0f, z), Quaternion.Identity, 1f));
        }

        return (volume, type);
    }

    FoliageCullPass Cull() =>
        new(
            device,
            device.CreateShader(ShaderStage.Compute, [1, 2, 3, 4], "cull.count.cs"),
            device.CreateShader(ShaderStage.Compute, [5, 6, 7, 8], "cull.place.cs"),
            4096,
            64
        );

    FoliageDrawPass Pass() =>
        new(
            device,
            new(
                device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "foliage.vs"),
                device.CreateShader(ShaderStage.Fragment, [5, 6, 7, 8], "foliage.fs")
            ),
            Output
        );

    FoliageMesh Pine() =>
        FoliageDrawPass.UploadMesh(
            device,
            [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
            [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            [0, 1, 2],
            "pine"
        );

    static FoliageDraw Draws(int type, FoliageMesh mesh, params float[] distances) =>
        new(
            type,
            [.. Enumerable.Range(0, distances.Length + 1)
                .Select(level => new DrawCommand { IndexCount = (uint)mesh.IndexCount })],
            distances
        );

    static TerrainView View() {
        var projection = Matrix4x4.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
            * Matrix4x4.PerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.1f, 4000f);

        return new(projection, Vector3.Zero, new(projection));
    }

    /// <summary>Runs one whole frame: upload, prepare both passes, dispatch, then the draws.</summary>
    int Frame(FoliageCullPass cull, FoliageDrawPass pass, IReadOnlyDictionary<int, FoliageMesh> meshes) {
        cull.Prepare(Everything(), Vector3.Zero);

        var commands = device.BeginCommandList();

        pass.Prepare(commands, cull, View());
        cull.Record(commands);

        var target = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 16, 16, TextureUsage.ColourTarget))
        );

        commands.BeginRenderPass(new([new(target)], name: "foliage"));

        var draws = pass.Record(commands, cull, meshes);

        commands.EndRenderPass();
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        return draws;
    }

    /// <summary>Every draw has a pipeline, a set, a vertex buffer and an index buffer beside it.</summary>
    [Fact]
    public void TheDrawIsBoundRatherThanIssuedIntoNothing() {
        using var cull = Cull();
        using var pass = Pass();
        var (volume, type) = Filled(64);
        var pine = Pine();

        cull.Upload(volume, [Draws(type, pine)]);
        device.Recorder!.Clear();

        var draws = Frame(cull, pass, new Dictionary<int, FoliageMesh> { [type] = pine });

        // One cell of one type at one level: one indirect draw, and the two cull dispatches.
        Assert.Equal(cull.BatchCount, draws);
        Assert.Equal(draws, device.Recorder.OfKind(RecordedCommandKind.DrawIndexedIndirect).Count);
        Assert.Equal(2, device.Recorder.OfKind(RecordedCommandKind.Dispatch).Count);

        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindVertexBuffer));
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindIndexBuffer));
        Assert.Equal(0, pass.MissingMeshes);
    }

    /// <summary>A batch draws one indirect command per declared level, empty ones included.</summary>
    /// <remarks>
    ///     The cull's own contract read back through the draw: a level with no survivors kept the
    ///     host's zero instance count and is still issued, which is what lets level N's range sit at
    ///     slot N with nothing read back.
    /// </remarks>
    [Fact]
    public void EveryDeclaredLevelIsIssued() {
        using var cull = Cull();
        using var pass = Pass();
        var (volume, type) = Filled(32);
        var pine = Pine();

        cull.Upload(volume, [Draws(type, pine, 50f, 120f)]);

        var draws = Frame(cull, pass, new Dictionary<int, FoliageMesh> { [type] = pine });

        Assert.Equal(cull.BatchCount * 3, draws);
        Assert.Equal(3u, cull.BatchOf(0).LevelCount);
    }

    /// <summary>A type whose mesh has not arrived is skipped and counted, not defaulted.</summary>
    /// <remarks>
    ///     ⚠ <b>There is no honest stand-in for a tree</b> — the grass's built-in blade exists
    ///     because every blade is the same shape, and no such fact exists here. What matters is that
    ///     the skip is a number: a forest whose pines have not loaded and one that culled to nothing
    ///     record alike, and only one of them fixes itself next frame.
    /// </remarks>
    [Fact]
    public void ABatchWithNoMeshIsSkippedAndCounted() {
        using var cull = Cull();
        using var pass = Pass();
        var (volume, type) = Filled(32);
        var pine = Pine();

        cull.Upload(volume, [Draws(type, pine)]);

        var draws = Frame(cull, pass, new Dictionary<int, FoliageMesh>());

        Assert.Equal(0, draws);
        Assert.Equal(cull.BatchCount, pass.MissingMeshes);
        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexedIndirect));
    }

    /// <summary>Nothing uploaded is nothing recorded, with every counter saying so.</summary>
    [Fact]
    public void AnEmptyVolumeDrawsNothing() {
        using var cull = Cull();
        using var pass = Pass();
        var pine = Pine();

        cull.Upload(new FoliageVolume(new(32f)), []);
        device.Recorder!.Clear();

        var draws = Frame(cull, pass, new Dictionary<int, FoliageMesh> { [0] = pine });

        Assert.Equal(0, draws);
        Assert.Equal(0, pass.MissingMeshes);
        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.DrawIndexedIndirect));
        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.Dispatch));
    }

    /// <summary>Consecutive frames write different ring slots, descriptor set and all.</summary>
    /// <remarks>
    ///     ⚠ <b><c>device.Write</c> is an immediate memcpy</b> — a single constant block or set
    ///     rewritten each Prepare is what the frame still in flight is reading.
    /// </remarks>
    [Fact]
    public void ConsecutiveFramesUseDifferentSlots() {
        using var cull = Cull();
        using var pass = Pass();
        var (volume, type) = Filled(16);
        var pine = Pine();
        var meshes = new Dictionary<int, FoliageMesh> { [type] = pine };

        cull.Upload(volume, [Draws(type, pine)]);

        var sets = new long[3];

        for (var frame = 0; frame < sets.Length; frame++) {
            device.Recorder!.Clear();
            Frame(cull, pass, meshes);

            sets[frame] = device.Recorder.Commands
                .Last(entry => entry.Kind == RecordedCommandKind.BindDescriptorSet)
                .B;
        }

        Assert.NotEqual(sets[0], sets[1]);

        // The ring is FramesInFlight wide, and the null device's is two.
        Assert.Equal(sets[0], sets[2]);
    }

    /// <summary>The upload folds a placement into the instances, and the bounds follow it.</summary>
    /// <remarks>
    ///     A volume placed at an origin is culled where it stands: the same cells that were visible
    ///     at the world origin are invisible once the volume moves behind the camera, with no other
    ///     change anywhere.
    /// </remarks>
    [Fact]
    public void AnOriginMovesTheVolumeForTheCull() {
        using var cull = Cull();
        var (volume, type) = Filled(32, z: -100f);
        var pine = Pine();

        // In front of the camera where painted: visible.
        cull.Upload(volume, [Draws(type, pine)]);
        Assert.True(cull.Prepare(Everything(), Vector3.Zero) > 0);

        // The same volume placed 500 m behind it: nothing survives the first stage.
        cull.Upload(volume, [Draws(type, pine)], new(0f, 0f, 600f));
        Assert.Equal(0, cull.Prepare(Everything(), Vector3.Zero));
    }

    /// <summary>The quality tier's distance scale reaches the batch records.</summary>
    [Fact]
    public void TheDistanceScaleReachesTheBatches() {
        using var cull = Cull();
        var (volume, type) = Filled(16);
        var pine = Pine();

        cull.Upload(volume, [Draws(type, pine)]);
        cull.Prepare(Everything(), Vector3.Zero, distanceScale: 0.5f);

        Assert.Equal(Tree.StartCullDistance * 0.5f, cull.BatchOf(0).StartCullDistance);
        Assert.Equal(Tree.EndCullDistance * 0.5f, cull.BatchOf(0).EndCullDistance);

        // The LOD thresholds stay authored — a tier that pulls the horizon in must not reshuffle
        // which mesh a nearby tree draws as.
        cull.Upload(volume, [Draws(type, pine, 50f)]);
        cull.Prepare(Everything(), Vector3.Zero, distanceScale: 0.5f);

        Assert.Equal(50f, cull.BatchOf(0).Lod0);
    }

    /// <summary>The cull says which palette type a batch is, for the draw that binds its mesh.</summary>
    [Fact]
    public void TheCullNamesEachBatchsType() {
        using var cull = Cull();
        var volume = new FoliageVolume(new(32f));
        var pineType = volume.AddType(Tree);
        var rockType = volume.AddType(Tree with { Name = "Rock" });
        var pine = Pine();

        volume.Add(pineType, new(new(0f, 0f, -100f), Quaternion.Identity, 1f));
        volume.Add(rockType, new(new(0f, 0f, -100f), Quaternion.Identity, 1f));

        cull.Upload(volume, [Draws(pineType, pine), Draws(rockType, pine)]);

        Assert.Equal(2, cull.BatchCount);
        Assert.NotEqual(cull.TypeOf(0), cull.TypeOf(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => cull.TypeOf(2));
    }

    /// <summary>An empty mesh is refused at upload, loudly.</summary>
    [Fact]
    public void AnEmptyMeshIsRefused() {
        Assert.Throws<ArgumentException>(
            () => FoliageDrawPass.UploadMesh(device, [], [], [], [0, 1, 2])
        );

        Assert.Throws<ArgumentException>(
            () => FoliageDrawPass.UploadMesh(device, [Vector3.Zero], [], [], [])
        );
    }

    [Fact]
    public void AShaderWithNoStageIsRefused() {
        var vertex = device.CreateShader(ShaderStage.Vertex, [1], "foliage.vs");

        Assert.Throws<ArgumentException>(() => new FoliageDrawPass(device, new(vertex, default), Output));
    }

    [Fact]
    public void UsingItAfterDisposalIsRefused() {
        using var cull = Cull();
        var pass = Pass();

        pass.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => pass.Prepare(device.BeginCommandList(), cull, View())
        );

        Assert.Throws<ObjectDisposedException>(
            () => pass.Record(device.BeginCommandList(), cull, new Dictionary<int, FoliageMesh>())
        );
    }
}
