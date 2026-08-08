// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>The stylesheet the controls come with.</summary>
/// <remarks>
///     <para>
///         <b>Loaded as <see cref="StyleOrigin.UserAgent" />, which is the whole design.</b> A game's
///         own stylesheet is <see cref="StyleOrigin.Author" /> and beats this at equal specificity,
///         so restyling a button is one rule rather than a fork of this file — and a rule that only
///         names <c>button</c> is enough, rather than one that has to out-specify whatever this
///         happened to write. That is the reason the cascade has three origins, and this is the
///         first thing in the engine to use the first of them.
///     </para>
///     <para>
///         ⚠ <b>Colours go through custom properties and geometry does not.</b> A theme wanting
///         different colours sets nine values on the root; one wanting different <i>shapes</i> writes
///         rules, because there is no useful token for "a switch's knob is a circle". The split is
///         where it is because recolouring is what nearly every application does and reshaping is
///         what a few do thoroughly.
///     </para>
///     <para>
///         ⚠ <b>The layout defaults are CSS's, so a container with no <c>flex-direction</c> is a
///         <i>row</i></b> — <c>LayoutStyleBuilder</c> starts from CSS's initial values rather than
///         from Yoga's, which differ in four places. Every rule below that wants a column therefore
///         says so, and a rule that reads as redundant beside a browser stylesheet is not.
///     </para>
///     <para>
///         <b><c>box-sizing: border-box</c> is set here on everything</b>, which is the one place it
///         can be: <c>LayoutStyleBuilder</c>'s own remarks say the property belongs in a user-agent
///         stylesheet where an author can see and override it rather than baked into the bridge
///         where they cannot. This is that stylesheet. Without it a control's declared height is the
///         height of its <i>content</i> and every padded control comes out taller than it says.
///     </para>
/// </remarks>
public static class ControlTheme {
    /// <summary>Loads the theme into a document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        return document.Load(Css, StyleOrigin.UserAgent);
    }

    /// <summary>The stylesheet's text, for a caller that wants to read or amend it.</summary>
    /// <remarks>
    ///     ⚠ <b>Read out of the assembly rather than held in a <c>const string</c>, and the change
    ///     is bigger than where the bytes live.</b> This sheet was 873 lines of CSS edited inside a
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
    public static string Css => sheet ??= Read("Vixen.Ui.Controls.ControlTheme.vcss");

    static string? sheet;

    static string Read(string name) {
        var assembly = typeof(ControlTheme).Assembly;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"the stylesheet '{name}' is not embedded in {assembly.GetName().Name}. It is added "
                + "by the .vcss glob in Vixen.Ui.targets, which this project imports at the bottom "
                + "of its .csproj.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
