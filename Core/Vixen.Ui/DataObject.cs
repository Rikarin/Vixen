// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Ui;

/// <summary>A payload offered in every representation its source can produce.</summary>
/// <remarks>
///     <para>
///         <b>The negotiation is the point.</b> A drag source rarely knows what will be under the
///         pointer when the button comes up. An asset row dragged out of a browser is a
///         <c>vixen.asset-id</c> to a material slot, a path to a file field and its own name to a
///         text box — the same drag, three answers — so the source offers all three and each target
///         asks for the one it can use. That is <c>NSPasteboard</c>'s model and the DOM's, and it is
///         the half an OS drag-in cannot have, which is why <see cref="DropEvent" /> carries its two
///         fixed representations as well.
///     </para>
///     <para>
///         ⚠ <b>Format names are <c>IClipboard</c>'s vocabulary, values are not.</b>
///         <c>Vixen.Platform.IClipboard.TryGetData</c> is bytes, because a pasteboard is a boundary
///         between processes and bytes are all that crosses it. Both ends of an in-app drag are
///         objects in one heap, so serialising an <c>AssetId</c> to bytes so the panel next door can
///         parse it back is cost with nothing bought. The <i>names</i> match so that the day a drag
///         does leave the process, a source already offering <see cref="DataFormats.Text" /> needs
///         no new vocabulary.
///     </para>
///     <para>
///         ⚠ <b>Ordered, and the order is the source's preference.</b> <see cref="Formats" /> comes
///         back in the order it was offered, so a target that can take several asks for them in
///         turn and gets what the source would rather it had. A dictionary's own order would make
///         that answer depend on hash codes.
///     </para>
/// </remarks>
public sealed class DataObject {
    readonly List<string> order = [];
    readonly Dictionary<string, object> values = new(StringComparer.Ordinal);

    /// <summary>What it can be read as, best first.</summary>
    public IReadOnlyList<string> Formats => order;

    /// <summary>The text representation, or <see langword="null" /> if it offers none.</summary>
    public string? Text => TryGet<string>(DataFormats.Text, out var text) ? text : null;

    /// <summary>The native paths it offers, empty if it offers none.</summary>
    public IReadOnlyList<string> Files =>
        TryGet<IReadOnlyList<string>>(DataFormats.FileUrl, out var files) ? files : [];

    /// <summary>Offers one representation.</summary>
    /// <param name="format">The format name — <see cref="DataFormats" /> for the standard ones.</param>
    /// <param name="value">What it is, in that format.</param>
    /// <remarks>
    ///     Offering the same format twice replaces the value and keeps the original position, so a
    ///     source that revises a representation does not silently demote it below the ones it
    ///     offered afterwards.
    /// </remarks>
    public void Set(string format, object value) {
        ArgumentException.ThrowIfNullOrEmpty(format);
        ArgumentNullException.ThrowIfNull(value);

        if (!values.ContainsKey(format)) {
            order.Add(format);
        }

        values[format] = value;
    }

    /// <summary>Offers it as text.</summary>
    public void SetText(string text) {
        ArgumentNullException.ThrowIfNull(text);
        Set(DataFormats.Text, text);
    }

    /// <summary>Offers it as native paths.</summary>
    public void SetFiles(IReadOnlyList<string> paths) {
        ArgumentNullException.ThrowIfNull(paths);
        Set(DataFormats.FileUrl, paths);
    }

    /// <summary>Whether it can be read in that format.</summary>
    public bool Has(string format) {
        ArgumentNullException.ThrowIfNull(format);

        return values.ContainsKey(format);
    }

    /// <summary>Reads one representation.</summary>
    /// <typeparam name="T">What the format's value is expected to be.</typeparam>
    /// <param name="format">The format name.</param>
    /// <param name="value">The value, if it was offered as a <typeparamref name="T" />.</param>
    /// <returns><see langword="false" /> if it was not offered, or was offered as something else.</returns>
    /// <remarks>
    ///     ⚠ <b>A format offered as the wrong type answers <see langword="false" /> rather than
    ///     throwing</b>, which is the same answer as not offering it at all — and is right, because
    ///     a target asking for a format it understands and a target asking for a format some other
    ///     source spelled the same way are indistinguishable here, and the second must not take the
    ///     application down.
    /// </remarks>
    public bool TryGet<T>(string format, [NotNullWhen(true)] out T? value) {
        ArgumentNullException.ThrowIfNull(format);

        if (values.TryGetValue(format, out var stored) && stored is T typed) {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }
}

/// <summary>The format names a drag source and a drop target agree on without arranging it.</summary>
/// <remarks>
///     Reverse-DNS-ish and lifted from the platforms rather than invented, so that the same string
///     works when a drag one day crosses a process boundary. Anything an application defines for
///     itself is spelled the same way — <c>vixen.asset-id</c>, <c>com.example.track</c>.
/// </remarks>
public static class DataFormats {
    /// <summary>Plain text. The value is a <see cref="string" />.</summary>
    public const string Text = "public.utf8-plain-text";

    /// <summary>Native file paths. The value is an <c>IReadOnlyList&lt;string&gt;</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Plural, and a list even for one file</b>, for <see cref="DropEvent.Files" />'s
    ///     reason: every platform's file drag is a set, and a target written against a single path
    ///     is a target that silently ignores four of the five files a user dropped.
    /// </remarks>
    public const string FileUrl = "public.file-url";
}
