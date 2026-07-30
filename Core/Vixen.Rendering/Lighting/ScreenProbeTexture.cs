// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.ScreenProbes;
using Vixen.Shaders;

namespace Vixen.Rendering.Lighting;

/// <summary>A screen probe atlas, mirrored into a device texture.</summary>
/// <remarks>
///     <para>
///         <b>One 2D texture, radiance in the colour and validity in the alpha.</b> A probe's map is
///         an 8×8 patch at <see cref="ScreenProbeLayout.AtlasOrigin" />, exactly the arrangement the
///         CPU atlas uses, because the two are one convention — <c>ScreenProbeAtlas.rvn</c> holds the
///         shader's half and the convention tests hold the pair together. Alpha one is a gathered
///         texel; alpha zero is a probe nothing gathered, which is what lets a readback tell "nothing
///         ran" from "gathered nothing but darkness".
///     </para>
///     <para>
///         <b>The mirror uploads the CPU atlas, unless the atlas is the dispatch's.</b> The same
///         arrangement, for the same doc 19 reasons, as <see cref="IrradianceFieldTexture.PoolIsWritten" />:
///         a target with compute traces the atlas on the device and the CPU copy stops being what the
///         shader reads; a readback is then the only way the closed forms have anything to test.
///     </para>
///     <para>
///         ⚠ The texture is not a graph resource — it is named into descriptor sets — so nothing else
///         in a frame will transition it. Whoever dispatches into it brackets with
///         <see cref="TransitionAtlas" />, the way every fill over the irradiance pool already does.
///     </para>
/// </remarks>
public sealed class ScreenProbeTexture : IDisposable {
    /// <summary>Floats per atlas texel.</summary>
    const int Channels = 4;

    /// <summary>How many planes a resolved probe is spread across.</summary>
    const int ProbePlanes = 4;

    readonly float[] scratch;
    readonly TextureHandle[] probes = new TextureHandle[ProbePlanes];
    readonly TextureViewHandle[] probeViews = new TextureViewHandle[ProbePlanes];

    IGraphicsDevice? device;
    TextureHandle atlas;
    TextureViewHandle atlasView;
    BufferHandle staging;
    BufferHandle download;
    BufferHandle probeDownload;
    bool disposed;

    /// <summary>Builds a mirror of one atlas. Nothing exists on the device until the first upload.</summary>
    /// <param name="probes">The atlas to mirror.</param>
    /// <exception cref="ArgumentNullException">There is no atlas.</exception>
    public ScreenProbeTexture(ScreenProbeAtlas probes) {
        ArgumentNullException.ThrowIfNull(probes);

        Probes = probes;
        scratch = new float[(long)probes.Layout.AtlasSize.X * probes.Layout.AtlasSize.Y * Channels];
    }

    /// <summary>The atlas this mirrors.</summary>
    public ScreenProbeAtlas Probes { get; }

    /// <summary>Whether the device objects exist yet.</summary>
    public bool IsCreated { get; private set; }

    /// <summary>How many times the atlas has been uploaded.</summary>
    public int Uploads { get; private set; }

    /// <summary>Whether a compute dispatch owns the texels, so the upload must not.</summary>
    /// <remarks>
    ///     Set, the upload creates the texture and gives it a state, once, and never copies — the
    ///     texels are the dispatch's, and an upload after it would overwrite a frame's tracing with
    ///     the stale CPU copy one frame later.
    /// </remarks>
    public bool AtlasIsWritten { get; init; }

    /// <summary>The atlas texture.</summary>
    public TextureHandle Atlas => atlas;

    /// <summary>Its view, for descriptor writes.</summary>
    public TextureViewHandle AtlasView => atlasView;

    /// <summary>One plane of the resolved probes — grid-sized, in the irradiance pool's packing.</summary>
    /// <param name="plane">Which one — zero the constant term with validity in alpha, one to three the
    ///     red, green and blue components of the linear coefficients.</param>
    public TextureViewHandle ProbeView(int plane) => probeViews[plane];

