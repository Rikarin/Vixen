// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Ui.Renderer;

namespace Vixen.Editor.App;

/// <summary>Turns decoded thumbnail pixels into something the interface draws.</summary>
/// <remarks>
///     <para>
///         <b>The host's half of <see cref="IThumbnailSurface" />.</b> The application decides which
///         assets are worth a picture and reduces them; this is the part that needs a device, and it
///         is the same three steps the glyph atlas takes — a staging buffer, a copy, and two
///         barriers.
///     </para>
///     <para>
///         ⚠ <b>The first barrier claims <c>Undefined</c> because the texture is new every time.</b>
///         A thumbnail is uploaded once and never rewritten, unlike the atlas, so there is no later
///         state to transition from — and claiming <c>ShaderResource</c> for a texture that has
///         never held anything is a validation error on every single upload.
///     </para>
///     <para>
///         ⚠ <b>Everything is deferred to <see cref="Retire" /> rather than destroyed on the spot.</b>
///         A tile scrolled off screen releases its image, and the frame that drew it may still be in
///         flight — destroying the texture underneath it is a use-after-free the validation layer
///         reports as a crash somewhere else entirely. ⚠ <b>But the frame is kept alive by
///         <c>IGraphicsDevice.Destroy</c>'s deferral, not by <see cref="Retire" />'s timing</b>: the
///         host calls it between frames without waiting for anything, so a backend that freed on the
///         spot would be freeing under the last frame however this class batched. See
///         <see cref="Retire" />.
///     </para>
///     <para>
///         ⚠ <b>An image number is handed back as well as its texture, and the two are not the same
///         resource.</b> The texture is the device's to free once no frame holds it; the
///         <i>number</i> is a registration in the renderer, holding a descriptor set per frame in
///         flight that no backend can free — so a class that destroyed only the texture left a
///         descriptor naming freed memory and grew the renderer's set count for every picture the
///         browser ever decoded. See <see cref="Destroy" />.
///     </para>
///     <para>
///         ⚠ <b><see cref="Upload" /> makes the resources and <see cref="Flush" /> is what submits
///         the copy, and the split is about which frame the command buffer belongs to.</b>
///         <c>ThumbnailCache.Pump</c> runs from the application's update, which is <i>outside</i>
///         <c>EditorHost.Present</c>'s <c>BeginFrame</c>/<c>EndFrame</c> pair — and a list recorded
///         there is allocated from the pool of the slot the <i>coming</i> <c>BeginFrame</c> is about
///         to reset. Submitting it where it is recorded means <c>vkResetCommandPool</c> on a buffer
///         still executing; recording it in <see cref="Flush" /> puts it inside the frame that
///         retires it, which is the same bargain <c>ShaderGraphPreviewRenderer.Update</c> makes and
///         the same point in <c>Present</c> it is drained at.
///     </para>
/// </remarks>
sealed class ThumbnailSurface : IThumbnailSurface, IDisposable {
    readonly IGraphicsDevice device;
    readonly UiRenderer renderer;

    readonly Dictionary<ulong, Uploaded> live = [];
    readonly List<Uploaded> retiring = [];

    /// <summary>What has been made and not yet copied into.</summary>
    readonly List<Pending> waiting = [];

    /// <summary>Rectangles handed to <see cref="Update" /> and not yet copied in.</summary>
    readonly List<Patch> patching = [];

    /// <summary>Staging buffers whose copy has been submitted, for the next <see cref="Retire" />.</summary>
    /// <remarks>
    ///     ⚠ <b>An update's staging buffer cannot be freed where it is recorded, unlike an upload's.</b>
    ///     An upload's belongs to the <see cref="Uploaded" /> the image owns and goes when the image
    ///     does; a patch's is used once and has no owner, so it would leak per pointer move. It is
    ///     held until the next between-frames pass rather than destroyed inside <see cref="Flush" />
    ///     — the same batching, and the same deferral by <c>IGraphicsDevice.Destroy</c>, that
    ///     <see cref="Retire" /> already rests on.
    /// </remarks>
    readonly List<BufferHandle> spent = [];

