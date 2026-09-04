// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.NodeGraph;

/// <summary>The stylesheet the graph view's own elements come with.</summary>
/// <remarks>
///     <para>
///         A third sheet, after <c>ControlTheme</c> and <c>AdvancedTheme</c> and written against the
///         same tokens: everything <c>NodeCanvas</c> draws is already styled by the second, and what is
///         here is only the four elements this assembly adds — the search popup, its rows, a sticky
///         note, and the preview layer.
///     </para>
///     <para>
///         ⚠ <b>Both of those have to be loaded first.</b> Every colour below is a
///         <c>var(--…)</c> against a token one of them declares, and a custom property nothing declared
///         substitutes to nothing — which is a popup with no background over a canvas.
///     </para>
/// </remarks>
public static class NodeGraphTheme {
    /// <summary>Loads the theme into a document.</summary>
    /// <param name="document">The document, which should already have the other two sheets in it.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var sheet = document.Load(Css, StyleOrigin.UserAgent);

        document.Load(Utilities, StyleOrigin.UserAgent);

        return sheet;
    }

    /// <summary>This assembly's utility rules, in <c>@layer utilities</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>A sheet of its own rather than a share of the editor's, and that is shape C working
    ///     rather than a duplication of it.</b> What
    ///     <c>Vixen.Editor.Ui/build/Vixen.Editor.Ui.Styling.targets</c> shares is the <i>tokens</i>;
    ///     the scan and the output stay this project's, so the build stays incremental and this
    ///     assembly is not rebuilt because a panel somewhere else started using <c>gap-3</c>.
    ///     Everything here is inside <c>@layer utilities</c>, where document order decides nothing,
    ///     so a dozen assemblies loading a dozen of these behaves as one sheet.
    ///     <para>
    ///         ⚠ Loaded at the same origin as the sheet above, which is what keeps the ladder
    ///         meaningful: origin is the cascade's first question and the layer only its second, so
    ///         loading these as <c>Author</c> would stop them being ordered against the sheet at all
    ///         and start them beating a user's accessibility overrides.
    ///     </para>
    /// </remarks>
    public static string Utilities => VixenUtilityStyles.Utilities;

    /// <summary>The stylesheet's text, for a caller that wants to read or amend it.</summary>
    /// <remarks>
    ///     ⚠ <b>Read out of the assembly rather than held in a <c>const string</c>, and the change
    ///     is bigger than where the bytes live.</b> This sheet was 61 lines of CSS edited inside a
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
    public static string Css => sheet ??= Read("Vixen.Editor.NodeGraph.NodeGraphTheme.vcss");

    static string? sheet;

    static string Read(string name) {
        var assembly = typeof(NodeGraphTheme).Assembly;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"the stylesheet '{name}' is not embedded in {assembly.GetName().Name}. It is added "
                + "by the .vcss glob in Vixen.Ui.targets, which this project imports at the bottom "
                + "of its .csproj.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
