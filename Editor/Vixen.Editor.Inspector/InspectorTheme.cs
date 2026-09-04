// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Inspector;

/// <summary>The stylesheet the inspector's own elements come with.</summary>
/// <remarks>
///     <para>
///         A sheet after <c>ControlTheme</c> and <c>AdvancedTheme</c> and written against the same
///         tokens, on the same terms <c>NodeGraphTheme</c> is: the controls a drawer builds are
///         already styled by those two, and what is here is only the six elements this assembly
///         adds — the view, its body, a row, a row's label, a row's editor slot, and the component
///         group a vector drawer builds.
///     </para>
///     <para>
///         ⚠ <b>Both of those have to be loaded first.</b> Every colour below is a
///         <c>var(--…)</c> against a token one of them declares, and a custom property nothing
///         declared substitutes to nothing.
///     </para>
///     <para>
///         ⚠ <b>Without this the inspector lays out as rows of rows.</b> CSS's initial
///         <c>flex-direction</c> is <c>row</c> and <c>LayoutStyleBuilder</c> starts from CSS's
///         initial values, so an element nothing styles is a row — which puts the search box beside
///         the fields, and every member beside the one before it. Each <c>flex-direction: column</c>
///         below that reads as redundant beside a browser stylesheet is not.
///     </para>
///     <para>
///         ⚠ <b>A field's background is <c>--surface-sunken</c> and not <c>--surface</c>.</b> The
///         control set gives a text box <c>--surface</c>, which is right on a page and wrong in a
///         tool window: <c>dock-group</c> is <c>--surface</c> too, so a box drawn in the panel's own
///         colour is a border around nothing. Sunk rather than raised because a field is a hole you
///         type into, which is the convention every editor with a docked inspector already follows.
///     </para>
/// </remarks>
public static class InspectorTheme {
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
    ///     is bigger than where the bytes live.</b> This sheet was 200 lines of CSS edited inside a
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
    public static string Css => sheet ??= Read("Vixen.Editor.Inspector.InspectorTheme.vcss");

    static string? sheet;

    static string Read(string name) {
        var assembly = typeof(InspectorTheme).Assembly;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"the stylesheet '{name}' is not embedded in {assembly.GetName().Name}. It is added "
                + "by the .vcss glob in Vixen.Ui.targets, which this project imports at the bottom "
                + "of its .csproj.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}

/// <summary>The content browser's own two rules, which have nowhere better to live.</summary>
/// <remarks>
///     ⚠ <b>Here rather than in the shell's theme because the browser is the application's panel and
///     the shell knows nothing about it</b> — the same reason this assembly's sheet is loaded by the
///     application rather than by <c>EditorShell</c>. Two rules is not worth a fifth stylesheet.
/// </remarks>
public static class BrowserTheme {
    /// <summary>Adds the sheet to a document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        return document.Load(Css, StyleOrigin.UserAgent);
    }

    /// <summary>The stylesheet's text.</summary>
    /// <remarks>
    ///     ⚠ <b>Read out of the assembly rather than held in a <c>const string</c>, and the change
    ///     is bigger than where the bytes live.</b> This sheet was 212 lines of CSS edited inside a
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
    public static string Css => sheet ??= Read("Vixen.Editor.Inspector.BrowserTheme.vcss");

    static string? sheet;

    static string Read(string name) {
        var assembly = typeof(BrowserTheme).Assembly;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"the stylesheet '{name}' is not embedded in {assembly.GetName().Name}. It is added "
                + "by the .vcss glob in Vixen.Ui.targets, which this project imports at the bottom "
                + "of its .csproj.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
