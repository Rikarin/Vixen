// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Debugger;

/// <summary>The stylesheet the debugger's own elements come with.</summary>
/// <remarks>
///     ⚠ <b>It assumes <c>ProfilerTheme</c> is loaded and deliberately does not repeat it.</b> The
///     two assemblies' panels share a strip, a status line and the <c>parked</c> rule, and two
///     copies of those rules would be two places to change when the strip's height moves. Loading
///     this without the profiler's leaves the toolbars unstyled, which is why the editor loads both.
/// </remarks>
public static class DebuggerTheme {
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
    ///     is bigger than where the bytes live.</b> This sheet was 70 lines of CSS edited inside a
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
    public static string Css => sheet ??= Read("Vixen.Editor.Debugger.DebuggerTheme.vcss");

    /// <summary>This assembly's utility rules, in <c>@layer utilities</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A sheet of its own over the editor's tokens.</b> What
    ///         <c>Vixen.Editor.Ui/build/Vixen.Editor.Ui.Styling.targets</c> shares is the tokens; the
    ///         scan and the output stay this project's, so incremental build and project
    ///         independence both survive and every sheet still lands in <c>@layer utilities</c>,
    ///         where document order decides nothing.
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
        var assembly = typeof(DebuggerTheme).Assembly;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"the stylesheet '{name}' is not embedded in {assembly.GetName().Name}. It is added "
                + "by the .vcss glob in Vixen.Ui.targets, which this project imports at the bottom "
                + "of its .csproj.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
