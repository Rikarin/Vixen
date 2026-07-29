// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Video.Gpu;
using Vixen.Video.Playback;

namespace Vixen.Video.Rendering;

/// <summary>A video's picture as one ordinary colour texture, converted once a frame.</summary>
/// <remarks>
///     <para>
///         <b>What makes a video usable by everything that wants a texture and not three.</b>
///         <see cref="VideoRenderer" /> draws planes straight into whatever pass it is recorded in,
///         which is the cheapest thing for a cutscene that covers the screen and no use at all to a
///         consumer that can only bind one view: a user interface's image command, a material slot, a
///         thumbnail. This runs the same conversion into a target of its own and hands over the view.
///     </para>
///     <para>
///         ⚠ <b>It costs a texture and a pass per video per frame, and that is the whole trade.</b>
///         Drawing straight into the destination avoids both — so a full-screen cutscene should not
///         use this, and anything that has to name the video by a texture handle has no alternative
///         that is not a CPU conversion, which touches four times the bytes to do on a core what the
///         sampler does for nothing.
///     </para>
///     <para>
///         ⚠ <b>Not sRGB.</b> A decoded video's RGB is already gamma-encoded — that is what the BT.709
///         transfer function is — so an sRGB target encodes it a second time and shows as mid-tones
///         far too bright. The consumer samples these bytes as they are.
///     </para>
/// </remarks>
public sealed class VideoRenderTarget : IDisposable {
    /// <summary>What the target is, and what a renderer drawn into it must be built for.</summary>
    public const PixelFormat Format = PixelFormat.Rgba8UNorm;

    readonly IGraphicsDevice device;
    readonly VideoRenderer renderer;
    readonly bool ownsRenderer;
    readonly string name;

    bool disposed;
    ResourceState state = ResourceState.Undefined;

    /// <summary>Builds a target with a renderer of its own.</summary>
    /// <param name="device">Where it lives.</param>
    /// <param name="shaders">The modules to draw with.</param>
    /// <param name="name">A name for the debugger and captures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public VideoRenderTarget(IGraphicsDevice device, VideoShaders shaders, string name = "video target")
        : this(device, new VideoRenderer(device, shaders, new RenderOutput([Format]), name), true, name) { }

    /// <summary>Builds a target over a renderer somebody else owns.</summary>
    /// <param name="device">Where it lives.</param>
    /// <param name="renderer">What draws. Must have been built for <see cref="Format" />.</param>
    /// <param name="name">A name for the debugger and captures.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A renderer built for another format silently draws nothing useful</b> — a pipeline's
    ///     colour formats are part of what it was compiled against, and one that disagrees with the
    ///     pass is undefined on some drivers and a validation error on others. Two videos on screen
    ///     should share one renderer through this overload; a video drawn both into a target and
    ///     straight onto the backbuffer needs two, because those are two formats.
    /// </remarks>
    public VideoRenderTarget(IGraphicsDevice device, VideoRenderer renderer, string name = "video target")
        : this(device, renderer, false, name) { }

    VideoRenderTarget(IGraphicsDevice device, VideoRenderer renderer, bool ownsRenderer, string name) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(renderer);

