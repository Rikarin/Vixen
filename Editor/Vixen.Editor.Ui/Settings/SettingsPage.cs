// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Ui;

/// <summary>Which of the two settings windows a page belongs in.</summary>
/// <remarks>
///     ⚠ <b>Two windows, one mechanism — so the scope is on the contribution rather than in two
///     registries.</b> Doc 20 § A4's only difference between Preferences and Project Settings is
///     whose store the pages are over, and a contribution kind per window would make "add a settings
///     page" two declarations that a consumer has to know to read both of.
/// </remarks>
public enum SettingsScope {
    /// <summary>Preferences — the user's, and the same in every project they open.</summary>
    Preferences,

    /// <summary>Project Settings — this project's, and checked in beside it.</summary>
    Project
}

/// <summary>A settings page something other than the application contributed.</summary>
/// <param name="Scope">Which window it goes in.</param>
/// <param name="Category">The page itself: its id, its title, and what draws it.</param>
/// <remarks>
///     <para>
///         <b>Doc 36 § D4's <c>AddSettingsPage</c> row, on P2's terms.</b> A contribution kind is a
///         record in the assembly that owns it and <c>IEditorRegistry.Add</c> is the whole surface —
///         there is no method on <c>PluginContext</c> for this and there deliberately is not, because
///         the alternative is a registry, a consumer and a plugin method per kind.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in <c>Vixen.Editor.Core</c>, because a page is a
///         <see cref="SettingsCategory" /> and that is the shell's.</b> The registry keys on the
///         static type it is handed and nothing about that type has to live where the registry does —
///         <c>SceneOverlay</c> is in the scene view and <c>CustomInspector</c> is in the inspector for
///         the same reason.
///     </para>
///     <para>
///         ⚠ <b>What reads it is <c>EditorApplication</c>, and it re-reads on
///         <c>IEditorRegistry.Changed</c>.</b> A plugin activating three seconds after start-up is
///         always after a window was built, so a consumer that read the registry once would be one a
///         plugin cannot reach — the failure <c>RefreshAssetKinds</c> and <c>RefreshOverlays</c>
///         already exist to avoid. Withdrawing the contribution takes the page out of an open window
///         through <see cref="SettingsView.Remove" />.
///     </para>
/// </remarks>
public sealed record SettingsPage(SettingsScope Scope, SettingsCategory Category);
