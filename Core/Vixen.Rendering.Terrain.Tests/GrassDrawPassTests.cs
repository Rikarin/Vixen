// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>The grass draw — [docs/plan/31 § T6]'s missing half.</summary>
/// <remarks>
///     ⚠ <b><see cref="GrassDispatch.RecordDraws" /> binds nothing, and for a long time nothing else
///     did either.</b> The dispatch's own tests pass over a field that records indirect draws with no
///     pipeline, no vertex buffer and no descriptor set bound — which on a real backend is a refused
///     draw and on this one is a command stream that looks complete. What is asserted here is
///     everything a draw needs being present beside the draw.
/// </remarks>
public sealed class GrassDrawPassTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    static GrassType Meadow => GrassType.Of("Meadow") with { Layer = "Grass", Density = 4f, Jitter = 0.8f };

    static FoliageCellGrid Grid => new(32f);

    static RenderOutput Output => new([PixelFormat.Rgba8UNorm], PixelFormat.Depth32Float);

    GrassDispatch Dispatch(int capacity = 8) =>
        new(
            device,
            device.CreateShader(ShaderStage.Compute, [1, 2, 3, 4], "grass.scatter.cs"),
            device.CreateShader(ShaderStage.Compute, [5, 6, 7, 8], "grass.arguments.cs"),
            capacity,
            256
        );

    GrassDrawPass Pass() =>
        new(
            device,
            new(
                device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "grass.vs"),
                device.CreateShader(ShaderStage.Fragment, [5, 6, 7, 8], "grass.fs")
            ),
            Output
        );

    GrassTerrainSource Source() =>
        new(
            device.CreateTextureView(
                device.CreateTexture(new(PixelFormat.R16UNorm, 64, 64, TextureUsage.Sampled, Name: "heights"))
            ),
            device.CreateTextureView(
                device.CreateTexture(new(PixelFormat.Rgba8UNorm, 64, 64, TextureUsage.Sampled, Name: "weights"))
            ),
            device.CreateTextureView(
                device.CreateTexture(new(PixelFormat.R8UNorm, 64, 64, TextureUsage.Sampled, Name: "holes"))
            ),
            device.CreateSampler(new()),
            device.CreateSampler(new()),
            device.CreateSampler(new()),
            WeightChannel: 1,
            HeightMapSize: new(64f, 64f),
            HeightRange: new(-100f, 100f),
            MetresPerQuad: 1f,
            Origin: Vector3.Zero
        );

    static GrassSlot[] Resident(int count) =>
        [.. Enumerable.Range(0, count).Select(index => new GrassSlot(new(index, 0), index))];

    static TerrainView View() {
        var at = new Vector3(10f, 5f, 10f);
        var view = Matrix4x4.LookAt(at, at + new Vector3(1f, -0.2f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI * 0.5f, 1f, 0.1f, 1000f);

        return new(view * projection, at, new(view * projection));
    }

    /// <summary>Every draw has a pipeline, a set, a vertex buffer and an index buffer beside it.</summary>
    [Fact]
    public void TheDrawIsBoundRatherThanIssuedIntoNothing() {
        using var dispatch = Dispatch();
        using var pass = Pass();

        dispatch.Prepare(Meadow, Grid, Resident(3), Source());
        pass.Prepare(device.BeginCommandList(), dispatch, View(), GrassWind.Default);

        device.Recorder!.Clear();

        var target = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 16, 16, TextureUsage.ColourTarget))
        );

        var commands = device.BeginCommandList();

        commands.BeginRenderPass(new([new(target)], name: "grass"));

        var draws = pass.Record(commands, dispatch);

        commands.EndRenderPass();
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        Assert.Equal(3, draws);
        Assert.Equal(3, device.Recorder.OfKind(RecordedCommandKind.DrawIndexedIndirect).Count);

        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindPipeline));
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindDescriptorSet));
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindVertexBuffer));
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindIndexBuffer));
    }

    /// <summary>The blade's index count reaches the indirect template.</summary>
    /// <remarks>
    ///     ⚠ <b>An indirect command whose <c>IndexCount</c> is zero draws nothing however many
    ///     instances survived the cull</b>, and every counter in the frame still reads healthy: the
    ///     scatter ran, the cells were resident, the draws were recorded. It is the one failure in this
    ///     path that is completely invisible from the host, which is why the pass writes the template
    ///     rather than trusting a caller to.
    /// </remarks>
    [Fact]
    public void ThePassWritesTheBladesIndexCountIntoTheIndirectTemplate() {
        using var dispatch = Dispatch();
        using var pass = Pass();

        Assert.Equal(0u, dispatch.Mesh.IndexCount);

        pass.Prepare(device.BeginCommandList(), dispatch, View(), GrassWind.Default);

        Assert.Equal((uint)pass.DefaultBladeIndices, dispatch.Mesh.IndexCount);
        Assert.True(pass.DefaultBladeIndices > 0);
    }

    /// <summary>A pass with no blade records nothing, and says which of the two it is.</summary>
    [Fact]
    public void APassWithNoBladeDrawsNothingAndSaysSo() {
        using var dispatch = Dispatch();
        using var pass = Pass();

        dispatch.Prepare(Meadow, Grid, Resident(3), Source());

        pass.Blade = default;

        Assert.False(pass.HasBlade);

        var commands = device.BeginCommandList();

        Assert.Equal(0, pass.Record(commands, dispatch));
    }

    /// <summary>The built-in blade bends rather than leaning, which is a segment count.</summary>
    [Fact]
    public void TheBuiltInBladeHasEnoughSegmentsToBend() {
        using var pass = Pass();

        // Three segments is six triangles is eighteen indices. One quad would be six.
        Assert.Equal(18, pass.DefaultBladeIndices);
    }
}
