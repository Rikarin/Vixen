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
            Origin: Vector3.Zero,
            TileSamples: 64f,
            TileQuads: 63f,
            AtlasTiles: new(1f, 1f)
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

        dispatch.Prepare(Meadow, Grid, Resident(3), Source(), pass.MeshTemplate);
        pass.Prepare(device.BeginCommandList(), dispatch, View(), GrassWind.Breeze);

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

    /// <summary>The blade's index count reaches the indirect template, whatever the call order.</summary>
    /// <remarks>
    ///     ⚠ <b>An indirect command whose <c>IndexCount</c> is zero draws nothing however many
    ///     instances survived the cull</b>, and every counter in the frame still reads healthy: the
    ///     scatter ran, the cells were resident, the draws were recorded. It is the one failure in this
    ///     path that is completely invisible from the host, which is why the template rides
    ///     <see cref="GrassDispatch.Prepare" /> as a parameter — computed from the blade at the moment
    ///     it is read — rather than being patched onto the dispatch in an order nothing enforced.
    /// </remarks>
    [Fact]
    public void TheBladesIndexCountReachesTheIndirectTemplate() {
        using var dispatch = Dispatch();
        using var pass = Pass();

        Assert.True(pass.DefaultBladeIndices > 0);
        Assert.Equal((uint)pass.DefaultBladeIndices, pass.MeshTemplate.IndexCount);

        // Baked by the dispatch's own Prepare, with no draw-pass Prepare having run at all.
        dispatch.Prepare(Meadow, Grid, Resident(1), Source(), pass.MeshTemplate);

        Assert.Equal((uint)pass.DefaultBladeIndices, dispatch.Mesh.IndexCount);
    }

    /// <summary>The default albedo's texels are uploaded on the first frame, once.</summary>
    /// <remarks>
    ///     ⚠ <b>A texture created <c>Undefined</c> and never written samples as garbage, not as
    ///     white</b> — the comment on the default promised white and nothing delivered it. The upload is
    ///     a recorded copy fenced on both sides, because the texture needs a layout transition into
    ///     the copy and out to sampling.
    /// </remarks>
    [Fact]
    public void TheDefaultAlbedoIsUploadedOnTheFirstFrameOnce() {
        using var dispatch = Dispatch();
        using var pass = Pass();

        var commands = device.BeginCommandList();

        pass.Prepare(commands, dispatch, View(), GrassWind.Breeze);
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var recorded = device.Recorder!.Commands.ToList();
        var copy = recorded.FindIndex(entry => entry.Kind == RecordedCommandKind.CopyBufferToTexture);
        var barriers = recorded.Count(entry => entry.Kind == RecordedCommandKind.Barrier);

        Assert.True(copy >= 0, "the default albedo's texels were never uploaded.");
        Assert.True(barriers >= 2, $"only {barriers} barriers around a copy into an undefined-layout texture.");

        device.Recorder.Clear();

        // The second frame records nothing: the texels do not change, so re-copying them would be
        // a copy per frame to deliver a constant.
        var second = device.BeginCommandList();

        pass.Prepare(second, dispatch, View(), GrassWind.Breeze);
        second.Finish();
        device.GraphicsQueue.Submit([second]);

        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.CopyBufferToTexture));
    }

    /// <summary>An assigned albedo replaces the default, and an unassigned one does not.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both halves, because the pass had only ever taken the second.</b>
    ///         <see cref="GrassDrawPass.Albedo" /> has existed since the pass did and nothing in the
    ///         engine ever set it — the field on <c>GrassType</c> is what finally can, so what is
    ///         asserted here is that the setter is actually the binding's source and not a property
    ///         the descriptor write ignores.
    ///     </para>
    ///     <para>
    ///         Through <see cref="GrassDrawPass.AlbedoOrDefault" /> rather than by reading the
    ///         descriptor set back: that is the same expression the write uses and the same one the
    ///         velocity pass borrows, so an assertion on it is an assertion on both bindings at once.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AnAssignedAlbedoReplacesTheDefault() {
        using var pass = Pass();

        var unassigned = pass.AlbedoOrDefault;

        Assert.True(unassigned.IsValid, "the built-in white default is not a view.");

        var assigned = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.Sampled, Name: "blade"))
        );

        pass.Albedo = assigned;

        Assert.Equal(assigned, pass.AlbedoOrDefault);
        Assert.NotEqual(unassigned, pass.AlbedoOrDefault);

        // And back, because a rule whose texture reference stopped resolving must return to the
        // white default rather than keep a view nothing owns any more.
        pass.Albedo = default;

        Assert.Equal(unassigned, pass.AlbedoOrDefault);
    }

    /// <summary>A pass with no blade records nothing, and says which of the two it is.</summary>
    [Fact]
    public void APassWithNoBladeDrawsNothingAndSaysSo() {
        using var dispatch = Dispatch();
        using var pass = Pass();

        dispatch.Prepare(Meadow, Grid, Resident(3), Source(), pass.MeshTemplate);

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

        // And the card is that crossed with itself: two quads of the same three segments.
        Assert.Equal(36, pass.DefaultCardIndices);
    }

    /// <summary>A bound albedo draws the card; no albedo draws the tapered strip.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two built-ins are not interchangeable and the albedo is what says which.</b>
    ///         The strip's silhouette is its geometry and the card's is its alpha, so a cutout atlas
    ///         on the strip is seven blades squeezed into a sliver and the card with no map is a
    ///         green slab. Asserted through <see cref="GrassDrawPass.MeshTemplate" /> as well as the
    ///         mesh, because the index count is what the indirect command bakes — a pass that swapped
    ///         the buffers and not the count would draw half a card.
    ///     </para>
    ///     <para>
    ///         The selection is on read rather than latched at construction because a texture is not
    ///         resolvable until its pixels are on the device — see the property's own remarks.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheAlbedoDecidesWhichBuiltInTheFieldDraws() {
        using var pass = Pass();

        Assert.Equal(pass.DefaultBladeIndices, pass.Blade.IndexCount);
        Assert.Equal((uint)pass.DefaultBladeIndices, pass.MeshTemplate.IndexCount);

        var strip = pass.Blade;

        pass.Albedo = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.Sampled, Name: "card"))
        );

        Assert.Equal(pass.DefaultCardIndices, pass.Blade.IndexCount);
        Assert.Equal((uint)pass.DefaultCardIndices, pass.MeshTemplate.IndexCount);
        Assert.NotEqual(strip.Vertices, pass.Blade.Vertices);

        // And back, because a rule whose texture stopped resolving must draw the shape that works
        // without one rather than a slab of the white default.
        pass.Albedo = default;

        Assert.Equal(strip, pass.Blade);
    }

    /// <summary>An assigned mesh wins over both built-ins, including an assigned nothing.</summary>
    /// <remarks>
    ///     ⚠ <b><c>default</c> means "no mesh", not "go back to the built-in".</b> A project that
    ///     clears the blade is saying the field must not draw — <see cref="GrassDrawPass.HasBlade" />
    ///     is the answer it expects — and a pass that read the clear as "unassigned" would quietly
    ///     resume drawing built-in grass over whatever the project put there instead.
    /// </remarks>
    [Fact]
    public void AnAssignedMeshWinsOverTheBuiltIns() {
        using var pass = Pass();

        var mine = new GrassBladeMesh(
            device.CreateBuffer(new(64, BufferUsage.Vertex, MemoryAccess.HostUpload, "mine")),
            device.CreateBuffer(new(24, BufferUsage.Index, MemoryAccess.HostUpload, "mine")),
            6
        );

        pass.Blade = mine;

        Assert.Equal(mine, pass.Blade);

        // The albedo arriving does not take it back.
        pass.Albedo = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.Sampled, Name: "card"))
        );

        Assert.Equal(mine, pass.Blade);

        pass.Blade = default;

        Assert.False(pass.HasBlade);
    }
}
