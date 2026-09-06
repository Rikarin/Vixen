// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Texturing.Painting;

/// <summary>
///     How a compiled stack names one channel of one <c>.vxpaint</c>, so that a host can resolve it.
/// </summary>
/// <remarks>
///     <para>
///         <b>A scheme rather than a path, and the precedent is <c>meshmap:</c>.</b> A
///         <c>Source/Bitmap</c> node carries one string, and a host resolves it against the project's
///         assets. A <c>.vxpaint</c> is neither an asset in that database nor one picture: it holds
///         <em>one image per channel</em>, so a bare path would name a file and not say which of the
///         images in it a given channel of the stack reads.
///     </para>
///     <para>
///         ⚠ <b>The usage comes first and the path last, because only the path can contain the
///         separator.</b> A usage is an identifier — the nine <c>Output/Output</c> knows, plus
///         <see cref="Mask" /> — and a path is whatever an artist called a file in whatever folder
///         they put it in. Parsing from the front therefore has exactly one ambiguous field and it
///         is the last one, so <see cref="TryParse" /> never has to guess.
///     </para>
///     <para>
///         ⚠ <b>This is not a second opinion about where the file is.</b> The path is
///         <c>LayerAsset.Paint</c> verbatim, relative to the stack, and resolving it against a folder
///         is the host's job — <c>LayerStackPreview</c>'s, which is the only thing that knows where
///         the open document lives. A compiler that resolved it would be a compiler that touched the
///         file system on every edit.
///     </para>
/// </remarks>
static class PaintReference {
    /// <summary>What a painted reference starts with, rather than a project path.</summary>
    public const string Scheme = "vxpaint:";

    /// <summary>What a mask's single channel is called inside a <c>.vxpaint</c>.</summary>
    /// <remarks>
    ///     A mask's canvas is <c>PaintCanvas</c>'s degenerate case — one channel and no special
    ///     format — so it still needs a name, and the name has to be one no output usage can collide
    ///     with. <c>Output/Output</c> accepts nine usages and none of them is this word.
    /// </remarks>
    public const string Mask = "mask";

    /// <summary>
    ///     What separates the usage from the path. ⚠ Not a character a file name may contain on
    ///     Windows, which is what makes the split unambiguous rather than conventional.
    /// </summary>
    const char Separator = '|';

    /// <summary>Names one channel of one paint file.</summary>
    /// <param name="path">The <c>.vxpaint</c>, relative to the stack.</param>
    /// <param name="usage">Which channel of it — <c>baseColor</c>, <c>roughness</c>, <see cref="Mask" />.</param>
    /// <returns>The reference a <c>Source/Bitmap</c> carries.</returns>
    /// <exception cref="ArgumentException">Either part is blank, or the usage contains the separator.</exception>
    public static string Reference(string path, string usage) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(usage);

        var channel = usage.Trim();

        if (channel.Contains(Separator, StringComparison.Ordinal)) {
            throw new ArgumentException(
                $"The channel usage '{channel}' contains '{Separator}', which is what separates it from the "
                + "path. A usage is an identifier; a path is the half allowed to contain anything.",
                nameof(usage)
            );
        }

        return $"{Scheme}{channel}{Separator}{path.Trim()}";
    }

    /// <summary>Whether a bitmap reference is a painted one, and what it names.</summary>
    /// <param name="reference">The <c>Source</c> text a compilation carried.</param>
    /// <param name="path">The <c>.vxpaint</c> it names, relative to the stack.</param>
    /// <param name="usage">Which channel of it.</param>
    /// <returns>
    ///     <see langword="true" /> when it is a well-formed painted reference. A string that starts
    ///     with the scheme and is not well formed answers <see langword="false" /> with both parts
    ///     empty, so a caller that reports "this is not a file" gets that answer rather than
    ///     attempting to open a file called <c>vxpaint:…</c>.
    /// </returns>
    public static bool TryParse(string? reference, out string path, out string usage) {
        path = "";
        usage = "";

        if (reference is null || !reference.StartsWith(Scheme, StringComparison.Ordinal)) {
            return false;
        }

        var rest = reference[Scheme.Length..];
        var split = rest.IndexOf(Separator, StringComparison.Ordinal);

        if (split <= 0 || split == rest.Length - 1) {
            return false;
        }

        usage = rest[..split];
        path = rest[(split + 1)..];

        return usage.Length > 0 && path.Length > 0;
    }

    /// <summary>Whether a bitmap reference claims to be a painted one, well formed or not.</summary>
    /// <param name="reference">The <c>Source</c> text.</param>
    /// <returns>Whether it starts with the scheme.</returns>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="TryParse" /> on purpose.</b> A host that used the parse
    ///     alone would treat a malformed painted reference as a project path and tell an artist that
    ///     an asset named <c>vxpaint:baseColor</c> is missing, which names nothing they wrote.
    /// </remarks>
    public static bool Claims(string? reference) =>
        reference is not null && reference.StartsWith(Scheme, StringComparison.Ordinal);
}