        this.device = device;
        this.renderer = renderer;
        this.ownsRenderer = ownsRenderer;
        this.name = string.IsNullOrEmpty(name) ? "video target" : name;
    }

    /// <summary>The texture, or an invalid handle before the first draw.</summary>
    public TextureHandle Texture { get; private set; }

    /// <summary>The view a consumer binds, or an invalid handle before the first draw.</summary>
    public TextureViewHandle View { get; private set; }

    /// <summary>How big it currently is.</summary>
    public Int2 Size { get; private set; }

    /// <summary>Bumped whenever <see cref="View" /> becomes a different view.</summary>
    /// <remarks>
    ///     ⚠ <b>What a consumer holding the view has to watch.</b> A resize destroys the texture and
    ///     makes a new one, and a descriptor set still naming the old view names freed memory — which
    ///     is undefined behaviour rather than an error, and shows as a picture that is fine until the
    ///     window is dragged. <c>UiRenderer.RegisterImage</c> is idempotent and cheap, so the ordinary
    ///     answer is to re-register whenever this changes.
    /// </remarks>
    public int Revision { get; private set; }

    /// <summary>Converts a player's current picture into the target.</summary>
    /// <param name="commands">Where to record. Must <b>not</b> be inside a render pass.</param>
    /// <param name="planes">The uploaded planes.</param>
    /// <param name="player">The player, which says how big the picture is meant to look.</param>
    /// <returns>Whether anything was drawn.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The target is sized to the picture's <i>display</i> size, not its sample count.</b>
    ///     Anamorphic content — a 720×480 clip meant to be shown at 853×480 — is resampled to square
    ///     pixels here, once, rather than leaving every consumer to remember that this particular
    ///     texture is not the shape it claims. That is the whole reason a consumer can then treat it
    ///     as an ordinary picture.
    /// </remarks>
    public bool Draw(ICommandList commands, VideoTexture planes, VideoPlayer player) {
        ArgumentNullException.ThrowIfNull(player);

        return Draw(commands, planes, player.DisplaySize);
    }

    /// <summary>Converts uploaded planes into the target, at a size of the caller's choosing.</summary>
    /// <param name="commands">Where to record. Must <b>not</b> be inside a render pass.</param>
    /// <param name="planes">The uploaded planes.</param>
    /// <param name="size">How big the target should be.</param>
    /// <returns>Whether anything was drawn.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ObjectDisposedException">The target has been disposed.</exception>
    public bool Draw(ICommandList commands, VideoTexture planes, Int2 size) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(planes);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (planes.PlaneCount == 0 || size.X <= 0 || size.Y <= 0) {
            return false;
        }

        Resize(size);

        commands.PushDebugGroup($"{name} convert");

        // ⚠ Into the attachment state and back out again, every frame. A pass declares what its
        // attachments need but the transition *out* is the caller's — the consumer samples this, and
        // a texture left in ColourTarget is one the shader reads as undefined.
        commands.Barrier(new BarrierGroup([], [new TextureBarrier(Texture, state, ResourceState.ColourTarget)]));

        commands.BeginRenderPass(
            new RenderPassDescription(
                // Cleared rather than loaded: the video covers the whole target by construction, so
                // there is nothing underneath worth keeping and a load is a read of the whole texture
                // to be overwritten.
                [new ColourAttachment(View, LoadAction.Clear, StoreAction.Store, new Color4(0f, 0f, 0f, 1f))],
                name: name
            )
        );

        renderer.Begin();

        var drawn = renderer.Record(
            commands,
            VideoDraw.Filling(planes, new Rectangle(0, 0, size.X, size.Y)),
            size
        );

        commands.EndRenderPass();

        commands.Barrier(
            new BarrierGroup([], [new TextureBarrier(Texture, ResourceState.ColourTarget, ResourceState.ShaderRead)])
        );

        commands.PopDebugGroup();

        state = ResourceState.ShaderRead;

        return drawn;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        Destroy();

        if (ownsRenderer) {
            renderer.Dispose();
        }
    }

    void Resize(Int2 size) {
        if (Size == size && Texture.IsValid) {
            return;
        }

        Destroy();

        Texture = device.CreateTexture(
            new TextureDescription(
                Format,
                size.X,
                size.Y,
                TextureUsage.ColourTarget | TextureUsage.Sampled,
                Name: name
            )
        );

        View = device.CreateTextureView(Texture);
        Size = size;
        state = ResourceState.Undefined;

        // ⚠ Last, and after the handles are the new ones. A consumer that watches this and re-reads
        // View in the same breath must not be able to see the two disagree.
        Revision++;
    }

    void Destroy() {
        if (!Texture.IsValid) {
            return;
        }

        device.Destroy(View);
        device.Destroy(Texture);

        Texture = default;
        View = default;
        Size = default;
        state = ResourceState.Undefined;
    }
}