    /// <summary>Moves the four resolved-probe planes from one state to another, in one barrier.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <param name="before">What they are in.</param>
    /// <param name="after">What they need to be in.</param>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    public void TransitionProbes(ICommandList commands, ResourceState before, ResourceState after) {
        ArgumentNullException.ThrowIfNull(commands);

        var barriers = new TextureBarrier[ProbePlanes];

        for (var plane = 0; plane < ProbePlanes; plane++) {
            barriers[plane] = new(probes[plane], before, after);
        }

        commands.Barrier(new([], barriers));
    }

    /// <summary>What the atlas is in while a dispatch owns it — writable, and readable by the next one.</summary>
    /// <remarks>The same two bits, for the same barrier reason, as the irradiance pool's.</remarks>
    public const ResourceState AtlasIsBeingWritten = ResourceState.ShaderWrite | ResourceState.ShaderRead;

    /// <summary>Creates the texture if it does not exist, and uploads the CPU atlas unless a dispatch owns it.</summary>
    /// <param name="graphics">The device.</param>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <exception cref="ArgumentNullException">There is no device or command list.</exception>
    public void Upload(IGraphicsDevice graphics, ICommandList commands) {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Create(graphics);

        var settled = Uploads == 0 ? ResourceState.Undefined : ResourceState.ShaderRead;

        // The resolved-probe planes are always the dispatch's — nothing uploads them — so all any
        // upload owes them is a state they can be found in, once.
        if (Uploads == 0) {
            TransitionProbes(commands, ResourceState.Undefined, ResourceState.ShaderRead);
        }

        if (AtlasIsWritten) {
            // The texels are the dispatch's. All this owes them is a state they can be found in, once.
            if (Uploads == 0) {
                TransitionAtlas(commands, ResourceState.Undefined, ResourceState.ShaderRead);
            }
        } else {
            TransitionAtlas(commands, settled, ResourceState.CopyDestination);

            Pack();
            graphics.Write(staging, 0, MemoryMarshal.AsBytes(scratch.AsSpan()));

            var size = Probes.Layout.AtlasSize;

            commands.CopyBufferToTexture(staging, 0, new TextureRegion(atlas), new(size.X, size.Y, 1));
            TransitionAtlas(commands, ResourceState.CopyDestination, ResourceState.ShaderRead);
        }

        Uploads++;
    }

    /// <summary>Moves the atlas from one state to another.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <param name="before">What it is in.</param>
    /// <param name="after">What it needs to be in.</param>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    public void TransitionAtlas(ICommandList commands, ResourceState before, ResourceState after) {
        ArgumentNullException.ThrowIfNull(commands);

        commands.Barrier(new([], [new TextureBarrier(atlas, before, after)]));
    }

    /// <summary>Orders everything written to the atlas so far against whatever reads it next.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    public void OrderAtlas(ICommandList commands) =>
        TransitionAtlas(commands, AtlasIsBeingWritten, AtlasIsBeingWritten);

    /// <summary>Records a copy of the whole atlas back into host memory.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <returns>False before the texture exists, in which case nothing was recorded.</returns>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    /// <remarks>
    ///     What makes a device-authored atlas checkable — the same argument, verbatim, as the
    ///     irradiance pool's readback. The result is only readable once the queue has finished;
    ///     the atlas is left in <see cref="ResourceState.ShaderRead" />.
    /// </remarks>
    public bool RecordReadback(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!IsCreated || device is null) {
            return false;
        }

        if (!download.IsValid) {
            download = device.CreateBuffer(
                new BufferDescription(
                    (long)scratch.Length * sizeof(float),
                    BufferUsage.CopyDestination,
                    MemoryAccess.HostReadback,
                    "ScreenProbes.Readback"
                )
            );
        }

        var size = Probes.Layout.AtlasSize;

        TransitionAtlas(commands, ResourceState.ShaderRead, ResourceState.CopySource);
        commands.CopyTextureToBuffer(new TextureRegion(atlas), new(size.X, size.Y, 1), download, 0);
        TransitionAtlas(commands, ResourceState.CopySource, ResourceState.ShaderRead);

        return true;
    }

    /// <summary>Records a copy of the four resolved-probe planes back into host memory.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <returns>False before the textures exist, in which case nothing was recorded.</returns>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    public bool RecordProbeReadback(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!IsCreated || device is null) {
            return false;
        }

        var grid = Probes.Layout.GridSize;
        var planeBytes = (long)grid.X * grid.Y * Channels * sizeof(float);

        if (!probeDownload.IsValid) {
            probeDownload = device.CreateBuffer(
                new BufferDescription(
                    planeBytes * ProbePlanes,
                    BufferUsage.CopyDestination,
                    MemoryAccess.HostReadback,
                    "ScreenProbes.ProbeReadback"
                )
            );
        }

        TransitionProbes(commands, ResourceState.ShaderRead, ResourceState.CopySource);

        for (var plane = 0; plane < ProbePlanes; plane++) {
            commands.CopyTextureToBuffer(new TextureRegion(probes[plane]), new(grid.X, grid.Y, 1), probeDownload, plane * planeBytes);
        }

        TransitionProbes(commands, ResourceState.CopySource, ResourceState.ShaderRead);

        return true;
    }

    /// <summary>Decodes what the last <see cref="RecordProbeReadback" /> copied.</summary>
    /// <param name="resolved">One projection per probe, row-major over the grid.</param>
    /// <param name="validities">One validity per probe, same order.</param>
    /// <returns>False when nothing has been read back, or a span is too short.</returns>
    /// <remarks>
    ///     Reassembled from the four planes the way a sampler would read them — the colour-major
    ///     inverse, written out rather than shared with the encode, so it tests something.
    /// </remarks>
    public bool TryReadProbes(Span<SphericalHarmonicsL1> resolved, Span<float> validities) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var grid = Probes.Layout.GridSize;
        var count = grid.X * grid.Y;

        if (device is null || !probeDownload.IsValid || resolved.Length < count || validities.Length < count) {
            return false;
        }

        var floats = new float[count * Channels * ProbePlanes];

        device.Read(probeDownload, 0, MemoryMarshal.AsBytes(floats.AsSpan()));

        for (var index = 0; index < count; index++) {
            var l0 = floats.AsSpan(index * Channels);
            var red = floats.AsSpan((count * Channels) + (index * Channels));
            var green = floats.AsSpan((2 * count * Channels) + (index * Channels));
            var blue = floats.AsSpan((3 * count * Channels) + (index * Channels));

            resolved[index] = new(
                new Vector3(l0[0], l0[1], l0[2]),
                new Vector3(red[0], green[0], blue[0]),
                new Vector3(red[1], green[1], blue[1]),
                new Vector3(red[2], green[2], blue[2])
            );

            validities[index] = l0[3];
        }

        return true;
    }

    /// <summary>Decodes what the last <see cref="RecordReadback" /> copied.</summary>
    /// <param name="texels">One entry per atlas texel, row-major — radiance and, in W, validity.</param>
    /// <returns>False when nothing has been read back, or the span is too short.</returns>
    public bool TryRead(Span<Vector4> texels) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var size = Probes.Layout.AtlasSize;
        var count = size.X * size.Y;

        if (device is null || !download.IsValid || texels.Length < count) {
            return false;
        }

        var floats = new float[count * Channels];

        device.Read(download, 0, MemoryMarshal.AsBytes(floats.AsSpan()));

        for (var index = 0; index < count; index++) {
            var at = index * Channels;

            texels[index] = new(floats[at], floats[at + 1], floats[at + 2], floats[at + 3]);
        }

        return true;
    }

    /// <summary>One plane's texture, for importing into a compositor's graph.</summary>
    /// <param name="plane">Which one.</param>
    /// <remarks>
    ///     The planes reach a consuming pass as graph imports rather than as parameter writes — a
    ///     <c>ResourceBinding</c> resolves textures against the graph and nothing else, and an import
    ///     is also what tells the graph the planes are already in <see cref="ResourceState.ShaderRead" />
    ///     so it will not transition what the resolve owns.
    /// </remarks>
    public TextureHandle ProbePlane(int plane) => probes[plane];

    /// <summary>What one plane is, for the same import.</summary>
    public TextureDescription ProbePlaneDescription =>
        new(
            PixelFormat.Rgba32Float,
            Probes.Layout.GridSize.X,
            Probes.Layout.GridSize.Y,
            TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.Storage
        );

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (device is null) {
            return;
        }

        if (atlasView.IsValid) {
            device.Destroy(atlasView);
        }

        if (atlas.IsValid) {
            device.Destroy(atlas);
        }

        foreach (var view in probeViews) {
            if (view.IsValid) {
                device.Destroy(view);
            }
        }

        foreach (var plane in probes) {
            if (plane.IsValid) {
                device.Destroy(plane);
            }
        }

        if (staging.IsValid) {
            device.Destroy(staging);
        }

        if (download.IsValid) {
            device.Destroy(download);
        }

        if (probeDownload.IsValid) {
            device.Destroy(probeDownload);
        }

        IsCreated = false;
    }

    /// <summary>Lays the CPU atlas out in <see cref="scratch" /> — radiance, and validity in alpha.</summary>
    void Pack() {
        var layout = Probes.Layout;
        var resolution = layout.MapResolution;
        var width = layout.AtlasSize.X;

        for (var py = 0; py < layout.GridSize.Y; py++) {
            for (var px = 0; px < layout.GridSize.X; px++) {
                var probe = new Int2(px, py);
                var origin = layout.AtlasOrigin(probe);
                var validity = Probes.IsValid(probe) ? 1f : 0f;

                for (var ty = 0; ty < resolution; ty++) {
                    for (var tx = 0; tx < resolution; tx++) {
                        var radiance = Probes[probe, new(tx, ty)];
                        var at = (((origin.Y + ty) * width) + origin.X + tx) * Channels;

                        scratch[at] = radiance.X;
                        scratch[at + 1] = radiance.Y;
                        scratch[at + 2] = radiance.Z;
                        scratch[at + 3] = validity;
                    }
                }
            }
        }
    }

    void Create(IGraphicsDevice graphics) {
        if (IsCreated) {
            return;
        }

        device = graphics;

        var size = Probes.Layout.AtlasSize;

        // Every usage, whichever filler is live — the same reasoning as the irradiance pool's:
        // CopySource is what the readback needs, CopyDestination and Storage together are what let an
        // atlas be seeded from the host and refined by a dispatch, and Sampled is what the resolve
        // and upsample passes will do to it.
        atlas = graphics.CreateTexture(
            new TextureDescription(
                PixelFormat.Rgba32Float,
                size.X,
                size.Y,
                TextureUsage.Sampled
                | TextureUsage.CopySource
                | TextureUsage.CopyDestination
                | TextureUsage.Storage,
                Name: "ScreenProbes.RadianceAtlas"
            )
        );

        atlasView = graphics.CreateTextureView(atlas);

        var grid = Probes.Layout.GridSize;

        for (var plane = 0; plane < ProbePlanes; plane++) {
            probes[plane] = graphics.CreateTexture(
                new TextureDescription(
                    PixelFormat.Rgba32Float,
                    grid.X,
                    grid.Y,
                    TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.Storage,
                    Name: $"ScreenProbes.Probe{plane}"
                )
            );

            probeViews[plane] = graphics.CreateTextureView(probes[plane]);
        }

        staging = graphics.CreateBuffer(
            new BufferDescription(
                (long)scratch.Length * sizeof(float),
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                "ScreenProbes.Staging"
            )
        );

        IsCreated = true;
    }
}
