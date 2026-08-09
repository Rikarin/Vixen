// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Ui;

/// <summary>The stylesheet the editor's own chrome comes with.</summary>
/// <remarks>
///     <para>
///         A third user-agent sheet after <c>ControlTheme</c> and <c>AdvancedTheme</c>, on the same
///         terms and for the same reason: it draws what those two do not — the shell, the toolbar,
///         the status bar, the palette and the notification list — and it is written entirely
///         against their tokens, so recolouring the root recolours all three.
///     </para>
///     <para>
///         ⚠ <b><see cref="Install" /> loads this and neither of the others.</b> An editor needs all
///         three, in order, because this sheet reads <c>--surface</c>, <c>--border</c> and the rest,
///         and a custom property nothing declared substitutes to nothing.
///     </para>
///     <para>
///         ⚠ <b>It also <i>overrides</i> the control set's tokens, which the control set is built to
///         allow.</b> An application shipping a button gets the neutral palette
///         <c>ControlTheme</c> declares; a tool window is a different room, and a set of values on
///         the root is exactly the mechanism its own remarks nominate for saying so. Nothing in
///         <c>ControlTheme</c> or <c>AdvancedTheme</c> is edited, so a game that never installs this
///         sheet is untouched — and every screenshot of a bare control still shows what a game gets.
///     </para>
///     <para>
///         <b>What it is trying to look like.</b> Two things, deliberately: the near-black neutral
///         density of a 3D tool, where a panel is a working surface and chrome gets out of the way;
///         and the layered, rounded, softly-lit materiality of a modern audio workstation, where a
///         field is a well you type into and a floating thing is visibly floating. The join is the
///         <i>desk</i> — the workspace is the darkest thing on screen and the panels are cards laid
///         on it, which is what gives depth without a single gradient.
///     </para>
///     <para>
///         ⚠ <b>Depth comes from luminance steps, not from borders.</b> Four surfaces —
///         <c>--workspace</c>, <c>--surface-sunken</c>, <c>--surface</c>, <c>--surface-raised</c> —
///         and a hairline that is barely there. A tool window that separates everything with visible
///         lines is the look this is getting away from, and it is why the borders below are one step
///         from invisible while the fills are not.
///     </para>
/// </remarks>
public static class EditorTheme {
    /// <summary>Loads the sheet, and the utilities the editor's markup uses, into a document.</summary>
    /// <param name="document">The document, which should already have the other two sheets in it.</param>
    /// <returns>This sheet's index, for a hot reload. The utility sheet's is not a thing to reload.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two sheets, in this order, at this origin — and every part of that sentence is
    ///         load-bearing.</b> This sheet is entirely inside <c>@layer components</c> and
    ///         <see cref="EditorStyles.Utilities" /> is entirely inside <c>@layer utilities</c>, which
    ///         comes later and therefore wins. That is the whole ladder, and it only works because
    ///         both are the same origin: a layer is the cascade's <i>second</i> question and origin is
    ///         its first, so a ladder spread across two origins is not a ladder at all. The reverse
    ///         arrangement is measured rather than remembered — see
    ///         <c>StylesheetTests.An_origin_outranks_the_whole_ladder</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The consequence, stated plainly, because it inverted:</b> <c>p-3</c> now wins
    ///         against <c>task-row { padding: 6px }</c>. It used to lose — this sheet was unlayered,
    ///         an unlayered rule beats every layer, and so every chrome rule beat every utility
    ///         silently and always. Retro-fitting a utility onto chrome the sheet already styles no
    ///         longer means deleting the hand-written rule first.
    ///     </para>
    ///     <para>
    ///         And the chrome sheet is loaded <em>first</em>, where nothing but the layer can decide
    ///         the outcome. Loading it second would make the utility win on source order as well, and
    ///         a layering regression would then pass every test in the suite — the mistake
    ///         <c>Vixen.Ui.Styling.Utilities</c>' README records having made once already.
    ///     </para>
    ///     <para>
    ///         A panel's own scoped <c>&lt;style&gt;</c> in a <c>.vxml</c> loads as <c>Author</c>
    ///         through <c>Component</c>, so it beats both of these on origin and needs no
    ///         <c>!important</c> to do it.
    ///     </para>
    /// </remarks>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var sheet = document.Load(Css, StyleOrigin.UserAgent);
        document.Load(EditorStyles.Utilities, StyleOrigin.UserAgent);

        return sheet;
    }

    /// <summary>The sheet's text, for a caller that wants to read or amend it.</summary>
    /// <remarks>
    ///     ⚠ <b>Read out of the assembly rather than held in a <c>const string</c>, and the change is
    ///     bigger than where the bytes live.</b> This sheet was fourteen hundred lines of CSS edited
    ///     inside a raw string literal, which is what a tree with no <c>.vcss</c> item type forces —
    ///     no syntax highlighting, no formatter, no way for a hot-reload watcher to see an edit, and
    ///     a rebuild of the whole assembly for a colour. It is a real file now, embedded by the glob
    ///     in <c>Core/Vixen.Ui/build/Vixen.Ui.targets</c>.
    ///     <para>
    ///         Cached, because the string is handed to <c>Load</c> once per document and read whole
    ///         by two tests, and re-decoding a hundred kilobytes of UTF-8 for each is a cost with
    ///         nothing on the other side of it. The resource is immutable, so the cache cannot go
    ///         stale — a hot reload replaces the sheet through <c>UiDocument</c>, not through here.
    ///     </para>
    /// </remarks>
    public static string Css => sheet ??= Read("Vixen.Editor.Ui.Theming.EditorTheme.vcss");

    static string? sheet;

    static string Read(string name) {
        var assembly = typeof(EditorTheme).Assembly;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"the stylesheet '{name}' is not embedded in {assembly.GetName().Name}. It is added "
                + "by the .vcss glob in Vixen.Ui.targets, which this project imports at the bottom "
                + "of its .csproj.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
