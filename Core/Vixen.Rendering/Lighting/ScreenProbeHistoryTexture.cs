// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.ScreenProbes;

namespace Vixen.Rendering.Lighting;

/// <summary>The screen probes' accumulated history, on the device — two sets of planes, ping-ponged.</summary>
/// <remarks>
///     <para>
///         <b>Six planes per set.</b> Four carry the accumulated projection in
///         <see cref="ScreenProbeResolve" />'s own packing, so whatever upsamples resolved probes
///         upsamples accumulated ones without knowing the difference. The fifth holds each probe's
///         surface position with the accumulated weight in alpha, and the sixth its normal — what
///         reprojection tests a surface against, and what tells "no history" from "history worth
///         nothing".
///     </para>
///     <para>
///         <b>Two sets, because accumulation reads last frame while writing this one.</b> The
///         dispatch reads the <i>front</i> set and writes the <i>back</i>; <see cref="Swap" /> then
///         makes the back the front. One set read and written at once would race a panning camera
///         against itself — the same reason <c>ScreenProbeHistory</c> double-buffers on the CPU.
///     </para>
///     <para>
///         ⚠ Not graph resources — named into descriptor sets, so whoever dispatches into a set
///         brackets it with <see cref="TransitionBack" /> and consumers find every plane in
///         <see cref="ResourceState.ShaderRead" /> between dispatches.
///     </para>
/// </remarks>
public sealed class ScreenProbeHistoryTexture : IDisposable {
    /// <summary>Planes per set.</summary>
    public const int Planes = 6;

    /// <summary>Planes in the filtered set — the four the upsample reads.</summary>
    public const int FilteredPlanes = 4;

    readonly TextureHandle[,] textures = new TextureHandle[2, Planes];
    readonly TextureViewHandle[,] views = new TextureViewHandle[2, Planes];
    readonly TextureHandle[] filtered = new TextureHandle[FilteredPlanes];
    readonly TextureViewHandle[] filteredViews = new TextureViewHandle[FilteredPlanes];

    IGraphicsDevice? device;
    BufferHandle download;
    BufferHandle filteredDownload;
    int front;
    bool primed;
    bool disposed;

    /// <summary>Builds a history over one lattice. Nothing exists until <see cref="EnsureCreated" />.</summary>
    /// <param name="layout">Where the probes stand.</param>
    public ScreenProbeHistoryTexture(ScreenProbeLayout layout) {
        Layout = layout;
    }

    /// <summary>The lattice the history covers.</summary>
    public ScreenProbeLayout Layout { get; }

    /// <summary>Whether the device objects exist yet.</summary>
    public bool IsCreated { get; private set; }

    /// <summary>The set the last accumulation wrote — what a consumer reads.</summary>
    /// <param name="plane">Which plane, 0 to <see cref="Planes" /> − 1.</param>
    public TextureViewHandle FrontView(int plane) => views[front, plane];

    /// <summary>The front set's texture, for importing into a graph.</summary>
    public TextureHandle FrontTexture(int plane) => textures[front, plane];

    /// <summary>The set the next accumulation writes.</summary>
    public TextureViewHandle BackView(int plane) => views[1 - front, plane];

    /// <summary>The back set's texture — what a graph imports for passes that run after the swap.</summary>
    /// <remarks>
    ///     A compositor publishes planes at build time, and the accumulation swaps at execute time —
    ///     so the set that is <i>back</i> while building is the set consumers read once the frame has
    ///     run.
    /// </remarks>
    public TextureHandle BackTexture(int plane) => textures[1 - front, plane];

    /// <summary>What one plane is, for a graph import.</summary>
    public TextureDescription PlaneDescription =>
        new(
            PixelFormat.Rgba32Float,
            Layout.GridSize.X,
            Layout.GridSize.Y,
            TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.Storage
        );

    /// <summary>Makes the back set the front — the written history becomes the read one.</summary>
    public void Swap() => front = 1 - front;

    /// <summary>One plane of the spatially filtered probes — what the upsample reads when a filter runs.</summary>
    /// <remarks>
    ///     Separate from both history sets, deliberately: the filter reads accumulated probes and
    ///     writes here, so the history stays raw and next frame blends against an unfiltered past —
    ///     filtered history is a blur that widens every frame with no knob that set it.
    /// </remarks>
    public TextureViewHandle FilteredView(int plane) => filteredViews[plane];

    /// <summary>The filtered plane's texture, for importing into a graph.</summary>
    public TextureHandle FilteredTexture(int plane) => filtered[plane];

