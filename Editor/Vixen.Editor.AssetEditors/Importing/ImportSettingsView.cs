// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>The three sections every import-settings editor has, whatever it is importing.</summary>
/// <remarks>
///     <para>
///         <b>The panel is <c>ImportSettingsView.vxml</c>; this file exists only to make it public
///         and sealed.</b> The markup compiler emits a partial class with no accessibility
///         modifier, which is <c>internal</c> — deliberately, so that a component is not public API
///         by accident.
///     </para>
///     <para>
///         The base settings, where the asset appears in a build, and the per-target matrix. A
///         texture editor is this plus a channel viewer; a model editor is this plus a part list —
///         which is the whole reason it is a control rather than three copies of the same wiring.
///     </para>
///     <para>
///         ⚠ <b>Nothing here has an Apply button.</b> Every edit is already a command on the
///         document's stack and the document is already dirty; a second, weaker commit point would
///         mean two answers to "have my changes taken effect" and an editor where Ctrl+S and Apply
///         can disagree. What re-imports the asset is the import pass noticing that the settings
///         hash moved, which is a thing the shell runs and not a thing a panel does.
///     </para>
/// </remarks>
public sealed partial class ImportSettingsView;
