// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Graphics;
using Vixen.Water;

namespace Vixen.Rendering.Water;

/// <summary>
///     The interchange, on the device: surface height, flow in two channels, and the ground beneath.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D3](../../docs/plan/35-water.md#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render).</b>
///         Every consumer wants a <em>field</em> over the ground plane rather than a set of bodies —
///         the material wants depth for its colour and its shoreline, the vertex stage wants
///         attenuation, the foam wants the gradient, the ripple simulation wants a boundary — and none
///         of them wants to iterate bodies per pixel.
///     </para>
///     <para>
///         ⚠ <b>Depth is not a channel, and that is the rule rather than a saving.</b> How deep the
///         water is at a texel is the surface minus the ground, computed where it is used. Storing it
///         would be a third number that can disagree with the two it came from, and the frame it
///         disagrees on is the one where the shoreline is in a different place for the material than
///         for the wave attenuation.
///     </para>
///     <para>
///         <b>Uploaded from <see cref="WaterField" /> rather than rendered into.</b> § D3 describes a
///         top-down render with every body drawing through a material; that is faster and it is a
///         second rasterisation of the same bodies, one for the picture and one for the boat, with
///         nothing holding the two together. See <see cref="WaterZoneState" /> — the field is
///         re-rasterised a hundred frames apart, so what this costs per frame is nothing.
///     </para>
///     <para>
///         ⚠ <b>It uploads only when the field changed, and <see cref="UploadCount" /> is what says
///         so.</b> A texture re-uploaded every frame is a megabyte a frame across the bus to say what
///         it already said — which does not show up in a picture, only in a capture.
///     </para>
/// </remarks>
public sealed class WaterInfoTexture : IDisposable {
    /// <summary>How many channels the texture carries.</summary>
    /// <remarks>Surface height, flow x, flow z, ground. Coverage is the surface against the ground.</remarks>
    public const int Channels = 4;

    readonly IGraphicsDevice device;

    // ⚠ A ring, FramesInFlight deep — TerrainRenderer's arrangement and its reason. The buffer is
    // written on the host the moment a rasterisation is staged, while a copy recorded up to
    // FramesInFlight frames ago may still be reading it on the device; one buffer written at offset
    // zero would overwrite an upload in flight. Stage advances the ring at most once a frame, so
    // this depth is exactly enough.
    readonly BufferHandle[] staging;

    float[] staged = [];
    byte[] bytes = [];
    int slot;
    bool pending;
    bool disposed;

    /// <summary>Creates the texture for a zone.</summary>
    /// <param name="device">The device it lives on.</param>
    /// <param name="zone">The zone whose window it carries.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    /// <exception cref="ArgumentException">The zone is not one that can be rasterised.</exception>
    public WaterInfoTexture(IGraphicsDevice device, in WaterZone zone) {
        ArgumentNullException.ThrowIfNull(device);

        if (zone.Validate() is { } why) {
            throw new ArgumentException(why, nameof(zone));
        }

        this.device = device;
        staging = new BufferHandle[Math.Max(1, device.FramesInFlight)];
        Zone = zone;

        Description = new(
            Format(zone.Precision),
            zone.Resolution,
            zone.Resolution,
            TextureUsage.Sampled | TextureUsage.CopyDestination,
            Name: "WaterInfo"
        );

        Texture = device.CreateTexture(Description);
        View = device.CreateTextureView(Texture);
    }

    /// <summary>The zone this carries the window of.</summary>
    public WaterZone Zone { get; }

    /// <summary>What it is.</summary>
    public TextureDescription Description { get; }

    /// <summary>The texture.</summary>
    public TextureHandle Texture { get; }

    /// <summary>A view of it, for a descriptor set.</summary>
    public TextureViewHandle View { get; }

    /// <summary>How many times the field has been staged for upload.</summary>
    /// <remarks>
    ///     ⚠ <b>The reading that says the amortisation is real.</b> It should track
    ///     <see cref="WaterZoneState.RasterCount" /> and not the frame count; a number that tracks the
    ///     frame count is a megabyte a frame going across the bus to say what it already said, which
    ///     does not show up in a picture.
    /// </remarks>
    public int UploadCount { get; private set; }

    /// <summary>Which rasterisation of the field is currently on the device.</summary>
    int uploaded = -1;

    /// <summary>Stages a zone's field for upload, if it has been rasterised since the last one.</summary>
    /// <param name="state">The zone's state.</param>
    /// <returns>Whether there is anything to record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state" /> is null.</exception>
    /// <exception cref="ArgumentException">The state's field is a different shape from this texture.</exception>
    /// <remarks>
    ///     Separate from <see cref="Record" /> because a copy into a texture cannot be recorded inside
    ///     a render pass, and because the decision — is there anything to send — belongs where the
    ///     caller can act on it rather than inside a command list.
    /// </remarks>
    public bool Stage(WaterZoneState state) {
        ArgumentNullException.ThrowIfNull(state);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (state.Field is not { } field) {
            return false;
        }

        if (field.Description.Resolution != Zone.Resolution) {
            throw new ArgumentException(
                $"The zone's field is {field.Description.Resolution}² and this texture is "
                + $"{Zone.Resolution}². A resolution change is a new texture, not an upload.",
                nameof(state)
            );
        }

        if (state.RasterCount == uploaded) {
            return false;
        }

        Fill(field);
        uploaded = state.RasterCount;
        UploadCount++;
        pending = true;

        return true;
    }

