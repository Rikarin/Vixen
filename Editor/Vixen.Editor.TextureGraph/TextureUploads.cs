// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Editor.TextureGraph;

/// <summary>Turns CPU pixels into the textures a plan's external images are read from.</summary>
/// <remarks>
///     <para>
///         <b>The one step
///         <see cref="TexturePlanEvaluator.Evaluate(TexturePlan,IReadOnlyDictionary{int,TextureExternal})" />
///         does not do, and every external image needs it.</b> A <see cref="TextureImage" /> marked
///         <see cref="TextureImage.External" /> says "the caller supplies this one", and
///         <c>Evaluate</c> takes a dictionary of handles the caller has already created — so a plan
///         has always been able to express a picture that came from somewhere other than a kernel,
///         and nothing in this assembly could produce one. <see cref="Externals" /> is what goes into
///         that parameter.
///     </para>
///     <para>
///         ⚠ <b>This is what doc 48 § 4.1's <c>Text</c> and <c>Svg Path</c> reach the GPU through,
///         and it is why neither is a kernel</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/687">#687</a>. A compute kernel has no
///         rasteriser, cannot reach a font or a path parser (the evaluator compiles each kernel alone,
///         with no reference paths), and could not be given one: both of those shapes are filled on
///         the CPU, by <c>Vixen.Ui.Text</c>'s <c>GlyphRasterizer</c>, and arrive here as coverage.
///         <see cref="AddCoverage" /> is that door.
///     </para>
///     <para>
///         <b>A type of its own rather than a method on the evaluator, and the reason is a
///         lifetime.</b> A <see cref="TextureBake" /> owns its textures and destroys them when it is
///         disposed; an uploaded bitmap outlives any one bake, because the interactive preview of doc
///         48 § M4 re-evaluates the same plan over the same imported picture many times a second. An
///         upload owned by a bake would be destroyed by the evaluation after the one that made it,
///         and re-uploading a 4K bitmap per keystroke is the cost that arrangement hides. So the two
///         lifetimes are two objects, and this one is disposed when the *document* closes.
///     </para>
///     <para>
///         ⚠ <b>It records and submits on <see cref="IGraphicsDevice.ComputeQueue" />, and the
///         command list's kind comes from that same submitter rather than being spelled.</b> Every
///         texture a bake touches is <c>ResourceSharing.Exclusive</c>, so filling one from a second
///         queue family without an ownership transfer leaves its contents <b>undefined by
///         specification</b>, the validation layers say nothing because it is undefined behaviour
///         rather than invalid usage, and on every adapter this engine has been developed on the two
///         families are one — a clean picture here and a corrupt one on a discrete card.
///         <a href="https://github.com/Rikarin/Vixen/issues/617">#617</a> was that defect in the
///         evaluator and <a href="https://github.com/Rikarin/Vixen/issues/679">#679</a> was the same
///         defect in the tests' own upload helper, three months apart, because the queue was chosen
///         in two places. It is chosen once here.
///     </para>
///     <para>
///         ⚠ <b>An upload is not a frame.</b> It opens one, submits a single command list and waits,
///         exactly as <c>Evaluate</c> does — so it must not be called between a caller's own
///         <c>BeginFrame</c> and <c>EndFrame</c>.
///     </para>
///     <para>
///         ⚠ <b>What no test here proves: that the transition to
///         <see cref="ResourceState.ShaderRead" /> at the end of the copy is needed.</b> Deleting it
///         leaves every case in <c>TextureUploadDeviceTests</c> green on MoltenVK — measured, not
///         assumed. Metal has no image layouts, so the barrier a Vulkan implementation insists on
///         costs nothing here and says nothing. It is the same blind spot as the queue above and has
///         the same answer: the rule is written down and followed, because the machine it protects is
///         not this one.
///     </para>
/// </remarks>
public sealed class TextureUploads : IDisposable {
    readonly IGraphicsDevice device;
    readonly Dictionary<int, Int2> sizes = [];
    readonly Dictionary<int, TextureExternal> declared = [];

    bool disposed;

