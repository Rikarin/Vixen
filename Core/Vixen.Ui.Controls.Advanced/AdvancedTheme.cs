// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>The stylesheet the advanced controls come with.</summary>
/// <remarks>
///     <para>
///         A second sheet rather than more rules in <see cref="ControlTheme" />, because the two
///         assemblies ship separately: an application that wants a button and not a docking host
///         should not carry three hundred lines of CSS about splitters. Both load as
///         <see cref="StyleOrigin.UserAgent" /> and both are written against the same tokens, so
///         recolouring the root recolours everything either of them draws.
///     </para>
///     <para>
///         ⚠ <b><see cref="Install" /> loads this and not the base theme.</b> An application needs
///         both, in that order — this sheet reads <c>--surface</c>, <c>--border</c> and the rest, and
///         a custom property that nothing declared substitutes to nothing.
///     </para>
/// </remarks>
public static class AdvancedTheme {
    /// <summary>Loads the theme into a document.</summary>
    /// <param name="document">The document, which should already have <see cref="ControlTheme" /> in it.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        return document.Load(Css, StyleOrigin.UserAgent);
    }

    /// <summary>The stylesheet's text, for a caller that wants to read or amend it.</summary>
    /// <remarks>
    ///     ⚠ <b>Read out of the assembly rather than held in a <c>const string</c>, and the change
    ///     is bigger than where the bytes live.</b> This sheet was 969 lines of CSS edited inside a
    ///     raw string literal, which is what a tree with no <c>.vcss</c> item type forced — no syntax
    ///     highlighting, no formatter, no way for a hot-reload watcher to see an edit, and a rebuild
    ///     of the whole assembly for a colour. It is a real file now, embedded by the glob in
    ///     <c>Core/Vixen.Ui/build/Vixen.Ui.targets</c>.
    ///     <para>
    ///         Cached, because the string is handed to <c>Load</c> once per document and re-decoding
    ///         the UTF-8 for every caller is a cost with nothing on the other side of it. The
    ///         resource is immutable, so the cache cannot go stale — a hot reload replaces the sheet
    ///         through <c>UiDocument</c>, not through here.
    ///     </para>
    /// </remarks>
    public static string Css => sheet ??= Read("Vixen.Ui.Controls.Advanced.AdvancedTheme.vcss");

    static string? sheet;

    static string Read(string name) {
        var assembly = typeof(AdvancedTheme).Assembly;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"the stylesheet '{name}' is not embedded in {assembly.GetName().Name}. It is added "
                + "by the .vcss glob in Vixen.Ui.targets, which this project imports at the bottom "
                + "of its .csproj.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
