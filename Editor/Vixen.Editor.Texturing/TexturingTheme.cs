// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
///         ⚠ <b>Loaded by the module rather than by the editor, and that is the difference between
///         a plugin's sheet and the editor's four.</b> <c>EditorApplication</c> names
///         <c>InspectorTheme</c>, <c>AssetEditorTheme</c>, <c>BrowserTheme</c> and
///         <c>WorldTheme</c> one by one — a plugin is not in that list and must not be, since doc 48
///         § D14's claim is that a third party could have written this assembly.
///         <c>TexturingModule.Activate</c> loads it through <c>PluginContext.AddStyleSheet</c>, which
///         also takes it back out on unload (#1486). ⚠ Until then the three views installed it
///         themselves, guarded once per document because a panel's factory re-runs on every
///         relayout, and nothing ever removed it.
///     </para>
///     <para>
///         ⚠ <b><see cref="StyleOrigin.UserAgent" />, which is where every other editor sheet is
///         loaded</b> and <c>AddStyleSheet</c>'s default. Origin is the cascade's first question and
///         a layer only its second, so loading this as <c>Author</c> would take it out of the order
///         it shares with <c>ControlTheme</c> and start it beating a user's accessibility overrides.
///     </para>
/// </remarks>
static class TexturingTheme {
    static string? sheet;

    /// <summary>The stylesheet's text.</summary>
    /// <remarks>
    ///     Read out of the assembly rather than held in a <c>const string</c>, so it is a real file
    ///     with syntax highlighting, a formatter and something a hot-reload watcher can see. Cached,
    ///     because the resource is immutable and re-decoding the UTF-8 per caller buys nothing.
    /// </remarks>
    public static string Css => sheet ??= Read("Vixen.Editor.Texturing.TexturingTheme.vcss");

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