    ulong next = ThumbnailCache.FirstImage;

    /// <summary>How many copies have been submitted, over the life of this surface.</summary>
    /// <remarks>
    ///     A number rather than a flag, because the defect this counts against — a recorded copy that
    ///     was never submitted — leaves every structural claim about an upload true and only this one
    ///     false.
    /// </remarks>
    public int Submitted { get; private set; }

    /// <summary>How many uploads are made but not yet copied into.</summary>
    public int Waiting => waiting.Count;

    /// <summary>Wires a surface to the device and the renderer that draws the interface.</summary>
    /// <param name="device">The device.</param>
    /// <param name="renderer">The main window's renderer, which is what an <c>Image</c> resolves against.</param>
    public ThumbnailSurface(IGraphicsDevice device, UiRenderer renderer) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(renderer);

        this.device = device;
        this.renderer = renderer;
    }

    /// <inheritdoc />
    public ulong Upload(int width, int height, ReadOnlySpan<byte> rgba) {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) {
            return 0;
        }

        var texture = device.CreateTexture(
            new(
                PixelFormat.Rgba8UNorm,
                width,
                height,
                // ⚠ `CopySource` is for the test that reads the pixels back and for nothing else, the
                // same bargain `ShaderGraphPreviewRenderer` makes: a thumbnail that uploaded nothing
                // is indistinguishable from one that has not been decoded yet, so something has to be
                // able to look. A texture without it is one no readback may touch.
                TextureUsage.Sampled | TextureUsage.CopyDestination | TextureUsage.CopySource,
                Name: "thumbnail"
            )
        );

        var staging = device.CreateBuffer(
            new(rgba.Length, BufferUsage.CopySource, MemoryAccess.HostUpload, "thumbnail staging")
        );

        device.Write(staging, 0, rgba);

        var view = device.CreateTextureView(texture);
        var image = next++;

        renderer.RegisterImage(image, view);
        live[image] = new Uploaded(image, texture, view, staging, width, height);
        waiting.Add(new Pending(image, texture, staging, width, height));

        return image;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Queued for <see cref="Flush" /> rather than copied here, for the reason
    ///         <see cref="Upload" /> is</b> — this runs from the application's update, outside the
    ///         frame, and a command list recorded there is allocated from the pool the coming
    ///         <c>BeginFrame</c> is about to reset.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the deferral is what makes writing into a live texture safe.</b> Unlike an
    ///         upload, this texture has already been drawn from and the last frame may still be
    ///         sampling it. What orders the copy behind that frame is the barrier out of
    ///         <c>ShaderRead</c> in <see cref="Flush" />: a barrier's source scope covers everything
    ///         earlier in submission order on the same queue, previously submitted lists included, so
    ///         no second texture and no wait is needed. ⚠ It is therefore <em>this</em> class's
    ///         ordering that carries it, and not <c>IGraphicsDevice.Destroy</c>'s deferral, which is
    ///         what carries every other lifetime here.
    ///     </para>
    ///     <para>
    ///         Several updates to the same image in one frame are all recorded, in order, which is
    ///         what a drag produces: the last one wins where they overlap, and each is a copy of its
    ///         own rectangle rather than of the picture.
    ///     </para>
    /// </remarks>
    public bool Update(ulong image, int x, int y, int width, int height, ReadOnlySpan<byte> rgba) {
        if (!live.TryGetValue(image, out var uploaded)) {
            return false;
        }

        // ⚠ Bounded against the texture and not against the caller's arithmetic. A copy that runs
        // off the edge is undefined behaviour in the backend rather than an error, and the caller
        // here is a plugin.
        if (width <= 0
            || height <= 0
            || x < 0
            || y < 0
            || x + width > uploaded.Width
            || y + height > uploaded.Height
            || rgba.Length < width * height * 4) {
            return false;
        }

        var staging = device.CreateBuffer(
            new(width * height * 4, BufferUsage.CopySource, MemoryAccess.HostUpload, "thumbnail patch staging")
        );

        device.Write(staging, 0, rgba[..(width * height * 4)]);
        patching.Add(new Patch(image, uploaded.Texture, staging, x, y, width, height));

        return true;
    }

    /// <summary>Submits the copies <see cref="Upload" /> has queued, on the frame that owns them.</summary>
    /// <returns>How many were copied.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Called between <c>BeginFrame</c> and <c>EndFrame</c>, and before the interface
    ///         records.</b> Submits on one queue run in order, so a tile that draws a number this
    ///         same frame samples a texture whose copy and whose transition to <c>ShaderRead</c> are
    ///         already ahead of it — which is why the registration can happen at <see cref="Upload" />
    ///         and be correct.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One list for every pending upload rather than one each.</b> A folder scrolled
    ///         into view finishes decoding in a burst, and a submission per thumbnail is a queue
    ///         submit per picture for work that is a few kilobytes of copy.
    ///     </para>
    /// </remarks>
    public int Flush() {
        if (waiting.Count == 0 && patching.Count == 0) {
            return 0;
        }

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "thumbnails")) {
            foreach (var pending in waiting) {
                commands.Barrier(
                    new([], [new(pending.Texture, ResourceState.Undefined, ResourceState.CopyDestination)])
                );

                commands.CopyBufferToTexture(
                    pending.Staging,
                    0,
                    new(pending.Texture),
                    new(pending.Width, pending.Height, 1)
                );

                commands.Barrier(
                    new([], [new(pending.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)])
                );
            }

            // ⚠ After the uploads and in the same list, which is what lets a picture be made and
            // patched before it has ever been drawn: the loop above has already taken this texture
            // Undefined → CopyDestination → ShaderRead, so the barrier below has a state to leave.
            foreach (var patch in patching) {
                commands.Barrier(
                    new([], [new(patch.Texture, ResourceState.ShaderRead, ResourceState.CopyDestination)])
                );

                commands.CopyBufferToTexture(
                    patch.Staging,
                    0,
                    // ⚠ The origin is the whole of the saving. The staging buffer holds the
                    // rectangle's own rows tightly packed, so the copy moves `width × height × 4`
                    // bytes whatever the texture is — which is the cost #912 was filed about.
                    new(patch.Texture, Origin: new(patch.X, patch.Y, 0)),
                    new(patch.Width, patch.Height, 1)
                );

                commands.Barrier(
                    new([], [new(patch.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)])
                );

                spent.Add(patch.Staging);
            }

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        var copied = waiting.Count + patching.Count;

        Submitted += copied;
        waiting.Clear();
        patching.Clear();

        return copied;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A pending copy for the released image is dropped with it.</b> An eviction can land in
    ///     the same <c>Pump</c> that made the image — a cache filled past its capacity in one drain —
    ///     and <see cref="Retire" /> runs before the frame's <see cref="Flush" />, so a copy left
    ///     queued would name a texture that has already been destroyed.
    /// </remarks>
    public void Release(ulong image) {
        if (live.Remove(image, out var uploaded)) {
            retiring.Add(uploaded);
        }

        waiting.RemoveAll(pending => pending.Image == image);

        // ⚠ And the queued patches, for the same reason and with one more step: a patch owns its
        // staging buffer outright, so dropping the record without destroying it is a leak of exactly
        // as many buffers as the drag had pointer moves.
        foreach (var patch in patching) {
            if (patch.Image == image) {
                device.Destroy(patch.Staging);
            }
        }

        patching.RemoveAll(patch => patch.Image == image);
    }

    /// <summary>Hands back what has been released, for the device to free once no frame holds it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The host does <i>not</i> wait for the device before calling this, and the comment
    ///         that said it did was the whole of task #364.</b> <c>EditorHost.Sync</c> calls this
    ///         between <c>EndFrame</c> and the next <c>BeginFrame</c>; the only <c>WaitIdle</c> near
    ///         it belongs to the loop that closes a removed pane, which runs on the frames a window
    ///         is closed and no others. What keeps this safe is
    ///         <see cref="IGraphicsDevice.Destroy(TextureHandle)" />'s own deferral — and that
    ///         deferral was zero frames wide for a caller outside a frame until
    ///         <c>VulkanDevice.Retire</c> was taught which slot it was in.
    ///     </para>
    ///     <para>
    ///         So this is a batching step and not a safety one: it holds a released image until the
    ///         host's next <c>Sync</c> so that a release and its destroy are one pass rather than
    ///         scattered through the update. The lifetime guarantee is the device's.
    ///     </para>
    /// </remarks>
    public void Retire() {
        foreach (var uploaded in retiring) {
            Destroy(uploaded);
        }

        retiring.Clear();

        foreach (var staging in spent) {
            device.Destroy(staging);
        }

        spent.Clear();
    }

    /// <summary>The texture behind an image number, for a caller that wants to read it back.</summary>
    /// <param name="image">What <see cref="Upload" /> returned.</param>
    /// <returns>The texture, or an invalid handle if there is no such image.</returns>
    /// <remarks>
    ///     Left in <see cref="ResourceState.ShaderRead" /> once <see cref="Flush" /> has run, so a
    ///     reader barriers from there.
    /// </remarks>
    public TextureHandle TextureOf(ulong image) => live.TryGetValue(image, out var uploaded) ? uploaded.Texture : default;

    /// <inheritdoc />
    public void Dispose() {
        Retire();

        // Nothing was submitted for these, so there is no work to wait on — the textures are
        // destroyed by the loop below like any other.
        waiting.Clear();

        // ⚠ A patch's staging buffer has no other owner, so it has to be named here. The textures
        // below are the images'; these are not.
        foreach (var patch in patching) {
            device.Destroy(patch.Staging);
        }

        patching.Clear();

        foreach (var uploaded in live.Values) {
            Destroy(uploaded);
        }

        live.Clear();
    }

    /// <summary>Takes the number back and then frees what it named.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The unregistration is first, and it is the half this class went without.</b>
    ///         <c>UiRenderer.RegisterImage</c> does not own the view — its own remarks say
    ///         "unregister before destroying" — and a registration left behind is a descriptor set
    ///         naming freed memory for as long as the renderer lives. What kept that from being a
    ///         crash is that <see cref="next" /> never reuses a number, so no <i>new</i> draw could
    ///         name a retired one; what a draw list built earlier still names is
    ///         <c>ProjectBrowser.Rebind</c>'s business, and resting a lifetime on a panel three
    ///         classes away calling <c>Refresh</c> before the frame draws is not a guarantee. After
    ///         this, a draw that still carries the number is skipped by <c>UiRenderer.SubmitDraw</c>
    ///         — a tile with no picture in it, which is what the grid shows before a decode finishes
    ///         anyway.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is also the whole of the leak.</b> A registration holds one descriptor set per
    ///         frame in flight, and a backend cannot free one — so a browser scrolled through a
    ///         thousand textures took a thousand rings and gave none of them back.
    ///         <c>UiRenderer.UnregisterImage</c> keeps the ring for the next number, which is why
    ///         handing it back is what makes the re-decode free rather than merely correct.
    ///     </para>
    /// </remarks>
    void Destroy(Uploaded uploaded) {
        renderer.UnregisterImage(uploaded.Image);

        device.Destroy(uploaded.View);
        device.Destroy(uploaded.Texture);
        device.Destroy(uploaded.Staging);
    }

    readonly record struct Uploaded(
        ulong Image,
        TextureHandle Texture,
        TextureViewHandle View,
        BufferHandle Staging,
        int Width,
        int Height
    );

    readonly record struct Pending(ulong Image, TextureHandle Texture, BufferHandle Staging, int Width, int Height);

    readonly record struct Patch(
        ulong Image,
        TextureHandle Texture,
        BufferHandle Staging,
        int X,
        int Y,
        int Width,
        int Height
    );
}
