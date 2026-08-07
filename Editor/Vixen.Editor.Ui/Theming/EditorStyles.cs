// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Ui;

/// <summary>The utility half of the editor's stylesheet, compiled at build time.</summary>
/// <remarks>
///     <para>
///         <b>There is no code here, and that is the feature.</b> <c>Theming/vixen.ui.yaml</c> is the
///         design tokens, every source file and every <c>.vxml</c> in this assembly is scanned for
///         class names, and the sheet is generated into <c>obj/</c> by
///         <c>Vixen.Ui.Styling.Utilities.targets</c> before the compiler runs. What is left over is
///         this: a name for the constant, so that the rest of the editor does not have to know that a
///         generated type exists.
///     </para>
///     <para>
///         ⚠ <b>The build step is why this file is four lines rather than a hundred and thirty.</b>
///         <c>Samples/14-Mmo/Mmo.Ui/Theme/MmoStyles.cs</c> is what this looked like before it: embed
///         the markup as resources, walk the manifest, run the scanner, run the generator, cache the
///         answer — a startup bootstrap whose own remarks admit it is standing in for a build step
///         that had not been written. It has been written. The sample is left as it is deliberately,
///         as the reference for what the step replaced.
///     </para>
///     <para>
///         ⚠ <b>The tokens are not a second palette, and the yaml goes to some trouble to stay that
///         way.</b> Every colour in it is a <c>var(--…)</c> reference to a custom property
///         <see cref="EditorTheme" /> already declares on the root — so <c>bg-surface</c> and a
///         hand-written <c>background: var(--surface)</c> are the same declaration, the light/dark
///         toggle moves both, and a user theme loaded through <see cref="ThemeService" /> moves both
///         again. Copying the hex across would have produced two palettes that agreed until the day
///         one of them was edited.
///     </para>
///     <para>
///         ⚠ <b>The utility layer loses every argument it has with <see cref="EditorTheme" />, and
///         that is deliberate — but it is a sharper trade here than in a game.</b> Everything
///         generated lands in <c>@layer utilities</c> and the hand-written sheet is unlayered, so
///         <c>task-row { padding: 6px }</c> beats <c>p-3</c> without either of them saying
///         <c>!important</c>. The consequence is worth stating plainly: a utility only takes effect on
///         a property no rule in <see cref="EditorTheme" /> already sets for that element. New panels
///         get the whole vocabulary; retro-fitting one onto chrome the sheet already styles means
///         deleting the hand-written rule first.
///     </para>
///     <para>
///         A public wrapper over an internal generated type rather than a generated public type,
///         because a public generated type is a public API baseline entry and a documentation page
///         for a class whose entire content is a string constant — and because the name the editor
///         says is then this assembly's to choose rather than the build step's.
///     </para>
/// </remarks>
public static class EditorStyles {
    /// <summary>The utilities the editor's markup and code use, in <c>@layer utilities</c>.</summary>
    public static string Utilities => EditorUtilityStyles.Utilities;

    /// <summary>How many utility rules the build emitted.</summary>
    /// <remarks>
    ///     Exposed because it is the cheapest possible check that the build step ran at all: a
    ///     project whose <c>.targets</c> import went missing compiles perfectly and produces a sheet
    ///     with nothing in it, and every style in every panel then quietly does nothing.
    /// </remarks>
    public static int RuleCount => EditorUtilityStyles.RuleCount;
}
