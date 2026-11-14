// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.TextureGraph;

public sealed partial class TexturePlanEvaluator {
    /// <summary>Writes results a bake holds to <see cref="TextureResultCache.Store" />, for a later session to read.</summary>
    /// <param name="bake">An incremental evaluation's bake, still alive.</param>
    /// <param name="images">Which of its images to keep, in <see cref="TexturePlan.Images" />.</param>
    /// <returns>How many were written: those not kept already, which the bake holds whole.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The device already has a frame open.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>A read-back per result, one at a time</b>, so a 4K stack's dozens of pictures are never all in
    ///         memory at once. That is why it is the host's call and never the evaluator's: it is for a save, not a
    ///         frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only a whole picture under its own key.</b> An image the caller supplied is the caller's; one
    ///         neither the bake nor the cache holds whole, pooled or repaired over rectangles, is gone; and one keyed
    ///         by a <c>Nonce</c> depends on a picture nothing can name again. None of those is written. What the bake
    ///         does not hold and the cache does is read from the cache.
    ///     </para>
    /// </remarks>
    public int Persist(TextureBake bake, IEnumerable<int> images) {
        ArgumentNullException.ThrowIfNull(bake);
        ArgumentNullException.ThrowIfNull(images);

        if (Results.Store is not { } store || bake.Keys.IsDefaultOrEmpty) {
            return 0;
        }

        var written = 0;

        foreach (var image in images.Distinct()) {
            var key = bake.Keys[image];

            if (bake.Plan.Images[image].External || !key.IsValid || TextureResultCache.IsNonce(key) || store.Has(key)) {
                continue;
            }

            var shape = ShapeOf(bake.Plan, image);

            // ⚠ The cache's own copy when the bake has none. A bake holds what it ran and what it was asked for, so
            // the bake of a save, which ran nothing, holds the outputs and not one image beneath them.
            var lent = !bake.TextureOf(image).IsValid
                && Results.Find(key, shape) is { } entry
                && bake.Lend(image, entry.Texture);

            if (!bake.TextureOf(image).IsValid) {
                continue;
            }

            try {
                store.Write(key, shape, bake.ReadTexels(image));
                written++;
            } finally {
                if (lent) {
                    bake.Unlend(image);
                }
            }
        }

        return written;
    }

    /// <summary>A result the store keeps, uploaded into the cache, or null when it keeps none.</summary>
    /// <param name="key">The key the evaluation wants.</param>
    /// <param name="shape">The shape the plan says it has.</param>
    /// <returns>The entry, unpinned, as though an evaluation had filed it.</returns>
    /// <remarks>
    ///     ⚠ <b>Asked only for what the walk wants and the cache does not hold</b>, so a result above a hit is never
    ///     read from disk, and an op the edit changed costs one <see cref="ITextureResultStore.Has" />.
    /// </remarks>
    TextureResultCache.Entry? Recall(TextureResultKey key, TexturePoolSlot shape) {
        if (Results.Store is not { } store || TextureResultCache.IsNonce(key) || !store.Has(key)) {
            return null;
        }

        if (store.Read(key, shape) is not { } texels || texels.Length != TextureResultCache.BytesOf(shape)) {
            return null;
        }

        var rented = Results.Rent(shape, "texture graph recalled result");

        try {
            Upload(rented.Texture, shape, texels);
        } catch {
            Results.Return(rented);

            throw;
        }

        var entry = Results.Add(key, rented);

        Results.Unpin(entry);
        Results.Recall();

        return entry;
    }

    /// <summary>Copies texels into a pooled texture and leaves it readable, as a run leaves a result.</summary>
    void Upload(TextureHandle texture, TexturePoolSlot shape, byte[] texels) {
        var staging = device.CreateBuffer(
            new(texels.Length, BufferUsage.CopySource, MemoryAccess.HostUpload, "texture graph recall staging")
        );
        var queue = device.ComputeQueue;

        try {
            device.Write(staging, 0, texels);
            device.BeginFrame();

            using (var commands = device.BeginCommandList(queue.Kind, "texture graph recall")) {
                commands.Barrier(
                    new BarrierGroup(
                        [],
                        [new TextureBarrier(texture, ResourceState.Undefined, ResourceState.CopyDestination)]
                    )
                );
                commands.CopyBufferToTexture(staging, 0, new(texture), new(shape.Width, shape.Height, 1));
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
            queue.WaitIdle();
        } finally {
            device.Destroy(staging);
        }
    }
}
