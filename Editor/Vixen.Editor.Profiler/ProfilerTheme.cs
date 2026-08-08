// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Profiler;

/// <summary>The stylesheet the diagnostics panels' own elements come with.</summary>
/// <remarks>
///     <para>
///         A sheet after <c>ControlTheme</c>, <c>AdvancedTheme</c> and the editor's, on the terms
///         <c>InspectorTheme</c> is: everything below is written against tokens those declare, and a
///         custom property nothing declared substitutes to nothing.
///     </para>
///     <para>
///         ⚠ <b>The eight flame colours are the only place this sheet invents a palette</b>, and they
///         are here rather than as computed colours in the view for two reasons. A theme has to be
///         able to choose its own eight — the dark set below is unreadable on a light background —
///         and a colour a stylesheet owns is one a game team can override without a fork.
///     </para>
///     <para>
///         ⚠ <b>They are hues of one lightness rather than eight arbitrary colours.</b> A chart whose
///         bars vary in brightness reads as though the bright ones matter, which is exactly the
///         wrong signal: colour here means "a different scope" and nothing else, so only the hue
///         moves.
///     </para>
/// </remarks>
public static class ProfilerTheme {
    /// <summary>Loads the theme into a document.</summary>
    /// <param name="document">The document, which should already have the other sheets in it.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var sheet = document.Load(Css, StyleOrigin.UserAgent);

        document.Load(Utilities, StyleOrigin.UserAgent);

        return sheet;
    }

    /// <summary>The stylesheet's text, for a caller that wants to read or amend it.</summary>
    /// <remarks>
    ///     ⚠ <b>Read out of the assembly rather than held in a <c>const string</c>, and the change
    ///     is bigger than where the bytes live.</b> This sheet was 172 lines of CSS edited inside a
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
    public static string Css => sheet ??= Read("Vixen.Editor.Profiler.ProfilerTheme.vcss");

    /// <summary>This assembly's utility rules, in <c>@layer utilities</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A sheet of its own rather than a share of the editor's, and that is shape C
    ///         working rather than a duplication of it.</b> What
    ///         <c>Vixen.Editor.Ui/build/Vixen.Editor.Ui.Styling.targets</c> shares is the
    ///         <i>tokens</i>; the scan and the output stay this project's, so the build stays
    ///         incremental and this assembly does not have to be rebuilt because a panel somewhere
    ///         else started using <c>gap-3</c>. Everything here is inside <c>@layer utilities</c>,
    ///         where document order decides nothing, so a dozen assemblies loading a dozen of these
    ///         behaves as one sheet.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Loaded at the same origin as the sheet above, which is what keeps the layer
    ///         meaningful.</b> Origin is the cascade's first question and the layer only its second,
    ///         so a utility sheet loaded as <c>Author</c> here would beat every hand-written rule in
    ///         <c>Sheet</c> on origin alone — the inversion <c>EditorTheme.Install</c> spells out at
    ///         length. It is loaded second so that a layering regression cannot hide behind source
    ///         order.
    ///     </para>
    /// </remarks>
    public static string Utilities => VixenUtilityStyles.Utilities;

    static string? sheet;

    static string Read(string name) {
        var assembly = typeof(ProfilerTheme).Assembly;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"the stylesheet '{name}' is not embedded in {assembly.GetName().Name}. It is added "
                + "by the .vcss glob in Vixen.Ui.targets, which this project imports at the bottom "
                + "of its .csproj.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
