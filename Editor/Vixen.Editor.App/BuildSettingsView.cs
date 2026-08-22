// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.App;

/// <summary>Doc 20's B7: what a player build is, and the two buttons that run one.</summary>
/// <remarks>
///     <para>
///         The panel is <c>BuildSettingsView.vxml</c> since doc 36 § F7 wave 2; this file is the row
///         its list is made of, and the type declaration the emitter's partial pairs with.
///     </para>
///     <para>
///         <b>Five fields and a list, over <see cref="PlayerBuild" />'s calls.</b> Target,
///         configuration, output path and the scenes that ship are the four doc 20 names; the fifth
///         thing on the window is the sentence saying why Build is greyed, which is the same rule
///         every unimplemented menu line follows and is what stops "it does nothing when I press it".
///     </para>
///     <para>
///         ⚠ <b>No Apply, and this is the one settings surface where that is right.</b> Doc 20's A4
///         asks for an explicit Apply because its two settings cost something to change — lowering the
///         undo depth drops history, changing the content target invalidates an import. Nothing here
///         costs anything until Build is pressed, and Build is the thing that <i>reads</i> these
///         fields: an edit that had not been applied yet would mean the button building something
///         other than what is on screen, which is worse than a file written per keystroke.
///     </para>
///     <para>
///         ⚠ <b>The scene list is the panel's own control rather than the inspector's list drawer.</b>
///         A list of strings drawn generically is a column of text boxes; what this list needs is
///         order (the first entry is what <c>AppConfig.StartupScene</c> defaults to), a picker that
///         offers only scenes that exist, and a column saying which entries a build would refuse.
///         None of those three is expressible as a property attribute.
///     </para>
///     <para>
///         ⚠ <b>Nothing on this panel is bound, and the <c>.vxml</c>'s header is where that is
///         argued.</b> Its short form: an effect runs at the frame's flush, and this panel's callers
///         read <c>BuildButton.Disabled</c> back on the line after <c>Rebuild()</c>.
///     </para>
/// </remarks>
sealed partial class BuildSettingsView;

/// <summary>One row of the scenes-in-build list.</summary>
/// <param name="Order">Its one-based position, which is what makes "first" visible.</param>
/// <param name="Path">The project-relative path.</param>
/// <param name="State">Whether it resolves, and whether it is the first.</param>
sealed record SceneRow(int Order, string Path, string State);
