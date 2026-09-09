// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Blockout;

/// <summary>The stylesheet the blockout module's own elements come with.</summary>
/// <remarks>
///     <para>
///         A sheet after <c>ControlTheme</c>, <c>AdvancedTheme</c> and the editor's, on the terms
///         <c>ProfilerTheme</c> is: everything in it is written against tokens those declare, and a
///         custom property nothing declared substitutes to nothing.
///     </para>
///     <para>
///         ⚠ <b>No utility sheet, and that is a decision rather than an omission.</b> This assembly
///         declares no <c>@theme</c> of its own and writes no utility class in its markup, so joining
///         <c>SharedThemeTests</c>' participant list would mean inventing a loader to publish an
///         empty sheet — which is the sweep that list exists to refuse. The two rules it needs are
///         component rules and they are in the <c>.vcss</c>.
///     </para>
/// </remarks>
public static class BlockoutTheme {
    static string? sheet;

    /// <summary>The stylesheet's text, for a caller that wants to read or amend it.</summary>
    public static string Css => sheet ??= Read("Vixen.Editor.Blockout.BlockoutTheme.vcss");

    /// <summary>Loads the theme into a document.</summary>
    /// <param name="document">The document, which should already have the other sheets in it.</param>
    /// <returns>The sheet's index, for a hot reload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    public static int Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        return document.Load(Css, StyleOrigin.UserAgent);
    }

    static string Read(string name) {
        var assembly = typeof(BlockoutTheme).Assembly;

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"the stylesheet '{name}' is not embedded in {assembly.GetName().Name}. It is added "
                + "by the .vcss glob in Vixen.Ui.targets, which this project reaches by setting "
                + "<VixenUi>true</VixenUi>.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
