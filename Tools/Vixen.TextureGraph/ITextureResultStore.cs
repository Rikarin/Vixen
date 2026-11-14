// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.TextureGraph;

/// <summary>Results kept past the evaluator that made them: on disk, between sessions and processes.</summary>
/// <remarks>
///     <para>
///         <b>The disk tier of <see cref="TextureResultCache" />, and the host's to place.</b> A result's key is its
///         content, so a picture written under one in one process is the picture any later process computes for it —
///         on the same device. The key carries no adapter, so a store is the host's statement of what it is shared
///         across, and a host keeps one per adapter and driver.
///     </para>
///     <para>
///         ⚠ <b>Read on a miss and written only when asked.</b> The evaluator reads a result it wants and does not
///         hold; it never writes one by itself, because writing is a read-back per result and that is what the cache
///         exists to keep off the interactive path. <see cref="TexturePlanEvaluator.Persist" /> is the write, for a
///         host to call at a moment such as a save.
///     </para>
/// </remarks>
public interface ITextureResultStore {
    /// <summary>Whether a result is kept here. Asked for every result an evaluation wants and does not hold.</summary>
    /// <param name="key">Its key.</param>
    /// <returns>Whether <see cref="Read" /> would find it.</returns>
    bool Has(TextureResultKey key);

    /// <summary>The texels kept under a key.</summary>
    /// <param name="key">Its key.</param>
    /// <param name="shape">The shape the plan says it has.</param>
    /// <returns>The texels, tightly packed and top row first, or null when it is not kept or not of that shape.</returns>
    byte[]? Read(TextureResultKey key, TexturePoolSlot shape);

    /// <summary>Keeps a result.</summary>
    /// <param name="key">Its key.</param>
    /// <param name="shape">Its shape.</param>
    /// <param name="texels">Its texels, tightly packed and top row first.</param>
    void Write(TextureResultKey key, TexturePoolSlot shape, ReadOnlySpan<byte> texels);
}
