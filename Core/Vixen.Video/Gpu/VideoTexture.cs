// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Video.Gpu;

/// <summary>The planes of a video, on the GPU, kept up to date from a player.</summary>
/// <remarks>
///     <para>
///         <b>Three textures, not one, and no conversion on the way.</b> A 4:2:0 frame is a full-size
///         luma plane and two quarter-size chroma planes — three <c>R8</c> textures, uploaded exactly
///         as the decoder produced them. Converting to RGBA first would mean touching four times as
///         many bytes on the CPU, uploading four times as many, and doing on a core what the sampler
///         does for nothing. <see cref="Coefficients" /> is what a material multiplies by.
///     </para>
///     <para>
///         <b>A staging buffer per frame in flight.</b> The RHI defers destruction for the caller but
///         explicitly does not defer <em>overwriting</em>: a staging buffer the GPU is still copying
///         out of must not be written again, and "still" lasts <c>FramesInFlight</c> frames. So there
///         is one buffer per slot and <see cref="Upload(ICommandList, VideoFrame)" /> rotates through them, which is the same
///         answer <c>DescriptorAllocator</c> arrives at for the same reason.
///     </para>
///     <para>
///         <b>Planes are aligned within the buffer rather than packed.</b> A copy's source offset has
///         alignment rules on every API — four bytes at minimum, more on some hardware — and a luma
///         plane's size is <c>width × height</c>, which is not reliably a multiple of anything. Two
///         hundred and fifty-six bytes of padding per plane costs three quarters of a kilobyte and
///         removes a class of driver-specific failure that only appears at odd resolutions.
///     </para>
/// </remarks>
public sealed class VideoTexture : IDisposable {
    /// <summary>What a plane's offset in the staging buffer is rounded up to.</summary>
    const int PlaneAlignment = 256;

    readonly IGraphicsDevice device;
    readonly string name;
    readonly long[] planeOffsets = new long[4];
    readonly BufferHandle[] staging;
    readonly TextureHandle[] textures = new TextureHandle[4];
    readonly TextureViewHandle[] views = new TextureViewHandle[4];

    bool disposed;
    long stagingSize;
    int slot;

    /// <summary>Creates an empty video texture.</summary>
    /// <param name="device">The device its resources live on.</param>
    /// <param name="name">A name for the debugger and captures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    /// <remarks>
    ///     Nothing is allocated until the first <see cref="Upload(ICommandList, VideoFrame)" />, because the size of a video is
    ///     not known until a frame has been decoded — and creating a texture for the container's
    ///     stated size would be creating the wrong one for any stream whose codec disagrees.
    /// </remarks>
    public VideoTexture(IGraphicsDevice device, string name = "video") {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        this.name = string.IsNullOrEmpty(name) ? "video" : name;

        staging = new BufferHandle[Math.Max(1, device.FramesInFlight)];
        Sampler = device.CreateSampler(SamplerDescription.LinearClamp with { Name = $"{this.name} sampler" });
    }

    /// <summary>What the textures currently hold.</summary>
    public VideoFormat Format { get; private set; }

    /// <summary>How many planes there are.</summary>
    public int PlaneCount => Format.PlaneCount;

    /// <summary>What a shader multiplies the samples by to get RGB.</summary>
    public VideoColourCoefficients Coefficients { get; private set; }

    /// <summary>A sampler for the planes: linear, clamped.</summary>
    /// <remarks>
    ///     Clamped rather than repeating, because a video's edge pixel bleeding round to the other
    ///     side is the one wrapping artefact that is never wanted; linear because chroma is being
    ///     magnified two-to-one in both directions and point sampling it is visible as blocking on
    ///     every colour edge.
    /// </remarks>
    public SamplerHandle Sampler { get; }

    /// <summary>The <see cref="Playback.VideoPlayer.FrameVersion" /> that was last uploaded.</summary>
    public uint UploadedVersion { get; private set; }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        DestroyPlanes();

        foreach (var buffer in staging) {
            if (buffer.IsValid) {
                device.Destroy(buffer);
            }
        }

