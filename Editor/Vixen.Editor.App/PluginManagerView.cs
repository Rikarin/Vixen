// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.App;

/// <summary>The plugin manager doc 11 calls "a view and nothing more", plus the three verbs.</summary>
/// <remarks>
///     <para>
///         The panel is <c>PluginManagerView.vxml</c>; this file is the record its strip and its
///         detail line are made of, and the type declaration the emitter's partial pairs with.
///     </para>
///     <para>
///         <b>A grid over <c>PluginHost.Plugins</c>, which has held everything this needs
///         since it was written.</b> A <c>LoadedPlugin</c> carries the manifest, the state,
///         the failure and the registration count, and <c>PluginDescriptor</c> is
///         deliberately "the result of reading, not of loading" — so a plugin that is disabled,
///         incompatible or broken is an ordinary row rather than an absence.
///     </para>
///     <para>
///         ⚠ <b>Enable, disable and reload, which is more than doc 11's "a view".</b> Doc 20's E3
///         exit criterion is that a plugin can be enabled, disabled <i>and</i> reloaded from a panel,
///         and the difference matters: the plugin-development loop is build, reload, look — and the
///         plugin-that-broke-my-editor loop is disable and restart. Both need somewhere to click.
///     </para>
///     <para>
///         ⚠ <b>The failure is under the grid rather than in a column.</b> A plugin that did not
///         start says why in a sentence — a missing dependency, a type that is not there, an
///         exception from its own <c>Activate</c> — and a sentence in a table cell is a sentence
///         nobody reads. It is also where the "did not unload cleanly" warning belongs, which is the
///         one failure the runtime reports by saying nothing at all.
///     </para>
/// </remarks>
sealed partial class PluginManagerView;

/// <summary>Everything the panel says about the plugin it has selected, as one reading.</summary>
/// <param name="HasSelection">Whether a plugin is chosen, which is what greys the two verbs.</param>
/// <param name="Toggle">What the switch button is called — "Disable", or "Enable" for a suppressed one.</param>
/// <param name="Sentence">The line under the grid.</param>
/// <param name="Failed">Whether that line is a failure, which is a class rather than a colour.</param>
/// <remarks>
///     ⚠ <b>A snapshot rather than four signals or a counter</b>, and <c>PrefabBanner</c>'s argument
///     applies unchanged. All four fields come from one reading of the host and the selection
///     together, so the value is what changes and assigning it is the notification — and a
///     <c>Signal&lt;int&gt;</c> bumped to force a re-read would be standing in for a value change
///     that is perfectly expressible, and would throw away the equality that keeps an unchanged
///     reading from repainting three elements.
/// </remarks>
readonly record struct PluginNote(bool HasSelection, string Toggle, string Sentence, bool Failed) {
    /// <summary>Before the panel has been pointed at a host.</summary>
    public static PluginNote Empty { get; } = new(false, string.Empty, string.Empty, false);
}