    /// <summary>Moves the filtered set between <see cref="ResourceState.ShaderRead" /> and writable.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <param name="before">What the set is in.</param>
    /// <param name="after">What it needs to be in.</param>
    public void TransitionFiltered(ICommandList commands, ResourceState before, ResourceState after) {
        ArgumentNullException.ThrowIfNull(commands);

        var barriers = new TextureBarrier[FilteredPlanes];

        for (var plane = 0; plane < FilteredPlanes; plane++) {
            barriers[plane] = new(filtered[plane], before, after);
        }

        commands.Barrier(new([], barriers));
    }

    /// <summary>Creates both sets, without recording anything — a graph import needs handles at build time.</summary>
    /// <param name="graphics">The device.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public void EnsureCreated(IGraphicsDevice graphics) {
        ArgumentNullException.ThrowIfNull(graphics);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (IsCreated) {
            return;
        }

        device = graphics;

        var grid = Layout.GridSize;

        for (var set = 0; set < 2; set++) {
            for (var plane = 0; plane < Planes; plane++) {
                textures[set, plane] = graphics.CreateTexture(
                    new TextureDescription(
                        PixelFormat.Rgba32Float,
                        grid.X,
                        grid.Y,
                        TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.Storage,
                        Name: $"ScreenProbes.History{set}.{plane}"
                    )
                );

                views[set, plane] = graphics.CreateTextureView(textures[set, plane]);
            }
        }

        for (var plane = 0; plane < FilteredPlanes; plane++) {
            filtered[plane] = graphics.CreateTexture(
                new TextureDescription(
                    PixelFormat.Rgba32Float,
                    grid.X,
                    grid.Y,
                    TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.Storage,
                    Name: $"ScreenProbes.Filtered.{plane}"
                )
            );

            filteredViews[plane] = graphics.CreateTextureView(filtered[plane]);
        }

