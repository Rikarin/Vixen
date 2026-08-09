// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.AssetEditors;

/// <summary>The stylesheet the asset editors' own elements come with.</summary>
/// <remarks>
///     <para>
///         A fifth user-agent sheet, after <c>ControlTheme</c>, <c>AdvancedTheme</c>,
///         <c>EditorTheme</c> and <c>InspectorTheme</c>, and written against the tokens they declare.
///         What is here is only the elements this assembly adds — the matrix, the ladder, the fact
///         rows, the group list, the preview panes. Everything a drawer or a code editor builds is
///         already styled by the sheets above.
///     </para>
///     <para>
///         ⚠ <b>All four of those have to be loaded first.</b> Every colour below is a
///         <c>var(--…)</c> against a token one of them declares, and a custom property nothing
///         declared substitutes to nothing.
///     </para>
///     <para>
///         ⚠ <b>Every <c>flex-direction: column</c> below that looks redundant is load-bearing.</b>
///         CSS's initial direction is <c>row</c> and <c>LayoutStyleBuilder</c> starts from CSS's
///         initial values, so an element nothing styles lays its children out side by side — which
///         for a settings panel means every section beside the one before it.
///     </para>
/// </remarks>
public static class AssetEditorTheme {
    /// <summary>Loads the theme into a document.</summary>
    /// <param name="document">The document, which should already have the other four sheets in it.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var sheet = document.Load(Css, StyleOrigin.UserAgent);

        document.Load(Utilities, StyleOrigin.UserAgent);

        return sheet;
    }

    /// <summary>The stylesheet's text, for a caller that wants to read or amend it.</summary>
    /// <remarks>
    ///     ⚠ <b>Read out of the assembly rather than held in a <c>const string</c>, and the change
    ///     is bigger than where the bytes live.</b> This sheet was 794 lines of CSS edited inside a
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
    public static string Css => sheet ??= Read("Vixen.Editor.AssetEditors.AssetEditorTheme.vcss");

    /// <summary>This assembly's utility rules, in <c>@layer utilities</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A sheet of its own over the editor's tokens, which is the arrangement the remark
    ///         about <c>.hidden</c> below was a symptom of not having.</b>
    ///         <c>Vixen.Editor.Ui/build/Vixen.Editor.Ui.Styling.targets</c> shares the tokens; the
    ///         scan and the output stay this project's, so incremental build survives and this
    ///         assembly is not rebuilt because a panel elsewhere started using <c>gap-3</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Loaded at the same origin as the sheet above, which is what keeps the ladder
    ///         inside one origin.</b> Origin is the cascade's first question and the layer only its
    ///         second, so <c>components</c> → <c>utilities</c> orders nothing across an origin
    ///         boundary: as <c>Author</c> these would stop being ordered against <c>Sheet</c> at all
    ///         and would start beating a player's <c>User</c> overrides, which are supposed to win.
    ///     </para>
    /// </remarks>
    public static string Utilities => VixenUtilityStyles.Utilities;

    static string? sheet;

    static string Read(string name) {
        var assembly = typeof(AssetEditorTheme).Assembly;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"the stylesheet '{name}' is not embedded in {assembly.GetName().Name}. It is added "
                + "by the .vcss glob in Vixen.Ui.targets, which this project imports at the bottom "
                + "of its .csproj.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