        device.Destroy(Sampler);
    }

    /// <summary>A plane's texture.</summary>
    /// <param name="plane">Which plane.</param>
    /// <returns>The texture, or a null handle if there is no such plane yet.</returns>
    public TextureHandle Plane(int plane) =>
        plane >= 0 && plane < PlaneCount ? textures[plane] : default;

    /// <summary>A plane's view, which is what a descriptor set binds.</summary>
    /// <param name="plane">Which plane.</param>
    /// <returns>The view, or a null handle if there is no such plane yet.</returns>
    public TextureViewHandle PlaneView(int plane) =>
        plane >= 0 && plane < PlaneCount ? views[plane] : default;

    /// <summary>Copies a player's current frame into the textures, if it is a new one.</summary>
    /// <param name="commands">A list to record the copy into. Must not be inside a render pass.</param>
    /// <param name="player">The player to take the frame from.</param>
    /// <returns>Whether anything was uploaded.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     Version-checked, so a 24 fps video in a 144 fps game costs one upload in six frames rather
    ///     than one per frame. The check is the whole reason <c>FrameVersion</c> exists.
    /// </remarks>
    public bool Upload(ICommandList commands, Playback.VideoPlayer player) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(player);

        if (player.CurrentFrame is not { } frame || player.FrameVersion == UploadedVersion) {
            return false;
        }

        Upload(commands, frame);
        UploadedVersion = player.FrameVersion;

        return true;
    }

    /// <summary>Copies a frame into the textures.</summary>
    /// <param name="commands">A list to record the copy into. Must not be inside a render pass.</param>
    /// <param name="frame">The frame.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ObjectDisposedException">The texture has been disposed.</exception>
    public void Upload(ICommandList commands, VideoFrame frame) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(disposed, this);

        var recreated = false;

        if (!Format.IsCompatibleWith(frame.Format) || !textures[0].IsValid) {
            Recreate(frame.Format);
            recreated = true;
        }

        Coefficients = VideoColourCoefficients.For(frame.Format.Matrix, frame.Format.Range);

        slot = (slot + 1) % staging.Length;

        var buffer = staging[slot];
        var planes = Format.PlaneCount;

        for (var plane = 0; plane < planes; plane++) {
            var bytes = frame.Stride(plane) * Format.PlaneHeight(plane);

            device.Write(buffer, planeOffsets[plane], frame.Pixels.Slice(frame.Offset(plane), bytes));
        }

        commands.PushDebugGroup($"{name} upload");

        Span<TextureBarrier> before = stackalloc TextureBarrier[planes];

        for (var plane = 0; plane < planes; plane++) {
            before[plane] = new TextureBarrier(
                textures[plane],
                recreated ? ResourceState.Undefined : ResourceState.ShaderRead,
                ResourceState.CopyDestination
            );
        }

        commands.Barrier(new BarrierGroup([], before));

        for (var plane = 0; plane < planes; plane++) {
            commands.CopyBufferToTexture(
                buffer,
                planeOffsets[plane],
                new TextureRegion(textures[plane]),
                new Int3(Format.PlaneWidth(plane) / BytesPerTexel(plane), Format.PlaneHeight(plane), 1)
            );
        }

        Span<TextureBarrier> after = stackalloc TextureBarrier[planes];

        for (var plane = 0; plane < planes; plane++) {
            after[plane] = new TextureBarrier(
                textures[plane],
                ResourceState.CopyDestination,
                ResourceState.ShaderRead
            );
        }

        commands.Barrier(new BarrierGroup([], after));
        commands.PopDebugGroup();
    }

    /// <summary>What one texel of a plane weighs, so a byte width can become a texel width.</summary>
    /// <remarks>
    ///     One for every plane of every layout except the packed one, where a row of
    ///     <c>width × 4</c> bytes is <c>width</c> BGRA texels.
    /// </remarks>
    int BytesPerTexel(int plane) =>
        Format.Layout == VideoPixelLayout.Bgra8 && plane == 0 ? 4 : 1;

    void Recreate(in VideoFormat format) {
        DestroyPlanes();

        Format = format;

        var planes = format.PlaneCount;
        var offset = 0L;

        for (var plane = 0; plane < planes; plane++) {
            var isPacked = format.Layout == VideoPixelLayout.Bgra8 && plane == 0;
            var width = isPacked ? format.Width : format.PlaneWidth(plane);
            var height = format.PlaneHeight(plane);

            textures[plane] = device.CreateTexture(
                new TextureDescription(
                    isPacked ? PixelFormat.Bgra8UNorm : PixelFormat.R8UNorm,
                    width,
                    height,
                    TextureUsage.Sampled | TextureUsage.CopyDestination,
                    Name: $"{name} plane {plane}"
                )
            );

            views[plane] = device.CreateTextureView(textures[plane]);

            planeOffsets[plane] = offset;
            offset += Align(format.PlaneWidth(plane) * height);
        }

        if (offset > stagingSize) {
            for (var index = 0; index < staging.Length; index++) {
                if (staging[index].IsValid) {
                    device.Destroy(staging[index]);
                }

                staging[index] = device.CreateBuffer(
                    new BufferDescription(
                        offset,
                        BufferUsage.CopySource,
                        MemoryAccess.HostUpload,
                        $"{name} staging {index}"
                    )
                );
            }

            stagingSize = offset;
        }
    }

    void DestroyPlanes() {
        for (var plane = 0; plane < views.Length; plane++) {
            if (views[plane].IsValid) {
                device.Destroy(views[plane]);
                views[plane] = default;
            }

            if (textures[plane].IsValid) {
                device.Destroy(textures[plane]);
                textures[plane] = default;
            }
        }
    }

    static long Align(long size) => (size + PlaneAlignment - 1) / PlaneAlignment * PlaneAlignment;
}