        IsCreated = true;
    }

    /// <summary>Puts every plane of both sets in <see cref="ResourceState.ShaderRead" />, once.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    public void Prime(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);

        if (primed || !IsCreated) {
            return;
        }

        var barriers = new TextureBarrier[(2 * Planes) + FilteredPlanes];

        for (var set = 0; set < 2; set++) {
            for (var plane = 0; plane < Planes; plane++) {
                barriers[(set * Planes) + plane] =
                    new(textures[set, plane], ResourceState.Undefined, ResourceState.ShaderRead);
            }
        }

        for (var plane = 0; plane < FilteredPlanes; plane++) {
            barriers[(2 * Planes) + plane] = new(filtered[plane], ResourceState.Undefined, ResourceState.ShaderRead);
        }

        commands.Barrier(new([], barriers));
        primed = true;
    }

    /// <summary>Moves the back set between <see cref="ResourceState.ShaderRead" /> and writable.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <param name="before">What the set is in.</param>
    /// <param name="after">What it needs to be in.</param>
    public void TransitionBack(ICommandList commands, ResourceState before, ResourceState after) {
        ArgumentNullException.ThrowIfNull(commands);

        var barriers = new TextureBarrier[Planes];

        for (var plane = 0; plane < Planes; plane++) {
            barriers[plane] = new(textures[1 - front, plane], before, after);
        }

        commands.Barrier(new([], barriers));
    }

    /// <summary>Records a copy of the front set back into host memory.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <returns>False before the textures exist.</returns>
    public bool RecordReadback(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!IsCreated || device is null) {
            return false;
        }

        var grid = Layout.GridSize;
        var planeBytes = (long)grid.X * grid.Y * 4 * sizeof(float);

        if (!download.IsValid) {
            download = device.CreateBuffer(
                new BufferDescription(
                    planeBytes * Planes,
                    BufferUsage.CopyDestination,
                    MemoryAccess.HostReadback,
                    "ScreenProbes.HistoryReadback"
                )
            );
        }

        var barriers = new TextureBarrier[Planes];

        for (var plane = 0; plane < Planes; plane++) {
            barriers[plane] = new(textures[front, plane], ResourceState.ShaderRead, ResourceState.CopySource);
        }

        commands.Barrier(new([], barriers));

        for (var plane = 0; plane < Planes; plane++) {
            commands.CopyTextureToBuffer(
                new TextureRegion(textures[front, plane]),
                new(grid.X, grid.Y, 1),
                download,
                plane * planeBytes
            );
        }

        for (var plane = 0; plane < Planes; plane++) {
            barriers[plane] = new(textures[front, plane], ResourceState.CopySource, ResourceState.ShaderRead);
        }

        commands.Barrier(new([], barriers));

        return true;
    }

    /// <summary>Records a copy of the filtered planes back into host memory.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <returns>False before the textures exist.</returns>
    public bool RecordFilteredReadback(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!IsCreated || device is null) {
            return false;
        }

        var grid = Layout.GridSize;
        var planeBytes = (long)grid.X * grid.Y * 4 * sizeof(float);

        if (!filteredDownload.IsValid) {
            filteredDownload = device.CreateBuffer(
                new BufferDescription(
                    planeBytes * FilteredPlanes,
                    BufferUsage.CopyDestination,
                    MemoryAccess.HostReadback,
                    "ScreenProbes.FilteredReadback"
                )
            );
        }

        TransitionFiltered(commands, ResourceState.ShaderRead, ResourceState.CopySource);

        for (var plane = 0; plane < FilteredPlanes; plane++) {
            commands.CopyTextureToBuffer(
                new TextureRegion(filtered[plane]),
                new(grid.X, grid.Y, 1),
                filteredDownload,
                plane * planeBytes
            );
        }

        TransitionFiltered(commands, ResourceState.CopySource, ResourceState.ShaderRead);

        return true;
    }

    /// <summary>Decodes what the last <see cref="RecordFilteredReadback" /> copied.</summary>
    /// <param name="filteredProbes">One projection per probe, row-major over the grid.</param>
    /// <returns>False when nothing has been read back, or the span is too short.</returns>
    public bool TryReadFiltered(Span<SphericalHarmonicsL1> filteredProbes) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var grid = Layout.GridSize;
        var count = grid.X * grid.Y;

        if (device is null || !filteredDownload.IsValid || filteredProbes.Length < count) {
            return false;
        }

        var floats = new float[count * 4 * FilteredPlanes];

        device.Read(filteredDownload, 0, MemoryMarshal.AsBytes(floats.AsSpan()));

        for (var index = 0; index < count; index++) {
            var l0 = floats.AsSpan(index * 4);
            var red = floats.AsSpan((count * 4) + (index * 4));
            var green = floats.AsSpan((2 * count * 4) + (index * 4));
            var blue = floats.AsSpan((3 * count * 4) + (index * 4));

            filteredProbes[index] = new(
                new Vector3(l0[0], l0[1], l0[2]),
                new Vector3(red[0], green[0], blue[0]),
                new Vector3(red[1], green[1], blue[1]),
                new Vector3(red[2], green[2], blue[2])
            );
        }

        return true;
    }

    /// <summary>Decodes what the last <see cref="RecordReadback" /> copied.</summary>
    /// <param name="accumulated">One projection per probe, row-major over the grid.</param>
    /// <param name="weights">The accumulated weight per probe, same order.</param>
    /// <returns>False when nothing has been read back, or a span is too short.</returns>
    public bool TryRead(Span<SphericalHarmonicsL1> accumulated, Span<float> weights) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var grid = Layout.GridSize;
        var count = grid.X * grid.Y;

        if (device is null || !download.IsValid || accumulated.Length < count || weights.Length < count) {
            return false;
        }

        var floats = new float[count * 4 * Planes];

        device.Read(download, 0, MemoryMarshal.AsBytes(floats.AsSpan()));

        for (var index = 0; index < count; index++) {
            var l0 = floats.AsSpan(index * 4);
            var red = floats.AsSpan((count * 4) + (index * 4));
            var green = floats.AsSpan((2 * count * 4) + (index * 4));
            var blue = floats.AsSpan((3 * count * 4) + (index * 4));
            var surface = floats.AsSpan((4 * count * 4) + (index * 4));

            accumulated[index] = new(
                new Vector3(l0[0], l0[1], l0[2]),
                new Vector3(red[0], green[0], blue[0]),
                new Vector3(red[1], green[1], blue[1]),
                new Vector3(red[2], green[2], blue[2])
            );

            weights[index] = surface[3];
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (device is null) {
            return;
        }

        for (var set = 0; set < 2; set++) {
            for (var plane = 0; plane < Planes; plane++) {
                if (views[set, plane].IsValid) {
                    device.Destroy(views[set, plane]);
                }

                if (textures[set, plane].IsValid) {
                    device.Destroy(textures[set, plane]);
                }
            }
        }

        foreach (var view in filteredViews) {
            if (view.IsValid) {
                device.Destroy(view);
            }
        }

        foreach (var plane in filtered) {
            if (plane.IsValid) {
                device.Destroy(plane);
            }
        }

        if (download.IsValid) {
            device.Destroy(download);
        }

        if (filteredDownload.IsValid) {
            device.Destroy(filteredDownload);
        }

        IsCreated = false;
    }
}