    /// <summary>Records the copy of whatever <see cref="Stage" /> last staged.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Outside a render pass.</b> A copy into a texture cannot be recorded inside one,
    ///         and everything a render-graph pass executes is inside one — so this goes in the
    ///         command list before the graph, exactly where a fixture's own uploads go.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Records one copy per <see cref="Stage" />, not one per frame.</b> A guard on the
    ///         staging buffer alone is true forever after the first upload, and a frame that
    ///         re-copies an unchanged texture is the megabyte-a-frame across the bus that
    ///         <see cref="UploadCount" /> exists to make visible — invisible in a picture either way.
    ///     </para>
    /// </remarks>
    public void Record(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!pending) {
            return;
        }

        commands.Barrier(new([], [new(Texture, ResourceState.Undefined, ResourceState.CopyDestination)]));
        commands.CopyBufferToTexture(staging[slot], 0, new(Texture), new(Zone.Resolution, Zone.Resolution, 1));
        commands.Barrier(new([], [new(Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]));

        pending = false;
    }

    /// <summary>What the frame lends the graph.</summary>
    /// <remarks>
    ///     Entering as <see cref="ResourceState.ShaderRead" /> because <see cref="Record" /> left it
    ///     there, and leaving the same way so the next frame's copy transitions from something known.
    /// </remarks>
    public (TextureHandle Texture, TextureViewHandle View, TextureDescription Description) Imported =>
        (Texture, View, Description);

    /// <summary>Packs the field into the staging buffer, converting where the zone is half precision.</summary>
    /// <remarks>
    ///     ⚠ <b>Row by row and channel by channel rather than a memory copy.</b> The field keeps its
    ///     five channels as separate arrays — which is what makes rasterising them cheap — and a
    ///     texture wants them interleaved. There is no arrangement that is both, and interleaving here
    ///     costs one pass over a texture that is written a hundred frames apart.
    /// </remarks>
    void Fill(WaterField field) {
        var resolution = Zone.Resolution;
        var texels = resolution * resolution;

        if (staged.Length < texels * Channels) {
            staged = new float[texels * Channels];
        }

        for (var z = 0; z < resolution; z++) {
            for (var x = 0; x < resolution; x++) {
                var sample = field.At(x, z);
                var at = (((z * resolution) + x) * Channels);

                staged[at] = sample.SurfaceHeight;
                staged[at + 1] = sample.Flow.X;
                staged[at + 2] = sample.Flow.Y;
                staged[at + 3] = sample.GroundHeight;
            }
        }

        var size = texels * Channels * (Zone.Precision == WaterInfoPrecision.Half ? 2 : 4);

        if (bytes.Length < size) {
            bytes = new byte[size];
        }

        if (Zone.Precision == WaterInfoPrecision.Half) {
            var halves = MemoryMarshal.Cast<byte, Half>(bytes.AsSpan(0, size));

            for (var index = 0; index < texels * Channels; index++) {
                halves[index] = (Half)staged[index];
            }
        } else {
            MemoryMarshal.AsBytes(staged.AsSpan(0, texels * Channels)).CopyTo(bytes);
        }

        // The next slot of the ring, so a copy still in flight from an earlier frame keeps the
        // buffer it was recorded against.
        slot = (slot + 1) % staging.Length;

        Ensure(size);
        device.Write(staging[slot], 0, bytes.AsSpan(0, size));
    }

    void Ensure(int size) {
        if (staging[slot].IsValid) {
            return;
        }

        // The size never changes for one texture — the resolution and the precision are fixed at
        // construction — so a slot's buffer is created once and kept.
        staging[slot] = device.CreateBuffer(
            new(size, BufferUsage.CopySource, MemoryAccess.HostUpload, "WaterInfo staging")
        );
    }

    /// <summary>Which format a precision is.</summary>
    /// <remarks>
    ///     ⚠ Four channels of float, not a packed one. The surface height and the ground height are
    ///     world coordinates and a normalised format would need a range somebody typed — which is the
    ///     number Unreal's half-precision flag turns into a stepped horizon, and the reason
    ///     <see cref="WaterZone.HeightQuantum" /> is on the panel.
    /// </remarks>
    public static PixelFormat Format(WaterInfoPrecision precision) =>
        precision == WaterInfoPrecision.Half ? PixelFormat.Rgba16Float : PixelFormat.Rgba32Float;

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var buffer in staging) {
            if (buffer.IsValid) {
                device.Destroy(buffer);
            }
        }

        device.Destroy(View);
        device.Destroy(Texture);
    }
}
