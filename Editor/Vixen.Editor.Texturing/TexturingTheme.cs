// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Texturing;

/// <summary>The stylesheet this plugin's own panels come with.</summary>
/// <remarks>
///     <para>
///         <b>The first half of <a href="https://github.com/Rikarin/Vixen/issues/881">#881</a>, which
///         is doc 36 § P4's markup debt.</b> <c>LayerStackView</c> wrote its layout through
///         <c>SetStyle</c> at nine call sites, three lines each; the flex boxes are a file now, and
///         the file has room for the reason each one is the way it is.
///     </para>
///     <para>
///         ⚠ <b>Installed by the view rather than by the editor, and that is the difference between
///         a plugin's sheet and the editor's four.</b> <c>EditorApplication</c> names
///         <c>InspectorTheme</c>, <c>AssetEditorTheme</c>, <c>BrowserTheme</c> and
///         <c>WorldTheme</c> one by one — a plugin is not in that list and must not be, since doc 48
///         § D14's claim is that a third party could have written this assembly. A panel element's
///         <c>UiElement.Document</c> reaches the document that element is in, so a view can load its
///         own sheet. ⚠ <c>PluginContext.AddStyleSheet</c> exists since #1484 and is the seam that
///         also takes a sheet back out on unload; this view-installed sheet predates it and is never
///         removed (<c>docs/ModuleRegistrationExempt.txt</c> carries the rows).
///     </para>
///     <para>
///         ⚠ <b>Once per document, and the guard is not politeness.</b> A panel's factory re-runs
///         whenever the workspace relays out — opening any other panel does it — so an unguarded
///         install would add a copy of every rule to the editor's document on each relayout, for as
///         long as the session lasted. <c>UiDocument.Load</c> appends; it has no notion of a sheet
///         it already holds.
///     </para>
///     <para>
///         ⚠ <b><see cref="StyleOrigin.UserAgent" />, which is where every other editor sheet is
///         loaded.</b> Origin is the cascade's first question and a layer only its second, so
///         loading this as <c>Author</c> would take it out of the order it shares with
///         <c>ControlTheme</c> and start it beating a user's accessibility overrides.
///     </para>
/// </remarks>
static class TexturingTheme {
    static readonly ConditionalWeakTable<UiDocument, object> Loaded = [];

    static string? sheet;

    /// <summary>The stylesheet's text.</summary>
    /// <remarks>
    ///     Read out of the assembly rather than held in a <c>const string</c>, so it is a real file
    ///     with syntax highlighting, a formatter and something a hot-reload watcher can see. Cached,
    ///     because the resource is immutable and re-decoding the UTF-8 per caller buys nothing.
    /// </remarks>
    public static string Css => sheet ??= Read("Vixen.Editor.Texturing.TexturingTheme.vcss");

    /// <summary>Loads the sheet into a document, unless that document already has it.</summary>
    /// <param name="document">The document the panel is in.</param>
    /// <returns>Whether this call is the one that loaded it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    public static bool Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (Loaded.TryGetValue(document, out _)) {
            return false;
        }

        Loaded.Add(document, Loaded);
        document.Load(Css, StyleOrigin.UserAgent);

        return true;
    }

    static string Read(string name) {
        var assembly = typeof(TexturingTheme).Assembly;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"the stylesheet '{name}' is not embedded in {assembly.GetName().Name}. It is added "
                + "by the .vcss glob in Vixen.Ui.targets, which this project imports at the bottom "
                + "of its .csproj.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
