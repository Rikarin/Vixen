// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.Texturing.Painting;

namespace Vixen.Editor.Texturing;

/// <summary>What one fill has already asked the session's pixels about, keyed by absolute path.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/981">#981</a>, and the syscalls it
///         saves are the smaller half.</b> A plan carries one external per channel a layer writes,
///         and an unrestricted paint layer writes all seven of
///         <c>LayerStackDocument.DefaultChannels</c> — so <see cref="TextureExternalImages.Fill" />
///         asked <see cref="PaintCanvasStore" /> about the same file seven times per evaluation.
///         Since the store landed six of those are hits and a hit costs a <c>FileInfo</c> stat
///         rather than a read, which is why this is worth a type rather than an alarm.
///     </para>
///     <para>
///         ⚠ <b>The half that is a correctness bug: seven stamps inside one evaluation can straddle
///         a write.</b> A <c>.vxpaint</c> is a file other tools touch — a version-control checkout,
///         a second editor — and the store is honest about that by design: it re-reads whatever the
///         stamp says has changed. Asked twice across a write it therefore answers with two
///         <em>different</em> canvases, correctly, and the evaluation that asked builds one map out
///         of both. A resolution memoised for the duration of the pass is what makes one evaluation
///         see one file.
///     </para>
///     <para>
///         ⚠ <b>Failures are memoised too, and that is the same requirement rather than a
///         nicety.</b> An unmemoised throw is a question re-asked, so a file that appears or goes
///         mid-pass splits the evaluation exactly as a rewrite does — and the caller composes the
///         sentence, so what is held here is the exception's message and not the sentence. Two
///         channels of one file are one answer whichever way that answer came out.
///     </para>
///     <para>
///         <b>Per fill and never longer.</b> Held across evaluations this would be the cache #885
///         refused: the store's stamp is what notices a file the session did not write, and a memo
///         that outlived the pass would be a way of never noticing.
///     </para>
/// </remarks>
/// <param name="canvases">The session's pixels, which every miss here asks.</param>
sealed class TextureExternalPass(PaintCanvasStore canvases) {
    readonly Dictionary<string, (PaintCanvas? Canvas, string? Failure)> painted = new(StringComparer.Ordinal);
    readonly Dictionary<string, (TextureData? Picture, string? Failure)> pictures = new(StringComparer.Ordinal);

    /// <summary>The canvas at a path, asked of the store once per pass.</summary>
    /// <param name="absolute">Where the <c>.vxpaint</c> is.</param>
    /// <returns>
    ///     The canvas, or null with the message of what went wrong — or null with no message, which
    ///     is a layer naming a canvas that is neither open nor on disk.
    /// </returns>
    public (PaintCanvas? Canvas, string? Failure) Canvas(string absolute) {
        if (painted.TryGetValue(absolute, out var already)) {
            return already;
        }

        (PaintCanvas? Canvas, string? Failure) answer;

        try {
            answer = (canvases.Open(absolute), null);
        } catch (Exception failure) when (failure is IOException
            or InvalidDataException or UnauthorizedAccessException or EndOfStreamException) {
            answer = (null, failure.Message);
        }

        painted[absolute] = answer;

        return answer;
    }

    /// <summary>The decoded picture at a path, asked of the store once per pass.</summary>
    /// <param name="absolute">Where the picture is.</param>
    /// <param name="decode">How to read one, called only when neither this pass nor the store holds it.</param>
    /// <returns>The picture, or null with the message of what went wrong.</returns>
    public (TextureData? Picture, string? Failure) Picture(string absolute, Func<string, TextureData> decode) {
        if (pictures.TryGetValue(absolute, out var already)) {
            return already;
        }

        (TextureData? Picture, string? Failure) answer;

        try {
            answer = (canvases.Picture(absolute, decode), null);
        } catch (Exception failure) when (failure is IOException
            or InvalidDataException or NotSupportedException or ArgumentException
            or UnauthorizedAccessException) {
            answer = (null, failure.Message);
        }

        pictures[absolute] = answer;

        return answer;
    }
}
