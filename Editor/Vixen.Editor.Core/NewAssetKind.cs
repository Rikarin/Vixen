// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>One line in Create ▸: what it is called, what it writes, and whether it opens.</summary>
/// <param name="Id">The command id. Prefix a plugin's with the plugin's own id.</param>
/// <param name="Title">What the menu line says.</param>
/// <param name="Extension">What the new file is called after the dot, including it.</param>
/// <param name="DefaultName">What it is called before the dot, before a number is appended.</param>
/// <param name="Contents">
///     What to write into it. Empty is a zero-byte file, which is what a kind whose editor opens an
///     empty one as a sensible new document wants; anything else is a starter document, which a kind
///     read by an <i>importer</i> needs — an empty file that the importer deserialises and validates
///     arrives with a warning beside it rather than as a new asset.
/// </param>
/// <param name="Opens">Whether to open it after creating it, which needs an editor claiming the extension.</param>
/// <param name="Order">Where among the lines, low first. Ties keep registration order.</param>
/// <remarks>
///     <para>
///         ⚠ <b>F3: this was a literal tuple array in <c>EditorWorlds</c>.</b> A plugin that
///         introduced an asset type could not put it in Create ▸ at all — the menu was a list in the
///         application of every kind the application knew about, which is the same shape of problem
///         as an editor that hard-references its own features.
///     </para>
///     <para>
///         Here rather than in <c>Vixen.Editor.App</c> because a contribution has to be declarable
///         by something that does not reference the application — which is every plugin, and every
///         feature assembly once P3 moves the built-ins to the front door.
///     </para>
/// </remarks>
public sealed record NewAssetKind(
    string Id,
    string Title,
    string Extension,
    string DefaultName,
    string Contents = "",
    bool Opens = true,
    int Order = 0
) {
    /// <summary>Produces the contents per file, or <see langword="null" /> to use <see cref="Contents" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Per creation rather than per registration, which is the whole reason it is a delegate
    ///     and not a longer string.</b> A starter document that carries an identifier, a name or a
    ///     date is a different file every time it is made — and evaluating it once when a plugin
    ///     loaded would give every asset in the project the first one's, which is a collision nobody
    ///     would look for in the Create menu. A kind whose contents are a fixed template leaves this
    ///     alone.
    /// </remarks>
    public Func<string>? Build { get; init; }

    /// <summary>What to write into a new one.</summary>
    /// <returns>The contents.</returns>
    public string NewContents() => Build is null ? Contents : Build();
}