    /// <summary>Builds an upload set on a device.</summary>
    /// <param name="device">Where the textures live. The same one the evaluator will run on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public TextureUploads(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
    }

    /// <summary>What every upload is created with, and therefore what it is declared as.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One expression, read twice</b> — by <see cref="Upload" /> and by
    ///         <see cref="Externals" /> — because a declaration that does not match the creation puts
    ///         back exactly the undefined behaviour the declaration exists to refuse.
    ///         <c>TextureKernelHarness.SourceUsage</c> is the same shape for the same reason.
    ///     </para>
    ///     <para>
    ///         <see cref="TextureUsage.Sampled" /> because a kernel reads it and because every
    ///         external image is viewed and held readable for the whole bake;
    ///         <see cref="TextureUsage.CopyDestination" /> because this fills it; and
    ///         <see cref="TextureUsage.CopySource" /> because a <see cref="TextureOp.Cpu" /> op copies
    ///         out of the image it reads — <a href="https://github.com/Rikarin/Vixen/issues/744">#744</a>.
    ///         Deliberately no <see cref="TextureUsage.Storage" />: an external image is never written
    ///         by an op, and <c>TexturePlan.Validate</c> refuses a plan where one is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>CopySource</c> is not a nicety and it is not visible here.</b> Nothing in
    ///         production builds a plan with a CPU op yet — § 4.6's <c>Normal → Height</c> Poisson
    ///         solve is the node that will — so today this only decides whether that node's first bake
    ///         is a refusal. On a unified adapter it would have been neither: MoltenVK enforces no
    ///         usage bits, and the wrong answer belongs to a discrete card.
    ///     </para>
    /// </remarks>
    public const TextureUsage UploadUsage =
        TextureUsage.Sampled | TextureUsage.CopyDestination | TextureUsage.CopySource;

    /// <summary>
    ///     What to hand <see cref="TexturePlanEvaluator.Evaluate(TexturePlan,IReadOnlyDictionary{int,TextureExternal})" />
    ///     as its externals.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Keyed by the image's index in <see cref="TexturePlan.Images" />, which is what the
    ///         evaluator looks each one up by. Every texture in here is already in
    ///         <see cref="ResourceState.ShaderRead" />, which is the state <c>Evaluate</c> documents it
    ///         expects an external image to arrive in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <see cref="TextureExternal" /> rather than a bare handle, and that is the whole
    ///         point of the type</b> — <a href="https://github.com/Rikarin/Vixen/issues/744">#744</a>.
    ///         The bare-handle overload of <c>Evaluate</c> declares <see cref="TextureUsage.Sampled" />
    ///         and nothing else, so an upload passed through it could not be read by a CPU op however
    ///         it was created. There is no way to get the handles out of here without the usage beside
    ///         them, because a caller who could would be the caller who gets it wrong.
    ///     </para>
    /// </remarks>
    public IReadOnlyDictionary<int, TextureExternal> Externals => declared;

    /// <summary>How many images have been uploaded.</summary>
    public int Count => declared.Count;

    /// <summary>Uploads texels for one of a plan's external images.</summary>
    /// <param name="plan">The plan the image belongs to.</param>
    /// <param name="image">Its index in <see cref="TexturePlan.Images" />.</param>
    /// <param name="width">The picture's width in texels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="texels">
    ///     The bytes, tightly packed, top row first, in the image's own
    ///     <see cref="TextureImage.Format" />.
    /// </param>
    /// <returns>The texture, which this object owns until it is disposed.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="TextureFormat.R8" /> and <see cref="TextureFormat.Rg8" /> are
    ///         perfectly good here, and that is not a contradiction of
    ///         <see cref="TextureFormats.IsStorable" />.</b> That predicate is about what a *kernel*
    ///         may write, and neither format is a storage image on a conformant device. Reading one
    ///         is fine and uploading one is fine — a mask costs a quarter of what it costs as RGBA,
    ///         and a sampled read of it hands a kernel <c>(r, 0, 0, 1)</c>. So this method must not
    ///         be guarded by <c>IsStorable</c>, and a test says so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The size is the caller's and is not checked against the plan, deliberately.</b>
    ///         An external image is the one place an absolute size enters a plan — an imported
    ///         bitmap is whatever size it is — and every kernel clamps its taps to the *source's*
    ///         dimensions rather than the target's, so a picture that does not match the plan's base
    ///         resolution is read correctly. <see cref="SizeOf" /> is what remembers it, because
    ///         <see cref="TexturePlan.SizeOf" /> computes a size from the image's level and for an
    ///         external image that number is nominal.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="plan" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="image" /> is not in the plan's table, or an extent is not positive.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///     The image is not external, it has already been uploaded, or the byte count is not the one
    ///     the size and the format imply.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This set has been disposed.</exception>
    public TextureHandle Add(TexturePlan plan, int image, int width, int height, ReadOnlySpan<byte> texels) {
        RefuseInsideAFrame();

        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (image < 0 || image >= plan.Images.Length) {
            throw new ArgumentOutOfRangeException(
                nameof(image),
                image,
                $"The plan's image table holds {plan.Images.Length}."
            );
        }

        if (!plan.Images[image].External) {
            // ⚠ A refusal rather than an upload nothing reads. `Evaluate` consults the externals
            // dictionary only for images the plan marks external, so a handle filed under an
            // internal image's index is silently ignored — and the picture that comes out is the
            // one a kernel wrote over the caller's own pixels, which is a plausible picture.
            throw new ArgumentException(
                $"Image {image} is not external, so nothing would ever read an upload for it. An image a "
                + "kernel writes is allocated by the pool; only an image the plan marks External is the "
                + "caller's to supply.",
                nameof(image)
            );
        }

        if (declared.ContainsKey(image)) {
            throw new ArgumentException(
                $"Image {image} has already been uploaded. A second upload would leak the first, because this "
                + "set is what owns them.",
                nameof(image)
            );
        }

        var format = plan.Images[image].Format;
        var expected = (long)width * height * TextureFormats.BytesPerTexel(format);

        if (texels.Length != expected) {
            // ⚠ Not a copy of whatever fits. A short buffer copied into a texture leaves the tail
            // undefined, which reads as a picture whose bottom rows are somebody else's memory.
            throw new ArgumentException(
                $"Image {image} is {width}×{height} of {format}, which is {expected} bytes, and {texels.Length} "
                + "were given.",
                nameof(texels)
            );
        }

        var texture = Upload(image, format, width, height, texels);

        declared[image] = new(texture, UploadUsage);
        sizes[image] = new(width, height);

        return texture;
    }

    /// <summary>Uploads a coverage field — one float per texel — as a single-channel mask.</summary>
    /// <param name="plan">The plan the image belongs to.</param>
    /// <param name="image">Its index in <see cref="TexturePlan.Images" />, and it has to be an <c>R8</c>.</param>
    /// <param name="width">The field's width in texels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="coverage">Row-major, top row first, clamped into <c>[0, 1]</c> on the way in.</param>
    /// <returns>The texture, which this object owns until it is disposed.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>This is the shape <c>Vixen.Ui.Text</c>'s <c>CoverageBitmap.Coverage</c> already
    ///         has</b> — <c>float[]</c>, row-major, row 0 at the top, one value per pixel in
    ///         <c>[0, 1]</c> — so a rasterised string or a filled path is one call from being an
    ///         image in a plan. It is deliberately a <c>ReadOnlySpan&lt;float&gt;</c> and not that
    ///         type, so that this assembly takes no reference to a text stack to accept a number per
    ///         texel.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Rounded rather than truncated.</b> <c>(byte)(c * 255)</c> maps 1.0 to 255 and
    ///         everything else a step low — a half-covered edge texel comes out at 127 — and the
    ///         error is entirely on the dark side, so a rasterised glyph uploaded that way is
    ///         measurably thinner than the one that was drawn.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="plan" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     The image is not an <see cref="TextureFormat.R8" />, or the field is not
    ///     <paramref name="width" /> × <paramref name="height" /> long — plus everything
    ///     <see cref="Add" /> refuses.
    /// </exception>
    public TextureHandle AddCoverage(
        TexturePlan plan,
        int image,
        int width,
        int height,
        ReadOnlySpan<float> coverage
    ) {
        RefuseInsideAFrame();

        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (image >= 0 && image < plan.Images.Length && plan.Images[image].Format != TextureFormat.R8) {
            throw new ArgumentException(
                $"Image {image} is {plan.Images[image].Format}, and a coverage field is one channel. A mask is "
                + "an R8: it is a quarter of the bytes and a kernel reads it as (r, 0, 0, 1).",
                nameof(image)
            );
        }

        if (coverage.Length != width * height) {
            throw new ArgumentException(
                $"A {width}×{height} coverage field is {width * height} values, and {coverage.Length} were given.",
                nameof(coverage)
            );
        }

        var texels = new byte[width * height];

        for (var at = 0; at < texels.Length; at++) {
            texels[at] = Quantize(coverage[at]);
        }

        return Add(plan, image, width, height, texels);
    }

    /// <summary>How big one uploaded image actually is.</summary>
    /// <param name="image">Its index in <see cref="TexturePlan.Images" />.</param>
    /// <returns>The size in texels.</returns>
    /// <remarks>
    ///     ⚠ <b>Here because <see cref="TexturePlan.SizeOf" /> cannot answer it.</b> That method
    ///     reads a size off the image's level and the plan's base resolution, which for an image the
    ///     plan does not allocate is a number nothing produced — the plan's own <c>Validate</c> says
    ///     as much when it skips an external image's level entirely. Whoever uploaded the picture is
    ///     the only thing that knows its size, so it is remembered here.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">Nothing has been uploaded for that image.</exception>
    public Int2 SizeOf(int image) => sizes[image];

    /// <summary>One coverage value as the byte a mask stores.</summary>
    /// <param name="coverage">A number in <c>[0, 1]</c>; anything outside is clamped.</param>
    /// <returns>The eight-bit level.</returns>
    /// <remarks>
    ///     ⚠ <b>Internal so that the rounding can be asserted with no device</b>, which is the point:
    ///     a device test skips on a machine with no adapter, and this is the half of
    ///     <see cref="AddCoverage" /> that a reader gets wrong. <c>(byte)(c * 255)</c> is the natural
    ///     spelling, it maps every value but 1.0 a step low, and the error is all on the dark side —
    ///     a rasterised glyph uploaded that way is thinner than the one that was drawn, uniformly,
    ///     which looks like a font weight rather than like a bug.
    /// </remarks>
    internal static byte Quantize(float coverage) => (byte)((Math.Clamp(coverage, 0f, 1f) * 255f) + 0.5f);


    /// <summary>Refuses an upload made from inside a caller's own frame.</summary>
    /// <remarks>
    ///     ⚠ <b><c>TexturePlanEvaluator.RefuseInsideAFrame</c>'s refusal, on the path a guarded
    ///     caller reaches first.</b> #775 put the sentence on this class and the check on
    ///     <c>Evaluate</c> and <c>Read</c> only — and an upload is what a caller does *before* it
    ///     evaluates, so the trap survived on the entry point that is used earliest. An upload opens
    ///     a frame, submits one command list and waits, exactly as an evaluation does, so the damage
    ///     is the same: the nested <c>BeginFrame</c> resets the command pools of the slot the caller
    ///     is recording into, and every frame after the caller's own <c>EndFrame</c> waits on a fence
    ///     nothing signalled.
    /// </remarks>
    void RefuseInsideAFrame() {
        if (!device.IsFrameOpen) {
            return;
        }

        throw new InvalidOperationException(
            "A texture cannot be uploaded inside a frame: this opens and closes one of its own, which "
            + "resets the command pools of the slot the caller is recording into and leaves the "
            + "caller's fences a slot behind for the rest of the session. Upload from outside "
            + "BeginFrame/EndFrame, as the editor's own callers do."
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        device.WaitIdle();

        foreach (var external in declared.Values) {
            if (external.Texture.IsValid) {
                device.Destroy(external.Texture);
            }
        }

        declared.Clear();
        sizes.Clear();
    }

    TextureHandle Upload(int image, TextureFormat format, int width, int height, ReadOnlySpan<byte> texels) {
        var name = string.Create(CultureInfo.InvariantCulture, $"texture graph external {image}");

        var texture = device.CreateTexture(
            new(
                TextureFormats.Pixel(format),
                width,
                height,
                // The same expression `Externals` declares, and it is one constant precisely so that
                // the creation and the declaration cannot say different things — #744.
                UploadUsage,
                Name: name
            )
        );

        var staging = device.CreateBuffer(
            new(texels.Length, BufferUsage.CopySource, MemoryAccess.HostUpload, "texture graph upload staging")
        );

        // ⚠ One expression decides the queue and everything below reads it — the list's kind, the
        // submission and the wait. Spelling `QueueKind.Compute` separately from
        // `device.ComputeQueue` is how #617 and #679 both happened.
        var queue = device.ComputeQueue;

        try {
            device.Write(staging, 0, texels);
            device.BeginFrame();

            using (var commands = device.BeginCommandList(queue.Kind, "texture graph upload")) {
                commands.Barrier(
                    new BarrierGroup(
                        [],
                        [new TextureBarrier(texture, ResourceState.Undefined, ResourceState.CopyDestination)]
                    )
                );

                commands.CopyBufferToTexture(staging, 0, new(texture), new(width, height, 1));

                // Left readable, because that is the state `Evaluate` documents an external image
                // has to arrive in and there is no later point at which anything would do it.
                commands.Barrier(
                    new BarrierGroup(
                        [],
                        [new TextureBarrier(texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]
                    )
                );

                commands.Finish();
                queue.Submit([commands]);
            }

            device.EndFrame();

            // The queue rather than the device: it is the one call here that leaves a record of
            // which queue the copy went to, which is what `TextureUploadQueueTests` reads.
            queue.WaitIdle();
        } catch {
            device.Destroy(texture);
            device.Destroy(staging);

            throw;
        }

        device.Destroy(staging);

        return texture;
    }
}
